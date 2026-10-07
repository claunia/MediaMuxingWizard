#!/usr/bin/env bash
# Builds "Media Muxing Wizard.app" and a DMG. Run on macOS.
# Usage: packaging/macos/build-app.sh [arm64|x64] [version] [signing identity, default ad-hoc "-"]
set -euo pipefail
arch="${1:-arm64}"
version="${2:-0.1.0}"
identity="${3:--}"
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"

"$root/packaging/publish.sh" "osx-$arch" "$version"
app="$root/artifacts/Media Muxing Wizard.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -a "$root/artifacts/publish/osx-$arch/." "$app/Contents/MacOS/"
sed "s/@VERSION@/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
cp "$root/packaging/icon/icon.icns" "$app/Contents/Resources/icon.icns"

codesign --force --deep --options runtime --timestamp=none -s "$identity" "$app"

dmg="$root/artifacts/MediaMuxingWizard-$version-$arch.dmg"
rm -f "$dmg"
hdiutil create -volname "Media Muxing Wizard" -srcfolder "$app" -ov -format UDZO "$dmg"
echo "Created $dmg"
