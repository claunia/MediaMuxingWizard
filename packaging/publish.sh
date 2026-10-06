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
echo "Published to $out"
