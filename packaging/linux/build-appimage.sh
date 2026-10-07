#!/usr/bin/env bash
# Builds an AppImage. Needs appimagetool on PATH (https://github.com/AppImage/appimagetool/releases).
# Usage: packaging/linux/build-appimage.sh [x64|arm64] [version]
set -euo pipefail
arch="${1:-x64}"
version="${2:-0.1.0}"
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
id=com.claunia.MediaMuxingWizard

"$root/packaging/publish.sh" "linux-$arch" "$version"
appdir="$root/artifacts/AppDir-$arch"
rm -rf "$appdir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/metainfo"
cp -a "$root/artifacts/publish/linux-$arch/." "$appdir/usr/bin/"
cp "$here/$id.desktop" "$appdir/usr/share/applications/"
cp "$here/$id.desktop" "$appdir/"
cp "$here/$id.metainfo.xml" "$appdir/usr/share/metainfo/"
for size in 16 24 32 48 64 128 256 512; do
  mkdir -p "$appdir/usr/share/icons/hicolor/${size}x${size}/apps"
  cp "$root/packaging/icon/icon-$size.png" "$appdir/usr/share/icons/hicolor/${size}x${size}/apps/$id.png"
done
mkdir -p "$appdir/usr/share/icons/hicolor/scalable/apps"
cp "$root/packaging/icon/icon.svg" "$appdir/usr/share/icons/hicolor/scalable/apps/$id.svg"
cp "$root/packaging/icon/icon-256.png" "$appdir/$id.png"
cat > "$appdir/AppRun" <<'RUN'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/bin/MediaMuxingWizard" "$@"
RUN
chmod +x "$appdir/AppRun"

machine=$([ "$arch" = arm64 ] && echo aarch64 || echo x86_64)
ARCH=$machine appimagetool "$appdir" "$root/artifacts/MediaMuxingWizard-$version-$machine.AppImage"
