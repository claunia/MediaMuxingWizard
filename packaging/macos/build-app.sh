#!/usr/bin/env bash
# Builds "Media Muxing Wizard.app", a DMG and a portable zip of the app. Run on macOS.
# Usage: packaging/macos/build-app.sh [arm64|x64] [version] [signing identity, default ad-hoc "-"]
set -euo pipefail
arch="${1:-arm64}"
version="${2:-1.0.0}"
identity="${3:--}"
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"

"$root/packaging/publish.sh" "osx-$arch" "$version"
app="$root/artifacts/Media Muxing Wizard.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -a "$root/artifacts/publish/osx-$arch/." "$app/Contents/MacOS/"
# Bundle versions are numbers only: a pre-release suffix (1.2.3-rc1) is left out of Info.plist.
sed "s/@VERSION@/${version%%-*}/g" "$here/Info.plist" > "$app/Contents/Info.plist"
cp "$root/packaging/icon/icon.icns" "$app/Contents/Resources/icon.icns"

codesign --force --deep --options runtime --timestamp=none -s "$identity" "$app"

# Portable archive of the app bundle (ditto keeps the signature and permissions).
zip="$root/artifacts/MediaMuxingWizard-$version-macos-$arch.zip"
rm -f "$zip"
ditto -c -k --keepParent "$app" "$zip"

dmg="$root/artifacts/MediaMuxingWizard-$version-$arch.dmg"
rm -f "$dmg"
# hdiutil's own estimate of the image size from -srcfolder can come out too small ("No space left on device"): size
# it from the bundle, with headroom for the file system.
size=$(( $(du -sm "$app" | cut -f1) * 12 / 10 + 20 ))
hdiutil create -volname "Media Muxing Wizard" -srcfolder "$app" -size "${size}m" -ov -format UDZO "$dmg"
echo "Created $dmg and $zip"
