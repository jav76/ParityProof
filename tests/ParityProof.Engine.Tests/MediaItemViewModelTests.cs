using System;
using System.Collections.Generic;
using ParityProof.App.ViewModels;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class MediaItemViewModelTests
{
    [Fact]
    public void ChecksumFingerprints_FormattedCorrectly_WhenHashesPresent()
    {
        MediaFile source = new(
            RelativePath: "DCIM/100CANON/IMG_0001.CR3",
            FullPath: "/card/DCIM/100CANON/IMG_0001.CR3",
            FileLength: 1024 * 1024,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw,
            HeadHash: 0x123456789ABCDEF0,
            TailHash: 0x0FEDCBA987654321,
            DeepHash: 0xAABBCCDDEEFF0011,
            FullHash: 0x1122334455667788);

        Dictionary<string, FileMatchStatus> destStatuses = new();
        VerificationResultItem resultItem = new(source, destStatuses);
        MediaItemViewModel vm = new(resultItem);

        Assert.Equal("0x123456789ABCDEF0", vm.HeadHashHex);
        Assert.Equal("0x0FEDCBA987654321", vm.TailHashHex);
        Assert.Equal("0xAABBCCDDEEFF0011", vm.DeepHashHex);
        Assert.Equal("0x1122334455667788", vm.FullHashHex);
        Assert.True(vm.HasDeepHash);
        Assert.True(vm.HasFullHash);
    }

    [Fact]
    public void ChecksumFingerprints_ReturnsNone_WhenHashesNull()
    {
        MediaFile source = new(
            RelativePath: "DCIM/100CANON/IMG_0001.CR3",
            FullPath: "/card/DCIM/100CANON/IMG_0001.CR3",
            FileLength: 1024 * 1024,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        Dictionary<string, FileMatchStatus> destStatuses = new();
        VerificationResultItem resultItem = new(source, destStatuses);
        MediaItemViewModel vm = new(resultItem);

        Assert.Equal("None", vm.HeadHashHex);
        Assert.Equal("None", vm.TailHashHex);
        Assert.Equal("None", vm.DeepHashHex);
        Assert.Equal("None", vm.FullHashHex);
        Assert.False(vm.HasDeepHash);
        Assert.False(vm.HasFullHash);
    }

    [Fact]
    public void DestinationComparisons_GeneratesComparativeRows_WithMatchStatus()
    {
        ulong head = 0x1111222233334444;
        ulong tail = 0x5555666677778888;

        MediaFile source = new(
            RelativePath: "DCIM/100CANON/IMG_0001.CR3",
            FullPath: "/card/DCIM/100CANON/IMG_0001.CR3",
            FileLength: 1024 * 1024,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw,
            HeadHash: head,
            TailHash: tail);

        FileMatchStatus verifiedDest = new(
            DestinationId: "ssd_backup",
            DestinationRootPath: "/backup/ssd",
            Status: MediaStatus.Verified,
            MatchedFilePath: "/backup/ssd/IMG_0001.CR3",
            DestinationHeadHash: head,
            DestinationTailHash: tail);

        Dictionary<string, FileMatchStatus> destStatuses = new()
        {
            ["ssd_backup"] = verifiedDest
        };

        VerificationResultItem resultItem = new(source, destStatuses);
        MediaItemViewModel vm = new(resultItem);

        Assert.Single(vm.DestinationComparisons);
        DestinationComparisonViewModel comp = vm.DestinationComparisons[0];

        Assert.Equal("ssd_backup", comp.DestinationId);
        Assert.Equal("✓ MATCH", comp.StatusText);
        Assert.Equal("#10B981", comp.StatusBadgeColor);
        Assert.Equal("/backup/ssd/IMG_0001.CR3", comp.MatchedFilePath);
        Assert.True(comp.HasMatchedFilePath);
        Assert.False(comp.HasFailureReason);

        Assert.Equal(2, comp.Rows.Count);

        HashComparisonRowViewModel headRow = comp.Rows[0];
        Assert.Equal("Head", headRow.ChunkName);
        Assert.Equal("0x1111222233334444", headRow.SourceHashHex);
        Assert.Equal("0x1111222233334444", headRow.DestinationHashHex);
        Assert.Equal("✓ MATCH", headRow.MatchBadgeText);
        Assert.Equal("#10B981", headRow.MatchBadgeColor);
        Assert.True(headRow.IsMatch);
        Assert.False(headRow.IsMismatch);

        HashComparisonRowViewModel tailRow = comp.Rows[1];
        Assert.Equal("Tail", tailRow.ChunkName);
        Assert.True(tailRow.IsMatch);
    }

    [Fact]
    public void DestinationComparisons_FlagsMismatch_WhenHashesDiffer()
    {
        ulong srcHead = 0x1111222233334444;
        ulong srcTail = 0x5555666677778888;
        ulong corruptTail = 0x9999888877776666;

        MediaFile source = new(
            RelativePath: "DCIM/100CANON/IMG_0001.CR3",
            FullPath: "/card/DCIM/100CANON/IMG_0001.CR3",
            FileLength: 1024 * 1024,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw,
            HeadHash: srcHead,
            TailHash: srcTail);

        FileMatchStatus corruptDest = new(
            DestinationId: "ssd_corrupt",
            DestinationRootPath: "/backup/corrupt",
            Status: MediaStatus.Corrupt,
            MatchedFilePath: "/backup/corrupt/IMG_0001.CR3",
            FailureReason: "Checksum mismatch on head/tail chunks.",
            DestinationHeadHash: srcHead,
            DestinationTailHash: corruptTail);

        Dictionary<string, FileMatchStatus> destStatuses = new()
        {
            ["ssd_corrupt"] = corruptDest
        };

        VerificationResultItem resultItem = new(source, destStatuses);
        MediaItemViewModel vm = new(resultItem);

        DestinationComparisonViewModel comp = vm.DestinationComparisons[0];
        Assert.Equal("✕ MISMATCH", comp.StatusText);
        Assert.Equal("#EF4444", comp.StatusBadgeColor);
        Assert.True(comp.HasFailureReason);
        Assert.Equal("Checksum mismatch on head/tail chunks.", comp.FailureReason);

        HashComparisonRowViewModel headRow = comp.Rows[0];
        Assert.True(headRow.IsMatch);

        HashComparisonRowViewModel tailRow = comp.Rows[1];
        Assert.False(tailRow.IsMatch);
        Assert.True(tailRow.IsMismatch);
        Assert.Equal("✕ MISMATCH", tailRow.MatchBadgeText);
        Assert.Equal("#EF4444", tailRow.MatchBadgeColor);
        Assert.Equal("#EF4444", tailRow.DestinationHashColor);
    }

    [Fact]
    public void DestinationComparisons_HandlesMissingDestination()
    {
        MediaFile source = new(
            RelativePath: "DCIM/100CANON/IMG_0001.CR3",
            FullPath: "/card/DCIM/100CANON/IMG_0001.CR3",
            FileLength: 1024 * 1024,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw,
            HeadHash: 0x1111222233334444,
            TailHash: 0x5555666677778888);

        FileMatchStatus missingDest = new(
            DestinationId: "nas_missing",
            DestinationRootPath: "/backup/nas",
            Status: MediaStatus.Missing);

        Dictionary<string, FileMatchStatus> destStatuses = new()
        {
            ["nas_missing"] = missingDest
        };

        VerificationResultItem resultItem = new(source, destStatuses);
        MediaItemViewModel vm = new(resultItem);

        DestinationComparisonViewModel comp = vm.DestinationComparisons[0];
        Assert.Equal("MISSING", comp.StatusText);
        Assert.Equal("#F59E0B", comp.StatusBadgeColor);
        Assert.False(comp.HasMatchedFilePath);

        HashComparisonRowViewModel headRow = comp.Rows[0];
        Assert.Equal("MISSING", headRow.MatchBadgeText);
        Assert.Equal("#F59E0B", headRow.MatchBadgeColor);
        Assert.Equal("Missing", headRow.DestinationHashHex);
        Assert.False(headRow.IsMatch);
        Assert.False(headRow.IsMismatch);
    }
}
