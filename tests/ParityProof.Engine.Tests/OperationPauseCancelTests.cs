using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Core.Threading;
using ParityProof.Engine.IO;
using ParityProof.Engine.Matching;
using ParityProof.Engine.Transfer;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class OperationPauseCancelTests : IDisposable
{
    private readonly string _testDir;
    private readonly MultiDestinationVerifier _verifier = new();
    private readonly MediaCopier _copier = new();

    public OperationPauseCancelTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_PauseCancelTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task MultiDestinationVerifier_PauseAndResume_CompletesSuccessfully()
    {
        string cardDir = Path.Combine(_testDir, "card");
        string ssdDir = Path.Combine(_testDir, "ssd");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(ssdDir);

        for (int i = 0; i < 5; i++)
        {
            byte[] data = new byte[32 * 1024];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(Path.Combine(cardDir, $"FILE_{i}.JPG"), data);
            File.WriteAllBytes(Path.Combine(ssdDir, $"FILE_{i}.JPG"), data);
        }

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("ssd", "Primary SSD", ssdDir)
        };

        PauseTokenSource pauseSource = new();
        bool pausedCallbackObserved = false;

        Progress<VerificationProgress> progress = new(p =>
        {
            if (p.ProcessedFiles == 1 && !pauseSource.IsPaused)
            {
                pauseSource.Pause();
            }

            if (p.IsPaused)
            {
                pausedCallbackObserved = true;
                pauseSource.Resume();
            }
        });

        (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Quick,
            FilterPreset.PhotosOnly,
            progress,
            CancellationToken.None,
            pauseSource.Token);

        Assert.Equal(5, summary.TotalFiles);
        Assert.Equal(5, summary.FullyVerifiedFiles);
        Assert.Equal(5, results.Count);
        Assert.True(pausedCallbackObserved);
    }

    [Fact]
    public async Task MultiDestinationVerifier_Cancellation_ThrowsOperationCanceledException()
    {
        string cardDir = Path.Combine(_testDir, "card_cancel");
        string ssdDir = Path.Combine(_testDir, "ssd_cancel");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(ssdDir);

        for (int i = 0; i < 5; i++)
        {
            byte[] data = new byte[32 * 1024];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(Path.Combine(cardDir, $"FILE_{i}.JPG"), data);
        }

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("ssd", "Primary SSD", ssdDir)
        };

        using CancellationTokenSource cts = new();
        Progress<VerificationProgress> progress = new(p =>
        {
            if (p.ProcessedFiles >= 1)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _verifier.VerifyAsync(
                cardDir,
                destinations,
                VerificationMode.Quick,
                FilterPreset.PhotosOnly,
                progress,
                cts.Token);
        });
    }

    [Fact]
    public async Task MediaCopier_PauseAndResume_CompletesTransfer()
    {
        string srcDir = Path.Combine(_testDir, "copy_pause_src");
        string dstDir = Path.Combine(_testDir, "copy_pause_dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        List<MediaFile> files = new();
        for (int i = 0; i < 3; i++)
        {
            string fileName = $"IMG_{i}.CR3";
            string path = Path.Combine(srcDir, fileName);
            byte[] data = new byte[64 * 1024];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(path, data);

            files.Add(new MediaFile(
                RelativePath: fileName,
                FullPath: path,
                FileLength: data.Length,
                LastWriteTimeUtc: DateTime.UtcNow,
                Category: MediaCategory.PhotoRaw));
        }

        PauseTokenSource pauseSource = new();
        bool pausedReported = false;

        Progress<CopyProgressInfo> progress = new(p =>
        {
            if (p.FilesCompleted == 1 && !pauseSource.IsPaused)
            {
                pauseSource.Pause();
            }

            if (p.IsPaused)
            {
                pausedReported = true;
                pauseSource.Resume();
            }
        });

        int copied = await _copier.CopyMissingFilesAsync(
            files,
            dstDir,
            progress,
            CancellationToken.None,
            pauseSource.Token);

        Assert.Equal(3, copied);
        Assert.True(pausedReported);

        foreach (MediaFile f in files)
        {
            Assert.True(File.Exists(Path.Combine(dstDir, f.RelativePath)));
        }
    }

    [Fact]
    public async Task MediaCopier_Cancellation_CleansUpPartialFile()
    {
        string srcDir = Path.Combine(_testDir, "copy_cancel_src");
        string dstDir = Path.Combine(_testDir, "copy_cancel_dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        string fileName = "LARGE_VIDEO.MOV";
        string srcPath = Path.Combine(srcDir, fileName);
        // Create 8MB payload
        byte[] data = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(srcPath, data);

        MediaFile mediaFile = new(
            RelativePath: fileName,
            FullPath: srcPath,
            FileLength: data.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.Video);

        using CancellationTokenSource cts = new();
        Progress<CopyProgressInfo> progress = new(p =>
        {
            // Cancel as soon as some bytes have been copied
            if (p.CopiedBytes > 0)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _copier.CopyMissingFilesAsync(
                new[] { mediaFile },
                dstDir,
                progress,
                cts.Token);
        });

        string dstPath = Path.Combine(dstDir, fileName);
        // The partially copied file MUST be cleaned up and deleted!
        Assert.False(File.Exists(dstPath), "Partially transferred file must be deleted upon cancellation.");
    }

    [Fact]
    public async Task FastDirectoryScanner_ScanDirectoryAsync_ReportsLiveProgress()
    {
        string scanDir = Path.Combine(_testDir, "scan_progress_test");
        Directory.CreateDirectory(scanDir);

        for (int i = 0; i < 5; i++)
        {
            File.WriteAllBytes(Path.Combine(scanDir, $"PHOTO_{i}.JPG"), new byte[1024]);
        }

        List<VerificationProgress> reports = new();
        Progress<VerificationProgress> progress = new(p =>
        {
            reports.Add(p);
        });

        IReadOnlyList<MediaFile> files = await FastDirectoryScanner.ScanDirectoryAsync(
            scanDir,
            FilterPreset.PhotosOnly,
            phaseName: "Scanning Test Directory",
            progress: progress);

        Assert.Equal(5, files.Count);
        Assert.NotEmpty(reports);
        Assert.Contains(reports, r => r.Phase.Contains("Scanning Test Directory"));
    }

    [Fact]
    public async Task FastDirectoryScanner_ScanDirectoryAsync_PauseAndResume_CompletesSuccessfully()
    {
        string scanDir = Path.Combine(_testDir, "scan_pause_test");
        Directory.CreateDirectory(scanDir);

        for (int i = 0; i < 6; i++)
        {
            File.WriteAllBytes(Path.Combine(scanDir, $"IMG_{i}.JPG"), new byte[2048]);
        }

        PauseTokenSource pauseSource = new();
        bool pausedObserved = false;

        SynchronousProgress<VerificationProgress> progress = new(p =>
        {
            if (p.ProcessedFiles == 2 && !pauseSource.IsPaused)
            {
                pauseSource.Pause();
            }

            if (p.IsPaused)
            {
                pausedObserved = true;
                pauseSource.Resume();
            }
        });

        IReadOnlyList<MediaFile> files = await FastDirectoryScanner.ScanDirectoryAsync(
            scanDir,
            FilterPreset.PhotosOnly,
            phaseName: "Scanning Directory",
            progress: progress,
            pauseToken: pauseSource.Token);

        Assert.Equal(6, files.Count);
        Assert.True(pausedObserved);
    }

    [Fact]
    public async Task FastDirectoryScanner_ScanDirectoryAsync_Cancellation_ThrowsOperationCanceledException()
    {
        string scanDir = Path.Combine(_testDir, "scan_cancel_test");
        Directory.CreateDirectory(scanDir);

        for (int i = 0; i < 6; i++)
        {
            File.WriteAllBytes(Path.Combine(scanDir, $"IMG_{i}.JPG"), new byte[2048]);
        }

        using CancellationTokenSource cts = new();
        SynchronousProgress<VerificationProgress> progress = new(p =>
        {
            if (p.ProcessedFiles >= 1)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await FastDirectoryScanner.ScanDirectoryAsync(
                scanDir,
                FilterPreset.PhotosOnly,
                phaseName: "Scanning Directory",
                progress: progress,
                cancellationToken: cts.Token);
        });
    }

    [Fact]
    public async Task MultiDestinationVerifier_PauseDuringSourceScan_CompletesSuccessfully()
    {
        string cardDir = Path.Combine(_testDir, "card_scan_pause");
        string ssdDir = Path.Combine(_testDir, "ssd_scan_pause");
        Directory.CreateDirectory(cardDir);
        Directory.CreateDirectory(ssdDir);

        for (int i = 0; i < 4; i++)
        {
            byte[] data = new byte[16 * 1024];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(Path.Combine(cardDir, $"SCAN_{i}.JPG"), data);
            File.WriteAllBytes(Path.Combine(ssdDir, $"SCAN_{i}.JPG"), data);
        }

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("ssd", "Primary SSD", ssdDir)
        };

        PauseTokenSource pauseSource = new();
        bool pausedDuringScanObserved = false;

        SynchronousProgress<VerificationProgress> progress = new(p =>
        {
            if (p.Phase.StartsWith("Scanning Source Directory") && p.ProcessedFiles == 2 && !pauseSource.IsPaused)
            {
                pauseSource.Pause();
            }

            if (p.IsPaused)
            {
                pausedDuringScanObserved = true;
                pauseSource.Resume();
            }
        });

        (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) = await _verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Quick,
            FilterPreset.PhotosOnly,
            progress,
            CancellationToken.None,
            pauseSource.Token);

        Assert.Equal(4, summary.TotalFiles);
        Assert.Equal(4, summary.FullyVerifiedFiles);
        Assert.True(pausedDuringScanObserved);
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
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
