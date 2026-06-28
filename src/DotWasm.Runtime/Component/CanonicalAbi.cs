using System.Buffers.Binary;
using DotWasm.Models.Component;
using SysEnc = System.Text.Encoding;

namespace DotWasm.Runtime.Component;

/// <summary>Calls the core <c>realloc</c> function: (origPtr, origSize, align, newSize) -> newPtr.</summary>
public delegate int ReallocFn(int origPtr, int origSize, int align, int newSize);

/// <summary>
/// Implements the WebAssembly Component Model Canonical ABI (synchronous profile):
/// memory load/store, flattening, and flat lift/lower for all value types including
/// resource handles. One context binds the canonical options (memory, realloc, string
/// encoding) and the owning instance's handle table for a single lift/lower operation.
/// </summary>
public sealed class CanonContext
{
    public required ComponentTypeContext Types { get; init; }
    public MemoryInstance? Memory { get; init; }
    public ReallocFn? Realloc { get; init; }
    public StringEncoding StringEncoding { get; init; } = StringEncoding.Utf8;

    /// <summary>Handle table of the instance owning this lift/lower (for own/borrow).</summary>
    public ComponentInstanceHandles? Handles { get; init; }

    /// <summary>Identity of the instance owning this context (for the lower_borrow optimization).</summary>
    public object? InstanceIdentity { get; init; }

    /// <summary>Owned handles lent out as borrows during the current call (to settle at call end).</summary>
    public List<ResourceHandle> BorrowLenders { get; } = [];

    const int Utf16Tag = unchecked((int)0x8000_0000);

    Span<byte> Mem
    {
        get
        {
            if (Memory is null)
                WasmTrapException.Throw("Canonical ABI operation requires a memory option.");
            return Memory!.Data;
        }
    }

    int CallRealloc(int origPtr, int origSize, int align, int newSize)
    {
        if (Realloc is null)
            WasmTrapException.Throw("Canonical ABI operation requires a realloc option.");
        var ptr = Realloc!(origPtr, origSize, align, newSize);
        if (ptr < 0 || ptr + newSize > Memory!.Data.Length)
            WasmTrapException.Throw("realloc returned an out-of-bounds pointer.");
        return ptr;
    }

    // ================= memory primitives =================

    static ulong LoadIntUnsigned(ReadOnlySpan<byte> mem, int ptr, int nbytes)
    {
        ulong v = 0;
        for (var i = 0; i < nbytes; i++)
            v |= (ulong)mem[ptr + i] << (8 * i);
        return v;
    }

    static void StoreIntBits(Span<byte> mem, int ptr, int nbytes, ulong bits)
    {
        for (var i = 0; i < nbytes; i++)
            mem[ptr + i] = (byte)(bits >> (8 * i));
    }

    // ================= store (value -> memory) =================

    public void Store(object? v, ComponentValType type, int ptr)
    {
        var t = Types.Structural(type);
        switch (t)
        {
            case PrimitiveType p:
                StorePrimitive(v, p.Kind, ptr);
                break;
            case ListType l:
                StoreList(v, l, ptr);
                break;
            case RecordType rec:
                StoreRecord((object?[])v!, rec, ptr);
                break;
            case VariantType variant:
                StoreVariant(v, variant, ptr);
                break;
            case FlagsType flags:
                StoreIntBits(Mem, ptr, ElemSizeFlags(flags.Names.Length), PackFlags((FlagsValue)v!, flags.Names.Length));
                break;
            case OwnType own:
                StoreIntBits(Mem, ptr, 4, (uint)LowerOwn((ResourceValue)v!, own.TypeIndex));
                break;
            case BorrowType borrow:
                StoreIntBits(Mem, ptr, 4, (uint)LowerBorrow((ResourceValue)v!, borrow.TypeIndex));
                break;
            default:
                WasmTrapException.Throw("Unsupported type in store.");
                break;
        }
    }

    void StorePrimitive(object? v, PrimitiveValType k, int ptr)
    {
        var mem = Mem;
        switch (k)
        {
            case PrimitiveValType.Bool: StoreIntBits(mem, ptr, 1, (bool)v! ? 1u : 0u); break;
            case PrimitiveValType.S8: StoreIntBits(mem, ptr, 1, (ulong)(byte)(sbyte)ToLong(v)); break;
            case PrimitiveValType.U8: StoreIntBits(mem, ptr, 1, (byte)ToULong(v)); break;
            case PrimitiveValType.S16: StoreIntBits(mem, ptr, 2, (ulong)(ushort)(short)ToLong(v)); break;
            case PrimitiveValType.U16: StoreIntBits(mem, ptr, 2, (ushort)ToULong(v)); break;
            case PrimitiveValType.S32: StoreIntBits(mem, ptr, 4, (uint)(int)ToLong(v)); break;
            case PrimitiveValType.U32: StoreIntBits(mem, ptr, 4, (uint)ToULong(v)); break;
            case PrimitiveValType.S64: StoreIntBits(mem, ptr, 8, (ulong)ToLong(v)); break;
            case PrimitiveValType.U64: StoreIntBits(mem, ptr, 8, ToULong(v)); break;
            case PrimitiveValType.F32: StoreIntBits(mem, ptr, 4, (uint)BitConverter.SingleToInt32Bits(ToFloat(v))); break;
            case PrimitiveValType.F64: StoreIntBits(mem, ptr, 8, (ulong)BitConverter.DoubleToInt64Bits(ToDouble(v))); break;
            case PrimitiveValType.Char: StoreIntBits(mem, ptr, 4, CharToI32(v)); break;
            case PrimitiveValType.String: StoreString((string)v!, ptr); break;
        }
    }

    void StoreString(string s, int ptr)
    {
        var (begin, taggedCodeUnits) = StoreStringIntoRange(s);
        var mem = Mem;
        StoreIntBits(mem, ptr, 4, (uint)begin);
        StoreIntBits(mem, ptr + 4, 4, (uint)taggedCodeUnits);
    }

    (int ptr, int taggedCodeUnits) StoreStringIntoRange(string s)
    {
        switch (StringEncoding)
        {
            case StringEncoding.Utf8:
            {
                var bytes = SysEnc.UTF8.GetBytes(s);
                var ptr = CallRealloc(0, 0, 1, bytes.Length);
                bytes.CopyTo(Mem[ptr..]);
                return (ptr, bytes.Length);
            }
            case StringEncoding.Utf16:
            {
                var bytes = SysEnc.Unicode.GetBytes(s);
                var ptr = CallRealloc(0, 0, 2, bytes.Length);
                bytes.CopyTo(Mem[ptr..]);
                return (ptr, bytes.Length / 2);
            }
            case StringEncoding.Latin1Utf16:
            {
                var latin1 = true;
                foreach (var ch in s)
                    if (ch >= 0x100) { latin1 = false; break; }
                if (latin1)
                {
                    var ptr = CallRealloc(0, 0, 1, s.Length);
                    var mem = Mem;
                    for (var i = 0; i < s.Length; i++)
                        mem[ptr + i] = (byte)s[i];
                    return (ptr, s.Length);
                }
                else
                {
                    var bytes = SysEnc.Unicode.GetBytes(s);
                    var ptr = CallRealloc(0, 0, 2, bytes.Length);
                    bytes.CopyTo(Mem[ptr..]);
                    return (ptr, (bytes.Length / 2) | Utf16Tag);
                }
            }
            default:
                WasmTrapException.Throw("Unknown string encoding.");
                return (0, 0);
        }
    }

    void StoreList(object? v, ListType l, int ptr)
    {
        var items = (object?[])v!;
        if (l.FixedLength is { } n)
        {
            if (items.Length != (int)n)
                WasmTrapException.Throw("Fixed-length list value has wrong length.");
            StoreListElems(items, l.Element, ptr);
            return;
        }
        var (begin, length) = StoreListIntoRange(items, l.Element);
        var mem = Mem;
        StoreIntBits(mem, ptr, 4, (uint)begin);
        StoreIntBits(mem, ptr + 4, 4, (uint)length);
    }

    (int ptr, int length) StoreListIntoRange(object?[] items, ComponentValType elem)
    {
        var elemSize = Types.ElemSize(elem);
        var byteLength = items.Length * elemSize;
        var ptr = CallRealloc(0, 0, Types.Alignment(elem), byteLength);
        StoreListElems(items, elem, ptr);
        return (ptr, items.Length);
    }

    void StoreListElems(object?[] items, ComponentValType elem, int ptr)
    {
        var elemSize = Types.ElemSize(elem);
        for (var i = 0; i < items.Length; i++)
            Store(items[i], elem, ptr + i * elemSize);
    }

    void StoreRecord(object?[] values, RecordType rec, int ptr)
    {
        if (values.Length != rec.Fields.Length)
            WasmTrapException.Throw("Record value has wrong field count.");
        for (var i = 0; i < rec.Fields.Length; i++)
        {
            var f = rec.Fields[i];
            ptr = ComponentTypeContext.AlignTo(ptr, Types.Alignment(f.Type));
            Store(values[i], f.Type, ptr);
            ptr += Types.ElemSize(f.Type);
        }
    }

    void StoreVariant(object? v, VariantType variant, int ptr)
    {
        var (caseIndex, payload) = MatchCase(v, variant);
        var discSize = ComponentTypeContext.DiscriminantSize(variant.Cases.Length);
        StoreIntBits(Mem, ptr, discSize, (uint)caseIndex);
        ptr += discSize;
        ptr = ComponentTypeContext.AlignTo(ptr, Types.MaxCaseAlignmentOf(variant));
        var c = variant.Cases[caseIndex];
        if (c.Type is not null)
            Store(payload, c.Type, ptr);
    }

    // ================= load (memory -> value) =================

    public object? Load(ComponentValType type, int ptr)
    {
        var t = Types.Structural(type);
        return t switch
        {
            PrimitiveType p => LoadPrimitive(p.Kind, ptr),
            ListType l => LoadList(l, ptr),
            RecordType rec => LoadRecord(rec, ptr),
            VariantType variant => LoadVariant(variant, ptr),
            FlagsType flags => UnpackFlags(LoadIntUnsigned(Mem, ptr, ElemSizeFlags(flags.Names.Length)), flags.Names.Length),
            OwnType own => LiftOwn((int)LoadIntUnsigned(Mem, ptr, 4), own.TypeIndex),
            BorrowType borrow => LiftBorrow((int)LoadIntUnsigned(Mem, ptr, 4), borrow.TypeIndex),
            _ => throw new WasmTrapException("Unsupported type in load."),
        };
    }

    object? LoadPrimitive(PrimitiveValType k, int ptr)
    {
        var mem = Mem;
        return k switch
        {
            PrimitiveValType.Bool => LoadIntUnsigned(mem, ptr, 1) != 0,
            PrimitiveValType.S8 => (sbyte)(byte)LoadIntUnsigned(mem, ptr, 1),
            PrimitiveValType.U8 => (byte)LoadIntUnsigned(mem, ptr, 1),
            PrimitiveValType.S16 => (short)(ushort)LoadIntUnsigned(mem, ptr, 2),
            PrimitiveValType.U16 => (ushort)LoadIntUnsigned(mem, ptr, 2),
            PrimitiveValType.S32 => (int)(uint)LoadIntUnsigned(mem, ptr, 4),
            PrimitiveValType.U32 => (uint)LoadIntUnsigned(mem, ptr, 4),
            PrimitiveValType.S64 => (long)LoadIntUnsigned(mem, ptr, 8),
            PrimitiveValType.U64 => LoadIntUnsigned(mem, ptr, 8),
            PrimitiveValType.F32 => CanonF32(BitConverter.Int32BitsToSingle((int)(uint)LoadIntUnsigned(mem, ptr, 4))),
            PrimitiveValType.F64 => CanonF64(BitConverter.Int64BitsToDouble((long)LoadIntUnsigned(mem, ptr, 8))),
            PrimitiveValType.Char => ConvertI32ToChar((uint)LoadIntUnsigned(mem, ptr, 4)),
            PrimitiveValType.String => LoadStringFromRange((int)LoadIntUnsigned(mem, ptr, 4), (int)LoadIntUnsigned(mem, ptr + 4, 4)),
            _ => throw new WasmTrapException("Unsupported primitive."),
        };
    }

    string LoadStringFromRange(int ptr, int taggedCodeUnits)
    {
        int byteLength;
        SysEnc encoding;
        switch (StringEncoding)
        {
            case StringEncoding.Utf8:
                byteLength = taggedCodeUnits;
                encoding = SysEnc.UTF8;
                break;
            case StringEncoding.Utf16:
                byteLength = 2 * taggedCodeUnits;
                encoding = SysEnc.Unicode;
                break;
            case StringEncoding.Latin1Utf16:
                if ((taggedCodeUnits & Utf16Tag) != 0)
                {
                    byteLength = 2 * (taggedCodeUnits ^ Utf16Tag);
                    encoding = SysEnc.Unicode;
                }
                else
                {
                    byteLength = taggedCodeUnits;
                    encoding = SysEnc.Latin1;
                }
                break;
            default:
                WasmTrapException.Throw("Unknown string encoding.");
                return "";
        }
        var mem = Mem;
        if (ptr < 0 || ptr + byteLength > mem.Length)
            WasmTrapException.Throw("String out of bounds.");
        return encoding.GetString(mem.Slice(ptr, byteLength));
    }

    object?[] LoadList(ListType l, int ptr)
    {
        if (l.FixedLength is { } n)
            return LoadListFromValidRange(ptr, (int)n, l.Element);
        var begin = (int)LoadIntUnsigned(Mem, ptr, 4);
        var length = (int)LoadIntUnsigned(Mem, ptr + 4, 4);
        return LoadListFromValidRange(begin, length, l.Element);
    }

    object?[] LoadListFromValidRange(int ptr, int length, ComponentValType elem)
    {
        var elemSize = Types.ElemSize(elem);
        var result = new object?[length];
        for (var i = 0; i < length; i++)
            result[i] = Load(elem, ptr + i * elemSize);
        return result;
    }

    object?[] LoadRecord(RecordType rec, int ptr)
    {
        var values = new object?[rec.Fields.Length];
        for (var i = 0; i < rec.Fields.Length; i++)
        {
            var f = rec.Fields[i];
            ptr = ComponentTypeContext.AlignTo(ptr, Types.Alignment(f.Type));
            values[i] = Load(f.Type, ptr);
            ptr += Types.ElemSize(f.Type);
        }
        return values;
    }

    object? LoadVariant(VariantType variant, int ptr)
    {
        var discSize = ComponentTypeContext.DiscriminantSize(variant.Cases.Length);
        var caseIndex = (int)LoadIntUnsigned(Mem, ptr, discSize);
        ptr += discSize;
        if (caseIndex >= variant.Cases.Length)
            WasmTrapException.Throw("Variant case index out of range.");
        ptr = ComponentTypeContext.AlignTo(ptr, Types.MaxCaseAlignmentOf(variant));
        var c = variant.Cases[caseIndex];
        var payload = c.Type is null ? null : Load(c.Type, ptr);
        return BuildVariantHostValue(variant, (uint)caseIndex, payload);
    }

    // ================= flat lower (value -> core bits) =================

    public void LowerFlat(object? v, ComponentValType type, List<ulong> outBits)
    {
        var t = Types.Structural(type);
        switch (t)
        {
            case PrimitiveType p:
                LowerFlatPrimitive(v, p.Kind, outBits);
                break;
            case ListType l:
                LowerFlatList(v, l, outBits);
                break;
            case RecordType rec:
                LowerFlatRecord((object?[])v!, rec, outBits);
                break;
            case VariantType variant:
                LowerFlatVariant(v, variant, outBits);
                break;
            case FlagsType flags:
                outBits.Add(PackFlags((FlagsValue)v!, flags.Names.Length));
                break;
            case OwnType own:
                outBits.Add((uint)LowerOwn((ResourceValue)v!, own.TypeIndex));
                break;
            case BorrowType borrow:
                outBits.Add((uint)LowerBorrow((ResourceValue)v!, borrow.TypeIndex));
                break;
            default:
                WasmTrapException.Throw("Unsupported type in lower_flat.");
                break;
        }
    }

    void LowerFlatPrimitive(object? v, PrimitiveValType k, List<ulong> o)
    {
        switch (k)
        {
            case PrimitiveValType.Bool: o.Add((bool)v! ? 1u : 0u); break;
            case PrimitiveValType.U8: o.Add((byte)ToULong(v)); break;
            case PrimitiveValType.U16: o.Add((ushort)ToULong(v)); break;
            case PrimitiveValType.U32: o.Add((uint)ToULong(v)); break;
            case PrimitiveValType.U64: o.Add(ToULong(v)); break;
            case PrimitiveValType.S8: o.Add((uint)(int)ToLong(v)); break;
            case PrimitiveValType.S16: o.Add((uint)(int)ToLong(v)); break;
            case PrimitiveValType.S32: o.Add((uint)(int)ToLong(v)); break;
            case PrimitiveValType.S64: o.Add((ulong)ToLong(v)); break;
            case PrimitiveValType.F32: o.Add((uint)BitConverter.SingleToInt32Bits(ToFloat(v))); break;
            case PrimitiveValType.F64: o.Add((ulong)BitConverter.DoubleToInt64Bits(ToDouble(v))); break;
            case PrimitiveValType.Char: o.Add(CharToI32(v)); break;
            case PrimitiveValType.String:
            {
                var (ptr, len) = StoreStringIntoRange((string)v!);
                o.Add((uint)ptr);
                o.Add((uint)len);
                break;
            }
        }
    }

    void LowerFlatList(object? v, ListType l, List<ulong> o)
    {
        var items = (object?[])v!;
        if (l.FixedLength is { } n)
        {
            if (items.Length != (int)n)
                WasmTrapException.Throw("Fixed-length list value has wrong length.");
            foreach (var e in items)
                LowerFlat(e, l.Element, o);
            return;
        }
        var (ptr, length) = StoreListIntoRange(items, l.Element);
        o.Add((uint)ptr);
        o.Add((uint)length);
    }

    void LowerFlatRecord(object?[] values, RecordType rec, List<ulong> o)
    {
        if (values.Length != rec.Fields.Length)
            WasmTrapException.Throw("Record value has wrong field count.");
        for (var i = 0; i < rec.Fields.Length; i++)
            LowerFlat(values[i], rec.Fields[i].Type, o);
    }

    void LowerFlatVariant(object? v, VariantType variant, List<ulong> o)
    {
        var (caseIndex, payload) = MatchCase(v, variant);
        var joined = Types.FlattenType(variant); // [i32 disc, ...payload]
        o.Add((uint)caseIndex);
        var before = o.Count;
        var c = variant.Cases[caseIndex];
        if (c.Type is not null)
            LowerFlat(payload, c.Type, o);
        // pad remaining joined payload slots with zero (bits are type-agnostic; see join())
        var produced = o.Count - before;
        var payloadSlots = joined.Count - 1;
        for (var i = produced; i < payloadSlots; i++)
            o.Add(0);
    }

    // ================= flat lift (core bits -> value) =================

    public sealed class CoreValueIter(IReadOnlyList<ulong> values)
    {
        int i;
        public ulong Next() => values[i++];
        public bool Done => i == values.Count;
        public void Skip(int n) => i += n;
    }

    public object? LiftFlat(CoreValueIter vi, ComponentValType type)
    {
        var t = Types.Structural(type);
        switch (t)
        {
            case PrimitiveType p:
                return LiftFlatPrimitive(vi, p.Kind);
            case ListType l:
                return LiftFlatList(vi, l);
            case RecordType rec:
            {
                var values = new object?[rec.Fields.Length];
                for (var i = 0; i < rec.Fields.Length; i++)
                    values[i] = LiftFlat(vi, rec.Fields[i].Type);
                return values;
            }
            case VariantType variant:
                return LiftFlatVariant(vi, variant);
            case FlagsType flags:
                return UnpackFlags(vi.Next() & 0xFFFFFFFF, flags.Names.Length);
            case OwnType own:
                return LiftOwn((int)vi.Next(), own.TypeIndex);
            case BorrowType borrow:
                return LiftBorrow((int)vi.Next(), borrow.TypeIndex);
            default:
                throw new WasmTrapException("Unsupported type in lift_flat.");
        }
    }

    object? LiftFlatPrimitive(CoreValueIter vi, PrimitiveValType k)
    {
        return k switch
        {
            PrimitiveValType.Bool => (vi.Next() & 0xFFFFFFFF) != 0,
            PrimitiveValType.U8 => (byte)vi.Next(),
            PrimitiveValType.U16 => (ushort)vi.Next(),
            PrimitiveValType.U32 => (uint)vi.Next(),
            PrimitiveValType.U64 => vi.Next(),
            PrimitiveValType.S8 => (sbyte)(byte)vi.Next(),
            PrimitiveValType.S16 => (short)(ushort)vi.Next(),
            PrimitiveValType.S32 => (int)(uint)vi.Next(),
            PrimitiveValType.S64 => (long)vi.Next(),
            PrimitiveValType.F32 => CanonF32(BitConverter.Int32BitsToSingle((int)(uint)vi.Next())),
            PrimitiveValType.F64 => CanonF64(BitConverter.Int64BitsToDouble((long)vi.Next())),
            PrimitiveValType.Char => ConvertI32ToChar((uint)vi.Next()),
            PrimitiveValType.String => LiftFlatString(vi),
            _ => throw new WasmTrapException("Unsupported primitive."),
        };
    }

    string LiftFlatString(CoreValueIter vi)
    {
        var ptr = (int)vi.Next();
        var packed = (int)vi.Next();
        return LoadStringFromRange(ptr, packed);
    }

    object?[] LiftFlatList(CoreValueIter vi, ListType l)
    {
        if (l.FixedLength is { } n)
        {
            var arr = new object?[(int)n];
            for (var i = 0; i < (int)n; i++)
                arr[i] = LiftFlat(vi, l.Element);
            return arr;
        }
        var ptr = (int)vi.Next();
        var length = (int)vi.Next();
        return LoadListFromValidRange(ptr, length, l.Element);
    }

    object? LiftFlatVariant(CoreValueIter vi, VariantType variant)
    {
        var joined = Types.FlattenType(variant); // [i32 disc, payload...]
        var caseIndex = (int)(uint)vi.Next();
        if (caseIndex >= variant.Cases.Length)
            WasmTrapException.Throw("Variant case index out of range.");
        var c = variant.Cases[caseIndex];
        object? payload = null;
        var consumed = 0;
        if (c.Type is not null)
        {
            // Lift consumes exactly flatten(c.Type) slots; bits are reinterpreted by target type.
            consumed = Types.FlattenType(c.Type).Count;
            payload = LiftFlat(vi, c.Type);
        }
        // skip remaining joined payload slots
        var payloadSlots = joined.Count - 1;
        vi.Skip(payloadSlots - consumed);
        return BuildVariantHostValue(variant, (uint)caseIndex, payload);
    }

    // ================= top-level lift/lower of value lists =================

    public List<ulong> LowerFlatValues(IReadOnlyList<object?> values, IReadOnlyList<ComponentValType> types, int maxFlat)
    {
        var flatTypes = Types.FlattenTypes(types);
        if (flatTypes.Count > maxFlat)
        {
            // store a tuple of all values in memory, pass a single pointer
            var tupleType = new TupleType([.. types]);
            var align = Types.Alignment(tupleType);
            var size = Types.ElemSize(tupleType);
            var ptr = CallRealloc(0, 0, align, size);
            StoreTuple(values, types, ptr);
            return [(uint)ptr];
        }
        var bits = new List<ulong>();
        for (var i = 0; i < values.Count; i++)
            LowerFlat(values[i], types[i], bits);
        return bits;
    }

    /// <summary>Lower values into a caller-provided out-pointer buffer (canon lower indirect results).</summary>
    public void LowerFlatValuesIntoOutPtr(IReadOnlyList<object?> values, IReadOnlyList<ComponentValType> types, int outPtr)
    {
        StoreTuple(values, types, outPtr);
    }

    void StoreTuple(IReadOnlyList<object?> values, IReadOnlyList<ComponentValType> types, int ptr)
    {
        for (var i = 0; i < types.Count; i++)
        {
            ptr = ComponentTypeContext.AlignTo(ptr, Types.Alignment(types[i]));
            Store(values[i], types[i], ptr);
            ptr += Types.ElemSize(types[i]);
        }
    }

    public object?[] LiftFlatValues(CoreValueIter vi, IReadOnlyList<ComponentValType> types, int maxFlat)
    {
        var flatTypes = Types.FlattenTypes(types);
        if (flatTypes.Count > maxFlat)
        {
            var ptr = (int)vi.Next();
            return LoadTuple(types, ptr);
        }
        var result = new object?[types.Count];
        for (var i = 0; i < types.Count; i++)
            result[i] = LiftFlat(vi, types[i]);
        return result;
    }

    object?[] LoadTuple(IReadOnlyList<ComponentValType> types, int ptr)
    {
        var result = new object?[types.Count];
        for (var i = 0; i < types.Count; i++)
        {
            ptr = ComponentTypeContext.AlignTo(ptr, Types.Alignment(types[i]));
            result[i] = Load(types[i], ptr);
            ptr += Types.ElemSize(types[i]);
        }
        return result;
    }

    // ================= resource handles =================

    int LowerOwn(ResourceValue v, uint typeIndex)
    {
        var rt = Types.Resource(typeIndex);
        RequireHandles();
        return Handles!.Add(new ResourceHandle(rt, v.Rep, own: true));
    }

    int LowerBorrow(ResourceValue v, uint typeIndex)
    {
        var rt = Types.Resource(typeIndex);
        if (ReferenceEquals(InstanceIdentity, rt.ImplementingInstance))
            return v.Rep;
        RequireHandles();
        return Handles!.Add(new ResourceHandle(rt, v.Rep, own: false));
    }

    object LiftOwn(int i, uint typeIndex)
    {
        var rt = Types.Resource(typeIndex);
        RequireHandles();
        var h = Handles!.Remove(i);
        if (!ReferenceEquals(h.Type, rt))
            WasmTrapException.Throw("Resource type mismatch on own lift.");
        if (h.NumLends != 0)
            WasmTrapException.Throw("Cannot transfer own handle that is currently lent.");
        if (!h.Own)
            WasmTrapException.Throw("Cannot lift a borrow handle as own.");
        return new ResourceValue(h.Rep, Owned: true, rt);
    }

    object LiftBorrow(int i, uint typeIndex)
    {
        var rt = Types.Resource(typeIndex);
        RequireHandles();
        var h = Handles!.Get(i);
        if (!ReferenceEquals(h.Type, rt))
            WasmTrapException.Throw("Resource type mismatch on borrow lift.");
        if (h.Own)
        {
            h.NumLends++;
            BorrowLenders.Add(h);
        }
        return new ResourceValue(h.Rep, Owned: false, rt);
    }

    void RequireHandles()
    {
        if (Handles is null)
            WasmTrapException.Throw("Resource operation requires a handle table.");
    }

    // ================= shared helpers =================

    static int ElemSizeFlags(int n) => n <= 8 ? 1 : n <= 16 ? 2 : 4;

    static ulong PackFlags(FlagsValue v, int n)
    {
        ulong bits = 0;
        for (var i = 0; i < n && i < v.Values.Length; i++)
            if (v.Values[i])
                bits |= 1ul << i;
        return bits;
    }

    static FlagsValue UnpackFlags(ulong bits, int n)
    {
        var values = new bool[n];
        for (var i = 0; i < n; i++)
            values[i] = (bits & (1ul << i)) != 0;
        return new FlagsValue(values);
    }

    (int caseIndex, object? payload) MatchCase(object? v, VariantType variant)
    {
        switch (v)
        {
            case VariantValue vv:
                return ((int)vv.Case, vv.Payload);
            case OptionValue ov:
                return ov.HasValue ? (1, ov.Value) : (0, null);
            case ResultValue rv:
                return rv.IsOk ? (0, rv.Value) : (1, rv.Value);
            case uint e:
                return ((int)e, null);
            case int e:
                return (e, null);
            default:
                WasmTrapException.Throw("Invalid variant host value.");
                return (0, null);
        }
    }

    // Build the host value for a variant whose original spec type may be a specialization.
    object? BuildVariantHostValue(VariantType variant, uint caseIndex, object? payload)
    {
        // Heuristic by despecialized shape: option ("none"/"some"), result ("ok"/"error").
        var cases = variant.Cases;
        if (cases.Length == 2 && cases[0].Name == "none" && cases[1].Name == "some")
            return caseIndex == 0 ? OptionValue.None : OptionValue.Some(payload);
        if (cases.Length == 2 && cases[0].Name == "ok" && cases[1].Name == "error")
            return caseIndex == 0 ? ResultValue.Ok(payload) : ResultValue.Err(payload);
        if (cases.All(c => c.Type is null))
            return caseIndex; // enum
        return new VariantValue(caseIndex, payload);
    }

    // numeric coercions (host boxed value -> primitive)
    static long ToLong(object? v) => v switch
    {
        sbyte x => x, byte x => x, short x => x, ushort x => x,
        int x => x, uint x => x, long x => x, ulong x => (long)x,
        _ => Convert.ToInt64(v),
    };

    static ulong ToULong(object? v) => v switch
    {
        sbyte x => (ulong)x, byte x => x, short x => (ulong)x, ushort x => x,
        int x => (ulong)x, uint x => x, long x => (ulong)x, ulong x => x,
        _ => Convert.ToUInt64(v),
    };

    static float ToFloat(object? v) => v switch { float x => x, double x => (float)x, _ => Convert.ToSingle(v) };
    static double ToDouble(object? v) => v switch { double x => x, float x => x, _ => Convert.ToDouble(v) };

    static uint CharToI32(object? v)
    {
        var i = v switch { int x => x, uint x => (int)x, char x => x, _ => Convert.ToInt32(v) };
        if (i < 0 || i >= 0x110000 || (i >= 0xD800 && i <= 0xDFFF))
            WasmTrapException.Throw("Invalid char (not a Unicode scalar value).");
        return (uint)i;
    }

    static int ConvertI32ToChar(uint i)
    {
        if (i >= 0x110000 || (i >= 0xD800 && i <= 0xDFFF))
            WasmTrapException.Throw("Invalid char (not a Unicode scalar value).");
        return (int)i;
    }

    const uint CanonicalF32Nan = 0x7fc00000;
    const ulong CanonicalF64Nan = 0x7ff8000000000000;

    static float CanonF32(float f) =>
        float.IsNaN(f) ? BitConverter.Int32BitsToSingle((int)CanonicalF32Nan) : f;

    static double CanonF64(double f) =>
        double.IsNaN(f) ? BitConverter.Int64BitsToDouble((long)CanonicalF64Nan) : f;

    // ================= core <-> bits conversion at the call boundary =================

    public static WasmValue[] BitsToCore(IReadOnlyList<ulong> bits, IReadOnlyList<CoreFlatType> types)
    {
        var result = new WasmValue[bits.Count];
        for (var i = 0; i < bits.Count; i++)
        {
            result[i] = types[i] switch
            {
                CoreFlatType.I32 => WasmValue.FromI32((int)(uint)bits[i]),
                CoreFlatType.I64 => WasmValue.FromI64((long)bits[i]),
                CoreFlatType.F32 => WasmValue.FromF32(BitConverter.Int32BitsToSingle((int)(uint)bits[i])),
                CoreFlatType.F64 => WasmValue.FromF64(BitConverter.Int64BitsToDouble((long)bits[i])),
                _ => default,
            };
        }
        return result;
    }

    public static List<ulong> CoreToBits(ReadOnlySpan<WasmValue> values, IReadOnlyList<CoreFlatType> types)
    {
        var result = new List<ulong>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            result.Add(types[i] switch
            {
                CoreFlatType.I32 => (uint)values[i].I32,
                CoreFlatType.I64 => (ulong)values[i].I64,
                CoreFlatType.F32 => (uint)BitConverter.SingleToInt32Bits(values[i].F32),
                CoreFlatType.F64 => (ulong)BitConverter.DoubleToInt64Bits(values[i].F64),
                _ => 0,
            });
        }
        return result;
    }
}
