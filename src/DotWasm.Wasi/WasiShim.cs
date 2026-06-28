using System.Diagnostics;
using System.Text;
using DotWasm.Runtime.Component;

namespace DotWasm.Wasi;

/// <summary>Thrown by the host when the guest calls <c>wasi:cli/exit.exit</c>.</summary>
public sealed class WasiExitException(bool failure) : Exception($"WASI exit (failure={failure}).")
{
    public bool Failure { get; } = failure;
}

/// <summary>
/// Host implementations for a useful subset of WASI Preview 2 (0.2.x): standard output/error,
/// clocks, random, environment and exit. Register on a <see cref="ComponentLinker"/> before
/// instantiating a component built against the default (wasm32-wasip2) toolchain.
///
/// Implemented: cli/stdout, cli/stderr (via io/streams output-stream), clocks/wall-clock,
/// clocks/monotonic-clock, random/random + insecure, cli/environment, cli/exit.
/// Other WASI interfaces a component may import (io/error, io/poll, cli/stdin, filesystem,
/// sockets) are left unimplemented and only fault if actually called.
/// </summary>
public sealed class WasiShim
{
    public TextWriter Stdout { get; set; } = Console.Out;
    public TextWriter Stderr { get; set; } = Console.Error;
    public IReadOnlyList<(string Key, string Value)> Environment { get; set; } = [];
    public IReadOnlyList<string> Arguments { get; set; } = [];

    // Random with a fixed default seed is reproducible; replace for real entropy.
    public Random Random { get; set; } = new(0x5eed);

    // output-stream resource reps -> which fd (1 = stdout, 2 = stderr).
    readonly Dictionary<int, int> streams = new();
    int nextStreamRep = 1;

    readonly long startTicks = Stopwatch.GetTimestamp();

    public const string DefaultVersion = "0.2.3";

    public void Register(ComponentLinker linker, string version = DefaultVersion)
    {
        string I(string iface) => $"wasi:{iface}@{version}";

        // --- cli/stdout, cli/stderr ---
        linker.DefineImportFunc(I("cli/stdout"), "get-stdout", _ => [NewStream(1)]);
        linker.DefineImportFunc(I("cli/stderr"), "get-stderr", _ => [NewStream(2)]);

        // --- io/streams: output-stream ---
        var s = I("io/streams");
        linker.DefineImportFunc(s, "[method]output-stream.check-write", _ => [ResultValue.Ok(0x100000UL)]);
        linker.DefineImportFunc(s, "[method]output-stream.write", a => { WriteStream(a); return [ResultValue.Ok()]; });
        linker.DefineImportFunc(s, "[method]output-stream.blocking-write-and-flush", a => { WriteStream(a); return [ResultValue.Ok()]; });
        linker.DefineImportFunc(s, "[method]output-stream.flush", _ => [ResultValue.Ok()]);
        linker.DefineImportFunc(s, "[method]output-stream.blocking-flush", _ => [ResultValue.Ok()]);

        // --- cli/stdin (init grabs it even if unused); input-stream reads return EOF ---
        linker.DefineImportFunc(I("cli/stdin"), "get-stdin", _ => [NewStream(0)]);
        linker.DefineImportFunc(s, "[method]input-stream.read", _ => [ResultValue.Ok(Array.Empty<object?>())]);
        linker.DefineImportFunc(s, "[method]input-stream.blocking-read", _ => [ResultValue.Ok(Array.Empty<object?>())]);
        linker.DefineImportFunc(s, "[method]input-stream.skip", _ => [ResultValue.Ok(0UL)]);
        linker.DefineImportFunc(s, "[method]input-stream.blocking-skip", _ => [ResultValue.Ok(0UL)]);

        // --- filesystem/preopens (std init queries the preopened dirs / cwd) ---
        linker.DefineImportFunc(I("filesystem/preopens"), "get-directories", _ => [Array.Empty<object?>()]);

        // --- clocks ---
        linker.DefineImportFunc(I("clocks/wall-clock"), "now", _ =>
        {
            var now = DateTimeOffset.UtcNow;
            var seconds = (ulong)now.ToUnixTimeSeconds();
            var nanos = (uint)(now.Nanosecond);
            return [new object?[] { seconds, nanos }]; // record datetime { seconds: u64, nanoseconds: u32 }
        });
        linker.DefineImportFunc(I("clocks/wall-clock"), "resolution", _ => [new object?[] { 0UL, 1u }]);
        linker.DefineImportFunc(I("clocks/monotonic-clock"), "now", _ => [MonotonicNanos()]);
        linker.DefineImportFunc(I("clocks/monotonic-clock"), "resolution", _ => [1UL]);

        // --- random ---
        var r = I("random/random");
        linker.DefineImportFunc(r, "get-random-bytes", a => [RandomBytes((ulong)a[0]!)]);
        linker.DefineImportFunc(r, "get-random-u64", _ => [NextU64()]);
        var ri = I("random/insecure");
        linker.DefineImportFunc(ri, "get-insecure-random-bytes", a => [RandomBytes((ulong)a[0]!)]);
        linker.DefineImportFunc(ri, "get-insecure-random-u64", _ => [NextU64()]);

        // --- environment / exit ---
        var env = I("cli/environment");
        linker.DefineImportFunc(env, "get-environment", _ =>
            [Environment.Select(kv => (object?)new object?[] { kv.Key, kv.Value }).ToArray()]);
        linker.DefineImportFunc(env, "get-arguments", _ =>
            [Arguments.Select(arg => (object?)arg).ToArray()]);
        linker.DefineImportFunc(env, "initial-cwd", _ => [OptionValue.None]);
        linker.DefineImportFunc(I("cli/exit"), "exit", a =>
            throw new WasiExitException(a.Length > 0 && a[0] is ResultValue { IsOk: false }));
    }

    object NewStream(int fd)
    {
        var rep = nextStreamRep++;
        streams[rep] = fd;
        return rep; // own<output-stream> as a raw rep (wrapped with identity by the ABI)
    }

    void WriteStream(object?[] a)
    {
        var self = (ResourceValue)a[0]!;
        var contents = (object?[])a[1]!;
        var bytes = new byte[contents.Length];
        for (var i = 0; i < contents.Length; i++)
            bytes[i] = Convert.ToByte(contents[i]);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        (streams.TryGetValue(self.Rep, out var fd) && fd == 2 ? Stderr : Stdout).Write(text);
    }

    ulong MonotonicNanos()
    {
        var elapsed = Stopwatch.GetTimestamp() - startTicks;
        return (ulong)(elapsed * (1_000_000_000.0 / Stopwatch.Frequency));
    }

    object?[] RandomBytes(ulong n)
    {
        var bytes = new byte[(int)n];
        Random.NextBytes(bytes);
        var result = new object?[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) result[i] = bytes[i];
        return result;
    }

    ulong NextU64()
    {
        Span<byte> b = stackalloc byte[8];
        Random.NextBytes(b);
        return BitConverter.ToUInt64(b);
    }
}
