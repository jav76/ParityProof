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
using ParityProof.Engine.Hashing;
using ParityProof.Engine.IO;
using ParityProof.Platform.Diagnostics;

namespace ParityProof.Engine.Matching;

[LogMethod]
public sealed class DuplicateAnalyzer : IDuplicateAnalyzer
{
    private const int FULL_HASH_BUFFER_SIZE = 1024 * 1024; // 1 MB buffer
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

        progress?.Report(new VerificationProgress(
            CurrentFile: string.Empty,
            ProcessedFiles: 0,
            TotalFiles: 0,
            ProcessedBytes: 0,
            TotalBytes: 0,
            Phase: "Auditing destinations for duplicates from source directory..."));

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
        await CheckPauseAsync(pauseToken, cancellationToken).ConfigureAwait(false);

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
        await CheckPauseAsync(pauseToken, cancellationToken).ConfigureAwait(false);

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
            Phase: "Auditing candidate duplicates: Quick head/tail scan...",
            CurrentFileBytes: 0,
            CurrentFileProcessedBytes: 0,
            MegaBytesPerSecond: 0,
            EstimatedTimeRemaining: TimeSpan.Zero,
            IsPaused: false));

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
                await CheckPauseAsync(pauseToken, ct).ConfigureAwait(false);

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
                    IsPaused: pauseToken.IsPaused));
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

        // Hydrate deep or full hashes for relevant source files
        HashSet<(long Length, ulong Hash)> validSourceKeys = new();
        foreach (MediaFile sf in hydratedSources)
        {
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

        int totalConfirmed = confirmedCandidates.Count;
        long totalConfirmedBytes = confirmedCandidates.Sum(e => e.File.FileLength);
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
                await CheckPauseAsync(pauseToken, ct).ConfigureAwait(false);

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
                    IsPaused: pauseToken.IsPaused));
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

    private static async Task CheckPauseAsync(PauseToken pauseToken, CancellationToken cancellationToken)
    {
        if (pauseToken.IsPaused)
        {
            await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        }
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
}
