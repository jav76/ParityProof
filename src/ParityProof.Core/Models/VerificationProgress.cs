using System;

namespace ParityProof.Core.Models;

public sealed record VerificationProgress(
    string CurrentFile,
    int ProcessedFiles,
    int TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    string Phase,
    long CurrentFileBytes = 0,
    long CurrentFileProcessedBytes = 0,
    double MegaBytesPerSecond = 0.0,
    TimeSpan EstimatedTimeRemaining = default,
    bool IsPaused = false,
    double ScanRateFilesPerSecond = 0.0);
