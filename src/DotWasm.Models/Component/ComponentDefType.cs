using System.Collections.Immutable;

namespace DotWasm.Models.Component;

/// <summary>An entry in the component type index space (a <c>deftype</c>).</summary>
public abstract record ComponentDefType;

/// <summary>A defined value type (record, variant, primitive alias, etc.).</summary>
public sealed record TypeValDef(ComponentValType Type) : ComponentDefType;

public sealed record TypeFuncDef(ComponentFuncType Type) : ComponentDefType;

public sealed record TypeResourceDef(ResourceType Type) : ComponentDefType;

public sealed record TypeInstanceDef(InstanceType Type) : ComponentDefType;

public sealed record TypeComponentDef(ComponentDeclType Type) : ComponentDefType;

/// <summary>A component-level function type: named params and an optional single result.</summary>
public sealed record ComponentFuncType(
    ImmutableArray<NamedValType> Params,
    ComponentValType? Result
);

public sealed record NamedValType(string Name, ComponentValType Type);

/// <summary>
/// A resource type. <c>Rep</c> is the core representation (always <c>i32</c> today);
/// <c>DestructorFuncIndex</c> indexes the core function space when a destructor is present.
/// </summary>
public sealed record ResourceType(WasmValueType Rep, uint? DestructorFuncIndex);

// ---- Instance / component types (structural decls; decoded for completeness) ----

public sealed record InstanceType(ImmutableArray<InstanceDecl> Decls);

/// <summary>Component type (the structural type of a sub-component), named to avoid clashing
/// with the <see cref="ComponentDefType"/> case wrappers.</summary>
public sealed record ComponentDeclType(ImmutableArray<ComponentDecl> Decls);

public abstract record InstanceDecl;

public sealed record InstanceCoreTypeDecl(CoreTypeDef Type) : InstanceDecl;

public sealed record InstanceTypeDecl(ComponentDefType Type) : InstanceDecl;

public sealed record InstanceAliasDecl(Alias Alias) : InstanceDecl;

public sealed record InstanceExportDecl(string Name, ExternDesc Desc) : InstanceDecl;

public abstract record ComponentDecl;

public sealed record ComponentImportDecl(string Name, ExternDesc Desc) : ComponentDecl;

public sealed record ComponentInnerDecl(InstanceDecl Decl) : ComponentDecl;

/// <summary>Describes the type of an import or export.</summary>
public abstract record ExternDesc;

public sealed record CoreModuleDesc(uint CoreTypeIndex) : ExternDesc;

public sealed record FuncDesc(uint TypeIndex) : ExternDesc;

public sealed record ValueDesc(ComponentValType Type) : ExternDesc;

public sealed record TypeBoundDesc(bool IsEq, uint TypeIndex) : ExternDesc;

public sealed record ComponentDescType(uint TypeIndex) : ExternDesc;

public sealed record InstanceDesc(uint TypeIndex) : ExternDesc;

/// <summary>A core type entry (func type or module type) in the core type index space.</summary>
public abstract record CoreTypeDef;

public sealed record CoreFuncTypeDef(FuncType Type) : CoreTypeDef;

public sealed record CoreModuleTypeDef(ImmutableArray<CoreModuleDecl> Decls) : CoreTypeDef;

public abstract record CoreModuleDecl;

public sealed record CoreModuleImportDecl(string Module, string Name, ExternalType Type) : CoreModuleDecl;

public sealed record CoreModuleExportDecl(string Name, ExternalType Type) : CoreModuleDecl;

public sealed record CoreModuleTypeDecl(CoreTypeDef Type) : CoreModuleDecl;

public sealed record CoreModuleAliasDecl(Alias Alias) : CoreModuleDecl;
