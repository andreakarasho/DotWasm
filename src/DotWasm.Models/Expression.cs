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

    private long[]? _operands;

    /// <summary>
    /// A contiguous array of primary immediates parallel to <see cref="Instructions"/>.
    /// For the hottest single-immediate opcodes (local/global index, constants,
    /// memory offset+index, branch label, call target) the interpreter can read the
    /// immediate from this cache-friendly array instead of dereferencing the scattered
    /// polymorphic heap-allocated instruction record. Constants are stored as their
    /// <c>WasmValue.Bits</c> representation; memory ops pack <c>offset | (memIndex &lt;&lt; 32)</c>.
    /// Opcodes that need more than one immediate (control flow, br_table, etc.) store 0
    /// here and continue to read from the record.
    /// </summary>
    public long[] Operands
    {
        get
        {
            var cached = _operands;
            if (cached is not null)
            {
                return cached;
            }

            var instructions = Instructions;
            var operands = new long[instructions.Length];
            for (var i = 0; i < instructions.Length; i++)
            {
                operands[i] = ComputeOperand(instructions[i]);
            }

            _operands = operands;
            return operands;
        }
    }

    static long PackMemArg(uint memoryIndex, uint offset) =>
        unchecked((long)((ulong)offset | ((ulong)memoryIndex << 32)));

    static long ComputeOperand(Instruction instr) =>
        instr switch
        {
            LocalGetInstruction i => i.LocalIndex,
            LocalSetInstruction i => i.LocalIndex,
            LocalTeeInstruction i => i.LocalIndex,
            GlobalGetInstruction i => i.GlobalIndex,
            GlobalSetInstruction i => i.GlobalIndex,
            BrInstruction i => i.LabelIndex,
            BrIfInstruction i => i.LabelIndex,
            CallInstruction i => i.FunctionIndex,
            // Constants stored as WasmValue.Bits (reconstructed via WasmValue.FromRaw).
            I32ConstInstruction i => i.Value,
            I64ConstInstruction i => i.Value,
            F32ConstInstruction i => unchecked((long)(ulong)BitConverter.SingleToUInt32Bits(i.Value)),
            F64ConstInstruction i => BitConverter.DoubleToInt64Bits(i.Value),
            // Memory load/store: pack offset (low 32) and memory index (high 32).
            I32LoadInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I64LoadInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            F32LoadInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            F64LoadInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I32Load8SInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I32Load8UInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I32Load16SInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I32Load16UInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I64Load8SInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I64Load8UInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I64Load16SInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I64Load16UInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I64Load32SInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I64Load32UInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I32StoreInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I64StoreInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            F32StoreInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            F64StoreInstruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I32Store8Instruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I32Store16Instruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I64Store8Instruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I64Store16Instruction i => PackMemArg(i.MemoryIndex, i.Offset),
            I64Store32Instruction i => PackMemArg(i.MemoryIndex, i.Offset),
            _ => 0,
        };
}
