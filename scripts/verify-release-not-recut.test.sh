#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/verify-release-not-recut.sh"

fail=0
assert() {
  local this="$1" published="$2" want_out="$3" want_rc="$4" query_status="${5:-ok}"
  local got rc
  set +e
  got="$(bash "$sh" "$this" "$published" "$query_status")"
  rc=$?
  set -e
  if [ "$got" != "$want_out" ] || [ "$rc" != "$want_rc" ]; then
    echo "FAIL: this='$this' published='$published' query_status='$query_status' -> out='$got' rc=$rc (want out='$want_out' rc=$want_rc)"
    fail=1
  fi
}

# Never published before -> proceed.
assert "abc1234" ""        "ok"        0
# Already published from the exact same commit (e.g. a harmless re-run) -> refuse.
assert "abc1234" "abc1234" "republish" 1
# Already published from a DIFFERENT commit -> the moved/re-cut-tag case (AI-1514). Refuse.
assert "abc1234" "def5678" "moved"     1
# Real-world reproduction: v0.11.5 published from e0b3d233..., tag later moved to 99b820e0.
assert "99b820e0000000000000000000000000000000" "e0b3d23397fc795117b1a2fccb64e172859ba5a1" "moved" 1
# The npm lookup itself errored (network/registry/auth failure, or anything
# the caller couldn't positively classify as a real "not found") -> the
# guard's whole purpose is refusing a moved/re-cut tag, so it must fail
# CLOSED rather than treat the ambiguity as "brand new version". The
# published-sha argument is irrelevant/ignored here — even an empty one
# (what a naively-swallowed npm error looks like) must still refuse.
assert "abc1234" ""        "error"     1 "error"

[ "$fail" -eq 0 ] && echo "ok" || exit 1
