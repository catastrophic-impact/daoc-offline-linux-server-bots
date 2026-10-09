#!/usr/bin/env bash
# build-release.sh - build the portable Linux server folder and tarball.
#
#   tools/build-release.sh            -> dist/daoc-server/ and dist/daoc-server-<version>-linux-x64.tar.gz
#
# Needs the .NET 10 SDK, cmake, g++ and curl (the build box has them). The result needs none of them:
# it bundles the .NET runtime, links the pathing library's C++ runtime statically, and contains a
# fresh Classic + SI world built from the pinned upstream OpenDAoC-Database plus world/patches.
# Navmeshes come from world/navmesh (installed into dist/daoc-server); no client files are included.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
VERSION="${VERSION:-$(git -C "$REPO" describe --tags --always 2>/dev/null || echo dev)}"
OUT="$REPO/dist/daoc-server"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

missing=()
[[ -x $DOTNET ]] && "$DOTNET" --list-sdks 2>/dev/null | grep -q '^10\.' || missing+=(".NET 10 SDK")
command -v cmake >/dev/null || missing+=("cmake")
command -v g++ >/dev/null || missing+=("g++")
command -v curl >/dev/null || missing+=("curl")
if ((${#missing[@]})); then
  echo "build-release: missing: ${missing[*]}" >&2
  echo "  Install them (see docs/BUILD.md), or run tools/setup-build-box.sh --build on the host" >&2
  echo "  to build inside a ready-made distrobox." >&2
  exit 1
fi

# The CoreServer project copies a local serverconfig.xml; the build only needs it to exist.
[[ -f $REPO/src/CoreServer/config/serverconfig.xml ]] ||
  cp "$REPO/src/CoreServer/config/serverconfig.example.xml" "$REPO/src/CoreServer/config/serverconfig.xml"

rm -rf "$OUT"
echo "== Publishing server (self-contained linux-x64)"
"$DOTNET" publish "$REPO/src/CoreServer/CoreServer.csproj" -c Release -r linux-x64 --self-contained true \
  -o "$OUT/bin" -nologo -v quiet -clp:ErrorsOnly

# daoc-admin shares bin/ (and its bundled runtime) with the server.
echo "== Publishing daoc-admin (TUI + CLI)"
"$DOTNET" publish "$REPO/src/DaocAdmin/DaocAdmin.csproj" -c Release -r linux-x64 --self-contained true \
  -o "$OUT/bin" -nologo -v quiet -clp:ErrorsOnly

echo "== Building native pathing library"
cmake -S "$REPO/src/Pathing/Detour" -B "$REPO/src/Pathing/Detour/build" -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build "$REPO/src/Pathing/Detour/build" >/dev/null
mkdir -p "$OUT/bin/lib"
# LibraryImport("lib/Detour") has a folder in its name, so .NET on Linux probes lib/Detour.so.
cp -L "$REPO/src/Pathing/Detour/build/libDetour.so" "$OUT/bin/lib/Detour.so"

# A post-build step copies translations into src/Release/ only; publish needs them too.
(cd "$REPO/src/GameServer/language" && find . -name '*.txt' -exec install -D -m 644 {} "$OUT/bin/languages/{}" \;)

echo "== Building the world (upstream OpenDAoC-Database + world/patches)"
dump=$("$REPO/tools/fetch-upstream-db.sh")
"$DOTNET" build "$REPO/src/WorldBuilder/WorldBuilder.csproj" -c Release -nologo -v quiet -clp:ErrorsOnly
mkdir -p "$OUT/defaults"
DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$DOTNET")")}" "$DOTNET" \
  "$REPO/src/WorldBuilder/bin/Release/net10.0/DaocWorldBuilder.dll" \
  --source "$dump" --out "$OUT/defaults/world.sqlite" --patches "$REPO/world/patches" | grep -E "^(Schema|World)"

# The server reads config/ next to its program and navmesh/ from its working directory (bin/).
rm -rf "$OUT/bin/config"
ln -s ../config "$OUT/bin/config"
ln -s ../navmesh "$OUT/bin/navmesh"
ln -s ../logs "$OUT/bin/logs"

cp "$REPO/deploy/serverconfig.template.xml" "$OUT/defaults/serverconfig.xml"
cp "$REPO/src/GameServer/config/logconfig.xml" "$REPO/src/GameServer/config/invalidnames.txt" "$OUT/defaults/"
cp "$REPO/deploy/daoc-server.sh" "$REPO/deploy/daoc-admin" "$REPO/deploy/navmesh-install.sh" "$REPO/deploy/release.conf" "$OUT/"
cp "$REPO/deploy/README-release.md" "$OUT/README.md"
echo "$VERSION" > "$OUT/VERSION"

# Navmeshes ship in the source as ~90 MB parts (world/navmesh/; under GitHub's 100 MB file limit).
# Join and verify them, and install them into the folder so dist/daoc-server is ready to start.
echo "== Navmeshes (world/navmesh)"
navset="daoc-navmeshes-classic-si-2.tar.xz"
nav="$REPO/dist/$navset"
want=$(<"$REPO/world/navmesh/$navset.sha256")
if [[ ! -f $nav || $(sha256sum "$nav" | cut -d' ' -f1) != "$want" ]]; then
  cat "$REPO/world/navmesh/$navset".part-* > "$nav.tmp"
  [[ $(sha256sum "$nav.tmp" | cut -d' ' -f1) == "$want" ]] || { echo "build-release: navmesh parts in world/navmesh are damaged" >&2; exit 1; }
  mv "$nav.tmp" "$nav"
fi
cp "$REPO/world/navmesh/$navset.sha256" "$nav.sha256"
DAOC_HOME="$OUT" "$OUT/navmesh-install.sh" --from-archive "$nav"

# The server tarball leaves the navmeshes out (they are the separate archive above).
tarball="$REPO/dist/daoc-server-$VERSION-linux-x64.tar.gz"
tar -C "$REPO/dist" --exclude='daoc-server/navmesh' -czf "$tarball" daoc-server
echo "== Done: $OUT"
echo "         $tarball ($(du -h "$tarball" | cut -f1))"
