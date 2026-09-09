using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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

        IReadOnlyList<MediaFile> sourceFiles = await FastDirectoryScanner.ScanDirectoryAsync(
            sourcePath,
            filterPreset,
            phaseName: "Scanning Source Directory",
            referenceTotalFiles: 0,
            referenceTotalBytes: 0,
            progress: progress,
            cancellationToken: cancellationToken,
            pauseToken: pauseToken).ConfigureAwait(false);

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
                SafetyStatus: OverallSafetyStatus.SafeToFormat,
                Duration: stopwatch.Elapsed);

            return (emptySummary, Array.Empty<VerificationResultItem>());
        }

        List<BackupDestination> activeDestinations = destinations.Where(d => d.IsEnabled).ToList();
        Dictionary<string, Dictionary<long, List<MediaFile>>> destinationIndexes = new();
        Dictionary<string, IReadOnlyList<MediaFile>> allDestinationFiles = new();

        foreach (BackupDestination dest in activeDestinations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<MediaFile> destFiles = await FastDirectoryScanner.ScanDirectoryAsync(
                dest.RootPath,
                filterPreset,
                phaseName: $"Indexing Destination: {dest.Name}",
                referenceTotalFiles: 0,
                referenceTotalBytes: 0,
                progress: progress,
                cancellationToken: cancellationToken,
                pauseToken: pauseToken).ConfigureAwait(false);

            allDestinationFiles[dest.Id] = destFiles;

            IReadOnlyDictionary<string, MediaFile>? cachedHashes = null;
            if (_cache is not null && destFiles.Count > 0)
            {
                cachedHashes = await _cache.GetBatchAsync(destFiles, cancellationToken).ConfigureAwait(false);
            }

            Dictionary<long, List<MediaFile>> indexByLength = new();
            foreach (MediaFile df in destFiles)
            {
                MediaFile fileToIndex = df;
                if (cachedHashes is not null && cachedHashes.TryGetValue(df.FullPath, out MediaFile? cached))
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

        int verificationConcurrency = StorageMediaDetector.GetRecommendedVerificationConcurrency(sourcePath);
        VerificationResultItem[] resultArray = new VerificationResultItem[totalFiles];
        long processedFilesCount = 0;
        long processedBytesCount = 0;
        string activeVerificationFile = string.Empty;

        CancellationTokenSource reporterCts = new();
        Task progressReporterTask = Task.Run(async () =>
        {
            while (!reporterCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(50, reporterCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (pauseToken.IsPaused)
                {
                    progress?.Report(new VerificationProgress(
                        CurrentFile: activeVerificationFile,
                        ProcessedFiles: (int)Interlocked.Read(ref processedFilesCount),
                        TotalFiles: totalFiles,
                        ProcessedBytes: Interlocked.Read(ref processedBytesCount),
                        TotalBytes: totalBytes,
                        Phase: "Paused",
                        CurrentFileBytes: 0,
                        CurrentFileProcessedBytes: 0,
                        MegaBytesPerSecond: 0,
                        EstimatedTimeRemaining: TimeSpan.Zero,
                        IsPaused: true));
                    continue;
                }

                double elapsed = stopwatch.Elapsed.TotalSeconds;
                long pFiles = Interlocked.Read(ref processedFilesCount);
                long pBytes = Interlocked.Read(ref processedBytesCount);
                double mbPerSec = elapsed > 0 ? (pBytes / (1024.0 * 1024.0)) / elapsed : 0.0;
                long remainingBytes = Math.Max(0, totalBytes - pBytes);
                double remainingSeconds = mbPerSec > 0 ? (remainingBytes / (1024.0 * 1024.0)) / mbPerSec : 0.0;

                progress?.Report(new VerificationProgress(
                    CurrentFile: activeVerificationFile,
                    ProcessedFiles: (int)pFiles,
                    TotalFiles: totalFiles,
                    ProcessedBytes: pBytes,
                    TotalBytes: totalBytes,
                    Phase: $"Verifying: {activeVerificationFile}",
                    CurrentFileBytes: 0,
                    CurrentFileProcessedBytes: 0,
                    MegaBytesPerSecond: Math.Round(mbPerSec, 1),
                    EstimatedTimeRemaining: TimeSpan.FromSeconds(remainingSeconds),
                    IsPaused: false));
            }
        }, CancellationToken.None);

        try
        {
            await Parallel.ForEachAsync(
                sourceFiles.Select((file, index) => (file, index)),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = verificationConcurrency,
                    CancellationToken = cancellationToken
                },
                async (pair, ct) =>
                {
                    (MediaFile sourceFile, int index) = pair;
                    ct.ThrowIfCancellationRequested();

                    if (pauseToken.IsPaused)
                    {
                        stopwatch.Stop();
                        await pauseToken.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                        stopwatch.Start();
                    }

                    activeVerificationFile = sourceFile.RelativePath;

                    Dictionary<string, FileMatchStatus> statuses = new();
                    foreach (BackupDestination dest in activeDestinations)
                    {
                        Dictionary<long, List<MediaFile>> indexByLen = destinationIndexes[dest.Id];
                        FileMatchStatus matchStatus = await _matcher.MatchFileAsync(
                            sourceFile,
                            dest,
                            indexByLen,
                            mode,
                            ct).ConfigureAwait(false);

                        statuses[dest.Id] = matchStatus;
                    }

                    resultArray[index] = new VerificationResultItem(sourceFile, statuses);

                    long completedFiles = Interlocked.Increment(ref processedFilesCount);
                    long completedBytes = Interlocked.Add(ref processedBytesCount, sourceFile.FileLength);

                    if (completedFiles <= 5)
                    {
                        double elapsed = stopwatch.Elapsed.TotalSeconds;
                        double mbPerSec = elapsed > 0 ? (completedBytes / (1024.0 * 1024.0)) / elapsed : 0.0;
                        long remainingBytes = Math.Max(0, totalBytes - completedBytes);
                        double remainingSeconds = mbPerSec > 0 ? (remainingBytes / (1024.0 * 1024.0)) / mbPerSec : 0.0;

                        progress?.Report(new VerificationProgress(
                            CurrentFile: sourceFile.RelativePath,
                            ProcessedFiles: (int)completedFiles,
                            TotalFiles: totalFiles,
                            ProcessedBytes: completedBytes,
                            TotalBytes: totalBytes,
                            Phase: $"Verifying: {sourceFile.RelativePath}",
                            CurrentFileBytes: sourceFile.FileLength,
                            CurrentFileProcessedBytes: sourceFile.FileLength,
                            MegaBytesPerSecond: Math.Round(mbPerSec, 1),
                            EstimatedTimeRemaining: TimeSpan.FromSeconds(remainingSeconds),
                            IsPaused: false));
                    }

                    if (pauseToken.IsPaused)
                    {
                        stopwatch.Stop();
                        long curFiles = Interlocked.Read(ref processedFilesCount);
                        progress?.Report(new VerificationProgress(
                            CurrentFile: sourceFile.RelativePath,
                            ProcessedFiles: (int)curFiles,
                            TotalFiles: totalFiles,
                            ProcessedBytes: Interlocked.Read(ref processedBytesCount),
                            TotalBytes: totalBytes,
                            Phase: "Paused",
                            CurrentFileBytes: sourceFile.FileLength,
                            CurrentFileProcessedBytes: 0,
                            MegaBytesPerSecond: 0,
                            EstimatedTimeRemaining: TimeSpan.Zero,
                            IsPaused: true));

                        await pauseToken.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                        stopwatch.Start();
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            reporterCts.Cancel();
            await progressReporterTask.ConfigureAwait(false);
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
        if (corruptCount > 0 || missingCount > 0)
        {
            overallStatus = (fullyVerifiedCount > 0 || partiallyVerifiedCount > 0)
                ? OverallSafetyStatus.PartiallyBackedUp
                : OverallSafetyStatus.UnsafeToFormat;
        }
        else if (partiallyVerifiedCount > 0)
        {
            overallStatus = OverallSafetyStatus.PartiallyBackedUp;
        }
        else
        {
            overallStatus = OverallSafetyStatus.SafeToFormat;
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

