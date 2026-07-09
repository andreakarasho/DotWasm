namespace DotWasm.Bindgen;

// Minimal model of the WIT subset the generator understands.

public abstract record WitType;

public sealed record WitPrim(string Kind) : WitType;          // bool,s8..u64,f32,f64,char,string

public sealed record WitNamed(string Name) : WitType;         // record/variant/enum/flags/alias ref

public sealed record WitList(WitType Element) : WitType;

public sealed record WitOption(WitType Inner) : WitType;

public sealed record WitResult(WitType? Ok, WitType? Err) : WitType;

public sealed record WitTuple(List<WitType> Items) : WitType;

public sealed record WitHandle(string Resource, bool Owned) : WitType;  // own<R> / borrow<R>

// ---- type definitions ----

public abstract record WitTypeDef(string Name);

public sealed record WitRecord(string Name, List<(string Field, WitType Type)> Fields) : WitTypeDef(Name);

public sealed record WitVariant(string Name, List<(string Case, WitType? Type)> Cases) : WitTypeDef(Name);

public sealed record WitEnum(string Name, List<string> Cases) : WitTypeDef(Name);

public sealed record WitFlags(string Name, List<string> Names) : WitTypeDef(Name);

public sealed record WitAlias(string Name, WitType Target) : WitTypeDef(Name);

public sealed record WitResourceDef(string Name, List<WitFunc> Methods) : WitTypeDef(Name);

// ---- functions ----

public sealed record WitFunc(
    string Name,
    List<(string Name, WitType Type)> Params,
    WitType? Result,
    WitFuncKind Kind,
    string? ResourceName = null
);

public enum WitFuncKind { Free, Constructor, Method, Static }

// ---- interface ----

public sealed record WitInterface(
    string Package,
    string Name,
    List<WitTypeDef> Types,
    List<WitFunc> Funcs
)
{
    /// <summary>Fully-qualified export name, e.g. "test:comp/ops".</summary>
    public string ExportName => $"{Package}/{Name}";
}
