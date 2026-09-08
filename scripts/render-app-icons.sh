#!/usr/bin/env bash
# Renders src/Capacitor.App/Assets/kcap-icon.svg into the committed kcap-icon.png (512 px) and
# kcap-icon.icns. Needs rsvg-convert (brew install librsvg) and macOS's iconutil; CI never runs it.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
assets="$here/../src/Capacitor.App/Assets"
svg="$assets/kcap-icon.svg"

command -v rsvg-convert >/dev/null || { echo "rsvg-convert not found (brew install librsvg)" >&2; exit 1; }
command -v iconutil >/dev/null || { echo "iconutil not found (macOS only)" >&2; exit 1; }

rsvg-convert -w 512 -h 512 "$svg" -o "$assets/kcap-icon.png"

iconset="$(mktemp -d)/kcap-icon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  rsvg-convert -w "$size" -h "$size" "$svg" -o "$iconset/icon_${size}x${size}.png"
  rsvg-convert -w $((size * 2)) -h $((size * 2)) "$svg" -o "$iconset/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$iconset" -o "$assets/kcap-icon.icns"
rm -rf "$(dirname "$iconset")"
echo "rendered $assets/kcap-icon.png and $assets/kcap-icon.icns"
