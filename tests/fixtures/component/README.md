# Component Model test fixtures

Binary `.wasm` components used by `tests/DotWasm.Component.Tests`.

Hand-written (via `wasm-tools parse <name>.wat`):
- `add.wasm`     — minimal: lifts a core `add(i32,i32)->i32` as `add(s32,s32)->s32`.
- `types.wasm`   — declares every value-type form (record/variant/list/...); identity func.
- `res.wasm`     — a resource type with a destructor.
- `imp.wasm`     — a component func import + `canon lower`.
- `strmem.wasm`  — string lift (indirect result) + string lower (realloc), bump allocator.

Real toolchain output (regenerate from `tests/fixtures/rust-component`):
- `ops.wasm` — `cargo component build --release` of the no_std Rust component.
              Exercises records, lists, variants, enum, flags, option, result, tuple,
              indirect params (>16 flat), f32/u64, and a resource. Copied from
              `target/wasm32-wasip1/release/compfix.wasm`.

`ops.wasm` is built `no_std` with an inline bump allocator so it has no WASI imports
(WASI is out of scope for this Component Model support).
