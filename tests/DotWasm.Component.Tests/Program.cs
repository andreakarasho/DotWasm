using DotWasm.Encoding;
using DotWasm.Models.Component;
using DotWasm.Runtime;
using DotWasm.Runtime.Component;

// Modes:
//   dump <file.wasm>   decode a component and print its definitions
//   (no args)          run the test suite
if (args.Length >= 2 && args[0] == "dump")
{
    var bytes = File.ReadAllBytes(args[1]);
    Console.WriteLine($"IsComponent: {ComponentEncoding.IsComponent(bytes)}");
    Dump.Component(ComponentEncoding.Decode(bytes), "  ");
    return 0;
}

return Tests.Run();

static class Tests
{
    static int passed;
    static int failed;

    static string FixturesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "fixtures")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            throw new DirectoryNotFoundException("tests/fixtures not found.");
        return Path.Combine(dir, "tests", "fixtures", "component");
    }

    static ComponentInstance Instantiate(string fixture)
    {
        var path = Path.Combine(FixturesDir(), fixture);
        var component = ComponentEncoding.Decode(File.ReadAllBytes(path));
        return new ComponentLinker(new WasmStore()).Instantiate(component);
    }

    static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { passed++; Console.WriteLine($"  PASS  {name}"); }
        else { failed++; Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : $"  -- {detail}")}"); }
    }

    static void CheckEq(string name, object? expected, object? actual) =>
        Check(name, Equals(expected, actual), $"expected {expected}, got {actual}");

    public static int Run()
    {
        try { Primitives(); } catch (Exception e) { Check("primitives (threw)", false, e.ToString()); }
        try { Strings(); } catch (Exception e) { Check("strings (threw)", false, e.ToString()); }
        try { Aggregates(); } catch (Exception e) { Check("aggregates (threw)", false, e.ToString()); }
        try { Resources(); } catch (Exception e) { Check("resources (threw)", false, e.ToString()); }

        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Primitives()
    {
        Console.WriteLine("[add.wasm]");
        var add = Instantiate("add.wasm");
        var r = add.Invoke("add", 2, 3);
        CheckEq("add(2,3) == 5", 5, r[0]);
        CheckEq("add(-1,1) == 0", 0, add.Invoke("add", -1, 1)[0]);
        CheckEq("add(2147483647,1) wraps", unchecked(2147483647 + 1), add.Invoke("add", 2147483647, 1)[0]);

        Console.WriteLine("[types.wasm]");
        var types = Instantiate("types.wasm");
        // g: (a: s32) -> u32, lifted from core identity f(i32)->i32
        var g = types.Invoke("g", 5);
        CheckEq("g(5) == 5u", 5u, g[0]);
        CheckEq("g(5) type is uint", typeof(uint), g[0]?.GetType());
    }

    static void Strings()
    {
        Console.WriteLine("[strmem.wasm]");
        var m = Instantiate("strmem.wasm");
        // greeting() -> string  (indirect single result: core returns ptr to {ptr,len})
        CheckEq("greeting() == hello", "hello", m.Invoke("greeting")[0]);
        // strlen(s) -> u32  (string lowered via realloc, UTF-8 byte length)
        CheckEq("strlen(hello) == 5", 5u, m.Invoke("strlen", "hello")[0]);
        CheckEq("strlen(empty) == 0", 0u, m.Invoke("strlen", "")[0]);
        CheckEq("strlen(héllo) == 6 (utf8)", 6u, m.Invoke("strlen", "héllo")[0]);
        CheckEq("strlen(€) == 3 (utf8)", 3u, m.Invoke("strlen", "€")[0]);
    }

    static ComponentSubInstance Ops()
    {
        var inst = Instantiate("ops.wasm");
        return inst.GetInstance("test:comp/ops")
            ?? throw new InvalidOperationException("no test:comp/ops export");
    }

    static void Aggregates()
    {
        Console.WriteLine("[ops.wasm  (real cargo-component)]");
        var ops = Ops();

        // record: add-point({2,3},{10,20}) -> {12,23}
        var p = (object?[])ops.Invoke("add-point", new object?[] { 2, 3 }, new object?[] { 10, 20 })[0]!;
        Check("add-point -> {12,23}", (int)p[0]! == 12 && (int)p[1]! == 23, $"{p[0]},{p[1]}");

        // list: sum-list([1,2,3,4]) -> 10
        CheckEq("sum-list([1..4]) == 10", 10, ops.Invoke("sum-list", new object?[] { new object?[] { 1, 2, 3, 4 } })[0]);
        // list returned: make-list(3) -> [0,1,2]
        var lst = (object?[])ops.Invoke("make-list", 3u)[0]!;
        Check("make-list(3) == [0,1,2]", lst.Length == 3 && (uint)lst[0]! == 0 && (uint)lst[2]! == 2, string.Join(",", lst));

        // variant: describe
        CheckEq("describe(circle 2.5)", "circle 2.5", ops.Invoke("describe", new VariantValue(0, 2.5))[0]);
        CheckEq("describe(rect{1,2})", "rect 1 2", ops.Invoke("describe", new VariantValue(1, new object?[] { 1, 2 }))[0]);
        CheckEq("describe(unit)", "unit", ops.Invoke("describe", new VariantValue(2))[0]);

        // result
        var ok = (ResultValue)ops.Invoke("divide", 10, 2)[0]!;
        Check("divide(10,2) == Ok(5)", ok.IsOk && (int)ok.Value! == 5, $"{ok}");
        var err = (ResultValue)ops.Invoke("divide", 1, 0)[0]!;
        Check("divide(1,0) == Err(\"div by zero\")", !err.IsOk && (string)err.Value! == "div by zero", $"{err}");

        // option
        var some = (OptionValue)ops.Invoke("maybe-inc", OptionValue.Some(5))[0]!;
        Check("maybe-inc(Some 5) == Some 6", some.HasValue && (int)some.Value! == 6, $"{some}");
        var none = (OptionValue)ops.Invoke("maybe-inc", OptionValue.None)[0]!;
        Check("maybe-inc(None) == None", !none.HasValue);

        // enum
        CheckEq("next-color(red=0) == green=1", 1u, ops.Invoke("next-color", 0u)[0]);
        CheckEq("next-color(blue=2) == red=0", 0u, ops.Invoke("next-color", 2u)[0]);

        // flags: read|write -> 0b011 = 3
        CheckEq("perm-bits(read|write) == 3", 3u, ops.Invoke("perm-bits", new FlagsValue([true, true, false]))[0]);

        // tuple: swap((7, 2.5)) -> (2.5, 7)
        var sw = (object?[])ops.Invoke("swap", new object?[] { new object?[] { 7, 2.5 } })[0]!;
        Check("swap((7,2.5)) == (2.5,7)", (double)sw[0]! == 2.5 && (int)sw[1]! == 7, $"{sw[0]},{sw[1]}");

        // indirect params: 17 i32 params > MAX_FLAT_PARAMS(16) -> spilled to memory via realloc
        var bigArgs = new object?[17];
        for (var i = 0; i < 17; i++) bigArgs[i] = i + 1;
        CheckEq("big-sum(1..17) == 153 (indirect params)", 153, ops.Invoke("big-sum", bigArgs)[0]);

        // f32 + u64 (i64 flat path)
        CheckEq("f32-add(1.5,2.25) == 3.75", 3.75f, ops.Invoke("f32-add", 1.5f, 2.25f)[0]);
        CheckEq("u64-add(0xFFFFFFFF,1) == 2^32", 4294967296UL, ops.Invoke("u64-add", 0xFFFFFFFFUL, 1UL)[0]);
        CheckEq("u64-add big", ulong.MaxValue - 1, ops.Invoke("u64-add", ulong.MaxValue - 2, 1UL)[0]);
    }

    static void Resources()
    {
        Console.WriteLine("[ops.wasm  resources]");
        var ops = Ops();

        var counter = ops.Invoke("[constructor]counter", 10)[0];
        Check("constructor returns ResourceValue", counter is ResourceValue);
        CheckEq("increment() == 11", 11, ops.Invoke("[method]counter.increment", counter)[0]);
        CheckEq("increment() == 12", 12, ops.Invoke("[method]counter.increment", counter)[0]);
        CheckEq("get() == 12", 12, ops.Invoke("[method]counter.get", counter)[0]);

        // a second independent counter
        var c2 = ops.Invoke("[constructor]counter", 100)[0];
        CheckEq("c2.increment() == 101", 101, ops.Invoke("[method]counter.increment", c2)[0]);
        CheckEq("c1.get() still 12", 12, ops.Invoke("[method]counter.get", counter)[0]);
    }
}

static class Dump
{
    public static void Component(Component component, string indent)
    {
        Console.WriteLine($"version={component.Version} definitions={component.Definitions.Length}");
        foreach (var def in component.Definitions)
            Console.WriteLine($"{indent}{def.GetType().Name}: {def}");
    }
}
