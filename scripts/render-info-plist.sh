#!/usr/bin/env bash
# Usage: render-info-plist.sh <version> <out-file>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
version="${1:?usage: render-info-plist.sh <version> <out-file>}"
out="${2:?usage: render-info-plist.sh <version> <out-file>}"
short="${version%%-*}"; short="${short%%+*}"
sed -e "s/{VERSION}/${version%%+*}/" -e "s/{SHORT_VERSION}/$short/" "$here/../src/Capacitor.App/Packaging/Info.plist" > "$out"
plutil -lint "$out" >/dev/null
