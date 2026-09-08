#!/usr/bin/env bash
# SemVer 2 precedence for the shapes MinVer produces (core, optional prerelease identifiers,
# optional +build). Sourced by the release scripts; semver.test.sh pins every rule.

semver_strip_build() { printf '%s' "${1%%+*}"; }

# Exit 0 when the version carries a prerelease part (build metadata ignored).
semver_is_prerelease() {
  local v; v="$(semver_strip_build "$1")"
  [[ "$v" == *-* ]]
}

# A version core is exactly three non-negative integers, no leading zeros.
semver_validate_core() {
  [[ "$1" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]
}

# Prints -1, 0 or 1 for a<b, a=b, a>b. Returns 2 and prints nothing when either
# core is malformed, so a caller can't mistake a refusal for a comparison.
semver_cmp() {
  local a b acore bcore apre="" bpre=""
  a="$(semver_strip_build "$1")"; b="$(semver_strip_build "$2")"
  acore="${a%%-*}"; bcore="${b%%-*}"
  [ "$acore" != "$a" ] && apre="${a#*-}"
  [ "$bcore" != "$b" ] && bpre="${b#*-}"

  semver_validate_core "$acore" || { echo "semver_cmp: invalid version core '$acore'" >&2; return 2; }
  semver_validate_core "$bcore" || { echo "semver_cmp: invalid version core '$bcore'" >&2; return 2; }

  local a1 a2 a3 b1 b2 b3
  IFS=. read -r a1 a2 a3 <<<"$acore"
  IFS=. read -r b1 b2 b3 <<<"$bcore"
  local x y
  for pair in "$a1:$b1" "$a2:$b2" "$a3:$b3"; do
    x="${pair%%:*}"; y="${pair##*:}"
    if (( x < y )); then echo -1; return; fi
    if (( x > y )); then echo 1; return; fi
  done

  if [ -z "$apre" ] && [ -z "$bpre" ]; then echo 0; return; fi
  if [ -z "$apre" ]; then echo 1; return; fi
  if [ -z "$bpre" ]; then echo -1; return; fi

  local -a ai bi
  IFS=. read -ra ai <<<"$apre"
  IFS=. read -ra bi <<<"$bpre"
  local n=${#ai[@]}; (( ${#bi[@]} > n )) && n=${#bi[@]}
  local i
  for ((i = 0; i < n; i++)); do
    x="${ai[i]-}"; y="${bi[i]-}"
    if [ -z "$x" ]; then echo -1; return; fi
    if [ -z "$y" ]; then echo 1; return; fi
    if [[ "$x" =~ ^[0-9]+$ && "$y" =~ ^[0-9]+$ ]]; then
      if (( x < y )); then echo -1; return; fi
      if (( x > y )); then echo 1; return; fi
    elif [[ "$x" =~ ^[0-9]+$ ]]; then echo -1; return
    elif [[ "$y" =~ ^[0-9]+$ ]]; then echo 1; return
    else
      if [[ "$x" < "$y" ]]; then echo -1; return; fi
      if [[ "$x" > "$y" ]]; then echo 1; return; fi
    fi
  done
  echo 0
}
