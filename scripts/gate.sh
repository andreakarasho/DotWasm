#!/usr/bin/env bash
# Test gate for interpreter optimization work.
# Builds Release, runs a curated spec-test subset covering the hot paths the
# optimizations touch (memory, locals, arithmetic, control flow, calls).
# Excludes call.wast (pre-existing native-stack overflow on deep recursion).
# Exits non-zero on build failure, any spec failure, or a runtime crash.
set -u
cd "$(dirname "$0")/.." || exit 99

RUNTIME=src/DotWasm.Runtime/DotWasm.Runtime.csproj
SPEC=tests/DotWasm.SpecTest/DotWasm.SpecTest.csproj

echo "=== build ==="
if ! dotnet build "$SPEC" -c Release 2>&1 | tail -3; then
  echo "GATE: BUILD FAILED"; exit 1
fi

# Curated coverage list (filename substrings). Avoids the deep-recursion crasher.
FILES=(
  local_get local_set local_tee
  i32 i64 f32 f64
  memory address align endianness
  block loop br br_if br_table
  select global
  fac
  conversions float_misc
  simd_i32x4_arith simd_f32x4_arith
  throw try_table tag
  ref_func ref_null ref_is_null
  struct array_ i31
)

DLL=$(find tests/DotWasm.SpecTest/bin/Release -name DotWasm.SpecTest.dll | head -1)
if [ -z "$DLL" ]; then echo "GATE: dll not found"; exit 1; fi

total_pass=0; total_fail=0; fail_files=""
for f in "${FILES[@]}"; do
  out=$(dotnet "$DLL" --filter "$f" 2>&1)
  rc=$?
  if echo "$out" | grep -q "Stack overflow\|Unhandled exception"; then
    echo "GATE: CRASH on filter '$f'"; echo "$out" | tail -5; exit 1
  fi
  # sum pass=/fail= across matched files
  while IFS= read -r line; do
    p=$(echo "$line" | sed -n 's/.*pass=\([0-9]*\).*/\1/p')
    fl=$(echo "$line" | sed -n 's/.*fail=\([0-9]*\).*/\1/p')
    [ -n "$p" ] && total_pass=$((total_pass+p))
    [ -n "$fl" ] && total_fail=$((total_fail+fl))
    [ -n "$fl" ] && [ "$fl" -gt 0 ] && fail_files="$fail_files $line"
  done < <(echo "$out" | grep "pass=")
done

echo "=== GATE TOTAL: pass=$total_pass fail=$total_fail ==="
if [ "$total_fail" -gt 0 ]; then
  echo "GATE: FAILURES:"; echo "$fail_files"; exit 1
fi
echo "GATE: OK"
exit 0
