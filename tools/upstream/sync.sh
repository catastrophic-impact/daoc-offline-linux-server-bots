#!/usr/bin/env bash
# sync.sh - bring an upstream's changes into this repo, mapped onto our folder layout.
#
#   tools/upstream/sync.sh <name> report <to>          list files that changed from SYNCED to <to>, with our paths
#   tools/upstream/sync.sh <name> check  <to>          dry-run: do the patches apply cleanly?
#   tools/upstream/sync.sh <name> apply  <to> [--bots|--core]
#                                                       apply to the working tree (no commit)
#
# <name> is a block in upstreams.conf (offlinedaoc, opendaoc). <to> is a tag or sha.
# The upstream is cloned into $UPSTREAM_CACHE (default ~/.cache/daoc-upstreams/<name>) on first use.
# --bots applies only the Bots module (src/Bots); --core applies everything else. Default: both.
# `apply` needs a clean working tree so the result is one reviewable diff. It stops at the first
# failing file group and leaves rejects as .rej files; nothing is committed.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
NAME="${1:-}"; MODE="${2:-}"; TO="${3:-}"; SCOPE="${4:-both}"
[[ -n $NAME && -n $MODE && -n $TO ]] || { sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }
[[ $MODE == report || $MODE == check || $MODE == apply ]] || { echo "mode must be report, check or apply" >&2; exit 2; }
[[ $SCOPE == both || $SCOPE == --bots || $SCOPE == --core ]] || { echo "scope must be --bots or --core" >&2; exit 2; }

# shellcheck source=upstreams.conf
source "$REPO/tools/upstream/upstreams.conf"
url_var="${NAME}_URL"; synced_var="${NAME}_SYNCED"; map_var="${NAME}_MAP[@]"
[[ -n ${!url_var:-} ]] || { echo "unknown upstream '$NAME' (see tools/upstream/upstreams.conf)" >&2; exit 2; }
URL="${!url_var}"; FROM="${!synced_var}"; MAP=("${!map_var}")

# Without a SYNCED sha there is nothing to diff from: show the folder map and stop (no clone).
if [[ -z $FROM ]]; then
  echo "== $NAME has no SYNCED sha yet. Folder map ($URL):"
  for pair in "${MAP[@]}"; do echo "  ${pair%%:*}  ->  ${pair#*:}"; done
  exit 0
fi

CACHE="${UPSTREAM_CACHE:-$HOME/.cache/daoc-upstreams}/$NAME"
if [[ ! -d $CACHE/.git ]]; then
  mkdir -p "$(dirname "$CACHE")"; git clone -q "$URL" "$CACHE"
else
  git -C "$CACHE" fetch -q --tags origin
fi
git -C "$CACHE" rev-parse -q --verify "$TO^{commit}" >/dev/null || { echo "$TO not found in $URL" >&2; exit 1; }
TO_SHA="$(git -C "$CACHE" rev-parse --short "$TO^{commit}")"

# Longest prefix first, so GameServer/bots wins over GameServer.
mapfile -t SORTED < <(printf '%s\n' "${MAP[@]}" | awk '{ print length($0) "\t" $0 }' | sort -rn | cut -f2-)

# Split by destination: the module (src/Bots) and everything else. Exclusions keep the two disjoint.
bots_prefix=""; bots_dest=""
for pair in "${SORTED[@]}"; do [[ ${pair#*:} == src/Bots ]] && { bots_prefix="${pair%%:*}"; bots_dest="src/Bots"; }; done

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

make_patch() { # <prefix> <dest> <out> [exclude-prefix...]
  local prefix="$1" dest="$2" out="$3"; shift 3
  local spec=("$prefix")
  for ex in "$@"; do spec+=(":(exclude)$ex"); done
  local strip=$(( $(tr -cd / <<<"$prefix" | wc -c) + 2 ))   # a/ + each path component
  git -C "$CACHE" diff --binary "$FROM" "$TO_SHA" -- "${spec[@]}" >"$out"
  echo "$strip $dest"
}

declare -a GROUPS_ARGS=()   # "patchfile|strip|dest"
i=0
for pair in "${SORTED[@]}"; do
  prefix="${pair%%:*}"; dest="${pair#*:}"
  [[ $dest == src/Bots && $SCOPE == --core ]] && continue
  [[ $dest != src/Bots && $SCOPE == --bots ]] && continue
  excludes=()
  for other in "${SORTED[@]}"; do
    op="${other%%:*}"; [[ $op != "$prefix" && $op == "$prefix"/* ]] && excludes+=("$op")
  done
  out="$work/$((i++)).patch"
  read -r strip d < <(make_patch "$prefix" "$dest" "$out" "${excludes[@]}")
  [[ -s $out ]] && GROUPS_ARGS+=("$out|$strip|$d|$prefix")
done

if (( ${#GROUPS_ARGS[@]} == 0 )); then echo "No upstream changes in mapped folders between $FROM and $TO_SHA."; exit 0; fi

if [[ $MODE == report ]]; then
  echo "== $NAME $FROM -> $TO_SHA (mapped to our paths)"
  for g in "${GROUPS_ARGS[@]}"; do
    IFS='|' read -r patch strip dest prefix <<<"$g"
    git apply --numstat "$patch" | awk -v s="$strip" -v d="$dest" '{ n=split($3, p, "/"); path=p[s]; for(k=s+1;k<=n;k++) path=path "/" p[k]; printf "%6s +%-6s -%-6s %s/%s\n", "", $1, $2, d, path }'
  done
  exit 0
fi

if [[ $MODE == apply ]] && [[ -n $(git -C "$REPO" status --porcelain) ]]; then
  echo "Working tree is not clean; commit or stash first so the sync is one reviewable diff." >&2; exit 1
fi

rc=0
merged=(); conflicted=()
for g in "${GROUPS_ARGS[@]}"; do
  IFS='|' read -r patch strip dest prefix <<<"$g"
  flags=(-p"$strip" --directory="$dest" --whitespace=nowarn)
  if [[ $MODE == check ]]; then
    if git -C "$REPO" apply --check "${flags[@]}" "$patch" 2>"$work/err"; then echo "ok      $prefix -> $dest"
    else
      echo "CONFLICT $prefix -> $dest  (apply would fall back to a 3-way merge for:)"
      sed -n 's/^error: \(.*\): patch does not apply$/    \1/p' "$work/err"; rc=1
    fi
    continue
  fi
  echo "applying $prefix -> $dest"
  # Files our edits keep from patching cleanly: apply everything else, then 3-way merge those
  # (base = upstream at the synced sha, theirs = the new version, ours = our file).
  mapfile -t bad < <(git -C "$REPO" apply --check "${flags[@]}" "$patch" 2>&1 | sed -n 's/^error: \(.*\): patch does not apply$/\1/p')
  excludes=(); for f in "${bad[@]}"; do excludes+=(--exclude="$f"); done
  git -C "$REPO" apply "${flags[@]}" "${excludes[@]}" "$patch" || { echo "apply failed for $dest" >&2; rc=1; continue; }
  for f in "${bad[@]}"; do
    up="$prefix/${f#"$dest"/}"
    git -C "$CACHE" cat-file -e "$FROM:$up" 2>/dev/null || { echo "  cannot 3-way $f (not in upstream $FROM)" >&2; conflicted+=("$f"); continue; }
    git -C "$CACHE" show "$FROM:$up" >"$work/base"; git -C "$CACHE" show "$TO_SHA:$up" >"$work/theirs"
    if git merge-file -L ours -L "upstream-$FROM" -L "upstream-$TO_SHA" "$REPO/$f" "$work/base" "$work/theirs"; then merged+=("$f"); else conflicted+=("$f"); fi
  done
done
(( ${#merged[@]} )) && { echo "Merged cleanly with our edits kept (review these):"; printf '  %s\n' "${merged[@]}"; }
if (( ${#conflicted[@]} )); then
  echo "CONFLICTS to resolve by hand (look for <<<<<<< ours markers):"; printf '  %s\n' "${conflicted[@]}"; rc=1
fi
(( rc == 0 )) && [[ $MODE == apply ]] && echo "Applied. Build, test, then update UPSTREAM.md, ${NAME}_SYNCED in tools/upstream/upstreams.conf and AdminService.OfflineDaocVersion."
exit $rc
