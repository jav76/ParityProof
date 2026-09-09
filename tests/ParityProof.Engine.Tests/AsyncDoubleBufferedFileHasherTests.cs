using System;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Engine.IO;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class AsyncDoubleBufferedFileHasherTests : IDisposable
{
    private readonly string _testDir;

    public AsyncDoubleBufferedFileHasherTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_HasherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task ComputeHashAsync_EmptyFile_ReturnsEmptyHash()
    {
        string filePath = Path.Combine(_testDir, "empty.bin");
        File.WriteAllBytes(filePath, Array.Empty<byte>());

        FileHashResult result = await AsyncDoubleBufferedFileHasher.ComputeHashAsync(filePath);

        ulong expectedHash = XxHash3.HashToUInt64(ReadOnlySpan<byte>.Empty);
        Assert.True(result.Success);
        Assert.False(result.AbortedEarly);
        Assert.Equal(expectedHash, result.FullHash);
        Assert.Equal(expectedHash, result.HeadHash);
    }

    [Theory]
    [InlineData(1024)] // 1 KB
    [InlineData(64 * 1024)] // 64 KB
    [InlineData(128 * 1024)] // 128 KB
    [InlineData(5 * 1024 * 1024)] // 5 MB (spans multiple 2 MB buffers)
    public async Task ComputeHashAsync_VariousFileSizes_MatchesReferenceXxHash3(int byteLength)
    {
        string filePath = Path.Combine(_testDir, $"file_{byteLength}.bin");
        byte[] payload = new byte[byteLength];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(filePath, payload);

        ulong expectedFullHash = XxHash3.HashToUInt64(payload);
        int headLength = Math.Min(AsyncDoubleBufferedFileHasher.HEAD_CHUNK_SIZE, byteLength);
        ulong expectedHeadHash = XxHash3.HashToUInt64(payload.AsSpan(0, headLength));

        long totalBytesRead = 0;
        FileHashResult result = await AsyncDoubleBufferedFileHasher.ComputeHashAsync(
            filePath,
            onBytesRead: bytes => Interlocked.Add(ref totalBytesRead, bytes));

        Assert.True(result.Success);
        Assert.False(result.AbortedEarly);
        Assert.Equal(expectedFullHash, result.FullHash);
        Assert.Equal(expectedHeadHash, result.HeadHash);
        Assert.Equal(byteLength, totalBytesRead);
    }

    [Fact]
    public async Task ComputeHashAsync_ExpectedHeadMismatch_AbortsEarlyWithoutFullRead()
    {
        string filePath = Path.Combine(_testDir, "large_mismatch.bin");
        byte[] payload = new byte[8 * 1024 * 1024]; // 8 MB (exceeds 4 MB buffer)
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(filePath, payload);

        ulong wrongExpectedHeadHash = 0xDEADBEEFCAFEBABE;
        long totalBytesRead = 0;

        FileHashResult result = await AsyncDoubleBufferedFileHasher.ComputeHashAsync(
            filePath,
            expectedHeadHash: wrongExpectedHeadHash,
            onBytesRead: bytes => Interlocked.Add(ref totalBytesRead, bytes));

        Assert.False(result.Success);
        Assert.True(result.AbortedEarly);
        Assert.Equal(0UL, result.FullHash);
        // Only the first read chunk was processed before early abort
        Assert.True(totalBytesRead <= AsyncDoubleBufferedFileHasher.DEFAULT_BUFFER_SIZE);
        Assert.True(totalBytesRead < payload.Length);
    }

    [Fact]
    public async Task ComputeHashAsync_ExpectedHeadMatches_ComputesFullHashSuccessfully()
    {
        string filePath = Path.Combine(_testDir, "large_match.bin");
        byte[] payload = new byte[4 * 1024 * 1024]; // 4 MB
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(filePath, payload);

        ulong correctHeadHash = XxHash3.HashToUInt64(payload.AsSpan(0, AsyncDoubleBufferedFileHasher.HEAD_CHUNK_SIZE));
        ulong expectedFullHash = XxHash3.HashToUInt64(payload);

        FileHashResult result = await AsyncDoubleBufferedFileHasher.ComputeHashAsync(
            filePath,
            expectedHeadHash: correctHeadHash);

        Assert.True(result.Success);
        Assert.False(result.AbortedEarly);
        Assert.Equal(expectedFullHash, result.FullHash);
        Assert.Equal(correctHeadHash, result.HeadHash);
    }

    [Fact]
    public async Task ComputeHashAsync_Cancellation_ThrowsOperationCanceledException()
    {
        string filePath = Path.Combine(_testDir, "cancel_test.bin");
        byte[] payload = new byte[10 * 1024 * 1024]; // 10 MB
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(filePath, payload);

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Pre-canceled

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await AsyncDoubleBufferedFileHasher.ComputeHashAsync(filePath, cancellationToken: cts.Token);
        });
    }
}
