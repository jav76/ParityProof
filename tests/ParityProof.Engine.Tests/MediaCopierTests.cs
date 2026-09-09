using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Engine.Transfer;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class MediaCopierTests : IDisposable
{
    private readonly string _testDir;
    private readonly MediaCopier _copier = new();

    public MediaCopierTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_CopierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_CopiesAndVerifiesIntegrity()
    {
        string srcDir = Path.Combine(_testDir, "card");
        string dstDir = Path.Combine(_testDir, "backup");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        string srcFilePath = Path.Combine(srcDir, "SUBDIR", "PHOTO_01.CR3");
        Directory.CreateDirectory(Path.GetDirectoryName(srcFilePath)!);

        byte[] payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(srcFilePath, payload);

        MediaFile missingFile = new(
            RelativePath: "SUBDIR/PHOTO_01.CR3",
            FullPath: srcFilePath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { missingFile },
            dstDir);

        Assert.Equal(1, copied);

        string expectedDstPath = Path.Combine(dstDir, "SUBDIR", "PHOTO_01.CR3");
        Assert.True(File.Exists(expectedDstPath));
        Assert.Equal(payload.Length, new FileInfo(expectedDstPath).Length);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_MultiDestinationFanOut_CopiesToAllDestinationsInSinglePass()
    {
        string srcDir = Path.Combine(_testDir, "src_card");
        string dstDir1 = Path.Combine(_testDir, "dst_nvme");
        string dstDir2 = Path.Combine(_testDir, "dst_ssd");
        string dstDir3 = Path.Combine(_testDir, "dst_archive");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir1);
        Directory.CreateDirectory(dstDir2);
        Directory.CreateDirectory(dstDir3);

        string srcFilePath = Path.Combine(srcDir, "DCIM", "100EOS", "CLIP_001.MOV");
        Directory.CreateDirectory(Path.GetDirectoryName(srcFilePath)!);

        // 256 KB file spanning multiple 64 KB chunks
        byte[] payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(srcFilePath, payload);

        MediaFile missingFile = new(
            RelativePath: "DCIM/100EOS/CLIP_001.MOV",
            FullPath: srcFilePath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.Video);

        List<string> targetDestinations = new() { dstDir1, dstDir2, dstDir3 };

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { missingFile },
            targetDestinations);

        Assert.Equal(1, copied);

        foreach (string destDir in targetDestinations)
        {
            string expectedPath = Path.Combine(destDir, "DCIM", "100EOS", "CLIP_001.MOV");
            Assert.True(File.Exists(expectedPath));
            Assert.Equal(payload.Length, new FileInfo(expectedPath).Length);

            byte[] destBytes = File.ReadAllBytes(expectedPath);
            Assert.Equal(payload, destBytes);
        }
    }

    [Fact]
    public async Task CopyMissingFilesAsync_SmallFileUnder64Kb_CopiesAndVerifies()
    {
        string srcDir = Path.Combine(_testDir, "src_small");
        string dstDir = Path.Combine(_testDir, "dst_small");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        string srcFilePath = Path.Combine(srcDir, "THUMB.JPG");
        byte[] payload = new byte[16 * 1024]; // 16 KB (under 64 KB boundary)
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(srcFilePath, payload);

        MediaFile missingFile = new(
            RelativePath: "THUMB.JPG",
            FullPath: srcFilePath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoStandard);

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { missingFile },
            dstDir);

        Assert.Equal(1, copied);
        string expectedPath = Path.Combine(dstDir, "THUMB.JPG");
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(payload.Length, new FileInfo(expectedPath).Length);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_PreExistingBackupOnOneDestination_IsPreservedWhenTransferCancelled()
    {
        string srcDir = Path.Combine(_testDir, "src_qa001");
        string dstDir1 = Path.Combine(_testDir, "dst_existing");
        string dstDir2 = Path.Combine(_testDir, "dst_new");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir1);
        Directory.CreateDirectory(dstDir2);

        string fileName = "PHOTO_QA001.CR3";
        string srcFilePath = Path.Combine(srcDir, fileName);
        byte[] payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(srcFilePath, payload);

        // Pre-create identical intact backup on Destination 1
        string dst1FilePath = Path.Combine(dstDir1, fileName);
        File.WriteAllBytes(dst1FilePath, payload);

        MediaFile missingFile = new(
            RelativePath: fileName,
            FullPath: srcFilePath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Cancel immediately

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _copier.CopyMissingFilesAsync(
                new[] { missingFile },
                new List<string> { dstDir1, dstDir2 },
                cancellationToken: cts.Token);
        });

        // Pre-existing backup on Destination 1 MUST still exist and be intact!
        Assert.True(File.Exists(dst1FilePath), "Pre-existing backup file on Destination 1 must not be deleted on cancellation.");
        Assert.Equal(payload.Length, new FileInfo(dst1FilePath).Length);
        Assert.Equal(payload, File.ReadAllBytes(dst1FilePath));
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
            // Ignore cleanup errors
        }
    }
}
