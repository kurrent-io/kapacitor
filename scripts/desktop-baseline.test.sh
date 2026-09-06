#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/desktop-baseline.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

make_pkg() { # <dir> <version>  — the real download layout: one full nupkg, no manifest beside it
  local dir="$1" v="$2" work; work="$(mktemp -d)"
  printf '<?xml version="1.0"?><package><metadata><id>KurrentCapacitor</id><version>%s</version></metadata></package>' "$v" > "$work/KurrentCapacitor.nuspec"
  (cd "$work" && zip -q "$dir/KurrentCapacitor-$v-osx-arm64-full.nupkg" KurrentCapacitor.nuspec)
  rm -rf "$work"
}

fail=0
count() { find "$1" -name '*-full.nupkg' | wc -l | tr -d ' '; }

d="$tmp/lower"; mkdir -p "$d"; make_pkg "$d" "0.12.1"
bash "$sh" "$d" "0.12.2" >/dev/null
[ "$(count "$d")" = "1" ] || { echo "FAIL: lower baseline should be kept"; fail=1; }

d="$tmp/higher"; mkdir -p "$d"; make_pkg "$d" "0.13.0-beta.1"
bash "$sh" "$d" "0.12.2" >/dev/null
[ "$(count "$d")" = "0" ] || { echo "FAIL: higher baseline (beta above a stable patch) should be deleted"; fail=1; }

d="$tmp/equal"; mkdir -p "$d"; make_pkg "$d" "0.12.2"
bash "$sh" "$d" "0.12.2" >/dev/null
[ "$(count "$d")" = "0" ] || { echo "FAIL: equal baseline should be deleted"; fail=1; }

d="$tmp/empty"; mkdir -p "$d"
bash "$sh" "$d" "0.12.2" >/dev/null || { echo "FAIL: empty download should be a no-op"; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
