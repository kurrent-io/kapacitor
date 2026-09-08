#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/assert-bundle-digest.sh"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

# A stand-in kcap with the gate's contract: with KCAP_CONSENT_SEED_DEFAULT present it hashes its
# sibling kcap-daemon against the digest baked into it, refusing a mismatch or a placeholder with
# exit 43 and the reason line, and a spawn ends as an immediate child exit. Mode "ungated" never
# refuses; mode "locked" fails before the gate.
make_bundle() { # <dir> <daemon-content> <embedded-digest> [mode]
  local app="$1" mode="${4:-gated}"; mkdir -p "$app/Contents/MacOS"
  printf '%s' "$2" > "$app/Contents/MacOS/kcap-daemon"
  cat > "$app/Contents/MacOS/kcap" <<EOF
#!/usr/bin/env bash
source "$here/lib/hash.sh"
[ "\$1" = daemon ] || exit 2
[ "\$2" = stop ] && exit 0
[ "$mode" = locked ] && { echo "Another kcap daemon start is already in progress" >&2; exit 1; }
if [ "$mode" = gated ] && [ -n "\${KCAP_CONSENT_SEED_DEFAULT+x}" ]; then
  actual="\$(sha256_of "\$(dirname "\$0")/kcap-daemon")"
  if [ "$3" = "$(printf '0%.0s' $(seq 1 64))" ] || [ "\$actual" != "$3" ]; then
    echo "daemon_start_reason=package_inconsistent" >&2; exit 43
  fi
fi
echo "Daemon 'digest-probe' failed to start (exit code 0)." >&2; exit 1
EOF
  chmod +x "$app/Contents/MacOS/kcap"
}

fail=0
assert() { # <label> <want-rc> <bundle> <digest-file>
  local rc; set +e; bash "$sh" "$3" "$4" >/dev/null 2>&1; rc=$?; set -e
  if [ "$rc" != "$2" ]; then echo "FAIL: $1 -> rc=$rc (want $2)"; fail=1; fi
}

printf '%s' "daemon-bytes" > "$tmp/daemon"
digest="$(sha256_of "$tmp/daemon")"
printf '%s\n' "$digest" > "$tmp/daemon.sha256"
placeholder="$(printf '0%.0s' $(seq 1 64))"
stale="$(printf 'a%.0s' $(seq 1 64))"

make_bundle "$tmp/good.app" "daemon-bytes" "$digest"
assert "matching pair" 0 "$tmp/good.app" "$tmp/daemon.sha256"

make_bundle "$tmp/swapped.app" "different-daemon-bytes" "$digest"
assert "substituted daemon" 1 "$tmp/swapped.app" "$tmp/daemon.sha256"

make_bundle "$tmp/placeholder.app" "daemon-bytes" "$placeholder"
assert "placeholder cli" 1 "$tmp/placeholder.app" "$tmp/daemon.sha256"

make_bundle "$tmp/stale.app" "daemon-bytes" "$stale"
assert "cli embedding another daemon's digest" 1 "$tmp/stale.app" "$tmp/daemon.sha256"

make_bundle "$tmp/ungated.app" "daemon-bytes" "$digest" ungated
assert "cli that never refuses" 1 "$tmp/ungated.app" "$tmp/daemon.sha256"

make_bundle "$tmp/locked.app" "daemon-bytes" "$digest" locked
assert "cli that fails before the gate" 1 "$tmp/locked.app" "$tmp/daemon.sha256"

[ "$fail" -eq 0 ] && echo "ok" || exit 1
