namespace ParityProof.Core.Models;

public sealed record SourceTelemetryInfo(
    string Path,
    string ScanStatus,
    int ScanFilesCount,
    long ScanBytesCount,
    double ScanSpeed,
    string IndexStatus,
    string HashStatus,
    double Percentage,
    long ProcessedBytes,
    long TotalBytes,
    double SpeedMbPerSec,
    string? CurrentFile = null,
    double CurrentFilePercentage = 0.0,
    string? CurrentFileProgressText = null);
