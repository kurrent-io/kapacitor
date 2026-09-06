#!/usr/bin/env bash
# Asserts a bundled kcap reports the expected version and satisfies the app's CLI floor.
# Usage: assert-app-cli-version.sh <kcap-binary> <expected-version> [floor]
# The floor defaults to KcapCliCompatibility.Floor, read from the app source.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/semver.sh
source "$here/lib/semver.sh"

kcap="${1:?usage: assert-app-cli-version.sh <kcap-binary> <expected-version> [floor]}"
expected="${2:?usage: assert-app-cli-version.sh <kcap-binary> <expected-version> [floor]}"
floor="${3:-}"
if [ -z "$floor" ]; then
  floor="$(grep -oE 'Floor = "[^"]+"' "$here/../src/Capacitor.App/Services/KcapCliCompatibility.cs" | sed -E 's/Floor = "([^"]+)"/\1/')"
fi
[ -n "$floor" ] || { echo "could not read KcapCliCompatibility.Floor" >&2; exit 1; }

output="$("$kcap" --version --no-update-check)"
lines="$(printf '%s\n' "$output" | grep -c . || true)"
[ "$lines" -eq 1 ] || { echo "expected one line from --version, got: $output" >&2; exit 1; }
[[ "$output" == kcap\ * ]] || { echo "unexpected --version output: $output" >&2; exit 1; }
actual="${output#kcap }"

if [ "$(semver_strip_build "$actual")" != "$(semver_strip_build "$expected")" ]; then
  echo "bundled kcap reports $actual, expected $expected" >&2; exit 1
fi
cmp_result="$(semver_cmp "$actual" "$floor")" || exit 1
if [ "$cmp_result" -lt 0 ]; then
  echo "bundled kcap $actual is below the app's CLI floor $floor" >&2; exit 1
fi
echo "bundled kcap $actual matches $expected and satisfies floor $floor"
