using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Models;
using ParityProof.Platform.Diagnostics;

namespace ParityProof.Platform.Linux;

public sealed class LinuxDriveDetector : IDriveDetector
{
    private const int POLL_INTERVAL_MS = 2000;

    public event EventHandler<DriveNotificationEventArgs>? DriveChanged;

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, string> _knownMounts = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private Timer? _pollTimer;
    private int _isPolling;
    private bool _isDisposed;

    public void Start()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(LinuxDriveDetector));
        }

        lock (_lock)
        {
            Dictionary<string, string> initialMounts = ReadProcMounts();
            foreach (KeyValuePair<string, string> kvp in initialMounts)
            {
                _knownMounts[kvp.Key] = kvp.Value;
            }

            SetupWatchers();
            _pollTimer = new Timer(OnPollMounts, null, POLL_INTERVAL_MS, POLL_INTERVAL_MS);
        }
    }

    private void SetupWatchers()
    {
        string userName = Environment.UserName;
        List<string> candidatePaths = new()
        {
            $"/media/{userName}",
            $"/run/media/{userName}",
            "/media",
            "/run/media"
        };

        HashSet<string> attached = new(StringComparer.Ordinal);

        foreach (string path in candidatePaths)
        {
            if (Directory.Exists(path) && attached.Add(path))
            {
                try
                {
                    FileSystemWatcher watcher = new(path)
                    {
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true,
                        NotifyFilter = NotifyFilters.DirectoryName
                    };

                    watcher.Created += OnMountPointCreated;
                    watcher.Deleted += OnMountPointDeleted;
                    _watchers.Add(watcher);
                }
                catch
                {
                    // Fall back to /proc/mounts polling if inotify watcher fails
                }
            }
        }
    }

    private void OnMountPointCreated(object sender, FileSystemEventArgs e)
    {
        string createdPath = e.FullPath;
        string userName = Environment.UserName;

        if (string.Equals(createdPath, $"/media/{userName}", StringComparison.Ordinal) ||
            string.Equals(createdPath, $"/run/media/{userName}", StringComparison.Ordinal))
        {
            AttachUserWatcher(createdPath);
            return;
        }

        CheckAndNotifyMounts();
    }

    private void OnMountPointDeleted(object sender, FileSystemEventArgs e)
    {
        string deletedPath = e.FullPath;

        lock (_lock)
        {
            if (_knownMounts.TryGetValue(deletedPath, out string? volumeLabel))
            {
                _knownMounts.Remove(deletedPath);

                DriveNotificationEventArgs args = new(
                    drivePath: deletedPath,
                    volumeLabel: volumeLabel,
                    isRemovable: true,
                    eventType: DriveEventType.Removed);

                DriveChanged?.Invoke(this, args);
            }
        }
    }

    private void AttachUserWatcher(string userPath)
    {
        lock (_lock)
        {
            foreach (FileSystemWatcher existing in _watchers)
            {
                if (string.Equals(existing.Path, userPath, StringComparison.Ordinal))
                {
                    return;
                }
            }

            try
            {
                FileSystemWatcher watcher = new(userPath)
                {
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.DirectoryName
                };

                watcher.Created += OnMountPointCreated;
                watcher.Deleted += OnMountPointDeleted;
                _watchers.Add(watcher);
            }
            catch
            {
                // Fall back cleanly to polling
            }
        }
    }

    private void OnPollMounts(object? state)
    {
        if (_isDisposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0)
        {
            return;
        }

        try
        {
            CheckAndNotifyMounts();
        }
        finally
        {
            Interlocked.Exchange(ref _isPolling, 0);
        }
    }

    private void CheckAndNotifyMounts()
    {
        Dictionary<string, string> currentMounts = ReadProcMounts();
        List<DriveNotificationEventArgs> notifications = new();

        lock (_lock)
        {
            foreach (KeyValuePair<string, string> current in currentMounts)
            {
                if (!_knownMounts.ContainsKey(current.Key))
                {
                    _knownMounts[current.Key] = current.Value;
                    notifications.Add(new DriveNotificationEventArgs(
                        drivePath: current.Key,
                        volumeLabel: current.Value,
                        isRemovable: true,
                        eventType: DriveEventType.Inserted));
                }
            }

            List<string> removedKeys = new();
            foreach (KeyValuePair<string, string> known in _knownMounts)
            {
                if (!currentMounts.ContainsKey(known.Key))
                {
                    removedKeys.Add(known.Key);
                    notifications.Add(new DriveNotificationEventArgs(
                        drivePath: known.Key,
                        volumeLabel: known.Value,
                        isRemovable: true,
                        eventType: DriveEventType.Removed));
                }
            }

            foreach (string removedKey in removedKeys)
            {
                _knownMounts.Remove(removedKey);
            }
        }

        foreach (DriveNotificationEventArgs args in notifications)
        {
            DriveChanged?.Invoke(this, args);
        }
    }

    public static Dictionary<string, string> ReadProcMounts()
    {
        Dictionary<string, string> mounts = new(StringComparer.Ordinal);
        if (!File.Exists("/proc/mounts"))
        {
            return mounts;
        }

        try
        {
            foreach (string line in File.ReadLines("/proc/mounts"))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    continue;
                }

                string device = parts[0];
                string mountPoint = StorageMediaDetector.UnescapeOctal(parts[1]);

                if (!mountPoint.StartsWith("/media/", StringComparison.Ordinal) &&
                    !mountPoint.StartsWith("/run/media/", StringComparison.Ordinal))
                {
                    continue;
                }

                string volumeLabel = Path.GetFileName(mountPoint);
                if (string.IsNullOrEmpty(volumeLabel))
                {
                    volumeLabel = "Removable Media";
                }

                mounts[mountPoint] = volumeLabel;
            }
        }
        catch
        {
            // Transient read errors ignored
        }

        return mounts;
    }

    public void Stop()
    {
        _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        lock (_lock)
        {
            foreach (FileSystemWatcher watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                }
                catch
                {
                    // Ignore disposal error
                }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _pollTimer?.Dispose();

        lock (_lock)
        {
            foreach (FileSystemWatcher watcher in _watchers)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
            _knownMounts.Clear();
        }
    }
}
