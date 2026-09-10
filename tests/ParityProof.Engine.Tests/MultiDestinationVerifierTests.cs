using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ParityProof.App.ViewModels;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Engine.Matching;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class MultiDestinationVerifierTests : IDisposable
{
    private readonly string _testDir;
    private readonly MultiDestinationVerifier _verifier = new();

    public MultiDestinationVerifierTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_MultiDestTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task VerifyAsync_MultiDestination_CalculatesCorrectSafetyStatus()
    {
        string cardDir = Path.Combine(_testDir, "card");
        string ssdDir = Path.Combine(_testDir, "ssd");
        string nasDir = Path.Combine(_testDir, "nas");

        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(ssdDir);
        Directory.CreateDirectory(nasDir);

        byte[] img1 = new byte[64 * 1024];
        byte[] img2 = new byte[80 * 1024];
        byte[] img3 = new byte[96 * 1024];

        Random.Shared.NextBytes(img1);
        Random.Shared.NextBytes(img2);
        Random.Shared.NextBytes(img3);

        // Write all 3 to card
        File.WriteAllBytes(Path.Combine(cardDir, "IMG_0001.CR3"), img1);
        File.WriteAllBytes(Path.Combine(cardDir, "IMG_0002.CR3"), img2);
        File.WriteAllBytes(Path.Combine(cardDir, "IMG_0003.CR3"), img3);

        // SSD has all 3
        File.WriteAllBytes(Path.Combine(ssdDir, "IMG_0001.CR3"), img1);
        File.WriteAllBytes(Path.Combine(ssdDir, "IMG_0002.CR3"), img2);
        File.WriteAllBytes(Path.Combine(ssdDir, "IMG_0003.CR3"), img3);

        // NAS has only 2 (missing IMG_0003.CR3)
        File.WriteAllBytes(Path.Combine(nasDir, "IMG_0001.CR3"), img1);
        File.WriteAllBytes(Path.Combine(nasDir, "IMG_0002.CR3"), img2);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("ssd", "Primary SSD", ssdDir),
            new BackupDestination("nas", "Archive NAS", nasDir)
        };

        (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Quick,
            FilterPreset.PhotosOnly);

        Assert.Equal(3, summary.TotalFiles);
        Assert.Equal(2, summary.FullyVerifiedFiles);
        Assert.Equal(1, summary.PartiallyVerifiedFiles);
        Assert.Equal(0, summary.MissingFiles);
        Assert.Equal(OverallSafetyStatus.PartiallyBackedUp, summary.SafetyStatus);
        Assert.Equal(3, results.Count);
        Assert.All(results, item => Assert.NotNull(item.SourceFile.HeadHash));
        Assert.All(results, item => Assert.NotNull(item.SourceFile.TailHash));
        Assert.NotNull(results[0].DestinationStatuses["ssd"].DestinationHeadHash);
        Assert.NotNull(results[0].DestinationStatuses["ssd"].DestinationTailHash);

        // Now add the missing file to NAS and verify again
        File.WriteAllBytes(Path.Combine(nasDir, "IMG_0003.CR3"), img3);

        (VerificationSummary summary2, _) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Quick,
            FilterPreset.PhotosOnly);

        Assert.Equal(3, summary2.FullyVerifiedFiles);
        Assert.Equal(0, summary2.PartiallyVerifiedFiles);
        Assert.Equal(0, summary2.MissingFiles);
        Assert.Equal(OverallSafetyStatus.SafeToFormat, summary2.SafetyStatus);
    }

    [Fact]
    public async Task VerifyAsync_ParallelVerification_MaintainsExactFileOrderingAndCounts()
    {
        string cardDir = Path.Combine(_testDir, "card_order");
        string backupDir = Path.Combine(_testDir, "backup_order");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(backupDir);

        const int TOTAL_TEST_FILES = 20;
        List<string> expectedFileNames = new();

        for (int i = 0; i < TOTAL_TEST_FILES; i++)
        {
            string fileName = $"IMG_{i:D4}.CR3";
            expectedFileNames.Add(fileName);

            byte[] data = new byte[32 * 1024];
            Random.Shared.NextBytes(data);

            File.WriteAllBytes(Path.Combine(cardDir, fileName), data);
            File.WriteAllBytes(Path.Combine(backupDir, fileName), data);
        }

        expectedFileNames.Sort(StringComparer.OrdinalIgnoreCase);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("backup", "Backup Drive", backupDir)
        };

        (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Quick,
            FilterPreset.PhotosOnly);

        Assert.Equal(TOTAL_TEST_FILES, summary.TotalFiles);
        Assert.Equal(TOTAL_TEST_FILES, summary.FullyVerifiedFiles);
        Assert.Equal(TOTAL_TEST_FILES, results.Count);

        for (int i = 0; i < TOTAL_TEST_FILES; i++)
        {
            Assert.Equal(expectedFileNames[i], Path.GetFileName(results[i].SourceFile.FullPath));
            Assert.True(results[i].IsFullyVerified);
        }
    }

    [Fact]
    public async Task VerifyAsync_FullMode_CorruptedMiddle_DetectsCorruption()
    {
        string cardDir = Path.Combine(_testDir, "card_middle_corrupt");
        string backupDir = Path.Combine(_testDir, "backup_middle_corrupt");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(backupDir);

        const int FILE_SIZE = 256 * 1024; // 256 KB
        byte[] original = new byte[FILE_SIZE];
        Random.Shared.NextBytes(original);

        byte[] corrupted = new byte[FILE_SIZE];
        Array.Copy(original, corrupted, FILE_SIZE);

        // Mutate byte in the middle (at 128 KB), keeping head 64 KB and tail 64 KB identical
        corrupted[128 * 1024] = (byte)(corrupted[128 * 1024] ^ 0xFF);

        File.WriteAllBytes(Path.Combine(cardDir, "IMG_0001.CR3"), original);
        File.WriteAllBytes(Path.Combine(backupDir, "IMG_0001.CR3"), corrupted);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("backup", "Backup Drive", backupDir)
        };

        (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Full,
            FilterPreset.PhotosOnly);

        Assert.Equal(1, summary.TotalFiles);
        Assert.Equal(0, summary.FullyVerifiedFiles);
        Assert.Equal(1, summary.CorruptFiles);
        Assert.Equal(OverallSafetyStatus.UnsafeToFormat, summary.SafetyStatus);
        Assert.True(results[0].HasAnyCorruption);
    }

    [Fact]
    public async Task VerifyAsync_DeepProbeMode_DetectsInteriorCorruption()
    {
        string cardDir = Path.Combine(_testDir, "card_deep_corrupt");
        string backupDir = Path.Combine(_testDir, "backup_deep_corrupt");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(backupDir);

        const int FILE_SIZE = 8 * 1024 * 1024; // 8 MB (Tier 2)
        byte[] original = new byte[FILE_SIZE];
        Random.Shared.NextBytes(original);

        byte[] corrupted = new byte[FILE_SIZE];
        Array.Copy(original, corrupted, FILE_SIZE);

        // Mutate byte in the interior (at 50% = 4 MB), keeping head 64 KB and tail 64 KB identical
        corrupted[4 * 1024 * 1024] = (byte)(corrupted[4 * 1024 * 1024] ^ 0xFF);

        File.WriteAllBytes(Path.Combine(cardDir, "IMG_0001.CR3"), original);
        File.WriteAllBytes(Path.Combine(backupDir, "IMG_0001.CR3"), corrupted);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("backup", "Backup Drive", backupDir)
        };

        (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Deep,
            FilterPreset.PhotosOnly);

        Assert.Equal(1, summary.TotalFiles);
        Assert.Equal(0, summary.FullyVerifiedFiles);
        Assert.Equal(1, summary.CorruptFiles);
        Assert.Equal(OverallSafetyStatus.UnsafeToFormat, summary.SafetyStatus);
        Assert.True(results[0].HasAnyCorruption);
    }

    [Fact]
    public async Task VerifyAsync_DeepProbeMode_MatchesIdenticalFilesAcrossDestinations()
    {
        string cardDir = Path.Combine(_testDir, "card_deep_ok");
        string ssdDir = Path.Combine(_testDir, "ssd_deep_ok");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(ssdDir);

        byte[] payload1 = new byte[5 * 1024 * 1024]; // 5 MB
        byte[] payload2 = new byte[6 * 1024 * 1024]; // 6 MB
        Random.Shared.NextBytes(payload1);
        Random.Shared.NextBytes(payload2);

        File.WriteAllBytes(Path.Combine(cardDir, "PHOTO1.CR3"), payload1);
        File.WriteAllBytes(Path.Combine(cardDir, "PHOTO2.CR3"), payload2);

        File.WriteAllBytes(Path.Combine(ssdDir, "PHOTO1.CR3"), payload1);
        File.WriteAllBytes(Path.Combine(ssdDir, "PHOTO2.CR3"), payload2);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("ssd", "Fast SSD", ssdDir)
        };

        (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Deep,
            FilterPreset.PhotosOnly);

        Assert.Equal(2, summary.TotalFiles);
        Assert.Equal(2, summary.FullyVerifiedFiles);
        Assert.Equal(0, summary.CorruptFiles);
        Assert.Equal(OverallSafetyStatus.SafeToFormat, summary.SafetyStatus);
        Assert.True(results[0].IsFullyVerified);
        Assert.True(results[1].IsFullyVerified);
    }

    [Fact]
    public async Task VerifyAsync_FullMode_AmbiguousCandidates_ResolvesCorrectMatch()
    {
        string cardDir = Path.Combine(_testDir, "card_ambig");
        string backupDir = Path.Combine(_testDir, "backup_ambig");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(backupDir);

        const int FILE_SIZE = 128 * 1024; // 128 KB
        byte[] payload1 = new byte[FILE_SIZE];
        byte[] payload2 = new byte[FILE_SIZE];
        Random.Shared.NextBytes(payload1);
        Random.Shared.NextBytes(payload2);

        // Card has payload1 as IMG_A.CR3
        File.WriteAllBytes(Path.Combine(cardDir, "IMG_A.CR3"), payload1);

        // Backup has payload2 as OTHER.CR3 and payload1 as RENAMED.CR3 (same size, different names)
        File.WriteAllBytes(Path.Combine(backupDir, "OTHER.CR3"), payload2);
        File.WriteAllBytes(Path.Combine(backupDir, "RENAMED.CR3"), payload1);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("backup", "Backup Drive", backupDir)
        };

        (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Full,
            FilterPreset.PhotosOnly);

        Assert.Equal(1, summary.TotalFiles);
        Assert.Equal(1, summary.FullyVerifiedFiles);
        Assert.Equal(0, summary.CorruptFiles);
        Assert.Equal(OverallSafetyStatus.SafeToFormat, summary.SafetyStatus);
        Assert.Equal(Path.Combine(backupDir, "RENAMED.CR3"), results[0].DestinationStatuses["backup"].MatchedFilePath);
    }

    [Fact]
    public async Task VerifyAsync_FullMode_MultiDriveThroughputReporting()
    {
        string cardDir = Path.Combine(_testDir, "card_progress");
        string ssdDir = Path.Combine(_testDir, "ssd_progress");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(ssdDir);

        byte[] payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(Path.Combine(cardDir, "IMG_0001.CR3"), payload);
        File.WriteAllBytes(Path.Combine(ssdDir, "IMG_0001.CR3"), payload);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("ssd", "Fast SSD", ssdDir)
        };

        List<VerificationProgress> reportedProgress = new();
        Progress<VerificationProgress> progress = new(p =>
        {
            reportedProgress.Add(p);
        });

        (VerificationSummary summary, _) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Full,
            FilterPreset.PhotosOnly,
            progress: progress);

        Assert.Equal(1, summary.FullyVerifiedFiles);
        Assert.NotEmpty(reportedProgress);
    }

    [Fact]
    public async Task VerifyAsync_EmptySourceDirectory_ReturnsNoMediaFound_NeverSafeToFormat()
    {
        string emptyCardDir = Path.Combine(_testDir, "card_empty_qa003");
        string backupDir = Path.Combine(_testDir, "backup_qa003");
        Directory.CreateDirectory(emptyCardDir);
        Directory.CreateDirectory(backupDir);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("backup", "Backup Drive", backupDir)
        };

        (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) = await _verifier.VerifyAsync(
            emptyCardDir,
            destinations,
            VerificationMode.Quick,
            FilterPreset.PhotosOnly);

        Assert.Equal(0, summary.TotalFiles);
        Assert.Equal(OverallSafetyStatus.NoMediaFound, summary.SafetyStatus);
        Assert.NotEqual(OverallSafetyStatus.SafeToFormat, summary.SafetyStatus);
        Assert.Empty(results);
    }

    [Fact]
    public async Task VerifyAsync_SingleFileLockedOrInaccessible_DoesNotAbortRemainingBatch()
    {
        string cardDir = Path.Combine(_testDir, "card_qa004");
        string backupDir = Path.Combine(_testDir, "backup_qa004");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(backupDir);

        byte[] payload1 = new byte[64 * 1024];
        byte[] payload2 = new byte[64 * 1024];
        byte[] payload3 = new byte[64 * 1024];
        Random.Shared.NextBytes(payload1);
        Random.Shared.NextBytes(payload2);
        Random.Shared.NextBytes(payload3);

        string srcFile1 = Path.Combine(cardDir, "FILE1.CR3");
        string srcFile2 = Path.Combine(cardDir, "FILE2.CR3");
        string srcFile3 = Path.Combine(cardDir, "FILE3.CR3");

        File.WriteAllBytes(srcFile1, payload1);
        File.WriteAllBytes(srcFile2, payload2);
        File.WriteAllBytes(srcFile3, payload3);

        File.WriteAllBytes(Path.Combine(backupDir, "FILE1.CR3"), payload1);
        File.WriteAllBytes(Path.Combine(backupDir, "FILE2.CR3"), payload2);
        File.WriteAllBytes(Path.Combine(backupDir, "FILE3.CR3"), payload3);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("backup", "Backup Drive", backupDir)
        };

        // Exclusively lock FILE2 on the source filesystem
        using FileStream lockStream = new(
            srcFile2,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Full,
            FilterPreset.PhotosOnly);

        Assert.Equal(3, summary.TotalFiles);
        // FILE1 and FILE3 succeeded despite FILE2 being locked
        Assert.Equal(2, summary.FullyVerifiedFiles);
        Assert.Equal(1, summary.CorruptFiles);
        Assert.Equal(3, results.Count);

        VerificationResultItem lockedResult = results.First(r => r.SourceFile.RelativePath == "FILE2.CR3");
        Assert.True(lockedResult.HasAnyCorruption);
    }

    [Fact]
    public async Task VerifyAsync_ThrowsOnDuplicateDestinationIds()
    {
        string cardDir = Path.Combine(_testDir, "dup_id_card");
        string dest1 = Path.Combine(_testDir, "dup_id_dst1");
        string dest2 = Path.Combine(_testDir, "dup_id_dst2");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(dest1);
        Directory.CreateDirectory(dest2);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("dest_1", "Drive 1", dest1),
            new BackupDestination("dest_1", "Drive 2", dest2)
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await _verifier.VerifyAsync(
                cardDir,
                destinations,
                VerificationMode.Quick,
                FilterPreset.PhotosOnly);
        });
    }

    [Fact]
    public void MainViewModel_AddDestination_GeneratesUniqueIdsAfterRemoval()
    {
        MainViewModel vm = new();
        string dir1 = Path.Combine(_testDir, "vm_dst1");
        string dir2 = Path.Combine(_testDir, "vm_dst2");
        string dir3 = Path.Combine(_testDir, "vm_dst3");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        Directory.CreateDirectory(dir3);

        vm.AddDestination(dir1);
        vm.AddDestination(dir2);
        Assert.Equal(2, vm.Destinations.Count);
        string firstId = vm.Destinations[0].Id;
        string secondId = vm.Destinations[1].Id;
        Assert.NotEqual(firstId, secondId);

        // Remove the first destination
        vm.Destinations.RemoveAt(0);
        Assert.Single(vm.Destinations);
        Assert.Equal(secondId, vm.Destinations[0].Id);

        // Add a third destination; its ID must not collide with remaining secondId
        vm.AddDestination(dir3);
        Assert.Equal(2, vm.Destinations.Count);
        string thirdId = vm.Destinations[1].Id;
        Assert.NotEqual(secondId, thirdId);
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
