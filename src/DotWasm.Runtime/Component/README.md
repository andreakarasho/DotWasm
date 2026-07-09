# DotWasm Component Model — host API

Run WebAssembly **components** (not just core modules) from C#. DotWasm consumes the
compiled component binary; there is no WIT parser in the runtime (see the typed bindings
generator under `tools/` for WIT-aware codegen).

## Quick start

```csharp
using DotWasm.Encoding;
using DotWasm.Runtime;
using DotWasm.Runtime.Component;

var bytes = File.ReadAllBytes("component.wasm");
var component = ComponentEncoding.Decode(bytes);          // -> Models.Component.Component

var linker = new ComponentLinker(new WasmStore());
var instance = linker.Instantiate(component);             // -> ComponentInstance

// Top-level function export:
object?[] result = instance.Invoke("run", 40, 2);        // args are component values

// Exported interface (instance export), e.g. "pkg:ns/iface":
ComponentSubInstance iface = instance.GetInstance("pkg:ns/iface")!;
object?[] sum = iface.Invoke("add", 2, 3);               // 5
```

`Invoke` returns `object?[]` — zero or one element (the Component Model allows at most one
result today).

## Value mapping (host ⇄ component)

| Component type        | C# value                                  |
|-----------------------|-------------------------------------------|
| `bool`                | `bool`                                     |
| `s8/u8`               | `sbyte` / `byte`                           |
| `s16/u16`             | `short` / `ushort`                         |
| `s32/u32`             | `int` / `uint`                             |
| `s64/u64`             | `long` / `ulong`                           |
| `f32/f64`             | `float` / `double`                         |
| `char`                | `int` (Unicode scalar value)               |
| `string`              | `string`                                   |
| `list<T>`             | `object?[]` (elements)                     |
| `tuple<...>`          | `object?[]` (in order)                     |
| `record`              | `object?[]` (field values, declared order) |
| `enum`                | `uint` (case index)                        |
| `variant`             | `VariantValue(case, payload?)`             |
| `flags`               | `FlagsValue(bool[])` (declaration order)   |
| `option<T>`           | `OptionValue` (`Some`/`None`)              |
| `result<O,E>`         | `ResultValue` (`Ok`/`Err`)                 |
| `own<R>` / `borrow<R>`| `ResourceValue`                            |

The mapping is type-directed: you pass plain CLR values and DotWasm interprets them per the
function's declared type. The `Comp` helpers make aggregate literals read clearly:

```csharp
iface.Invoke("add-point", Comp.Record(1, 2), Comp.Record(3, 4));   // record args
iface.Invoke("describe", Comp.Variant(0, 2.5));                    // variant: case 0, payload 2.5
iface.Invoke("maybe-inc", OptionValue.Some(5));
var r = (ResultValue)iface.Invoke("divide", 10, 2)[0]!;            // r.IsOk, r.Value
```

> Note: `Invoke(string, params object?[])` spreads its args. To pass a *single* aggregate
> argument (one record/list/tuple), cast it to `object?`: `Invoke("f", (object?)Comp.Record(1,2))`.

## Host-implemented imports

Provide host implementations for a component's imports before instantiating.

```csharp
// Function of an imported interface ("instance import"):
linker.DefineImportFunc("pkg:ns/host-api", "add", a => [ (int)a[0]! + (int)a[1]! ]);

// Top-level function import:
linker.DefineImport("some-import", a => [ /* ... */ ]);
```

## Resources

An exported resource constructor returns a `ResourceValue`; methods take it back:

```csharp
var ctr = iface.Invoke("[constructor]counter", 10)[0];       // ResourceValue
iface.Invoke("[method]counter.increment", ctr);              // 11
```

A host *implementing* an imported resource works with raw i32 reps:

```csharp
var state = new Dictionary<int,int>(); var next = 1;
linker.DefineImportFunc("pkg:ns/store", "[constructor]bucket", a => { var r = next++; state[r] = (int)a[0]!; return [r]; });
linker.DefineImportFunc("pkg:ns/store", "[method]bucket.add", a => { var self = (ResourceValue)a[0]!; state[self.Rep] += (int)a[1]!; return [state[self.Rep]]; });
```

## WASI

Components built with the default toolchain (Rust `wasm32-wasip2`, jco, C# wasi-wasm) import
WASI 0.2. `DotWasm.Wasi.WasiShim` provides host implementations for a useful subset:

```csharp
var linker = new ComponentLinker(new WasmStore());
new DotWasm.Wasi.WasiShim().Register(linker);   // stdout/stderr/clocks/random/env/exit
var inst = linker.Instantiate(component);
inst.Invoke("run");                              // println! goes to Console
```

Implemented: cli stdout/stderr/stdin, io/streams (output-stream write), clocks
(wall + monotonic), random, cli environment/exit. Other WASI interfaces (full filesystem,
sockets, io/poll) are left unimplemented and only fault if a component actually calls them.

## Not supported

Out of scope in the core runtime: async/streams/futures, value imports/exports, component
`start`, general arbitrary nested components beyond the wit-bindgen export shim, and the
WASI interfaces the shim doesn't cover.
