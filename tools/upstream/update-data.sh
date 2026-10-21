#!/usr/bin/env bash
# update-data.sh - bring an Offline DAoC release's world and navmesh changes into the repo, using
# the small game patch every release now ships (OfflineDAoC-v<ver>-game-patch.zip, ~40 MB).
#
#   tools/upstream/update-data.sh <version>        e.g. 0.37    (run inside the build box)
#
# 1. downloads the patch (cached in .cache/upstream-patches/);
# 2. world: builds our current world (patches 200-* and earlier 2NN-*), checks the release's
#    db-<ver>.json against it row by row, and writes world/patches/2NN-offline-<ver>.sql;
# 3. navmesh: puts the patch's navmesh files (checked against its patch.json) into the set and
#    stages only the files that changed (tools/navmesh-stage.sh).
# Safe to re-run: it reuses the same patch file name and recompresses nothing that is unchanged.
# Releases that skip a version need each patch applied in order (0.37, then 0.38, ...).
# Code is separate: tools/upstream/sync.sh.
set -euo pipefail

VER=${1:?"usage: $0 <version, e.g. 0.37>"}
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
CACHE="$REPO/.cache/upstream-patches"; mkdir -p "$CACHE"
ZIP="$CACHE/OfflineDAoC-v$VER-game-patch.zip"
URL="https://github.com/shadowofze/OfflineDAoC/releases/download/v${VER}b/OfflineDAoC-v$VER-game-patch.zip"
work="$(mktemp -d "$CACHE/work.XXXXXX")"; trap 'rm -rf "$work"' EXIT

[[ -s $ZIP ]] || { echo "== Downloading $URL"; curl -fL --retry 3 -o "$ZIP.tmp" "$URL" && mv "$ZIP.tmp" "$ZIP"; }

echo "== Extracting db-$VER.json and navmesh files (verified against patch.json)"
python3 -I - "$ZIP" "$VER" "$work" <<'PY'
import hashlib, json, sys, zipfile
zpath, ver, work = sys.argv[1:4]
z = zipfile.ZipFile(zpath); root = next(n for n in z.namelist() if n.endswith("patch.json")).rsplit("patch.json", 1)[0]
open(f"{work}/db.json", "wb").write(z.read(f"{root}db-{ver}.json"))
patch = json.loads(z.read(root + "patch.json"))
import os; os.makedirs(f"{work}/patch-nav", exist_ok=True)
for f in patch["Files"]:
    if f["Path"].startswith("runtime/server/navmesh/"):
        data = z.read(root + "files/" + f["Path"])
        if hashlib.sha256(data).hexdigest() != f["Sha256"]: sys.exit("navmesh file fails its patch.json hash: " + f["Path"])
        open(f"{work}/patch-nav/" + f["Path"].split("/")[-1], "wb").write(data); print("  navmesh:", f["Path"].split("/")[-1])
PY

# Patch name: reuse this version's file if it exists, else the next free 2NN.
shopt -s nullglob
same=("$REPO"/world/patches/2??-offline-"$VER".sql)
if (( ${#same[@]} )); then out=${same[0]}
else
  next=210
  for f in "$REPO"/world/patches/2??-offline-*.sql; do n=$(basename "$f"); n=$(( 10#${n:0:3} + 10 )); (( n > next )) && next=$n; done
  out="$REPO/world/patches/$next-offline-$VER.sql"
fi
shopt -u nullglob

echo "== Building our current world (without this version's patch)"
mkdir -p "$work/patches"
for p in "$REPO"/world/patches/2??-*.sql; do [[ $p == "$out" ]] || cp "$p" "$work/patches/"; done
dump=$("$REPO/tools/fetch-upstream-db.sh")
"$DOTNET" build "$REPO/src/WorldBuilder/WorldBuilder.csproj" -c Release -nologo -v quiet -clp:ErrorsOnly
"$DOTNET" "$REPO/src/WorldBuilder/bin/Release/net10.0/DaocWorldBuilder.dll" --source "$dump" --out "$work/world.sqlite" --patches "$work/patches" | grep -E '^World'

echo "== World delta"
python3 "$REPO/tools/world/apply-db-delta.py" "$work/db.json" "$work/world.sqlite" "$out"

echo "== Navmesh"
"$REPO/tools/navmesh-unpack.sh" "$work/nav" | tail -1
cp "$work"/patch-nav/* "$work/nav/" 2>/dev/null || true
"$REPO/tools/navmesh-stage.sh" "$work/nav"
echo
echo "Done. Review: git status. Then build (tools/build-release.sh), test, and note the sync in UPSTREAM.md."
