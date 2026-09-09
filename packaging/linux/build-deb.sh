#!/usr/bin/env bash
set -euo pipefail

# Usage: build-deb.sh <publish_dir> <version> <arch: x64|arm64> <output_dir>
PUBLISH_DIR="${1:?Publish directory required}"
VERSION="${2:?Version required}"
ARCH="${3:?Architecture (x64|arm64) required}"
OUTPUT_DIR="${4:?Output directory required}"

if [ "$ARCH" = "x64" ]; then
    DEB_ARCH="amd64"
elif [ "$ARCH" = "arm64" ]; then
    DEB_ARCH="arm64"
else
    echo "Unsupported architecture: $ARCH" >&2
    exit 1
fi

PKG_NAME="parityproof_${VERSION}_${DEB_ARCH}"
PKG_DIR=$(mktemp -d -t "parityproof-deb-XXXXXX")
trap 'rm -rf "$PKG_DIR"' EXIT

INSTALL_DIR="$PKG_DIR/usr/lib/parityproof"
BIN_DIR="$PKG_DIR/usr/bin"
DESKTOP_DIR="$PKG_DIR/usr/share/applications"
CONTROL_DIR="$PKG_DIR/DEBIAN"

mkdir -p "$INSTALL_DIR" "$BIN_DIR" "$DESKTOP_DIR" "$CONTROL_DIR"

# Copy published binaries and libraries (exclude debug symbols)
cp -r "$PUBLISH_DIR"/* "$INSTALL_DIR"/
rm -f "$INSTALL_DIR"/*.pdb "$INSTALL_DIR"/*.dbg

# Ensure executable permissions
chmod 755 "$INSTALL_DIR/ParityProof.App"

# Create launcher script in /usr/bin
cat << 'EOF' > "$BIN_DIR/parityproof"
#!/bin/sh
exec /usr/lib/parityproof/ParityProof.App "$@"
EOF
chmod 755 "$BIN_DIR/parityproof"

# Create desktop entry
cat << EOF > "$DESKTOP_DIR/parityproof.desktop"
[Desktop Entry]
Type=Application
Name=ParityProof
Comment=Pro Media Ingest & Backup Verification
Exec=/usr/bin/parityproof
Terminal=false
Categories=Utility;Graphics;AudioVideo;
StartupWMClass=ParityProof.App
EOF
chmod 644 "$DESKTOP_DIR/parityproof.desktop"

# Create Debian control file
cat << EOF > "$CONTROL_DIR/control"
Package: parityproof
Version: $VERSION
Section: utils
Priority: optional
Architecture: $DEB_ARCH
Maintainer: ParityProof Contributors <https://github.com/jav76/ParityProof>
Description: Pro Media Ingest and Backup Verification
 ParityProof is a high-performance cross-platform desktop application purpose-built
 for photographers, DITs, and videographers to rapidly verify whether media on
 memory cards has been safely backed up to storage destinations before formatting.
EOF
chmod 644 "$CONTROL_DIR/control"

mkdir -p "$OUTPUT_DIR"
dpkg-deb --build --root-owner-group "$PKG_DIR" "$OUTPUT_DIR/${PKG_NAME}.deb"
echo "Generated $OUTPUT_DIR/${PKG_NAME}.deb"
