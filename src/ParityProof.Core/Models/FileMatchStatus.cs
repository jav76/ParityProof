using ParityProof.Core.Enums;

namespace ParityProof.Core.Models;

public sealed record FileMatchStatus(
    string DestinationId,
    string DestinationRootPath,
    MediaStatus Status,
    string? MatchedFilePath = null,
    string? FailureReason = null,
    ulong? DestinationHeadHash = null,
    ulong? DestinationTailHash = null,
    ulong? DestinationDeepHash = null,
    ulong? DestinationFullHash = null);
