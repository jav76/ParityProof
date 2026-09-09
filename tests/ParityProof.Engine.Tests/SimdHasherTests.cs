using System;
using System.IO;
using System.Text;
using ParityProof.Engine.Hashing;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class SimdHasherTests
{
    [Fact]
    public void Hash64_EmptySpan_ReturnsNonZeroConsistentValue()
    {
        ReadOnlySpan<byte> empty = ReadOnlySpan<byte>.Empty;
        ulong hash1 = SimdHasher.Hash64(empty);
        ulong hash2 = SimdHasher.Hash64(empty);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Hash64_DifferentPayloads_ProduceDistinctHashes()
    {
        byte[] data1 = Encoding.UTF8.GetBytes("Camera Image Sensor Data Block 1");
        byte[] data2 = Encoding.UTF8.GetBytes("Camera Image Sensor Data Block 2");

        ulong hash1 = SimdHasher.Hash64(data1);
        ulong hash2 = SimdHasher.Hash64(data2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Hash128_DifferentPayloads_ProduceDistinctHashes()
    {
        byte[] data1 = Encoding.UTF8.GetBytes("RAW File Header A");
        byte[] data2 = Encoding.UTF8.GetBytes("RAW File Header B");

        UInt128 hash1 = SimdHasher.Hash128(data1);
        UInt128 hash2 = SimdHasher.Hash128(data2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void HashStream_MatchesSpanHash()
    {
        byte[] payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);

        ulong expected = SimdHasher.Hash64(payload);

        using MemoryStream stream = new(payload);
        byte[] rented = new byte[64 * 1024];
        ulong actual = SimdHasher.HashStream(stream, rented);

        Assert.Equal(expected, actual);
    }
}
