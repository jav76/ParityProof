using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Core.Threading;
using ParityProof.Engine.Matching;
using Xunit;

namespace ParityProof.Engine.Tests;

// The UI leaves "PAUSING..." only when a progress report says the operation is paused. These tests pause the
// analyzer at the points where no per-file work is in flight and check that it reports the pause before it
// blocks, then finishes normally once resumed.
public sealed class DuplicateAnalyzerPauseTests : IDisposable
{
    private const int PHOTO_SIZE_BYTES = 256 * 1024;
    private const int DESTINATION_COPY_COUNT = 2;
    private const int BLOCKED_CHECK_DELAY_MS = 200;
    private const string PHOTO_NAME = "IMG_0001.JPG";
    private const string PAUSED_PHASE_SUFFIX = "(Paused)";
    private const string AUDIT_START_PHASE_PREFIX = "Auditing destinations for duplicates";
    private const string QUICK_SCAN_START_PHASE_PREFIX = "Auditing candidate duplicates";
    private const string QUICK_SCAN_ITEM_PHASE_PREFIX = "Quick Scan:";

    private static readonly TimeSpan _pausedReportTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _completionTimeout = TimeSpan.FromSeconds(10);

    private readonly string _testDir;

    public DuplicateAnalyzerPauseTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_DupPauseTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    [Theory]
    [InlineData("entry", VerificationMode.Full, AUDIT_START_PHASE_PREFIX)]
    [InlineData("source-hydration", VerificationMode.Quick, QUICK_SCAN_START_PHASE_PREFIX)]
    [InlineData("source-hydration", VerificationMode.Full, QUICK_SCAN_START_PHASE_PREFIX)]
    [InlineData("source-hashing", VerificationMode.Full, "Verifying Bit-for-Bit Hash")]
    [InlineData("source-hashing", VerificationMode.Deep, "Verifying Deep Probe Hash")]
    public async Task PauseWithNoFileInFlight_ReportsPausedBeforeBlocking_AndResumesToCompletion(
        string pausePoint,
        VerificationMode mode,
        string expectedPausedPhasePrefix)
    {
        (List<MediaFile> sourceFiles, List<BackupDestination> destinations,
            Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles) = CreateDuplicatedBackup();

        PauseTokenSource pauseSource = new();
        TaskCompletionSource<VerificationProgress> pausedReport =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // A file that was already being hashed can also report IsPaused when it completes. Only the report sent
        // at the checkpoint, before the analyzer blocks, carries the "(Paused)" phase.
        SynchronousProgress progress = new(p =>
        {
            if (p.IsPaused)
            {
                if (p.Phase.EndsWith(PAUSED_PHASE_SUFFIX, StringComparison.Ordinal))
                {
                    pausedReport.TrySetResult(p);
                }

                return;
            }

            bool isPausePoint = pausePoint switch
            {
                "source-hydration" => p.Phase.StartsWith(QUICK_SCAN_START_PHASE_PREFIX, StringComparison.Ordinal),
                "source-hashing" => p.Phase.StartsWith(QUICK_SCAN_ITEM_PHASE_PREFIX, StringComparison.Ordinal) &&
                    p.ProcessedFiles == DESTINATION_COPY_COUNT,
                _ => false
            };

            if (isPausePoint)
            {
                pauseSource.Pause();
            }
        });

        if (pausePoint == "entry")
        {
            pauseSource.Pause();
        }

        DuplicateAnalyzer analyzer = new();
        Task<DuplicateAnalysisResult> analysis = Task.Run(() => analyzer.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            mode,
            progress,
            CancellationToken.None,
            pauseSource.Token));

        VerificationProgress paused = await pausedReport.Task.WaitAsync(_pausedReportTimeout);

        Assert.StartsWith(expectedPausedPhasePrefix, paused.Phase, StringComparison.Ordinal);
        Assert.NotNull(paused.Stages);
        await Task.Delay(BLOCKED_CHECK_DELAY_MS);
        Assert.False(analysis.IsCompleted);

        pauseSource.Resume();
        DuplicateAnalysisResult result = await analysis.WaitAsync(_completionTimeout);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal(DESTINATION_COPY_COUNT, group.Files.Count);
        Assert.Equal(DESTINATION_COPY_COUNT - 1, result.TotalDuplicateCopies);
    }

    // One card photo with two identical copies on the same backup, so every stage has a candidate to process.
    private (List<MediaFile> SourceFiles, List<BackupDestination> Destinations,
        Dictionary<string, IReadOnlyList<MediaFile>> DestinationFiles) CreateDuplicatedBackup()
    {
        byte[] content = new byte[PHOTO_SIZE_BYTES];
        Random.Shared.NextBytes(content);

        string cardDir = Path.Combine(_testDir, "card");
        string backupDir = Path.Combine(_testDir, "backup");
        Directory.CreateDirectory(cardDir);

        string sourcePath = Path.Combine(cardDir, PHOTO_NAME);
        File.WriteAllBytes(sourcePath, content);
        List<MediaFile> sourceFiles = new()
        {
            new(PHOTO_NAME, sourcePath, content.Length, DateTime.UtcNow, MediaCategory.PhotoStandard),
        };

        List<MediaFile> backupFiles = new();
        for (int i = 0; i < DESTINATION_COPY_COUNT; i++)
        {
            string relativeDir = $"copy{i}";
            string copyDir = Path.Combine(backupDir, relativeDir);
            Directory.CreateDirectory(copyDir);
            string copyPath = Path.Combine(copyDir, PHOTO_NAME);
            File.WriteAllBytes(copyPath, content);
            backupFiles.Add(new MediaFile(
                Path.Combine(relativeDir, PHOTO_NAME),
                copyPath,
                content.Length,
                DateTime.UtcNow,
                MediaCategory.PhotoStandard));
        }

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("d1", "Backup", backupDir),
        };

        Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new()
        {
            ["d1"] = backupFiles,
        };

        return (sourceFiles, destinations, destinationFiles);
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
        catch (IOException)
        {
            // Best-effort cleanup of the temp directory.
        }
    }

    private sealed class SynchronousProgress : IProgress<VerificationProgress>
    {
        private readonly Action<VerificationProgress> _handler;

        public SynchronousProgress(Action<VerificationProgress> handler)
        {
            _handler = handler;
        }

        public void Report(VerificationProgress value)
        {
            _handler(value);
        }
    }
}
