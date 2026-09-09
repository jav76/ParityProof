using System;
using System.Collections.Generic;

namespace ParityProof.Core.Models;

public sealed record FilterPreset(
    string Name,
    IReadOnlySet<string> Extensions,
    bool IncludeSidecars)
{
    public static readonly FilterPreset PhotosOnly = new(
        "Photos Only",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cr2", ".cr3", ".nef", ".arw", ".raf", ".dng", ".rw2", ".orf",
            ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".png", ".webp"
        },
        IncludeSidecars: false);

    public static readonly FilterPreset PhotosAndVideos = new(
        "Photos and Videos",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cr2", ".cr3", ".nef", ".arw", ".raf", ".dng", ".rw2", ".orf",
            ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".png", ".webp",
            ".mov", ".mp4", ".mxf", ".braw", ".r3d", ".avi", ".mts"
        },
        IncludeSidecars: false);

    public static readonly FilterPreset AllCameraMediaWithSidecars = new(
        "All Camera Media (with Sidecars)",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cr2", ".cr3", ".nef", ".arw", ".raf", ".dng", ".rw2", ".orf",
            ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".png", ".webp",
            ".mov", ".mp4", ".mxf", ".braw", ".r3d", ".avi", ".mts",
            ".xmp", ".thm", ".lrv"
        },
        IncludeSidecars: true);
}
