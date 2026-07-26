#!/usr/bin/env bash
# Decides whether a release build should proceed, given the commit this
# build is for, the commit npm already has on record for the exact
# version being released (empty string if the version has never been
# published), and whether that npm lookup itself was trustworthy. Pure
# decision logic only — network/registry lookups, retries, and any
# CI-specific error formatting live in the caller (release.yml).
#
# Exit 0 + "ok"        -> version never published, proceed.
# Exit 1 + "republish" -> already published from the SAME commit: npm
#                         forbids republishing a version even unchanged.
# Exit 1 + "moved"     -> already published from a DIFFERENT commit: the
#                         release tag was moved/re-cut after that version's
#                         npm artifact was published (the AI-1514 failure
#                         mode) — refuse.
# Exit 1 + "error"     -> the npm lookup did not complete successfully
#                         (network/registry/auth error, or anything the
#                         caller couldn't positively classify as a real
#                         "not found") — this guard's whole purpose is to
#                         refuse a moved/re-cut tag, so an inconclusive
#                         lookup must NEVER be treated as "brand new
#                         version". Fail closed instead.
set -euo pipefail
this_sha="${1:?usage: verify-release-not-recut.sh <this-sha> <published-sha-or-empty> [query-status: ok|error]}"
published_sha="${2:-}"
query_status="${3:-ok}"

if [ "$query_status" != "ok" ]; then
  echo "error"
  exit 1
fi

if [ -z "$published_sha" ]; then
  echo "ok"
  exit 0
fi

if [ "$published_sha" = "$this_sha" ]; then
  echo "republish"
  exit 1
fi

echo "moved"
exit 1
