#!/usr/bin/env bash
set -euo pipefail

# Usage: build-dmg.sh <publish_dir> <version> <arch: arm64|x64> <output_dir>
PUBLISH_DIR="${1:?Publish directory required}"
VERSION="${2:?Version required}"
ARCH="${3:?Architecture (arm64|x64) required}"
OUTPUT_DIR="${4:?Output directory required}"

DMG_NAME="ParityProof-v${VERSION}-osx-${ARCH}"
APP_BUNDLE="ParityProof.app"
APP_DIR=$(mktemp -d -t "parityproof-app-XXXXXX")
trap 'rm -rf "$APP_DIR"' EXIT

CONTENTS_DIR="$APP_DIR/$APP_BUNDLE/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

# Copy published binaries and dynamic libraries
cp -r "$PUBLISH_DIR"/* "$MACOS_DIR"/
rm -f "$MACOS_DIR"/*.pdb "$MACOS_DIR"/*.dsym

# Ensure executable permission
chmod 755 "$MACOS_DIR/ParityProof.App"

# Create Info.plist
cat << EOF > "$CONTENTS_DIR/Info.plist"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>ParityProof</string>
    <key>CFBundleDisplayName</key>
    <string>ParityProof</string>
    <key>CFBundleIdentifier</key>
    <string>com.parityproof.app</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>CFBundleExecutable</key>
    <string>ParityProof.App</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSRequiresAquaSystemAppearance</key>
    <false/>
</dict>
</plist>
EOF
chmod 644 "$CONTENTS_DIR/Info.plist"

mkdir -p "$OUTPUT_DIR"

# Check if running on macOS (hdiutil available)
if command -v hdiutil >/dev/null 2>&1; then
    DMG_STAGE=$(mktemp -d -t "dmg-stage-XXXXXX")
    cp -r "$APP_DIR/$APP_BUNDLE" "$DMG_STAGE/"
    ln -s /Applications "$DMG_STAGE/Applications"
    hdiutil create -volname "ParityProof" -srcfolder "$DMG_STAGE" -ov -format UDZO "$OUTPUT_DIR/${DMG_NAME}.dmg"
    rm -rf "$DMG_STAGE"
    echo "Generated $OUTPUT_DIR/${DMG_NAME}.dmg"
else
    # Fallback when running on non-macOS environment: create tar.gz of .app bundle
    echo "hdiutil not found (non-macOS environment). Creating tar.gz of .app bundle."
    tar -czf "$OUTPUT_DIR/${DMG_NAME}.app.tar.gz" -C "$APP_DIR" "$APP_BUNDLE"
    echo "Generated $OUTPUT_DIR/${DMG_NAME}.app.tar.gz"
fi
