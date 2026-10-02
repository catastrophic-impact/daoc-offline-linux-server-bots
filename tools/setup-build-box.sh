#!/usr/bin/env bash
# setup-build-box.sh - create (or update) the Ubuntu 22.04 distrobox used to build portable
# releases, with the .NET 10 SDK, cmake and g++. Safe to re-run. Run it on the host.
#
#   tools/setup-build-box.sh            set up the box
#   tools/setup-build-box.sh --build    set up the box, then build the release from this checkout
#
# Settings (environment):
#   BOX_NAME   default daoc-server-box
#   BOX_HOME   default ~/boxes/daoc-server-box-home   (the box's own home: the SDK lives here)
#
# Why a box: Ubuntu 22.04 (glibc 2.35) builds binaries that run on glibc 2.34+ systems, and the
# host needs nothing installed. Building directly on your own system works too; see docs/BUILD.md.
set -euo pipefail

BOX_NAME="${BOX_NAME:-daoc-server-box}"
BOX_HOME="${BOX_HOME:-$HOME/boxes/daoc-server-box-home}"
IMAGE="docker.io/library/ubuntu:22.04"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

die() { echo "setup-build-box: $*" >&2; exit 1; }
command -v distrobox >/dev/null || die "distrobox is not installed (https://distrobox.it). Or build without it: see docs/BUILD.md."
[[ ${1:-} == "" || ${1:-} == --build ]] || die "usage: $0 [--build]"

mkdir -p "$BOX_HOME"
if ! distrobox list --no-color | cut -d'|' -f2 | tr -d ' ' | grep -qx "$BOX_NAME"; then
  echo "== Creating distrobox $BOX_NAME (home: $BOX_HOME)"
  distrobox create --name "$BOX_NAME" --image "$IMAGE" --home "$BOX_HOME" --yes
fi

echo "== Installing build tools inside $BOX_NAME"
distrobox enter "$BOX_NAME" -- bash -lc '
  set -euo pipefail
  need=()
  for pkg in curl ca-certificates libicu70 cmake g++ make git; do
    dpkg -s "$pkg" >/dev/null 2>&1 || need+=("$pkg")
  done
  if ((${#need[@]})); then
    sudo apt-get update -qq
    sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "${need[@]}" >/dev/null
  fi
  if ! "$HOME/.dotnet/dotnet" --list-sdks 2>/dev/null | grep -q "^10\."; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" >/dev/null
  fi
  echo "   .NET SDK: $("$HOME/.dotnet/dotnet" --version)   cmake: $(cmake --version | head -1 | cut -d" " -f3)   g++: $(g++ -dumpversion)"
'

if [[ ${1:-} == --build ]]; then
  echo "== Building the release from $REPO"
  distrobox enter "$BOX_NAME" -- bash -lc "cd '$REPO' && tools/build-release.sh"
else
  echo
  echo "Build tools are ready. To build a release from this checkout:"
  echo "  distrobox enter $BOX_NAME -- bash -lc \"cd '$REPO' && tools/build-release.sh\""
  echo "(or run: $0 --build)"
fi
