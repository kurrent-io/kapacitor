#!/usr/bin/env bash
# Signs each file with hardened runtime, a secure timestamp and the given entitlements — the same
# flags Velopack uses, so pre-signed binaries and the outer bundle agree.
# Usage: sign-macos.sh <identity> <keychain> <entitlements.plist> <file...>
set -euo pipefail
identity="${1:?usage: sign-macos.sh <identity> <keychain> <entitlements> <file...>}"
keychain="${2:?usage: sign-macos.sh <identity> <keychain> <entitlements> <file...>}"
entitlements="${3:?usage: sign-macos.sh <identity> <keychain> <entitlements> <file...>}"
shift 3
[ -n "$identity" ] && [ -n "$keychain" ] || { echo "signing identity and keychain must be non-empty" >&2; exit 1; }
[ -f "$entitlements" ] || { echo "entitlements file not found: $entitlements" >&2; exit 1; }
[ "$#" -gt 0 ] || { echo "no files to sign" >&2; exit 1; }
for file in "$@"; do
  codesign --force --timestamp --options runtime --entitlements "$entitlements" --sign "$identity" --keychain "$keychain" "$file"
  codesign --verify --strict "$file"
done
