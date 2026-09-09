using System;
using System.Collections.Generic;
using System.IO;
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
