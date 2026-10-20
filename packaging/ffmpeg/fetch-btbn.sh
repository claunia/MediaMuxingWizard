#!/usr/bin/env bash
# Downloads the LGPL shared FFmpeg libraries of one platform from a BtbN/FFmpeg-Builds release
# (https://github.com/BtbN/FFmpeg-Builds) and keeps only what FFmpegLoader loads, plus the license and the source
# reference the LGPL asks for. The result is meant for MMW_BUNDLE_FFMPEG.
# Usage: packaging/ffmpeg/fetch-btbn.sh <release tag> <win64|winarm64|linux64|linuxarm64> <dest> [branch]
#   release tag: an autobuild-YYYY-MM-DD-HH-MM tag (pins the FFmpeg commit) or "latest"
#   branch:      FFmpeg release branch matching the FFmpeg.AutoGen bindings, default n9.0
set -euo pipefail
tag="${1:?release tag, e.g. autobuild-2026-10-20-13-08}"
platform="${2:?platform: win64, winarm64, linux64 or linuxarm64}"
dest="${3:?destination directory}"
branch="${4:-n9.0}"
libraries=(avutil swresample swscale avcodec avformat)

case "$platform" in
  win64|winarm64) ext=zip ;;
  linux64|linuxarm64) ext=tar.xz ;;
  *) echo "Unknown platform: $platform" >&2; exit 1 ;;
esac

base="https://github.com/BtbN/FFmpeg-Builds/releases/download/$tag"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

curl -fsSL --retry 3 -o "$work/checksums.sha256" "$base/checksums.sha256"
series="${branch#n}"
pattern="^ffmpeg-${branch//./\\.}[.-].*-${platform}-lgpl-shared-${series//./\\.}\\.${ext//./\\.}\$"
name="$(pattern="$pattern" awk '{ sub(/^\*/, "", $2) } $2 ~ ENVIRON["pattern"] { print $2; exit }' "$work/checksums.sha256")"
if [ -z "$name" ]; then
  echo "No $branch lgpl-shared build for $platform in $tag" >&2
  exit 1
fi

echo "Downloading $name"
curl -fsSL --retry 3 -o "$work/$name" "$base/$name"
(cd "$work" && grep -E "[ *]$name\$" checksums.sha256 | sha256sum -c -)

if [ "$ext" = zip ] && command -v unzip >/dev/null; then
  unzip -q "$work/$name" -d "$work"
elif [ "$ext" = zip ]; then
  7z x -bd -o"$work" "$work/$name" >/dev/null
else
  tar -xJf "$work/$name" -C "$work"
fi
top="$work/${name%."$ext"}"

rm -rf "$dest"
mkdir -p "$dest"
for library in "${libraries[@]}"; do
  if [ "$ext" = zip ]; then
    files=("$top"/bin/"$library"-[0-9]*.dll)
  else
    # Only the soname (libavcodec.so.63), dereferenced: FFmpegLoader looks for that name.
    files=("$top"/lib/lib"$library".so.[0-9]*)
    files=("$(printf '%s\n' "${files[@]}" | grep -E "/lib$library\.so\.[0-9]+\$" || true)")
  fi
  if [ ! -e "${files[0]}" ]; then
    echo "$library is missing from $name" >&2
    exit 1
  fi
  cp -L "${files[@]}" "$dest/"
done
# BtbN's LGPL builds enable version 3 (LGPLv3); name the license after the text it holds.
if grep -q "Version 3" "$top/LICENSE.txt"; then license=COPYING.LGPLv3; else license=COPYING.LGPLv2.1; fi
cp "$top/LICENSE.txt" "$dest/$license"

# The asset name carries the FFmpeg revision (git describe, e.g. n9.0.2-29-g6dfaba896f).
describe="${name#ffmpeg-}"
describe="${describe%%-"$platform"-*}"
case "$describe" in
  *-g*) source="https://github.com/FFmpeg/FFmpeg/tree/${describe##*-g}" ;;
  *) source="https://github.com/FFmpeg/FFmpeg/tree/release/$series (the build of $tag)" ;;
esac
cat > "$dest/FFMPEG-SOURCE.txt" <<EOF
FFmpeg $describe, LGPL shared build for $platform by BtbN/FFmpeg-Builds (unmodified).

Binaries:     $base/$name
Source:       $source
Build scripts and configuration: https://github.com/BtbN/FFmpeg-Builds/tree/$tag
EOF
echo "FFmpeg libraries in $dest"
