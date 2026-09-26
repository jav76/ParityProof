using System;
using System.Collections.Generic;

namespace ParityProof.Core.Models;

public sealed record CopyFailure(
    string RelativePath,
    string SourcePath,
    string DestinationRootPath,
    string ErrorMessage);

// Counts are per source file: failed when any destination did not receive the file, skipped when every
// destination already held an identical copy, copied otherwise. Failures has one entry per file and destination.
public sealed record CopyBatchResult(
    int CopiedCount,
    int SkippedCount,
    int FailedFileCount,
    int RenamedCopyCount,
    IReadOnlyList<CopyFailure> Failures)
{
    public static CopyBatchResult Empty { get; } = new(
        0,
        0,
        0,
        0,
        Array.Empty<CopyFailure>());
}
