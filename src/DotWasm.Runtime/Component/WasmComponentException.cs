using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DotWasm.Runtime.Component;

/// <summary>Raised for component-model decode/link/instantiation errors (not runtime traps).</summary>
public sealed class WasmComponentException(string message) : Exception(message)
{
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void Throw(string message) => throw new WasmComponentException(message);
}
