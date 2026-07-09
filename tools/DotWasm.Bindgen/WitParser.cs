using System.Text;

namespace DotWasm.Bindgen;

/// <summary>Recursive-descent parser for the subset of WIT emitted by `wasm-tools component wit`.</summary>
public sealed class WitParser
{
    readonly List<string> tokens;
    int pos;

    public List<WitInterface> Interfaces { get; } = [];
    public HashSet<string> ExportedInterfaces { get; } = [];

    WitParser(List<string> tokens) => this.tokens = tokens;

    public static WitParser Parse(string source)
    {
        var p = new WitParser(Tokenize(source));
        p.ParseTop();
        return p;
    }

    // ---- tokenizer ----

    static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        const string punct = "{}()<>,;=:/@.";
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                i += 2;
                continue;
            }
            if (punct.IndexOf(c) >= 0) { tokens.Add(c.ToString()); i++; continue; }
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && punct.IndexOf(s[i]) < 0) i++;
            tokens.Add(s[start..i]);
        }
        return tokens;
    }

    // ---- token helpers ----

    string Peek => pos < tokens.Count ? tokens[pos] : "";
    string Next() => tokens[pos++];
    bool Eat(string t) { if (Peek == t) { pos++; return true; } return false; }
    void Expect(string t) { if (!Eat(t)) throw new FormatException($"Expected '{t}' but got '{Peek}' at token {pos}."); }
    static string Unescape(string id) => id.StartsWith('%') ? id[1..] : id;

    // ---- grammar ----

    void ParseTop()
    {
        while (pos < tokens.Count)
        {
            switch (Peek)
            {
                case "package": ParsePackage(); break;
                case "world": ParseWorld(null); break;
                case "interface": ParseInterface("root"); break;
                default: Next(); break; // skip unknown
            }
        }
    }

    // package id ; | package id { items }
    void ParsePackage()
    {
        Expect("package");
        var pkg = ParsePackageId();
        if (Eat(";")) return;
        Expect("{");
        while (!Eat("}"))
        {
            switch (Peek)
            {
                case "interface": ParseInterface(pkg); break;
                case "world": ParseWorld(pkg); break;
                default: Next(); break;
            }
        }
    }

    // ns:name (@ver)? -> "ns:name"
    string ParsePackageId()
    {
        var ns = Unescape(Next());
        Expect(":");
        var name = Unescape(Next());
        SkipVersion();
        return $"{ns}:{name}";
    }

    void SkipVersion()
    {
        if (Eat("@"))
            while (Peek is not ("{" or ";" or "}" or "" or "/" or "," or ")"))
                Next();
    }

    void ParseWorld(string? pkg)
    {
        Expect("world");
        Next(); // world name
        Expect("{");
        while (!Eat("}"))
        {
            if (Peek is "export" or "import")
            {
                var kind = Next();
                var name = ParseInterfaceRef();
                Eat(";");
                if (kind == "export")
                    ExportedInterfaces.Add(name);
            }
            else Next();
        }
    }

    // ns:name/iface (@ver)?  (or a bare local interface name)
    string ParseInterfaceRef()
    {
        var first = Unescape(Next());
        if (Eat(":"))
        {
            var name = Unescape(Next());
            string iface = name;
            if (Eat("/")) iface = Unescape(Next());
            SkipVersion();
            return $"{first}:{name}/{iface}";
        }
        return first;
    }

    void ParseInterface(string pkg)
    {
        Expect("interface");
        var name = Unescape(Next());
        var types = new List<WitTypeDef>();
        var funcs = new List<WitFunc>();
        Expect("{");
        while (!Eat("}"))
            ParseInterfaceItem(types, funcs);
        Interfaces.Add(new WitInterface(pkg, name, types, funcs));
    }

    void ParseInterfaceItem(List<WitTypeDef> types, List<WitFunc> funcs)
    {
        switch (Peek)
        {
            case "record": types.Add(ParseRecord()); break;
            case "variant": types.Add(ParseVariant()); break;
            case "enum": types.Add(ParseEnum()); break;
            case "flags": types.Add(ParseFlags()); break;
            case "resource": types.Add(ParseResource()); break;
            case "type": types.Add(ParseAlias()); break;
            case "use": SkipUse(); break;
            default: funcs.Add(ParseFreeFunc()); break;
        }
    }

    WitRecord ParseRecord()
    {
        Expect("record");
        var name = Unescape(Next());
        Expect("{");
        var fields = new List<(string, WitType)>();
        while (!Eat("}"))
        {
            var f = Unescape(Next());
            Expect(":");
            fields.Add((f, ParseType()));
            Eat(",");
        }
        return new WitRecord(name, fields);
    }

    WitVariant ParseVariant()
    {
        Expect("variant");
        var name = Unescape(Next());
        Expect("{");
        var cases = new List<(string, WitType?)>();
        while (!Eat("}"))
        {
            var c = Unescape(Next());
            WitType? t = null;
            if (Eat("(")) { t = ParseType(); Expect(")"); }
            cases.Add((c, t));
            Eat(",");
        }
        return new WitVariant(name, cases);
    }

    WitEnum ParseEnum()
    {
        Expect("enum");
        var name = Unescape(Next());
        Expect("{");
        var cases = new List<string>();
        while (!Eat("}")) { cases.Add(Unescape(Next())); Eat(","); }
        return new WitEnum(name, cases);
    }

    WitFlags ParseFlags()
    {
        Expect("flags");
        var name = Unescape(Next());
        Expect("{");
        var names = new List<string>();
        while (!Eat("}")) { names.Add(Unescape(Next())); Eat(","); }
        return new WitFlags(name, names);
    }

    WitAlias ParseAlias()
    {
        Expect("type");
        var name = Unescape(Next());
        Expect("=");
        var t = ParseType();
        Eat(";");
        return new WitAlias(name, t);
    }

    WitResourceDef ParseResource()
    {
        Expect("resource");
        var name = Unescape(Next());
        var methods = new List<WitFunc>();
        if (Eat(";")) return new WitResourceDef(name, methods); // resource with no body
        Expect("{");
        while (!Eat("}"))
        {
            var memberName = Unescape(Next());
            if (memberName == "constructor")
            {
                // constructor(params);  -- no ':' before the parens
                var ps = ParseParams();
                Eat(";");
                methods.Add(new WitFunc("constructor", ps, null, WitFuncKind.Constructor, name));
                continue;
            }
            Expect(":");
            var kind = Eat("static") ? WitFuncKind.Static : WitFuncKind.Method;
            Expect("func");
            var mps = ParseParams();
            WitType? mresult = Eat("-") && Eat(">") ? ParseType() : null;
            Eat(";");
            methods.Add(new WitFunc(memberName, mps, mresult, kind, name));
        }
        return new WitResourceDef(name, methods);
    }

    WitFunc ParseFreeFunc()
    {
        var name = Unescape(Next());
        Expect(":");
        var isStatic = Eat("static");
        Expect("func");
        var ps = ParseParams();
        WitType? result = Eat("-") && Eat(">") ? ParseType() : null;
        Eat(";");
        return new WitFunc(name, ps, result, isStatic ? WitFuncKind.Static : WitFuncKind.Free);
    }

    List<(string, WitType)> ParseParams()
    {
        Expect("(");
        var ps = new List<(string, WitType)>();
        while (!Eat(")"))
        {
            var n = Unescape(Next());
            Expect(":");
            ps.Add((n, ParseType()));
            Eat(",");
        }
        return ps;
    }

    void SkipUse()
    {
        Expect("use");
        while (!Eat(";") && pos < tokens.Count) Next();
    }

    WitType ParseType()
    {
        var t = Unescape(Next());
        switch (t)
        {
            case "bool" or "s8" or "u8" or "s16" or "u16" or "s32" or "u32"
                or "s64" or "u64" or "f32" or "f64" or "char" or "string":
                return new WitPrim(t);
            case "list":
                Expect("<"); var el = ParseType(); Expect(">"); return new WitList(el);
            case "option":
                Expect("<"); var inner = ParseType(); Expect(">"); return new WitOption(inner);
            case "tuple":
                Expect("<");
                var items = new List<WitType>();
                while (!Eat(">")) { items.Add(ParseType()); Eat(","); }
                return new WitTuple(items);
            case "result":
                return ParseResultType();
            case "borrow":
                Expect("<"); var b = Unescape(Next()); Expect(">"); return new WitHandle(b, false);
            case "own":
                Expect("<"); var o = Unescape(Next()); Expect(">"); return new WitHandle(o, true);
            default:
                return new WitNamed(t); // record/variant/enum/flags/alias, or a resource (own handle)
        }
    }

    WitType ParseResultType()
    {
        if (!Eat("<")) return new WitResult(null, null);
        WitType? ok = Eat("_") ? null : ParseType();
        WitType? err = null;
        if (Eat(",")) err = Eat("_") ? null : ParseType();
        Expect(">");
        return new WitResult(ok, err);
    }
}
