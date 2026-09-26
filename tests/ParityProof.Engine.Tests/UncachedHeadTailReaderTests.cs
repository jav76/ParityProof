using System;
using System.IO;
using Microsoft.Win32.SafeHandles;
using ParityProof.Engine.IO;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class UncachedHeadTailReaderTests : IDisposable
{
    private const int SHORT_FILE_LENGTH = 1000;
    private const long CLAIMED_FILE_LENGTH = 200_000;

    // Around the 64 KB chunk and sector-alignment boundaries, plus a length that is not a multiple of anything.
    private static readonly int[] _fileLengths = { 1, 4095, 65535, 65536, 65537, 131071, 131195, 1_000_003 };

    private readonly string _tempDirectory;

    public UncachedHeadTailReaderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ParityProof_UncachedTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void ComputeHeadTailHash_MatchesChunkReader()
    {
        foreach (int fileLength in _fileLengths)
        {
            string path = WriteRandomFile(fileLength);
            using SafeFileHandle handle = File.OpenHandle(path);

            Assert.Equal(
                ChunkReader.ComputeHeadTailHash(handle, fileLength),
                UncachedHeadTailReader.ComputeHeadTailHash(handle, fileLength));
        }
    }

    // O_DIRECT fails any read that is not sector-aligned in offset, length or memory address, as Windows does for
    // the uncached handle this reader is used with.
    [LinuxFact(requiresDirectIO: true)]
    public void ComputeHeadTailHash_DirectIOHandle_MatchesChunkReader()
    {
        foreach (int fileLength in _fileLengths)
        {
            string path = WriteRandomFile(fileLength);
            (ulong HeadHash, ulong TailHash) expected = ChunkReader.ComputeHeadTailHash(path);
            using SafeFileHandle handle = LinuxFileHandles.TryOpen(path, LinuxFileHandles.ReadOnlyDirectFlags)
                ?? throw new IOException($"Could not open '{path}' with O_DIRECT.");

            Assert.Equal(expected, UncachedHeadTailReader.ComputeHeadTailHash(handle, fileLength));
        }
    }

    [Fact]
    public void ComputeHeadTailHash_FileShorterThanLength_Throws()
    {
        string path = WriteRandomFile(SHORT_FILE_LENGTH);
        using SafeFileHandle handle = File.OpenHandle(path);

        Assert.Throws<IOException>(() => UncachedHeadTailReader.ComputeHeadTailHash(handle, CLAIMED_FILE_LENGTH));
    }

    private string WriteRandomFile(int length)
    {
        string path = Path.Combine(_tempDirectory, $"payload_{length}.bin");
        byte[] payload = new byte[length];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(path, payload);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
