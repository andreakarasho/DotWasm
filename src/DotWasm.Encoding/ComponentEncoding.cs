using System.Collections.Immutable;
using DotWasm.Models;
using DotWasm.Models.Component;
using WasmValueType = DotWasm.Models.WasmValueType;

namespace DotWasm.Encoding;

/// <summary>
/// Decoder for the WebAssembly Component Model binary format. Produces a
/// <see cref="Component"/> whose definitions are kept in declaration order so that
/// instantiation can build up the component's index spaces faithfully.
/// </summary>
public static class ComponentEncoding
{
    public const ushort ComponentLayer = 0x0001;

    /// <summary>Returns true if the bytes begin with the component preamble (layer == 1).</summary>
    public static bool IsComponent(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            return false;
        var magic = (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
        if (magic != WasmEncoding.Magic)
            return false;
        var layer = (ushort)(bytes[6] | (bytes[7] << 8));
        return layer == ComponentLayer;
    }

    public static Component Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new SpanReader(bytes);
        var magic = reader.ReadUInt32LittleEndian();
        if (magic != WasmEncoding.Magic)
            WasmDecodeException.Throw($"Invalid WebAssembly magic '{magic:x8}'");

        var versionAndLayer = reader.ReadUInt32LittleEndian();
        var version = versionAndLayer & 0xFFFF;
        var layer = (versionAndLayer >> 16) & 0xFFFF;
        if (layer != ComponentLayer)
            WasmDecodeException.Throw(
                $"Not a component (layer {layer}); use WasmEncoding.Decode for core modules."
            );

        var definitions = ImmutableArray.CreateBuilder<ComponentDefinition>();

        while (!reader.IsEmpty)
        {
            var sectionId = reader.ReadByte();
            var payloadLength = checked((int)reader.ReadUInt32Leb128());
            var sectionSpan = reader.ReadBytes(payloadLength);
            var section = new SpanReader(sectionSpan);

            switch (sectionId)
            {
                case 0: // custom
                    break;
                case 1: // core module (single, inline)
                    definitions.Add(new DefCoreModule(WasmEncoding.Decode(sectionSpan)));
                    break;
                case 2: // core instance (vec)
                    for (var n = section.ReadUInt32Leb128(); n > 0; n--)
                        definitions.Add(new DefCoreInstance(ReadCoreInstance(ref section)));
                    break;
                case 3: // core type (vec)
                    for (var n = section.ReadUInt32Leb128(); n > 0; n--)
                        definitions.Add(new DefCoreType(ReadCoreType(ref section)));
                    break;
                case 4: // component (single, inline)
                    definitions.Add(new DefComponent(Decode(sectionSpan)));
                    break;
                case 5: // component instance (vec)
                    for (var n = section.ReadUInt32Leb128(); n > 0; n--)
                        definitions.Add(new DefComponentInstance(ReadComponentInstance(ref section)));
                    break;
                case 6: // alias (vec)
                    for (var n = section.ReadUInt32Leb128(); n > 0; n--)
                        definitions.Add(new DefAlias(ReadAlias(ref section)));
                    break;
                case 7: // type (vec)
                    for (var n = section.ReadUInt32Leb128(); n > 0; n--)
                        definitions.Add(new DefType(ReadDefType(ref section)));
                    break;
                case 8: // canon (vec)
                    for (var n = section.ReadUInt32Leb128(); n > 0; n--)
                        definitions.Add(new DefCanon(ReadCanon(ref section)));
                    break;
                case 9: // start (single)
                    definitions.Add(new DefStart(ReadStart(ref section)));
                    break;
                case 10: // import (vec)
                    for (var n = section.ReadUInt32Leb128(); n > 0; n--)
                        definitions.Add(new DefImport(ReadImport(ref section)));
                    break;
                case 11: // export (vec)
                    for (var n = section.ReadUInt32Leb128(); n > 0; n--)
                        definitions.Add(new DefExport(ReadExport(ref section)));
                    break;
                case 12: // value (vec)
                    for (var n = section.ReadUInt32Leb128(); n > 0; n--)
                        definitions.Add(new DefValue(ReadValType(ref section)));
                    break;
                default:
                    WasmDecodeException.Throw($"Unknown component section id {sectionId}.");
                    break;
            }
        }

        return new Component(version, definitions.ToImmutable());
    }

    // ---- Names ----

    static string ReadName(ref SpanReader reader)
    {
        var length = checked((int)reader.ReadUInt32Leb128());
        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    /// <summary>Import/export names carry a leading discriminant byte (0x00 plain, 0x01 interface).</summary>
    static string ReadImportExportName(ref SpanReader reader)
    {
        var kind = reader.ReadByte();
        if (kind > 0x01)
            WasmDecodeException.Throw($"Invalid import/export name kind 0x{kind:x2}.");
        return ReadName(ref reader);
    }

    // ---- Value types ----

    static ComponentValType ReadValType(ref SpanReader reader)
    {
        var code = reader.ReadInt64Leb128();
        if (code >= 0)
            return new DefinedTypeRef((uint)code);
        return new PrimitiveType(PrimitiveFromCode((int)code));
    }

    static PrimitiveValType PrimitiveFromCode(int code) => code switch
    {
        -1 => PrimitiveValType.Bool,
        -2 => PrimitiveValType.S8,
        -3 => PrimitiveValType.U8,
        -4 => PrimitiveValType.S16,
        -5 => PrimitiveValType.U16,
        -6 => PrimitiveValType.S32,
        -7 => PrimitiveValType.U32,
        -8 => PrimitiveValType.S64,
        -9 => PrimitiveValType.U64,
        -10 => PrimitiveValType.F32,
        -11 => PrimitiveValType.F64,
        -12 => PrimitiveValType.Char,
        -13 => PrimitiveValType.String,
        _ => ThrowPrimitive(code),
    };

    static PrimitiveValType ThrowPrimitive(int code)
    {
        WasmDecodeException.Throw($"Invalid component primitive type code {code}.");
        throw null!;
    }

    // ---- Type section entries (deftype) ----

    static ComponentDefType ReadDefType(ref SpanReader reader)
    {
        var tag = reader.ReadByte();
        switch (tag)
        {
            // primitives (defvaltype form)
            case >= 0x73 and <= 0x7f:
                return new TypeValDef(new PrimitiveType(PrimitiveFromCode((sbyte)tag)));
            case 0x72: // record
                return new TypeValDef(ReadRecord(ref reader));
            case 0x71: // variant
                return new TypeValDef(ReadVariant(ref reader));
            case 0x70: // list
                return new TypeValDef(new ListType(ReadValType(ref reader)));
            case 0x67: // fixed-length list
            {
                var element = ReadValType(ref reader);
                var length = reader.ReadUInt32Leb128();
                return new TypeValDef(new ListType(element, length));
            }
            case 0x6f: // tuple
                return new TypeValDef(new TupleType(ReadValTypeVec(ref reader)));
            case 0x6e: // flags
                return new TypeValDef(new FlagsType(ReadNameVec(ref reader)));
            case 0x6d: // enum
                return new TypeValDef(new EnumType(ReadNameVec(ref reader)));
            case 0x6b: // option
                return new TypeValDef(new OptionType(ReadValType(ref reader)));
            case 0x6a: // result
                return new TypeValDef(ReadResult(ref reader));
            case 0x69: // own
                return new TypeValDef(new OwnType(reader.ReadUInt32Leb128()));
            case 0x68: // borrow
                return new TypeValDef(new BorrowType(reader.ReadUInt32Leb128()));
            case 0x40: // func type
                return new TypeFuncDef(ReadFuncType(ref reader));
            case 0x41: // component type
                return new TypeComponentDef(ReadComponentType(ref reader));
            case 0x42: // instance type
                return new TypeInstanceDef(ReadInstanceType(ref reader));
            case 0x3f: // resource type
                return new TypeResourceDef(ReadResourceType(ref reader));
            default:
                WasmDecodeException.Throw($"Invalid component deftype tag 0x{tag:x2}.");
                throw null!;
        }
    }

    static RecordType ReadRecord(ref SpanReader reader)
    {
        var count = reader.ReadUInt32Leb128();
        var fields = ImmutableArray.CreateBuilder<RecordField>((int)count);
        for (var i = 0u; i < count; i++)
        {
            var name = ReadName(ref reader);
            fields.Add(new RecordField(name, ReadValType(ref reader)));
        }
        return new RecordType(fields.MoveToImmutable());
    }

    static VariantType ReadVariant(ref SpanReader reader)
    {
        var count = reader.ReadUInt32Leb128();
        var cases = ImmutableArray.CreateBuilder<VariantCase>((int)count);
        for (var i = 0u; i < count; i++)
        {
            var name = ReadName(ref reader);
            ComponentValType? type = reader.ReadByte() == 0x01 ? ReadValType(ref reader) : null;
            uint? refines = reader.ReadByte() == 0x01 ? reader.ReadUInt32Leb128() : null;
            cases.Add(new VariantCase(name, type, refines));
        }
        return new VariantType(cases.MoveToImmutable());
    }

    static ResultType ReadResult(ref SpanReader reader)
    {
        ComponentValType? ok = reader.ReadByte() == 0x01 ? ReadValType(ref reader) : null;
        ComponentValType? err = reader.ReadByte() == 0x01 ? ReadValType(ref reader) : null;
        return new ResultType(ok, err);
    }

    static ImmutableArray<ComponentValType> ReadValTypeVec(ref SpanReader reader)
    {
        var count = reader.ReadUInt32Leb128();
        var items = ImmutableArray.CreateBuilder<ComponentValType>((int)count);
        for (var i = 0u; i < count; i++)
            items.Add(ReadValType(ref reader));
        return items.MoveToImmutable();
    }

    static ImmutableArray<string> ReadNameVec(ref SpanReader reader)
    {
        var count = reader.ReadUInt32Leb128();
        var items = ImmutableArray.CreateBuilder<string>((int)count);
        for (var i = 0u; i < count; i++)
            items.Add(ReadName(ref reader));
        return items.MoveToImmutable();
    }

    static ComponentFuncType ReadFuncType(ref SpanReader reader)
    {
        var paramCount = reader.ReadUInt32Leb128();
        var paramz = ImmutableArray.CreateBuilder<NamedValType>((int)paramCount);
        for (var i = 0u; i < paramCount; i++)
        {
            var name = ReadName(ref reader);
            paramz.Add(new NamedValType(name, ReadValType(ref reader)));
        }

        // resultlist: 0x00 valtype => one result; 0x01 0x00 => no results
        var resultTag = reader.ReadByte();
        ComponentValType? result = resultTag switch
        {
            0x00 => ReadValType(ref reader),
            0x01 => ReadNamedResultList(ref reader),
            _ => ThrowResult(resultTag),
        };
        return new ComponentFuncType(paramz.MoveToImmutable(), result);
    }

    static ComponentValType? ReadNamedResultList(ref SpanReader reader)
    {
        // Legacy named-results form: a vector of (name, valtype). Empty => no result.
        var count = reader.ReadUInt32Leb128();
        if (count == 0)
            return null;
        // Single named result is the only currently-valid case; collapse to its type.
        ComponentValType? only = null;
        for (var i = 0u; i < count; i++)
        {
            _ = ReadName(ref reader);
            var t = ReadValType(ref reader);
            only ??= t;
        }
        return only;
    }

    static ComponentValType? ThrowResult(byte tag)
    {
        WasmDecodeException.Throw($"Invalid component func result tag 0x{tag:x2}.");
        throw null!;
    }

    static ResourceType ReadResourceType(ref SpanReader reader)
    {
        // rep is a core valtype (currently always i32)
        var rep = ReadCoreValType(ref reader);
        var hasDtor = reader.ReadByte();
        uint? dtor = hasDtor == 0x01 ? reader.ReadUInt32Leb128() : null;
        return new ResourceType(rep, dtor);
    }

    static InstanceType ReadInstanceType(ref SpanReader reader)
    {
        var count = reader.ReadUInt32Leb128();
        var decls = ImmutableArray.CreateBuilder<InstanceDecl>((int)count);
        for (var i = 0u; i < count; i++)
            decls.Add(ReadInstanceDecl(ref reader));
        return new InstanceType(decls.MoveToImmutable());
    }

    static InstanceDecl ReadInstanceDecl(ref SpanReader reader)
    {
        var tag = reader.ReadByte();
        return tag switch
        {
            0x00 => new InstanceCoreTypeDecl(ReadCoreType(ref reader)),
            0x01 => new InstanceTypeDecl(ReadDefType(ref reader)),
            0x02 => new InstanceAliasDecl(ReadAlias(ref reader)),
            0x04 => ReadInstanceExportDecl(ref reader),
            _ => ThrowInstanceDecl(tag),
        };
    }

    static InstanceDecl ReadInstanceExportDecl(ref SpanReader reader)
    {
        var name = ReadImportExportName(ref reader);
        return new InstanceExportDecl(name, ReadExternDesc(ref reader));
    }

    static InstanceDecl ThrowInstanceDecl(byte tag)
    {
        WasmDecodeException.Throw($"Invalid instance decl tag 0x{tag:x2}.");
        throw null!;
    }

    static ComponentDeclType ReadComponentType(ref SpanReader reader)
    {
        var count = reader.ReadUInt32Leb128();
        var decls = ImmutableArray.CreateBuilder<ComponentDecl>((int)count);
        for (var i = 0u; i < count; i++)
        {
            var tag = reader.ReadByte();
            decls.Add(tag switch
            {
                0x00 => new ComponentInnerDecl(new InstanceCoreTypeDecl(ReadCoreType(ref reader))),
                0x01 => new ComponentInnerDecl(new InstanceTypeDecl(ReadDefType(ref reader))),
                0x02 => new ComponentInnerDecl(new InstanceAliasDecl(ReadAlias(ref reader))),
                0x03 => ReadComponentImportDecl(ref reader),
                0x04 => new ComponentInnerDecl(ReadInstanceExportDecl(ref reader)),
                _ => ThrowComponentDecl(tag),
            });
        }
        return new ComponentDeclType(decls.MoveToImmutable());
    }

    static ComponentDecl ReadComponentImportDecl(ref SpanReader reader)
    {
        var name = ReadImportExportName(ref reader);
        return new ComponentImportDecl(name, ReadExternDesc(ref reader));
    }

    static ComponentDecl ThrowComponentDecl(byte tag)
    {
        WasmDecodeException.Throw($"Invalid component decl tag 0x{tag:x2}.");
        throw null!;
    }

    static ExternDesc ReadExternDesc(ref SpanReader reader)
    {
        var tag = reader.ReadByte();
        switch (tag)
        {
            case 0x00: // core module
            {
                var sub = reader.ReadByte();
                if (sub != 0x11)
                    WasmDecodeException.Throw($"Invalid core externdesc sub-tag 0x{sub:x2}.");
                return new CoreModuleDesc(reader.ReadUInt32Leb128());
            }
            case 0x01:
                return new FuncDesc(reader.ReadUInt32Leb128());
            case 0x02: // value: 0x00 valtype (value bound = exact type)
            {
                var bound = reader.ReadByte();
                if (bound != 0x00)
                    WasmDecodeException.Throw($"Unsupported value bound 0x{bound:x2}.");
                return new ValueDesc(ReadValType(ref reader));
            }
            case 0x03: // type bound
            {
                var bound = reader.ReadByte();
                return bound switch
                {
                    0x00 => new TypeBoundDesc(IsEq: true, reader.ReadUInt32Leb128()),
                    0x01 => new TypeBoundDesc(IsEq: false, 0), // sub resource
                    _ => ThrowTypeBound(bound),
                };
            }
            case 0x04:
                return new ComponentDescType(reader.ReadUInt32Leb128());
            case 0x05:
                return new InstanceDesc(reader.ReadUInt32Leb128());
            default:
                WasmDecodeException.Throw($"Invalid externdesc tag 0x{tag:x2}.");
                throw null!;
        }
    }

    static ExternDesc ThrowTypeBound(byte tag)
    {
        WasmDecodeException.Throw($"Invalid type bound 0x{tag:x2}.");
        throw null!;
    }

    // ---- Core types ----

    static CoreTypeDef ReadCoreType(ref SpanReader reader)
    {
        var tag = reader.ReadByte();
        return tag switch
        {
            0x60 => new CoreFuncTypeDef(ReadCoreFuncType(ref reader)),
            0x50 => ReadCoreModuleType(ref reader),
            _ => ThrowCoreType(tag),
        };
    }

    static CoreTypeDef ThrowCoreType(byte tag)
    {
        WasmDecodeException.Throw($"Invalid core type tag 0x{tag:x2}.");
        throw null!;
    }

    static FuncType ReadCoreFuncType(ref SpanReader reader)
    {
        var paramCount = reader.ReadUInt32Leb128();
        var paramz = ImmutableArray.CreateBuilder<WasmValueType>((int)paramCount);
        for (var i = 0u; i < paramCount; i++)
            paramz.Add(ReadCoreValType(ref reader));
        var resultCount = reader.ReadUInt32Leb128();
        var results = ImmutableArray.CreateBuilder<WasmValueType>((int)resultCount);
        for (var i = 0u; i < resultCount; i++)
            results.Add(ReadCoreValType(ref reader));
        return new FuncType
        {
            Parameters = paramz.MoveToImmutable(),
            Results = results.MoveToImmutable(),
        };
    }

    static CoreTypeDef ReadCoreModuleType(ref SpanReader reader)
    {
        var count = reader.ReadUInt32Leb128();
        var decls = ImmutableArray.CreateBuilder<CoreModuleDecl>((int)count);
        for (var i = 0u; i < count; i++)
        {
            var tag = reader.ReadByte();
            switch (tag)
            {
                case 0x00: // import
                {
                    var module = ReadName(ref reader);
                    var name = ReadName(ref reader);
                    decls.Add(new CoreModuleImportDecl(module, name, ReadCoreExternalType(ref reader)));
                    break;
                }
                case 0x01: // type
                    decls.Add(new CoreModuleTypeDecl(ReadCoreType(ref reader)));
                    break;
                case 0x02: // alias
                    decls.Add(new CoreModuleAliasDecl(ReadAlias(ref reader)));
                    break;
                case 0x03: // export
                {
                    var name = ReadName(ref reader);
                    decls.Add(new CoreModuleExportDecl(name, ReadCoreExternalType(ref reader)));
                    break;
                }
                default:
                    WasmDecodeException.Throw($"Invalid core module decl tag 0x{tag:x2}.");
                    break;
            }
        }
        return new CoreModuleTypeDef(decls.MoveToImmutable());
    }

    static ExternalType ReadCoreExternalType(ref SpanReader reader)
    {
        // core importdesc: 0x00 typeidx (func) | 0x01 tabletype | 0x02 memtype | 0x03 globaltype
        var kind = reader.ReadByte();
        switch (kind)
        {
            case 0x00:
            {
                var typeIndex = reader.ReadUInt32Leb128();
                return new FuncType
                {
                    TypeIndex = typeIndex,
                    Parameters = [],
                    Results = [],
                };
            }
            case 0x03:
            {
                var valueType = ReadCoreValType(ref reader);
                var mutable = reader.ReadByte() == 0x01;
                return new GlobalType { ValueType = valueType, Mutable = mutable };
            }
            default:
                WasmDecodeException.Throw($"Unsupported core externtype kind 0x{kind:x2} in module type.");
                throw null!;
        }
    }

    static WasmValueType ReadCoreValType(ref SpanReader reader)
    {
        var b = reader.ReadByte();
        return b switch
        {
            0x7f => WasmTypes.I32,
            0x7e => WasmTypes.I64,
            0x7d => WasmTypes.F32,
            0x7c => WasmTypes.F64,
            0x7b => WasmTypes.V128,
            _ => ThrowCoreValType(b),
        };
    }

    static WasmValueType ThrowCoreValType(byte b)
    {
        WasmDecodeException.Throw($"Unsupported core value type 0x{b:x2} in component.");
        throw null!;
    }

    // ---- Core instances ----

    static CoreInstanceExpr ReadCoreInstance(ref SpanReader reader)
    {
        var tag = reader.ReadByte();
        switch (tag)
        {
            case 0x00: // instantiate
            {
                var moduleIndex = reader.ReadUInt32Leb128();
                var argCount = reader.ReadUInt32Leb128();
                var args = ImmutableArray.CreateBuilder<CoreInstantiateArg>((int)argCount);
                for (var i = 0u; i < argCount; i++)
                {
                    var name = ReadName(ref reader);
                    var sort = reader.ReadByte(); // 0x12 = instance
                    if (sort != 0x12)
                        WasmDecodeException.Throw($"Core instantiate arg must be an instance (got 0x{sort:x2}).");
                    args.Add(new CoreInstantiateArg(name, reader.ReadUInt32Leb128()));
                }
                return new CoreInstantiate(moduleIndex, args.MoveToImmutable());
            }
            case 0x01: // inline exports
            {
                var count = reader.ReadUInt32Leb128();
                var exports = ImmutableArray.CreateBuilder<CoreInlineExport>((int)count);
                for (var i = 0u; i < count; i++)
                {
                    var name = ReadName(ref reader);
                    var sort = ReadCoreSort(ref reader);
                    exports.Add(new CoreInlineExport(name, sort, reader.ReadUInt32Leb128()));
                }
                return new CoreInlineExports(exports.MoveToImmutable());
            }
            default:
                WasmDecodeException.Throw($"Invalid core instance tag 0x{tag:x2}.");
                throw null!;
        }
    }

    static CoreSort ReadCoreSort(ref SpanReader reader)
    {
        var b = reader.ReadByte();
        return b switch
        {
            0x00 => CoreSort.Func,
            0x01 => CoreSort.Table,
            0x02 => CoreSort.Memory,
            0x03 => CoreSort.Global,
            0x10 => CoreSort.Type,
            0x11 => CoreSort.Module,
            0x12 => CoreSort.Instance,
            _ => ThrowCoreSort(b),
        };
    }

    static CoreSort ThrowCoreSort(byte b)
    {
        WasmDecodeException.Throw($"Invalid core sort 0x{b:x2}.");
        throw null!;
    }

    // ---- Component instances ----

    static ComponentInstanceExpr ReadComponentInstance(ref SpanReader reader)
    {
        var tag = reader.ReadByte();
        switch (tag)
        {
            case 0x00: // instantiate
            {
                var componentIndex = reader.ReadUInt32Leb128();
                var argCount = reader.ReadUInt32Leb128();
                var args = ImmutableArray.CreateBuilder<ComponentInstantiateArg>((int)argCount);
                for (var i = 0u; i < argCount; i++)
                {
                    var name = ReadName(ref reader);
                    args.Add(new ComponentInstantiateArg(name, ReadSortIdx(ref reader)));
                }
                return new ComponentInstantiate(componentIndex, args.MoveToImmutable());
            }
            case 0x01: // inline exports
            {
                var count = reader.ReadUInt32Leb128();
                var exports = ImmutableArray.CreateBuilder<ComponentInlineExport>((int)count);
                for (var i = 0u; i < count; i++)
                {
                    var name = ReadName(ref reader);
                    exports.Add(new ComponentInlineExport(name, ReadSortIdx(ref reader)));
                }
                return new ComponentInlineExports(exports.MoveToImmutable());
            }
            default:
                WasmDecodeException.Throw($"Invalid component instance tag 0x{tag:x2}.");
                throw null!;
        }
    }

    // ---- Sorts / aliases ----

    static SortIdx ReadSortIdx(ref SpanReader reader)
    {
        var sort = ReadSort(ref reader);
        return new SortIdx(sort, reader.ReadUInt32Leb128());
    }

    static ComponentSortKind ReadSort(ref SpanReader reader)
    {
        var b = reader.ReadByte();
        if (b == 0x00)
        {
            return ReadCoreSort(ref reader) switch
            {
                CoreSort.Func => ComponentSortKind.CoreFunc,
                CoreSort.Table => ComponentSortKind.CoreTable,
                CoreSort.Memory => ComponentSortKind.CoreMemory,
                CoreSort.Global => ComponentSortKind.CoreGlobal,
                CoreSort.Type => ComponentSortKind.CoreType,
                CoreSort.Module => ComponentSortKind.CoreModule,
                CoreSort.Instance => ComponentSortKind.CoreInstance,
                _ => ComponentSortKind.CoreFunc,
            };
        }
        return b switch
        {
            0x01 => ComponentSortKind.Func,
            0x02 => ComponentSortKind.Value,
            0x03 => ComponentSortKind.Type,
            0x04 => ComponentSortKind.Component,
            0x05 => ComponentSortKind.Instance,
            _ => ThrowSort(b),
        };
    }

    static ComponentSortKind ThrowSort(byte b)
    {
        WasmDecodeException.Throw($"Invalid component sort 0x{b:x2}.");
        throw null!;
    }

    static Alias ReadAlias(ref SpanReader reader)
    {
        var sort = ReadSort(ref reader);
        var target = reader.ReadByte();
        switch (target)
        {
            case 0x00: // export from a component instance
            {
                var instanceIndex = reader.ReadUInt32Leb128();
                return new AliasExport(sort, instanceIndex, ReadName(ref reader));
            }
            case 0x01: // export from a core instance
            {
                var instanceIndex = reader.ReadUInt32Leb128();
                return new AliasCoreExport(sort, instanceIndex, ReadName(ref reader));
            }
            case 0x02: // outer
            {
                var count = reader.ReadUInt32Leb128();
                return new AliasOuter(sort, count, reader.ReadUInt32Leb128());
            }
            default:
                WasmDecodeException.Throw($"Invalid alias target 0x{target:x2}.");
                throw null!;
        }
    }

    // ---- Canon ----

    static Canon ReadCanon(ref SpanReader reader)
    {
        var tag = reader.ReadByte();
        switch (tag)
        {
            case 0x00: // lift
            {
                Expect(ref reader, 0x00);
                var coreFuncIndex = reader.ReadUInt32Leb128();
                var opts = ReadCanonOpts(ref reader);
                var typeIndex = reader.ReadUInt32Leb128();
                return new CanonLift(coreFuncIndex, typeIndex, opts);
            }
            case 0x01: // lower
            {
                Expect(ref reader, 0x00);
                var funcIndex = reader.ReadUInt32Leb128();
                var opts = ReadCanonOpts(ref reader);
                return new CanonLower(funcIndex, opts);
            }
            case 0x02:
                return new CanonResourceNew(reader.ReadUInt32Leb128());
            case 0x03:
                return new CanonResourceDrop(reader.ReadUInt32Leb128(), Async: false);
            case 0x04:
                return new CanonResourceRep(reader.ReadUInt32Leb128());
            case 0x07:
                return new CanonResourceDrop(reader.ReadUInt32Leb128(), Async: true);
            default:
                WasmDecodeException.Throw($"Unsupported canon tag 0x{tag:x2}.");
                throw null!;
        }
    }

    static void Expect(ref SpanReader reader, byte expected)
    {
        var actual = reader.ReadByte();
        if (actual != expected)
            WasmDecodeException.Throw($"Expected byte 0x{expected:x2}, got 0x{actual:x2}.");
    }

    static ImmutableArray<CanonOpt> ReadCanonOpts(ref SpanReader reader)
    {
        var count = reader.ReadUInt32Leb128();
        var opts = ImmutableArray.CreateBuilder<CanonOpt>((int)count);
        for (var i = 0u; i < count; i++)
        {
            var b = reader.ReadByte();
            opts.Add(b switch
            {
                0x00 => new CanonStringEncoding(StringEncoding.Utf8),
                0x01 => new CanonStringEncoding(StringEncoding.Utf16),
                0x02 => new CanonStringEncoding(StringEncoding.Latin1Utf16),
                0x03 => new CanonMemory(reader.ReadUInt32Leb128()),
                0x04 => new CanonRealloc(reader.ReadUInt32Leb128()),
                0x05 => new CanonPostReturn(reader.ReadUInt32Leb128()),
                _ => ThrowCanonOpt(b),
            });
        }
        return opts.MoveToImmutable();
    }

    static CanonOpt ThrowCanonOpt(byte b)
    {
        WasmDecodeException.Throw($"Unsupported canon option 0x{b:x2} (async not supported).");
        throw null!;
    }

    // ---- Import / export / start ----

    static ComponentImport ReadImport(ref SpanReader reader)
    {
        var name = ReadImportExportName(ref reader);
        return new ComponentImport(name, ReadExternDesc(ref reader));
    }

    static ComponentExport ReadExport(ref SpanReader reader)
    {
        var name = ReadImportExportName(ref reader);
        var idx = ReadSortIdx(ref reader);
        ExternDesc? desc = reader.ReadByte() == 0x01 ? ReadExternDesc(ref reader) : null;
        return new ComponentExport(name, idx, desc);
    }

    static ComponentStart ReadStart(ref SpanReader reader)
    {
        var funcIndex = reader.ReadUInt32Leb128();
        var argCount = reader.ReadUInt32Leb128();
        var args = ImmutableArray.CreateBuilder<uint>((int)argCount);
        for (var i = 0u; i < argCount; i++)
            args.Add(reader.ReadUInt32Leb128());
        var resultCount = reader.ReadUInt32Leb128();
        return new ComponentStart(funcIndex, args.MoveToImmutable(), resultCount);
    }
}
