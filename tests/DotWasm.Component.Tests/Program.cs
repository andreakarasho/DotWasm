using DotWasm.Encoding;
using DotWasm.Models.Component;
using DotWasm.Runtime;
using DotWasm.Runtime.Component;
using Gen = Ops.Generated;

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

if (args.Length >= 2 && args[0] == "inst")
{
    var component = ComponentEncoding.Decode(File.ReadAllBytes(args[1]));
    try
    {
        var inst = new ComponentLinker(new WasmStore()).Instantiate(component);
        Console.WriteLine("INSTANTIATED. exports:");
        foreach (var (name, val) in inst.Exports)
        {
            Console.WriteLine($"  {name}: {val.GetType().Name}");
            if (val is ComponentSubInstance sub)
                foreach (var f in sub.Funcs.Keys) Console.WriteLine($"      fn {f}");
        }
        return 0;
    }
    catch (Exception e)
    {
        Console.WriteLine($"INSTANTIATE FAILED: {e}");
        return 1;
    }
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
        try { CrossInterfaceUse(); } catch (Exception e) { Check("cross-interface use (threw)", false, e.ToString()); }
        try { InterfaceImports(); } catch (Exception e) { Check("interface imports (threw)", false, e.ToString()); }
        try { CrossInterfaceResource(); } catch (Exception e) { Check("cross-interface resource (threw)", false, e.ToString()); }
        try { HostImplementedResource(); } catch (Exception e) { Check("host-implemented resource (threw)", false, e.ToString()); }
        try { ExportMintedHostResource(); } catch (Exception e) { Check("export-minted host resource (threw)", false, e.ToString()); }
        try { TypedBindings(); } catch (Exception e) { Check("typed bindings (threw)", false, e.ToString()); }
        try { Wasi(); } catch (Exception e) { Check("wasi (threw)", false, e.ToString()); }
        try { Allocations(); } catch (Exception e) { Check("allocations (threw)", false, e.ToString()); }

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

    static void CrossInterfaceUse()
    {
        Console.WriteLine("[multi.wasm  (tier 1: interface `use` shared types)]");
        var inst = Instantiate("multi.wasm");
        var math = inst.GetInstance("test:multi/math")!;
        // add(vec2{1,2}, vec2{3,4}) -> vec2{4,6}   (vec2 comes from a `use`d interface)
        var v = (object?[])math.Invoke("add", new object?[] { 1, 2 }, new object?[] { 3, 4 })[0]!;
        Check("math.add -> {4,6}", (int)v[0]! == 4 && (int)v[1]! == 6, $"{v[0]},{v[1]}");
        CheckEq("math.len2({3,4}) == 25", 25, math.Invoke("len2", (object?)new object?[] { 3, 4 })[0]);
    }

    static void InterfaceImports()
    {
        Console.WriteLine("[imp2.wasm  (tier 2: host-implemented interface import)]");
        var path = Path.Combine(FixturesDir(), "imp2.wasm");
        var component = ComponentEncoding.Decode(File.ReadAllBytes(path));
        var linker = new ComponentLinker(new WasmStore());
        linker.DefineImportFunc("test:imp/host-api", "add", a => [(int)a[0]! + (int)a[1]!]);
        linker.DefineImportFunc("test:imp/host-api", "greet", a => [$"hi {(string)a[1 - 1]!}"]);
        linker.DefineImportFunc("test:imp/host-api", "record-sum", a =>
        {
            var kv = (object?[])a[0]!; // record kv { key: string, value: s32 }
            return [(int)kv[1]!];
        });
        var inst = linker.Instantiate(component);
        // run() = add(40,2) + record-sum({key,100}) = 42 + 100 = 142
        CheckEq("run() == 142 (calls imported add + record-sum)", 142, inst.Invoke("run")[0]);
        // run-str() = greet("world") = "hi world"
        CheckEq("run-str() == \"hi world\"", "hi world", inst.Invoke("run-str")[0]);
    }

    static void CrossInterfaceResource()
    {
        Console.WriteLine("[res2.wasm  (cross-interface resource: node used across entity+graph)]");
        var inst = Instantiate("res2.wasm");
        var entity = inst.GetInstance("test:res/entity")!;
        var graph = inst.GetInstance("test:res/graph")!;

        // node defined in `entity`, used (borrow) by `graph.combine` -> identities must match
        var n1 = entity.Invoke("[constructor]node", 5)[0];
        var n2 = entity.Invoke("[constructor]node", 7)[0];
        Check("node is ResourceValue", n1 is ResourceValue);
        CheckEq("entity node.value() == 5", 5, entity.Invoke("[method]node.value", n1)[0]);
        // pass entity-made nodes to graph.combine (borrow across the interface boundary)
        CheckEq("graph.combine(n1,n2) == 12", 12, graph.Invoke("combine", n1, n2)[0]);
    }

    static void HostImplementedResource()
    {
        Console.WriteLine("[resimp.wasm  (host implements an imported resource)]");
        var path = Path.Combine(FixturesDir(), "resimp.wasm");
        var component = ComponentEncoding.Decode(File.ReadAllBytes(path));
        var linker = new ComponentLinker(new WasmStore());

        // Host implements the `bucket` resource: rep is a key into host state.
        var buckets = new Dictionary<int, int>();
        var next = 1;
        linker.DefineImportFunc("test:resimp/store", "[constructor]bucket", a =>
        {
            var rep = next++;
            buckets[rep] = (int)a[0]!; // seed
            return [rep];              // own<bucket> as a raw rep (wrapped with identity by the ABI)
        });
        linker.DefineImportFunc("test:resimp/store", "[method]bucket.add", a =>
        {
            var self = (ResourceValue)a[0]!; // borrow<bucket>
            buckets[self.Rep] += (int)a[1]!;
            return [buckets[self.Rep]];
        });

        var inst = linker.Instantiate(component);
        // run() = bucket(10); add(5)=15; add(2)=17 -> 32
        CheckEq("run() == 32 (guest uses host-implemented resource)", 32, inst.Invoke("run")[0]);
    }

    static void ExportMintedHostResource()
    {
        // Variant A: host MINTS an own<app> at the export-param site, guest calls a host
        // method on it (self: borrow<app>). The export-param resource type and the method-self
        // resource type must resolve to ONE identity, else borrow lift traps.
        Console.WriteLine("[rmping.wasm  (export-minted host resource, self-borrow into host method)]");
        var path = Path.Combine(FixturesDir(), "rmping.wasm");
        var component = ComponentEncoding.Decode(File.ReadAllBytes(path));
        var linker = new ComponentLinker(new WasmStore());

        var pinged = false;
        linker.DefineImportFunc("test:rmping/app", "[method]app.ping", a =>
        {
            var self = (ResourceValue)a[0]!; // borrow<app>
            pinged = self.Rep == 100;
            return [];
        });

        var inst = linker.Instantiate(component);
        inst.Invoke("setup", 100); // 100 = own<app> rep, host-minted at the export param
        Check("rmping setup pinged host app (rep 100)", pinged);

        // Full shape: export-minted own<app> + a second host resource `system` minted by a host
        // constructor, passed back as list<borrow<system>> into a host method on `app`.
        Console.WriteLine("[resmint.wasm  (export-minted app + list<borrow<system>> into host method)]");
        var path2 = Path.Combine(FixturesDir(), "resmint.wasm");
        var linker2 = new ComponentLinker(new WasmStore());
        var nextSys = 1;
        var gotSelf = 0;
        var gotSystems = -1;
        linker2.DefineImportFunc("test:resmint/app", "[constructor]system", _ => [nextSys++]);
        linker2.DefineImportFunc("test:resmint/app", "[method]app.add-systems", a =>
        {
            gotSelf = ((ResourceValue)a[0]!).Rep;          // borrow<app>
            gotSystems = ((object?[])a[1]!).Length;        // list<borrow<system>>
            return [];
        });
        var inst2 = linker2.Instantiate(ComponentEncoding.Decode(File.ReadAllBytes(path2)));
        inst2.Invoke("setup", 100);
        Check("resmint add-systems got self app (rep 100)", gotSelf == 100, $"self={gotSelf}");
        Check("resmint add-systems got 1 system borrow", gotSystems == 1, $"count={gotSystems}");
    }

    static void TypedBindings()
    {
        Console.WriteLine("[ops.wasm  (generated typed bindings)]");
        var ops = new Gen.Ops(Instantiate("ops.wasm"));

        var p = ops.AddPoint(new Gen.Point(2, 3), new Gen.Point(10, 20));
        Check("typed AddPoint -> (12,23)", p is { X: 12, Y: 23 }, $"{p}");
        CheckEq("typed SumList == 10", 10, ops.SumList([1, 2, 3, 4]));
        Check("typed MakeList(3)", ops.MakeList(3).SequenceEqual([0u, 1u, 2u]));
        CheckEq("typed Describe(Circle 2.5)", "circle 2.5", ops.Describe(new Gen.ShapeCircle(2.5)));
        CheckEq("typed Describe(Rect)", "rect 1 2", ops.Describe(new Gen.ShapeRect(new Gen.Point(1, 2))));

        var ok = ops.Divide(10, 2);
        Check("typed Divide ok", ok is { IsOk: true, Ok: 5 }, $"{ok}");
        var err = ops.Divide(1, 0);
        Check("typed Divide err", err is { IsOk: false, Err: "div by zero" }, $"{err}");

        CheckEq("typed MaybeInc(5) == 6", 6, ops.MaybeInc(5));
        Check("typed MaybeInc(null) == null", ops.MaybeInc(null) is null);
        CheckEq("typed NextColor(Red) == Green", Gen.Color.Green, ops.NextColor(Gen.Color.Red));
        CheckEq("typed PermBits(Read|Write) == 3", 3u, ops.PermBits(Gen.Perms.Read | Gen.Perms.Write));
        CheckEq("typed Swap((7,2.5)).Item1 == 2.5", 2.5, ops.Swap((7, 2.5)).Item1);

        var counter = ops.NewCounter(10);
        CheckEq("typed counter.Increment() == 11", 11, counter.Increment());
        CheckEq("typed counter.Get() == 11", 11, counter.Get());
    }

    static void Wasi()
    {
        Console.WriteLine("[wasiapp.wasm  (real std wasm32-wasip2 + WASI shim)]");
        var path = Path.Combine(FixturesDir(), "wasiapp.wasm");
        var component = ComponentEncoding.Decode(File.ReadAllBytes(path));
        var linker = new ComponentLinker(new WasmStore());

        var shim = new DotWasm.Wasi.WasiShim();
        var outBuf = new StringWriter();
        var errBuf = new StringWriter();
        shim.Stdout = outBuf;
        shim.Stderr = errBuf;
        shim.Register(linker);

        var inst = linker.Instantiate(component);
        // run() prints to stdout/stderr (WASI streams) and returns the wall-clock unix seconds.
        var now = (ulong)inst.Invoke("run")[0]!;

        Check("wasi stdout captured", outBuf.ToString().Contains("hello from wasi stdout"), $"out=[{outBuf}]");
        Check("wasi stderr captured", errBuf.ToString().Contains("hello from wasi stderr"), $"err=[{errBuf}]");
        var nowRef = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Check("wasi wall-clock plausible", now > 1_700_000_000UL && now <= nowRef + 5, $"now={now}");
    }

    static void Allocations()
    {
        Console.WriteLine("[ops.wasm  (allocation: zero-box typed vs dynamic)]");
        var ops = new Gen.Ops(Instantiate("ops.wasm"));
        var dyn = Ops();

        // warm up both paths (JIT + ArrayPool buckets)
        for (var i = 0; i < 500; i++) { ops.NextColor(Gen.Color.Red); dyn.Invoke("next-color", 0u); }

        const int N = 5000;
        var b0 = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < N; i++) ops.NextColor(Gen.Color.Red);   // pure-register zero-box call
        var typed = GC.GetAllocatedBytesForCurrentThread() - b0;

        var b1 = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < N; i++) dyn.Invoke("next-color", 0u);   // dynamic object?[] path
        var dynamic = GC.GetAllocatedBytesForCurrentThread() - b1;

        // The typed path's per-call allocation is fixed interpreter/pool overhead (no boxing);
        // the dynamic path additionally boxes args/results into object?[] every call.
        Check($"typed low/bounded alloc ({typed / (double)N:F0} B/call)", typed / N < 256);
        Check($"dynamic allocates >=4x typed ({dynamic / (double)N:F0} vs {typed / (double)N:F0} B/call)", dynamic > typed * 4);
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
