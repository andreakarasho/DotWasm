using DotWasm.Models;
using DotWasm.Models.Component;

namespace DotWasm.Runtime.Component;

/// <summary>A core function in a component's core function index space.</summary>
public abstract record CoreFuncEntry
{
    /// <summary>Invoke this core function with flat core values.</summary>
    public abstract void Invoke(ReadOnlySpan<WasmValue> args, Span<WasmValue> results);

    public abstract int ResultArity { get; }
}

/// <summary>A core function that is an export of an instantiated core module.</summary>
public sealed record ModuleCoreFunc(WasmInstance Instance, string ExportName, FuncType Type)
    : CoreFuncEntry
{
    public override int ResultArity => Type.Results.Length;

    public override void Invoke(ReadOnlySpan<WasmValue> args, Span<WasmValue> results) =>
        Instance.Invoke(ExportName, args, results);
}

/// <summary>A host-provided core function (a lowered import or a resource built-in).</summary>
public sealed record HostCoreFunc(HostFunction Function) : CoreFuncEntry
{
    public override int ResultArity => Function.Type.Results.Length;

    public override void Invoke(ReadOnlySpan<WasmValue> args, Span<WasmValue> results) =>
        Function.Delegate(args, results);
}

/// <summary>A core instance's exports, keyed by export name.</summary>
public sealed class CoreInstance(Dictionary<string, CoreExport> exports)
{
    public IReadOnlyDictionary<string, CoreExport> Exports => exports;

    public CoreExport GetExport(string name)
    {
        if (!exports.TryGetValue(name, out var e))
            WasmComponentException.Throw($"Core instance has no export '{name}'.");
        return e;
    }
}

/// <summary>A single export of a core instance.</summary>
public sealed record CoreExport(CoreSort Sort, object Value)
{
    public CoreFuncEntry AsFunc() => (CoreFuncEntry)Value;
    public MemoryInstance AsMemory() => (MemoryInstance)Value;
    public TableInstance AsTable() => (TableInstance)Value;
    public GlobalInstance AsGlobal() => (GlobalInstance)Value;
}

/// <summary>
/// Mutable index-space state built up while instantiating a component. Callables capture
/// this and resolve canonical-option targets lazily, since cross-references may be defined
/// after the function that uses them.
/// </summary>
public sealed class ComponentInstanceState
{
    public List<WasmModule> CoreModules { get; } = [];
    public List<CoreInstance> CoreInstances { get; } = [];
    public List<CoreFuncEntry> CoreFuncs { get; } = [];
    public List<MemoryInstance> CoreMems { get; } = [];
    public List<TableInstance> CoreTables { get; } = [];
    public List<GlobalInstance> CoreGlobals { get; } = [];
    public List<CoreTypeDef> CoreTypes { get; } = [];
    public List<IComponentCallable> CompFuncs { get; } = [];
    public List<ComponentSubInstance> CompInstances { get; } = [];
    public List<DotWasm.Models.Component.Component> SubComponents { get; } = [];
    public List<ComponentDefType> Types { get; } = [];
    public Dictionary<uint, ResourceTypeIdentity> Resources { get; } = [];

    public ComponentInstanceHandles Handles { get; } = new();

    ComponentTypeContext? typeContext;

    /// <summary>The finalized type context (built once instantiation is complete).</summary>
    public ComponentTypeContext TypeContext => typeContext ??= BuildCurrent();

    /// <summary>Build a fresh type context from the types defined so far (mid-pass use).</summary>
    public ComponentTypeContext BuildCurrent()
    {
        var resources = new ResourceTypeIdentity[Types.Count];
        foreach (var (idx, rt) in Resources)
            if (idx < resources.Length)
                resources[idx] = rt;
        return new ComponentTypeContext([.. Types], resources);
    }

    public MemoryInstance ResolveMemory(int index) => CoreMems[index];

    public ModuleCoreFunc ResolveModuleFunc(int index) =>
        CoreFuncs[index] as ModuleCoreFunc
        ?? throw new WasmComponentException($"Core func #{index} is not a module export.");

    public ReallocFn ResolveRealloc(int index)
    {
        var fn = ResolveModuleFunc(index);
        return (p, o, a, n) =>
        {
            var result = new WasmValue[1];
            fn.Invoke([WasmValue.FromI32(p), WasmValue.FromI32(o), WasmValue.FromI32(a), WasmValue.FromI32(n)], result);
            return result[0].I32;
        };
    }
}

/// <summary>A component-level function (lifted export or imported host function).</summary>
public interface IComponentCallable
{
    ComponentFuncType Type { get; }

    /// <summary>Invoke with host-level component values, returning host-level results.</summary>
    object?[] CallHost(object?[] args);
}

/// <summary>An imported component function backed by a host delegate.</summary>
public sealed class ImportedComponentFunc(ComponentFuncType type, Func<object?[], object?[]> impl)
    : IComponentCallable
{
    public ComponentFuncType Type => type;

    public object?[] CallHost(object?[] args) => impl(args);
}

/// <summary>
/// A component-level function callable from the host: lowers host arguments into core
/// values, calls the lifted core function, then lifts the core results back. Implements
/// the synchronous <c>canon lift</c> contract.
/// </summary>
public sealed class ComponentFunc : IComponentCallable
{
    readonly ComponentInstanceState state;
    readonly ComponentFuncType type;
    readonly ModuleCoreFunc callee;
    readonly int memoryIndex;
    readonly int reallocIndex;
    readonly int postReturnIndex;
    readonly StringEncoding stringEncoding;

    readonly List<ComponentValType> paramTypes;
    readonly List<ComponentValType> resultTypes;
    // Flattening is computed lazily on first call: the component type index space is only
    // complete once instantiation finishes (a lift may be defined before later types).
    List<CoreFlatType>? coreParamFlat;
    List<CoreFlatType>? coreResultFlat;

    public ComponentFunc(
        ComponentInstanceState state,
        ComponentFuncType type,
        ModuleCoreFunc callee,
        int memoryIndex,
        int reallocIndex,
        int postReturnIndex,
        StringEncoding stringEncoding
    )
    {
        this.state = state;
        this.type = type;
        this.callee = callee;
        this.memoryIndex = memoryIndex;
        this.reallocIndex = reallocIndex;
        this.postReturnIndex = postReturnIndex;
        this.stringEncoding = stringEncoding;

        paramTypes = [.. type.Params.Select(p => p.Type)];
        resultTypes = type.Result is null ? [] : [type.Result];
    }

    public ComponentFuncType Type => type;

    public object?[] CallHost(object?[] args) => Call(args);

    void EnsurePrepared()
    {
        if (coreParamFlat is not null)
            return;
        var types = state.TypeContext;
        var flatParams = types.FlattenTypes(paramTypes);
        var flatResults = types.FlattenTypes(resultTypes);
        coreParamFlat = flatParams.Count > 16 ? [CoreFlatType.I32] : flatParams;
        coreResultFlat = flatResults.Count > 1 ? [CoreFlatType.I32] : flatResults;
    }

    public object?[] Call(object?[] args)
    {
        if (args.Length != paramTypes.Count)
            throw new ArgumentException(
                $"Expected {paramTypes.Count} argument(s), got {args.Length}.");

        EnsurePrepared();
        var ctx = NewContext();

        var lowerBits = ctx.LowerFlatValues(args, paramTypes, 16);
        var coreArgs = CanonContext.BitsToCore(lowerBits, coreParamFlat!);

        var coreResults = new WasmValue[callee.ResultArity];
        callee.Invoke(coreArgs, coreResults);

        var resultBits = CanonContext.CoreToBits(coreResults, coreResultFlat!);
        var lifted = ctx.LiftFlatValues(new CanonContext.CoreValueIter(resultBits), resultTypes, 1);

        // settle borrows lent during the call
        foreach (var lender in ctx.BorrowLenders)
            lender.NumLends--;

        if (postReturnIndex >= 0)
        {
            var postReturn = state.ResolveModuleFunc(postReturnIndex);
            postReturn.Invoke(coreResults, Span<WasmValue>.Empty);
        }

        return lifted;
    }

    CanonContext NewContext() => new()
    {
        Types = state.TypeContext,
        Memory = memoryIndex >= 0 ? state.ResolveMemory(memoryIndex) : null,
        Realloc = reallocIndex >= 0 ? state.ResolveRealloc(reallocIndex) : null,
        StringEncoding = stringEncoding,
        Handles = state.Handles,
        InstanceIdentity = state,
    };
}

/// <summary>A component instance's named function exports (e.g. an exported interface).</summary>
public sealed class ComponentSubInstance(
    Dictionary<string, ComponentFunc> funcs,
    Dictionary<string, ResourceTypeIdentity>? resources = null)
{
    public IReadOnlyDictionary<string, ComponentFunc> Funcs => funcs;
    public IReadOnlyDictionary<string, ResourceTypeIdentity> Resources =>
        resources ?? (IReadOnlyDictionary<string, ResourceTypeIdentity>)new Dictionary<string, ResourceTypeIdentity>();

    public ComponentFunc GetFunc(string name)
    {
        if (!funcs.TryGetValue(name, out var f))
            WasmComponentException.Throw($"Instance has no function export '{name}'.");
        return f;
    }

    public ResourceTypeIdentity? GetResourceType(string name) =>
        resources is not null && resources.TryGetValue(name, out var r) ? r : null;

    public object?[] Invoke(string name, params object?[] args) => GetFunc(name).Call(args);
}

/// <summary>The result of instantiating a component: its exports, callable from the host.</summary>
public sealed class ComponentInstance
{
    readonly Dictionary<string, object> exports;

    internal ComponentInstance(ComponentInstanceState state, Dictionary<string, object> exports)
    {
        State = state;
        this.exports = exports;
    }

    public ComponentInstanceState State { get; }

    public IReadOnlyDictionary<string, object> Exports => exports;

    /// <summary>Call a top-level exported function by name.</summary>
    public object?[] Invoke(string exportName, params object?[] args)
    {
        if (!exports.TryGetValue(exportName, out var e) || e is not ComponentFunc f)
            throw new ArgumentException($"No exported function '{exportName}'.");
        return f.Call(args);
    }

    /// <summary>Get an exported function by name, or null if absent / not a function.</summary>
    public ComponentFunc? GetFunc(string exportName) =>
        exports.TryGetValue(exportName, out var e) ? e as ComponentFunc : null;

    /// <summary>Get an exported instance (interface) by name, or null if absent.</summary>
    public ComponentSubInstance? GetInstance(string exportName) =>
        exports.TryGetValue(exportName, out var e) ? e as ComponentSubInstance : null;

    /// <summary>Get an exported resource type by name, or null if absent.</summary>
    public ResourceTypeIdentity? GetResourceType(string exportName) =>
        exports.TryGetValue(exportName, out var e) ? e as ResourceTypeIdentity : null;
}
