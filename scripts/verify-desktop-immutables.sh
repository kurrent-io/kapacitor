#!/usr/bin/env bash
# Inside the publish concurrency group, before any upload: every immutable object of this version
# that already exists in the bucket must be byte-identical to the artifact about to be published.
# Identical bytes are a retry; different bytes mean a second build of the same version won a race,
# and this run must not overwrite what clients may already hold.
# Usage: verify-desktop-immutables.sh <local-dir> <version> <fetch-script>
#   <fetch-script> <object-name> <out-file> must exit 0 on a successful fetch, 44 when the object
#   does not exist, and any other code on an error the caller cannot tell apart from "not
#   published" — a transient fetch failure must never read as "safe to publish".
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"

local_dir="${1:?usage: verify-desktop-immutables.sh <local-dir> <version> <fetch-script>}"
version="${2:?usage: verify-desktop-immutables.sh <local-dir> <version> <fetch-script>}"
fetch="${3:?usage: verify-desktop-immutables.sh <local-dir> <version> <fetch-script>}"

names=(
  "KurrentCapacitor-$version-osx-arm64-full.nupkg"
  "KurrentCapacitor-$version-osx-arm64-delta.nupkg"
  "Kurrent-Capacitor-$version-osx-arm64.dmg"
)
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
for name in "${names[@]}"; do
  [ -f "$local_dir/$name" ] || continue
  set +e
  "$fetch" "$name" "$tmp/$name" >/dev/null
  fetch_rc=$?
  set -e
  if [ "$fetch_rc" -eq 44 ]; then
    echo "$name: not published yet"; continue
  elif [ "$fetch_rc" -ne 0 ]; then
    echo "$name: could not fetch the published copy (fetch exit $fetch_rc); refusing to publish" >&2
    exit 1
  fi
  local_hash="$(sha256_of "$local_dir/$name")"; remote_hash="$(sha256_of "$tmp/$name")"
  if [ "$local_hash" != "$remote_hash" ]; then
    echo "$name is already published with different bytes (remote $remote_hash, local $local_hash); refusing to overwrite an immutable object — cut a new version" >&2
    exit 1
  fi
  echo "$name: already published with identical bytes"
done
echo "immutable objects for $version are consistent"
