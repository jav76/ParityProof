using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Engine.Matching;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class ContentAddressedMatcherTests : IDisposable
{
    private readonly string _testDir;
    private readonly ContentAddressedMatcher _matcher = new();

    public ContentAddressedMatcherTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_MatcherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task MatchFileAsync_ReorganizedDateFolders_MatchesCorrectly()
    {
        string srcPath = Path.Combine(_testDir, "card", "DCIM", "100CANON", "IMG_0001.CR3");
        string dstPath = Path.Combine(_testDir, "backup", "2026", "09", "08", "IMG_0001.CR3");

        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);

        byte[] payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);

        File.WriteAllBytes(srcPath, payload);
        File.WriteAllBytes(dstPath, payload);

        MediaFile srcFile = new(
            RelativePath: "DCIM/100CANON/IMG_0001.CR3",
            FullPath: srcPath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        MediaFile dstFile = new(
            RelativePath: "2026/09/08/IMG_0001.CR3",
            FullPath: dstPath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        BackupDestination destination = new("dest1", "Primary SSD", Path.Combine(_testDir, "backup"));
        Dictionary<long, List<MediaFile>> index = new()
        {
            [payload.Length] = new List<MediaFile> { dstFile }
        };

        FileMatchStatus quickStatus = await _matcher.MatchFileAsync(
            srcFile,
            destination,
            index,
            VerificationMode.Quick);

        Assert.Equal(MediaStatus.Verified, quickStatus.Status);
        Assert.Equal(dstPath, quickStatus.MatchedFilePath);

        FileMatchStatus fullStatus = await _matcher.MatchFileAsync(
            srcFile,
            destination,
            index,
            VerificationMode.Full);

        Assert.Equal(MediaStatus.Verified, fullStatus.Status);
    }

    [Fact]
    public async Task MatchFileAsync_CorruptedFile_DetectsCorruption()
    {
        string srcPath = Path.Combine(_testDir, "card", "IMG_CORRUPT.CR3");
        string dstPath = Path.Combine(_testDir, "backup", "IMG_CORRUPT.CR3");

        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);

        byte[] payloadSrc = new byte[128 * 1024];
        byte[] payloadDst = new byte[128 * 1024];
        Random.Shared.NextBytes(payloadSrc);
        Array.Copy(payloadSrc, payloadDst, payloadSrc.Length);

        // Mutate one byte in tail
        payloadDst[^1] = (byte)(payloadDst[^1] ^ 0xFF);

        File.WriteAllBytes(srcPath, payloadSrc);
        File.WriteAllBytes(dstPath, payloadDst);

        MediaFile srcFile = new(
            RelativePath: "IMG_CORRUPT.CR3",
            FullPath: srcPath,
            FileLength: payloadSrc.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        MediaFile dstFile = new(
            RelativePath: "IMG_CORRUPT.CR3",
            FullPath: dstPath,
            FileLength: payloadDst.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        BackupDestination destination = new("dest1", "Primary SSD", Path.Combine(_testDir, "backup"));
        Dictionary<long, List<MediaFile>> index = new()
        {
            [payloadDst.Length] = new List<MediaFile> { dstFile }
        };

        FileMatchStatus quickStatus = await _matcher.MatchFileAsync(
            srcFile,
            destination,
            index,
            VerificationMode.Quick);

        Assert.Equal(MediaStatus.Corrupt, quickStatus.Status);
    }

    [Fact]
    public async Task MatchFileAsync_DeepProbeMode_MatchesIdenticalFiles()
    {
        string srcPath = Path.Combine(_testDir, "card_deep", "IMG_0002.CR3");
        string dstPath = Path.Combine(_testDir, "backup_deep", "IMG_0002.CR3");

        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);

        byte[] payload = new byte[8 * 1024 * 1024]; // 8 MB (Tier 2)
        Random.Shared.NextBytes(payload);

        File.WriteAllBytes(srcPath, payload);
        File.WriteAllBytes(dstPath, payload);

        MediaFile srcFile = new(
            RelativePath: "IMG_0002.CR3",
            FullPath: srcPath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        MediaFile dstFile = new(
            RelativePath: "IMG_0002.CR3",
            FullPath: dstPath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        BackupDestination destination = new("dest1", "Primary SSD", Path.Combine(_testDir, "backup_deep"));
        Dictionary<long, List<MediaFile>> index = new()
        {
            [payload.Length] = new List<MediaFile> { dstFile }
        };

        FileMatchStatus deepStatus = await _matcher.MatchFileAsync(
            srcFile,
            destination,
            index,
            VerificationMode.Deep);

        Assert.Equal(MediaStatus.Verified, deepStatus.Status);
        Assert.Equal(dstPath, deepStatus.MatchedFilePath);
    }

    [Fact]
    public async Task MatchFileAsync_DeepProbeMode_DetectsInteriorCorruption_WhileQuickModeMissesIt()
    {
        string srcPath = Path.Combine(_testDir, "card_interior", "IMG_INTERIOR.CR3");
        string dstPath = Path.Combine(_testDir, "backup_interior", "IMG_INTERIOR.CR3");

        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);

        const int FILE_SIZE = 8 * 1024 * 1024; // 8 MB (Tier 2)
        byte[] payloadSrc = new byte[FILE_SIZE];
        byte[] payloadDst = new byte[FILE_SIZE];
        Random.Shared.NextBytes(payloadSrc);
        Array.Copy(payloadSrc, payloadDst, FILE_SIZE);

        // Mutate byte in the interior (at 50% = 4 MB), keeping head 64 KB and tail 64 KB completely identical
        payloadDst[4 * 1024 * 1024] = (byte)(payloadDst[4 * 1024 * 1024] ^ 0xFF);

        File.WriteAllBytes(srcPath, payloadSrc);
        File.WriteAllBytes(dstPath, payloadDst);

        MediaFile srcFile = new(
            RelativePath: "IMG_INTERIOR.CR3",
            FullPath: srcPath,
            FileLength: FILE_SIZE,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        MediaFile dstFile = new(
            RelativePath: "IMG_INTERIOR.CR3",
            FullPath: dstPath,
            FileLength: FILE_SIZE,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        BackupDestination destination = new("dest1", "Primary SSD", Path.Combine(_testDir, "backup_interior"));
        Dictionary<long, List<MediaFile>> index = new()
        {
            [FILE_SIZE] = new List<MediaFile> { dstFile }
        };

        // Quick mode checks only head & tail, so it should report Verified (misses the middle corruption)
        FileMatchStatus quickStatus = await _matcher.MatchFileAsync(
            srcFile,
            destination,
            index,
            VerificationMode.Quick);

        Assert.Equal(MediaStatus.Verified, quickStatus.Status);

        // Deep Probe mode samples interior slices, so it detects the corruption!
        FileMatchStatus deepStatus = await _matcher.MatchFileAsync(
            srcFile,
            destination,
            index,
            VerificationMode.Deep);

        Assert.Equal(MediaStatus.Corrupt, deepStatus.Status);
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
