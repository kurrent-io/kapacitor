#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/assert-app-cli-version.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
fake="$tmp/kcap"
printf '#!/usr/bin/env bash\nprintf "%%s\\n" "$FAKE_VERSION_OUTPUT"\n' > "$fake"; chmod +x "$fake"

fail=0
assert() {
  local output="$1" expected="$2" want_rc="$3"
  local rc
  set +e
  FAKE_VERSION_OUTPUT="$output" bash "$sh" "$fake" "$expected" "0.12.0-beta.1" >/dev/null 2>&1
  rc=$?
  set -e
  if [ "$rc" != "$want_rc" ]; then echo "FAIL: output='$output' expected='$expected' -> rc=$rc (want $want_rc)"; fail=1; fi
}
assert "kcap 0.12.0-beta.2+abc"        "0.12.0-beta.2"        0   # match, build metadata ignored
assert "kcap 0.12.0-beta.1.7+abc"      "0.12.0-beta.1.7"      0   # height-suffixed beta is above the floor
assert "kcap 0.12.0-beta.2"            "0.12.0-beta.3"        1   # version mismatch
assert "kcap 0.11.38"                  "0.11.38"              1   # below the floor
assert "kcap 0.12.0-beta.0"            "0.12.0-beta.0"        1   # a prerelease of the floor is below it
assert $'kcap 0.12.0-beta.2\nextra'    "0.12.0-beta.2"        1   # more than one line
assert "0.12.0-beta.2"                 "0.12.0-beta.2"        1   # missing prefix

rc=0
FAKE_VERSION_OUTPUT="kcap 1.2" bash "$sh" "$fake" "1.2" "0.12.0-beta.1" >/dev/null 2>&1 || rc=$?
[ "$rc" -ne 0 ] || { echo "FAIL: malformed version core 'kcap 1.2' -> rc=0 (want non-zero)"; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
