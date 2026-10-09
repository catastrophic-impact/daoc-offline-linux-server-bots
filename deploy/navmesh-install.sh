#!/usr/bin/env bash
# navmesh-install.sh - install the navigation meshes (zone*.nav, plus the bots' seams.json and
# pockets.json zone data) into this server folder's navmesh/.
# NPCs and bots need them to path around obstacles; without them they walk in straight lines.
#
#   ./navmesh-install.sh                         default: the navmesh archive from this project's release
#                                                (daoc-navmeshes-*.tar.xz next to this folder, or downloaded)
#   ./navmesh-install.sh --from-archive <file>   a downloaded daoc-navmeshes-*.tar.xz
#   ./navmesh-install.sh --from-dir <folder>     zone*.nav files from another OpenDAoC-based server
#   ./navmesh-install.sh --build-from-client <DAoC client folder>   generate them yourself (slow; see README)
#
# The archive and --from-dir are checked against MANIFEST.sha256 (the shipped navmesh set).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="${DAOC_HOME:-$HERE}/navmesh"
SET="classic-si-2"
ARCHIVE_NAME="daoc-navmeshes-$SET.tar.xz"
# Set when the project is published, or override: NAVMESH_URL=https://.../daoc-navmeshes-classic-si-2.tar.xz
work=""
NAVMESH_URL="${NAVMESH_URL:-$(sed -n 's/^NAVMESH_URL=//p' "$HERE/release.conf" 2>/dev/null)}"
die() { echo "navmesh-install: $*" >&2; exit 1; }

install_archive() {
  local archive=$1
  [[ -f $archive ]] || die "no such file: $archive"
  if [[ -f $archive.sha256 ]]; then
    [[ $(sha256sum "$archive" | cut -d' ' -f1) == "$(<"$archive.sha256")" ]] || die "$archive is damaged (checksum mismatch)"
  fi
  work=$(mktemp -d "${DEST%/}.install.XXXXXX")
  trap 'rm -rf "${work:-}"' EXIT
  echo "Unpacking $(basename "$archive") ..."
  tar -xJf "$archive" -C "$work"
  [[ -f $work/navmesh/MANIFEST.sha256 ]] || die "$archive is not a navmesh archive"
  (cd "$work/navmesh" && grep -v '^#' MANIFEST.sha256 | sha256sum --check --quiet) || die "navmeshes failed verification"
  mkdir -p "$DEST"
  mv -f "$work"/navmesh/zone*.nav "$DEST/"
  # Bot zone-border and walled-pocket data, built from these meshes (set classic-si-2 and later).
  for f in seams.json pockets.json; do if [[ -f $work/navmesh/$f ]]; then mv -f "$work/navmesh/$f" "$DEST/"; fi; done
  cp "$work/navmesh/MANIFEST.sha256" "$DEST/"
  echo "Installed $(ls "$DEST"/zone*.nav | wc -l) navmeshes into $DEST"
}

case "${1:-}" in
  "")
    for candidate in "$HERE/$ARCHIVE_NAME" "$HERE/../$ARCHIVE_NAME" "$PWD/$ARCHIVE_NAME"; do
      [[ -f $candidate ]] && { install_archive "$candidate"; exit 0; }
    done
    [[ -n $NAVMESH_URL ]] || die "no $ARCHIVE_NAME found next to this folder, and no download URL is configured. Download it from the project's release page, put it next to this folder, and run this again (or use --from-archive)."
    tmp="$HERE/.$ARCHIVE_NAME.download"
    echo "Downloading $NAVMESH_URL ..."
    curl -fL --retry 3 -C - "$NAVMESH_URL" -o "$tmp" || die "download failed"
    curl -fsL "$NAVMESH_URL.sha256" -o "$tmp.sha256" || true
    install_archive "$tmp"
    rm -f "$tmp" "$tmp.sha256"
    ;;
  --from-archive)
    install_archive "${2:?"usage: $0 --from-archive <file>"}"
    ;;
  --from-dir)
    src=${2:?"usage: $0 --from-dir <folder with zone*.nav>"}
    compgen -G "$src/zone*.nav" >/dev/null || die "no zone*.nav files in $src"
    mkdir -p "$DEST"
    count=0
    for f in "$src"/zone*.nav; do
      [[ -s $f ]] || die "empty navmesh file: $f"
      cp -f "$f" "$DEST/"
      count=$((count + 1))
    done
    for f in seams.json pockets.json; do if [[ -f $src/$f ]]; then cp -f "$src/$f" "$DEST/"; fi; done
    echo "Installed $count navmeshes into $DEST"
    ;;
  --build-from-client)
    die "--build-from-client is not available yet: the generator (tools/navmesh in the source) still needs its Wine automation. Use the default archive."
    ;;
  *)
    sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
    ;;
esac
