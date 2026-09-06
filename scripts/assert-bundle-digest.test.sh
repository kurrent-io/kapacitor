#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/assert-bundle-digest.sh"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

make_bundle() { # <dir> <daemon-content> <embedded-digest-or-empty>
  local app="$1"; mkdir -p "$app/Contents/MacOS"
  printf '%s' "$2" > "$app/Contents/MacOS/kcap-daemon"
  { printf 'prefix-bytes'; [ -n "$3" ] && python3 -c 'import sys; sys.stdout.buffer.write(sys.argv[1].encode("utf-16-le"))' "$3"; printf 'suffix'; } > "$app/Contents/MacOS/kcap"
}

fail=0
assert() { # <label> <want-rc> <bundle> <digest-file>
  local rc; set +e; bash "$sh" "$3" "$4" >/dev/null 2>&1; rc=$?; set -e
  if [ "$rc" != "$2" ]; then echo "FAIL: $1 -> rc=$rc (want $2)"; fail=1; fi
}

make_bundle "$tmp/good.app" "daemon-bytes" ""
digest="$(sha256_of "$tmp/good.app/Contents/MacOS/kcap-daemon")"
printf '%s\n' "$digest" > "$tmp/daemon.sha256"
make_bundle "$tmp/good.app" "daemon-bytes" "$digest"
assert "matching pair" 0 "$tmp/good.app" "$tmp/daemon.sha256"

make_bundle "$tmp/swapped.app" "different-daemon-bytes" "$digest"
assert "substituted daemon" 1 "$tmp/swapped.app" "$tmp/daemon.sha256"

placeholder="$(printf '0%.0s' $(seq 1 64))"
make_bundle "$tmp/placeholder.app" "daemon-bytes" "$placeholder"
assert "placeholder cli" 1 "$tmp/placeholder.app" "$tmp/daemon.sha256"

make_bundle "$tmp/unembedded.app" "daemon-bytes" ""
assert "cli without the digest" 1 "$tmp/unembedded.app" "$tmp/daemon.sha256"

[ "$fail" -eq 0 ] && echo "ok" || exit 1
