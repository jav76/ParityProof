using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Engine.IO;
using ParityProof.Platform.Diagnostics;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class ParallelDirectoryScannerTests : IDisposable
{
    private readonly string _testDir;

    public ParallelDirectoryScannerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_ParallelTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task ScanDirectoryAsync_NestedDirectories_DiscoversAllMediaAcrossParallelWorkers()
    {
        for (int b = 1; b <= 4; b++)
        {
            string branchDir = Path.Combine(_testDir, $"Branch_{b}", "Subfolder", "Media");
            Directory.CreateDirectory(branchDir);

            for (int f = 1; f <= 5; f++)
            {
                File.WriteAllBytes(Path.Combine(branchDir, $"RAW_{b}_{f}.ARW"), new byte[1024]);
                File.WriteAllBytes(Path.Combine(branchDir, $"PIC_{b}_{f}.JPG"), new byte[512]);
                File.WriteAllBytes(Path.Combine(branchDir, $"DOC_{b}_{f}.PDF"), new byte[256]);
            }
        }

        IReadOnlyList<MediaFile> discovered = await FastDirectoryScanner.ScanDirectoryAsync(
            _testDir,
            FilterPreset.PhotosOnly);

        Assert.Equal(40, discovered.Count);
        Assert.All(discovered, file =>
        {
            Assert.True(file.FullPath.EndsWith(".ARW", StringComparison.OrdinalIgnoreCase) ||
                        file.FullPath.EndsWith(".JPG", StringComparison.OrdinalIgnoreCase));
            Assert.False(file.FullPath.EndsWith(".PDF", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task ScanDirectoryAsync_SkipsIgnoredSubtreesCompletely()
    {
        string normalDir = Path.Combine(_testDir, "ValidFolder");
        string gitDir = Path.Combine(_testDir, ".git", "objects");
        string nodeModulesDir = Path.Combine(_testDir, "node_modules", "package");
        string eaDir = Path.Combine(_testDir, "@eaDir");
        string recycleDir = Path.Combine(_testDir, "#recycle");

        Directory.CreateDirectory(normalDir);
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(nodeModulesDir);
        Directory.CreateDirectory(eaDir);
        Directory.CreateDirectory(recycleDir);

        File.WriteAllBytes(Path.Combine(normalDir, "VALID_PHOTO.JPG"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(gitDir, "GIT_PHOTO.JPG"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(nodeModulesDir, "NODE_PHOTO.JPG"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(eaDir, "EA_PHOTO.JPG"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(recycleDir, "RECYCLE_PHOTO.JPG"), new byte[1024]);

        IReadOnlyList<MediaFile> discovered = await FastDirectoryScanner.ScanDirectoryAsync(
            _testDir,
            FilterPreset.PhotosOnly);

        Assert.Single(discovered);
        Assert.Equal("VALID_PHOTO.JPG", Path.GetFileName(discovered[0].FullPath));
    }

    [Fact]
    public void StorageMediaDetector_ReturnsValidWorkerCount()
    {
        int count = StorageMediaDetector.GetRecommendedWorkerCount(_testDir);
        Assert.InRange(count, StorageMediaDetector.MIN_PARALLEL_WORKERS, StorageMediaDetector.DEFAULT_SSD_WORKERS);
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
