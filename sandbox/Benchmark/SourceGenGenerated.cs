// Build-time transpiled (Wasm -> native C#) version of grayscale_bench, produced by
// sandbox/SourceGenPoc's transpiler. Included here so the benchmark can compare the
// AOT-safe source-gen path against the interpreter and the JIT runtimes.
internal static class SourceGenGenerated
{
    public static void Grayscale(byte[] mem, int p0, int p1)
    {
        int l0 = p0;
        int l1 = p1;
        int l2 = 0;
        int l3 = 0;
        int l4 = 0;
        int l5 = 0;
        int l6 = 0;
        int t0 = (l1 * 4);
        int t1 = (l0 + t0);
        l2 = t1;
        L1: ;
        int t2 = (((uint)l0 >= (uint)l2) ? 1 : 0);
        if ((t2) != 0) goto B0;
        int t3 = (int)mem[l0];
        l3 = t3;
        int t4 = (l0 + 1);
        int t5 = (int)mem[t4];
        l4 = t5;
        int t6 = (l0 + 2);
        int t7 = (int)mem[t6];
        l5 = t7;
        int t8 = (l3 * 77);
        int t9 = (l4 * 150);
        int t10 = (t8 + t9);
        int t11 = (l5 * 29);
        int t12 = (t10 + t11);
        int t13 = (int)((uint)t12 >> (8 & 31));
        l6 = t13;
        mem[l0] = (byte)(l6);
        int t14 = (l0 + 1);
        mem[t14] = (byte)(l6);
        int t15 = (l0 + 2);
        mem[t15] = (byte)(l6);
        int t16 = (l0 + 4);
        l0 = t16;
        goto L1;
        B0: ;
    }
}
