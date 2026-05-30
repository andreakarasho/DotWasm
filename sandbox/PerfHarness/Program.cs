using System.Diagnostics;
using DotWasm.Encoding;
using DotWasm.Runtime;

// Lightweight DotWasm-only perf harness.
//   args: [bench] [iterations]
//   bench = "grayscale" (default) | "collatz"

string bench = args.Length > 0 ? args[0] : "grayscale";
int iterations = args.Length > 1 ? int.Parse(args[1]) : 2000;

string ResolveWasm(string name)
{
    var p = Path.Combine(AppContext.BaseDirectory, name);
    return File.Exists(p)
        ? p
        : Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, name);
}

Action runOnce;
var store = new WasmStore();
var linker = new WasmLinker(store);

if (bench == "collatz")
{
    var module = WasmEncoding.Decode(File.ReadAllBytes(ResolveWasm("collatz_bench.wasm")));
    var instance = linker.Instantiate(module);
    runOnce = () => instance.Invoke("collatz_bench", [10000], []);
}
else
{
    const int PixelCount = 128 * 128;
    const int ImageBytes = PixelCount * 4;
    var module = WasmEncoding.Decode(File.ReadAllBytes(ResolveWasm("grayscale_bench.wasm")));
    var source = new byte[ImageBytes];
    var rng = new Random(42);
    rng.NextBytes(source);
    for (var i = 3; i < source.Length; i += 4) source[i] = 255;
    var instance = linker.Instantiate(module);
    if (!instance.TryGetExportedMemory("memory", out var memory))
        throw new InvalidOperationException("memory export not found");
    runOnce = () =>
    {
        source.CopyTo(memory.Data[..ImageBytes]);
        instance.Invoke("grayscale_bench", [0, PixelCount], []);
    };
}

for (var i = 0; i < Math.Min(200, iterations); i++) runOnce();

var sw = Stopwatch.StartNew();
for (var i = 0; i < iterations; i++) runOnce();
sw.Stop();

var usPerCall = sw.Elapsed.TotalMilliseconds * 1000.0 / iterations;
Console.WriteLine($"PERF {bench}: {usPerCall:F2} us/call over {iterations} iters ({sw.Elapsed.TotalSeconds:F2}s)");
