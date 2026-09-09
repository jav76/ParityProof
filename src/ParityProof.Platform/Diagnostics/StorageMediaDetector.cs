using System;
using System.Collections.Generic;
using System.IO;
using ParityProof.Core.Enums;

namespace ParityProof.Platform.Diagnostics;

public static class StorageMediaDetector
{
    public const int DEFAULT_NETWORK_WORKERS = 8;
    public const int DEFAULT_SSD_WORKERS = 8;
    public const int DEFAULT_HDD_WORKERS = 2;
    public const int MIN_PARALLEL_WORKERS = 2;
    public const int DEFAULT_SD_CARD_WORKERS = 2;
    public const int MAX_SD_CARD_WORKERS = 3;
    public const int DEFAULT_FALLBACK_WORKERS = 4;

    private static readonly HashSet<string> NetworkFileSystemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nfs", "nfs4", "cifs", "smb", "smbfs", "fuse.sshfs", "davfs", "ceph", "glusterfs", "9p", "afpfs"
    };

    public static StorageMediaType Detect(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return StorageMediaType.Unknown;
            }

            string fullPath = Path.GetFullPath(path);

            if (OperatingSystem.IsLinux())
            {
                return DetectLinux(fullPath);
            }

            if (OperatingSystem.IsWindows())
            {
                return DetectWindows(fullPath);
            }

            if (OperatingSystem.IsMacOS())
            {
                return DetectMacOS(fullPath);
            }
        }
        catch
        {
            // Graceful fallback on any detection failure
        }

        return StorageMediaType.Unknown;
    }

    public static int GetRecommendedWorkerCount(string path)
    {
        int cpuCores = Environment.ProcessorCount;
        StorageMediaType mediaType = Detect(path);

        return mediaType switch
        {
            StorageMediaType.RotationalHdd => DEFAULT_HDD_WORKERS,
            StorageMediaType.NetworkShare => Math.Clamp(cpuCores, MIN_PARALLEL_WORKERS, DEFAULT_NETWORK_WORKERS),
            StorageMediaType.SolidState => Math.Clamp(cpuCores, MIN_PARALLEL_WORKERS, DEFAULT_SSD_WORKERS),
            _ => Math.Clamp(cpuCores / 2, MIN_PARALLEL_WORKERS, DEFAULT_SSD_WORKERS)
        };
    }

    public static int GetRecommendedDriveWorkers(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DEFAULT_FALLBACK_WORKERS;
        }

        int cpuCores = Environment.ProcessorCount;

        if (IsRemovableStorage(path))
        {
            // Removable media and SD cards perform best with 2 to 3 concurrent readers to prevent flash controller thrashing
            return Math.Clamp(cpuCores, DEFAULT_SD_CARD_WORKERS, MAX_SD_CARD_WORKERS);
        }

        StorageMediaType mediaType = Detect(path);

        return mediaType switch
        {
            StorageMediaType.RotationalHdd => DEFAULT_HDD_WORKERS,
            StorageMediaType.NetworkShare => Math.Clamp(cpuCores, MIN_PARALLEL_WORKERS, DEFAULT_NETWORK_WORKERS),
            StorageMediaType.SolidState => Math.Clamp(cpuCores, MIN_PARALLEL_WORKERS, DEFAULT_SSD_WORKERS),
            _ => Math.Clamp(cpuCores / 2, MIN_PARALLEL_WORKERS, DEFAULT_SSD_WORKERS)
        };
    }

    public static int GetRecommendedVerificationConcurrency(string sourcePath) =>
        GetRecommendedDriveWorkers(sourcePath);

    public static bool IsRemovableStorage(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);

            if (OperatingSystem.IsLinux())
            {
                if (fullPath.StartsWith("/media/", StringComparison.Ordinal) ||
                    fullPath.StartsWith("/run/media/", StringComparison.Ordinal))
                {
                    return true;
                }

                string baseDev = GetLinuxBaseDevice(fullPath);
                if (!string.IsNullOrEmpty(baseDev))
                {
                    string removablePath = Path.Combine("/sys/block", baseDev, "removable");
                    if (File.Exists(removablePath))
                    {
                        string val = File.ReadAllText(removablePath).Trim();
                        return string.Equals(val, "1", StringComparison.Ordinal);
                    }
                }
            }

            string? root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root))
            {
                DriveInfo driveInfo = new(root);
                if (driveInfo.DriveType == DriveType.Removable)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore detection failure
        }

        return false;
    }

    private static string GetLinuxBaseDevice(string fullPath)
    {
        if (!File.Exists("/proc/mounts"))
        {
            return string.Empty;
        }

        string longestMountPoint = string.Empty;
        string matchedDevice = string.Empty;

        foreach (string line in File.ReadLines("/proc/mounts"))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                string device = parts[0];
                string mountPoint = parts[1];

                if (fullPath.StartsWith(mountPoint, StringComparison.Ordinal) &&
                    mountPoint.Length > longestMountPoint.Length)
                {
                    longestMountPoint = mountPoint;
                    matchedDevice = device;
                }
            }
        }

        if (!matchedDevice.StartsWith("/dev/", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        string rawDev = Path.GetFileName(matchedDevice);
        string baseDev = rawDev;

        if (rawDev.StartsWith("nvme", StringComparison.OrdinalIgnoreCase))
        {
            int pIndex = rawDev.IndexOf('p', StringComparison.OrdinalIgnoreCase);
            if (pIndex > 0)
            {
                baseDev = rawDev[..pIndex];
            }
        }
        else
        {
            int digitIndex = 0;
            while (digitIndex < rawDev.Length && !char.IsAsciiDigit(rawDev[digitIndex]))
            {
                digitIndex++;
            }

            if (digitIndex > 0 && digitIndex < rawDev.Length)
            {
                baseDev = rawDev[..digitIndex];
            }
        }

        return baseDev;
    }

    private static StorageMediaType DetectLinux(string fullPath)
    {
        string longestMountPoint = string.Empty;
        string matchedFsType = string.Empty;
        string matchedDevice = string.Empty;

        if (File.Exists("/proc/mounts"))
        {
            foreach (string line in File.ReadLines("/proc/mounts"))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    string device = parts[0];
                    string mountPoint = parts[1];
                    string fsType = parts[2];

                    if (fullPath.StartsWith(mountPoint, StringComparison.Ordinal) &&
                        mountPoint.Length > longestMountPoint.Length)
                    {
                        longestMountPoint = mountPoint;
                        matchedFsType = fsType;
                        matchedDevice = device;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(matchedFsType) && NetworkFileSystemTypes.Contains(matchedFsType))
        {
            return StorageMediaType.NetworkShare;
        }

        if (!string.IsNullOrEmpty(matchedDevice) && matchedDevice.StartsWith("/dev/", StringComparison.Ordinal))
        {
            string rawDev = Path.GetFileName(matchedDevice);
            string baseDev = rawDev;

            if (rawDev.StartsWith("nvme", StringComparison.OrdinalIgnoreCase))
            {
                int pIndex = rawDev.IndexOf('p', StringComparison.OrdinalIgnoreCase);
                if (pIndex > 0)
                {
                    baseDev = rawDev[..pIndex];
                }
            }
            else
            {
                int digitIndex = 0;
                while (digitIndex < rawDev.Length && !char.IsAsciiDigit(rawDev[digitIndex]))
                {
                    digitIndex++;
                }

                if (digitIndex > 0 && digitIndex < rawDev.Length)
                {
                    baseDev = rawDev[..digitIndex];
                }
            }

            string rotationalPath = Path.Combine("/sys/block", baseDev, "queue/rotational");
            if (File.Exists(rotationalPath))
            {
                string content = File.ReadAllText(rotationalPath).Trim();
                if (string.Equals(content, "1", StringComparison.Ordinal))
                {
                    return StorageMediaType.RotationalHdd;
                }

                if (string.Equals(content, "0", StringComparison.Ordinal))
                {
                    return StorageMediaType.SolidState;
                }
            }
        }

        return StorageMediaType.SolidState;
    }

    private static StorageMediaType DetectWindows(string fullPath)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return StorageMediaType.NetworkShare;
        }

        try
        {
            string? root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root))
            {
                DriveInfo driveInfo = new(root);
                if (driveInfo.DriveType == DriveType.Network)
                {
                    return StorageMediaType.NetworkShare;
                }
            }
        }
        catch
        {
            // Ignore drive info lookup failure
        }

        return StorageMediaType.SolidState;
    }

    private static StorageMediaType DetectMacOS(string fullPath)
    {
        try
        {
            string? root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root))
            {
                DriveInfo driveInfo = new(root);
                if (driveInfo.DriveType == DriveType.Network)
                {
                    return StorageMediaType.NetworkShare;
                }
            }
        }
        catch
        {
            // Ignore drive info lookup failure
        }

        return StorageMediaType.SolidState;
    }
}
