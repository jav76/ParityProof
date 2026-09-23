namespace ParityProof.Core.Models;

public sealed record DestinationTelemetryInfo(
    string DestinationId,
    string DestinationName,
    string RootPath,
    string ScanStatus,
    int ScanFilesCount,
    long ScanBytesCount,
    double ScanSpeed,
    string IndexStatus,
    string VerifyStatus,
    double Percentage,
    int VerifiedFiles,
    int TotalFiles,
    long BytesRead,
    double SpeedMbPerSec,
    string StatusText);
