using System;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Models;
using ParityProof.Platform.Common;

namespace ParityProof.Platform.MacOS;

public sealed class MacDriveDetector : IDriveDetector
{
    public event EventHandler<DriveNotificationEventArgs>? DriveChanged;

    private readonly PollingDriveDetector _fallback = new();

    public void Start()
    {
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
