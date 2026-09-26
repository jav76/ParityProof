using System.Collections.Generic;
using System.Linq;
using ParityProof.Core.Enums;

namespace ParityProof.Core.Models;

public sealed class VerificationResultItem
{
    public MediaFile SourceFile { get; }
    public Dictionary<string, FileMatchStatus> DestinationStatuses { get; }

    public VerificationResultItem(
        MediaFile sourceFile,
        Dictionary<string, FileMatchStatus> destinationStatuses)
    {
        SourceFile = sourceFile;
        DestinationStatuses = destinationStatuses;
    }

    public bool IsFullyVerified =>
        DestinationStatuses.Count > 0 &&
        DestinationStatuses.Values.All(status => status.Status == MediaStatus.Verified);

    public bool IsPartiallyVerified =>
        DestinationStatuses.Values.Any(status => status.Status == MediaStatus.Verified) &&
        !IsFullyVerified;

    public bool IsMissingFromAll =>
        DestinationStatuses.Values.All(status => status.Status == MediaStatus.Missing);

    public bool HasAnyCorruption =>
        DestinationStatuses.Values.Any(status => status.Status == MediaStatus.Corrupt);

    // Mirrors the counting precedence in MultiDestinationVerifier so per-file labels add up to the summary totals.
    public FileOverallStatus OverallStatus => this switch
    {
        { HasAnyCorruption: true } => FileOverallStatus.Corrupt,
        { IsFullyVerified: true } => FileOverallStatus.Verified,
        { IsPartiallyVerified: true } => FileOverallStatus.Partial,
        _ => FileOverallStatus.Missing
    };
}
