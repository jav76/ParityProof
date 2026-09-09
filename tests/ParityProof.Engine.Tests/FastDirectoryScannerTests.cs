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
