#!/usr/bin/env bash
# dev-deploy.sh - the fast loop for bot and server work: rebuild only the server assemblies, copy
# them into a dev server folder, and restart it if it was running. Takes well under a minute,
# instead of a full release build.
#
#   tools/dev-deploy.sh [server folder]    default: $DAOC_HOME, else ~/next-test/daoc-server
#
# Run it where the .NET SDK is (the build box). The server folder must come from a release build
# (tools/build-release.sh); this only replaces the server's own DLLs in its bin/.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="${1:-${DAOC_HOME:-$HOME/next-test/daoc-server}}"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
die() { echo "dev-deploy: $*" >&2; exit 1; }

[[ -x $DEST/bin/CoreServer ]] || die "no server folder at $DEST (unpack a release there first)"
[[ -f $REPO/src/CoreServer/config/serverconfig.xml ]] ||
  cp "$REPO/src/CoreServer/config/serverconfig.example.xml" "$REPO/src/CoreServer/config/serverconfig.xml"

echo "== Building server assemblies"
"$DOTNET" build "$REPO/src/GameServer/GameServer.csproj" -c Release -nologo -v quiet -clp:ErrorsOnly

was_running=false
if [[ $("$DEST/daoc-server.sh" status) == running* ]]; then
  was_running=true
  echo "== Stopping the server (it saves first)"
  "$DEST/daoc-server.sh" stop
fi

echo "== Copying into $DEST/bin"
for name in GameServer CoreBase CoreDatabase Admin.Protocol; do
  for ext in dll pdb; do
    src="$REPO/src/Release/lib/$name.$ext"
    [[ -f $src ]] && cp -f "$src" "$DEST/bin/"
  done
done

if $was_running; then
  echo "== Restarting in the background (log: logs/console.out; reopen the admin screen with ./daoc-admin)"
  mkdir -p "$DEST/logs"
  setsid "$DEST/daoc-server.sh" start --console </dev/null >"$DEST/logs/console.out" 2>&1 &
fi
echo "== Done"
