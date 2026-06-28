using DotWasm.Models;
using DotWasm.Models.Component;
using ModelComponent = DotWasm.Models.Component.Component;

namespace DotWasm.Runtime.Component;

/// <summary>
/// Instantiates a decoded <see cref="Component"/> against a <see cref="WasmStore"/>:
/// instantiates embedded core modules, wires core instances and aliases, binds canonical
/// lift/lower functions and resource built-ins, and exposes the component's exports.
/// </summary>
public sealed class ComponentLinker(WasmStore store)
{
    readonly Dictionary<string, Func<object?[], object?[]>> hostImports = [];
    readonly Dictionary<string, Func<object?[], object?[]>> hostImportFuncs = [];

    /// <summary>Provide a host implementation for a top-level function import, keyed by import name.</summary>
    public void DefineImport(string name, Func<object?[], object?[]> impl) => hostImports[StripVersion(name)] = impl;

    /// <summary>Provide a host implementation for a function of an imported interface (instance import).</summary>
    public void DefineImportFunc(string interfaceName, string funcName, Func<object?[], object?[]> impl) =>
        hostImportFuncs[StripVersion($"{interfaceName}#{funcName}")] = impl;

    // Drop the interface's `@semver` so imports resolve version-agnostically: a host impl
    // registered for `wasi:clocks/monotonic-clock` (any version) satisfies a guest importing
    // `wasi:clocks/monotonic-clock@0.2.10`. Strips the first `@...` up to `#` (or end).
    // ponytail: no per-version override — a single impl covers all 0.2.x; add a versioned
    // map only if two ABIs of one interface must coexist.
    static string StripVersion(string key)
    {
        var at = key.IndexOf('@');
        if (at < 0) return key;
        var hash = key.IndexOf('#', at);
        return hash < 0 ? key[..at] : key[..at] + key[hash..];
    }

    public ComponentInstance Instantiate(ModelComponent component)
    {
        var state = new ComponentInstanceState();
        var exports = new Dictionary<string, object>();

        var trace = Environment.GetEnvironmentVariable("DOTWASM_TRACE") == "1";
        var di = 0;
        foreach (var def in component.Definitions)
        {
            if (trace) Console.Error.WriteLine($"[{di++}] {def.GetType().Name}");
            Process(def, state, exports);
        }

        return new ComponentInstance(state, exports);
    }

    void Process(ComponentDefinition def, ComponentInstanceState s, Dictionary<string, object> exports)
    {
        switch (def)
        {
            case DefCoreModule m:
                s.CoreModules.Add(m.Module);
                break;
            case DefCoreInstance ci:
                s.CoreInstances.Add(InstantiateCore(ci.Expr, s));
                break;
            case DefCoreType ct:
                s.CoreTypes.Add(ct.Type);
                break;
            case DefAlias a:
                ProcessAlias(a.Alias, s);
                break;
            case DefType t:
                ProcessType(t.Type, s);
                break;
            case DefCanon c:
                ProcessCanon(c.Canon, s);
                break;
            case DefImport i:
                ProcessImport(i.Import, s);
                break;
            case DefExport e:
                ProcessExport(e.Export, s, exports);
                break;
            case DefComponentInstance cinst:
                s.CompInstances.Add(BuildCompInstance(cinst.Expr, s));
                break;
            case DefStart:
                WasmComponentException.Throw("Component start functions are not supported.");
                break;
            case DefComponent sub:
                s.SubComponents.Add(sub.Component);
                break;
            case DefValue:
                WasmComponentException.Throw("Value imports/exports are not supported.");
                break;
        }
    }

    // ---- Core instances ----

    CoreInstance InstantiateCore(CoreInstanceExpr expr, ComponentInstanceState s)
    {
        switch (expr)
        {
            case CoreInstantiate inst:
            {
                var module = s.CoreModules[(int)inst.ModuleIndex];
                var linker = new WasmLinker(store);
                foreach (var arg in inst.Args)
                {
                    var argInstance = s.CoreInstances[(int)arg.InstanceIndex];
                    foreach (var (name, export) in argInstance.Exports)
                        RegisterCoreExport(linker, arg.Name, name, export);
                }
                var wasmInstance = linker.Instantiate(module);
                return CoreInstanceFromWasm(wasmInstance);
            }
            case CoreInlineExports inline:
            {
                var map = new Dictionary<string, CoreExport>();
                foreach (var e in inline.Exports)
                    map[e.Name] = new CoreExport(e.Sort, ResolveCoreSortValue(e.Sort, (int)e.Index, s));
                return new CoreInstance(map);
            }
            default:
                WasmComponentException.Throw("Unknown core instance expression.");
                return null!;
        }
    }

    static object ResolveCoreSortValueStatic(CoreSort sort, int index, ComponentInstanceState s) =>
        sort switch
        {
            CoreSort.Func => s.CoreFuncs[index],
            CoreSort.Memory => s.CoreMems[index],
            CoreSort.Table => s.CoreTables[index],
            CoreSort.Global => s.CoreGlobals[index],
            _ => throw new WasmComponentException($"Cannot resolve core sort {sort} for inline export."),
        };

    object ResolveCoreSortValue(CoreSort sort, int index, ComponentInstanceState s) =>
        ResolveCoreSortValueStatic(sort, index, s);

    static CoreInstance CoreInstanceFromWasm(WasmInstance wasm)
    {
        var map = new Dictionary<string, CoreExport>();
        foreach (var export in wasm.Module.Exports)
        {
            switch (export.Kind)
            {
                case ImportExportKind.Function:
                    if (wasm.TryGetExportedFunction(export.Name, out var fn))
                        map[export.Name] = new CoreExport(CoreSort.Func,
                            new ModuleCoreFunc(wasm, export.Name, FuncTypeOf(fn)));
                    break;
                case ImportExportKind.Memory:
                    if (wasm.TryGetExportedMemory(export.Name, out var mem) && mem is not null)
                        map[export.Name] = new CoreExport(CoreSort.Memory, mem);
                    break;
                case ImportExportKind.Table:
                    if (wasm.TryGetExportedTable(export.Name, out var tbl) && tbl is not null)
                        map[export.Name] = new CoreExport(CoreSort.Table, tbl);
                    break;
                case ImportExportKind.Global:
                    if (wasm.TryGetExportedGlobal(export.Name, out var g) && g is not null)
                        map[export.Name] = new CoreExport(CoreSort.Global, g);
                    break;
            }
        }
        return new CoreInstance(map);
    }

    static FuncType FuncTypeOf(FunctionInstance fn) => fn switch
    {
        RuntimeFunction rf => rf.FlatType,
        HostFunction hf => hf.Type,
        _ => throw new WasmComponentException("Unknown function instance kind."),
    };

    static void RegisterCoreExport(WasmLinker linker, string moduleName, string name, CoreExport export)
    {
        switch (export.Sort)
        {
            case CoreSort.Func:
            {
                var entry = export.AsFunc();
                var host = entry switch
                {
                    HostCoreFunc h => h.Function,
                    ModuleCoreFunc m => new HostFunction
                    {
                        Type = m.Type,
                        Delegate = (a, r) => m.Invoke(a, r),
                    },
                    _ => throw new WasmComponentException("Unknown core func entry."),
                };
                linker.RegisterFunction(moduleName, name, host);
                break;
            }
            case CoreSort.Memory:
                linker.RegisterMemory(moduleName, name, export.AsMemory());
                break;
            case CoreSort.Table:
                linker.RegisterTable(moduleName, name, export.AsTable());
                break;
            case CoreSort.Global:
                linker.RegisterGlobal(moduleName, name, export.AsGlobal());
                break;
            default:
                WasmComponentException.Throw($"Cannot register core export of sort {export.Sort}.");
                break;
        }
    }

    // ---- Aliases ----

    void ProcessAlias(Alias alias, ComponentInstanceState s)
    {
        switch (alias)
        {
            case AliasCoreExport ace:
            {
                var coreInst = s.CoreInstances[(int)ace.InstanceIndex];
                var export = coreInst.GetExport(ace.Name);
                switch (ace.Sort)
                {
                    case ComponentSortKind.CoreFunc: s.CoreFuncs.Add(export.AsFunc()); break;
                    case ComponentSortKind.CoreMemory: s.CoreMems.Add(export.AsMemory()); break;
                    case ComponentSortKind.CoreTable: s.CoreTables.Add(export.AsTable()); break;
                    case ComponentSortKind.CoreGlobal: s.CoreGlobals.Add(export.AsGlobal()); break;
                    default:
                        WasmComponentException.Throw($"Unsupported core export alias sort {ace.Sort}.");
                        break;
                }
                break;
            }
            case AliasExport ae:
            {
                var compInst = s.CompInstances[(int)ae.InstanceIndex];
                switch (ae.Sort)
                {
                    case ComponentSortKind.Func:
                        if (!compInst.Funcs.TryGetValue(ae.Name, out var f))
                            WasmComponentException.Throw($"Instance has no func export '{ae.Name}'.");
                        s.CompFuncs.Add(f!);
                        break;
                    case ComponentSortKind.Type:
                        if (compInst.Resources.TryGetValue(ae.Name, out var rt))
                        {
                            s.Resources[(uint)s.Types.Count] = rt;
                            s.Types.Add(new TypeResourceDef(new ResourceType(WasmTypes.I32, null)));
                        }
                        else if (compInst.Types.TryGetValue(ae.Name, out var vt))
                        {
                            s.Types.Add(new TypeValDef(vt));
                        }
                        else
                        {
                            WasmComponentException.Throw($"Instance has no type export '{ae.Name}'.");
                        }
                        break;
                    default:
                        WasmComponentException.Throw($"Unsupported component export alias sort {ae.Sort}.");
                        break;
                }
                break;
            }
            case AliasOuter:
                WasmComponentException.Throw("Outer aliases are not yet supported.");
                break;
        }
    }

    // ---- Types ----

    void ProcessType(ComponentDefType type, ComponentInstanceState s)
    {
        var index = (uint)s.Types.Count;
        s.Types.Add(type);
        if (type is TypeResourceDef rd)
        {
            s.Resources[index] = new ResourceTypeIdentity
            {
                DestructorCoreFuncIndex = rd.Type.DestructorFuncIndex,
                ImplementingInstance = s,
            };
        }
    }

    // ---- Canon ----

    void ProcessCanon(Canon canon, ComponentInstanceState s)
    {
        switch (canon)
        {
            case CanonLift lift:
            {
                var callee = s.CoreFuncs[(int)lift.CoreFuncIndex] as ModuleCoreFunc
                    ?? throw new WasmComponentException("canon lift target is not a module core func.");
                var funcType = (s.Types[(int)lift.TypeIndex] as TypeFuncDef)?.Type
                    ?? throw new WasmComponentException("canon lift type is not a function type.");
                var (memory, realloc, postReturn, encoding) = ReadOptions(lift.Options);
                s.CompFuncs.Add(new ComponentFunc(s, funcType, callee, memory, realloc, postReturn, encoding));
                break;
            }
            case CanonLower lower:
            {
                var imported = s.CompFuncs[(int)lower.FuncIndex];
                var (memory, realloc, _, encoding) = ReadOptions(lower.Options);
                s.CoreFuncs.Add(new HostCoreFunc(BuildLoweredFunc(imported, s, memory, realloc, encoding)));
                break;
            }
            case CanonResourceNew rn:
                s.CoreFuncs.Add(new HostCoreFunc(BuildResourceNew(rn.TypeIndex, s)));
                break;
            case CanonResourceDrop rd:
                s.CoreFuncs.Add(new HostCoreFunc(BuildResourceDrop(rd.TypeIndex, s)));
                break;
            case CanonResourceRep rr:
                s.CoreFuncs.Add(new HostCoreFunc(BuildResourceRep(rr.TypeIndex, s)));
                break;
        }
    }

    static (int memory, int realloc, int postReturn, StringEncoding encoding) ReadOptions(
        IEnumerable<CanonOpt> opts)
    {
        var memory = -1;
        var realloc = -1;
        var postReturn = -1;
        var encoding = StringEncoding.Utf8;
        foreach (var o in opts)
        {
            switch (o)
            {
                case CanonMemory m: memory = (int)m.CoreMemIndex; break;
                case CanonRealloc r: realloc = (int)r.CoreFuncIndex; break;
                case CanonPostReturn p: postReturn = (int)p.CoreFuncIndex; break;
                case CanonStringEncoding e: encoding = e.Encoding; break;
            }
        }
        return (memory, realloc, postReturn, encoding);
    }

    // ---- Imports / exports ----

    void ProcessImport(ComponentImport import, ComponentInstanceState s)
    {
        switch (import.Desc)
        {
            case FuncDesc fd:
            {
                var funcType = (s.Types[(int)fd.TypeIndex] as TypeFuncDef)?.Type
                    ?? throw new WasmComponentException("Imported func type is not a function type.");
                if (!hostImports.TryGetValue(StripVersion(import.Name), out var impl))
                    WasmComponentException.Throw($"No host implementation provided for import '{import.Name}'.");
                s.CompFuncs.Add(new ImportedComponentFunc(funcType, impl!));
                break;
            }
            case TypeBoundDesc { IsEq: true } tb:
                // `(type (eq N))` import (e.g. a world's `use iface.{res}`): this type aliases
                // the existing type #N. If #N is a resource, reuse its identity so handles
                // minted/lifted through either index unify (export-param own<r> vs method self
                // borrow<r>); otherwise a fresh identity would trap on borrow/own lift.
                if (s.Resources.TryGetValue(tb.TypeIndex, out var aliased))
                    s.Resources[(uint)s.Types.Count] = aliased;
                s.Types.Add(s.Types[(int)tb.TypeIndex]);
                break;
            case TypeBoundDesc:
                // imported (abstract) `(sub resource)` type: register a fresh identity slot
                ProcessType(new TypeResourceDef(new ResourceType(WasmTypes.I32, null)), s);
                break;
            case InstanceDesc id:
                s.CompInstances.Add(BuildImportedInstance(import.Name, id.TypeIndex, s));
                break;
            default:
                WasmComponentException.Throw($"Unsupported import descriptor {import.Desc.GetType().Name}.");
                break;
        }
    }

    /// <summary>
    /// Build an imported component instance from its instance type. Type exports are inlined
    /// into self-contained value types; function exports are backed by host delegates
    /// (registered via <see cref="DefineImportFunc"/>).
    /// </summary>
    ComponentInstanceExports BuildImportedInstance(string interfaceName, uint typeIndex, ComponentInstanceState s)
    {
        var instanceType = (s.Types[(int)typeIndex] as TypeInstanceDef)?.Type
            ?? throw new WasmComponentException($"Import '{interfaceName}' is not an instance type.");

        // Reconstruct the instance type's local type index space (type decls and type-exports
        // each occupy an index; func-exports do not).
        var local = new List<ComponentDefType>();
        var localResources = new Dictionary<int, ResourceTypeIdentity>();
        var typeExportIndex = new Dictionary<string, int>();
        var funcExportIndex = new Dictionary<string, int>();
        var resourceExportName = new Dictionary<string, int>();

        foreach (var decl in instanceType.Decls)
        {
            switch (decl)
            {
                case InstanceTypeDecl td:
                    local.Add(td.Type);
                    break;
                case InstanceExportDecl ed:
                    switch (ed.Desc)
                    {
                        case TypeBoundDesc { IsEq: true } tb:
                        {
                            var slot = local.Count;
                            if (TryResolveLocalResource(local, localResources, (int)tb.TypeIndex, out var rid))
                            {
                                localResources[slot] = rid!;
                                local.Add(new TypeResourceDef(new ResourceType(WasmTypes.I32, null)));
                                resourceExportName[ed.Name] = slot;
                            }
                            else
                            {
                                local.Add(new TypeValDef(new DefinedTypeRef(tb.TypeIndex)));
                                typeExportIndex[ed.Name] = slot;
                            }
                            break;
                        }
                        case TypeBoundDesc { IsEq: false }: // sub resource
                            var rt = new ResourceTypeIdentity();
                            localResources[local.Count] = rt;
                            resourceExportName[ed.Name] = local.Count;
                            local.Add(new TypeResourceDef(new ResourceType(WasmTypes.I32, null)));
                            break;
                        case FuncDesc fd:
                            funcExportIndex[ed.Name] = (int)fd.TypeIndex;
                            break;
                    }
                    break;
                case InstanceAliasDecl ad when ad.Alias.Sort == ComponentSortKind.Type:
                    // A type-sort alias occupies a slot in the instance type's type index space.
                    // An outer alias to a resource already shared into the component's type space
                    // (e.g. output-stream defined in io/streams and used by cli/stdout) must reuse
                    // that identity; otherwise register a fresh placeholder.
                    localResources[local.Count] =
                        ad.Alias is AliasOuter outer && s.Resources.TryGetValue(outer.Index, out var shared)
                            ? shared
                            : new ResourceTypeIdentity();
                    local.Add(new TypeResourceDef(new ResourceType(WasmTypes.I32, null)));
                    break;
                // core-type decls occupy the core type space, not the value type space; skip.
            }
        }

        var localCtx = new ComponentTypeContext([.. local], BuildResourceArray(local.Count, localResources));
        var result = new ComponentInstanceExports();

        // Map each instance-local resource index to a synthetic global resource index, so that
        // own/borrow inside inlined types resolve to a stable identity in the importing component.
        var localToSynthetic = new Dictionary<uint, uint>();
        foreach (var (localIdx, rt) in localResources)
            localToSynthetic[(uint)localIdx] = s.RegisterSyntheticResource(rt);
        uint ResMap(uint i) => localToSynthetic.TryGetValue(i, out var g)
            ? g
            : throw new WasmComponentException($"Import '{interfaceName}' references unknown resource type #{i}.");

        foreach (var (name, idx) in typeExportIndex)
            result.Types[name] = Inline(new DefinedTypeRef((uint)idx), localCtx, ResMap);

        foreach (var (name, idx) in resourceExportName)
            result.Resources[name] = localResources[idx];

        foreach (var (name, funcTypeIdx) in funcExportIndex)
        {
            var ft = (local[funcTypeIdx] as TypeFuncDef)?.Type
                ?? throw new WasmComponentException($"Import '{interfaceName}' func '{name}' has no func type.");
            var inlined = InlineFunc(ft, localCtx, ResMap);
            var key = $"{interfaceName}#{name}";
            var impl = hostImportFuncs.TryGetValue(StripVersion(key), out var h)
                ? h
                : MissingImport(key);
            result.Funcs[name] = new ImportedComponentFunc(inlined, impl);
        }

        return result;
    }

    // Follow type-index refs within an instance type's local space; if they land on a resource,
    // return its identity (so a re-exported / aliased resource keeps a single identity).
    static bool TryResolveLocalResource(
        List<ComponentDefType> local,
        Dictionary<int, ResourceTypeIdentity> localResources,
        int index,
        out ResourceTypeIdentity? identity)
    {
        var guard = 0;
        while (index >= 0 && index < local.Count && guard++ < 10_000)
        {
            if (localResources.TryGetValue(index, out identity))
                return true;
            if (local[index] is TypeValDef { Type: DefinedTypeRef r })
            {
                index = (int)r.TypeIndex;
                continue;
            }
            break;
        }
        identity = null;
        return false;
    }

    static ResourceTypeIdentity[] BuildResourceArray(int count, Dictionary<int, ResourceTypeIdentity> map)
    {
        var arr = new ResourceTypeIdentity[count];
        foreach (var (i, rt) in map)
            arr[i] = rt;
        return arr;
    }

    static Func<object?[], object?[]> MissingImport(string key) =>
        _ => throw new WasmComponentException($"No host implementation for imported function '{key}'. Use DefineImportFunc.");

    // ---- Type inlining (resolve instance-local type refs to self-contained types) ----
    // resMap rewrites an instance-local resource type index to a synthetic global index.

    static ComponentValType Inline(ComponentValType t, ComponentTypeContext ctx, Func<uint, uint> resMap)
    {
        var s = ctx.Structural(t);
        return s switch
        {
            PrimitiveType => s,
            FlagsType => s,
            RecordType rec => new RecordType(
                [.. rec.Fields.Select(f => new RecordField(f.Name, Inline(f.Type, ctx, resMap)))]),
            VariantType v => new VariantType(
                [.. v.Cases.Select(c => new VariantCase(c.Name, c.Type is null ? null : Inline(c.Type, ctx, resMap), c.Refines))]),
            ListType l => new ListType(Inline(l.Element, ctx, resMap), l.FixedLength),
            OwnType o => new OwnType(resMap(o.TypeIndex)),
            BorrowType b => new BorrowType(resMap(b.TypeIndex)),
            _ => s,
        };
    }

    static ComponentFuncType InlineFunc(ComponentFuncType ft, ComponentTypeContext ctx, Func<uint, uint> resMap) => new(
        [.. ft.Params.Select(p => new NamedValType(p.Name, Inline(p.Type, ctx, resMap)))],
        ft.Result is null ? null : Inline(ft.Result, ctx, resMap));

    void ProcessExport(ComponentExport export, ComponentInstanceState s, Dictionary<string, object> exports)
    {
        switch (export.Idx.Sort)
        {
            case ComponentSortKind.Func:
            {
                var f = s.CompFuncs[(int)export.Idx.Index];
                exports[export.Name] = f;
                s.CompFuncs.Add(f); // re-export occupies a new func index
                break;
            }
            case ComponentSortKind.Instance:
            {
                var inst = s.CompInstances[(int)export.Idx.Index];
                exports[export.Name] = ToHostInstance(inst);
                s.CompInstances.Add(inst);
                break;
            }
            case ComponentSortKind.Type:
            {
                if (s.Resources.TryGetValue(export.Idx.Index, out var rt))
                    exports[export.Name] = rt;
                break;
            }
            default:
                // other export sorts are not host-callable; ignore
                break;
        }
    }

    static ComponentSubInstance ToHostInstance(ComponentInstanceExports inst)
    {
        var funcs = new Dictionary<string, ComponentFunc>();
        foreach (var (name, f) in inst.Funcs)
            if (f is ComponentFunc cf)
                funcs[name] = cf;
        return new ComponentSubInstance(funcs, new Dictionary<string, ResourceTypeIdentity>(inst.Resources));
    }

    ComponentInstanceExports BuildCompInstance(ComponentInstanceExpr expr, ComponentInstanceState s)
    {
        switch (expr)
        {
            case ComponentInlineExports inline:
            {
                var result = new ComponentInstanceExports();
                foreach (var e in inline.Exports)
                {
                    switch (e.Idx.Sort)
                    {
                        case ComponentSortKind.Func:
                            result.Funcs[e.Name] = s.CompFuncs[(int)e.Idx.Index];
                            break;
                        case ComponentSortKind.Type when s.Resources.TryGetValue(e.Idx.Index, out var rt):
                            result.Resources[e.Name] = rt;
                            break;
                        case ComponentSortKind.Type:
                            result.Types[e.Name] = new DefinedTypeRef(e.Idx.Index);
                            break;
                    }
                }
                return result;
            }
            case ComponentInstantiate inst:
                return InstantiateSubComponent(s.SubComponents[(int)inst.ComponentIndex], inst.Args, s);
            default:
                WasmComponentException.Throw("Unknown component instance expression.");
                return null!;
        }
    }

    /// <summary>
    /// Instantiate a nested sub-component used as an export adapter (the wit-bindgen export
    /// shim): it imports the parent's lifted functions/types by name and re-exports them
    /// grouped as an interface instance. The parent's <see cref="ComponentFunc"/> objects are
    /// reused directly, so no re-lifting occurs.
    /// </summary>
    ComponentInstanceExports InstantiateSubComponent(
        DotWasm.Models.Component.Component sub,
        IReadOnlyList<ComponentInstantiateArg> args,
        ComponentInstanceState parent)
    {
        // Resolve each instantiation argument to the parent-level object it names.
        var argByName = new Dictionary<string, object>();
        foreach (var arg in args)
        {
            object resolved = arg.Arg.Sort switch
            {
                ComponentSortKind.Func => parent.CompFuncs[(int)arg.Arg.Index],
                ComponentSortKind.Type => (object?)(parent.Resources.TryGetValue(arg.Arg.Index, out var rt) ? rt : null)
                    ?? parent.Types[(int)arg.Arg.Index],
                ComponentSortKind.Instance => parent.CompInstances[(int)arg.Arg.Index],
                _ => throw new WasmComponentException($"Unsupported sub-component arg sort {arg.Arg.Sort}."),
            };
            argByName[arg.Name] = resolved;
        }

        // Child index spaces (funcs + types), filled in declaration order so that export
        // references line up with imports and local type defs.
        var childFuncs = new List<IComponentCallable>();
        var childTypes = new List<ComponentDefType>();
        var childResources = new Dictionary<uint, ResourceTypeIdentity>();
        var result = new ComponentInstanceExports();

        foreach (var def in sub.Definitions)
        {
            switch (def)
            {
                case DefImport defImport:
                {
                    var import = defImport.Import;
                    switch (import.Desc)
                    {
                        case FuncDesc:
                            childFuncs.Add((IComponentCallable)argByName[import.Name]);
                            break;
                        case TypeBoundDesc:
                            if (argByName.TryGetValue(import.Name, out var bound) && bound is ResourceTypeIdentity rt)
                                childResources[(uint)childTypes.Count] = rt;
                            childTypes.Add(new TypeResourceDef(new ResourceType(WasmTypes.I32, null)));
                            break;
                        default:
                            WasmComponentException.Throw($"Unsupported sub-component import '{import.Name}'.");
                            break;
                    }
                    break;
                }
                case DefType t:
                    childTypes.Add(t.Type);
                    break;
                case DefExport defExport:
                {
                    var export = defExport.Export;
                    switch (export.Idx.Sort)
                    {
                        case ComponentSortKind.Func:
                            result.Funcs[export.Name] = childFuncs[(int)export.Idx.Index];
                            break;
                        case ComponentSortKind.Type when childResources.TryGetValue(export.Idx.Index, out var rt):
                            result.Resources[export.Name] = rt;
                            break;
                    }
                    break;
                }
                case DefAlias:
                    // Shim adapters do not require aliases; ignore.
                    break;
                default:
                    WasmComponentException.Throw(
                        $"Unsupported definition in sub-component: {def.GetType().Name}.");
                    break;
            }
        }

        return result;
    }

    // ---- Lowered import (component -> host) ----

    HostFunction BuildLoweredFunc(
        IComponentCallable imported,
        ComponentInstanceState s,
        int memoryIndex,
        int reallocIndex,
        StringEncoding encoding)
    {
        var type = imported.Type;
        var paramTypes = type.Params.Select(p => p.Type).ToList();
        var resultTypes = type.Result is null ? new List<ComponentValType>() : [type.Result];
        var types = s.BuildCurrent();
        var paramFlat = types.FlattenTypes(paramTypes);
        var resultFlat = types.FlattenTypes(resultTypes);
        var paramsIndirect = paramFlat.Count > 16;
        var resultsIndirect = resultFlat.Count > 1;

        var coreParams = new List<CoreFlatType>(paramsIndirect ? [CoreFlatType.I32] : paramFlat);
        if (resultsIndirect)
            coreParams.Add(CoreFlatType.I32); // out pointer
        var coreResults = resultsIndirect ? new List<CoreFlatType>() : resultFlat;

        var coreType = CoreFuncType(coreParams, coreResults);
        var paramSlotCount = paramsIndirect ? 1 : paramFlat.Count;

        return new HostFunction
        {
            Type = coreType,
            Delegate = (args, results) =>
            {
                var ctx = NewLowerContext(s, memoryIndex, reallocIndex, encoding);
                var argBits = CanonContext.CoreToBits(args, coreParams);
                var paramIter = new CanonContext.CoreValueIter(argBits.GetRange(0, paramSlotCount));
                var lifted = ctx.LiftFlatValues(paramIter, paramTypes, 16);

                var hostResults = imported.CallHost(lifted);

                if (resultsIndirect)
                {
                    var outPtr = (int)argBits[paramSlotCount];
                    ctx.LowerFlatValuesIntoOutPtr(hostResults, resultTypes, outPtr);
                }
                else
                {
                    var resBits = ctx.LowerFlatValues(hostResults, resultTypes, 1);
                    var core = CanonContext.BitsToCore(resBits, resultFlat);
                    for (var i = 0; i < core.Length; i++)
                        results[i] = core[i];
                }

                foreach (var lender in ctx.BorrowLenders)
                    lender.NumLends--;
            },
        };
    }

    CanonContext NewLowerContext(ComponentInstanceState s, int memoryIndex, int reallocIndex, StringEncoding encoding) => new()
    {
        Types = s.TypeContext,
        Memory = memoryIndex >= 0 ? s.ResolveMemory(memoryIndex) : null,
        Realloc = reallocIndex >= 0 ? s.ResolveRealloc(reallocIndex) : null,
        StringEncoding = encoding,
        Handles = s.Handles,
        InstanceIdentity = s,
    };

    // ---- Resource built-ins ----

    HostFunction BuildResourceNew(uint typeIndex, ComponentInstanceState s) => new()
    {
        Type = CoreFuncType([CoreFlatType.I32], [CoreFlatType.I32]),
        Delegate = (args, results) =>
        {
            var rt = ResolveResource(typeIndex, s);
            var idx = s.Handles.Add(new ResourceHandle(rt, args[0].I32, own: true));
            results[0] = WasmValue.FromI32(idx);
        },
    };

    HostFunction BuildResourceRep(uint typeIndex, ComponentInstanceState s) => new()
    {
        Type = CoreFuncType([CoreFlatType.I32], [CoreFlatType.I32]),
        Delegate = (args, results) =>
        {
            var rt = ResolveResource(typeIndex, s);
            var h = s.Handles.Get(args[0].I32);
            if (!ReferenceEquals(h.Type, rt))
                WasmTrapException.Throw("resource.rep type mismatch.");
            results[0] = WasmValue.FromI32(h.Rep);
        },
    };

    HostFunction BuildResourceDrop(uint typeIndex, ComponentInstanceState s) => new()
    {
        Type = CoreFuncType([CoreFlatType.I32], []),
        Delegate = (args, _) =>
        {
            var rt = ResolveResource(typeIndex, s);
            var h = s.Handles.Remove(args[0].I32);
            if (!ReferenceEquals(h.Type, rt))
                WasmTrapException.Throw("resource.drop type mismatch.");
            if (h.NumLends != 0)
                WasmTrapException.Throw("Cannot drop a resource that is currently lent.");
            if (h.Own && rt.DestructorCoreFuncIndex is { } dtorIndex)
            {
                var dtor = s.ResolveModuleFunc((int)dtorIndex);
                dtor.Invoke([WasmValue.FromI32(h.Rep)], Span<WasmValue>.Empty);
            }
        },
    };

    static ResourceTypeIdentity ResolveResource(uint typeIndex, ComponentInstanceState s)
    {
        if (!s.Resources.TryGetValue(typeIndex, out var rt))
            WasmComponentException.Throw($"Type #{typeIndex} is not a resource.");
        return rt!;
    }

    // ---- helpers ----

    static FuncType CoreFuncType(IEnumerable<CoreFlatType> ps, IEnumerable<CoreFlatType> rs) => new()
    {
        IsNullable = false,
        Parameters = [.. ps.Select(ToWasmType)],
        Results = [.. rs.Select(ToWasmType)],
    };

    static WasmValueType ToWasmType(CoreFlatType t) => t switch
    {
        CoreFlatType.I32 => WasmTypes.I32,
        CoreFlatType.I64 => WasmTypes.I64,
        CoreFlatType.F32 => WasmTypes.F32,
        CoreFlatType.F64 => WasmTypes.F64,
        _ => WasmTypes.I32,
    };
}
