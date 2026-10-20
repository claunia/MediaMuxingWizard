#!/usr/bin/env bash
# Builds the LGPL FFmpeg shared libraries FFmpegLoader loads (avutil, swresample, swscale, avcodec, avformat) for
# macOS from the release tarball, with install names relative to the library (@loader_path) so they work from the
# app bundle. Only FFmpeg's own code is built, plus zlib and bzip2 from macOS. Run on macOS; x64 is
# cross-compiled on Apple Silicon (needs nasm, e.g. brew install nasm).
# Usage: packaging/ffmpeg/build-macos.sh <arm64|x64> <version> <dest> [sha256 of ffmpeg-<version>.tar.xz]
set -euo pipefail
arch="${1:?architecture: arm64 or x64}"
version="${2:?FFmpeg version, e.g. 9.0.2}"
dest="${3:?destination directory}"
sha256="${4:-}"
libraries=(avutil swresample swscale avcodec avformat)

case "$arch" in
  arm64) cpu=arm64 ;;
  x64) cpu=x86_64 ;;
  *) echo "Unknown architecture: $arch" >&2; exit 1 ;;
esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
tarball="ffmpeg-$version.tar.xz"
curl -fsSL --retry 3 -o "$work/$tarball" "https://ffmpeg.org/releases/$tarball"
if [ -n "$sha256" ]; then
  echo "$sha256  $work/$tarball" | shasum -a 256 -c -
fi
tar -xJf "$work/$tarball" -C "$work"

# Same minimum as Info.plist (LSMinimumSystemVersion).
min="-mmacosx-version-min=12.0"
options=(
  --prefix="$work/install" --enable-shared --disable-static --install-name-dir=@loader_path
  --disable-programs --disable-doc --disable-avdevice --disable-avfilter --disable-network
  --disable-autodetect --enable-zlib --enable-bzlib
  --target-os=darwin --arch="$cpu" --cc="clang -arch $cpu" --extra-cflags="$min" --extra-ldflags="$min"
)
if [ "$cpu" != "$(uname -m)" ]; then
  options+=(--enable-cross-compile)
fi
(cd "$work/ffmpeg-$version" && ./configure "${options[@]}" && make -j"$(sysctl -n hw.ncpu)" && make install)

rm -rf "$dest"
mkdir -p "$dest"
for library in "${libraries[@]}"; do
  # Only the major-versioned name (libavcodec.63.dylib), dereferenced: it is the install name and what FFmpegLoader
  # looks for.
  file="$(find "$work/install/lib" -name "lib$library.*.dylib" | grep -E "/lib$library\.[0-9]+\.dylib\$")"
  cp -L "$file" "$dest/"
done

for file in "$dest"/*.dylib; do
  if [ "$(lipo -archs "$file")" != "$cpu" ]; then
    echo "$file is not $cpu: $(lipo -archs "$file")" >&2
    exit 1
  fi
  # Every non-system dependency must resolve next to the library.
  if otool -L "$file" | tail -n +2 | grep -v -E "@loader_path/|/usr/lib/|/System/Library/"; then
    echo "$file links libraries outside the bundle" >&2
    exit 1
  fi
done

cp "$work/ffmpeg-$version/COPYING.LGPLv2.1" "$dest/"
cat > "$dest/FFMPEG-SOURCE.txt" <<EOF
FFmpeg $version, LGPL shared build for macOS $arch (unmodified source).

Source:    https://ffmpeg.org/releases/$tarball (also attached to the Media Muxing Wizard release)
Configure: ./configure $(printf '%q ' "${options[@]/--prefix=*/--prefix=install}")
EOF
echo "FFmpeg libraries in $dest"
