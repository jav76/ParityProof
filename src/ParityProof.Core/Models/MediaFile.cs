using System;
using ParityProof.Core.Enums;

namespace ParityProof.Core.Models;

public sealed record MediaFile(
    string RelativePath,
    string FullPath,
    long FileLength,
    DateTime LastWriteTimeUtc,
    MediaCategory Category,
    ulong? HeadHash = null,
    ulong? TailHash = null,
    ulong? DeepHash = null,
    ulong? FullHash = null,
    string? CameraModel = null,
    DateTime? CaptureDateTime = null);
