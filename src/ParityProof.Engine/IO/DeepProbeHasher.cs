using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ParityProof.Engine.IO;

public static class DeepProbeHasher
{
    public const int TIER1_MAX_BYTES = 4 * 1024 * 1024; // 4 MB (100% sequential read)
    public const int TIER2_MAX_BYTES = 128 * 1024 * 1024; // 128 MB (RAW photos tier)
    public const int HEAD_TAIL_CHUNK_SIZE = 64 * 1024; // 64 KB head/tail slices
    public const int TIER2_INTERIOR_SLICE_SIZE = 256 * 1024; // 256 KB interior slices
    public const int TIER3_INTERIOR_SLICE_SIZE = 1024 * 1024; // 1 MB interior slices
    public const int BUFFER_SIZE = 4 * 1024 * 1024; // 4 MB pooled buffer

    public static ulong ComputeDeepHash(
        string filePath,
        Action<long>? onBytesRead = null,
        CancellationToken cancellationToken = default)
    {
        FileInfo fileInfo = new(filePath);
        long fileLength = fileInfo.Length;

        if (fileLength == 0)
        {
            return XxHash3.HashToUInt64(ReadOnlySpan<byte>.Empty);
        }

        using SafeFileHandle handle = NativeDirectIO.OpenDirectOrSequential(filePath);
        if (handle.IsInvalid)
        {
            throw new IOException($"Failed to open file for probe hashing: {filePath}");
        }

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
        try
        {
            XxHash3 hasher = new();

            // Tier 1: Small files (<= 4 MB) are read 100% sequentially in a single pass
            if (fileLength <= TIER1_MAX_BYTES)
            {
                long fileOffset = 0;
                while (fileOffset < fileLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int bytesToRead = (int)Math.Min((long)BUFFER_SIZE, fileLength - fileOffset);
                    Span<byte> bufferSpan = rentedBuffer.AsSpan(0, bytesToRead);
                    int bytesRead = ReadExact(handle, bufferSpan, fileOffset);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    onBytesRead?.Invoke(bytesRead);
                    hasher.Append(bufferSpan.Slice(0, bytesRead));
                    fileOffset += bytesRead;
                }

                return hasher.GetCurrentHashAsUInt64();
            }

            // Generate deterministic scale-adaptive probe offsets
            List<(long Offset, int Length)> probePlan = GenerateProbePlan(fileLength);

            foreach ((long offset, int length) in probePlan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<byte> bufferSpan = rentedBuffer.AsSpan(0, length);
                int bytesRead = ReadExact(handle, bufferSpan, offset);
                if (bytesRead > 0)
                {
                    onBytesRead?.Invoke(bytesRead);
                    hasher.Append(bufferSpan.Slice(0, bytesRead));
                }
            }

            return hasher.GetCurrentHashAsUInt64();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private static int ReadExact(SafeFileHandle handle, Span<byte> destination, long fileOffset)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int bytesRead = RandomAccess.Read(handle, destination.Slice(totalRead), fileOffset + totalRead);
            if (bytesRead <= 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    public static List<(long Offset, int Length)> GenerateProbePlan(long fileLength)
    {
        List<(long Offset, int Length)> plan = new();

        if (fileLength <= TIER1_MAX_BYTES)
        {
            plan.Add((0, (int)fileLength));
            return plan;
        }

        // Head slice
        int headLength = (int)Math.Min((long)HEAD_TAIL_CHUNK_SIZE, fileLength);
        plan.Add((0, headLength));

        if (fileLength <= TIER2_MAX_BYTES)
        {
            // Tier 2 (4 MB to 128 MB): Head + Tail + 3 interior slices (256 KB) at 25%, 50%, 75%
            double[] percentages = { 0.25, 0.50, 0.75 };
            foreach (double pct in percentages)
            {
                long targetOffset = (long)(fileLength * pct) - (TIER2_INTERIOR_SLICE_SIZE / 2);
                long clampedOffset = Math.Clamp(
                    targetOffset,
                    headLength,
                    Math.Max(headLength, fileLength - HEAD_TAIL_CHUNK_SIZE - TIER2_INTERIOR_SLICE_SIZE));

                int sliceLength = (int)Math.Min(
                    (long)TIER2_INTERIOR_SLICE_SIZE,
                    Math.Max(0, fileLength - clampedOffset - HEAD_TAIL_CHUNK_SIZE));

                if (sliceLength > 0 && clampedOffset > plan[^1].Offset)
                {
                    plan.Add((clampedOffset, sliceLength));
                }
            }
        }
        else
        {
            // Tier 3 (> 128 MB to 100 GB+): Head + Tail + 8 interior slices (1 MB) from 10% to 90%
            double[] percentages = { 0.10, 0.20, 0.35, 0.50, 0.65, 0.80, 0.90 };
            foreach (double pct in percentages)
            {
                long targetOffset = (long)(fileLength * pct) - (TIER3_INTERIOR_SLICE_SIZE / 2);
                long clampedOffset = Math.Clamp(
                    targetOffset,
                    headLength,
                    Math.Max(headLength, fileLength - HEAD_TAIL_CHUNK_SIZE - TIER3_INTERIOR_SLICE_SIZE));

                int sliceLength = (int)Math.Min(
                    (long)TIER3_INTERIOR_SLICE_SIZE,
                    Math.Max(0, fileLength - clampedOffset - HEAD_TAIL_CHUNK_SIZE));

                if (sliceLength > 0 && clampedOffset > plan[^1].Offset)
                {
                    plan.Add((clampedOffset, sliceLength));
                }
            }
        }

        // Tail slice
        long tailOffset = Math.Max(headLength, fileLength - HEAD_TAIL_CHUNK_SIZE);
        int tailLength = (int)Math.Min((long)HEAD_TAIL_CHUNK_SIZE, fileLength - tailOffset);
        if (tailLength > 0 && tailOffset > plan[^1].Offset)
        {
            plan.Add((tailOffset, tailLength));
        }

        return plan;
    }
}
