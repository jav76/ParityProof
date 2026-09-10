using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Logging;
using ParityProof.Core.Models;
using ParityProof.Core.Threading;
using ParityProof.Engine.Hashing;
using ParityProof.Engine.IO;

namespace ParityProof.Engine.Transfer;

[LogMethod]
public sealed class MediaCopier : IMediaCopier
{
    private const int COPY_BUFFER_SIZE = 2 * 1024 * 1024; // 2 MB streaming buffer
    private const int HEAD_TAIL_CHUNK_SIZE = 64 * 1024; // 64 KB head/tail boundary chunk
    private const string TEMP_FILE_EXTENSION = ".parityproof.tmp";

    public Task<int> CopyMissingFilesAsync(
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

    public async Task<int> CopyMissingFilesAsync(
        IReadOnlyList<MediaFile> missingFiles,
        IReadOnlyList<string> destinationRootPaths,
        IProgress<CopyProgressInfo>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default)
    {
        if (missingFiles.Count == 0 || destinationRootPaths.Count == 0)
        {
            return 0;
        }

        List<string> activeDestPaths = destinationRootPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (activeDestPaths.Count == 0)
        {
            return 0;
        }

        long totalBytes = missingFiles.Sum(f => f.FileLength);
        long totalCopiedBytes = 0;
        int completedFiles = 0;
        Stopwatch overallStopwatch = Stopwatch.StartNew();

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
                        IsPaused: true));

                    overallStopwatch.Stop();
                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    overallStopwatch.Start();
                }

                List<string> destPathsToWrite = new(activeDestPaths.Count);
                List<string> tempPathsToClean = new(activeDestPaths.Count);

                ulong srcHead = file.HeadHash ?? 0;
                ulong srcTail = file.TailHash ?? 0;
                bool srcHashesKnown = srcHead != 0 || srcTail != 0;

                foreach (string destRoot in activeDestPaths)
                {
                    string path = Path.Combine(destRoot, file.RelativePath);
                    string canonicalSource = Path.GetFullPath(file.FullPath);
                    string canonicalDest = Path.GetFullPath(path);
                    if (string.Equals(canonicalSource, canonicalDest, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Safety violation: Source and destination file path are identical: '{file.FullPath}'");
                    }

                    FileInfo existingInfo = new(path);
                    if (existingInfo.Exists && existingInfo.Length == file.FileLength)
                    {
                        if (!srcHashesKnown)
                        {
                            (srcHead, srcTail) = ChunkReader.ComputeHeadTailHash(file.FullPath);
                            srcHashesKnown = true;
                        }

                        (ulong dstHead, ulong dstTail) = ChunkReader.ComputeHeadTailHash(path);
                        if (dstHead == srcHead && dstTail == srcTail)
                        {
                            // Destination already has an intact, verified copy of this file.
                            continue;
                        }
                    }

                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    destPathsToWrite.Add(path);
                    tempPathsToClean.Add(path + TEMP_FILE_EXTENSION);
                }

                if (destPathsToWrite.Count == 0)
                {
                    completedFiles++;
                    continue;
                }

                long fileCopiedBytes = 0;
                ulong inFlightHeadHash = 0;
                ulong inFlightTailHash = 0;
                bool headHashCaptured = false;
                int tailBufferCount = 0;

                try
                {
                    await using (FileStream sourceStream = new(
                        file.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 0,
                        FileOptions.SequentialScan | FileOptions.Asynchronous))
                    {
                        List<FileStream> destStreams = new(destPathsToWrite.Count);
                        try
                        {
                            foreach (string tempPath in tempPathsToClean)
                            {
                                destStreams.Add(new FileStream(
                                    tempPath,
                                    FileMode.Create,
                                    FileAccess.Write,
                                    FileShare.None,
                                    bufferSize: 0,
                                    FileOptions.SequentialScan | FileOptions.Asynchronous));
                            }

                            int bytesRead;
                            while ((bytesRead = await sourceStream.ReadAsync(
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

                                // 3. Fan-out write to all destinations concurrently
                                if (destStreams.Count == 1)
                                {
                                    await destStreams[0].WriteAsync(
                                        rentedBuffer.AsMemory(0, bytesRead),
                                        cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    Task[] writeTasks = new Task[destStreams.Count];
                                    for (int i = 0; i < destStreams.Count; i++)
                                    {
                                        writeTasks[i] = destStreams[i].WriteAsync(
                                            rentedBuffer.AsMemory(0, bytesRead),
                                            cancellationToken).AsTask();
                                    }
                                    await Task.WhenAll(writeTasks).ConfigureAwait(false);
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
                                    IsPaused: false));
                            }

                            // Compute final in-flight tail hash without touching the source disk again
                            inFlightTailHash = file.FileLength <= HEAD_TAIL_CHUNK_SIZE
                                ? inFlightHeadHash
                                : SimdHasher.Hash64(tailBuffer.AsSpan(0, tailBufferCount));

                            // Ensure all buffers are committed
                            foreach (FileStream destStream in destStreams)
                            {
                                await destStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            foreach (FileStream destStream in destStreams)
                            {
                                await destStream.DisposeAsync().ConfigureAwait(false);
                            }
                        }
                    }

                    // Zero-pass source verification: verify destination files against in-flight computed hashes
                    for (int i = 0; i < destPathsToWrite.Count; i++)
                    {
                        string tempPath = tempPathsToClean[i];
                        string destPath = destPathsToWrite[i];

                        FileInfo tempInfo = new(tempPath);
                        if (tempInfo.Length != file.FileLength)
                        {
                            throw new IOException(
                                $"Post-copy size verification failed for '{file.RelativePath}' at '{destPath}'. Expected {file.FileLength} bytes, wrote {tempInfo.Length} bytes.");
                        }

                        (ulong dstHead, ulong dstTail) = ChunkReader.ComputeHeadTailHash(tempPath);
                        if (dstHead != inFlightHeadHash || dstTail != inFlightTailHash)
                        {
                            throw new IOException(
                                $"Post-copy hash verification failed for '{file.RelativePath}' at '{destPath}'. Destination checksum mismatch after transfer.");
                        }

                        File.Move(tempPath, destPath, overwrite: true);

                        try
                        {
                            if (file.LastWriteTimeUtc != default)
                            {
                                File.SetLastWriteTimeUtc(destPath, file.LastWriteTimeUtc);
                            }

                            if (File.Exists(file.FullPath))
                            {
                                DateTime creationTimeUtc = File.GetCreationTimeUtc(file.FullPath);
                                if (creationTimeUtc != default)
                                {
                                    File.SetCreationTimeUtc(destPath, creationTimeUtc);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Logger.Warning(
                                ex,
                                "Failed to preserve timestamps for destination file {DestinationPath}",
                                destPath);
                        }
                    }

                    completedFiles++;
                }
                catch (Exception)
                {
                    // Clean up partial temporary files only. Never touch pre-existing files.
                    foreach (string tempPath in tempPathsToClean)
                    {
                        if (File.Exists(tempPath))
                        {
                            try
                            {
                                File.Delete(tempPath);
                            }
                            catch
                            {
                                // Suppress cleanup exception to preserve original error
                            }
                        }
                    }
                    throw;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }

        return completedFiles;
    }
}
