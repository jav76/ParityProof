using System;

namespace ParityProof.Core.Models;

public sealed record CopyProgressInfo(
    string CurrentFileName,
    long TotalBytes,
    long CopiedBytes,
    double MegaBytesPerSecond,
    TimeSpan EstimatedTimeRemaining,
    int FilesCompleted,
    int TotalFiles,
    long CurrentFileBytes = 0,
    long CurrentFileCopiedBytes = 0,
    bool IsPaused = false);
