using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Logging;
using ParityProof.Core.Models;
using ParityProof.Core.Threading;
using ParityProof.Core.Utils;
using ParityProof.Engine.Hashing;
using ParityProof.Engine.IO;
using ParityProof.Platform.Diagnostics;

namespace ParityProof.Engine.Matching;

// Where the audit stands at a pause checkpoint, so the paused report keeps the progress bar in place. It is not
// nested in DuplicateAnalyzer because the class-level [LogMethod] weaving would wrap its constructor, and a woven
// struct constructor fails at runtime with InvalidProgramException.
internal readonly record struct DuplicateAuditCheckpoint(
    string Phase,
    int SourceFileCount,
    long SourceBytes,
    int TotalCandidates,
    long ProcessedCandidates,
    long TotalCandidateBytes,
    long ProcessedCandidateBytes);

[LogMethod]
public sealed class DuplicateAnalyzer : IDuplicateAnalyzer
{
    private const int FULL_HASH_BUFFER_SIZE = 1024 * 1024; // 1 MB buffer
    private const string AUDIT_START_PHASE = "Auditing destinations for duplicates from source directory...";
    private const string QUICK_SCAN_PHASE = "Auditing candidate duplicates: Quick head/tail scan...";
    private readonly IIndexCache? _cache;

    private sealed class DestinationFileEntry
    {
        public MediaFile File { get; }
        public string DestinationId { get; }
        public string DestinationName { get; }

        public DestinationFileEntry(MediaFile file, string destinationId, string destinationName)
        {
            File = file;
            DestinationId = destinationId;
            DestinationName = destinationName;
        }
    }

    public DuplicateAnalyzer(IIndexCache? cache = null)
    {
        _cache = cache;
    }

    public async Task<DuplicateAnalysisResult> AnalyzeDuplicatesAsync(
        IReadOnlyList<MediaFile> sourceFiles,
        IReadOnlyList<BackupDestination> destinations,
        IReadOnlyDictionary<string, IReadOnlyList<MediaFile>> destinationFiles,
        VerificationMode mode,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default)
    {
        if (sourceFiles.Count == 0 || destinations.Count == 0 || destinationFiles.Count == 0)
        {
            return DuplicateAnalysisResult.Empty;
        }

        int srcFilesCount = sourceFiles.Count;
        long srcFilesBytes = sourceFiles.Sum(f => f.FileLength);

        progress?.Report(new VerificationProgress(
            CurrentFile: string.Empty,
            ProcessedFiles: 0,
            TotalFiles: 0,
            ProcessedBytes: 0,
            TotalBytes: 0,
            Phase: AUDIT_START_PHASE,
            Stages: CreateDuplicateStages(srcFilesCount, srcFilesBytes, 0, 0, 0.0, null, isCompleted: false)));

        DuplicateAuditCheckpoint startCheckpoint = new(
            Phase: AUDIT_START_PHASE,
            SourceFileCount: srcFilesCount,
            SourceBytes: srcFilesBytes,
            TotalCandidates: 0,
            ProcessedCandidates: 0,
            TotalCandidateBytes: 0,
            ProcessedCandidateBytes: 0);

        List<DestinationFileEntry> allEntries = new();
        foreach (BackupDestination dest in destinations.Where(d => d.IsEnabled))
        {
            if (destinationFiles.TryGetValue(dest.Id, out IReadOnlyList<MediaFile>? files))
            {
                foreach (MediaFile file in files)
                {
                    allEntries.Add(new DestinationFileEntry(file, dest.Id, dest.Name));
                }
            }
        }

        if (allEntries.Count <= 1)
        {
            return DuplicateAnalysisResult.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await CheckPauseAsync(pauseToken, progress, startCheckpoint, cancellationToken).ConfigureAwait(false);

        // Stage 1: Source-Guided Fast Metadata & Size Scan.
        // Index source files by (FileLength, Category).
        // Destination files not matching any source file are discarded with zero disk I/O.
        Dictionary<(long Length, MediaCategory Category), List<MediaFile>> sourceMetaSizeBuckets = new();
        foreach (MediaFile sf in sourceFiles)
        {
            (long Length, MediaCategory Category) key = (sf.FileLength, sf.Category);
            if (!sourceMetaSizeBuckets.TryGetValue(key, out List<MediaFile>? list))
            {
                list = new List<MediaFile>();
                sourceMetaSizeBuckets[key] = list;
            }

            list.Add(sf);
        }

        Dictionary<(long Length, MediaCategory Category), List<DestinationFileEntry>> destMetaSizeBuckets = new();
        foreach (DestinationFileEntry entry in allEntries)
        {
            (long Length, MediaCategory Category) key = (entry.File.FileLength, entry.File.Category);
            if (!sourceMetaSizeBuckets.ContainsKey(key))
            {
                continue;
            }

            if (!destMetaSizeBuckets.TryGetValue(key, out List<DestinationFileEntry>? list))
            {
                list = new List<DestinationFileEntry>();
                destMetaSizeBuckets[key] = list;
            }

            list.Add(entry);
        }

        // Only buckets with at least 2 destination copies can represent duplicated source files in destination
        List<List<DestinationFileEntry>> candidateBuckets = destMetaSizeBuckets.Values
            .Where(bucket => bucket.Count > 1)
            .ToList();

        if (candidateBuckets.Count == 0)
        {
            return DuplicateAnalysisResult.Empty;
        }

        // In Super-Fast mode, duplicate analysis completes using metadata and file size alone (zero hashing)
        if (mode == VerificationMode.SuperFast)
        {
            List<(long FileSize, string GroupKey, List<DestinationFileEntry> Entries)> superFastGroups = new();
            foreach (List<DestinationFileEntry> bucket in candidateBuckets)
            {
                long fileSize = bucket[0].File.FileLength;
                string groupKey = $"meta_{fileSize}_{bucket[0].File.Category}";
                superFastGroups.Add((fileSize, groupKey, bucket));
            }

            return BuildDuplicateAnalysisResult(superFastGroups);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await CheckPauseAsync(pauseToken, progress, startCheckpoint, cancellationToken).ConfigureAwait(false);

        // Stage 2: Quick Scan (Head & Tail 64KB Chunk Hashing) for candidate size/metadata buckets in parallel
        List<DestinationFileEntry> flatCandidates = candidateBuckets.SelectMany(b => b).ToList();
        string samplePath = destinations.FirstOrDefault()?.RootPath ?? string.Empty;
        int concurrency = StorageMediaDetector.GetRecommendedVerificationConcurrency(samplePath);

        // Identify source files that match the candidate buckets
        HashSet<(long Length, MediaCategory Category)> candidateKeys = destMetaSizeBuckets.Keys
            .Where(k => destMetaSizeBuckets[k].Count > 1)
            .ToHashSet();

        List<MediaFile> relevantSources = sourceFiles
            .Where(sf => candidateKeys.Contains((sf.FileLength, sf.Category)))
            .ToList();

        int totalCandidates = flatCandidates.Count + relevantSources.Count;
        long totalCandidateBytes = flatCandidates.Sum(e => e.File.FileLength);
        long processedCandidatesCount = 0;
        long processedCandidatesBytes = 0;
        Stopwatch stage2Stopwatch = Stopwatch.StartNew();

        progress?.Report(new VerificationProgress(
            CurrentFile: string.Empty,
            ProcessedFiles: 0,
            TotalFiles: totalCandidates,
            ProcessedBytes: 0,
            TotalBytes: totalCandidateBytes,
            Phase: QUICK_SCAN_PHASE,
            CurrentFileBytes: 0,
            CurrentFileProcessedBytes: 0,
            MegaBytesPerSecond: 0,
            EstimatedTimeRemaining: TimeSpan.Zero,
            Stages: CreateDuplicateStages(srcFilesCount, srcFilesBytes, totalCandidates, 0, 0.0, null, isCompleted: false),
            IsPaused: false));

        DuplicateAuditCheckpoint quickScanCheckpoint = new(
            Phase: QUICK_SCAN_PHASE,
            SourceFileCount: srcFilesCount,
            SourceBytes: srcFilesBytes,
            TotalCandidates: totalCandidates,
            ProcessedCandidates: 0,
            TotalCandidateBytes: totalCandidateBytes,
            ProcessedCandidateBytes: 0);

        // Hydrate from SQLite cache if available
        List<MediaFile> filesToHydrate = flatCandidates.Select(e => e.File).Concat(relevantSources).ToList();
        IReadOnlyDictionary<string, MediaFile>? cachedMap = null;
        if (_cache is not null && filesToHydrate.Count > 0)
        {
            cachedMap = await _cache.GetBatchAsync(filesToHydrate, cancellationToken).ConfigureAwait(false);
        }

        ConcurrentBag<MediaFile> cacheUpsertBag = new();

        // Hydrate relevant source files
        List<MediaFile> hydratedSources = new();
        foreach (MediaFile sf in relevantSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CheckPauseAsync(pauseToken, progress, quickScanCheckpoint, cancellationToken).ConfigureAwait(false);

            MediaFile currentSource = sf;
            if (cachedMap is not null && cachedMap.TryGetValue(currentSource.FullPath, out MediaFile? cached))
            {
                currentSource = cached;
            }

            if (!currentSource.HeadHash.HasValue || !currentSource.TailHash.HasValue)
            {
                (ulong head, ulong tail) = ChunkReader.ComputeHeadTailHash(currentSource.FullPath);
                currentSource = currentSource with { HeadHash = head, TailHash = tail };
                cacheUpsertBag.Add(currentSource);
            }

            hydratedSources.Add(currentSource);
        }

        HashSet<(long Length, MediaCategory Category, ulong Head, ulong Tail)> validSourceHeadTailKeys = new();
        foreach (MediaFile sf in hydratedSources)
        {
            validSourceHeadTailKeys.Add((
                sf.FileLength,
                sf.Category,
                sf.HeadHash ?? 0UL,
                sf.TailHash ?? 0UL));
        }

        DestinationFileEntry[] hydratedArray = new DestinationFileEntry[flatCandidates.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, flatCandidates.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            },
            async (index, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                await CheckPauseAsync(
                    pauseToken,
                    progress,
                    quickScanCheckpoint with
                    {
                        ProcessedCandidates = Interlocked.Read(ref processedCandidatesCount),
                        ProcessedCandidateBytes = Interlocked.Read(ref processedCandidatesBytes),
                    },
                    ct).ConfigureAwait(false);

                DestinationFileEntry entry = flatCandidates[index];
                MediaFile currentFile = entry.File;
                if (cachedMap is not null && cachedMap.TryGetValue(currentFile.FullPath, out MediaFile? cached))
                {
                    currentFile = cached;
                }

                if (!currentFile.HeadHash.HasValue || !currentFile.TailHash.HasValue)
                {
                    (ulong head, ulong tail) = ChunkReader.ComputeHeadTailHash(currentFile.FullPath);
                    currentFile = currentFile with { HeadHash = head, TailHash = tail };
                    cacheUpsertBag.Add(currentFile);
                }

                hydratedArray[index] = new DestinationFileEntry(
                    currentFile,
                    entry.DestinationId,
                    entry.DestinationName);

                long completed = Interlocked.Increment(ref processedCandidatesCount);
                long completedBytes = Interlocked.Add(ref processedCandidatesBytes, currentFile.FileLength);

                double elapsed = stage2Stopwatch.Elapsed.TotalSeconds;
                double mbPerSec = elapsed > 0 ? (completedBytes / (1024.0 * 1024.0)) / elapsed : 0.0;
                long remBytes = Math.Max(0, totalCandidateBytes - completedBytes);
                double remSec = mbPerSec > 0 ? (remBytes / (1024.0 * 1024.0)) / mbPerSec : 0.0;

                progress?.Report(new VerificationProgress(
                    CurrentFile: currentFile.RelativePath,
                    ProcessedFiles: (int)completed,
                    TotalFiles: totalCandidates,
                    ProcessedBytes: completedBytes,
                    TotalBytes: totalCandidateBytes,
                    Phase: $"Quick Scan: {Path.GetFileName(currentFile.FullPath)}",
                    CurrentFileBytes: currentFile.FileLength,
                    CurrentFileProcessedBytes: currentFile.FileLength,
                    MegaBytesPerSecond: Math.Round(mbPerSec, 1),
                    EstimatedTimeRemaining: TimeSpan.FromSeconds(remSec),
                    IsPaused: pauseToken.IsPaused,
                    Stages: CreateDuplicateStages(srcFilesCount, srcFilesBytes, totalCandidates, completed, mbPerSec, currentFile.RelativePath, isCompleted: false)));
            }).ConfigureAwait(false);

        List<DestinationFileEntry> hydratedCandidates = hydratedArray.ToList();

        // Sub-group candidate destination files by (FileLength, HeadHash, TailHash),
        // filtering out any destination files that do not match a source file's head/tail hash
        Dictionary<(long Length, ulong Head, ulong Tail), List<DestinationFileEntry>> headTailBuckets = new();
        foreach (DestinationFileEntry entry in hydratedCandidates)
        {
            (long Length, MediaCategory Category, ulong Head, ulong Tail) sourceKey = (
                entry.File.FileLength,
                entry.File.Category,
                entry.File.HeadHash ?? 0UL,
                entry.File.TailHash ?? 0UL);

            if (!validSourceHeadTailKeys.Contains(sourceKey))
            {
                continue;
            }

            (long Length, ulong Head, ulong Tail) bucketKey = (
                entry.File.FileLength,
                entry.File.HeadHash ?? 0UL,
                entry.File.TailHash ?? 0UL);

            if (!headTailBuckets.TryGetValue(bucketKey, out List<DestinationFileEntry>? bucket))
            {
                bucket = new List<DestinationFileEntry>();
                headTailBuckets[bucketKey] = bucket;
            }

            bucket.Add(entry);
        }

        List<List<DestinationFileEntry>> quickCandidateBuckets = headTailBuckets.Values
            .Where(bucket => bucket.Count > 1)
            .ToList();

        if (quickCandidateBuckets.Count == 0)
        {
            if (_cache is not null && !cacheUpsertBag.IsEmpty)
            {
                await _cache.UpsertBatchAsync(cacheUpsertBag.ToList(), cancellationToken).ConfigureAwait(false);
            }

            return DuplicateAnalysisResult.Empty;
        }

        // In Quick mode, duplicate detection completes with the quick scan candidates (zero full-file reads)
        if (mode == VerificationMode.Quick)
        {
            if (_cache is not null && !cacheUpsertBag.IsEmpty)
            {
                await _cache.UpsertBatchAsync(cacheUpsertBag.ToList(), cancellationToken).ConfigureAwait(false);
            }

            List<(long FileSize, string GroupKey, List<DestinationFileEntry> Entries)> quickGroups = new();
            foreach (List<DestinationFileEntry> bucket in quickCandidateBuckets)
            {
                long fileSize = bucket[0].File.FileLength;
                ulong head = bucket[0].File.HeadHash ?? 0UL;
                ulong tail = bucket[0].File.TailHash ?? 0UL;
                string groupKey = $"{head:x16}_{tail:x16}";
                quickGroups.Add((fileSize, groupKey, bucket));
            }

            return BuildDuplicateAnalysisResult(quickGroups);
        }

        // Stage 3: Deep Probe or Bit-for-Bit Hash Verification (Deep and Full Modes).
        // Thoroughly verify ONLY candidate duplicate groups that matched on size and head/tail hashes with source files.
        List<DestinationFileEntry> confirmedCandidates = quickCandidateBuckets
            .SelectMany(bucket => bucket)
            .ToList();

        bool isDeep = mode == VerificationMode.Deep;
        string phasePrefix = isDeep ? "Verifying Deep Probe Hash" : "Verifying Bit-for-Bit Hash";
        int totalConfirmed = confirmedCandidates.Count;
        long totalConfirmedBytes = confirmedCandidates.Sum(e => e.File.FileLength);

        DuplicateAuditCheckpoint hashCheckpoint = new(
            Phase: phasePrefix,
            SourceFileCount: srcFilesCount,
            SourceBytes: srcFilesBytes,
            TotalCandidates: totalConfirmed,
            ProcessedCandidates: 0,
            TotalCandidateBytes: totalConfirmedBytes,
            ProcessedCandidateBytes: 0);

        // Hydrate deep or full hashes for relevant source files
        HashSet<(long Length, ulong Hash)> validSourceKeys = new();
        foreach (MediaFile sf in hydratedSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CheckPauseAsync(pauseToken, progress, hashCheckpoint, cancellationToken).ConfigureAwait(false);

            MediaFile currentSource = sf;
            if (isDeep)
            {
                if (!currentSource.DeepHash.HasValue)
                {
                    ulong deepHash = DeepProbeHasher.ComputeDeepHash(currentSource.FullPath, cancellationToken: cancellationToken);
                    currentSource = currentSource with { DeepHash = deepHash };
                    cacheUpsertBag.Add(currentSource);
                }

                if (currentSource.DeepHash.HasValue)
                {
                    validSourceKeys.Add((currentSource.FileLength, currentSource.DeepHash.Value));
                }
            }
            else
            {
                if (!currentSource.FullHash.HasValue)
                {
                    if (currentSource.FileLength <= ChunkReader.DEFAULT_CHUNK_SIZE && currentSource.HeadHash.HasValue)
                    {
                        currentSource = currentSource with { FullHash = currentSource.HeadHash.Value };
                    }
                    else
                    {
                        ulong fullHash = ComputeFullFileHash(currentSource.FullPath, cancellationToken);
                        currentSource = currentSource with { FullHash = fullHash };
                    }

                    cacheUpsertBag.Add(currentSource);
                }

                if (currentSource.FullHash.HasValue)
                {
                    validSourceKeys.Add((currentSource.FileLength, currentSource.FullHash.Value));
                }
            }
        }

        long processedConfirmedCount = 0;
        long processedConfirmedBytes = 0;
        Stopwatch stage3Stopwatch = Stopwatch.StartNew();

        DestinationFileEntry[] fullyHashedArray = new DestinationFileEntry[totalConfirmed];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, totalConfirmed),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            },
            async (index, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                await CheckPauseAsync(
                    pauseToken,
                    progress,
                    hashCheckpoint with
                    {
                        ProcessedCandidates = Interlocked.Read(ref processedConfirmedCount),
                        ProcessedCandidateBytes = Interlocked.Read(ref processedConfirmedBytes),
                    },
                    ct).ConfigureAwait(false);

                DestinationFileEntry entry = confirmedCandidates[index];
                MediaFile currentFile = entry.File;

                if (isDeep)
                {
                    if (!currentFile.DeepHash.HasValue)
                    {
                        ulong deepHash = DeepProbeHasher.ComputeDeepHash(currentFile.FullPath, cancellationToken: ct);
                        currentFile = currentFile with { DeepHash = deepHash };
                        cacheUpsertBag.Add(currentFile);
                    }
                }
                else
                {
                    if (!currentFile.FullHash.HasValue)
                    {
                        if (currentFile.FileLength <= ChunkReader.DEFAULT_CHUNK_SIZE && currentFile.HeadHash.HasValue)
                        {
                            currentFile = currentFile with { FullHash = currentFile.HeadHash.Value };
                        }
                        else
                        {
                            ulong fullHash = ComputeFullFileHash(currentFile.FullPath, ct);
                            currentFile = currentFile with { FullHash = fullHash };
                        }

                        cacheUpsertBag.Add(currentFile);
                    }
                }

                fullyHashedArray[index] = new DestinationFileEntry(
                    currentFile,
                    entry.DestinationId,
                    entry.DestinationName);

                long completed = Interlocked.Increment(ref processedConfirmedCount);
                long completedBytes = Interlocked.Add(ref processedConfirmedBytes, currentFile.FileLength);

                double elapsed = stage3Stopwatch.Elapsed.TotalSeconds;
                double mbPerSec = elapsed > 0 ? (completedBytes / (1024.0 * 1024.0)) / elapsed : 0.0;
                long remBytes = Math.Max(0, totalConfirmedBytes - completedBytes);
                double remSec = mbPerSec > 0 ? (remBytes / (1024.0 * 1024.0)) / mbPerSec : 0.0;

                progress?.Report(new VerificationProgress(
                    CurrentFile: currentFile.RelativePath,
                    ProcessedFiles: (int)completed,
                    TotalFiles: totalConfirmed,
                    ProcessedBytes: completedBytes,
                    TotalBytes: totalConfirmedBytes,
                    Phase: $"{phasePrefix}: {Path.GetFileName(currentFile.FullPath)}",
                    CurrentFileBytes: currentFile.FileLength,
                    CurrentFileProcessedBytes: currentFile.FileLength,
                    MegaBytesPerSecond: Math.Round(mbPerSec, 1),
                    EstimatedTimeRemaining: TimeSpan.FromSeconds(remSec),
                    IsPaused: pauseToken.IsPaused,
                    Stages: CreateDuplicateStages(srcFilesCount, srcFilesBytes, totalConfirmed, completed, mbPerSec, currentFile.RelativePath, isCompleted: false)));
            }).ConfigureAwait(false);

        if (_cache is not null && !cacheUpsertBag.IsEmpty)
        {
            await _cache.UpsertBatchAsync(cacheUpsertBag.ToList(), cancellationToken).ConfigureAwait(false);
        }

        List<DestinationFileEntry> fullyHashedCandidates = fullyHashedArray.ToList();

        // Sub-group by (FileLength, Hash), only keeping destination files whose hash matches a source file
        Dictionary<(long Length, ulong Hash), List<DestinationFileEntry>> finalBuckets = new();
        foreach (DestinationFileEntry entry in fullyHashedCandidates)
        {
            ulong? effectiveHash = isDeep ? entry.File.DeepHash : entry.File.FullHash;
            (long Length, ulong Hash) key = (
                entry.File.FileLength,
                effectiveHash ?? 0UL);

            if (!validSourceKeys.Contains(key))
            {
                continue;
            }

            if (!finalBuckets.TryGetValue(key, out List<DestinationFileEntry>? bucket))
            {
                bucket = new List<DestinationFileEntry>();
                finalBuckets[key] = bucket;
            }

            bucket.Add(entry);
        }

        List<(long FileSize, string GroupKey, List<DestinationFileEntry> Entries)> fullGroups = new();
        foreach (KeyValuePair<(long Length, ulong Hash), List<DestinationFileEntry>> kvp in finalBuckets)
        {
            if (kvp.Value.Count > 1)
            {
                fullGroups.Add((kvp.Key.Length, $"{kvp.Key.Hash:x16}", kvp.Value));
            }
        }

        return BuildDuplicateAnalysisResult(fullGroups);
    }

    private static DuplicateAnalysisResult BuildDuplicateAnalysisResult(
        IEnumerable<(long FileSize, string GroupKey, List<DestinationFileEntry> Entries)> groups)
    {
        List<DuplicateGroup> duplicateGroups = new();
        long totalReclaimableBytes = 0;
        int totalDuplicateCopies = 0;
        int crossDestinationRedundantCount = 0;

        foreach ((long fileSize, string groupKey, List<DestinationFileEntry> groupEntries) in groups)
        {
            if (groupEntries.Count <= 1)
            {
                continue;
            }

            HashSet<string> distinctDestIds = groupEntries.Select(e => e.DestinationId).ToHashSet();
            bool isCrossDestination = distinctDestIds.Count > 1;
            if (isCrossDestination)
            {
                crossDestinationRedundantCount++;
            }

            Dictionary<string, List<DestinationFileEntry>> byDestination = new();
            foreach (DestinationFileEntry entry in groupEntries)
            {
                if (!byDestination.TryGetValue(entry.DestinationId, out List<DestinationFileEntry>? list))
                {
                    list = new List<DestinationFileEntry>();
                    byDestination[entry.DestinationId] = list;
                }

                list.Add(entry);
            }

            bool isIntraDestination = false;
            long groupReclaimableBytes = 0;
            List<DuplicateFileItem> groupFiles = new();

            foreach (KeyValuePair<string, List<DestinationFileEntry>> destKvp in byDestination)
            {
                List<DestinationFileEntry> destList = destKvp.Value;
                bool isDestIntra = destList.Count > 1;
                if (isDestIntra)
                {
                    isIntraDestination = true;
                    int redundantCountInDest = destList.Count - 1;
                    groupReclaimableBytes += redundantCountInDest * fileSize;
                    totalDuplicateCopies += redundantCountInDest;
                }

                for (int i = 0; i < destList.Count; i++)
                {
                    DestinationFileEntry entry = destList[i];
                    bool isPrimary = (i == 0);
                    groupFiles.Add(new DuplicateFileItem(
                        File: entry.File,
                        DestinationId: entry.DestinationId,
                        DestinationName: entry.DestinationName,
                        IsPrimary: isPrimary,
                        RelativePath: entry.File.RelativePath,
                        IsSameDestinationDuplicate: isDestIntra));
                }
            }

            totalReclaimableBytes += groupReclaimableBytes;

            duplicateGroups.Add(new DuplicateGroup(
                GroupKey: groupKey,
                FileSize: fileSize,
                ReclaimableBytes: groupReclaimableBytes,
                IsIntraDestination: isIntraDestination,
                IsCrossDestination: isCrossDestination,
                Files: groupFiles));
        }

        return new DuplicateAnalysisResult(
            Groups: duplicateGroups,
            TotalDuplicateCopies: totalDuplicateCopies,
            TotalReclaimableBytes: totalReclaimableBytes,
            CrossDestinationRedundantFileCount: crossDestinationRedundantCount);
    }

    // The UI leaves its "pausing" state only when a report says the audit is paused, so report before blocking.
    // Otherwise a pause that lands in a sequential or cached step waits here with nothing reported.
    private static async Task CheckPauseAsync(
        PauseToken pauseToken,
        IProgress<VerificationProgress>? progress,
        DuplicateAuditCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (!pauseToken.IsPaused)
        {
            return;
        }

        progress?.Report(new VerificationProgress(
            CurrentFile: string.Empty,
            ProcessedFiles: (int)checkpoint.ProcessedCandidates,
            TotalFiles: checkpoint.TotalCandidates,
            ProcessedBytes: checkpoint.ProcessedCandidateBytes,
            TotalBytes: checkpoint.TotalCandidateBytes,
            Phase: $"{checkpoint.Phase} (Paused)",
            CurrentFileBytes: 0,
            CurrentFileProcessedBytes: 0,
            MegaBytesPerSecond: 0,
            EstimatedTimeRemaining: TimeSpan.Zero,
            IsPaused: true,
            Stages: CreateDuplicateStages(
                checkpoint.SourceFileCount,
                checkpoint.SourceBytes,
                checkpoint.TotalCandidates,
                checkpoint.ProcessedCandidates,
                0.0,
                null,
                isCompleted: false)));

        await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ulong ComputeFullFileHash(string filePath, CancellationToken cancellationToken)
    {
        using SafeFileHandle handle = NativeDirectIO.OpenDirectOrSequential(filePath);
        using FileStream stream = new(handle, FileAccess.Read);

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(FULL_HASH_BUFFER_SIZE);
        try
        {
            return SimdHasher.HashStream(stream, rentedBuffer, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private static IReadOnlyList<StageProgressInfo> CreateDuplicateStages(
        int totalFiles,
        long totalBytes,
        int totalCandidates,
        long processedCandidates,
        double mbPerSec,
        string? activeFile,
        bool isCompleted = false)
    {
        double dupPct = isCompleted
            ? 100.0
            : (totalCandidates > 0 ? Math.Min(100.0, (processedCandidates / (double)totalCandidates) * 100.0) : 0.0);

        return new StageProgressInfo[]
        {
            new(
                Id: PipelineStageId.Discovery,
                Title: "Media Discovery",
                Status: StageStatus.Completed,
                Percentage: 100.0,
                ProcessedUnits: totalFiles,
                TotalUnits: totalFiles,
                ProgressText: $"{totalFiles:N0} files ({ByteSizeFormatter.Format(totalBytes)})",
                TelemetryText: "Complete"),
            new(
                Id: PipelineStageId.Hashing,
                Title: "Source Hashing",
                Status: StageStatus.Completed,
                Percentage: 100.0,
                ProcessedUnits: totalBytes,
                TotalUnits: totalBytes,
                ProgressText: $"{ByteSizeFormatter.Format(totalBytes)} ({totalFiles:N0} files)",
                TelemetryText: "Complete"),
            new(
                Id: PipelineStageId.DestinationMatching,
                Title: "Destination Verification",
                Status: StageStatus.Completed,
                Percentage: 100.0,
                ProcessedUnits: totalFiles,
                TotalUnits: totalFiles,
                ProgressText: $"{totalFiles:N0} / {totalFiles:N0} files verified",
                TelemetryText: "Complete"),
            new(
                Id: PipelineStageId.DuplicateAnalysis,
                Title: "Duplicate Analysis",
                Status: isCompleted ? StageStatus.Completed : StageStatus.Running,
                Percentage: dupPct,
                ProcessedUnits: processedCandidates,
                TotalUnits: totalCandidates,
                ProgressText: isCompleted
                    ? $"{totalCandidates:N0} candidates checked"
                    : $"{processedCandidates:N0} / {totalCandidates:N0} candidates checked",
                TelemetryText: isCompleted ? "Complete" : (mbPerSec > 0 ? $"{mbPerSec:F1} MB/s" : "Auditing..."),
                ActiveFileName: isCompleted ? null : activeFile)
        };
    }
}
