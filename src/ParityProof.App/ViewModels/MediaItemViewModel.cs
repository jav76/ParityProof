using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;

namespace ParityProof.App.ViewModels;

public sealed partial class MediaItemViewModel : ViewModelBase
{
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

    public MediaItemViewModel(VerificationResultItem item)
    {
        Item = item;
    }
}
