using System.Collections.Immutable;
using DotWasm.Models.Component;

namespace DotWasm.Runtime.Component;

/// <summary>The four core wasm value types a component type can flatten to.</summary>
public enum CoreFlatType
{
    I32,
    I64,
    F32,
    F64,
}

/// <summary>Stable identity of a resource type within a component instance.</summary>
public sealed class ResourceTypeIdentity
{
    public uint? DestructorCoreFuncIndex { get; init; }

    /// <summary>Resolved destructor (set during instantiation), or null.</summary>
    public FunctionInstance? Destructor { get; set; }

    /// <summary>The instance that defines this resource (owns its rep + table semantics).</summary>
    public object? ImplementingInstance { get; set; }
}

/// <summary>
/// Resolves component value types against a component's type index space and computes
/// the Canonical ABI type math (alignment, element size, flattening). All numbers follow
/// the spec for a 32-bit memory (ptr_size = 4).
/// </summary>
public sealed class ComponentTypeContext
{
    public const int PtrSize = 4;

    /// <summary>Synthetic resource type indices (for resources reached only through inlined
    /// imported-instance types) start here, above any real type-space index.</summary>
    public const uint SyntheticResourceBase = 0x4000_0000;

    readonly ImmutableArray<ComponentDefType> types;
    readonly ResourceTypeIdentity[] resources;
    readonly IReadOnlyDictionary<uint, ResourceTypeIdentity>? syntheticResources;

    public ComponentTypeContext(
        ImmutableArray<ComponentDefType> types,
        ResourceTypeIdentity[] resources,
        IReadOnlyDictionary<uint, ResourceTypeIdentity>? syntheticResources = null
    )
    {
        this.types = types;
        this.resources = resources;
        this.syntheticResources = syntheticResources;
    }

    public ResourceTypeIdentity Resource(uint typeIndex)
    {
        if (typeIndex >= SyntheticResourceBase && syntheticResources is not null)
            return syntheticResources[typeIndex];
        return resources[(int)typeIndex];
    }

    /// <summary>Resolve type references and despecialize tuple/enum/option/result one level,
    /// returning a structural type: primitive, record, variant, list, flags, own or borrow.</summary>
    public ComponentValType Structural(ComponentValType t)
    {
        // Follow type-index references (including type aliases) to a structural form.
        var guard = 0;
        while (t is DefinedTypeRef r)
        {
            if (++guard > 10_000)
                throw new WasmComponentException($"Cyclic type reference at type #{r.TypeIndex}.");
            var def = types[(int)r.TypeIndex];
            // A bare reference to a resource type denotes an owned handle (WIT shorthand).
            if (def is TypeResourceDef)
                return new OwnType(r.TypeIndex);
            t = def switch
            {
                TypeValDef v => v.Type,
                _ => throw new WasmComponentException($"Type #{r.TypeIndex} is not a value type."),
            };
        }

        return t switch
        {
            TupleType tup => new RecordType(
                tup.Types.Select((ty, i) => new RecordField(i.ToString(), ty)).ToImmutableArray()),
            EnumType e => new VariantType(
                e.Names.Select(n => new VariantCase(n, null, null)).ToImmutableArray()),
            OptionType o => new VariantType(
            [
                new VariantCase("none", null, null),
                new VariantCase("some", o.Type, null),
            ]),
            ResultType res => new VariantType(
            [
                new VariantCase("ok", res.Ok, null),
                new VariantCase("error", res.Err, null),
            ]),
            _ => t,
        };
    }

    // ---- Alignment ----

    public int Alignment(ComponentValType type)
    {
        var t = Structural(type);
        return t switch
        {
            PrimitiveType p => PrimitiveAlignment(p.Kind),
            ListType { FixedLength: not null } l => Alignment(l.Element),
            ListType => PtrSize,
            RecordType rec => AlignmentRecord(rec),
            VariantType v => AlignmentVariant(v),
            FlagsType f => AlignmentFlags(f.Names.Length),
            OwnType or BorrowType => 4,
            _ => throw Unsupported(t),
        };
    }

    static int PrimitiveAlignment(PrimitiveValType k) => k switch
    {
        PrimitiveValType.Bool or PrimitiveValType.S8 or PrimitiveValType.U8 => 1,
        PrimitiveValType.S16 or PrimitiveValType.U16 => 2,
        PrimitiveValType.S32 or PrimitiveValType.U32 or PrimitiveValType.F32 or PrimitiveValType.Char => 4,
        PrimitiveValType.S64 or PrimitiveValType.U64 or PrimitiveValType.F64 => 8,
        PrimitiveValType.String => PtrSize,
        _ => 1,
    };

    int AlignmentRecord(RecordType rec)
    {
        var a = 1;
        foreach (var f in rec.Fields)
            a = Math.Max(a, Alignment(f.Type));
        return a;
    }

    int AlignmentVariant(VariantType v) =>
        Math.Max(DiscriminantSize(v.Cases.Length), MaxCaseAlignment(v.Cases));

    static int AlignmentFlags(int n)
    {
        if (n <= 8) return 1;
        if (n <= 16) return 2;
        return 4;
    }

    // ---- Element size ----

    public int ElemSize(ComponentValType type)
    {
        var t = Structural(type);
        return t switch
        {
            PrimitiveType p => PrimitiveSize(p.Kind),
            ListType { FixedLength: { } n } l => (int)n * ElemSize(l.Element),
            ListType => 2 * PtrSize,
            RecordType rec => ElemSizeRecord(rec),
            VariantType v => ElemSizeVariant(v),
            FlagsType f => ElemSizeFlags(f.Names.Length),
            OwnType or BorrowType => 4,
            _ => throw Unsupported(t),
        };
    }

    static int PrimitiveSize(PrimitiveValType k) => k switch
    {
        PrimitiveValType.Bool or PrimitiveValType.S8 or PrimitiveValType.U8 => 1,
        PrimitiveValType.S16 or PrimitiveValType.U16 => 2,
        PrimitiveValType.S32 or PrimitiveValType.U32 or PrimitiveValType.F32 or PrimitiveValType.Char => 4,
        PrimitiveValType.S64 or PrimitiveValType.U64 or PrimitiveValType.F64 => 8,
        PrimitiveValType.String => 2 * PtrSize,
        _ => 1,
    };

    int ElemSizeRecord(RecordType rec)
    {
        var s = 0;
        foreach (var f in rec.Fields)
        {
            s = AlignTo(s, Alignment(f.Type));
            s += ElemSize(f.Type);
        }
        return AlignTo(s, AlignmentRecord(rec));
    }

    int ElemSizeVariant(VariantType v)
    {
        var s = DiscriminantSize(v.Cases.Length);
        s = AlignTo(s, MaxCaseAlignment(v.Cases));
        var maxPayload = 0;
        foreach (var c in v.Cases)
            if (c.Type is not null)
                maxPayload = Math.Max(maxPayload, ElemSize(c.Type));
        s += maxPayload;
        return AlignTo(s, AlignmentVariant(v));
    }

    static int ElemSizeFlags(int n)
    {
        if (n <= 8) return 1;
        if (n <= 16) return 2;
        return 4;
    }

    // ---- Discriminant / case helpers ----

    public static int DiscriminantSize(int caseCount)
    {
        if (caseCount <= (1 << 8)) return 1;
        if (caseCount <= (1 << 16)) return 2;
        return 4;
    }

    int MaxCaseAlignment(ImmutableArray<VariantCase> cases)
    {
        var a = 1;
        foreach (var c in cases)
            if (c.Type is not null)
                a = Math.Max(a, Alignment(c.Type));
        return a;
    }

    public int MaxCaseAlignmentOf(VariantType v) => MaxCaseAlignment(v.Cases);

    // ---- Flattening ----

    public List<CoreFlatType> FlattenType(ComponentValType type)
    {
        var flat = new List<CoreFlatType>();
        FlattenInto(type, flat);
        return flat;
    }

    public List<CoreFlatType> FlattenTypes(IEnumerable<ComponentValType> typesToFlatten)
    {
        var flat = new List<CoreFlatType>();
        foreach (var t in typesToFlatten)
            FlattenInto(t, flat);
        return flat;
    }

    void FlattenInto(ComponentValType type, List<CoreFlatType> flat)
    {
        var t = Structural(type);
        switch (t)
        {
            case PrimitiveType p:
                FlattenPrimitive(p.Kind, flat);
                break;
            case ListType { FixedLength: { } n } l:
                for (var i = 0u; i < n; i++)
                    FlattenInto(l.Element, flat);
                break;
            case ListType:
                flat.Add(CoreFlatType.I32);
                flat.Add(CoreFlatType.I32);
                break;
            case RecordType rec:
                foreach (var f in rec.Fields)
                    FlattenInto(f.Type, flat);
                break;
            case VariantType v:
                FlattenVariant(v, flat);
                break;
            case FlagsType:
                flat.Add(CoreFlatType.I32);
                break;
            case OwnType or BorrowType:
                flat.Add(CoreFlatType.I32);
                break;
            default:
                throw Unsupported(t);
        }
    }

    static void FlattenPrimitive(PrimitiveValType k, List<CoreFlatType> flat)
    {
        switch (k)
        {
            case PrimitiveValType.Bool:
            case PrimitiveValType.S8:
            case PrimitiveValType.U8:
            case PrimitiveValType.S16:
            case PrimitiveValType.U16:
            case PrimitiveValType.S32:
            case PrimitiveValType.U32:
            case PrimitiveValType.Char:
                flat.Add(CoreFlatType.I32);
                break;
            case PrimitiveValType.S64:
            case PrimitiveValType.U64:
                flat.Add(CoreFlatType.I64);
                break;
            case PrimitiveValType.F32:
                flat.Add(CoreFlatType.F32);
                break;
            case PrimitiveValType.F64:
                flat.Add(CoreFlatType.F64);
                break;
            case PrimitiveValType.String:
                flat.Add(CoreFlatType.I32);
                flat.Add(CoreFlatType.I32);
                break;
        }
    }

    void FlattenVariant(VariantType v, List<CoreFlatType> flat)
    {
        // discriminant is always i32 when flattened
        flat.Add(CoreFlatType.I32);
        var payload = new List<CoreFlatType>();
        foreach (var c in v.Cases)
        {
            if (c.Type is null)
                continue;
            var caseFlat = FlattenType(c.Type);
            for (var i = 0; i < caseFlat.Count; i++)
            {
                if (i < payload.Count)
                    payload[i] = Join(payload[i], caseFlat[i]);
                else
                    payload.Add(caseFlat[i]);
            }
        }
        flat.AddRange(payload);
    }

    /// <summary>Unify two flat core types: identical stays; i32/f32 -> i32; otherwise i64.</summary>
    public static CoreFlatType Join(CoreFlatType a, CoreFlatType b)
    {
        if (a == b) return a;
        if ((a == CoreFlatType.I32 && b == CoreFlatType.F32) ||
            (a == CoreFlatType.F32 && b == CoreFlatType.I32))
            return CoreFlatType.I32;
        return CoreFlatType.I64;
    }

    public static int AlignTo(int addr, int alignment) =>
        (addr + alignment - 1) & ~(alignment - 1);

    static WasmComponentException Unsupported(ComponentValType t) =>
        new($"Unsupported component value type in ABI: {t.GetType().Name}.");
}
