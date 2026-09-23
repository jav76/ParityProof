using System;
using System.Collections.Generic;
using System.IO;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Engine.IO;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class FastDirectoryScannerTests : IDisposable
{
    private readonly string _testDir;

    public FastDirectoryScannerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_ScannerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public void ScanDirectory_FiltersAndCategorizesMediaCorrectly()
    {
        string subDir = Path.Combine(_testDir, "DCIM", "100EOS");
        Directory.CreateDirectory(subDir);

        File.WriteAllBytes(Path.Combine(subDir, "IMG_0001.CR3"), new byte[500]);
        File.WriteAllBytes(Path.Combine(subDir, "IMG_0002.JPG"), new byte[200]);
        File.WriteAllBytes(Path.Combine(subDir, "IMG_0003.MOV"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(subDir, "IMG_0001.XMP"), new byte[50]);
        File.WriteAllBytes(Path.Combine(subDir, "notes.txt"), new byte[30]);

        IReadOnlyList<MediaFile> photosOnly = FastDirectoryScanner.ScanDirectory(
            _testDir,
            FilterPreset.PhotosOnly);

        Assert.Equal(2, photosOnly.Count);
        Assert.Contains(photosOnly, f => f.Category == MediaCategory.PhotoRaw);
        Assert.Contains(photosOnly, f => f.Category == MediaCategory.PhotoStandard);

        IReadOnlyList<MediaFile> allMediaWithSidecars = FastDirectoryScanner.ScanDirectory(
            _testDir,
            FilterPreset.AllCameraMediaWithSidecars);

        Assert.Equal(4, allMediaWithSidecars.Count);
        Assert.Contains(allMediaWithSidecars, f => f.Category == MediaCategory.Video);
        Assert.Contains(allMediaWithSidecars, f => f.Category == MediaCategory.Sidecar);
        Assert.DoesNotContain(allMediaWithSidecars, f => f.FullPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanDirectory_DoesNotInfiniteLoopOnCircularSymlinks()
    {
        string mediaDir = Path.Combine(_testDir, "symlink_test", "DCIM");
        Directory.CreateDirectory(mediaDir);

        File.WriteAllBytes(Path.Combine(mediaDir, "PHOTO1.JPG"), new byte[512]);

        string loopDir = Path.Combine(mediaDir, "loop_link");
        try
        {
            Directory.CreateSymbolicLink(loopDir, _testDir);
        }
        catch (Exception)
        {
            return;
        }

        IReadOnlyList<MediaFile> files = FastDirectoryScanner.ScanDirectory(
            _testDir,
            FilterPreset.PhotosOnly);

        Assert.Single(files);
        Assert.EndsWith("PHOTO1.JPG", files[0].FullPath);
    }

    [Fact]
    public void ScanDirectory_SkipsNasSnapshotAndTrashDirectories()
    {
        string validDir = Path.Combine(_testDir, "ValidMedia");
        string zfsDir = Path.Combine(_testDir, ".zfs", "snapshot", "hourly.0");
        string snapDir = Path.Combine(_testDir, "#snapshot", "daily");
        string trashDir = Path.Combine(_testDir, ".Trash-1000");

        Directory.CreateDirectory(validDir);
        Directory.CreateDirectory(zfsDir);
        Directory.CreateDirectory(snapDir);
        Directory.CreateDirectory(trashDir);

        File.WriteAllBytes(Path.Combine(validDir, "KEEP_ME.JPG"), new byte[100]);
        File.WriteAllBytes(Path.Combine(zfsDir, "IGNORE_ZFS.JPG"), new byte[100]);
        File.WriteAllBytes(Path.Combine(snapDir, "IGNORE_SNAP.JPG"), new byte[100]);
        File.WriteAllBytes(Path.Combine(trashDir, "IGNORE_TRASH.JPG"), new byte[100]);

        IReadOnlyList<MediaFile> files = FastDirectoryScanner.ScanDirectory(
            _testDir,
            FilterPreset.PhotosOnly);

        Assert.Single(files);
        Assert.EndsWith("KEEP_ME.JPG", files[0].FullPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup exceptions
        }
    }
}
