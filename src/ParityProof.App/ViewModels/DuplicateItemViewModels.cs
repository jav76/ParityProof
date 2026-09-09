using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParityProof.Core.Models;
using ParityProof.Platform.Common;

namespace ParityProof.App.ViewModels;

public sealed partial class DuplicateFileItemViewModel : ViewModelBase
{
    public DuplicateFileItem Item { get; }

    public string DestinationId => Item.DestinationId;
    public string DestinationName => Item.DestinationName;
    public bool IsPrimary => Item.IsPrimary;
    public string RelativePath => Item.RelativePath;
    public string FullPath => Item.File.FullPath;

    public string StatusBadgeText => Item.IsPrimary ? "PRIMARY" : "DUPLICATE";
    public string StatusBadgeBackground => Item.IsPrimary ? "#064E3B" : "#78350F";
    public string StatusBadgeForeground => Item.IsPrimary ? "#10B981" : "#FBBF24";

    public DuplicateFileItemViewModel(DuplicateFileItem item)
    {
        Item = item;
    }

    [RelayCommand]
    private void RevealInFolder()
    {
        FileOpener.RevealInFileManager(FullPath);
    }
}

public sealed partial class DuplicateGroupViewModel : ViewModelBase
{
    public DuplicateGroup Group { get; }

    public string GroupKey => Group.GroupKey;
    public long FileSize => Group.FileSize;
    public long ReclaimableBytes => Group.ReclaimableBytes;
    public bool IsIntraDestination => Group.IsIntraDestination;
    public bool IsCrossDestination => Group.IsCrossDestination;

    public string FileName
    {
        get
        {
            DuplicateFileItem? first = Group.Files.FirstOrDefault();
            return first is not null ? Path.GetFileName(first.File.FullPath) : GroupKey;
        }
    }

    public string Extension
    {
        get
        {
            DuplicateFileItem? first = Group.Files.FirstOrDefault();
            return first is not null ? Path.GetExtension(first.File.FullPath).ToUpperInvariant() : string.Empty;
        }
    }

    public string FileSizeFormatted => FormatBytes(FileSize);
    public string ReclaimableBytesFormatted => FormatBytes(ReclaimableBytes);

    public string RedundancyBadgeText
    {
        get
        {
            if (IsIntraDestination && IsCrossDestination)
            {
                return "SAME-DRIVE WASTE & MULTI-DRIVE";
            }

            if (IsIntraDestination)
            {
                return "SAME-DRIVE DUPLICATE WASTE";
            }

            return "MULTI-DRIVE REDUNDANCY";
        }
    }

    public string RedundancyBadgeBackground
    {
        get
        {
            if (IsIntraDestination)
            {
                return "#78350F";
            }

            return "#0C4A6E";
        }
    }

    public string RedundancyBadgeForeground
    {
        get
        {
            if (IsIntraDestination)
            {
                return "#FBBF24";
            }

            return "#38BDF8";
        }
    }

    public ObservableCollection<DuplicateFileItemViewModel> Files { get; } = new();

    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Group = group;
        foreach (DuplicateFileItem file in group.Files)
        {
            Files.Add(new DuplicateFileItemViewModel(file));
        }
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024 * 1024)):F2} GB",
            >= 1024L * 1024 => $"{(bytes / (1024.0 * 1024)):F1} MB",
            >= 1024L => $"{(bytes / 1024.0):F0} KB",
            _ => $"{bytes} B"
        };
    }
}
