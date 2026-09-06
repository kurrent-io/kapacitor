#!/usr/bin/env bash
# Asserts the packed bundle still carries the daemon-digest invariant: the daemon inside hashes to
# the digest the release recorded, and the CLI inside embeds that digest (NativeAOT stores a C#
# string constant as UTF-16LE) rather than the all-zero placeholder a dev build carries.
# Usage: assert-bundle-digest.sh <bundle.app> <daemon.sha256-file>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"

bundle="${1:?usage: assert-bundle-digest.sh <bundle.app> <daemon.sha256-file>}"
digest_file="${2:?usage: assert-bundle-digest.sh <bundle.app> <daemon.sha256-file>}"
expected="$(tr -d '[:space:]' < "$digest_file")"
[[ "$expected" =~ ^[0-9a-f]{64}$ ]] || { echo "recorded digest is not 64 hex chars: '$expected'" >&2; exit 1; }

daemon="$bundle/Contents/MacOS/kcap-daemon"
cli="$bundle/Contents/MacOS/kcap"
[ -f "$daemon" ] || { echo "missing $daemon" >&2; exit 1; }
[ -f "$cli" ] || { echo "missing $cli" >&2; exit 1; }

actual="$(sha256_of "$daemon")"
[ "$actual" = "$expected" ] || { echo "packed daemon hashes to $actual, recorded digest is $expected" >&2; exit 1; }

placeholder="$(printf '0%.0s' $(seq 1 64))"
python3 - "$cli" "$expected" "$placeholder" <<'PY'
import sys
data = open(sys.argv[1], "rb").read()
expected = sys.argv[2].encode("utf-16-le")
placeholder = sys.argv[3].encode("utf-16-le")
if expected not in data:
    sys.stderr.write("packed kcap does not embed the recorded daemon digest\n"); sys.exit(1)
if placeholder in data:
    sys.stderr.write("packed kcap still embeds the placeholder digest\n"); sys.exit(1)
PY
echo "packed daemon matches the recorded digest and packed kcap embeds it"
