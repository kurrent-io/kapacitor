#!/usr/bin/env bash
# Asserts the packed bundle still carries the daemon-digest invariant: the daemon inside hashes to
# the digest the release recorded, and the CLI inside embeds that digest rather than the all-zero
# placeholder a dev build carries. The embedding is proven through the CLI's own app-managed start
# gate, not by searching the binary: NativeAOT does not lay a string literal out as plain UTF-16 on
# disk, so bytes are not evidence, while the gate's verdict is. With KCAP_CONSENT_SEED_DEFAULT
# present the gate runs before anything is spawned and a mismatch, the placeholder included, exits
# 43 with daemon_start_reason=package_inconsistent. A copy beside a foreign daemon must be refused
# and the bundled pair must get past the gate; the empty seed makes a daemon that does spawn refuse
# to boot, and a stop follows either way.
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

# A short root: the daemon's socket path must fit sockaddr_un, and a macOS TMPDIR leaves no room.
work="$(mktemp -d /tmp/kcap-digest.XXXXXX)"
trap 'rm -rf "$work"' EXIT

# Runs the gated start for <kcap-binary>, captures both streams to <log>, prints the exit code.
gate() {
  local rc=0
  KCAP_CONSENT_SEED_DEFAULT= KCAP_DAEMONS_DIR="$work/daemons" KCAP_CONFIG_DIR="$work/config" DO_NOT_TRACK=1 \
    "$1" daemon start -d --name digest-probe >"$2" 2>&1 || rc=$?
  KCAP_DAEMONS_DIR="$work/daemons" KCAP_CONFIG_DIR="$work/config" DO_NOT_TRACK=1 \
    "$1" daemon stop --name digest-probe >/dev/null 2>&1 || true
  echo "$rc"
}

mkdir -p "$work/foreign"
cp "$cli" "$work/foreign/kcap"
printf 'not the bundled daemon' > "$work/foreign/kcap-daemon"
rc="$(gate "$work/foreign/kcap" "$work/foreign.log")"
if [ "$rc" != 43 ] || ! grep -qx 'daemon_start_reason=package_inconsistent' "$work/foreign.log"; then
  echo "packed kcap did not refuse a foreign daemon (exit $rc):" >&2; cat "$work/foreign.log" >&2; exit 1
fi

# Past the gate means the spawn happened: either line is printed only after it.
rc="$(gate "$cli" "$work/bundle.log")"
if [ "$rc" = 43 ] || grep -q 'daemon_start_reason=' "$work/bundle.log" \
   || ! grep -q -E "started \(PID|failed to start \(exit code" "$work/bundle.log"; then
  echo "packed kcap did not accept the bundled daemon (exit $rc):" >&2; cat "$work/bundle.log" >&2; exit 1
fi
echo "packed daemon matches the recorded digest and packed kcap accepts it through the start gate"
