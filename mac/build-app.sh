#!/usr/bin/env bash
# Builds Diogenes.app for macOS and zips it.
#   mac/build-app.sh            -> Apple silicon (M chips), osx-arm64
#   mac/build-app.sh osx-x64    -> Intel Macs
# Needs the .NET 8 SDK. Output: publish-mac/Diogenes.app and DiogenesImGui-<rid>.zip
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/publish-mac"
APP="$OUT/Diogenes.app"
VERSION="${VERSION:-1.0.0}"

rm -rf "$OUT"
dotnet publish "$ROOT/DiogenesImGui/DiogenesImGui.csproj" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=none -o "$OUT/bin"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/bin/." "$APP/Contents/MacOS/"
cp "$ROOT/README.md" "$ROOT/LICENSE" "$APP/Contents/Resources/"

# app icon from the Windows .ico
ICONSET="$OUT/Diogenes.iconset"
mkdir -p "$ICONSET"
sips -s format png "$ROOT/DiogenesImGui/diogenes2-256.ico" --out "$OUT/icon.png" >/dev/null
for size in 16 32 128 256; do
    sips -z $size $size "$OUT/icon.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    [ $double -le 256 ] && sips -z $double $double "$OUT/icon.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Diogenes.icns"
rm -rf "$ICONSET" "$OUT/icon.png"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Diogenes</string>
    <key>CFBundleDisplayName</key><string>Diogenes</string>
    <key>CFBundleIdentifier</key><string>io.github.diogenes-imgui</string>
    <key>CFBundleExecutable</key><string>DiogenesImGui</string>
    <key>CFBundleIconFile</key><string>Diogenes</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF

# Apple silicon refuses unsigned code, so sign ad hoc (no developer account needed)
codesign --force --deep --sign - "$APP"

(cd "$OUT" && ditto -c -k --keepParent Diogenes.app "$ROOT/DiogenesImGui-$RID.zip")
echo "Built $APP and DiogenesImGui-$RID.zip"
