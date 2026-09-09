using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Logging;
using ParityProof.Core.Models;
using ParityProof.Engine.Hashing;
using ParityProof.Engine.IO;
using Microsoft.Win32.SafeHandles;

namespace ParityProof.Engine.Matching;

[LogMethod]
public sealed class ContentAddressedMatcher
{
    private const int FULL_HASH_BUFFER_SIZE = 1024 * 1024; // 1 MB buffer

    private readonly IIndexCache? _cache;

    public ContentAddressedMatcher(IIndexCache? cache = null)
    {
        _cache = cache;
    }

    public async Task<MediaFile> EnsureHashesAsync(
        MediaFile file,
        VerificationMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode == VerificationMode.SuperFast)
        {
            return file;
        }

        MediaFile updated = file;

        if (_cache is not null)
        {
            MediaFile? cached = await _cache.GetAsync(
                file.FullPath,
                file.FileLength,
                file.LastWriteTimeUtc,
                cancellationToken).ConfigureAwait(false);

            if (cached is not null)
            {
                if (mode == VerificationMode.Quick && cached.HeadHash.HasValue && cached.TailHash.HasValue)
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

                if (_cache is not null)
                {
                    await _cache.UpsertBatchAsync(new[] { updated }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else if (mode == VerificationMode.Full)
        {
            if (!updated.FullHash.HasValue)
            {
                ulong fullHash = ComputeFullFileHash(file.FullPath, cancellationToken);
                updated = updated with { FullHash = fullHash };

                if (_cache is not null)
                {
                    await _cache.UpsertBatchAsync(new[] { updated }, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return updated;
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

    public async Task<FileMatchStatus> MatchFileAsync(
        MediaFile sourceFile,
        BackupDestination destination,
        Dictionary<long, List<MediaFile>> destinationIndexByLength,
        VerificationMode mode,
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
            MediaFile hydratedSource = await EnsureHashesAsync(sourceFile, VerificationMode.Quick, cancellationToken)
                .ConfigureAwait(false);

            foreach (MediaFile candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                MediaFile hydratedCandidate = await EnsureHashesAsync(candidate, VerificationMode.Quick, cancellationToken)
                    .ConfigureAwait(false);

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
                FailureReason: hasSamePathCandidate ? "Checksum mismatch on head/tail chunks." : "No candidate matched head/tail checksum.");
        }

        if (mode == VerificationMode.Full)
        {
            MediaFile hydratedSource = await EnsureHashesAsync(sourceFile, VerificationMode.Full, cancellationToken)
                .ConfigureAwait(false);

            foreach (MediaFile candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                MediaFile hydratedCandidate = await EnsureHashesAsync(candidate, VerificationMode.Full, cancellationToken)
                    .ConfigureAwait(false);

                if (hydratedSource.FullHash == hydratedCandidate.FullHash)
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
                FailureReason: hasSamePathCandidate ? "Full file bit-for-bit checksum mismatch." : "No candidate matched full file checksum.");
        }

        return new FileMatchStatus(
            DestinationId: destination.Id,
            DestinationRootPath: destination.RootPath,
            Status: MediaStatus.Missing);
    }
}
