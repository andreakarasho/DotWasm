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
    ///
    /// During construction, adjacent instruction pairs that match a known
    /// superinstruction pattern are FUSED: the first slot's opcode is replaced
    /// with a synthetic fused opcode (see <see cref="WasmOpCodes"/>), and the
    /// interpreter's fused handler does the work of both instructions and skips
    /// the second slot via the instruction pointer. The array length is never
    /// changed, so every precomputed control-flow index (block/loop/if end and
    /// branch targets) stays valid — and because Wasm branch targets are always
    /// structured-control boundaries, the (non-control, mid-body) second
    /// instruction of a fused pair can never be a jump target.
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

            // Fusion pass: rewrite the first slot of each fusible group. Longer groups
            // are tried first so they take precedence over their sub-patterns.
            for (var i = 0; i + 1 < instructions.Length; )
            {
                // 3-op: local.get X ; i32.const C ; i32.<binop>
                if (
                    i + 2 < instructions.Length
                    && instructions[i] is LocalGetInstruction
                    && instructions[i + 1] is I32ConstInstruction
                    && TryFuseLocalGetConstBinOp(instructions[i + 2].OpCode, out var fused3)
                )
                {
                    opCodes[i] = fused3;
                    i += 3;
                    continue;
                }

                // 2-op: i32.const C ; i32.<binop>
                if (
                    instructions[i] is I32ConstInstruction
                    && TryFuseI32ConstBinOp(instructions[i + 1].OpCode, out var fused2)
                )
                {
                    opCodes[i] = fused2;
                    i += 2;
                    continue;
                }

                i++;
            }

            _opCodes = opCodes;
            return opCodes;
        }
    }

    static bool TryFuseI32ConstBinOp(byte binOp, out byte fused)
    {
        fused = binOp switch
        {
            WasmOpCodes.I32Add => WasmOpCodes.FusedI32ConstAdd,
            WasmOpCodes.I32Sub => WasmOpCodes.FusedI32ConstSub,
            WasmOpCodes.I32Mul => WasmOpCodes.FusedI32ConstMul,
            WasmOpCodes.I32And => WasmOpCodes.FusedI32ConstAnd,
            WasmOpCodes.I32Or => WasmOpCodes.FusedI32ConstOr,
            WasmOpCodes.I32Xor => WasmOpCodes.FusedI32ConstXor,
            WasmOpCodes.I32Shl => WasmOpCodes.FusedI32ConstShl,
            WasmOpCodes.I32ShrS => WasmOpCodes.FusedI32ConstShrS,
            WasmOpCodes.I32ShrU => WasmOpCodes.FusedI32ConstShrU,
            _ => 0,
        };
        return fused != 0;
    }

    static bool TryFuseLocalGetConstBinOp(byte binOp, out byte fused)
    {
        fused = binOp switch
        {
            WasmOpCodes.I32Add => WasmOpCodes.FusedLocalGetConstAdd,
            WasmOpCodes.I32Sub => WasmOpCodes.FusedLocalGetConstSub,
            WasmOpCodes.I32Mul => WasmOpCodes.FusedLocalGetConstMul,
            WasmOpCodes.I32And => WasmOpCodes.FusedLocalGetConstAnd,
            WasmOpCodes.I32Or => WasmOpCodes.FusedLocalGetConstOr,
            WasmOpCodes.I32Xor => WasmOpCodes.FusedLocalGetConstXor,
            WasmOpCodes.I32Shl => WasmOpCodes.FusedLocalGetConstShl,
            WasmOpCodes.I32ShrS => WasmOpCodes.FusedLocalGetConstShrS,
            WasmOpCodes.I32ShrU => WasmOpCodes.FusedLocalGetConstShrU,
            _ => 0,
        };
        return fused != 0;
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
