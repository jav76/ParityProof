using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Engine.Cache;
using ParityProof.Engine.Matching;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class DuplicateAnalyzerTests : IDisposable
{
    private readonly string _testDir;
    private readonly DuplicateAnalyzer _analyzer;

    public DuplicateAnalyzerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_DupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _analyzer = new DuplicateAnalyzer();
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_NoDuplicates_ReturnsEmptyResult()
    {
        string destDir = Path.Combine(_testDir, "dest1");
        Directory.CreateDirectory(destDir);

        byte[] bytes1 = new byte[1024];
        byte[] bytes2 = new byte[2048];
        Random.Shared.NextBytes(bytes1);
        Random.Shared.NextBytes(bytes2);

        string file1 = Path.Combine(destDir, "file1.jpg");
        string file2 = Path.Combine(destDir, "file2.jpg");
        File.WriteAllBytes(file1, bytes1);
        File.WriteAllBytes(file2, bytes2);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("d1", "Dest 1", destDir)
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["d1"] = new List<MediaFile>
            {
                new("file1.jpg", file1, bytes1.Length, DateTime.UtcNow, MediaCategory.PhotoStandard),
                new("file2.jpg", file2, bytes2.Length, DateTime.UtcNow, MediaCategory.PhotoStandard)
            }
        };

        List<MediaFile> sourceFiles = new()
        {
            new("file1.jpg", file1, bytes1.Length, DateTime.UtcNow, MediaCategory.PhotoStandard),
            new("file2.jpg", file2, bytes2.Length, DateTime.UtcNow, MediaCategory.PhotoStandard)
        };

        DuplicateAnalysisResult result = await _analyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.Quick);

        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalDuplicateCopies);
        Assert.Equal(0L, result.TotalReclaimableBytes);
        Assert.Equal(0, result.CrossDestinationRedundantFileCount);
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_IntraDestinationDuplicatesInSameFolder_IdentifiesDuplicatesAndReclaimableSpace()
    {
        string destDir = Path.Combine(_testDir, "dest_intra");
        Directory.CreateDirectory(destDir);

        byte[] content = new byte[100 * 1024];
        Random.Shared.NextBytes(content);

        string file1 = Path.Combine(destDir, "IMG_0001.CR3");
        string file2 = Path.Combine(destDir, "IMG_0001_copy.CR3");
        File.WriteAllBytes(file1, content);
        File.WriteAllBytes(file2, content);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("d1", "Primary SSD", destDir)
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["d1"] = new List<MediaFile>
            {
                new("IMG_0001.CR3", file1, content.Length, DateTime.UtcNow, MediaCategory.PhotoRaw),
                new("IMG_0001_copy.CR3", file2, content.Length, DateTime.UtcNow, MediaCategory.PhotoRaw)
            }
        };

        List<MediaFile> sourceFiles = new()
        {
            new("IMG_0001.CR3", file1, content.Length, DateTime.UtcNow, MediaCategory.PhotoRaw)
        };

        DuplicateAnalysisResult result = await _analyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.Quick);

        Assert.Single(result.Groups);
        DuplicateGroup group = result.Groups[0];
        Assert.True(group.IsIntraDestination);
        Assert.False(group.IsCrossDestination);
        Assert.Equal(content.Length, group.FileSize);
        Assert.Equal(content.Length, group.ReclaimableBytes);
        Assert.Equal(2, group.Files.Count);

        DuplicateFileItem primary = Assert.Single(group.Files, f => f.IsPrimary);
        DuplicateFileItem duplicate = Assert.Single(group.Files, f => !f.IsPrimary);
        Assert.Equal("IMG_0001.CR3", primary.RelativePath);
        Assert.Equal("IMG_0001_copy.CR3", duplicate.RelativePath);

        Assert.Equal(1, result.TotalDuplicateCopies);
        Assert.Equal(content.Length, result.TotalReclaimableBytes);
        Assert.Equal(0, result.CrossDestinationRedundantFileCount);
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_IntraDestinationDuplicatesInSubdirectories_IdentifiesDuplicates()
    {
        string destDir = Path.Combine(_testDir, "dest_subdirs");
        string sub1 = Path.Combine(destDir, "Shoot1");
        string sub2 = Path.Combine(destDir, "Archive", "OlderIngest");
        Directory.CreateDirectory(sub1);
        Directory.CreateDirectory(sub2);

        byte[] content = new byte[150 * 1024];
        Random.Shared.NextBytes(content);

        string file1 = Path.Combine(sub1, "photo.jpg");
        string file2 = Path.Combine(sub2, "photo_duplicate.jpg");
        File.WriteAllBytes(file1, content);
        File.WriteAllBytes(file2, content);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("d1", "Working Drive", destDir)
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["d1"] = new List<MediaFile>
            {
                new("Shoot1/photo.jpg", file1, content.Length, DateTime.UtcNow, MediaCategory.PhotoStandard),
                new("Archive/OlderIngest/photo_duplicate.jpg", file2, content.Length, DateTime.UtcNow, MediaCategory.PhotoStandard)
            }
        };

        List<MediaFile> sourceFiles = new()
        {
            new("Shoot1/photo.jpg", file1, content.Length, DateTime.UtcNow, MediaCategory.PhotoStandard)
        };

        DuplicateAnalysisResult result = await _analyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.Quick);

        Assert.Single(result.Groups);
        DuplicateGroup group = result.Groups[0];
        Assert.True(group.IsIntraDestination);
        Assert.Equal(content.Length, group.ReclaimableBytes);
        Assert.Equal(1, result.TotalDuplicateCopies);
        Assert.Equal(content.Length, result.TotalReclaimableBytes);
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_CrossDestinationDuplicatesOnly_MarksRedundantWithoutSameDiskWaste()
    {
        string ssdDir = Path.Combine(_testDir, "ssd_cross");
        string hddDir = Path.Combine(_testDir, "hdd_cross");
        Directory.CreateDirectory(ssdDir);
        Directory.CreateDirectory(hddDir);

        byte[] content = new byte[80 * 1024];
        Random.Shared.NextBytes(content);

        string fileSsd = Path.Combine(ssdDir, "video.mp4");
        string fileHdd = Path.Combine(hddDir, "video.mp4");
        File.WriteAllBytes(fileSsd, content);
        File.WriteAllBytes(fileHdd, content);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("ssd", "Fast SSD", ssdDir),
            new BackupDestination("hdd", "Cold HDD", hddDir)
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["ssd"] = new List<MediaFile>
            {
                new("video.mp4", fileSsd, content.Length, DateTime.UtcNow, MediaCategory.Video)
            },
            ["hdd"] = new List<MediaFile>
            {
                new("video.mp4", fileHdd, content.Length, DateTime.UtcNow, MediaCategory.Video)
            }
        };

        List<MediaFile> sourceFiles = new()
        {
            new("video.mp4", fileSsd, content.Length, DateTime.UtcNow, MediaCategory.Video)
        };

        DuplicateAnalysisResult result = await _analyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.Quick);

        Assert.Single(result.Groups);
        DuplicateGroup group = result.Groups[0];
        Assert.False(group.IsIntraDestination);
        Assert.True(group.IsCrossDestination);
        Assert.Equal(0L, group.ReclaimableBytes);
        Assert.Equal(2, group.Files.Count);

        Assert.Equal(0, result.TotalDuplicateCopies);
        Assert.Equal(0L, result.TotalReclaimableBytes);
        Assert.Equal(1, result.CrossDestinationRedundantFileCount);
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_MixedIntraAndCrossDuplicates_CalculatesCorrectMetrics()
    {
        string ssdDir = Path.Combine(_testDir, "ssd_mixed");
        string hddDir = Path.Combine(_testDir, "hdd_mixed");
        Directory.CreateDirectory(ssdDir);
        Directory.CreateDirectory(hddDir);

        byte[] content = new byte[90 * 1024];
        Random.Shared.NextBytes(content);

        string ssdCopy1 = Path.Combine(ssdDir, "clip.mov");
        string ssdCopy2 = Path.Combine(ssdDir, "clip_renamed.mov");
        string hddCopy1 = Path.Combine(hddDir, "clip.mov");

        File.WriteAllBytes(ssdCopy1, content);
        File.WriteAllBytes(ssdCopy2, content);
        File.WriteAllBytes(hddCopy1, content);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("ssd", "Primary SSD", ssdDir),
            new BackupDestination("hdd", "Backup HDD", hddDir)
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["ssd"] = new List<MediaFile>
            {
                new("clip.mov", ssdCopy1, content.Length, DateTime.UtcNow, MediaCategory.Video),
                new("clip_renamed.mov", ssdCopy2, content.Length, DateTime.UtcNow, MediaCategory.Video)
            },
            ["hdd"] = new List<MediaFile>
            {
                new("clip.mov", hddCopy1, content.Length, DateTime.UtcNow, MediaCategory.Video)
            }
        };

        List<MediaFile> sourceFiles = new()
        {
            new("clip.mov", ssdCopy1, content.Length, DateTime.UtcNow, MediaCategory.Video)
        };

        DuplicateAnalysisResult result = await _analyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.Quick);

        Assert.Single(result.Groups);
        DuplicateGroup group = result.Groups[0];
        Assert.True(group.IsIntraDestination);
        Assert.True(group.IsCrossDestination);
        Assert.Equal(content.Length, group.ReclaimableBytes);
        Assert.Equal(3, group.Files.Count);

        Assert.Equal(1, result.TotalDuplicateCopies);
        Assert.Equal(content.Length, result.TotalReclaimableBytes);
        Assert.Equal(1, result.CrossDestinationRedundantFileCount);
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_ProgressiveHashing_SameSizeDifferentContent_RejectsAsDuplicates()
    {
        string destDir = Path.Combine(_testDir, "dest_diff_content");
        Directory.CreateDirectory(destDir);

        int size = 120 * 1024;
        byte[] content1 = new byte[size];
        byte[] content2 = new byte[size];
        Random.Shared.NextBytes(content1);
        Random.Shared.NextBytes(content2);

        string file1 = Path.Combine(destDir, "img1.jpg");
        string file2 = Path.Combine(destDir, "img2.jpg");
        File.WriteAllBytes(file1, content1);
        File.WriteAllBytes(file2, content2);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("d1", "Dest 1", destDir)
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["d1"] = new List<MediaFile>
            {
                new("img1.jpg", file1, size, DateTime.UtcNow, MediaCategory.PhotoStandard),
                new("img2.jpg", file2, size, DateTime.UtcNow, MediaCategory.PhotoStandard)
            }
        };

        List<MediaFile> sourceFiles = new()
        {
            new("img1.jpg", file1, size, DateTime.UtcNow, MediaCategory.PhotoStandard)
        };

        DuplicateAnalysisResult result = await _analyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.Quick);

        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalDuplicateCopies);
        Assert.Equal(0L, result.TotalReclaimableBytes);
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_ProgressiveHashing_MatchingHeadTailDifferentMiddle_RejectsAsDuplicates()
    {
        string destDir = Path.Combine(_testDir, "dest_diff_middle");
        Directory.CreateDirectory(destDir);

        int totalSize = 256 * 1024; // 256 KB (head 64KB, middle 128KB, tail 64KB)
        byte[] headBytes = new byte[64 * 1024];
        byte[] tailBytes = new byte[64 * 1024];
        Random.Shared.NextBytes(headBytes);
        Random.Shared.NextBytes(tailBytes);

        byte[] middleBytes1 = new byte[128 * 1024];
        byte[] middleBytes2 = new byte[128 * 1024];
        Random.Shared.NextBytes(middleBytes1);
        Random.Shared.NextBytes(middleBytes2);

        byte[] file1Bytes = new byte[totalSize];
        byte[] file2Bytes = new byte[totalSize];

        Array.Copy(headBytes, 0, file1Bytes, 0, 64 * 1024);
        Array.Copy(middleBytes1, 0, file1Bytes, 64 * 1024, 128 * 1024);
        Array.Copy(tailBytes, 0, file1Bytes, 192 * 1024, 64 * 1024);

        Array.Copy(headBytes, 0, file2Bytes, 0, 64 * 1024);
        Array.Copy(middleBytes2, 0, file2Bytes, 64 * 1024, 128 * 1024);
        Array.Copy(tailBytes, 0, file2Bytes, 192 * 1024, 64 * 1024);

        string file1 = Path.Combine(destDir, "collision_test1.bin");
        string file2 = Path.Combine(destDir, "collision_test2.bin");
        File.WriteAllBytes(file1, file1Bytes);
        File.WriteAllBytes(file2, file2Bytes);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("d1", "Dest 1", destDir)
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["d1"] = new List<MediaFile>
            {
                new("collision_test1.bin", file1, totalSize, DateTime.UtcNow, MediaCategory.Other),
                new("collision_test2.bin", file2, totalSize, DateTime.UtcNow, MediaCategory.Other)
            }
        };

        List<MediaFile> sourceFiles = new()
        {
            new("collision_test1.bin", file1, totalSize, DateTime.UtcNow, MediaCategory.Other)
        };

        DuplicateAnalysisResult result = await _analyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.Full);

        // Even though sizes match and head/tail hashes match, stage 3 full hash in Full mode detects the middle difference!
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalDuplicateCopies);
        Assert.Equal(0L, result.TotalReclaimableBytes);
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_SuperFastMode_UsesMetadataAndSizeOnly()
    {
        string destDir = Path.Combine(_testDir, "dest_superfast");
        Directory.CreateDirectory(destDir);

        byte[] content = new byte[20 * 1024];
        Random.Shared.NextBytes(content);

        string file1 = Path.Combine(destDir, "f1.jpg");
        string file2 = Path.Combine(destDir, "f2.jpg");
        File.WriteAllBytes(file1, content);
        File.WriteAllBytes(file2, content);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("d1", "Dest 1", destDir)
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["d1"] = new List<MediaFile>
            {
                new("f1.jpg", file1, content.Length, DateTime.UtcNow, MediaCategory.PhotoStandard),
                new("f2.jpg", file2, content.Length, DateTime.UtcNow, MediaCategory.PhotoStandard)
            }
        };

        List<MediaFile> sourceFiles = new()
        {
            new("f1.jpg", file1, content.Length, DateTime.UtcNow, MediaCategory.PhotoStandard)
        };

        DuplicateAnalysisResult result = await _analyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.SuperFast);

        Assert.Single(result.Groups);
        Assert.Equal(1, result.TotalDuplicateCopies);
        Assert.Equal(content.Length, result.TotalReclaimableBytes);
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_FilesWithDifferentCategories_NeverConsideredDuplicates()
    {
        string destDir = Path.Combine(_testDir, "dest_diff_category");
        Directory.CreateDirectory(destDir);

        int size = 50 * 1024;
        byte[] content = new byte[size];
        Random.Shared.NextBytes(content);

        string file1 = Path.Combine(destDir, "photo.jpg");
        string file2 = Path.Combine(destDir, "clip.mp4");
        File.WriteAllBytes(file1, content);
        File.WriteAllBytes(file2, content);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("d1", "Dest 1", destDir)
        };

        // Same size, but different Category (PhotoStandard vs Video)
        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["d1"] = new List<MediaFile>
            {
                new("photo.jpg", file1, size, DateTime.UtcNow, MediaCategory.PhotoStandard),
                new("clip.mp4", file2, size, DateTime.UtcNow, MediaCategory.Video)
            }
        };

        List<MediaFile> sourceFiles = new()
        {
            new("photo.jpg", file1, size, DateTime.UtcNow, MediaCategory.PhotoStandard)
        };

        DuplicateAnalysisResult result = await _analyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.Quick);

        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalDuplicateCopies);
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_WithSqliteCache_CachesHashesForSubsequentRuns()
    {
        string dbPath = Path.Combine(_testDir, "test_cache.db");
        await using SqliteIndexCache cache = new(dbPath);

        DuplicateAnalyzer cachedAnalyzer = new(cache);
        string destDir = Path.Combine(_testDir, "dest_cached");
        Directory.CreateDirectory(destDir);

        byte[] content = new byte[50 * 1024];
        Random.Shared.NextBytes(content);

        string file1 = Path.Combine(destDir, "cached1.jpg");
        string file2 = Path.Combine(destDir, "cached2.jpg");
        File.WriteAllBytes(file1, content);
        File.WriteAllBytes(file2, content);

        DateTime writeTime = new FileInfo(file1).LastWriteTimeUtc;

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("d1", "Dest 1", destDir)
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["d1"] = new List<MediaFile>
            {
                new("cached1.jpg", file1, content.Length, writeTime, MediaCategory.PhotoStandard),
                new("cached2.jpg", file2, content.Length, writeTime, MediaCategory.PhotoStandard)
            }
        };

        List<MediaFile> sourceFiles = new()
        {
            new("cached1.jpg", file1, content.Length, writeTime, MediaCategory.PhotoStandard)
        };

        // First run computes and caches
        DuplicateAnalysisResult result1 = await cachedAnalyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.Quick);

        Assert.Single(result1.Groups);

        // Verify cache entry exists
        MediaFile? cachedFile = await cache.GetAsync(file1, content.Length, writeTime);
        Assert.NotNull(cachedFile);
        Assert.True(cachedFile.HeadHash.HasValue);
        Assert.True(cachedFile.TailHash.HasValue);

        // Second run uses cache
        DuplicateAnalysisResult result2 = await cachedAnalyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.Quick);

        Assert.Single(result2.Groups);
        Assert.Equal(result1.TotalReclaimableBytes, result2.TotalReclaimableBytes);
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        string destDir = Path.Combine(_testDir, "dest_cancel");
        Directory.CreateDirectory(destDir);

        byte[] content = new byte[10 * 1024];
        string file1 = Path.Combine(destDir, "f1.jpg");
        string file2 = Path.Combine(destDir, "f2.jpg");
        File.WriteAllBytes(file1, content);
        File.WriteAllBytes(file2, content);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("d1", "Dest 1", destDir)
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["d1"] = new List<MediaFile>
            {
                new("f1.jpg", file1, content.Length, DateTime.UtcNow, MediaCategory.PhotoStandard),
                new("f2.jpg", file2, content.Length, DateTime.UtcNow, MediaCategory.PhotoStandard)
            }
        };

        List<MediaFile> sourceFiles = new()
        {
            new("f1.jpg", file1, content.Length, DateTime.UtcNow, MediaCategory.PhotoStandard)
        };

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _analyzer.AnalyzeDuplicatesAsync(
                sourceFiles,
                destinations,
                destinationFiles,
                VerificationMode.Quick,
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task MultiDestinationVerifier_VerifyAsync_WhenScanDuplicatesFalse_SkipsDuplicateAnalysis()
    {
        string cardDir = Path.Combine(_testDir, "card_nodup");
        string ssdDir = Path.Combine(_testDir, "ssd_nodup");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(ssdDir);

        byte[] img1 = new byte[32 * 1024];
        Random.Shared.NextBytes(img1);

        File.WriteAllBytes(Path.Combine(cardDir, "IMG_0001.CR3"), img1);
        File.WriteAllBytes(Path.Combine(ssdDir, "IMG_0001.CR3"), img1);
        File.WriteAllBytes(Path.Combine(ssdDir, "IMG_0001_duplicate.CR3"), img1);

        MultiDestinationVerifier verifier = new();
        List<BackupDestination> destinations = new()
        {
            new BackupDestination("ssd", "Primary SSD", ssdDir)
        };

        (VerificationSummary summary, _) = await verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Quick,
            FilterPreset.PhotosOnly,
            scanDuplicates: false);

        Assert.Null(summary.DuplicateAnalysis);
    }

    [Fact]
    public async Task MultiDestinationVerifier_VerifyAsync_WhenScanDuplicatesTrue_PopulatesDuplicateAnalysis()
    {
        string cardDir = Path.Combine(_testDir, "card_endtoend");
        string ssdDir = Path.Combine(_testDir, "ssd_endtoend");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(ssdDir);

        byte[] img1 = new byte[64 * 1024];
        Random.Shared.NextBytes(img1);

        File.WriteAllBytes(Path.Combine(cardDir, "IMG_0001.CR3"), img1);
        File.WriteAllBytes(Path.Combine(ssdDir, "IMG_0001.CR3"), img1);
        File.WriteAllBytes(Path.Combine(ssdDir, "IMG_0001_duplicate.CR3"), img1);

        MultiDestinationVerifier verifier = new();
        List<BackupDestination> destinations = new()
        {
            new BackupDestination("ssd", "Primary SSD", ssdDir)
        };

        (VerificationSummary summary, _) = await verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Quick,
            FilterPreset.PhotosOnly,
            scanDuplicates: true);

        Assert.NotNull(summary.DuplicateAnalysis);
        Assert.Single(summary.DuplicateAnalysis.Groups);
        Assert.Equal(1, summary.DuplicateAnalysis.TotalDuplicateCopies);
        Assert.Equal(img1.Length, summary.DuplicateAnalysis.TotalReclaimableBytes);
    }

    [Fact]
    public async Task AnalyzeDuplicatesAsync_DuplicatesInDestinationNotFromSourceDirectory_AreIgnored()
    {
        string cardDir = Path.Combine(_testDir, "card_scope");
        string destDir = Path.Combine(_testDir, "dest_scope");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(destDir);

        byte[] sourceBytes = new byte[64 * 1024];
        byte[] unrelatedBytes = new byte[80 * 1024];
        Random.Shared.NextBytes(sourceBytes);
        Random.Shared.NextBytes(unrelatedBytes);

        // Source has ONLY source_photo.jpg
        string sourceFile = Path.Combine(cardDir, "source_photo.jpg");
        File.WriteAllBytes(sourceFile, sourceBytes);

        // Destination has 2 copies of source_photo.jpg AND 2 copies of unrelated.mp4 (unrelated duplicates)
        string destSource1 = Path.Combine(destDir, "source_photo.jpg");
        string destSource2 = Path.Combine(destDir, "source_photo_backup.jpg");
        string destUnrelated1 = Path.Combine(destDir, "unrelated.mp4");
        string destUnrelated2 = Path.Combine(destDir, "unrelated_dup.mp4");

        File.WriteAllBytes(destSource1, sourceBytes);
        File.WriteAllBytes(destSource2, sourceBytes);
        File.WriteAllBytes(destUnrelated1, unrelatedBytes);
        File.WriteAllBytes(destUnrelated2, unrelatedBytes);

        List<MediaFile> sourceFiles = new()
        {
            new("source_photo.jpg", sourceFile, sourceBytes.Length, DateTime.UtcNow, MediaCategory.PhotoStandard)
        };

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("d1", "Dest 1", destDir)
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["d1"] = new List<MediaFile>
            {
                new("source_photo.jpg", destSource1, sourceBytes.Length, DateTime.UtcNow, MediaCategory.PhotoStandard),
                new("source_photo_backup.jpg", destSource2, sourceBytes.Length, DateTime.UtcNow, MediaCategory.PhotoStandard),
                new("unrelated.mp4", destUnrelated1, unrelatedBytes.Length, DateTime.UtcNow, MediaCategory.Video),
                new("unrelated_dup.mp4", destUnrelated2, unrelatedBytes.Length, DateTime.UtcNow, MediaCategory.Video)
            }
        };

        DuplicateAnalysisResult result = await _analyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            VerificationMode.Quick);

        // Only the source file duplicates should be reported; unrelated destination duplicates are ignored
        Assert.Single(result.Groups);
        DuplicateGroup group = result.Groups[0];
        Assert.Equal(sourceBytes.Length, group.FileSize);
        Assert.Equal(sourceBytes.Length, group.ReclaimableBytes);
        Assert.Equal(2, group.Files.Count);
        Assert.All(group.Files, f => Assert.Contains("source_photo", f.RelativePath));

        Assert.Equal(1, result.TotalDuplicateCopies);
        Assert.Equal(sourceBytes.Length, result.TotalReclaimableBytes);
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
