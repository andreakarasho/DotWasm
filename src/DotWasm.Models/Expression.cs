using System.Collections.Immutable;

namespace DotWasm.Models;

public sealed record Expression
{
    public required ImmutableArray<Instruction> Instructions { get; init; }

    private byte[]? _opCodes;

    /// <summary>
    /// A contiguous array of opcodes parallel to <see cref="Instructions"/>.
    /// Computed once and cached so the interpreter dispatch loop can read the
    /// opcode for each instruction from a cache-friendly contiguous array
    /// instead of dereferencing each polymorphic heap-allocated instruction
    /// record just to branch on its opcode.
    /// </summary>
    public byte[] OpCodes
    {
        get
        {
            var cached = _opCodes;
            if (cached is not null)
            {
                return cached;
            }

            var instructions = Instructions;
            var opCodes = new byte[instructions.Length];
            for (var i = 0; i < instructions.Length; i++)
            {
                opCodes[i] = instructions[i].OpCode;
            }

            _opCodes = opCodes;
            return opCodes;
        }
    }
}
