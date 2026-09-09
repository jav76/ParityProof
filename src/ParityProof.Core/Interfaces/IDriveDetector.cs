using System;
using ParityProof.Core.Models;

namespace ParityProof.Core.Interfaces;

public interface IDriveDetector : IDisposable
{
    event EventHandler<DriveNotificationEventArgs>? DriveChanged;
    void Start();
    void Stop();
}
