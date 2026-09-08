#!/usr/bin/env bash
# `vpk download` leaves the channel's highest full package in the releases dir (no manifest), and
# `vpk pack` refuses a version at or below that baseline. Keep the package only when it is strictly
# below the candidate; otherwise delete it so the pack runs full-only, without a delta.
# Usage: desktop-baseline.sh <releases-dir> <candidate-version>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/semver.sh
source "$here/lib/semver.sh"

dir="${1:?usage: desktop-baseline.sh <releases-dir> <candidate-version>}"
candidate="${2:?usage: desktop-baseline.sh <releases-dir> <candidate-version>}"

shopt -s nullglob
pkgs=("$dir"/*-full.nupkg)
shopt -u nullglob
if [ "${#pkgs[@]}" -eq 0 ]; then echo "no baseline package; packing full-only"; exit 0; fi
[ "${#pkgs[@]}" -eq 1 ] || { echo "expected one full package in $dir, found ${#pkgs[@]}" >&2; exit 1; }

pkg="${pkgs[0]}"
base="$(unzip -p "$pkg" '*.nuspec' | grep -oE '<version>[^<]+</version>' | head -1 | sed -E 's#</?version>##g')"
[ -n "$base" ] || { echo "no <version> in the nuspec of $pkg" >&2; exit 1; }

cmp_result="$(semver_cmp "$base" "$candidate")" || exit 1
if [ "$cmp_result" -lt 0 ]; then
  echo "baseline $base kept for delta generation against $candidate"
else
  rm -f "$pkg"
  echo "baseline $base is not below $candidate; discarded, packing full-only"
fi
