#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/verify-npm-trio.sh"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

make_tarball() { # <out.tgz> <kcap-content> <daemon-content>
  local work; work="$(mktemp -d)"; mkdir -p "$work/package/bin"
  printf '%s' "$2" > "$work/package/bin/kcap"; printf '%s' "$3" > "$work/package/bin/kcap-daemon"
  tar -czf "$1" -C "$work" package; rm -rf "$work"
}
make_tarball "$tmp/match.tgz" "cli-bytes" "daemon-bytes"
printf 'cli-bytes' > "$tmp/kcap"; printf 'daemon-bytes' > "$tmp/kcap-daemon"
sha256_of "$tmp/kcap" > "$tmp/kcap.sha256"; sha256_of "$tmp/kcap-daemon" > "$tmp/daemon.sha256"
make_tarball "$tmp/other-daemon.tgz" "cli-bytes" "other-daemon"
make_tarball "$tmp/other-cli.tgz" "other-cli" "daemon-bytes"

fail=0
assert() { local rc; set +e; bash "$sh" "0.12.0-beta.2" "$tmp/kcap.sha256" "$tmp/daemon.sha256" --tarball "$2" >/dev/null 2>&1; rc=$?; set -e
  if [ "$rc" != "$3" ]; then echo "FAIL: $1 -> rc=$rc (want $3)"; fail=1; fi; }
assert "matching tarball passes"      "$tmp/match.tgz"        0
assert "different daemon fails"       "$tmp/other-daemon.tgz" 1
assert "different cli fails"          "$tmp/other-cli.tgz"    1
assert "missing tarball fails"        "$tmp/absent.tgz"       1
[ "$fail" -eq 0 ] && echo "ok" || exit 1
