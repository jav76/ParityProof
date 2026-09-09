using System;
using ParityProof.Core.Enums;

namespace ParityProof.Core.Models;

public sealed class DriveNotificationEventArgs : EventArgs
{
    public string DrivePath { get; }
    public string VolumeLabel { get; }
    public bool IsRemovable { get; }
    public DriveEventType EventType { get; }

    public DriveNotificationEventArgs(
        string drivePath,
        string volumeLabel,
        bool isRemovable,
        DriveEventType eventType)
    {
        DrivePath = drivePath;
        VolumeLabel = volumeLabel;
        IsRemovable = isRemovable;
        EventType = eventType;
    }
}
