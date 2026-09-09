using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
