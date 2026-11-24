#!/usr/bin/env bash
set -euo pipefail

target=${1:?Pass a directory for the Jellyfin FFmpeg test binaries}
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
archive="$tmp/ffmpeg.tar.xz"
curl --fail --silent --show-error --location \
  'https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v8.1.3-2/jellyfin-ffmpeg_8.1.3-2_portable_linux64-gpl.tar.xz' \
  --output "$archive"
printf '%s  %s\n' '1df84d04c4322902dba81c290508cdf3d89524d210b9fe03154bea6c4e3bfbb6' "$archive" | sha256sum --check -
mkdir -p "$target"
tar -xJf "$archive" -C "$target"
"$target/ffmpeg" -version | sed -n '1p'
