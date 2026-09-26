using System;
using System.IO;
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

    [Theory]
    [InlineData(false, true, "", true)] // USB card reader reporting a rotational queue
    [InlineData(true, true, "SD/MMC Reader", true)] // Non-rotational card reader
    [InlineData(true, true, "Extreme 55AE", false)] // Removable-flagged portable SSD
    [InlineData(true, true, "Portable SSD T7", false)]
    [InlineData(true, false, "", false)] // Internal or USB SSD without the removable flag
    [InlineData(false, false, "", null)] // Rotational fixed disk: inconclusive, falls back to DriveInfo
    public void ClassifyLinuxBlockDevice_SeparatesCardReadersFromSolidStateDrives(
        bool isNonRotational,
        bool isRemovableFlag,
        string model,
        bool? expected)
    {
        bool? actual = StorageMediaDetector.ClassifyLinuxBlockDevice(isNonRotational, isRemovableFlag, model);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/media/jaret/EOS/DCIM", "/media/jaret/EOS", true)]
    [InlineData("/media/jaret/EOS", "/media/jaret/EOS", true)]
    [InlineData("/media/jaret/EOS_DIGITAL/DCIM", "/media/jaret/EOS", false)]
    [InlineData("/home/jaret/photos", "/", true)]
    [InlineData("/media/jaret/EOS/DCIM", "/media/jaret/EOS/", true)]
    [InlineData("/media/jaret/EOS", "", false)]
    public void IsUnderMountPoint_RequiresPathSeparatorBoundary(string fullPath, string mountPoint, bool expected)
    {
        Assert.Equal(expected, StorageMediaDetector.IsUnderMountPoint(fullPath, mountPoint));
    }

    [Fact]
    public void IsRemovableStorage_LocalFolderNamedLikeCard_IsNotRemovableByName()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SD_CARD_imports_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.False(StorageMediaDetector.IsRemovableStorage(folder));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }
}
