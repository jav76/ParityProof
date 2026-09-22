using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ParityProof.App.ViewModels;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Core.Utils;
using ParityProof.Engine.Matching;
using ParityProof.Engine.Transfer;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class StageProgressReportingTests : IDisposable
{
    private const int TEST_FILE_SIZE_BYTES = 64 * 1024;
    private const int LARGE_TEST_FILE_SIZE_BYTES = 256 * 1024;

    private readonly string _testDir;
    private readonly MultiDestinationVerifier _verifier = new();
    private readonly MediaCopier _copier = new();

    public StageProgressReportingTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_StageProgressTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public void ByteSizeFormatter_FormatsCorrectlyAcrossMagnitudes()
    {
        Assert.Equal("0 B", ByteSizeFormatter.Format(0));
        Assert.Equal("512 B", ByteSizeFormatter.Format(512));
        Assert.Equal("1.0 KB", ByteSizeFormatter.Format(1024));
        Assert.Equal("1.5 MB", ByteSizeFormatter.Format((long)(1.5 * 1024 * 1024)));
        Assert.Equal("2.25 GB", ByteSizeFormatter.Format((long)(2.25 * 1024 * 1024 * 1024)));
        Assert.Equal("1.10 TB", ByteSizeFormatter.Format((long)(1.10 * 1024 * 1024 * 1024 * 1024)));
    }

    [Fact]
    public async Task VerifyAsync_ReportsConcentricStagesAndDestinationMeters()
    {
        string cardDir = Path.Combine(_testDir, "card");
        string ssdDir = Path.Combine(_testDir, "ssd");
        string nasDir = Path.Combine(_testDir, "nas");

        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(ssdDir);
        Directory.CreateDirectory(nasDir);

        byte[] payload1 = new byte[TEST_FILE_SIZE_BYTES];
        byte[] payload2 = new byte[LARGE_TEST_FILE_SIZE_BYTES];
        Random.Shared.NextBytes(payload1);
        Random.Shared.NextBytes(payload2);

        File.WriteAllBytes(Path.Combine(cardDir, "PHOTO1.CR3"), payload1);
        File.WriteAllBytes(Path.Combine(cardDir, "VIDEO1.MOV"), payload2);

        File.WriteAllBytes(Path.Combine(ssdDir, "PHOTO1.CR3"), payload1);
        File.WriteAllBytes(Path.Combine(ssdDir, "VIDEO1.MOV"), payload2);

        File.WriteAllBytes(Path.Combine(nasDir, "PHOTO1.CR3"), payload1);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination(
                Id: "dest-ssd",
                Name: "External NVMe SSD",
                RootPath: ssdDir,
                IsEnabled: true,
                IsRequired: true),
            new BackupDestination(
                Id: "dest-nas",
                Name: "Network Archive NAS",
                RootPath: nasDir,
                IsEnabled: true,
                IsRequired: false)
        };

        List<VerificationProgress> progressReports = new();
        Progress<VerificationProgress> progress = new(p =>
        {
            lock (progressReports)
            {
                progressReports.Add(p);
            }
        });

        (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Quick,
            FilterPreset.PhotosAndVideos,
            progress: progress);

        Assert.Equal(2, summary.TotalFiles);
        Assert.True(progressReports.Count > 0);

        VerificationProgress? finalReport = progressReports.LastOrDefault(r => r.Stages is not null && r.Stages.Count > 0);
        Assert.NotNull(finalReport);
        Assert.NotNull(finalReport.Stages);

        // Discovery, Hashing, and DestinationMatching stages must be present
        Assert.Contains(finalReport.Stages, s => s.Id == PipelineStageId.Discovery);
        Assert.Contains(finalReport.Stages, s => s.Id == PipelineStageId.Hashing);
        Assert.Contains(finalReport.Stages, s => s.Id == PipelineStageId.DestinationMatching);

        StageProgressInfo matchStage = finalReport.Stages.First(s => s.Id == PipelineStageId.DestinationMatching);
        Assert.NotNull(matchStage.DestinationMeters);
        Assert.Equal(2, matchStage.DestinationMeters.Count);
        Assert.Contains(matchStage.DestinationMeters, m => m.DestinationId == "dest-ssd");
        Assert.Contains(matchStage.DestinationMeters, m => m.DestinationId == "dest-nas");
    }

    [Fact]
    public async Task CopyMissingFilesAsync_ReportsTransferAndVerifyStagesWithActiveFile()
    {
        string sourceDir = Path.Combine(_testDir, "copy_src");
        string targetDir1 = Path.Combine(_testDir, "copy_dest1");
        string targetDir2 = Path.Combine(_testDir, "copy_dest2");

        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir1);
        Directory.CreateDirectory(targetDir2);

        byte[] payload = new byte[LARGE_TEST_FILE_SIZE_BYTES];
        Random.Shared.NextBytes(payload);
        string srcFilePath = Path.Combine(sourceDir, "INGEST_001.ARW");
        File.WriteAllBytes(srcFilePath, payload);

        MediaFile missingFile = new(
            FullPath: srcFilePath,
            RelativePath: "INGEST_001.ARW",
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        List<CopyProgressInfo> copyReports = new();
        Progress<CopyProgressInfo> progress = new(p =>
        {
            lock (copyReports)
            {
                copyReports.Add(p);
            }
        });

        int copiedCount = await _copier.CopyMissingFilesAsync(
            new[] { missingFile },
            new[] { targetDir1, targetDir2 },
            progress: progress);

        Assert.Equal(1, copiedCount);
        Assert.True(copyReports.Count > 0);

        CopyProgressInfo? finalReport = copyReports.LastOrDefault(r => r.Stages is not null && r.Stages.Count > 0);
        Assert.NotNull(finalReport);
        Assert.NotNull(finalReport.Stages);

        Assert.Contains(finalReport.Stages, s => s.Id == PipelineStageId.TransferWrite);
        Assert.Contains(finalReport.Stages, s => s.Id == PipelineStageId.PostTransferVerify);

        StageProgressInfo writeStage = finalReport.Stages.First(s => s.Id == PipelineStageId.TransferWrite);
        Assert.NotNull(writeStage.DestinationMeters);
        Assert.Equal(2, writeStage.DestinationMeters.Count);
    }

    [Fact]
    public void StageProgressViewModel_UpdatesStateBadgesAndColorsAccurately()
    {
        StageProgressInfo info = new(
            Id: PipelineStageId.Hashing,
            Title: "Source Hashing",
            Status: StageStatus.Running,
            Percentage: 65.0,
            ProcessedUnits: 65000,
            TotalUnits: 100000,
            ProgressText: "65 KB / 100 KB",
            TelemetryText: "45.0 MB/s",
            ActiveFileName: "TEST.RAW",
            ActiveFilePercentage: 50.0,
            ActiveFileProgressText: "50 KB / 100 KB");

        StageProgressViewModel vm = new(info);

        Assert.True(vm.IsActive);
        Assert.False(vm.IsCompleted);
        Assert.Equal("RUNNING", vm.StatusBadgeText);
        Assert.True(vm.HasActiveFile);
        Assert.Equal("TEST.RAW", vm.ActiveFileName);
        Assert.Equal("#38BDF8", vm.AccentBrushHex);

        StageProgressInfo completedInfo = info with
        {
            Status = StageStatus.Completed,
            Percentage = 100.0,
            ActiveFileName = null
        };

        vm.UpdateFrom(completedInfo);

        Assert.False(vm.IsActive);
        Assert.True(vm.IsCompleted);
        Assert.Equal("DONE ✓", vm.StatusBadgeText);
        Assert.False(vm.HasActiveFile);
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
            // Suppress cleanup error in test tear-down
        }
    }
}
