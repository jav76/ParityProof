using ParityProof.Core.Enums;

namespace ParityProof.Core.Models;

public sealed record FileMatchStatus(
    string DestinationId,
    string DestinationRootPath,
    MediaStatus Status,
    string? MatchedFilePath = null,
    string? FailureReason = null);
