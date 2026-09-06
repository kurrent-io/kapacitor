#!/usr/bin/env bash
# Creates a per-run keychain, imports the Developer ID Application certificate, and (when the
# App Store Connect key variables are set) stores the notarytool profile "kcap-notary" in it.
# Every secret is required to be non-empty: a tag never falls back to unsigned.
set -euo pipefail
: "${APPLE_CERTIFICATE_P12:?APPLE_CERTIFICATE_P12 (base64 .p12) is required}"
: "${APPLE_CERTIFICATE_PASSWORD:?APPLE_CERTIFICATE_PASSWORD is required}"
: "${RUNNER_TEMP:?RUNNER_TEMP is required}"

KEYCHAIN="$RUNNER_TEMP/kcap-signing.keychain-db"
KEYCHAIN_PASSWORD="$(openssl rand -hex 16)"
cert="$RUNNER_TEMP/kcap-cert.p12"
key=""
trap 'rm -f "$cert" "$key"' EXIT
printf '%s' "$APPLE_CERTIFICATE_P12" | base64 --decode > "$cert"

security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings -lut 21600 "$KEYCHAIN"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security import "$cert" -P "$APPLE_CERTIFICATE_PASSWORD" -A -t cert -f pkcs12 -k "$KEYCHAIN"
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security list-keychain -d user -s "$KEYCHAIN"

if [ -n "${APPLE_NOTARY_KEY_P8:-}" ]; then
  : "${APPLE_NOTARY_KEY_ID:?APPLE_NOTARY_KEY_ID is required with APPLE_NOTARY_KEY_P8}"
  : "${APPLE_NOTARY_ISSUER_ID:?APPLE_NOTARY_ISSUER_ID is required with APPLE_NOTARY_KEY_P8}"
  key="$RUNNER_TEMP/kcap-notary.p8"
  printf '%s' "$APPLE_NOTARY_KEY_P8" | base64 --decode > "$key"
  xcrun notarytool store-credentials kcap-notary --key "$key" --key-id "$APPLE_NOTARY_KEY_ID" --issuer "$APPLE_NOTARY_ISSUER_ID" --keychain "$KEYCHAIN"
fi

echo "KEYCHAIN=$KEYCHAIN" >> "${GITHUB_ENV:?GITHUB_ENV is required}"
