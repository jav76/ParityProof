using System;
using System.Collections.Generic;

namespace ParityProof.Core.Models;

public sealed record DuplicateFileItem(
    MediaFile File,
    string DestinationId,
    string DestinationName,
    bool IsPrimary,
    string RelativePath);

public sealed record DuplicateGroup(
    string GroupKey,
    long FileSize,
    long ReclaimableBytes,
    bool IsIntraDestination,
    bool IsCrossDestination,
    IReadOnlyList<DuplicateFileItem> Files);

public sealed record DuplicateAnalysisResult(
    IReadOnlyList<DuplicateGroup> Groups,
    int TotalDuplicateCopies,
    long TotalReclaimableBytes,
    int CrossDestinationRedundantFileCount)
{
    public static DuplicateAnalysisResult Empty { get; } = new(
        Array.Empty<DuplicateGroup>(),
        0,
        0,
        0);
}
