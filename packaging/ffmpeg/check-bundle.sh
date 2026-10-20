#!/usr/bin/env bash
# Checks that a publish folder carries the FFmpeg libraries, their license and the source reference, and with --run
# that the bundled mmw loads them (only where the executable can run on this machine).
# Usage: packaging/ffmpeg/check-bundle.sh <publish dir> [--run]
set -euo pipefail
dir="${1:?publish directory, e.g. artifacts/publish/linux-x64}"
run="${2:-}"
ffmpeg="$dir/ffmpeg"
status=0

for library in avutil swresample swscale avcodec avformat; do
  if ! compgen -G "$ffmpeg/$library-[0-9]*.dll" >/dev/null && ! compgen -G "$ffmpeg/lib$library.so.[0-9]*" >/dev/null &&
    ! compgen -G "$ffmpeg/lib$library.[0-9]*.dylib" >/dev/null; then
    echo "Missing $library in $ffmpeg" >&2
    status=1
  fi
done
for file in "COPYING.LGPL*" FFMPEG-SOURCE.txt; do
  if ! compgen -G "$ffmpeg/$file" >/dev/null; then
    echo "Missing $file in $ffmpeg" >&2
    status=1
  fi
done
[ "$status" -eq 0 ] || exit "$status"

if [ "$run" = --run ]; then
  # WAV files are read through FFmpeg, which is only offered when the libraries load.
  wav="$(mktemp -d)/check.wav"
  {
    printf 'RIFF\x34\x00\x00\x00WAVEfmt \x10\x00\x00\x00\x01\x00\x01\x00\x80\xbb\x00\x00\x00\x77\x01\x00\x02\x00\x10\x00'
    printf 'data\x10\x00\x00\x00'
    printf '\x00\x00%.0s' 1 2 3 4 5 6 7 8
  } > "$wav"
  mmw="$dir/mmw"
  [ -e "$mmw.exe" ] && mmw="$mmw.exe"
  "$mmw" probe "$wav"
  rm -rf "$(dirname "$wav")"
fi
echo "FFmpeg bundle in $dir is complete"
