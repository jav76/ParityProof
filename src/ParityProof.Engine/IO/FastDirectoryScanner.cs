using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Logging;
using ParityProof.Core.Models;
using ParityProof.Core.Threading;
using ParityProof.Platform.Diagnostics;

namespace ParityProof.Engine.IO;

public static class FastDirectoryScanner
{
    private const int PROGRESS_REPORT_INTERVAL_MS = 50;

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".arw", ".raf", ".dng", ".rw2", ".orf", ".iiq", ".3fr"
    };

    private static readonly HashSet<string> StandardImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".png", ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4", ".mxf", ".braw", ".r3d", ".avi", ".mts"
    };

    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xmp", ".thm", ".lrv"
    };

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "$recycle.bin", "system volume information", ".trashes", ".fseventsd",
        ".spotlight-v100", ".git", ".github", "node_modules", ".cache",
        "#recycle", "@eadir", "appdata", ".thumbnails", ".trash", "__pycache__"
    };

    public static MediaCategory CategorizeExtension(string extension)
    {
        if (RawExtensions.Contains(extension))
        {
            return MediaCategory.PhotoRaw;
        }

        if (StandardImageExtensions.Contains(extension))
        {
            return MediaCategory.PhotoStandard;
        }

        if (VideoExtensions.Contains(extension))
        {
            return MediaCategory.Video;
        }

        if (SidecarExtensions.Contains(extension))
        {
            return MediaCategory.Sidecar;
        }

        return MediaCategory.Other;
    }

    [LogMethod]
    public static IReadOnlyList<MediaFile> ScanDirectory(
        string rootPath,
        FilterPreset preset,
        CancellationToken cancellationToken = default)
    {
        return ScanDirectoryAsync(
            rootPath,
            preset,
            cancellationToken: cancellationToken).GetAwaiter().GetResult();
    }

    [LogMethod]
    public static async Task<IReadOnlyList<MediaFile>> ScanDirectoryAsync(
        string rootPath,
        FilterPreset preset,
        string phaseName = "Scanning Directory",
        int referenceTotalFiles = 0,
        long referenceTotalBytes = 0,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default)
    {
        List<MediaFile> emptyList = new();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return emptyList;
        }

        string normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        int workerCount = StorageMediaDetector.GetRecommendedWorkerCount(rootPath);
        Channel<string> dirQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        ConcurrentBag<MediaFile> discoveredFiles = new();
        long totalDiscoveredFiles = 0;
        long totalDiscoveredBytes = 0;
        int pendingDirectoryTasks = 1;
        string currentActiveFolder = string.Empty;

        Stopwatch scanStopwatch = Stopwatch.StartNew();
        CancellationTokenSource reporterCts = new();

        dirQueue.Writer.TryWrite(normalizedRoot);

        Task progressReporterTask = Task.Run(async () =>
        {
            while (!reporterCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PROGRESS_REPORT_INTERVAL_MS, reporterCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (pauseToken.IsPaused)
                {
                    progress?.Report(new VerificationProgress(
                        CurrentFile: currentActiveFolder,
                        ProcessedFiles: (int)Interlocked.Read(ref totalDiscoveredFiles),
                        TotalFiles: referenceTotalFiles,
                        ProcessedBytes: Interlocked.Read(ref totalDiscoveredBytes),
                        TotalBytes: referenceTotalBytes,
                        Phase: $"{phaseName} (Paused)",
                        CurrentFileBytes: 0,
                        CurrentFileProcessedBytes: 0,
                        MegaBytesPerSecond: 0,
                        EstimatedTimeRemaining: TimeSpan.Zero,
                        IsPaused: true,
                        ScanRateFilesPerSecond: 0));
                    continue;
                }

                double elapsedSeconds = scanStopwatch.Elapsed.TotalSeconds;
                long filesCount = Interlocked.Read(ref totalDiscoveredFiles);
                long bytesCount = Interlocked.Read(ref totalDiscoveredBytes);
                double scanRate = elapsedSeconds > 0 ? (filesCount / elapsedSeconds) : 0.0;
                double mbDiscovered = bytesCount / (1024.0 * 1024.0);
                double mbPerSec = elapsedSeconds > 0 ? (mbDiscovered / elapsedSeconds) : 0.0;

                progress?.Report(new VerificationProgress(
                    CurrentFile: currentActiveFolder,
                    ProcessedFiles: (int)filesCount,
                    TotalFiles: referenceTotalFiles,
                    ProcessedBytes: bytesCount,
                    TotalBytes: referenceTotalBytes,
                    Phase: $"{phaseName} ({filesCount:N0} files found)",
                    CurrentFileBytes: 0,
                    CurrentFileProcessedBytes: 0,
                    MegaBytesPerSecond: Math.Round(mbPerSec, 1),
                    EstimatedTimeRemaining: TimeSpan.Zero,
                    IsPaused: false,
                    ScanRateFilesPerSecond: Math.Round(scanRate, 0)));
            }
        }, CancellationToken.None);

        EnumerationOptions enumOptions = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        Task[] workerTasks = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workerTasks[i] = Task.Run(async () =>
            {
                ChannelReader<string> reader = dirQueue.Reader;
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out string? currentDir))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (pauseToken.IsPaused)
                        {
                            scanStopwatch.Stop();
                            await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                            scanStopwatch.Start();
                        }

                        string relativeDir = Path.GetRelativePath(normalizedRoot, currentDir);
                        currentActiveFolder = string.IsNullOrEmpty(relativeDir) || relativeDir == "."
                            ? currentDir
                            : relativeDir;

                        List<string> subDirectoriesToEnqueue = new();
                        try
                        {
                            FileSystemEnumerable<bool> fileSystemEnumerable = new(
                                currentDir,
                                (ref FileSystemEntry entry) =>
                                {
                                    if (entry.IsDirectory)
                                    {
                                        string dirName = entry.FileName.ToString();
                                        if (!IgnoredDirectories.Contains(dirName))
                                        {
                                            subDirectoriesToEnqueue.Add(entry.ToFullPath());
                                        }
                                        return false;
                                    }

                                    ReadOnlySpan<char> fileNameSpan = entry.FileName;
                                    int dotIndex = fileNameSpan.LastIndexOf('.');
                                    if (dotIndex < 0)
                                    {
                                        return false;
                                    }

                                    ReadOnlySpan<char> extSpan = fileNameSpan[dotIndex..];
                                    if (!MatchesExtensionSpan(extSpan, preset.Extensions) &&
                                        (!preset.IncludeSidecars || !MatchesExtensionSpan(extSpan, SidecarExtensions)))
                                    {
                                        return false;
                                    }

                                    string fullPath = entry.ToFullPath();
                                    string relativePath = Path.GetRelativePath(normalizedRoot, fullPath);
                                    long fileLength = entry.Length;
                                    DateTime lastWriteTimeUtc = entry.LastWriteTimeUtc.UtcDateTime;
                                    string extString = extSpan.ToString();
                                    MediaCategory category = CategorizeExtension(extString);

                                    MediaFile mediaFile = new(
                                        RelativePath: relativePath,
                                        FullPath: fullPath,
                                        FileLength: fileLength,
                                        LastWriteTimeUtc: lastWriteTimeUtc,
                                        Category: category);

                                    discoveredFiles.Add(mediaFile);
                                    Interlocked.Increment(ref totalDiscoveredFiles);
                                    Interlocked.Add(ref totalDiscoveredBytes, fileLength);

                                    return true;
                                },
                                enumOptions);

                            foreach (bool isMatch in fileSystemEnumerable)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                if (isMatch)
                                {
                                    long currentTotal = Interlocked.Read(ref totalDiscoveredFiles);
                                    if (currentTotal <= 5)
                                    {
                                        double elapsed = scanStopwatch.Elapsed.TotalSeconds;
                                        double scanRate = elapsed > 0 ? (currentTotal / elapsed) : 0.0;
                                        double mbDiscovered = Interlocked.Read(ref totalDiscoveredBytes) / (1024.0 * 1024.0);
                                        double mbPerSec = elapsed > 0 ? (mbDiscovered / elapsed) : 0.0;

                                        progress?.Report(new VerificationProgress(
                                            CurrentFile: currentActiveFolder,
                                            ProcessedFiles: (int)currentTotal,
                                            TotalFiles: referenceTotalFiles,
                                            ProcessedBytes: Interlocked.Read(ref totalDiscoveredBytes),
                                            TotalBytes: referenceTotalBytes,
                                            Phase: $"{phaseName} ({currentTotal:N0} files found)",
                                            CurrentFileBytes: 0,
                                            CurrentFileProcessedBytes: 0,
                                            MegaBytesPerSecond: Math.Round(mbPerSec, 1),
                                            EstimatedTimeRemaining: TimeSpan.Zero,
                                            IsPaused: false,
                                            ScanRateFilesPerSecond: Math.Round(scanRate, 0)));
                                    }
                                }

                                if (pauseToken.IsPaused)
                                {
                                    scanStopwatch.Stop();
                                    long currentTotal = Interlocked.Read(ref totalDiscoveredFiles);
                                    progress?.Report(new VerificationProgress(
                                        CurrentFile: currentActiveFolder,
                                        ProcessedFiles: (int)currentTotal,
                                        TotalFiles: referenceTotalFiles,
                                        ProcessedBytes: Interlocked.Read(ref totalDiscoveredBytes),
                                        TotalBytes: referenceTotalBytes,
                                        Phase: $"{phaseName} (Paused)",
                                        CurrentFileBytes: 0,
                                        CurrentFileProcessedBytes: 0,
                                        MegaBytesPerSecond: 0,
                                        EstimatedTimeRemaining: TimeSpan.Zero,
                                        IsPaused: true,
                                        ScanRateFilesPerSecond: 0));

                                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                                    scanStopwatch.Start();
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Inaccessible directory skipped safely
                        }

                        if (subDirectoriesToEnqueue.Count > 0)
                        {
                            Interlocked.Add(ref pendingDirectoryTasks, subDirectoriesToEnqueue.Count);
                            foreach (string subDir in subDirectoriesToEnqueue)
                            {
                                dirQueue.Writer.TryWrite(subDir);
                            }
                        }

                        if (Interlocked.Decrement(ref pendingDirectoryTasks) == 0)
                        {
                            dirQueue.Writer.TryComplete();
                        }
                    }
                }
            }, cancellationToken);
        }

        try
        {
            await Task.WhenAll(workerTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            dirQueue.Writer.TryComplete();
            throw;
        }
        finally
        {
            reporterCts.Cancel();
            await progressReporterTask.ConfigureAwait(false);
            scanStopwatch.Stop();
        }

        List<MediaFile> finalResults = discoveredFiles.ToList();
        finalResults.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        double totalSeconds = scanStopwatch.Elapsed.TotalSeconds;
        double finalRate = totalSeconds > 0 ? (finalResults.Count / totalSeconds) : 0.0;
        double finalMb = totalDiscoveredBytes / (1024.0 * 1024.0);
        double finalMbPerSec = totalSeconds > 0 ? (finalMb / totalSeconds) : 0.0;

        progress?.Report(new VerificationProgress(
            CurrentFile: string.Empty,
            ProcessedFiles: finalResults.Count,
            TotalFiles: referenceTotalFiles,
            ProcessedBytes: totalDiscoveredBytes,
            TotalBytes: referenceTotalBytes,
            Phase: $"{phaseName} Complete ({finalResults.Count:N0} files found)",
            CurrentFileBytes: 0,
            CurrentFileProcessedBytes: 0,
            MegaBytesPerSecond: Math.Round(finalMbPerSec, 1),
            EstimatedTimeRemaining: TimeSpan.Zero,
            IsPaused: false,
            ScanRateFilesPerSecond: Math.Round(finalRate, 0)));

        return finalResults;
    }

    private static bool MatchesExtensionSpan(ReadOnlySpan<char> targetSpan, IReadOnlySet<string> allowedExtensions)
    {
        foreach (string allowed in allowedExtensions)
        {
            if (targetSpan.Equals(allowed.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
