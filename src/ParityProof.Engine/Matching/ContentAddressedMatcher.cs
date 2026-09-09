using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Logging;
using ParityProof.Core.Models;
using ParityProof.Engine.IO;

namespace ParityProof.Engine.Matching;

[LogMethod]
public sealed class ContentAddressedMatcher
{
    private readonly IIndexCache? _cache;

    public ContentAddressedMatcher(IIndexCache? cache = null)
    {
        _cache = cache;
    }

    public async Task<MediaFile> EnsureHashesAsync(
        MediaFile file,
        VerificationMode mode,
        ConcurrentDictionary<string, MediaFile>? inMemoryCache = null,
        ChannelWriter<MediaFile>? persistenceWriter = null,
        Action<long>? onBytesRead = null,
        CancellationToken cancellationToken = default)
    {
        if (mode == VerificationMode.SuperFast)
        {
            return file;
        }

        MediaFile updated = file;

        if (inMemoryCache is not null && inMemoryCache.TryGetValue(file.FullPath, out MediaFile? memCached))
        {
            if (mode == VerificationMode.Quick && memCached.HeadHash.HasValue && memCached.TailHash.HasValue)
            {
                return memCached;
            }

            if (mode == VerificationMode.Deep && memCached.DeepHash.HasValue)
            {
                return memCached;
            }

            if (mode == VerificationMode.Full && memCached.FullHash.HasValue)
            {
                return memCached;
            }

            updated = memCached;
        }
        else if (_cache is not null)
        {
            MediaFile? cached = await _cache.GetAsync(
                file.FullPath,
                file.FileLength,
                file.LastWriteTimeUtc,
                cancellationToken).ConfigureAwait(false);

            if (cached is not null)
            {
                inMemoryCache?.TryAdd(cached.FullPath, cached);

                if (mode == VerificationMode.Quick && cached.HeadHash.HasValue && cached.TailHash.HasValue)
                {
                    return cached;
                }

                if (mode == VerificationMode.Deep && cached.DeepHash.HasValue)
                {
                    return cached;
                }

                if (mode == VerificationMode.Full && cached.FullHash.HasValue)
                {
                    return cached;
                }

                updated = cached;
            }
        }

        if (mode == VerificationMode.Quick)
        {
            if (!updated.HeadHash.HasValue || !updated.TailHash.HasValue)
            {
                (ulong head, ulong tail) = ChunkReader.ComputeHeadTailHash(file.FullPath);
                updated = updated with { HeadHash = head, TailHash = tail };
                onBytesRead?.Invoke(ChunkReader.DEFAULT_CHUNK_SIZE * 2);

                if (inMemoryCache is not null)
                {
                    inMemoryCache[updated.FullPath] = updated;
                }

                if (persistenceWriter is not null)
                {
                    persistenceWriter.TryWrite(updated);
                }
                else if (_cache is not null)
                {
                    await _cache.UpsertBatchAsync(new[] { updated }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else if (mode == VerificationMode.Deep)
        {
            if (!updated.DeepHash.HasValue)
            {
                ulong deepHash = DeepProbeHasher.ComputeDeepHash(
                    file.FullPath,
                    onBytesRead: onBytesRead,
                    cancellationToken: cancellationToken);

                updated = updated with { DeepHash = deepHash };

                if (inMemoryCache is not null)
                {
                    inMemoryCache[updated.FullPath] = updated;
                }

                if (persistenceWriter is not null)
                {
                    persistenceWriter.TryWrite(updated);
                }
                else if (_cache is not null)
                {
                    await _cache.UpsertBatchAsync(new[] { updated }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else if (mode == VerificationMode.Full)
        {
            if (!updated.FullHash.HasValue)
            {
                FileHashResult hashResult = await AsyncDoubleBufferedFileHasher.ComputeHashAsync(
                    file.FullPath,
                    onBytesRead: onBytesRead,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                updated = updated with
                {
                    FullHash = hashResult.FullHash,
                    HeadHash = updated.HeadHash ?? hashResult.HeadHash
                };

                if (inMemoryCache is not null)
                {
                    inMemoryCache[updated.FullPath] = updated;
                }

                if (persistenceWriter is not null)
                {
                    persistenceWriter.TryWrite(updated);
                }
                else if (_cache is not null)
                {
                    await _cache.UpsertBatchAsync(new[] { updated }, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return updated;
    }

    public async Task<FileMatchStatus> MatchFileAsync(
        MediaFile sourceFile,
        BackupDestination destination,
        Dictionary<long, List<MediaFile>> destinationIndexByLength,
        VerificationMode mode,
        ConcurrentDictionary<string, MediaFile>? inMemoryCache = null,
        ChannelWriter<MediaFile>? persistenceWriter = null,
        Action<long>? onSourceBytesRead = null,
        Action<long>? onDestBytesRead = null,
        CancellationToken cancellationToken = default)
    {
        if (!destinationIndexByLength.TryGetValue(sourceFile.FileLength, out List<MediaFile>? candidates) ||
            candidates.Count == 0)
        {
            return new FileMatchStatus(
                DestinationId: destination.Id,
                DestinationRootPath: destination.RootPath,
                Status: MediaStatus.Missing,
                FailureReason: "No file found with matching byte size.");
        }

        string sourceFileName = Path.GetFileName(sourceFile.FullPath);

        if (mode == VerificationMode.SuperFast)
        {
            MediaFile? exactPathMatch = candidates.FirstOrDefault(c =>
                string.Equals(c.RelativePath, sourceFile.RelativePath, StringComparison.OrdinalIgnoreCase));

            if (exactPathMatch is not null)
            {
                return new FileMatchStatus(
                    DestinationId: destination.Id,
                    DestinationRootPath: destination.RootPath,
                    Status: MediaStatus.Verified,
                    MatchedFilePath: exactPathMatch.FullPath);
            }

            MediaFile? nameMatch = candidates.FirstOrDefault(c =>
                string.Equals(Path.GetFileName(c.FullPath), sourceFileName, StringComparison.OrdinalIgnoreCase));

            if (nameMatch is not null)
            {
                return new FileMatchStatus(
                    DestinationId: destination.Id,
                    DestinationRootPath: destination.RootPath,
                    Status: MediaStatus.Verified,
                    MatchedFilePath: nameMatch.FullPath);
            }

            if (candidates.Count == 1)
            {
                return new FileMatchStatus(
                    DestinationId: destination.Id,
                    DestinationRootPath: destination.RootPath,
                    Status: MediaStatus.Verified,
                    MatchedFilePath: candidates[0].FullPath);
            }

            return new FileMatchStatus(
                DestinationId: destination.Id,
                DestinationRootPath: destination.RootPath,
                Status: MediaStatus.Missing,
                FailureReason: "Multiple candidates with identical size but differing filenames.");
        }

        if (mode == VerificationMode.Quick)
        {
            MediaFile hydratedSource = await EnsureHashesAsync(
                sourceFile,
                VerificationMode.Quick,
                inMemoryCache,
                persistenceWriter,
                onSourceBytesRead,
                cancellationToken).ConfigureAwait(false);

            foreach (MediaFile candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                MediaFile hydratedCandidate = await EnsureHashesAsync(
                    candidate,
                    VerificationMode.Quick,
                    inMemoryCache,
                    persistenceWriter,
                    onDestBytesRead,
                    cancellationToken).ConfigureAwait(false);

                if (hydratedSource.HeadHash == hydratedCandidate.HeadHash &&
                    hydratedSource.TailHash == hydratedCandidate.TailHash)
                {
                    return new FileMatchStatus(
                        DestinationId: destination.Id,
                        DestinationRootPath: destination.RootPath,
                        Status: MediaStatus.Verified,
                        MatchedFilePath: hydratedCandidate.FullPath);
                }
            }

            bool hasSamePathCandidate = candidates.Any(c =>
                string.Equals(c.RelativePath, sourceFile.RelativePath, StringComparison.OrdinalIgnoreCase));

            return new FileMatchStatus(
                DestinationId: destination.Id,
                DestinationRootPath: destination.RootPath,
                Status: hasSamePathCandidate ? MediaStatus.Corrupt : MediaStatus.Missing,
                FailureReason: hasSamePathCandidate
                    ? "Checksum mismatch on head/tail chunks."
                    : "No candidate matched head/tail checksum.");
        }

        if (mode == VerificationMode.Deep)
        {
            MediaFile hydratedSource = await EnsureHashesAsync(
                sourceFile,
                VerificationMode.Deep,
                inMemoryCache,
                persistenceWriter,
                onSourceBytesRead,
                cancellationToken).ConfigureAwait(false);

            foreach (MediaFile candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                MediaFile hydratedCandidate = await EnsureHashesAsync(
                    candidate,
                    VerificationMode.Deep,
                    inMemoryCache,
                    persistenceWriter,
                    onDestBytesRead,
                    cancellationToken).ConfigureAwait(false);

                if (hydratedSource.DeepHash == hydratedCandidate.DeepHash)
                {
                    return new FileMatchStatus(
                        DestinationId: destination.Id,
                        DestinationRootPath: destination.RootPath,
                        Status: MediaStatus.Verified,
                        MatchedFilePath: hydratedCandidate.FullPath);
                }
            }

            bool hasSamePathCandidate = candidates.Any(c =>
                string.Equals(c.RelativePath, sourceFile.RelativePath, StringComparison.OrdinalIgnoreCase));

            return new FileMatchStatus(
                DestinationId: destination.Id,
                DestinationRootPath: destination.RootPath,
                Status: hasSamePathCandidate ? MediaStatus.Corrupt : MediaStatus.Missing,
                FailureReason: hasSamePathCandidate
                    ? "Probe checksum mismatch across interior and boundary slices."
                    : "No candidate matched deep probe checksum.");
        }

        if (mode == VerificationMode.Full)
        {
            List<MediaFile> orderedCandidates = candidates
                .OrderByDescending(c => string.Equals(c.RelativePath, sourceFile.RelativePath, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(c => string.Equals(Path.GetFileName(c.FullPath), sourceFileName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            bool isCleanPathMatch = orderedCandidates.Count == 1 &&
                string.Equals(orderedCandidates[0].RelativePath, sourceFile.RelativePath, StringComparison.OrdinalIgnoreCase);

            if (isCleanPathMatch)
            {
                // Direct full stream with in-flight 64 KB head abort for clean 1:1 match
                MediaFile hydratedSource = await EnsureHashesAsync(
                    sourceFile,
                    VerificationMode.Full,
                    inMemoryCache,
                    persistenceWriter,
                    onSourceBytesRead,
                    cancellationToken).ConfigureAwait(false);

                MediaFile candidate = orderedCandidates[0];
                MediaFile hydratedCandidate;

                if (candidate.FullHash.HasValue)
                {
                    hydratedCandidate = candidate;
                }
                else
                {
                    FileHashResult candHash = await AsyncDoubleBufferedFileHasher.ComputeHashAsync(
                        candidate.FullPath,
                        expectedHeadHash: hydratedSource.HeadHash,
                        onBytesRead: onDestBytesRead,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (candHash.AbortedEarly || !candHash.Success)
                    {
                        return new FileMatchStatus(
                            DestinationId: destination.Id,
                            DestinationRootPath: destination.RootPath,
                            Status: MediaStatus.Corrupt,
                            FailureReason: "Full file checksum mismatch detected in initial stream chunk.");
                    }

                    hydratedCandidate = candidate with
                    {
                        FullHash = candHash.FullHash,
                        HeadHash = candidate.HeadHash ?? candHash.HeadHash
                    };

                    if (inMemoryCache is not null)
                    {
                        inMemoryCache[hydratedCandidate.FullPath] = hydratedCandidate;
                    }

                    if (persistenceWriter is not null)
                    {
                        persistenceWriter.TryWrite(hydratedCandidate);
                    }
                    else if (_cache is not null)
                    {
                        await _cache.UpsertBatchAsync(new[] { hydratedCandidate }, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (hydratedSource.FullHash == hydratedCandidate.FullHash)
                {
                    return new FileMatchStatus(
                        DestinationId: destination.Id,
                        DestinationRootPath: destination.RootPath,
                        Status: MediaStatus.Verified,
                        MatchedFilePath: hydratedCandidate.FullPath);
                }

                return new FileMatchStatus(
                    DestinationId: destination.Id,
                    DestinationRootPath: destination.RootPath,
                    Status: MediaStatus.Corrupt,
                    FailureReason: "Full file bit-for-bit checksum mismatch.");
            }
            else
            {
                // Ambiguous or multiple candidates: run 128 KB Quick pass first to prune false candidates
                MediaFile quickSource = await EnsureHashesAsync(
                    sourceFile,
                    VerificationMode.Quick,
                    inMemoryCache,
                    persistenceWriter,
                    onSourceBytesRead,
                    cancellationToken).ConfigureAwait(false);

                List<MediaFile> qualifiedCandidates = new();
                foreach (MediaFile candidate in orderedCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MediaFile quickCand = await EnsureHashesAsync(
                        candidate,
                        VerificationMode.Quick,
                        inMemoryCache,
                        persistenceWriter,
                        onDestBytesRead,
                        cancellationToken).ConfigureAwait(false);

                    if (quickSource.HeadHash == quickCand.HeadHash &&
                        quickSource.TailHash == quickCand.TailHash)
                    {
                        qualifiedCandidates.Add(quickCand);
                    }
                }

                if (qualifiedCandidates.Count > 0)
                {
                    MediaFile hydratedSource = await EnsureHashesAsync(
                        quickSource,
                        VerificationMode.Full,
                        inMemoryCache,
                        persistenceWriter,
                        onSourceBytesRead,
                        cancellationToken).ConfigureAwait(false);

                    foreach (MediaFile qualifiedCand in qualifiedCandidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        MediaFile hydratedCand = await EnsureHashesAsync(
                            qualifiedCand,
                            VerificationMode.Full,
                            inMemoryCache,
                            persistenceWriter,
                            onDestBytesRead,
                            cancellationToken).ConfigureAwait(false);

                        if (hydratedSource.FullHash == hydratedCand.FullHash)
                        {
                            return new FileMatchStatus(
                                DestinationId: destination.Id,
                                DestinationRootPath: destination.RootPath,
                                Status: MediaStatus.Verified,
                                MatchedFilePath: hydratedCand.FullPath);
                        }
                    }
                }

                bool hasSamePathCandidate = candidates.Any(c =>
                    string.Equals(c.RelativePath, sourceFile.RelativePath, StringComparison.OrdinalIgnoreCase));

                return new FileMatchStatus(
                    DestinationId: destination.Id,
                    DestinationRootPath: destination.RootPath,
                    Status: hasSamePathCandidate ? MediaStatus.Corrupt : MediaStatus.Missing,
                    FailureReason: hasSamePathCandidate
                        ? "Full file bit-for-bit checksum mismatch."
                        : "No candidate matched full file checksum.");
            }
        }

        return new FileMatchStatus(
            DestinationId: destination.Id,
            DestinationRootPath: destination.RootPath,
            Status: MediaStatus.Missing);
    }
}
