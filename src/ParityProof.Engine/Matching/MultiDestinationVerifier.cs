using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Logging;
using ParityProof.Core.Models;
using ParityProof.Core.Threading;
using ParityProof.Engine.IO;
using ParityProof.Platform.Diagnostics;

namespace ParityProof.Engine.Matching;

[LogMethod]
public sealed class MultiDestinationVerifier : IVerificationEngine
{
    public const int PROGRESS_REPORT_INTERVAL_MS = 50;
    public const int PERSISTENCE_FLUSH_INTERVAL_MS = 1000;
    public const int PERSISTENCE_BATCH_CAPACITY = 500;
    public const double SLIDING_WINDOW_DURATION_SECONDS = 1.5;
    public const double ETA_SLIDING_WINDOW_DURATION_SECONDS = 10.0;
    public const double ETA_DAMPING_FACTOR = 0.85;
    public const double ETA_CUMULATIVE_WEIGHT = 0.80;
    public const double ETA_WARMUP_PERIOD_SECONDS = 1.5;

    private readonly ContentAddressedMatcher _matcher;
    private readonly IIndexCache? _cache;
    private readonly IDuplicateAnalyzer _duplicateAnalyzer;

    public MultiDestinationVerifier(
        IIndexCache? cache = null,
        IDuplicateAnalyzer? duplicateAnalyzer = null)
    {
        _cache = cache;
        _matcher = new ContentAddressedMatcher(cache);
        _duplicateAnalyzer = duplicateAnalyzer ?? new DuplicateAnalyzer(cache);
    }

    public async Task<(VerificationSummary Summary, IReadOnlyList<VerificationResultItem> Results)> VerifyAsync(
        string sourcePath,
        IReadOnlyList<BackupDestination> destinations,
        VerificationMode mode,
        FilterPreset filterPreset,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default,
        bool scanDuplicates = false)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        DateTime startTimeUtc = DateTime.UtcNow;

        progress?.Report(new VerificationProgress(
            CurrentFile: sourcePath,
            ProcessedFiles: 0,
            TotalFiles: 0,
            ProcessedBytes: 0,
            TotalBytes: 0,
            Phase: "Scanning Source Directory..."));

        // Launch concurrent directory scans across source media and all active backup targets simultaneously
        Task<IReadOnlyList<MediaFile>> sourceScanTask = FastDirectoryScanner.ScanDirectoryAsync(
            sourcePath,
            filterPreset,
            phaseName: "Scanning Source Directory",
            referenceTotalFiles: 0,
            referenceTotalBytes: 0,
            progress: progress,
            cancellationToken: cancellationToken,
            pauseToken: pauseToken);

        List<BackupDestination> activeDestinations = destinations.Where(d => d.IsEnabled).ToList();
        if (activeDestinations.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != activeDestinations.Count)
        {
            throw new ArgumentException("Duplicate destination IDs detected in verification request.", nameof(destinations));
        }
        Task<(BackupDestination Dest, IReadOnlyList<MediaFile> Files)>[] destScanTasks = activeDestinations
            .Select(async dest =>
            {
                IReadOnlyList<MediaFile> files = await FastDirectoryScanner.ScanDirectoryAsync(
                    dest.RootPath,
                    filterPreset,
                    phaseName: $"Scanning: {dest.Name}",
                    referenceTotalFiles: 0,
                    referenceTotalBytes: 0,
                    progress: progress,
                    cancellationToken: cancellationToken,
                    pauseToken: pauseToken).ConfigureAwait(false);
                return (dest, files);
            })
            .ToArray();

        await Task.WhenAll(destScanTasks.Cast<Task>().Append(sourceScanTask)).ConfigureAwait(false);

        IReadOnlyList<MediaFile> sourceFiles = await sourceScanTask.ConfigureAwait(false);
        long totalBytes = sourceFiles.Sum(f => f.FileLength);
        int totalFiles = sourceFiles.Count;

        if (totalFiles == 0)
        {
            stopwatch.Stop();
            VerificationSummary emptySummary = new(
                TimestampUtc: startTimeUtc,
                SourcePath: sourcePath,
                Mode: mode,
                TotalFiles: 0,
                TotalBytes: 0,
                FullyVerifiedFiles: 0,
                PartiallyVerifiedFiles: 0,
                MissingFiles: 0,
                CorruptFiles: 0,
                SafetyStatus: OverallSafetyStatus.NoMediaFound,
                Duration: stopwatch.Elapsed);

            return (emptySummary, Array.Empty<VerificationResultItem>());
        }

        Dictionary<string, Dictionary<long, List<MediaFile>>> destinationIndexes = new();
        Dictionary<string, IReadOnlyList<MediaFile>> allDestinationFiles = new();
        ConcurrentDictionary<string, MediaFile> inMemoryCache = new(StringComparer.OrdinalIgnoreCase);

        // Gather destination scan results
        (BackupDestination Dest, IReadOnlyList<MediaFile> Files)[] destScanResults =
            await Task.WhenAll(destScanTasks).ConfigureAwait(false);

        List<MediaFile> allScannedFiles = new(sourceFiles.Count);
        allScannedFiles.AddRange(sourceFiles);

        foreach ((BackupDestination dest, IReadOnlyList<MediaFile> files) in destScanResults)
        {
            allDestinationFiles[dest.Id] = files;
            allScannedFiles.AddRange(files);
        }

        // Consolidated batch pre-fetch from SQLite cache for all scanned source and destination media files
        if (_cache is not null && allScannedFiles.Count > 0)
        {
            IReadOnlyDictionary<string, MediaFile> cachedEntries =
                await _cache.GetBatchAsync(allScannedFiles, cancellationToken).ConfigureAwait(false);

            foreach (KeyValuePair<string, MediaFile> kvp in cachedEntries)
            {
                inMemoryCache[kvp.Key] = kvp.Value;
            }
        }

        // Build length-partitioned candidate lookup buckets for each active destination
        foreach ((BackupDestination dest, IReadOnlyList<MediaFile> destFiles) in destScanResults)
        {
            Dictionary<long, List<MediaFile>> indexByLength = new();
            foreach (MediaFile df in destFiles)
            {
                MediaFile fileToIndex = df;
                if (inMemoryCache.TryGetValue(df.FullPath, out MediaFile? cached))
                {
                    fileToIndex = cached;
                }

                if (!indexByLength.TryGetValue(fileToIndex.FileLength, out List<MediaFile>? bucket))
                {
                    bucket = new List<MediaFile>();
                    indexByLength[fileToIndex.FileLength] = bucket;
                }

                bucket.Add(fileToIndex);
            }

            destinationIndexes[dest.Id] = indexByLength;
        }

        // Initialize background SQLite write-behind persistence channel
        Channel<MediaFile> persistenceChannel = Channel.CreateUnbounded<MediaFile>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        Task persistenceTask = Task.Run(async () =>
        {
            if (_cache is null)
            {
                return;
            }

            List<MediaFile> batch = new(PERSISTENCE_BATCH_CAPACITY);
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(PERSISTENCE_FLUSH_INTERVAL_MS));
            try
            {
                while (!cancellationToken.IsCancellationRequested || persistenceChannel.Reader.Count > 0)
                {
                    while (persistenceChannel.Reader.TryRead(out MediaFile? item))
                    {
                        batch.Add(item);
                        if (batch.Count >= PERSISTENCE_BATCH_CAPACITY)
                        {
                            break;
                        }
                    }

                    if (batch.Count > 0)
                    {
                        await _cache.UpsertBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                        batch.Clear();
                    }

                    if (persistenceChannel.Reader.Completion.IsCompleted && persistenceChannel.Reader.Count == 0)
                    {
                        break;
                    }

                    try
                    {
                        await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Logger.Warning(ex, "Background SQLite persistence flusher encountered a transient error.");
            }
            finally
            {
                // Final flush of remaining items
                while (persistenceChannel.Reader.TryRead(out MediaFile? item))
                {
                    batch.Add(item);
                }

                if (batch.Count > 0)
                {
                    try
                    {
                        await _cache.UpsertBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Logger.Warning(ex, "Failed to commit remaining cached hashes during shutdown.");
                    }
                }
            }
        });

        // Initialize per-device concurrency throttlers tuned to storage hardware
        int sourceConcurrency = StorageMediaDetector.GetRecommendedDriveWorkers(sourcePath);
        SemaphoreSlim sourceThrottler = new(sourceConcurrency, sourceConcurrency);

        Dictionary<string, SemaphoreSlim> destinationThrottlers = new();
        foreach (BackupDestination dest in activeDestinations)
        {
            int destWorkers = StorageMediaDetector.GetRecommendedDriveWorkers(dest.RootPath);
            destinationThrottlers[dest.Id] = new SemaphoreSlim(destWorkers, destWorkers);
        }

        // Initialize per-device throughput tracking
        ConcurrentDictionary<string, long> driveBytesRead = new();
        driveBytesRead["SRC"] = 0;
        foreach (BackupDestination dest in activeDestinations)
        {
            driveBytesRead[dest.Id] = 0;
        }

        Dictionary<string, string> driveDisplayNames = new();
        driveDisplayNames["SRC"] = "SRC";
        foreach (BackupDestination dest in activeDestinations)
        {
            driveDisplayNames[dest.Id] = string.IsNullOrWhiteSpace(dest.Name) ? dest.Id : dest.Name;
        }

        VerificationResultItem[] resultArray = new VerificationResultItem[totalFiles];
        long processedFilesCount = 0;
        long processedBytesCount = 0;
        string activeVerificationFile = string.Empty;

        // Dedicated hashing stopwatch excludes directory scanning and cache loading time
        Stopwatch hashingStopwatch = Stopwatch.StartNew();
        Queue<(double Timestamp, Dictionary<string, long> DriveBytes, long ProcessedBytes)> sampleQueue = new();
        Queue<(double Timestamp, long SourceBytes)> etaSampleQueue = new();
        double smoothedRemainingSeconds = -1.0;

        CancellationTokenSource reporterCts = new();
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
                    hashingStopwatch.Stop();
                    sampleQueue.Clear();
                    etaSampleQueue.Clear();
                    smoothedRemainingSeconds = -1.0;
                    long pFilesPaused = Interlocked.Read(ref processedFilesCount);
                    long pBytesPaused = Interlocked.Read(ref processedBytesCount);

                    progress?.Report(new VerificationProgress(
                        CurrentFile: activeVerificationFile,
                        ProcessedFiles: (int)pFilesPaused,
                        TotalFiles: totalFiles,
                        ProcessedBytes: pBytesPaused,
                        TotalBytes: totalBytes,
                        Phase: "Paused",
                        CurrentFileBytes: 0,
                        CurrentFileProcessedBytes: 0,
                        MegaBytesPerSecond: 0,
                        EstimatedTimeRemaining: TimeSpan.Zero,
                        IsPaused: true));
                    continue;
                }

                if (!hashingStopwatch.IsRunning)
                {
                    hashingStopwatch.Start();
                }

                double currentTimestamp = hashingStopwatch.Elapsed.TotalSeconds;
                long pFiles = Interlocked.Read(ref processedFilesCount);
                long pBytes = Interlocked.Read(ref processedBytesCount);

                // Snapshot current bytes per drive
                Dictionary<string, long> currentSnapshot = new(driveBytesRead.Count);
                long totalAllDrivesBytes = 0;
                foreach (KeyValuePair<string, long> kvp in driveBytesRead)
                {
                    currentSnapshot[kvp.Key] = kvp.Value;
                    totalAllDrivesBytes += kvp.Value;
                }

                sampleQueue.Enqueue((currentTimestamp, currentSnapshot, pBytes));

                // Retain samples within the 1.5s sliding window duration for the speed gauge
                while (sampleQueue.Count > 1 &&
                       (currentTimestamp - sampleQueue.Peek().Timestamp) > SLIDING_WINDOW_DURATION_SECONDS)
                {
                    sampleQueue.Dequeue();
                }

                (double oldestTimestamp, Dictionary<string, long> oldestSnapshot, _) = sampleQueue.Peek();
                double windowDeltaSeconds = currentTimestamp - oldestTimestamp;

                List<string> throughputParts = new();
                Dictionary<string, double> deviceThroughputs = new();
                double totalMbPerSec;

                if (windowDeltaSeconds >= 0.2)
                {
                    long totalDeltaBytes = 0;
                    foreach (KeyValuePair<string, long> kvp in currentSnapshot)
                    {
                        long oldDriveBytes = oldestSnapshot.TryGetValue(kvp.Key, out long val) ? val : 0;
                        long deltaDriveBytes = Math.Max(0, kvp.Value - oldDriveBytes);
                        totalDeltaBytes += deltaDriveBytes;

                        double driveMbPerSec = (deltaDriveBytes / (1024.0 * 1024.0)) / windowDeltaSeconds;
                        string displayName = driveDisplayNames.TryGetValue(kvp.Key, out string? name) ? name : kvp.Key;
                        throughputParts.Add($"{displayName}: {driveMbPerSec:F1} MB/s");
                        deviceThroughputs[displayName] = Math.Round(driveMbPerSec, 1);
                    }

                    totalMbPerSec = (totalDeltaBytes / (1024.0 * 1024.0)) / windowDeltaSeconds;
                }
                else
                {
                    double warmupSeconds = Math.Max(0.01, currentTimestamp);
                    long totalDeltaBytes = 0;
                    foreach (KeyValuePair<string, long> kvp in currentSnapshot)
                    {
                        totalDeltaBytes += kvp.Value;
                        double driveMbPerSec = (kvp.Value / (1024.0 * 1024.0)) / warmupSeconds;
                        string displayName = driveDisplayNames.TryGetValue(kvp.Key, out string? name) ? name : kvp.Key;
                        throughputParts.Add($"{displayName}: {driveMbPerSec:F1} MB/s");
                        deviceThroughputs[displayName] = Math.Round(driveMbPerSec, 1);
                    }

                    totalMbPerSec = (totalDeltaBytes / (1024.0 * 1024.0)) / warmupSeconds;
                }

                string multiDriveThroughputText = throughputParts.Count > 1
                    ? $"{string.Join(" | ", throughputParts)} (Total: {totalMbPerSec:F1} MB/s)"
                    : $"{totalMbPerSec:F1} MB/s";

                // Stabilized ETA calculation using continuous chunk streaming + dual-rate blending
                long currentSourceBytes = driveBytesRead.TryGetValue("SRC", out long sb) ? sb : pBytes;
                etaSampleQueue.Enqueue((currentTimestamp, currentSourceBytes));

                while (etaSampleQueue.Count > 1 &&
                       (currentTimestamp - etaSampleQueue.Peek().Timestamp) > ETA_SLIDING_WINDOW_DURATION_SECONDS)
                {
                    etaSampleQueue.Dequeue();
                }

                (double etaOldestTime, long etaOldestSrcBytes) = etaSampleQueue.Peek();
                double etaDeltaSeconds = currentTimestamp - etaOldestTime;
                long etaDeltaBytes = Math.Max(0, currentSourceBytes - etaOldestSrcBytes);

                double mediumTermRate = etaDeltaSeconds >= 0.5
                    ? (etaDeltaBytes / (1024.0 * 1024.0)) / etaDeltaSeconds
                    : 0.0;

                double cumulativeRate = currentTimestamp > 0.1
                    ? (currentSourceBytes / (1024.0 * 1024.0)) / currentTimestamp
                    : mediumTermRate;

                double blendedRate;
                if (mediumTermRate > 0.05 && cumulativeRate > 0.05)
                {
                    blendedRate = (ETA_CUMULATIVE_WEIGHT * cumulativeRate) +
                                  ((1.0 - ETA_CUMULATIVE_WEIGHT) * mediumTermRate);
                }
                else
                {
                    blendedRate = Math.Max(cumulativeRate, mediumTermRate);
                }

                long remainingBytes = Math.Max(0, totalBytes - currentSourceBytes);
                double rawRemainingSeconds = blendedRate > 0.05
                    ? (remainingBytes / (1024.0 * 1024.0)) / blendedRate
                    : 0.0;

                if (currentTimestamp < ETA_WARMUP_PERIOD_SECONDS || rawRemainingSeconds <= 0.0)
                {
                    smoothedRemainingSeconds = rawRemainingSeconds;
                }
                else if (smoothedRemainingSeconds < 0.0)
                {
                    smoothedRemainingSeconds = rawRemainingSeconds;
                }
                else
                {
                    smoothedRemainingSeconds = (ETA_DAMPING_FACTOR * smoothedRemainingSeconds) +
                                               ((1.0 - ETA_DAMPING_FACTOR) * rawRemainingSeconds);
                }

                TimeSpan etaTimeSpan = (currentTimestamp >= ETA_WARMUP_PERIOD_SECONDS && smoothedRemainingSeconds > 0.0)
                    ? TimeSpan.FromSeconds(smoothedRemainingSeconds)
                    : TimeSpan.Zero;

                progress?.Report(new VerificationProgress(
                    CurrentFile: activeVerificationFile,
                    ProcessedFiles: (int)pFiles,
                    TotalFiles: totalFiles,
                    ProcessedBytes: pBytes,
                    TotalBytes: totalBytes,
                    Phase: $"Verifying: {activeVerificationFile}",
                    CurrentFileBytes: 0,
                    CurrentFileProcessedBytes: 0,
                    MegaBytesPerSecond: Math.Round(totalMbPerSec, 1),
                    EstimatedTimeRemaining: etaTimeSpan,
                    IsPaused: false,
                    MultiDriveThroughputText: multiDriveThroughputText,
                    DeviceThroughputs: deviceThroughputs));
            }
        }, CancellationToken.None);

        try
        {
            // Decoupled pipeline degree of parallelism permits simultaneous cross-drive execution
            int maxParallelism = Math.Max(sourceConcurrency, activeDestinations.Count * 4);

            await Parallel.ForEachAsync(
                sourceFiles.Select((file, index) => (file, index)),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxParallelism,
                    CancellationToken = cancellationToken
                },
                async (pair, ct) =>
                {
                    (MediaFile sourceFile, int index) = pair;
                    ct.ThrowIfCancellationRequested();

                    if (pauseToken.IsPaused)
                    {
                        stopwatch.Stop();
                        hashingStopwatch.Stop();
                        await pauseToken.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                        stopwatch.Start();
                        hashingStopwatch.Start();
                    }

                    try
                    {
                        activeVerificationFile = sourceFile.RelativePath;

                        // Check if any active destination contains candidates matching source file length
                        bool hasAnyCandidate = activeDestinations.Any(d =>
                            destinationIndexes[d.Id].ContainsKey(sourceFile.FileLength));

                        Dictionary<string, FileMatchStatus> statuses = new();
                        MediaFile finalSourceFile = sourceFile;

                        if (!hasAnyCandidate)
                        {
                            // Fast-path: Missing on all destinations without performing any disk I/O on source card
                            foreach (BackupDestination dest in activeDestinations)
                            {
                                statuses[dest.Id] = new FileMatchStatus(
                                    DestinationId: dest.Id,
                                    DestinationRootPath: dest.RootPath,
                                    Status: MediaStatus.Missing,
                                    FailureReason: "No file found with matching byte size.");
                            }
                        }
                        else
                        {
                            // Ensure source file is hashed once under source media throttler
                            MediaFile hydratedSource = sourceFile;
                            if (mode != VerificationMode.SuperFast)
                            {
                                await sourceThrottler.WaitAsync(ct).ConfigureAwait(false);
                                try
                                {
                                    hydratedSource = await _matcher.EnsureHashesAsync(
                                        sourceFile,
                                        mode,
                                        inMemoryCache,
                                        persistenceChannel.Writer,
                                        onBytesRead: bytes => driveBytesRead.AddOrUpdate("SRC", bytes, (_, cur) => cur + bytes),
                                        cancellationToken: ct).ConfigureAwait(false);
                                }
                                finally
                                {
                                    sourceThrottler.Release();
                                }
                            }

                            finalSourceFile = hydratedSource;

                            // Dispatch parallel candidate matches across all active destinations concurrently
                            Task<FileMatchStatus>[] matchTasks = activeDestinations.Select(async dest =>
                            {
                                Dictionary<long, List<MediaFile>> indexByLen = destinationIndexes[dest.Id];
                                SemaphoreSlim throttler = destinationThrottlers[dest.Id];

                                await throttler.WaitAsync(ct).ConfigureAwait(false);
                                try
                                {
                                    return await _matcher.MatchFileAsync(
                                        hydratedSource,
                                        dest,
                                        indexByLen,
                                        mode,
                                        inMemoryCache,
                                        persistenceChannel.Writer,
                                        onSourceBytesRead: bytes => driveBytesRead.AddOrUpdate("SRC", bytes, (_, cur) => cur + bytes),
                                        onDestBytesRead: bytes => driveBytesRead.AddOrUpdate(dest.Id, bytes, (_, cur) => cur + bytes),
                                        cancellationToken: ct).ConfigureAwait(false);
                                }
                                finally
                                {
                                    throttler.Release();
                                }
                            }).ToArray();

                            FileMatchStatus[] resultsArray = await Task.WhenAll(matchTasks).ConfigureAwait(false);
                            for (int d = 0; d < activeDestinations.Count; d++)
                            {
                                statuses[activeDestinations[d].Id] = resultsArray[d];
                            }
                        }

                        if (inMemoryCache.TryGetValue(finalSourceFile.FullPath, out MediaFile? cachedSource))
                        {
                            finalSourceFile = finalSourceFile with
                            {
                                HeadHash = finalSourceFile.HeadHash ?? cachedSource.HeadHash,
                                TailHash = finalSourceFile.TailHash ?? cachedSource.TailHash,
                                DeepHash = finalSourceFile.DeepHash ?? cachedSource.DeepHash,
                                FullHash = finalSourceFile.FullHash ?? cachedSource.FullHash
                            };
                        }

                        resultArray[index] = new VerificationResultItem(finalSourceFile, statuses);
                        Interlocked.Increment(ref processedFilesCount);
                        Interlocked.Add(ref processedBytesCount, sourceFile.FileLength);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        AppLogger.Logger.Warning(
                            ex,
                            "Failed to verify media file {FilePath} due to an I/O error",
                            sourceFile.FullPath);

                        Dictionary<string, FileMatchStatus> errorStatuses = new();
                        foreach (BackupDestination dest in activeDestinations)
                        {
                            errorStatuses[dest.Id] = new FileMatchStatus(
                                DestinationId: dest.Id,
                                DestinationRootPath: dest.RootPath,
                                Status: MediaStatus.Corrupt,
                                FailureReason: $"I/O Error: {ex.Message}");
                        }

                        resultArray[index] = new VerificationResultItem(sourceFile, errorStatuses);
                        Interlocked.Increment(ref processedFilesCount);
                        Interlocked.Add(ref processedBytesCount, sourceFile.FileLength);
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            reporterCts.Cancel();
            try
            {
                await progressReporterTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when canceling reporter
            }

            hashingStopwatch.Stop();

            // Drain and complete SQLite write-behind flusher
            persistenceChannel.Writer.TryComplete();
            try
            {
                await persistenceTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Logger.Warning(ex, "Background SQLite persistence flusher completed with an exception.");
            }

            sourceThrottler.Dispose();
            foreach (SemaphoreSlim throttler in destinationThrottlers.Values)
            {
                throttler.Dispose();
            }

            stopwatch.Stop();
        }

        int fullyVerifiedCount = 0;
        int partiallyVerifiedCount = 0;
        int missingCount = 0;
        int corruptCount = 0;

        foreach (VerificationResultItem item in resultArray)
        {
            if (item.HasAnyCorruption)
            {
                corruptCount++;
            }
            else if (item.IsFullyVerified)
            {
                fullyVerifiedCount++;
            }
            else if (item.IsPartiallyVerified)
            {
                partiallyVerifiedCount++;
            }
            else
            {
                missingCount++;
            }
        }

        List<VerificationResultItem> results = resultArray.ToList();

        OverallSafetyStatus overallStatus;
        if (totalFiles == 0)
        {
            overallStatus = OverallSafetyStatus.NoMediaFound;
        }
        else if (corruptCount > 0 || missingCount > 0)
        {
            overallStatus = (fullyVerifiedCount > 0 || partiallyVerifiedCount > 0)
                ? OverallSafetyStatus.PartiallyBackedUp
                : OverallSafetyStatus.UnsafeToFormat;
        }
        else if (partiallyVerifiedCount > 0)
        {
            overallStatus = OverallSafetyStatus.PartiallyBackedUp;
        }
        else if (fullyVerifiedCount == totalFiles && totalFiles > 0)
        {
            overallStatus = OverallSafetyStatus.SafeToFormat;
        }
        else
        {
            overallStatus = OverallSafetyStatus.UnsafeToFormat;
        }

        DuplicateAnalysisResult? duplicateAnalysis = null;
        if (scanDuplicates && activeDestinations.Count > 0)
        {
            duplicateAnalysis = await _duplicateAnalyzer.AnalyzeDuplicatesAsync(
                sourceFiles,
                activeDestinations,
                allDestinationFiles,
                mode,
                progress,
                cancellationToken,
                pauseToken).ConfigureAwait(false);
        }

        VerificationSummary summary = new(
            TimestampUtc: startTimeUtc,
            SourcePath: sourcePath,
            Mode: mode,
            TotalFiles: totalFiles,
            TotalBytes: totalBytes,
            FullyVerifiedFiles: fullyVerifiedCount,
            PartiallyVerifiedFiles: partiallyVerifiedCount,
            MissingFiles: missingCount,
            CorruptFiles: corruptCount,
            SafetyStatus: overallStatus,
            Duration: stopwatch.Elapsed,
            DuplicateAnalysis: duplicateAnalysis);

        progress?.Report(new VerificationProgress(
            CurrentFile: string.Empty,
            ProcessedFiles: totalFiles,
            TotalFiles: totalFiles,
            ProcessedBytes: totalBytes,
            TotalBytes: totalBytes,
            Phase: "Verification Complete",
            CurrentFileBytes: 0,
            CurrentFileProcessedBytes: 0,
            MegaBytesPerSecond: 0,
            EstimatedTimeRemaining: TimeSpan.Zero,
            IsPaused: false));

        return (summary, results);
    }
}
