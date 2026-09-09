using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Models;

namespace ParityProof.Platform.Common;

public sealed class PollingDriveDetector : IDriveDetector
{
    private const int POLL_INTERVAL_MS = 1500;

    private readonly HashSet<string> _knownDrives = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private bool _isDisposed;

    public event EventHandler<DriveNotificationEventArgs>? DriveChanged;

    public void Start()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(PollingDriveDetector));
        }

        DriveInfo[] initialDrives = DriveInfo.GetDrives();
        foreach (DriveInfo drive in initialDrives)
        {
            if (drive.IsReady)
            {
                _knownDrives.Add(drive.RootDirectory.FullName);
            }
        }

        _timer = new Timer(OnPoll, null, POLL_INTERVAL_MS, POLL_INTERVAL_MS);
    }

    private void OnPoll(object? state)
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            DriveInfo[] currentDrives = DriveInfo.GetDrives();
            HashSet<string> currentSet = new(StringComparer.OrdinalIgnoreCase);

            foreach (DriveInfo drive in currentDrives)
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                string path = drive.RootDirectory.FullName;
                currentSet.Add(path);

                if (!_knownDrives.Contains(path))
                {
                    _knownDrives.Add(path);
                    DriveNotificationEventArgs args = new(
                        drivePath: path,
                        volumeLabel: string.IsNullOrEmpty(drive.VolumeLabel) ? "Removable Drive" : drive.VolumeLabel,
                        isRemovable: drive.DriveType == DriveType.Removable,
                        eventType: DriveEventType.Inserted);

                    DriveChanged?.Invoke(this, args);
                }
            }

            List<string> removed = new();
            foreach (string path in _knownDrives)
            {
                if (!currentSet.Contains(path))
                {
                    removed.Add(path);
                }
            }

            foreach (string path in removed)
            {
                _knownDrives.Remove(path);
                DriveNotificationEventArgs args = new(
                    drivePath: path,
                    volumeLabel: "Ejected Drive",
                    isRemovable: true,
                    eventType: DriveEventType.Removed);

                DriveChanged?.Invoke(this, args);
            }
        }
        catch
        {
            // Ignore temporary drive enumeration exceptions
        }
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timer?.Dispose();
    }
}
