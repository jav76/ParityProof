using System;
using System.Buffers;
using System.IO;
using ParityProof.Engine.Hashing;
using Microsoft.Win32.SafeHandles;

namespace ParityProof.Engine.IO;

public static class ChunkReader
{
    public const int DEFAULT_CHUNK_SIZE = 64 * 1024; // 64 KB

    public static (ulong HeadHash, ulong TailHash) ComputeHeadTailHash(
        SafeFileHandle handle,
        long fileLength,
        int chunkSize = DEFAULT_CHUNK_SIZE)
    {
        if (fileLength <= 0)
        {
            return (0UL, 0UL);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            int headBytesToRead = (int)Math.Min((long)chunkSize, fileLength);
            Span<byte> headSpan = rented.AsSpan(0, headBytesToRead);
            int headRead = ReadExact(handle, headSpan, fileOffset: 0);
            ulong headHash = SimdHasher.Hash64(headSpan.Slice(0, headRead));

            if (fileLength <= chunkSize)
            {
                return (headHash, headHash);
            }

            int tailBytesToRead = (int)Math.Min((long)chunkSize, fileLength);
            Span<byte> tailSpan = rented.AsSpan(0, tailBytesToRead);
            long tailOffset = fileLength - tailBytesToRead;
            int tailRead = ReadExact(handle, tailSpan, fileOffset: tailOffset);
            ulong tailHash = SimdHasher.Hash64(tailSpan.Slice(0, tailRead));

            return (headHash, tailHash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
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

    public static (ulong HeadHash, ulong TailHash) ComputeHeadTailHash(
        string filePath,
        int chunkSize = DEFAULT_CHUNK_SIZE)
    {
        FileInfo fileInfo = new(filePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            return (0UL, 0UL);
        }

        using SafeFileHandle handle = File.OpenHandle(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.SequentialScan);

        return ComputeHeadTailHash(handle, fileInfo.Length, chunkSize);
    }
}
