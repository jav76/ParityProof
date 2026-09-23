namespace ParityProof.Core.Models;

public sealed record DestinationProgressInfo(
    string DestinationId,
    string DestinationName,
    long ProcessedBytes,
    long TotalBytes,
    double Percentage,
    double MegaBytesPerSecond,
    string StatusText);
