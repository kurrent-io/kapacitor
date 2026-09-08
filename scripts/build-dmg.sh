#!/usr/bin/env bash
# A plain drag-to-Applications DMG: the stapled bundle plus an Applications symlink.
# Usage: build-dmg.sh <bundle.app> <out.dmg>
set -euo pipefail
bundle="${1:?usage: build-dmg.sh <bundle.app> <out.dmg>}"
out="${2:?usage: build-dmg.sh <bundle.app> <out.dmg>}"
staging="$(mktemp -d)"; trap 'rm -rf "$staging"' EXIT
ditto "$bundle" "$staging/$(basename "$bundle")"
ln -s /Applications "$staging/Applications"
rm -f "$out"
hdiutil create -volname "Kurrent Capacitor" -srcfolder "$staging" -ov -format UDZO "$out" >/dev/null
echo "built $out"
