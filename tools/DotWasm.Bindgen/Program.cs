using System.Diagnostics;
using DotWasm.Bindgen;

// dotwasm-bindgen <component.wasm | interface.wit> <namespace> [output.cs]
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotwasm-bindgen <component.wasm|.wit> <namespace> [output.cs]");
    return 1;
}

var input = args[0];
var ns = args[1];
var output = args.Length > 2 ? args[2] : Path.ChangeExtension(input, ".g.cs");

string wit;
if (input.EndsWith(".wit", StringComparison.OrdinalIgnoreCase))
{
    wit = File.ReadAllText(input);
}
else
{
    // extract WIT from the component via wasm-tools
    var psi = new ProcessStartInfo("wasm-tools", $"component wit \"{input}\"")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start wasm-tools");
    wit = p.StandardOutput.ReadToEnd();
    var err = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        Console.Error.WriteLine($"wasm-tools failed: {err}");
        return 1;
    }
}

var parser = WitParser.Parse(wit);
var emitter = new CSharpEmitter(ns, parser.Interfaces);
var code = emitter.Emit(parser.Interfaces, parser.ExportedInterfaces);
File.WriteAllText(output, code);
Console.WriteLine($"wrote {output} ({parser.Interfaces.Count} interface(s), {parser.Interfaces.Sum(i => i.Types.Count)} type(s))");
return 0;
