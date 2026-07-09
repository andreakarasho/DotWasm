using System.Text;
using DotWasm.Models;

namespace SourceGenPoc;

/// <summary>
/// Proof-of-concept build-time transpiler: Wasm module -> native C# source.
/// Handles the i32 + memory + structured-control subset used by the grayscale and
/// collatz benchmark functions. Stack-based Wasm is lowered to SSA temporaries; Wasm
/// structured control flow (block/loop/if/else/br/br_if) is lowered to C# labels + goto.
/// No runtime code generation — the emitted C# is compiled normally (NativeAOT-safe).
/// </summary>
public static class Transpiler
{
    public static string Transpile(WasmModule module, string exportName, string methodName)
    {
        var export = module.Exports.First(e => e.Name == exportName && e.Kind == ImportExportKind.Function);
        var func = module.Functions[(int)export.Index];
        if (module.DefinedTypes[(int)func.TypeIndex].CompositeType is not FuncType funcType)
            throw new NotSupportedException("PoC transpiler: function type is not a FuncType");

        var paramTypes = funcType.Parameters;
        var localTypes = func.Locals;
        int paramCount = paramTypes.Length;

        // Method signature: (byte[] mem, int p0, int p1, ...)
        var sig = new StringBuilder($"    public static void {methodName}(byte[] mem");
        for (int i = 0; i < paramCount; i++)
        {
            RequireI32(paramTypes[i], $"param {i}");
            sig.Append($", int p{i}");
        }
        sig.Append(')');

        var body = new StringBuilder();
        // Locals: params copied into l0.., declared locals zero-initialised.
        for (int i = 0; i < paramCount; i++)
            body.AppendLine($"        int l{i} = p{i};");
        for (int i = 0; i < localTypes.Length; i++)
        {
            RequireI32(localTypes[i], $"local {paramCount + i}");
            body.AppendLine($"        int l{paramCount + i} = 0;");
        }

        var stack = new List<string>();
        var ctrl = new List<Frame>();
        int tmp = 0, lbl = 0;

        string Pop() { var v = stack[^1]; stack.RemoveAt(stack.Count - 1); return v; }
        void Push(string v) => stack.Add(v);
        // Emit a binary op result into a fresh temp, push the temp.
        void Bin(string fmt)
        {
            var b = Pop();
            var a = Pop();
            var t = $"t{tmp++}";
            body.AppendLine($"        int {t} = {string.Format(fmt, a, b)};");
            Push(t);
        }
        void Un(string fmt)
        {
            var a = Pop();
            var t = $"t{tmp++}";
            body.AppendLine($"        int {t} = {string.Format(fmt, a)};");
            Push(t);
        }

        foreach (var instr in func.Body.Instructions)
        {
            switch (instr)
            {
                // ── control ──
                case BlockInstruction:
                    ctrl.Add(new Frame($"B{lbl++}", FrameKind.Block));
                    break;
                case LoopInstruction:
                {
                    var f = new Frame($"L{lbl++}", FrameKind.Loop);
                    body.AppendLine($"        {f.Label}: ;");
                    ctrl.Add(f);
                    break;
                }
                case IfInstruction:
                {
                    var cond = Pop();
                    body.AppendLine($"        if (({cond}) != 0) {{");
                    ctrl.Add(new Frame($"I{lbl++}", FrameKind.If));
                    break;
                }
                case ElseInstruction:
                    body.AppendLine("        } else {");
                    break;
                case EndInstruction:
                {
                    if (ctrl.Count == 0) break; // implicit function-level end
                    var f = ctrl[^1];
                    ctrl.RemoveAt(ctrl.Count - 1);
                    if (f.Kind == FrameKind.Block) body.AppendLine($"        {f.Label}: ;");
                    else if (f.Kind == FrameKind.If) body.AppendLine("        }");
                    // loop: start label already emitted; nothing on end
                    break;
                }
                case BrInstruction br:
                    body.AppendLine($"        goto {Target((int)br.LabelIndex).Label};");
                    break;
                case BrIfInstruction br:
                {
                    var cond = Pop();
                    body.AppendLine($"        if (({cond}) != 0) goto {Target((int)br.LabelIndex).Label};");
                    break;
                }
                // ── locals / const ──
                case LocalGetInstruction g: Push($"l{g.LocalIndex}"); break;
                case LocalSetInstruction s: body.AppendLine($"        l{s.LocalIndex} = {Pop()};"); break;
                case LocalTeeInstruction t:
                {
                    var v = stack[^1];
                    body.AppendLine($"        l{t.LocalIndex} = {v};");
                    break;
                }
                case I32ConstInstruction c: Push(c.Value.ToString()); break;
                case DropInstruction: Pop(); break;
                // ── i32 arithmetic / bitwise / shift ──
                case { OpCode: WasmOpCodes.I32Add }: Bin("({0} + {1})"); break;
                case { OpCode: WasmOpCodes.I32Sub }: Bin("({0} - {1})"); break;
                case { OpCode: WasmOpCodes.I32Mul }: Bin("({0} * {1})"); break;
                case { OpCode: WasmOpCodes.I32And }: Bin("({0} & {1})"); break;
                case { OpCode: WasmOpCodes.I32Or }: Bin("({0} | {1})"); break;
                case { OpCode: WasmOpCodes.I32Xor }: Bin("({0} ^ {1})"); break;
                case { OpCode: WasmOpCodes.I32Shl }: Bin("({0} << ({1} & 31))"); break;
                case { OpCode: WasmOpCodes.I32ShrS }: Bin("({0} >> ({1} & 31))"); break;
                case { OpCode: WasmOpCodes.I32ShrU }: Bin("(int)((uint){0} >> ({1} & 31))"); break;
                // ── i32 comparisons ──
                case { OpCode: WasmOpCodes.I32Eqz }: Un("(({0} == 0) ? 1 : 0)"); break;
                case { OpCode: WasmOpCodes.I32Eq }: Bin("(({0} == {1}) ? 1 : 0)"); break;
                case { OpCode: WasmOpCodes.I32Ne }: Bin("(({0} != {1}) ? 1 : 0)"); break;
                case { OpCode: WasmOpCodes.I32LtS }: Bin("(({0} < {1}) ? 1 : 0)"); break;
                case { OpCode: WasmOpCodes.I32LtU }: Bin("(((uint){0} < (uint){1}) ? 1 : 0)"); break;
                case { OpCode: WasmOpCodes.I32GtS }: Bin("(({0} > {1}) ? 1 : 0)"); break;
                case { OpCode: WasmOpCodes.I32GtU }: Bin("(((uint){0} > (uint){1}) ? 1 : 0)"); break;
                case { OpCode: WasmOpCodes.I32LeS }: Bin("(({0} <= {1}) ? 1 : 0)"); break;
                case { OpCode: WasmOpCodes.I32LeU }: Bin("(((uint){0} <= (uint){1}) ? 1 : 0)"); break;
                case { OpCode: WasmOpCodes.I32GeS }: Bin("(({0} >= {1}) ? 1 : 0)"); break;
                case { OpCode: WasmOpCodes.I32GeU }: Bin("(((uint){0} >= (uint){1}) ? 1 : 0)"); break;
                // ── memory load/store (memory 0) ──
                case I32Load8UInstruction m: LoadTmp($"(int)mem[{Addr(Pop(), m.Offset)}]"); break;
                case I32Load8SInstruction m: LoadTmp($"(int)(sbyte)mem[{Addr(Pop(), m.Offset)}]"); break;
                case I32LoadInstruction m: LoadTmp($"System.Runtime.CompilerServices.Unsafe.ReadUnaligned<int>(ref mem[{Addr(Pop(), m.Offset)}])"); break;
                case I32Store8Instruction m:
                {
                    var val = Pop();
                    body.AppendLine($"        mem[{Addr(Pop(), m.Offset)}] = (byte)({val});");
                    break;
                }
                case I32StoreInstruction m:
                {
                    var val = Pop();
                    body.AppendLine($"        System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref mem[{Addr(Pop(), m.Offset)}], {val});");
                    break;
                }
                case NopInstruction: break;
                default:
                    throw new NotSupportedException($"PoC transpiler: opcode 0x{instr.OpCode:X2} ({instr.GetType().Name}) not supported");
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(sig.ToString());
        sb.AppendLine("    {");
        sb.Append(body);
        sb.AppendLine("    }");
        return sb.ToString();

        Frame Target(int labelIndex) => ctrl[ctrl.Count - 1 - labelIndex];
        void LoadTmp(string expr)
        {
            var t = $"t{tmp++}";
            body.AppendLine($"        int {t} = {expr};");
            Push(t);
        }
        string Addr(string baseAddr, uint offset) => offset == 0 ? baseAddr : $"({baseAddr} + {offset})";
    }

    static void RequireI32(WasmValueType type, string what)
    {
        if (type is not I32Type)
            throw new NotSupportedException($"PoC transpiler: {what} is {type.GetType().Name}, only i32 supported");
    }

    enum FrameKind { Block, Loop, If }
    sealed record Frame(string Label, FrameKind Kind);
}
