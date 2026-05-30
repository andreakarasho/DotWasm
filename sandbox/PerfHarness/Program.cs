using System.Diagnostics;
using DotWasm.Encoding;
using DotWasm.Runtime;

// Lightweight DotWasm-only perf harness for the grayscale benchmark.
// Re-instantiates per call (matching BenchmarkDotNet's IterationSetup) but
// times a tight loop so optimization deltas are visible in seconds, not minutes.

const int PixelCount = 128 * 128;
const int ImageBytes = PixelCount * 4;
int iterations = args.Length > 0 ? int.Parse(args[0]) : 2000;

var wasmPath = Path.Combine(AppContext.BaseDirectory, "grayscale_bench.wasm");
if (!File.Exists(wasmPath))
{
    // fall back to sandbox source location
    wasmPath = Path.Combine(
        Path.GetDirectoryName(typeof(Program).Assembly.Location)!,
        "grayscale_bench.wasm");
}
var bytes = File.ReadAllBytes(wasmPath);
var module = WasmEncoding.Decode(bytes);

var source = new byte[ImageBytes];
var rng = new Random(42);
rng.NextBytes(source);
for (var i = 3; i < source.Length; i += 4) source[i] = 255;

// Reuse one instance across calls (the inner work dominates; this isolates
// the interpreter hot loop, which is what the optimizations target).
var store = new WasmStore();
var linker = new WasmLinker(store);
var instance = linker.Instantiate(module);
if (!instance.TryGetExportedMemory("memory", out var memory))
    throw new InvalidOperationException("memory export not found");

void RunOnce()
{
    source.CopyTo(memory.Data[..ImageBytes]);
    instance.Invoke("grayscale_bench", [0, PixelCount], []);
}

// warmup
for (var i = 0; i < Math.Min(200, iterations); i++) RunOnce();

var sw = Stopwatch.StartNew();
for (var i = 0; i < iterations; i++) RunOnce();
sw.Stop();

var usPerCall = sw.Elapsed.TotalMilliseconds * 1000.0 / iterations;
Console.WriteLine($"PERF grayscale: {usPerCall:F2} us/call over {iterations} iters ({sw.Elapsed.TotalSeconds:F2}s)");
