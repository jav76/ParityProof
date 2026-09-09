using System;
using System.IO;
using System.Runtime.InteropServices;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Models;
using ParityProof.Platform.Common;

namespace ParityProof.Platform.Windows;

public sealed class WindowsDriveDetector : IDriveDetector
{
    public event EventHandler<DriveNotificationEventArgs>? DriveChanged;

    private readonly PollingDriveDetector _fallback = new();

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            _fallback.DriveChanged += (sender, args) => DriveChanged?.Invoke(this, args);
            _fallback.Start();
            return;
        }

        // On Windows Native, start background polling fallback alongside OS events
        _fallback.DriveChanged += (sender, args) => DriveChanged?.Invoke(this, args);
        _fallback.Start();
    }

    public void Stop()
    {
        _fallback.Stop();
    }

    public void Dispose()
    {
        _fallback.Dispose();
    }
}
