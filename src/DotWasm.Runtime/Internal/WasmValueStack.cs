using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotWasm.Runtime;

/// <summary>
/// Backing store for the interpreter value stack, split into two parallel arrays sharing
/// one count:
/// <list type="bullet">
/// <item><c>bits</c> holds the scalar/bit payload for every slot.</item>
/// <item><c>refs</c> holds the reference payload; authoritative ONLY for reference-typed
/// slots.</item>
/// </list>
/// This shrinks the hot scalar slot from 16 bytes (the old <see cref="WasmValue"/>) to a
/// single 8-byte <see cref="ulong"/>, and lets scalar pushes/pops touch only the
/// (pointer-free, not GC-scanned) <c>bits</c> array.
///
/// Correctness contract:
/// <list type="bullet">
/// <item>The generic <see cref="Push(in WasmValue)"/> writes BOTH arrays (refs gets
/// <c>v.Reference</c>, which is null for scalars) so a generic round-trip reproduces the
/// value exactly and never leaves a stale reference for a generic push.</item>
/// <item>The scalar fast-path pushes (<see cref="PushBits"/> et al.) write ONLY
/// <c>bits</c>, deliberately leaving <c>refs</c> stale. This is safe because validation
/// guarantees a scalar-typed slot is only ever consumed via the scalar API (which reads
/// <c>bits</c> only), never via <c>.Reference</c>.</item>
/// <item><see cref="Clear"/> nulls the live <c>refs</c> region so references do not leak
/// (or get resurrected) across pooled-context reuse.</item>
/// </list>
/// </summary>
internal struct WasmValueStack
{
    const int InitialSize = 16;

    ulong[] bits;
    object?[] refs;
    int count;

    public WasmValueStack(int initialCapacity)
    {
        if (initialCapacity < 0)
            throw new ArgumentOutOfRangeException(
                nameof(initialCapacity),
                "Initial capacity must be non-negative."
            );

        bits = new ulong[initialCapacity];
        refs = new object?[initialCapacity];
        count = 0;
    }

    public readonly int Count => count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void EnsureCapacity()
    {
        if (count == bits.Length)
            Grow(bits.Length * 2);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Grow(int newSize)
    {
        if (newSize < InitialSize)
            newSize = InitialSize;
        Array.Resize(ref bits, newSize);
        Array.Resize(ref refs, newSize);
    }

    // ---------------------------------------------------------------------
    // Generic path (touches BOTH arrays).
    // ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(in WasmValue value)
    {
        EnsureCapacity();
        var c = count;
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bits), c) = value.Bits;
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(refs), c) = value.Reference;
        count = c + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WasmValue Pop()
    {
        if (count <= 0)
            throw new InvalidOperationException("Stack is empty");
        return UnsafePop();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WasmValue UnsafePop()
    {
        AssertNotEmpty();
        var c = --count;
        var b = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bits), c);
        var r = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(refs), c);
        return WasmValue.Combine(b, r);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly WasmValue Peek()
    {
        if (count <= 0)
            throw new InvalidOperationException("Stack is empty");
        var c = count - 1;
        var b = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bits), c);
        var r = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(refs), c);
        return WasmValue.Combine(b, r);
    }

    public void PushRange(ReadOnlySpan<WasmValue> items)
    {
        if (items.Length == 0)
            return;
        if (count + items.Length > bits.Length)
            Grow(Math.Max(bits.Length * 2, count + items.Length));
        for (int i = 0; i < items.Length; i++)
        {
            bits[count + i] = items[i].Bits;
            refs[count + i] = items[i].Reference;
        }
        count += items.Length;
    }

    /// <summary>
    /// Removes the top <paramref name="n"/> values and materializes them into a freshly
    /// allocated array so the caller sees an isolated snapshot. COLD path.
    /// </summary>
    public WasmValue[] Take(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, count);
        var result = new WasmValue[n];
        var start = count - n;
        for (int i = 0; i < n; i++)
            result[i] = WasmValue.Combine(bits[start + i], refs[start + i]);
        count = start;
        return result;
    }

    /// <summary>
    /// Materializes the whole live stack into a freshly allocated array. COLD path.
    /// </summary>
    public readonly WasmValue[] AsSpan()
    {
        var result = new WasmValue[count];
        for (int i = 0; i < count; i++)
            result[i] = WasmValue.Combine(bits[i], refs[i]);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Truncate(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, count);
        count = n;
    }

    public void Clear()
    {
        if (count > 0)
            Array.Clear(refs, 0, count);
        count = 0;
    }

    // ---------------------------------------------------------------------
    // Scalar fast path (touches bits[] ONLY — the perf win).
    // refs[count] is left STALE on purpose; never cleared.
    // ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushBits(ulong value)
    {
        EnsureCapacity();
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bits), count++) = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushI32(int value) => PushBits(unchecked((ulong)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushI64(long value) => PushBits(unchecked((ulong)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushF32(float value) => PushBits(Unsafe.As<float, uint>(ref value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushF64(double value) => PushBits(Unsafe.As<double, ulong>(ref value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong PopBits()
    {
        AssertNotEmpty();
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bits), --count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PopI32() => unchecked((int)PopBits());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long PopI64() => unchecked((long)PopBits());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float PopF32()
    {
        var b = (uint)PopBits();
        return Unsafe.As<uint, float>(ref b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double PopF64()
    {
        var b = PopBits();
        return Unsafe.As<ulong, double>(ref b);
    }

    /// <summary>
    /// Scalar analogue of the old <c>PopAndPeekTop</c>: pops the top scalar (returned via
    /// <paramref name="popped"/>) and returns a writable reference to the new-top bits slot.
    /// Used by binary scalar operators which consume two operands and produce one result
    /// occupying the first operand's slot, avoiding a redundant capacity check.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref ulong PopBitsAndPeekTop(out ulong popped)
    {
        AssertNotEmpty();
        ref var first = ref MemoryMarshal.GetArrayDataReference(bits);
        popped = Unsafe.Add(ref first, --count);
        Debug.Assert(count > 0);
        return ref Unsafe.Add(ref first, count - 1);
    }

    /// <summary>
    /// Returns a writable reference to the top scalar slot WITHOUT popping. Used by
    /// fused const-binop superinstructions which apply an immediate to the top in place.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref ulong PeekTopBits()
    {
        AssertNotEmpty();
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bits), count - 1);
    }

    [Conditional("DEBUG")]
    readonly void AssertNotEmpty()
    {
        if (count <= 0)
            throw new InvalidOperationException("Stack is empty");
    }
}
