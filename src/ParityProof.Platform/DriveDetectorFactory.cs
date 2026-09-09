using System;
using ParityProof.Core.Interfaces;
using ParityProof.Platform.Common;
using ParityProof.Platform.Linux;
using ParityProof.Platform.MacOS;
using ParityProof.Platform.Windows;

namespace ParityProof.Platform;

public static class DriveDetectorFactory
{
    public static IDriveDetector Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDriveDetector();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacDriveDetector();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxDriveDetector();
        }

        return new PollingDriveDetector();
    }
}
