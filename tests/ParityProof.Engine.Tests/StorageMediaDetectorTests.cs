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

    [Theory]
    [InlineData(@"/media/jaret/Extreme\040SSD", @"/media/jaret/Extreme SSD")]
    [InlineData(@"/media/user/Drive\011Tab", "/media/user/Drive\tTab")]
    [InlineData(@"/media/user/Backslash\134Test", @"/media/user/Backslash\Test")]
    [InlineData(@"/mnt/standard_path", @"/mnt/standard_path")]
    [InlineData("", "")]
    public void UnescapeOctal_DecodesEncodedOctalCharactersCorrectly(string input, string expected)
    {
        string actual = StorageMediaDetector.UnescapeOctal(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GetRecommendedWorkerCount_RemovableMedia_ClampsBetweenTwoAndThree()
    {
        int count = StorageMediaDetector.GetRecommendedWorkerCount("/media/jaret/SD_CARD");

        Assert.InRange(count, 2, 3);
    }
}
