#!/usr/bin/env bash
# Runs every scripts/*.test.sh and scripts/lib/*.test.sh; any failure fails the run.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
status=0
for t in "$here"/*.test.sh "$here"/lib/*.test.sh; do
  [ -e "$t" ] || continue
  printf '%s: ' "${t#"$here"/}"
  if bash "$t"; then :; else status=1; fi
done
exit "$status"
