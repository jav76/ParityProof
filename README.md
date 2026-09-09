# ParityProof

**ParityProof** is a cross-platform desktop application built with **C# (.NET 10)**, **Avalonia UI**, and **NativeAOT**. It is purpose-built for photographers, DITs (Digital Imaging Technicians), and videographers to rapidly verify whether media on memory cards (SD, CFexpress, microSD) has been safely backed up to primary and secondary storage destinations before formatting.

---

## Key Highlights

- **NativeAOT Performance**: Compiles to a single standalone native executable with instant startup (<50ms) and minimal RAM footprint (<50MB).
- **Zero-Allocation I/O**: Employs `System.IO.RandomAccess` with rented byte buffers (`ArrayPool<byte>`) and platform-specific unbuffered direct I/O (`FILE_FLAG_NO_BUFFERING` on Windows, `fcntl(fd, F_NOCACHE, 1)` on macOS/Linux).
- **SIMD Hashing**: Leverages `System.IO.Hashing.XxHash3` capable of processing 15–30 GB/s per core using AVX2, AVX-512, and ARM Neon intrinsics.
- **Three Verification Modes**:
  1. **Super-Fast**: Metadata + File Size (sub-second scan across tens of thousands of files).
  2. **Quick (Default)**: Metadata + Size + 64KB Head/Tail SIMD `XxHash3` (scans a 128GB card in ~3s).
  3. **Full**: Bit-for-bit full sequential verification to guarantee 100% data integrity.
- **Content-Addressed Multi-Destination Matching**: Matches files across multiple backup targets (Primary SSD, Archive NAS) even if reorganized into date hierarchies (`YYYY/MM/DD`) or renamed during ingest.
- **Persistent SQLite Cache**: Caches archive file sizes, modification timestamps, and hashes for instantaneous matching against massive multi-terabyte drives.
- **OS Hardware Hooks**: Subscribes to native card insertion events (`WM_DEVICECHANGE` on Windows, `DiskArbitration` on macOS, `udisks2` on Linux) to automatically prompt verification.
- **Pro Studio Dark UI**: Tailored dark theme with prominent "Safe to Format" hero status banner, virtualized media grid, EXIF inspector, one-click "Copy Missing to Backup", and multi-format audit reports (HTML, CSV, JSON).

---

## Releases


-  **[docs/VERSIONING_AND_RELEASES.md](docs/VERSIONING_AND_RELEASES.md)**: Semantic Versioning specification, GitHub Actions automation, and 7-target Native AOT matrix (`linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `win-x86`, `osx-arm64`, `osx-x64`).
