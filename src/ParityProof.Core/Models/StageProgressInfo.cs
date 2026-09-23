using System.Collections.Generic;
using ParityProof.Core.Enums;

namespace ParityProof.Core.Models;

public sealed record StageProgressInfo(
    PipelineStageId Id,
    string Title,
    StageStatus Status,
    double Percentage,
    long ProcessedUnits,
    long TotalUnits,
    string ProgressText,
    string TelemetryText,
    bool IsIndeterminate = false,
    string? ActiveFileName = null,
    double ActiveFilePercentage = 0.0,
    string? ActiveFileProgressText = null,
    IReadOnlyList<DestinationProgressInfo>? DestinationMeters = null);
