using System;
using System.Collections.Generic;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class VerificationResultItemTests
{
    [Theory]
    [InlineData(new[] { MediaStatus.Verified, MediaStatus.Verified }, FileOverallStatus.Verified)]
    [InlineData(new[] { MediaStatus.Verified, MediaStatus.Missing }, FileOverallStatus.Partial)]
    [InlineData(new[] { MediaStatus.Missing, MediaStatus.Missing }, FileOverallStatus.Missing)]
    [InlineData(new[] { MediaStatus.Pending }, FileOverallStatus.Missing)]
    [InlineData(new MediaStatus[0], FileOverallStatus.Missing)]
    [InlineData(new[] { MediaStatus.Corrupt, MediaStatus.Corrupt }, FileOverallStatus.Corrupt)]
    [InlineData(new[] { MediaStatus.Verified, MediaStatus.Corrupt }, FileOverallStatus.Corrupt)]
    [InlineData(new[] { MediaStatus.Corrupt, MediaStatus.Missing }, FileOverallStatus.Corrupt)]
    public void OverallStatus_UsesEnginePrecedence_CorruptThenVerifiedThenPartialThenMissing(
        MediaStatus[] destinationStatuses,
        FileOverallStatus expected)
    {
        MediaFile source = new(
            RelativePath: "DCIM/IMG_0001.CR3",
            FullPath: "/card/DCIM/IMG_0001.CR3",
            FileLength: 1024,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        Dictionary<string, FileMatchStatus> statuses = new();
        for (int index = 0; index < destinationStatuses.Length; index++)
        {
            string destinationId = $"dest_{index + 1}";
            statuses[destinationId] = new FileMatchStatus(destinationId, $"/backup{index}", destinationStatuses[index]);
        }

        VerificationResultItem item = new(source, statuses);

        Assert.Equal(expected, item.OverallStatus);
    }
}
