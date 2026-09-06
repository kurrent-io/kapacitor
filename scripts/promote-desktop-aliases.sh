#!/usr/bin/env bash
# Decides which DMG aliases a just-published version may take, from the merged feed manifest:
# "beta" when it is the highest version of any kind, "stable" when it is stable and the highest
# stable one. Pure decision — the workflow does the copies. An older tag published late, or a
# re-run, therefore never regresses an alias.
# Usage: promote-desktop-aliases.sh <releases.json> <candidate-version>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/semver.sh
source "$here/lib/semver.sh"

manifest="${1:?usage: promote-desktop-aliases.sh <releases.json> <candidate-version>}"
candidate="${2:?usage: promote-desktop-aliases.sh <releases.json> <candidate-version>}"

versions="$(jq -r '.Assets[] | select(.Type == "Full") | .Version' "$manifest")"
grep -qxF "$candidate" <<<"$versions" || { echo "$candidate is not in $manifest" >&2; exit 1; }

highest_all=""; highest_stable=""
while IFS= read -r v; do
  [ -n "$v" ] || continue
  if [ -z "$highest_all" ]; then
    highest_all="$v"
  else
    cmp_result="$(semver_cmp "$v" "$highest_all")" || exit 1
    [ "$cmp_result" -gt 0 ] && highest_all="$v"
  fi
  if ! semver_is_prerelease "$v"; then
    if [ -z "$highest_stable" ]; then
      highest_stable="$v"
    else
      cmp_result="$(semver_cmp "$v" "$highest_stable")" || exit 1
      [ "$cmp_result" -gt 0 ] && highest_stable="$v"
    fi
  fi
done <<<"$versions"

[ "$candidate" = "$highest_all" ] && echo beta
if ! semver_is_prerelease "$candidate" && [ "$candidate" = "$highest_stable" ]; then echo stable; fi
exit 0
