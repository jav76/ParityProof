using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;

namespace ParityProof.App.ViewModels;

public sealed class HashComparisonRowViewModel
{
    public string ChunkName { get; }
    public string SourceHashHex { get; }
    public string DestinationHashHex { get; }
    public string MatchBadgeText { get; }
    public string MatchBadgeColor { get; }
    public string MatchBadgeBackground { get; }
    public string DestinationHashColor { get; }
    public bool IsMismatch { get; }
    public bool IsMatch { get; }
    public bool IsVisible { get; }

    public HashComparisonRowViewModel(
        string chunkName,
        ulong? sourceHash,
        ulong? destHash,
        MediaStatus destStatus,
        bool isRelevantForMode)
    {
        ChunkName = chunkName;
        SourceHashHex = sourceHash.HasValue ? $"0x{sourceHash.Value:X16}" : "None";
        DestinationHashHex = destHash.HasValue
            ? $"0x{destHash.Value:X16}"
            : (destStatus == MediaStatus.Missing ? "Missing" : "None");

        IsVisible = isRelevantForMode || sourceHash.HasValue || destHash.HasValue;

        if (sourceHash.HasValue && destHash.HasValue)
        {
            if (sourceHash.Value == destHash.Value)
            {
                MatchBadgeText = "✓ MATCH";
                MatchBadgeColor = "#10B981";
                MatchBadgeBackground = "#132E24";
                DestinationHashColor = "#E2E8F0";
                IsMatch = true;
                IsMismatch = false;
            }
            else
            {
                MatchBadgeText = "✕ MISMATCH";
                MatchBadgeColor = "#EF4444";
                MatchBadgeBackground = "#3B1820";
                DestinationHashColor = "#EF4444";
                IsMatch = false;
                IsMismatch = true;
            }
        }
        else if (destStatus == MediaStatus.Missing)
        {
            MatchBadgeText = "MISSING";
            MatchBadgeColor = "#F59E0B";
            MatchBadgeBackground = "#332414";
            DestinationHashColor = "#94A3B8";
            IsMatch = false;
            IsMismatch = false;
        }
        else if (!sourceHash.HasValue && !destHash.HasValue)
        {
            MatchBadgeText = "-";
            MatchBadgeColor = "#64748B";
            MatchBadgeBackground = "Transparent";
            DestinationHashColor = "#64748B";
            IsMatch = false;
            IsMismatch = false;
        }
        else
        {
            MatchBadgeText = "UNAVAILABLE";
            MatchBadgeColor = "#94A3B8";
            MatchBadgeBackground = "Transparent";
            DestinationHashColor = "#94A3B8";
            IsMatch = false;
            IsMismatch = false;
        }
    }
}

public sealed class DestinationComparisonViewModel
{
    private readonly List<HashComparisonRowViewModel> _rows = new();

    public string DestinationId { get; }
    public string DestinationName => DestinationId;
    public MediaStatus Status { get; }
    public string StatusText { get; }
    public string StatusBadgeColor { get; }
    public string StatusBadgeBackground { get; }
    public string? MatchedFilePath { get; }
    public bool HasMatchedFilePath => !string.IsNullOrEmpty(MatchedFilePath);
    public string? FailureReason { get; }
    public bool HasFailureReason => !string.IsNullOrEmpty(FailureReason);

    public string HeadHashHex { get; }
    public string TailHashHex { get; }
    public string DeepHashHex { get; }
    public string FullHashHex { get; }

    public IReadOnlyList<HashComparisonRowViewModel> Rows => _rows;

    public DestinationComparisonViewModel(FileMatchStatus matchStatus, MediaFile sourceFile)
    {
        DestinationId = matchStatus.DestinationId;
        Status = matchStatus.Status;
        MatchedFilePath = matchStatus.MatchedFilePath;
        FailureReason = matchStatus.FailureReason;

        HeadHashHex = matchStatus.DestinationHeadHash.HasValue
            ? $"0x{matchStatus.DestinationHeadHash.Value:X16}"
            : "None";
        TailHashHex = matchStatus.DestinationTailHash.HasValue
            ? $"0x{matchStatus.DestinationTailHash.Value:X16}"
            : "None";
        DeepHashHex = matchStatus.DestinationDeepHash.HasValue
            ? $"0x{matchStatus.DestinationDeepHash.Value:X16}"
            : "None";
        FullHashHex = matchStatus.DestinationFullHash.HasValue
            ? $"0x{matchStatus.DestinationFullHash.Value:X16}"
            : "None";

        StatusText = matchStatus.Status switch
        {
            MediaStatus.Verified => "✓ MATCH",
            MediaStatus.Corrupt => "✕ MISMATCH",
            MediaStatus.Missing => "MISSING",
            _ => "UNKNOWN"
        };

        StatusBadgeColor = matchStatus.Status switch
        {
            MediaStatus.Verified => "#10B981",
            MediaStatus.Corrupt => "#EF4444",
            MediaStatus.Missing => "#F59E0B",
            _ => "#94A3B8"
        };

        StatusBadgeBackground = matchStatus.Status switch
        {
            MediaStatus.Verified => "#132E24",
            MediaStatus.Corrupt => "#3B1820",
            MediaStatus.Missing => "#332414",
            _ => "#1E293B"
        };

        bool hasSourceOrDestDeep = sourceFile.DeepHash.HasValue || matchStatus.DestinationDeepHash.HasValue;
        bool hasSourceOrDestFull = sourceFile.FullHash.HasValue || matchStatus.DestinationFullHash.HasValue;

        _rows.Add(new HashComparisonRowViewModel(
            "Head",
            sourceFile.HeadHash,
            matchStatus.DestinationHeadHash,
            matchStatus.Status,
            isRelevantForMode: true));

        _rows.Add(new HashComparisonRowViewModel(
            "Tail",
            sourceFile.TailHash,
            matchStatus.DestinationTailHash,
            matchStatus.Status,
            isRelevantForMode: true));

        if (hasSourceOrDestDeep)
        {
            _rows.Add(new HashComparisonRowViewModel(
                "Deep",
                sourceFile.DeepHash,
                matchStatus.DestinationDeepHash,
                matchStatus.Status,
                isRelevantForMode: true));
        }

        if (hasSourceOrDestFull)
        {
            _rows.Add(new HashComparisonRowViewModel(
                "Full",
                sourceFile.FullHash,
                matchStatus.DestinationFullHash,
                matchStatus.Status,
                isRelevantForMode: true));
        }
    }
}

public sealed partial class MediaItemViewModel : ViewModelBase
{
    private readonly List<DestinationComparisonViewModel> _destinationComparisons = new();

    public VerificationResultItem Item { get; }

    public string RelativePath => Item.SourceFile.RelativePath;
    public string FileName => Path.GetFileName(Item.SourceFile.FullPath);
    public string Extension => Path.GetExtension(Item.SourceFile.FullPath).ToUpperInvariant();
    public string Category => Item.SourceFile.Category.ToString();
    public long FileLength => Item.SourceFile.FileLength;

    public string SizeFormatted
    {
        get
        {
            double mb = Item.SourceFile.FileLength / (1024.0 * 1024.0);
            return mb >= 1.0 ? $"{mb:F1} MB" : $"{Item.SourceFile.FileLength / 1024.0:F1} KB";
        }
    }

    public string StatusText => Item.IsFullyVerified
        ? "Verified"
        : Item.IsPartiallyVerified
            ? "Partial"
            : Item.HasAnyCorruption
                ? "Corrupt"
                : "Missing";

    public string StatusBadgeColor => Item.IsFullyVerified
        ? "#10B981"
        : Item.IsPartiallyVerified
            ? "#F59E0B"
            : "#F43F5E";

    public string DestinationSummary
    {
        get
        {
            List<string> parts = new();
            foreach (KeyValuePair<string, FileMatchStatus> kvp in Item.DestinationStatuses)
            {
                parts.Add($"{kvp.Key}: {kvp.Value.Status}");
            }
            return string.Join(" | ", parts);
        }
    }

    public bool IsVerified => Item.IsFullyVerified;
    public bool IsMissing => Item.IsMissingFromAll;
    public bool IsCorrupt => Item.HasAnyCorruption;
    public string FullPath => Item.SourceFile.FullPath;

    public string HeadHashHex => Item.SourceFile.HeadHash.HasValue ? $"0x{Item.SourceFile.HeadHash.Value:X16}" : "None";
    public string TailHashHex => Item.SourceFile.TailHash.HasValue ? $"0x{Item.SourceFile.TailHash.Value:X16}" : "None";
    public string DeepHashHex => Item.SourceFile.DeepHash.HasValue ? $"0x{Item.SourceFile.DeepHash.Value:X16}" : "None";
    public string FullHashHex => Item.SourceFile.FullHash.HasValue ? $"0x{Item.SourceFile.FullHash.Value:X16}" : "None";

    public bool HasDeepHash => Item.SourceFile.DeepHash.HasValue;
    public bool HasFullHash => Item.SourceFile.FullHash.HasValue;

    public IReadOnlyList<DestinationComparisonViewModel> DestinationComparisons => _destinationComparisons;
    public bool HasDestinations => _destinationComparisons.Count > 0;

    public MediaItemViewModel(VerificationResultItem item)
    {
        Item = item;
        foreach (KeyValuePair<string, FileMatchStatus> kvp in item.DestinationStatuses)
        {
            _destinationComparisons.Add(new DestinationComparisonViewModel(kvp.Value, item.SourceFile));
        }
    }
}
