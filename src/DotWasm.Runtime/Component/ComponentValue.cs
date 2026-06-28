namespace DotWasm.Runtime.Component;

// Host-facing representation of Component Model values. The mapping is type-directed
// (the component function type tells us how to interpret each value), so scalars use
// plain CLR types and only the structured cases need wrappers:
//
//   bool        -> bool
//   s8/u8       -> sbyte / byte
//   s16/u16     -> short / ushort
//   s32/u32     -> int / uint
//   s64/u64     -> long / ulong
//   f32/f64     -> float / double
//   char        -> int   (Unicode scalar value / rune)
//   string      -> string
//   list<T>     -> object?[]   (elements)
//   tuple<...>  -> object?[]   (in order)
//   record      -> object?[]   (field values in declaration order)
//   enum        -> uint        (case index)
//   variant     -> VariantValue
//   flags       -> FlagsValue
//   option<T>   -> OptionValue
//   result<O,E> -> ResultValue
//   own/borrow  -> ResourceValue

/// <summary>A variant value: the selected case index and its optional payload.</summary>
public sealed record VariantValue(uint Case, object? Payload = null);

/// <summary>A flags value: one boolean per declared label, in declaration order.</summary>
public sealed record FlagsValue(bool[] Values);

/// <summary>An option value. Use <see cref="None"/> or <see cref="Some"/>.</summary>
public sealed record OptionValue(bool HasValue, object? Value)
{
    public static readonly OptionValue None = new(false, null);

    public static OptionValue Some(object? value) => new(true, value);
}

/// <summary>A result value. Use <see cref="Ok"/> or <see cref="Err"/>.</summary>
public sealed record ResultValue(bool IsOk, object? Value)
{
    public static ResultValue Ok(object? value = null) => new(true, value);

    public static ResultValue Err(object? value = null) => new(false, value);
}

/// <summary>
/// A resource handle as seen by the host. <see cref="Rep"/> is the resource's i32
/// representation. <see cref="Owned"/> distinguishes <c>own</c> from <c>borrow</c>.
/// </summary>
public sealed record ResourceValue(int Rep, bool Owned, ResourceTypeIdentity Type);

/// <summary>Readability helpers for building component values. Records, tuples and lists
/// are all <c>object?[]</c>; these just make intent clear at the call site.</summary>
public static class Comp
{
    public static object?[] Record(params object?[] fields) => fields;

    public static object?[] Tuple(params object?[] items) => items;

    public static object?[] List(params object?[] items) => items;

    public static VariantValue Variant(uint @case, object? payload = null) => new(@case, payload);

    public static OptionValue Some(object? value) => OptionValue.Some(value);

    public static OptionValue None => OptionValue.None;

    public static ResultValue Ok(object? value = null) => ResultValue.Ok(value);

    public static ResultValue Err(object? value = null) => ResultValue.Err(value);

    public static FlagsValue Flags(params bool[] values) => new(values);
}
