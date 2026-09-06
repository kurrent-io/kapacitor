#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=semver.sh
source "$here/semver.sh"

fail=0
cmp() {
  local got; got="$(semver_cmp "$1" "$2")"
  if [ "$got" != "$3" ]; then echo "FAIL: semver_cmp '$1' '$2' -> '$got' (want '$3')"; fail=1; fi
}
cmp "0.12.0"          "0.12.0"          0
cmp "0.12.1"          "0.12.0"          1
cmp "0.11.38"         "0.12.0-beta.1"   -1
cmp "0.12.0-beta.1"   "0.12.0"          -1   # release above its own prereleases
cmp "0.12.0-beta.2"   "0.12.0-beta.1"   1
cmp "0.12.0-beta.10"  "0.12.0-beta.9"   1    # numeric identifiers compare numerically
cmp "0.12.0-beta.1.5" "0.12.0-beta.1"   1    # more identifiers rank higher (MinVer height)
cmp "0.12.0-alpha.9"  "0.12.0-beta.1"   -1   # alphanumerics compare lexically
cmp "0.12.0-beta.0"   "0.12.0-beta.1"   -1
cmp "0.12.0+abc"      "0.12.0+def"      0    # build metadata ignored
cmp "0.13.0-beta.1"   "0.12.2"          1

pre() {
  if semver_is_prerelease "$1"; then got=yes; else got=no; fi
  if [ "$got" != "$2" ]; then echo "FAIL: semver_is_prerelease '$1' -> $got (want $2)"; fail=1; fi
}
pre "0.12.0-beta.1" yes
pre "0.12.0"        no
pre "0.12.0+sha"    no

[ "$(semver_strip_build "1.2.3+build.5")" = "1.2.3" ] || { echo "FAIL: strip_build"; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
