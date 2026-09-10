using System;
using System.Buffers;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ParityProof.Engine.IO;

public readonly record struct FileHashResult(
    bool Success,
    ulong FullHash,
    ulong HeadHash,
    bool AbortedEarly = false);

public static class AsyncDoubleBufferedFileHasher
{
    public const int DEFAULT_BUFFER_SIZE = 4 * 1024 * 1024; // 4 MB ping-pong buffer
    public const int HEAD_CHUNK_SIZE = 64 * 1024; // 64 KB head chunk for early rejection

    public static async Task<FileHashResult> ComputeHashAsync(
        string filePath,
        ulong? expectedHeadHash = null,
        Action<long>? onBytesRead = null,
        CancellationToken cancellationToken = default)
    {
        FileInfo fileInfo = new(filePath);
        long fileLength = fileInfo.Length;

        if (fileLength == 0)
        {
            ulong emptyHash = XxHash3.HashToUInt64(ReadOnlySpan<byte>.Empty);
            return new FileHashResult(
                Success: true,
                FullHash: emptyHash,
                HeadHash: emptyHash,
                AbortedEarly: false);
        }

        using SafeFileHandle handle = NativeDirectIO.OpenDirectOrSequential(filePath);
        if (handle.IsInvalid)
        {
            throw new IOException($"Failed to open file for direct read: {filePath}");
        }

        byte[] bufferA = ArrayPool<byte>.Shared.Rent(DEFAULT_BUFFER_SIZE);
        byte[] bufferB = ArrayPool<byte>.Shared.Rent(DEFAULT_BUFFER_SIZE);

        try
        {
            XxHash3 hasher = new();
            long fileOffset = 0;
            ulong computedHeadHash = 0;
            bool headEvaluated = false;

            byte[] currentBuffer = bufferA;
            byte[] nextBuffer = bufferB;

            // Kick off first asynchronous read
            int bytesToRead = (int)Math.Min(DEFAULT_BUFFER_SIZE, fileLength - fileOffset);
            int currentBytesRead = await ReadExactAsync(
                handle,
                currentBuffer.AsMemory(0, bytesToRead),
                fileOffset,
                cancellationToken).ConfigureAwait(false);

            while (currentBytesRead > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onBytesRead?.Invoke(currentBytesRead);
                fileOffset += currentBytesRead;

                // Evaluate head chunk on first block
                if (!headEvaluated)
                {
                    int headSliceLength = Math.Min(HEAD_CHUNK_SIZE, currentBytesRead);
                    computedHeadHash = XxHash3.HashToUInt64(currentBuffer.AsSpan(0, headSliceLength));
                    headEvaluated = true;

                    if (expectedHeadHash.HasValue && computedHeadHash != expectedHeadHash.Value)
                    {
                        return new FileHashResult(
                            Success: false,
                            FullHash: 0,
                            HeadHash: computedHeadHash,
                            AbortedEarly: true);
                    }
                }

                // Kick off next asynchronous read while hashing current buffer
                ValueTask<int> nextReadTask = default;
                bool hasMore = fileOffset < fileLength;
                if (hasMore)
                {
                    int nextBytesToRead = (int)Math.Min(DEFAULT_BUFFER_SIZE, fileLength - fileOffset);
                    nextReadTask = ReadExactAsync(
                        handle,
                        nextBuffer.AsMemory(0, nextBytesToRead),
                        fileOffset,
                        cancellationToken);
                }

                // Hash current buffer on CPU concurrently with disk I/O
                hasher.Append(currentBuffer.AsSpan(0, currentBytesRead));

                if (hasMore)
                {
                    currentBytesRead = await nextReadTask.ConfigureAwait(false);
                    // Swap ping-pong buffers
                    (currentBuffer, nextBuffer) = (nextBuffer, currentBuffer);
                }
                else
                {
                    break;
                }
            }

            ulong finalFullHash = hasher.GetCurrentHashAsUInt64();
            return new FileHashResult(
                Success: true,
                FullHash: finalFullHash,
                HeadHash: computedHeadHash,
                AbortedEarly: false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufferA);
            ArrayPool<byte>.Shared.Return(bufferB);
        }
    }

    private static async ValueTask<int> ReadExactAsync(
        SafeFileHandle handle,
        Memory<byte> buffer,
        long fileOffset,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await RandomAccess.ReadAsync(
                handle,
                buffer.Slice(totalRead),
                fileOffset + totalRead,
                cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
