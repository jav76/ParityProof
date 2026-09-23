#!/usr/bin/env python3
"""
generate-release-notes.py: Generates user-friendly release notes with direct
download links, platform guides, file sizes, and checksums for GitHub Releases.
"""

import os
import sys
from pathlib import Path


def format_size(size_bytes: int) -> str:
    if size_bytes < 1024:
        return f"{size_bytes} B"
    elif size_bytes < 1024 * 1024:
        return f"{size_bytes / 1024:.1f} KB"
    else:
        return f"{size_bytes / (1024 * 1024):.1f} MB"


def main():
    dist_dir = Path(sys.argv[1] if len(sys.argv) > 1 else "./dist")
    tag = sys.argv[2] if len(sys.argv) > 2 else os.getenv("RELEASE_TAG", "v1.0.0")
    repo = sys.argv[3] if len(sys.argv) > 3 else os.getenv("GITHUB_REPOSITORY", "jav76/ParityProof")
    output_path = Path(sys.argv[4] if len(sys.argv) > 4 else "./release-notes.md")

    base_url = f"https://github.com/{repo}/releases/download/{tag}"

    files_by_name = {}
    if dist_dir.exists():
        for file in dist_dir.iterdir():
            if file.is_file() and file.name != "checksums-sha256.txt":
                files_by_name[file.name] = {
                    "path": file,
                    "size": format_size(file.stat().st_size),
                    "url": f"{base_url}/{file.name}",
                }

    # Define known asset targets with user-friendly metadata
    targets = [
        # Windows
        {
            "os": "Windows",
            "arch": "64-bit (x64)",
            "desc": "Standard Windows 10/11 installer (Recommended)",
            "ext": ".msi",
            "type": "Installer (.msi)",
            "match": lambda name: "win-x64.msi" in name or ("win" in name and "x64" in name and name.endswith(".msi")),
        },
        {
            "os": "Windows",
            "arch": "64-bit (x64)",
            "desc": "Portable application (no installation required)",
            "ext": ".zip",
            "type": "Portable (.zip)",
            "match": lambda name: "win-x64.zip" in name,
        },
        {
            "os": "Windows",
            "arch": "ARM64",
            "desc": "Windows on ARM (Surface Pro, Snapdragon)",
            "ext": ".msi",
            "type": "Installer (.msi)",
            "match": lambda name: "win-arm64.msi" in name or ("win" in name and "arm64" in name and name.endswith(".msi")),
        },
        {
            "os": "Windows",
            "arch": "32-bit (x86)",
            "desc": "Legacy 32-bit Windows systems",
            "ext": ".msi",
            "type": "Installer (.msi)",
            "match": lambda name: "win-x86.msi" in name or ("win" in name and "x86" in name and name.endswith(".msi")),
        },
        # macOS
        {
            "os": "macOS",
            "arch": "Apple Silicon (M1/M2/M3/M4)",
            "desc": "Apple Silicon Mac disk image (Recommended)",
            "ext": ".dmg",
            "type": "Disk Image (.dmg)",
            "match": lambda name: "osx-arm64.dmg" in name,
        },
        {
            "os": "macOS",
            "arch": "Apple Silicon (M1/M2/M3/M4)",
            "desc": "Portable tarball archive",
            "ext": ".tar.gz",
            "type": "Archive (.tar.gz)",
            "match": lambda name: "osx-arm64.tar.gz" in name,
        },
        {
            "os": "macOS",
            "arch": "Intel (x64)",
            "desc": "Intel-based Mac disk image",
            "ext": ".dmg",
            "type": "Disk Image (.dmg)",
            "match": lambda name: "osx-x64.dmg" in name,
        },
        {
            "os": "macOS",
            "arch": "Intel (x64)",
            "desc": "Portable tarball archive",
            "ext": ".tar.gz",
            "type": "Archive (.tar.gz)",
            "match": lambda name: "osx-x64.tar.gz" in name,
        },
        # Linux
        {
            "os": "Linux",
            "arch": "64-bit (x64 / amd64)",
            "desc": "Debian, Ubuntu, Linux Mint, Pop!_OS",
            "ext": ".deb",
            "type": "Debian Package (.deb)",
            "match": lambda name: name.endswith(".deb") and ("amd64" in name or "x64" in name),
        },
        {
            "os": "Linux",
            "arch": "64-bit (x64)",
            "desc": "Standalone binary archive for all Linux distributions",
            "ext": ".tar.gz",
            "type": "Archive (.tar.gz)",
            "match": lambda name: "linux-x64.tar.gz" in name,
        },
        {
            "os": "Linux",
            "arch": "ARM64",
            "desc": "Debian/Ubuntu for ARM64 devices",
            "ext": ".deb",
            "type": "Debian Package (.deb)",
            "match": lambda name: name.endswith(".deb") and "arm64" in name,
        },
        {
            "os": "Linux",
            "arch": "ARM64",
            "desc": "Standalone binary archive for ARM64 Linux",
            "ext": ".tar.gz",
            "type": "Archive (.tar.gz)",
            "match": lambda name: "linux-arm64.tar.gz" in name,
        },
    ]

    # Map available files to target definitions
    matched_downloads = []
    used_filenames = set()

    for target in targets:
        for fname, finfo in files_by_name.items():
            if fname not in used_filenames and target["match"](fname):
                used_filenames.add(fname)
                matched_downloads.append({
                    "os": target["os"],
                    "arch": target["arch"],
                    "type": target["type"],
                    "desc": target["desc"],
                    "filename": fname,
                    "size": finfo["size"],
                    "url": finfo["url"],
                })
                break

    # Any remaining files not matched above
    other_downloads = []
    for fname, finfo in files_by_name.items():
        if fname not in used_filenames:
            other_downloads.append({
                "filename": fname,
                "size": finfo["size"],
                "url": finfo["url"],
            })

    # Read checksums if present
    checksums_file = dist_dir / "checksums-sha256.txt"
    checksum_lines = []
    if checksums_file.exists():
        try:
            with open(checksums_file, "r", encoding="utf-8") as f:
                checksum_lines = [line.strip() for line in f if line.strip()]
        except Exception:
            pass

    # Build Markdown content
    lines = [
        "## 📥 Downloads",
        "",
        "Choose the download package matching your operating system below:",
        "",
        "| Platform | Architecture | Package | Size | Description |",
        "| :--- | :--- | :--- | :--- | :--- |",
    ]

    for item in matched_downloads:
        os_badge = item["os"]
        if os_badge == "Windows":
            os_badge = "🪟 Windows"
        elif os_badge == "macOS":
            os_badge = "🍎 macOS"
        elif os_badge == "Linux":
            os_badge = "🐧 Linux"

        pkg_link = f"[{item['type']}]({item['url']})"
        lines.append(f"| {os_badge} | {item['arch']} | {pkg_link} | {item['size']} | {item['desc']} |")

    if other_downloads:
        lines.extend([
            "",
            "### Additional Packages",
            "",
            "| File | Size | Download |",
            "| :--- | :--- | :--- |",
        ])
        for other in other_downloads:
            lines.append(f"| `{other['filename']}` | {other['size']} | [Download]({other['url']}) |")

    lines.extend([
        "",
        "### 🚀 Installation Instructions",
        "",
        "- **Windows**: Download and run the `.msi` installer. If Windows SmartScreen displays a warning, click **More info** and then **Run anyway**.",
        "- **macOS**: Download the `.dmg` file for your processor (Apple Silicon for M-series, Intel for older Macs). Double-click to open and drag ParityProof into your **Applications** folder.",
        "- **Linux**: For Debian or Ubuntu, install the `.deb` package via `sudo dpkg -i parityproof_*.deb` or through your desktop software center.",
        "",
    ])

    if checksum_lines:
        lines.extend([
            "<details>",
            "<summary><b>🔒 Verify Checksums (SHA-256)</b></summary>",
            "",
            "```text",
            "\n".join(checksum_lines),
            "```",
            "",
            f"Download the full manifest: [checksums-sha256.txt]({base_url}/checksums-sha256.txt)",
            "",
            "</details>",
            "",
        ])

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
        f.write("\n")

    print(f"Generated release notes at {output_path} with {len(matched_downloads)} download links.")


if __name__ == "__main__":
    main()
