#!/usr/bin/env bash
# package-navmeshes.sh - verify a folder of navmeshes against world/navmesh-manifest.sha256 and pack
# them into dist/daoc-navmeshes-<set>.tar.xz (one GitHub release asset, ~500 MB).
#
#   tools/package-navmeshes.sh <folder with zone*.nav>
#
# The archive is also split into ~90 MB parts in world/navmesh/ (kept in the source, under GitHub's
# 100 MB file limit); tools/build-release.sh joins them. The raw .nav files are never committed.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MANIFEST="$REPO/world/navmesh-manifest.sha256"
SET="classic-si-2"
SRC=${1:?"usage: $0 <folder with zone*.nav>"}
OUT="$REPO/dist/daoc-navmeshes-$SET.tar.xz"

echo "Verifying $(grep -c '\.nav$' "$MANIFEST") navmeshes in $SRC ..."
(cd "$SRC" && grep -v '^#' "$MANIFEST" | sha256sum --check --quiet) || { echo "package-navmeshes: $SRC does not match $MANIFEST" >&2; exit 1; }

mkdir -p "$REPO/dist"
work=$(mktemp -d "$REPO/dist/.navmesh.XXXXXX")
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/navmesh"
grep -v '^#' "$MANIFEST" | awk '{print $2}' | while read -r f; do ln "$SRC/$f" "$work/navmesh/$f" 2>/dev/null || cp "$SRC/$f" "$work/navmesh/$f"; done
cp "$MANIFEST" "$work/navmesh/MANIFEST.sha256"
echo "Packing (xz, multi-threaded) ..."
tar -C "$work" -cf - navmesh | xz -6 -T0 > "$OUT.tmp"
mv "$OUT.tmp" "$OUT"
sha256sum "$OUT" | awk '{print $1}' > "$OUT.sha256"
rm -f "$REPO/world/navmesh/daoc-navmeshes-$SET.tar.xz".part-*
mkdir -p "$REPO/world/navmesh"
(cd "$REPO/world/navmesh" && split -b 90M -d -a 2 "$OUT" "daoc-navmeshes-$SET.tar.xz.part-")
cp "$OUT.sha256" "$REPO/world/navmesh/"
echo "Done: $OUT ($(du -h "$OUT" | cut -f1)), and its parts in world/navmesh/"
