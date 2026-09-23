using System;
using System.Collections.Generic;
using ParityProof.Platform.Linux;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class LinuxDriveDetectorTests : IDisposable
{
    private readonly LinuxDriveDetector _detector = new();

    [Fact]
    public void ReadProcMounts_ReturnsMountsWithoutException()
    {
        Dictionary<string, string> mounts = LinuxDriveDetector.ReadProcMounts();

        Assert.NotNull(mounts);
        // On Linux machines with active desktop mounts, mounts will be populated.
        // Even if empty, it must never throw or block.
    }

    [Fact]
    public void LinuxDriveDetector_StartAndStop_ExecutesCleanlyWithoutBlocking()
    {
        _detector.Start();
        _detector.Stop();
    }

    public void Dispose()
    {
        _detector.Dispose();
    }
}
