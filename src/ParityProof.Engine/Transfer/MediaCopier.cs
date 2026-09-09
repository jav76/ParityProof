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
using ParityProof.Engine.IO;

namespace ParityProof.Engine.Transfer;

[LogMethod]
public sealed class MediaCopier : IMediaCopier
{
    private const int COPY_BUFFER_SIZE = 2 * 1024 * 1024; // 2 MB streaming buffer

    public async Task<int> CopyMissingFilesAsync(
        IReadOnlyList<MediaFile> missingFiles,
        string destinationRootPath,
        IProgress<CopyProgressInfo>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default)
    {
        if (missingFiles.Count == 0)
        {
            return 0;
        }

        long totalBytes = missingFiles.Sum(f => f.FileLength);
        long totalCopiedBytes = 0;
        int completedFiles = 0;
        Stopwatch overallStopwatch = Stopwatch.StartNew();

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(COPY_BUFFER_SIZE);
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

                string destinationFilePath = Path.Combine(destinationRootPath, file.RelativePath);
                string? destDir = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                long fileCopiedBytes = 0;
                try
                {
                    await using (FileStream sourceStream = new(
                        file.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 0,
                        FileOptions.SequentialScan | FileOptions.Asynchronous))
                    await using (FileStream destStream = new(
                        destinationFilePath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 0,
                        FileOptions.SequentialScan | FileOptions.Asynchronous))
                    {
                        int bytesRead;
                        while ((bytesRead = await sourceStream.ReadAsync(
                            rentedBuffer.AsMemory(0, COPY_BUFFER_SIZE),
                            cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await destStream.WriteAsync(
                                rentedBuffer.AsMemory(0, bytesRead),
                                cancellationToken).ConfigureAwait(false);

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
                    }

                    FileInfo destInfo = new(destinationFilePath);
                    if (destInfo.Length != file.FileLength)
                    {
                        File.Delete(destinationFilePath);
                        throw new IOException(
                            $"Post-copy size verification failed for '{file.RelativePath}'. Expected {file.FileLength} bytes, wrote {destInfo.Length} bytes.");
                    }

                    (ulong srcHead, ulong srcTail) = ChunkReader.ComputeHeadTailHash(file.FullPath);
                    (ulong dstHead, ulong dstTail) = ChunkReader.ComputeHeadTailHash(destinationFilePath);

                    if (srcHead != dstHead || srcTail != dstTail)
                    {
                        File.Delete(destinationFilePath);
                        throw new IOException(
                            $"Post-copy hash verification failed for '{file.RelativePath}'. Checksum mismatch after transfer.");
                    }

                    completedFiles++;
                }
                catch (Exception)
                {
                    if (File.Exists(destinationFilePath))
                    {
                        try
                        {
                            File.Delete(destinationFilePath);
                        }
                        catch
                        {
                            // Suppress cleanup exception to preserve original error/cancellation
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

