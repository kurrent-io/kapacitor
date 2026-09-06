#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/promote-desktop-aliases.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

manifest() { # <file> <versions...>  — Velopack's releases.<channel>.json shape
  local file="$1"; shift
  { printf '{"Assets":['; local first=1
    for v in "$@"; do
      [ $first -eq 1 ] || printf ','; first=0
      printf '{"PackageId":"KurrentCapacitor","Version":"%s","Type":"Full","FileName":"KurrentCapacitor-%s-osx-arm64-full.nupkg","SHA1":"","SHA256":"","Size":1}' "$v" "$v"
    done
    printf ']}'; } > "$file"
}

fail=0
assert() { # <label> <manifest> <candidate> <want-output> <want-rc>
  local got rc; set +e; got="$(bash "$sh" "$2" "$3" 2>/dev/null)"; rc=$?; set -e
  if [ "$got" != "$4" ] || [ "$rc" != "$5" ]; then echo "FAIL: $1 -> out='$got' rc=$rc (want '$4' rc=$5)"; fail=1; fi
}

manifest "$tmp/m1.json" "0.12.0-beta.1" "0.12.0-beta.2"
assert "newest beta moves the beta alias only"      "$tmp/m1.json" "0.12.0-beta.2" "beta" 0
assert "older beta published late moves nothing"    "$tmp/m1.json" "0.12.0-beta.1" ""     0

manifest "$tmp/m2.json" "0.12.0-beta.2" "0.12.0"
assert "first stable moves both aliases"            "$tmp/m2.json" "0.12.0" $'beta\nstable' 0

manifest "$tmp/m3.json" "0.12.0" "0.13.0-beta.1" "0.12.2"
assert "stable patch below a beta moves stable only" "$tmp/m3.json" "0.12.2" "stable" 0
assert "beta above the stable moves beta only"       "$tmp/m3.json" "0.13.0-beta.1" "beta" 0

assert "candidate missing from the manifest fails"  "$tmp/m3.json" "9.9.9" "" 1

[ "$fail" -eq 0 ] && echo "ok" || exit 1
