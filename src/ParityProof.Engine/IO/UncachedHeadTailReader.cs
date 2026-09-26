using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ParityProof.Engine.Hashing;

namespace ParityProof.Engine.IO;

// Reads a copy's head and tail from the device rather than the system cache, so the post-copy check can catch
// data the drive did not store correctly.
internal static class UncachedHeadTailReader
{
    // FILE_FLAG_NO_BUFFERING. .NET passes FileOptions straight to CreateFile on Windows and accepts this value, but
    // has no named member for it.
    private const FileOptions WINDOWS_NO_BUFFERING = (FileOptions)0x20000000;

    // Uncached reads must start, end and land in memory on a sector boundary. Windows sectors are 512 bytes or 4 KB;
    // 64 KB covers any sector size for a few extra KB per read.
    private const int SECTOR_ALIGNMENT = 64 * 1024;

    // Windows only. NTFS, FAT and exFAT flush any cached data for the file before an uncached read, then read it from
    // the device.
    public static SafeFileHandle OpenUncached(string path)
    {
        return File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            WINDOWS_NO_BUFFERING);
    }

    // Returns the same hashes as ChunkReader.ComputeHeadTailHash, but only issues sector-aligned reads into
    // sector-aligned memory, as an uncached handle requires.
    public static (ulong HeadHash, ulong TailHash) ComputeHeadTailHash(
        SafeFileHandle handle,
        long fileLength,
        int chunkSize = ChunkReader.DEFAULT_CHUNK_SIZE)
    {
        if (fileLength <= 0)
        {
            return (0UL, 0UL);
        }

        // An unaligned chunk-sized range spans at most this many aligned bytes.
        int windowLength = AlignUp(chunkSize) + SECTOR_ALIGNMENT;
        byte[] rented = ArrayPool<byte>.Shared.Rent(windowLength + SECTOR_ALIGNMENT);
        GCHandle pin = GCHandle.Alloc(rented, GCHandleType.Pinned);
        try
        {
            Span<byte> window = rented.AsSpan(GetAlignmentPadding(pin.AddrOfPinnedObject()), windowLength);

            int headLength = (int)Math.Min(chunkSize, fileLength);
            ReadAligned(handle, window, alignedOffset: 0, headLength);
            ulong headHash = SimdHasher.Hash64(window.Slice(0, headLength));
            if (fileLength <= chunkSize)
            {
                return (headHash, headHash);
            }

            long tailOffset = fileLength - chunkSize;
            int tailPadding = (int)(tailOffset % SECTOR_ALIGNMENT);
            ReadAligned(handle, window, tailOffset - tailPadding, tailPadding + chunkSize);
            ulong tailHash = SimdHasher.Hash64(window.Slice(tailPadding, chunkSize));

            return (headHash, tailHash);
        }
        finally
        {
            pin.Free();
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // Fills at least the first requiredLength bytes of the window with whole aligned blocks read from alignedOffset.
    private static void ReadAligned(SafeFileHandle handle, Span<byte> window, long alignedOffset, int requiredLength)
    {
        int readLength = AlignUp(requiredLength);
        int totalRead = 0;
        while (totalRead < requiredLength)
        {
            int bytesRead = RandomAccess.Read(
                handle,
                window.Slice(totalRead, readLength - totalRead),
                alignedOffset + totalRead);
            if (bytesRead <= 0)
            {
                break;
            }

            totalRead += bytesRead;

            // A partial block is the end of the file, and a read after it would start off a sector boundary.
            if (bytesRead % SECTOR_ALIGNMENT != 0)
            {
                break;
            }
        }

        if (totalRead < requiredLength)
        {
            throw new IOException(
                $"Only {alignedOffset + totalRead} bytes could be read back from the copy, " +
                $"{alignedOffset + requiredLength} were expected.");
        }
    }

    private static int AlignUp(int length) => (length + SECTOR_ALIGNMENT - 1) / SECTOR_ALIGNMENT * SECTOR_ALIGNMENT;

    private static int GetAlignmentPadding(nint address)
    {
        int remainder = (int)(address % SECTOR_ALIGNMENT);
        return remainder == 0 ? 0 : SECTOR_ALIGNMENT - remainder;
    }
}
