#!/usr/bin/env bash
# The app must ship the exact CLI and daemon bytes npm published for this version. Downloads the
# platform package from the registry (or takes --tarball for tests) and compares both hashes.
# Usage: verify-npm-trio.sh <version> <kcap.sha256-file> <daemon.sha256-file> [--tarball <path>]
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"

version="${1:?usage: verify-npm-trio.sh <version> <kcap.sha256-file> <daemon.sha256-file> [--tarball <path>]}"
cli_digest="$(tr -d '[:space:]' < "${2:?kcap.sha256 file}")"
daemon_digest="$(tr -d '[:space:]' < "${3:?daemon.sha256 file}")"
tarball="${5:-}"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

if [ "${4:-}" != "--tarball" ]; then
  (cd "$tmp" && npm pack "@kurrent/kcap-darwin-arm64@$version" --registry https://registry.npmjs.org --pack-destination "$tmp" >/dev/null)
  tarball="$(ls "$tmp"/*.tgz | head -1)"
fi
[ -f "$tarball" ] || { echo "no tarball for @kurrent/kcap-darwin-arm64@$version" >&2; exit 1; }

mkdir -p "$tmp/x" && tar -xzf "$tarball" -C "$tmp/x"
actual_cli="$(sha256_of "$tmp/x/package/bin/kcap")"
actual_daemon="$(sha256_of "$tmp/x/package/bin/kcap-daemon")"
[ "$actual_cli" = "$cli_digest" ] || { echo "npm kcap hashes to $actual_cli, artifact recorded $cli_digest" >&2; exit 1; }
[ "$actual_daemon" = "$daemon_digest" ] || { echo "npm kcap-daemon hashes to $actual_daemon, artifact recorded $daemon_digest" >&2; exit 1; }
echo "npm @kurrent/kcap-darwin-arm64@$version carries the artifact's kcap and kcap-daemon"
