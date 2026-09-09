using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ParityProof.Core.Models;

namespace ParityProof.App.ViewModels;

public sealed partial class BackupDestinationViewModel : ViewModelBase
{
    private const string COLOR_SAFE = "#10B981"; // Emerald
    private const string COLOR_WARNING = "#F59E0B"; // Amber
    private const string COLOR_CRITICAL = "#EF4444"; // Rose

    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _rootPath;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isRequired;

    [ObservableProperty]
    private long _totalSpaceBytes;

    [ObservableProperty]
    private long _freeSpaceBytes;

    [ObservableProperty]
    private string _capacityFormatted = "Checking capacity...";

    [ObservableProperty]
    private double _capacityUsedPercentage;

    [ObservableProperty]
    private string _capacityBarColor = COLOR_SAFE;

    [ObservableProperty]
    private bool _hasSpaceInfo;

    [ObservableProperty]
    private bool _hasCapacityWarning;

    [ObservableProperty]
    private string _capacityWarningText = string.Empty;

    public BackupDestinationViewModel(
        string id,
        string name,
        string rootPath,
        bool isEnabled = true,
        bool isRequired = true)
    {
        _id = id;
        _name = name;
        _rootPath = rootPath;
        _isEnabled = isEnabled;
        _isRequired = isRequired;

        RefreshCapacity();
    }

    public void RefreshCapacity()
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            HasSpaceInfo = false;
            CapacityFormatted = "Capacity unknown";
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(RootPath);
            DriveInfo? bestDrive = null;
            int bestMatchLength = -1;

            DriveInfo[] drives = DriveInfo.GetDrives();
            foreach (DriveInfo drive in drives)
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                string driveRoot = drive.RootDirectory.FullName;
                if (fullPath.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase) &&
                    driveRoot.Length > bestMatchLength)
                {
                    bestDrive = drive;
                    bestMatchLength = driveRoot.Length;
                }
            }

            if (bestDrive is not null)
            {
                TotalSpaceBytes = bestDrive.TotalSize;
                FreeSpaceBytes = bestDrive.AvailableFreeSpace;
                HasSpaceInfo = true;

                long usedBytes = Math.Max(0, TotalSpaceBytes - FreeSpaceBytes);
                CapacityUsedPercentage = TotalSpaceBytes > 0
                    ? (usedBytes / (double)TotalSpaceBytes) * 100.0
                    : 0;

                CapacityFormatted = $"{FormatBytes(FreeSpaceBytes)} free of {FormatBytes(TotalSpaceBytes)}";
                CapacityBarColor = CapacityUsedPercentage switch
                {
                    >= 95.0 => COLOR_CRITICAL,
                    >= 85.0 => COLOR_WARNING,
                    _ => COLOR_SAFE
                };
            }
            else
            {
                HasSpaceInfo = false;
                CapacityFormatted = "Capacity unknown";
            }
        }
        catch
        {
            HasSpaceInfo = false;
            CapacityFormatted = "Capacity unavailable";
        }
    }

    public void CheckRequiredSpace(long requiredBytes)
    {
        if (HasSpaceInfo && requiredBytes > FreeSpaceBytes)
        {
            HasCapacityWarning = true;
            CapacityWarningText = $"Insufficient space: needs {FormatBytes(requiredBytes)}, only {FormatBytes(FreeSpaceBytes)} free";
        }
        else
        {
            HasCapacityWarning = false;
            CapacityWarningText = string.Empty;
        }
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 * 1024 => $"{(bytes / (1024.0 * 1024 * 1024 * 1024)):F1} TB",
            >= 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024 * 1024)):F1} GB",
            >= 1024L * 1024 => $"{(bytes / (1024.0 * 1024)):F1} MB",
            >= 1024L => $"{(bytes / 1024.0):F0} KB",
            _ => $"{bytes} B"
        };
    }

    public BackupDestination ToModel()
    {
        return new BackupDestination(
            Id: Id,
            Name: Name,
            RootPath: RootPath,
            IsEnabled: IsEnabled,
            IsRequired: IsRequired);
    }
}
