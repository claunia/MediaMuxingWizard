#!/usr/bin/env bash
# Publishes the editor and the mmw command line tool, self-contained, for one runtime identifier.
# Usage: packaging/publish.sh <rid> [version]     e.g. packaging/publish.sh linux-x64 0.1.0
set -euo pipefail
rid="${1:?runtime identifier, e.g. linux-x64, osx-arm64, win-x64}"
version="${2:-0.1.0}"
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/publish/$rid"

rm -rf "$out"
for project in src/MMW.App/MMW.App.csproj src/MMW.Cli/MMW.Cli.csproj; do
  dotnet publish "$root/$project" -c Release -r "$rid" --self-contained true \
    -p:Version="$version" -p:PublishReadyToRun=true -p:DebugType=none -o "$out"
done
# Optional native libraries, bundled next to the executable:
#   MMW_BUNDLE_FFMPEG=/path/to/lgpl-ffmpeg-9/lib     (libavcodec, libavformat, libavutil, libswresample, libswscale)
#   MMW_BUNDLE_TESSERACT=/path/to/tesseract-5/lib    (libtesseract and its dependencies)
#   MMW_BUNDLE_TESSDATA=/path/to/tessdata            (at least eng.traineddata)
if [ -n "${MMW_BUNDLE_FFMPEG:-}" ]; then
  mkdir -p "$out/ffmpeg"
  cp -a "$MMW_BUNDLE_FFMPEG"/*{avcodec,avformat,avutil,swresample,swscale}* "$out/ffmpeg/"
fi
if [ -n "${MMW_BUNDLE_TESSERACT:-}" ]; then
  mkdir -p "$out/tesseract"
  cp -a "$MMW_BUNDLE_TESSERACT"/. "$out/tesseract/"
fi
if [ -n "${MMW_BUNDLE_TESSDATA:-}" ]; then
  mkdir -p "$out/tessdata"
  cp -a "$MMW_BUNDLE_TESSDATA"/*.traineddata "$out/tessdata/"
fi
find "$root/src" -name 'THIRD-PARTY-NOTICES*.md' -not -path '*/bin/*' -not -path '*/obj/*' -exec cp {} "$out/" \;
echo "Published to $out"
