using System;
using System.Buffers;
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
using ParityProof.Core.Utils;
using ParityProof.Engine.Hashing;
using ParityProof.Engine.IO;

namespace ParityProof.Engine.Transfer;

[LogMethod]
public sealed class MediaCopier : IMediaCopier
{
    private const int COPY_BUFFER_SIZE = 2 * 1024 * 1024; // 2 MB streaming buffer
    private const int HEAD_TAIL_CHUNK_SIZE = 64 * 1024; // 64 KB head/tail boundary chunk
    private const int PAGE_CACHE_EVICTION_INTERVAL_BYTES = 16 * 1024 * 1024; // 16 MB kernel page cache boundary
    private const string TEMP_FILE_EXTENSION = ".parityproof.tmp";
    private const int MAX_COLLISION_SUFFIX = 10_000;
    private const int MAX_COMMIT_ATTEMPTS = 5;
    private const int WIN32_ERROR_CODE_MASK = 0xFFFF;
    private const int WIN32_ERROR_HANDLE_DISK_FULL = 39;
    private const int WIN32_ERROR_DISK_FULL = 112;
    private const int UNIX_ENOSPC = 28;
    private const string DESTINATION_FULL_MESSAGE =
        "Not copied: this destination ran out of space and has too little free space left for this file.";
    private const string DESTINATION_UNAVAILABLE_MESSAGE =
        "Not copied: this destination could not be reached after an earlier failure in this batch.";

    public Task<CopyBatchResult> CopyMissingFilesAsync(
        IReadOnlyList<MediaFile> missingFiles,
        string destinationRootPath,
        IProgress<CopyProgressInfo>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default)
    {
        return CopyMissingFilesAsync(
            missingFiles,
            new[] { destinationRootPath },
            progress,
            cancellationToken,
            pauseToken);
    }

    public async Task<CopyBatchResult> CopyMissingFilesAsync(
        IReadOnlyList<MediaFile> missingFiles,
        IReadOnlyList<string> destinationRootPaths,
        IProgress<CopyProgressInfo>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default)
    {
        if (missingFiles.Count == 0 || destinationRootPaths.Count == 0)
        {
            return CopyBatchResult.Empty;
        }

        List<string> activeDestPaths = destinationRootPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (activeDestPaths.Count == 0)
        {
            return CopyBatchResult.Empty;
        }

        long totalBytes = missingFiles.Sum(f => f.FileLength);
        long totalCopiedBytes = 0;
        int completedFiles = 0;
        int copiedFiles = 0;
        int failedFiles = 0;
        int renamedCopyCount = 0;
        List<CopyFailure> failures = new();
        HashSet<string> fullDestRoots = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> unavailableDestRoots = new(StringComparer.OrdinalIgnoreCase);
        Stopwatch overallStopwatch = Stopwatch.StartNew();

        CollisionSiblingIndex siblingIndex = new();

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(COPY_BUFFER_SIZE);
        byte[] tailBuffer = new byte[HEAD_TAIL_CHUNK_SIZE];

        try
        {
            foreach (MediaFile file in missingFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (pauseToken.IsPaused)
                {
                    progress?.Report(new CopyProgressInfo(
                        CurrentFileName: file.RelativePath,
                        TotalBytes: totalBytes,
                        CopiedBytes: totalCopiedBytes,
                        MegaBytesPerSecond: 0,
                        EstimatedTimeRemaining: TimeSpan.Zero,
                        FilesCompleted: completedFiles,
                        TotalFiles: missingFiles.Count,
                        CurrentFileBytes: file.FileLength,
                        CurrentFileCopiedBytes: 0,
                        IsPaused: true,
                        Stages: BuildCopyStages(
                            totalBytes,
                            totalCopiedBytes,
                            missingFiles.Count,
                            completedFiles,
                            0.0,
                            file.RelativePath,
                            file.FileLength,
                            0,
                            activeDestPaths,
                            isCompleted: false),
                        SourceTelemetry: BuildSourceCopyTelemetry(
                            file.RelativePath,
                            file.FileLength,
                            0,
                            totalBytes,
                            totalCopiedBytes,
                            missingFiles.Count,
                            completedFiles,
                            0.0,
                            isCompleted: false,
                            isPaused: true),
                        DestinationTelemetries: BuildDestinationCopyTelemetries(
                            activeDestPaths,
                            totalBytes,
                            totalCopiedBytes,
                            missingFiles.Count,
                            completedFiles,
                            0.0,
                            isCompleted: false,
                            isPaused: true),
                        RenamedCopyCount: renamedCopyCount));

                    overallStopwatch.Stop();
                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    overallStopwatch.Start();
                }

                string canonicalSource = Path.GetFullPath(file.FullPath);
                foreach (string destRoot in activeDestPaths)
                {
                    string canonicalDest = Path.GetFullPath(Path.Combine(destRoot, file.RelativePath));
                    if (string.Equals(canonicalSource, canonicalDest, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Safety violation: Source and destination file path are identical: '{file.FullPath}'");
                    }
                }

                // A failure here stays with this file and destination; the rest of the batch keeps copying.
                int failuresBeforeFile = failures.Count;
                Dictionary<string, Exception> destinationErrors = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> settledDestRoots = new(StringComparer.OrdinalIgnoreCase);
                List<PendingDestinationWrite> writes = new(activeDestPaths.Count);
                bool readingSource = false;
                Exception? sourceError = null;

                ulong srcHead = file.HeadHash ?? 0;
                ulong srcTail = file.TailHash ?? 0;
                bool srcHashesKnown = srcHead != 0 || srcTail != 0;

                try
                {
                    foreach (string destRoot in activeDestPaths)
                    {
                        // Checked before any probe, so an unplugged drive or dropped share is not probed or logged
                        // again for every remaining file.
                        if (unavailableDestRoots.Contains(destRoot))
                        {
                            failures.Add(new CopyFailure(
                                file.RelativePath,
                                file.FullPath,
                                destRoot,
                                DESTINATION_UNAVAILABLE_MESSAGE));
                            settledDestRoots.Add(destRoot);
                            continue;
                        }

                        try
                        {
                            string path = Path.Combine(destRoot, file.RelativePath);
                            FileInfo existingInfo = new(path);
                            if (existingInfo.Exists && existingInfo.Length == file.FileLength)
                            {
                                if (!srcHashesKnown)
                                {
                                    readingSource = true;
                                    (srcHead, srcTail) = ChunkReader.ComputeHeadTailHash(file.FullPath);
                                    readingSource = false;
                                    srcHashesKnown = true;
                                }

                                (ulong dstHead, ulong dstTail) = ChunkReader.ComputeHeadTailHash(path);
                                if (dstHead == srcHead && dstTail == srcTail)
                                {
                                    // Destination already has an intact, verified copy of this file.
                                    settledDestRoots.Add(destRoot);
                                    continue;
                                }
                            }

                            // Siblings are only looked up once the path holds a different file or a folder.
                            bool isPathOccupied = existingInfo.Exists || Directory.Exists(path);
                            if (isPathOccupied)
                            {
                                List<string> sameLengthSiblings = siblingIndex.FindSiblingsWithLength(
                                    path,
                                    file.FileLength);
                                if (sameLengthSiblings.Count > 0)
                                {
                                    if (!srcHashesKnown)
                                    {
                                        readingSource = true;
                                        (srcHead, srcTail) = ChunkReader.ComputeHeadTailHash(file.FullPath);
                                        readingSource = false;
                                        srcHashesKnown = true;
                                    }

                                    if (ContainsHeadTailMatch(sameLengthSiblings, srcHead, srcTail))
                                    {
                                        // An earlier renamed copy of this file is already intact on this destination.
                                        settledDestRoots.Add(destRoot);
                                        continue;
                                    }
                                }
                            }

                            // After a disk-full error only files that no longer fit are left out, so one large
                            // clip (or a >4 GB file on FAT32) does not stop the smaller photos after it.
                            if (fullDestRoots.Contains(destRoot) && !HasFreeSpaceFor(destRoot, file.FileLength))
                            {
                                failures.Add(new CopyFailure(
                                    file.RelativePath,
                                    file.FullPath,
                                    destRoot,
                                    DESTINATION_FULL_MESSAGE));
                                settledDestRoots.Add(destRoot);
                                continue;
                            }

                            // Camera file names repeat across bodies and counter resets, so an occupied path is
                            // never replaced: the copy moves to the next free sibling name (IMG_0001_1.CR3, ...).
                            string targetPath = path;
                            if (isPathOccupied)
                            {
                                targetPath = FindUnoccupiedSiblingPath(path);
                                AppLogger.Logger.Warning(
                                    "Destination {DestinationPath} already holds a different file or folder; " +
                                    "writing {SourcePath} as {ResolvedPath}",
                                    path,
                                    file.FullPath,
                                    targetPath);
                            }

                            string? dir = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }

                            writes.Add(new PendingDestinationWrite(
                                destRoot,
                                path,
                                targetPath,
                                targetPath + TEMP_FILE_EXTENSION));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && !readingSource)
                        {
                            // A source read error is not this destination's fault; the outer handler fails the
                            // file on every destination instead.
                            destinationErrors.TryAdd(destRoot, ex);
                        }
                    }

                    if (writes.Count > 0)
                    {
                        long fileCopiedBytes = 0;
                        ulong inFlightHeadHash = 0;
                        ulong inFlightTailHash = 0;
                        bool headHashCaptured = false;
                        int tailBufferCount = 0;
                        int bytesSinceEviction = 0;

                        await using (FileStream sourceStream = new(
                            file.FullPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 0,
                            FileOptions.SequentialScan | FileOptions.Asynchronous))
                        {
                            try
                            {
                                int activeWriteCount = 0;
                                foreach (PendingDestinationWrite write in writes)
                                {
                                    try
                                    {
                                        write.OpenTempStream();
                                        activeWriteCount++;
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException)
                                    {
                                        await FailWriteAsync(write, ex, destinationErrors).ConfigureAwait(false);
                                    }
                                }

                                List<Task> writeTasks = new(writes.Count);
                                int bytesRead;
                                while (activeWriteCount > 0 && (bytesRead = await sourceStream.ReadAsync(
                                    rentedBuffer.AsMemory(0, COPY_BUFFER_SIZE),
                                    cancellationToken).ConfigureAwait(false)) > 0)
                                {
                                    // 1. In-flight head checksum capture
                                    if (!headHashCaptured)
                                    {
                                        int headLen = Math.Min(HEAD_TAIL_CHUNK_SIZE, bytesRead);
                                        inFlightHeadHash = SimdHasher.Hash64(rentedBuffer.AsSpan(0, headLen));
                                        headHashCaptured = true;
                                    }

                                    // 2. In-flight tail buffer tracking
                                    if (bytesRead >= HEAD_TAIL_CHUNK_SIZE)
                                    {
                                        rentedBuffer.AsSpan(bytesRead - HEAD_TAIL_CHUNK_SIZE, HEAD_TAIL_CHUNK_SIZE)
                                            .CopyTo(tailBuffer);
                                        tailBufferCount = HEAD_TAIL_CHUNK_SIZE;
                                    }
                                    else if (tailBufferCount + bytesRead <= HEAD_TAIL_CHUNK_SIZE)
                                    {
                                        rentedBuffer.AsSpan(0, bytesRead)
                                            .CopyTo(tailBuffer.AsSpan(tailBufferCount));
                                        tailBufferCount += bytesRead;
                                    }
                                    else
                                    {
                                        int overflow = (tailBufferCount + bytesRead) - HEAD_TAIL_CHUNK_SIZE;
                                        tailBuffer.AsSpan(overflow, tailBufferCount - overflow)
                                            .CopyTo(tailBuffer.AsSpan(0));
                                        rentedBuffer.AsSpan(0, bytesRead)
                                            .CopyTo(tailBuffer.AsSpan(HEAD_TAIL_CHUNK_SIZE - bytesRead));
                                        tailBufferCount = HEAD_TAIL_CHUNK_SIZE;
                                    }

                                    // 3. Fan-out write to all destinations concurrently. WhenAll waits for every
                                    // write before the shared buffer is reused; a failed destination drops out alone.
                                    writeTasks.Clear();
                                    foreach (PendingDestinationWrite write in writes)
                                    {
                                        if (write.IsWriting)
                                        {
                                            writeTasks.Add(write.WriteChunkAsync(
                                                rentedBuffer.AsMemory(0, bytesRead),
                                                cancellationToken));
                                        }
                                    }

                                    await Task.WhenAll(writeTasks).ConfigureAwait(false);

                                    foreach (PendingDestinationWrite write in writes)
                                    {
                                        if (write.IsWriting && write.WriteError is not null)
                                        {
                                            await FailWriteAsync(write, write.WriteError, destinationErrors)
                                                .ConfigureAwait(false);
                                            activeWriteCount--;
                                        }
                                    }

                                    bytesSinceEviction += bytesRead;
                                    if (bytesSinceEviction >= PAGE_CACHE_EVICTION_INTERVAL_BYTES)
                                    {
                                        long evictOffset = fileCopiedBytes - bytesSinceEviction;
                                        foreach (PendingDestinationWrite write in writes)
                                        {
                                            if (write.IsWriting)
                                            {
                                                NativeDirectIO.EvictPageCache(
                                                    write.Stream!.SafeFileHandle,
                                                    evictOffset,
                                                    bytesSinceEviction);
                                            }
                                        }
                                        bytesSinceEviction = 0;
                                    }

                                    totalCopiedBytes += bytesRead;
                                    fileCopiedBytes += bytesRead;

                                    double elapsedSeconds = overallStopwatch.Elapsed.TotalSeconds;
                                    double mbPerSec = elapsedSeconds > 0
                                        ? (totalCopiedBytes / (1024.0 * 1024.0)) / elapsedSeconds
                                        : 0.0;

                                    long remainingBytes = Math.Max(0, totalBytes - totalCopiedBytes);
                                    double remainingSeconds = mbPerSec > 0
                                        ? (remainingBytes / (1024.0 * 1024.0)) / mbPerSec
                                        : 0.0;

                                    progress?.Report(new CopyProgressInfo(
                                        CurrentFileName: file.RelativePath,
                                        TotalBytes: totalBytes,
                                        CopiedBytes: totalCopiedBytes,
                                        MegaBytesPerSecond: Math.Round(mbPerSec, 2),
                                        EstimatedTimeRemaining: TimeSpan.FromSeconds(remainingSeconds),
                                        FilesCompleted: completedFiles,
                                        TotalFiles: missingFiles.Count,
                                        CurrentFileBytes: file.FileLength,
                                        CurrentFileCopiedBytes: fileCopiedBytes,
                                        IsPaused: false,
                                        Stages: BuildCopyStages(
                                            totalBytes,
                                            totalCopiedBytes,
                                            missingFiles.Count,
                                            completedFiles,
                                            mbPerSec,
                                            file.RelativePath,
                                            file.FileLength,
                                            fileCopiedBytes,
                                            activeDestPaths,
                                            isCompleted: false),
                                        SourceTelemetry: BuildSourceCopyTelemetry(
                                            file.RelativePath,
                                            file.FileLength,
                                            fileCopiedBytes,
                                            totalBytes,
                                            totalCopiedBytes,
                                            missingFiles.Count,
                                            completedFiles,
                                            mbPerSec,
                                            isCompleted: false,
                                            isPaused: false),
                                        DestinationTelemetries: BuildDestinationCopyTelemetries(
                                            activeDestPaths,
                                            totalBytes,
                                            totalCopiedBytes,
                                            missingFiles.Count,
                                            completedFiles,
                                            mbPerSec,
                                            isCompleted: false,
                                            isPaused: false),
                                        RenamedCopyCount: renamedCopyCount));
                                }

                                // Compute final in-flight tail hash without touching the source disk again
                                inFlightTailHash = file.FileLength <= HEAD_TAIL_CHUNK_SIZE
                                    ? inFlightHeadHash
                                    : SimdHasher.Hash64(tailBuffer.AsSpan(0, tailBufferCount));

                                // Ensure all buffers are committed
                                foreach (PendingDestinationWrite write in writes)
                                {
                                    if (!write.IsWriting)
                                    {
                                        continue;
                                    }

                                    try
                                    {
                                        await write.Stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
                                        NativeDirectIO.EvictPageCache(write.Stream.SafeFileHandle, 0, 0);
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException)
                                    {
                                        await FailWriteAsync(write, ex, destinationErrors).ConfigureAwait(false);
                                    }
                                }
                            }
                            finally
                            {
                                foreach (PendingDestinationWrite write in writes)
                                {
                                    try
                                    {
                                        await write.CloseStreamAsync().ConfigureAwait(false);
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException)
                                    {
                                        await FailWriteAsync(write, ex, destinationErrors).ConfigureAwait(false);
                                    }
                                }
                            }
                        }

                        // Zero-pass source verification: verify destination files against in-flight computed hashes
                        foreach (PendingDestinationWrite write in writes)
                        {
                            if (write.IsFailed)
                            {
                                continue;
                            }

                            try
                            {
                                FileInfo tempInfo = new(write.TempPath);
                                if (tempInfo.Length != file.FileLength)
                                {
                                    throw new IOException(
                                        $"Post-copy size verification failed for '{file.RelativePath}' at '{write.TargetPath}'. Expected {file.FileLength} bytes, wrote {tempInfo.Length} bytes.");
                                }

                                (ulong dstHead, ulong dstTail) = ChunkReader.ComputeHeadTailHash(write.TempPath);
                                if (dstHead != inFlightHeadHash || dstTail != inFlightTailHash)
                                {
                                    throw new IOException(
                                        $"Post-copy hash verification failed for '{file.RelativePath}' at '{write.TargetPath}'. Destination checksum mismatch after transfer.");
                                }

                                string committedPath = CommitWithoutOverwrite(
                                    write.TempPath,
                                    write.TargetPath,
                                    write.RequestedPath,
                                    file.FullPath);
                                write.MarkCommitted();
                                settledDestRoots.Add(write.DestinationRootPath);
                                siblingIndex.RecordWrittenFile(committedPath);
                                if (!string.Equals(committedPath, write.RequestedPath, StringComparison.Ordinal))
                                {
                                    renamedCopyCount++;
                                }

                                try
                                {
                                    if (file.LastWriteTimeUtc != default)
                                    {
                                        File.SetLastWriteTimeUtc(committedPath, file.LastWriteTimeUtc);
                                    }

                                    if (File.Exists(file.FullPath))
                                    {
                                        DateTime creationTimeUtc = File.GetCreationTimeUtc(file.FullPath);
                                        if (creationTimeUtc != default)
                                        {
                                            File.SetCreationTimeUtc(committedPath, creationTimeUtc);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AppLogger.Logger.Warning(
                                        ex,
                                        "Failed to preserve timestamps for destination file {DestinationPath}",
                                        committedPath);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                await FailWriteAsync(write, ex, destinationErrors).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Per-destination errors are caught above, so this is a source-side failure (open or read).
                    sourceError = ex;
                }
                finally
                {
                    // Clean up partial temporary files only. Never touch pre-existing files.
                    foreach (PendingDestinationWrite write in writes)
                    {
                        if (!write.IsCommitted)
                        {
                            write.DeleteTempFile();
                        }
                    }
                }

                foreach (KeyValuePair<string, Exception> destinationError in destinationErrors)
                {
                    RecordDestinationFailure(
                        file,
                        destinationError.Key,
                        destinationError.Value,
                        failures,
                        fullDestRoots,
                        unavailableDestRoots);
                }

                if (sourceError is not null)
                {
                    RecordSourceFailure(
                        file,
                        sourceError,
                        activeDestPaths,
                        settledDestRoots,
                        destinationErrors,
                        failures);
                }

                if (failures.Count > failuresBeforeFile)
                {
                    failedFiles++;
                }
                else
                {
                    completedFiles++;
                    if (writes.Count > 0)
                    {
                        copiedFiles++;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }

        progress?.Report(new CopyProgressInfo(
            CurrentFileName: string.Empty,
            TotalBytes: totalBytes,
            CopiedBytes: totalCopiedBytes,
            MegaBytesPerSecond: 0,
            EstimatedTimeRemaining: TimeSpan.Zero,
            FilesCompleted: completedFiles,
            TotalFiles: missingFiles.Count,
            CurrentFileBytes: 0,
            CurrentFileCopiedBytes: 0,
            IsPaused: false,
            Stages: BuildCopyStages(
                totalBytes,
                totalCopiedBytes,
                missingFiles.Count,
                completedFiles,
                0.0,
                null,
                0,
                0,
                activeDestPaths,
                isCompleted: true),
            SourceTelemetry: BuildSourceCopyTelemetry(
                string.Empty,
                0,
                0,
                totalBytes,
                totalCopiedBytes,
                missingFiles.Count,
                completedFiles,
                0.0,
                isCompleted: true,
                isPaused: false),
            DestinationTelemetries: BuildDestinationCopyTelemetries(
                activeDestPaths,
                totalBytes,
                totalCopiedBytes,
                missingFiles.Count,
                completedFiles,
                0.0,
                isCompleted: true,
                isPaused: false),
            RenamedCopyCount: renamedCopyCount));

        if (renamedCopyCount > 0)
        {
            AppLogger.Logger.Warning(
                "Copy saved {RenamedCopyCount} copies under a new name because a different file or folder " +
                "already used the requested name",
                renamedCopyCount);
        }

        return new CopyBatchResult(
            CopiedCount: copiedFiles,
            SkippedCount: completedFiles - copiedFiles,
            FailedFileCount: failedFiles,
            RenamedCopyCount: renamedCopyCount,
            Failures: failures);
    }

    private static async Task FailWriteAsync(
        PendingDestinationWrite write,
        Exception error,
        Dictionary<string, Exception> destinationErrors)
    {
        await write.AbandonAsync().ConfigureAwait(false);
        destinationErrors.TryAdd(write.DestinationRootPath, error);
    }

    private static void RecordDestinationFailure(
        MediaFile file,
        string destinationRootPath,
        Exception error,
        List<CopyFailure> failures,
        HashSet<string> fullDestRoots,
        HashSet<string> unavailableDestRoots)
    {
        AppLogger.Logger.Error(
            error,
            "Failed to copy {RelativePath} to {DestinationRoot}",
            file.RelativePath,
            destinationRootPath);
        failures.Add(new CopyFailure(file.RelativePath, file.FullPath, destinationRootPath, error.Message));

        if (!Directory.Exists(destinationRootPath))
        {
            if (unavailableDestRoots.Add(destinationRootPath))
            {
                AppLogger.Logger.Warning(
                    "Destination {DestinationRoot} cannot be reached; no further files in this batch will be " +
                    "written to it",
                    destinationRootPath);
            }
        }
        else if (IsDiskFull(error) && fullDestRoots.Add(destinationRootPath))
        {
            AppLogger.Logger.Warning(
                "Destination {DestinationRoot} is full; later files in this batch are only written to it " +
                "when they fit in its remaining free space",
                destinationRootPath);
        }
    }

    // A source error means the file is missed by every destination that was still waiting on it. It is logged
    // once, not once per destination.
    private static void RecordSourceFailure(
        MediaFile file,
        Exception error,
        IReadOnlyList<string> activeDestPaths,
        HashSet<string> settledDestRoots,
        Dictionary<string, Exception> destinationErrors,
        List<CopyFailure> failures)
    {
        AppLogger.Logger.Error(
            error,
            "Failed to read source file {SourcePath}; {RelativePath} was not copied",
            file.FullPath,
            file.RelativePath);

        foreach (string destRoot in activeDestPaths)
        {
            if (!settledDestRoots.Contains(destRoot) && !destinationErrors.ContainsKey(destRoot))
            {
                failures.Add(new CopyFailure(file.RelativePath, file.FullPath, destRoot, error.Message));
            }
        }
    }

    private static bool HasFreeSpaceFor(string destinationRootPath, long fileLength)
    {
        try
        {
            return new DriveInfo(destinationRootPath).AvailableFreeSpace > fileLength;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Free space is unknown (e.g. a UNC share on Windows), so a destination that already reported
            // disk full stays left out.
            return false;
        }
    }

    private static bool IsDiskFull(Exception error)
    {
        if (error is not IOException)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            int win32Error = error.HResult & WIN32_ERROR_CODE_MASK;
            return win32Error is WIN32_ERROR_DISK_FULL or WIN32_ERROR_HANDLE_DISK_FULL;
        }

        // On Unix, .NET stores the raw errno in HResult.
        return error.HResult == UNIX_ENOSPC;
    }

    private static string CommitWithoutOverwrite(
        string tempPath,
        string targetPath,
        string requestedPath,
        string sourcePath)
    {
        string candidatePath = targetPath;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, candidatePath, overwrite: false);
                return candidatePath;
            }
            catch (IOException ex) when (File.Exists(tempPath) && IsPathOccupied(candidatePath))
            {
                // A small cap stops a misbehaving filesystem (e.g. link succeeded but unlinking the temp
                // file failed) from spraying sibling names across the destination.
                if (attempt >= MAX_COMMIT_ATTEMPTS)
                {
                    throw new IOException(
                        $"Could not commit '{sourcePath}' next to '{requestedPath}': the chosen name was taken " +
                        $"during the copy on each of {MAX_COMMIT_ATTEMPTS} attempts.",
                        ex);
                }

                // Another writer claimed the name after it was resolved; keep their file and take the next free name.
                string nextPath = FindUnoccupiedSiblingPath(requestedPath);
                AppLogger.Logger.Warning(
                    ex,
                    "Destination {DestinationPath} appeared during the copy; writing {SourcePath} as {ResolvedPath}",
                    candidatePath,
                    sourcePath,
                    nextPath);
                candidatePath = nextPath;
            }
        }
    }

    private static bool ContainsHeadTailMatch(List<string> candidatePaths, ulong expectedHead, ulong expectedTail)
    {
        foreach (string candidatePath in candidatePaths)
        {
            (ulong head, ulong tail) = ChunkReader.ComputeHeadTailHash(candidatePath);
            if (head == expectedHead && tail == expectedTail)
            {
                return true;
            }
        }

        return false;
    }

    private static string FindUnoccupiedSiblingPath(string requestedPath)
    {
        int suffix = 1;
        string candidatePath = BuildSiblingPath(requestedPath, suffix);
        while (IsPathOccupied(candidatePath))
        {
            suffix++;
            candidatePath = BuildSiblingPath(requestedPath, suffix);
        }

        return candidatePath;
    }

    private static string BuildSiblingPath(string requestedPath, int suffix)
    {
        if (suffix > MAX_COLLISION_SUFFIX)
        {
            throw new IOException(
                $"No free file name found next to '{requestedPath}' after {MAX_COLLISION_SUFFIX} attempts.");
        }

        string directory = Path.GetDirectoryName(requestedPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(requestedPath);
        string extension = Path.GetExtension(requestedPath);
        return Path.Combine(directory, $"{stem}_{suffix}{extension}");
    }

    private static bool IsPathOccupied(string path) => File.Exists(path) || Directory.Exists(path);

    private static IReadOnlyList<StageProgressInfo> BuildCopyStages(
        long totalBytes,
        long copiedBytes,
        int totalFiles,
        int completedFiles,
        double mbPerSec,
        string? activeFileName,
        long activeFileBytes,
        long activeFileCopiedBytes,
        IReadOnlyList<string> activeDestPaths,
        bool isCompleted = false)
    {
        double bytePct = isCompleted
            ? 100.0
            : (totalBytes > 0 ? Math.Min(100.0, (copiedBytes / (double)totalBytes) * 100.0) : 0.0);

        double verifyPct = isCompleted
            ? 100.0
            : (totalFiles > 0 ? Math.Min(100.0, (completedFiles / (double)totalFiles) * 100.0) : 0.0);

        List<DestinationProgressInfo> destMeters = new(activeDestPaths.Count);
        foreach (string destRoot in activeDestPaths)
        {
            string destName = Path.GetFileName(destRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(destName))
            {
                destName = destRoot;
            }

            destMeters.Add(new DestinationProgressInfo(
                DestinationId: destRoot,
                DestinationName: destName,
                ProcessedBytes: copiedBytes,
                TotalBytes: totalBytes,
                Percentage: bytePct,
                MegaBytesPerSecond: isCompleted ? 0.0 : mbPerSec,
                StatusText: isCompleted ? "Complete" : $"{mbPerSec:F1} MB/s ({ByteSizeFormatter.Format(copiedBytes)} written)"));
        }

        StageStatus writeStatus = isCompleted || (copiedBytes >= totalBytes && totalBytes > 0)
            ? StageStatus.Completed
            : StageStatus.Running;

        StageStatus verifyStatus = isCompleted || (completedFiles >= totalFiles && totalFiles > 0)
            ? StageStatus.Completed
            : (completedFiles > 0 ? StageStatus.Running : StageStatus.Pending);

        double activeFilePct = activeFileBytes > 0
            ? Math.Min(100.0, (activeFileCopiedBytes / (double)activeFileBytes) * 100.0)
            : 0.0;

        return new StageProgressInfo[]
        {
            new(
                Id: PipelineStageId.TransferWrite,
                Title: "Streaming File Transfer",
                Status: writeStatus,
                Percentage: bytePct,
                ProcessedUnits: copiedBytes,
                TotalUnits: totalBytes,
                ProgressText: isCompleted
                    ? $"{ByteSizeFormatter.Format(totalBytes)} ({totalFiles:N0} files)"
                    : $"{ByteSizeFormatter.Format(copiedBytes)} / {ByteSizeFormatter.Format(totalBytes)} ({completedFiles:N0} / {totalFiles:N0} files)",
                TelemetryText: isCompleted ? "Complete" : $"{mbPerSec:F1} MB/s",
                ActiveFileName: isCompleted ? null : activeFileName,
                ActiveFilePercentage: isCompleted ? 0.0 : activeFilePct,
                ActiveFileProgressText: isCompleted ? null : $"{ByteSizeFormatter.Format(activeFileCopiedBytes)} / {ByteSizeFormatter.Format(activeFileBytes)}",
                DestinationMeters: destMeters),
            new(
                Id: PipelineStageId.PostTransferVerify,
                Title: "Checksum Verification",
                Status: verifyStatus,
                Percentage: verifyPct,
                ProcessedUnits: completedFiles,
                TotalUnits: totalFiles,
                ProgressText: $"{completedFiles:N0} / {totalFiles:N0} files verified",
                TelemetryText: isCompleted ? "Complete" : (completedFiles > 0 ? "Verifying..." : "Pending"))
        };
    }

    private static SourceTelemetryInfo BuildSourceCopyTelemetry(
        string currentFile,
        long currentFileLength,
        long currentFileCopied,
        long totalBytes,
        long totalCopiedBytes,
        int totalFiles,
        int completedFiles,
        double mbPerSec,
        bool isCompleted,
        bool isPaused)
    {
        double percentage = isCompleted
            ? 100.0
            : (totalBytes > 0 ? Math.Min(100.0, (totalCopiedBytes / (double)totalBytes) * 100.0) : 0.0);

        double filePercentage = currentFileLength > 0
            ? Math.Min(100.0, (currentFileCopied / (double)currentFileLength) * 100.0)
            : 0.0;

        string hashStatus = isPaused
            ? "PAUSED"
            : (isCompleted ? "COMPLETE" : "READING");

        return new SourceTelemetryInfo(
            Path: "Source Media",
            ScanStatus: "COMPLETE",
            ScanFilesCount: totalFiles,
            ScanBytesCount: totalBytes,
            ScanSpeed: 0.0,
            IndexStatus: "INDEXED",
            HashStatus: hashStatus,
            Percentage: percentage,
            ProcessedBytes: isCompleted ? totalBytes : totalCopiedBytes,
            TotalBytes: totalBytes,
            SpeedMbPerSec: isCompleted || isPaused ? 0.0 : mbPerSec,
            CurrentFile: isCompleted ? null : currentFile,
            CurrentFilePercentage: isCompleted ? 0.0 : filePercentage,
            CurrentFileProgressText: isCompleted ? null : $"{ByteSizeFormatter.Format(currentFileCopied)} / {ByteSizeFormatter.Format(currentFileLength)}");
    }

    private static IReadOnlyList<DestinationTelemetryInfo> BuildDestinationCopyTelemetries(
        IReadOnlyList<string> activeDestPaths,
        long totalBytes,
        long totalCopiedBytes,
        int totalFiles,
        int completedFiles,
        double mbPerSec,
        bool isCompleted,
        bool isPaused)
    {
        double percentage = isCompleted
            ? 100.0
            : (totalBytes > 0 ? Math.Min(100.0, (totalCopiedBytes / (double)totalBytes) * 100.0) : 0.0);

        string verifyStatus = isPaused
            ? "PAUSED"
            : (isCompleted ? "COMPLETE" : "WRITING");

        List<DestinationTelemetryInfo> list = new(activeDestPaths.Count);
        foreach (string targetPath in activeDestPaths)
        {
            string name = Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = targetPath;
            }

            string statusText = isCompleted
                ? $"Verified {completedFiles}/{totalFiles} written"
                : (isPaused ? "Paused" : $"{mbPerSec:F1} MB/s ({completedFiles}/{totalFiles} copied)");

            list.Add(new DestinationTelemetryInfo(
                DestinationId: targetPath,
                DestinationName: name,
                RootPath: targetPath,
                ScanStatus: "COMPLETE",
                ScanFilesCount: totalFiles,
                ScanBytesCount: totalBytes,
                ScanSpeed: 0.0,
                IndexStatus: "INDEXED",
                VerifyStatus: verifyStatus,
                Percentage: percentage,
                VerifiedFiles: completedFiles,
                TotalFiles: totalFiles,
                BytesRead: isCompleted ? totalBytes : totalCopiedBytes,
                SpeedMbPerSec: isCompleted || isPaused ? 0.0 : mbPerSec,
                StatusText: statusText));
        }

        return list;
    }
}
