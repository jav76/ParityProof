using System;
using ParityProof.Platform.Diagnostics;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class StorageMediaDetectorTests
{
    [Theory]
    [InlineData("mmcblk0p1", "mmcblk0")]
    [InlineData("mmcblk0", "mmcblk0")]
    [InlineData("mmcblk1p2", "mmcblk1")]
    [InlineData("nvme0n1p1", "nvme0n1")]
    [InlineData("nvme0n1", "nvme0n1")]
    [InlineData("sda1", "sda")]
    [InlineData("sda", "sda")]
    [InlineData("sdb3", "sdb")]
    public void NormalizeLinuxBaseDevice_ResolvesCorrectBaseBlockDevice(string rawDevice, string expectedBaseDevice)
    {
        string actual = StorageMediaDetector.NormalizeLinuxBaseDevice(rawDevice);

        Assert.Equal(expectedBaseDevice, actual);
    }
}
