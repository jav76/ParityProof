using System;
using System.IO;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Models;
using ParityProof.Platform.Common;

namespace ParityProof.Platform.Linux;

public sealed class LinuxDriveDetector : IDriveDetector
{
    public event EventHandler<DriveNotificationEventArgs>? DriveChanged;

    private readonly PollingDriveDetector _pollingDetector = new();
    private FileSystemWatcher? _mediaWatcher;
    private FileSystemWatcher? _runMediaWatcher;
    private bool _isDisposed;

    public void Start()
    {
        _pollingDetector.DriveChanged += (sender, args) => DriveChanged?.Invoke(this, args);
        _pollingDetector.Start();

        try
        {
            if (Directory.Exists("/media"))
            {
                _mediaWatcher = new FileSystemWatcher("/media")
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.DirectoryName
                };

                _mediaWatcher.Created += OnDirectoryCreated;
                _mediaWatcher.Deleted += OnDirectoryDeleted;
            }

            if (Directory.Exists("/run/media"))
            {
                _runMediaWatcher = new FileSystemWatcher("/run/media")
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.DirectoryName
                };

                _runMediaWatcher.Created += OnDirectoryCreated;
                _runMediaWatcher.Deleted += OnDirectoryDeleted;
            }
        }
        catch
        {
            // Fall back cleanly to polling detector
        }
    }

    private void OnDirectoryCreated(object sender, FileSystemEventArgs e)
    {
        DriveNotificationEventArgs args = new(
            drivePath: e.FullPath,
            volumeLabel: Path.GetFileName(e.FullPath),
            isRemovable: true,
            eventType: DriveEventType.Inserted);

        DriveChanged?.Invoke(this, args);
    }

    private void OnDirectoryDeleted(object sender, FileSystemEventArgs e)
    {
        DriveNotificationEventArgs args = new(
            drivePath: e.FullPath,
            volumeLabel: Path.GetFileName(e.FullPath),
            isRemovable: true,
            eventType: DriveEventType.Removed);

        DriveChanged?.Invoke(this, args);
    }

    public void Stop()
    {
        _pollingDetector.Stop();
        if (_mediaWatcher is not null)
        {
            _mediaWatcher.EnableRaisingEvents = false;
        }
        if (_runMediaWatcher is not null)
        {
            _runMediaWatcher.EnableRaisingEvents = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _pollingDetector.Dispose();
        _mediaWatcher?.Dispose();
        _runMediaWatcher?.Dispose();
    }
}
