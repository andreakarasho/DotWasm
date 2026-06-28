using System.Collections.Immutable;

namespace DotWasm.Models.Component;

/// <summary>A decoded WebAssembly component: an ordered list of definitions that build up
/// the component's index spaces, processed in order at instantiation time.</summary>
public sealed record Component(uint Version, ImmutableArray<ComponentDefinition> Definitions);

// ---- Sorts ----

public enum ComponentSortKind
{
    CoreFunc,
    CoreTable,
    CoreMemory,
    CoreGlobal,
    CoreType,
    CoreModule,
    CoreInstance,
    Func,
    Value,
    Type,
    Component,
    Instance,
}

public sealed record SortIdx(ComponentSortKind Sort, uint Index);

// ---- Definitions (flattened in declaration order) ----

public abstract record ComponentDefinition;

public sealed record DefCoreModule(WasmModule Module) : ComponentDefinition;

public sealed record DefCoreInstance(CoreInstanceExpr Expr) : ComponentDefinition;

public sealed record DefCoreType(CoreTypeDef Type) : ComponentDefinition;

public sealed record DefComponent(Component Component) : ComponentDefinition;

public sealed record DefComponentInstance(ComponentInstanceExpr Expr) : ComponentDefinition;

public sealed record DefAlias(Alias Alias) : ComponentDefinition;

public sealed record DefType(ComponentDefType Type) : ComponentDefinition;

public sealed record DefCanon(Canon Canon) : ComponentDefinition;

public sealed record DefStart(ComponentStart Start) : ComponentDefinition;

public sealed record DefImport(ComponentImport Import) : ComponentDefinition;

public sealed record DefExport(ComponentExport Export) : ComponentDefinition;

/// <summary>A value definition (value section). Stored opaquely; execution is out of MVP scope.</summary>
public sealed record DefValue(ComponentValType Type) : ComponentDefinition;

// ---- Core instance expressions ----

public abstract record CoreInstanceExpr;

public sealed record CoreInstantiate(uint ModuleIndex, ImmutableArray<CoreInstantiateArg> Args)
    : CoreInstanceExpr;

public sealed record CoreInlineExports(ImmutableArray<CoreInlineExport> Exports)
    : CoreInstanceExpr;

/// <summary>An instantiation argument: a named core instance supplying the module's imports.</summary>
public sealed record CoreInstantiateArg(string Name, uint InstanceIndex);

public sealed record CoreInlineExport(string Name, CoreSort Sort, uint Index);

public enum CoreSort
{
    Func,
    Table,
    Memory,
    Global,
    Type,
    Module,
    Instance,
}

// ---- Component instance expressions ----

public abstract record ComponentInstanceExpr;

public sealed record ComponentInstantiate(
    uint ComponentIndex,
    ImmutableArray<ComponentInstantiateArg> Args
) : ComponentInstanceExpr;

public sealed record ComponentInlineExports(ImmutableArray<ComponentInlineExport> Exports)
    : ComponentInstanceExpr;

public sealed record ComponentInstantiateArg(string Name, SortIdx Arg);

public sealed record ComponentInlineExport(string Name, SortIdx Idx);

// ---- Aliases ----

public abstract record Alias(ComponentSortKind Sort);

public sealed record AliasCoreExport(ComponentSortKind Sort, uint InstanceIndex, string Name)
    : Alias(Sort);

public sealed record AliasExport(ComponentSortKind Sort, uint InstanceIndex, string Name)
    : Alias(Sort);

public sealed record AliasOuter(ComponentSortKind Sort, uint Count, uint Index) : Alias(Sort);

// ---- Canonical function definitions ----

public abstract record Canon;

public sealed record CanonLift(uint CoreFuncIndex, uint TypeIndex, ImmutableArray<CanonOpt> Options)
    : Canon;

public sealed record CanonLower(uint FuncIndex, ImmutableArray<CanonOpt> Options) : Canon;

public sealed record CanonResourceNew(uint TypeIndex) : Canon;

public sealed record CanonResourceDrop(uint TypeIndex, bool Async) : Canon;

public sealed record CanonResourceRep(uint TypeIndex) : Canon;

public abstract record CanonOpt;

public enum StringEncoding
{
    Utf8,
    Utf16,
    Latin1Utf16,
}

public sealed record CanonStringEncoding(StringEncoding Encoding) : CanonOpt;

public sealed record CanonMemory(uint CoreMemIndex) : CanonOpt;

public sealed record CanonRealloc(uint CoreFuncIndex) : CanonOpt;

public sealed record CanonPostReturn(uint CoreFuncIndex) : CanonOpt;

// ---- Imports / exports / start ----

public sealed record ComponentImport(string Name, ExternDesc Desc);

public sealed record ComponentExport(string Name, SortIdx Idx, ExternDesc? Desc);

public sealed record ComponentStart(uint FuncIndex, ImmutableArray<uint> Args, uint ResultCount);
