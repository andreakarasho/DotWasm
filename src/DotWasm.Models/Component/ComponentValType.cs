using System.Collections.Immutable;

namespace DotWasm.Models.Component;

/// <summary>
/// A Component Model value type (<c>valtype</c> / <c>defvaltype</c>). Either a primitive,
/// a reference to a type-space entry, or an inline structural type.
/// </summary>
public abstract record ComponentValType;

public enum PrimitiveValType
{
    Bool,
    S8,
    U8,
    S16,
    U16,
    S32,
    U32,
    S64,
    U64,
    F32,
    F64,
    Char,
    String,
}

/// <summary>A primitive value type (bool, integers, floats, char, string).</summary>
public sealed record PrimitiveType(PrimitiveValType Kind) : ComponentValType;

/// <summary>
/// A reference to a defined type by index in the component type index space.
/// Produced when a <c>valtype</c> decodes to a non-negative type index.
/// </summary>
public sealed record DefinedTypeRef(uint TypeIndex) : ComponentValType;

public sealed record RecordType(ImmutableArray<RecordField> Fields) : ComponentValType;

public sealed record RecordField(string Name, ComponentValType Type);

public sealed record VariantType(ImmutableArray<VariantCase> Cases) : ComponentValType;

public sealed record VariantCase(string Name, ComponentValType? Type, uint? Refines);

/// <summary><c>FixedLength</c> is non-null for the fixed-length list form <c>list&lt;T, N&gt;</c>.</summary>
public sealed record ListType(ComponentValType Element, uint? FixedLength = null) : ComponentValType;

public sealed record TupleType(ImmutableArray<ComponentValType> Types) : ComponentValType;

public sealed record FlagsType(ImmutableArray<string> Names) : ComponentValType;

public sealed record EnumType(ImmutableArray<string> Names) : ComponentValType;

public sealed record OptionType(ComponentValType Type) : ComponentValType;

public sealed record ResultType(ComponentValType? Ok, ComponentValType? Err) : ComponentValType;

/// <summary>An owned handle to a resource defined by the given type index.</summary>
public sealed record OwnType(uint TypeIndex) : ComponentValType;

/// <summary>A borrowed handle to a resource defined by the given type index.</summary>
public sealed record BorrowType(uint TypeIndex) : ComponentValType;
