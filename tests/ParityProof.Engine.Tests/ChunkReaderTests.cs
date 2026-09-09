using System;
using System.IO;
using ParityProof.Engine.IO;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class ChunkReaderTests : IDisposable
{
    private readonly string _tempDirectory;

    public ChunkReaderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ParityProof_ChunkTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void ComputeHeadTailHash_EmptyFile_ReturnsZeroes()
    {
        string emptyFile = Path.Combine(_tempDirectory, "empty.raw");
        File.WriteAllBytes(emptyFile, Array.Empty<byte>());

        (ulong head, ulong tail) = ChunkReader.ComputeHeadTailHash(emptyFile);

        Assert.Equal(0UL, head);
        Assert.Equal(0UL, tail);
    }

    [Fact]
    public void ComputeHeadTailHash_SmallFile_HeadEqualsTail()
    {
        string smallFile = Path.Combine(_tempDirectory, "small.raw");
        byte[] payload = new byte[1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(smallFile, payload);

        (ulong head, ulong tail) = ChunkReader.ComputeHeadTailHash(smallFile);

        Assert.NotEqual(0UL, head);
        Assert.Equal(head, tail);
    }

    [Fact]
    public void ComputeHeadTailHash_LargeFile_DistinctHeadAndTail()
    {
        string largeFile = Path.Combine(_tempDirectory, "large.raw");
        byte[] payload = new byte[256 * 1024]; // 256 KB
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(largeFile, payload);

        (ulong head, ulong tail) = ChunkReader.ComputeHeadTailHash(largeFile);

        Assert.NotEqual(0UL, head);
        Assert.NotEqual(0UL, tail);
        Assert.NotEqual(head, tail);
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
            // Ignore cleanup exceptions
        }
    }
}
