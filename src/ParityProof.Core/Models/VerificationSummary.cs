using System;
using ParityProof.Core.Enums;

namespace ParityProof.Core.Models;

public sealed record VerificationSummary(
    DateTime TimestampUtc,
    string SourcePath,
    VerificationMode Mode,
    int TotalFiles,
    long TotalBytes,
    int FullyVerifiedFiles,
    int PartiallyVerifiedFiles,
    int MissingFiles,
    int CorruptFiles,
    OverallSafetyStatus SafetyStatus,
    TimeSpan Duration,
    DuplicateAnalysisResult? DuplicateAnalysis = null);
