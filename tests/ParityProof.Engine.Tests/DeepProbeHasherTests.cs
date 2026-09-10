using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using ParityProof.Engine.IO;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class DeepProbeHasherTests : IDisposable
{
    private readonly string _testDir;

    public DeepProbeHasherTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_ProbeTests_" + Guid.NewGuid().ToString("N"));
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
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void GenerateProbePlan_Tier1_ReturnsSingleFullSpan()
    {
        const long FILE_LEN = 2 * 1024 * 1024; // 2 MB
        List<(long Offset, int Length)> plan = DeepProbeHasher.GenerateProbePlan(FILE_LEN);

        Assert.Single(plan);
        Assert.Equal(0, plan[0].Offset);
        Assert.Equal(FILE_LEN, plan[0].Length);
    }

    [Fact]
    public void GenerateProbePlan_Tier2_ReturnsHeadTailAndInteriorSlices()
    {
        const long FILE_LEN = 30 * 1024 * 1024; // 30 MB RAW photo
        List<(long Offset, int Length)> plan = DeepProbeHasher.GenerateProbePlan(FILE_LEN);

        Assert.True(plan.Count >= 4);
        Assert.Equal(0, plan[0].Offset);
        Assert.Equal(DeepProbeHasher.HEAD_TAIL_CHUNK_SIZE, plan[0].Length);

        // Verify monotonic offsets and no out-of-bounds
        long lastEnd = 0;
        foreach ((long offset, int length) in plan)
        {
            Assert.True(offset >= lastEnd);
            Assert.True(offset + length <= FILE_LEN);
            lastEnd = offset + length;
        }
    }

    [Fact]
    public void GenerateProbePlan_Tier3_ReturnsMultipleDistributedSlices()
    {
        const long FILE_LEN = 200 * 1024 * 1024; // 200 MB video clip
        List<(long Offset, int Length)> plan = DeepProbeHasher.GenerateProbePlan(FILE_LEN);

        Assert.True(plan.Count >= 7);
        Assert.Equal(0, plan[0].Offset);

        long lastEnd = 0;
        foreach ((long offset, int length) in plan)
        {
            Assert.True(offset >= lastEnd);
            Assert.True(offset + length <= FILE_LEN);
            lastEnd = offset + length;
        }
    }

    [Fact]
    public void ComputeDeepHash_SmallFile_MatchesXxHash3FullRead()
    {
        string filePath = Path.Combine(_testDir, "small.bin");
        byte[] payload = new byte[100 * 1024]; // 100 KB
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(filePath, payload);

        ulong expected = XxHash3.HashToUInt64(payload);
        ulong actual = DeepProbeHasher.ComputeDeepHash(filePath);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeDeepHash_InteriorCorruption_DetectedByDeepProbe()
    {
        string originalPath = Path.Combine(_testDir, "photo_orig.cr3");
        string corruptPath = Path.Combine(_testDir, "photo_corrupt.cr3");

        const int FILE_LEN = 20 * 1024 * 1024; // 20 MB RAW photo
        byte[] original = new byte[FILE_LEN];
        Random.Shared.NextBytes(original);

        byte[] corrupted = new byte[FILE_LEN];
        Array.Copy(original, corrupted, FILE_LEN);

        // Mutate byte exactly in the center (at 10 MB, inside the 50% interior slice)
        corrupted[10 * 1024 * 1024] = (byte)(corrupted[10 * 1024 * 1024] ^ 0xFF);

        File.WriteAllBytes(originalPath, original);
        File.WriteAllBytes(corruptPath, corrupted);

        // Head and tail check (Quick mode) would see identical head and tail!
        (ulong srcHead, ulong srcTail) = ChunkReader.ComputeHeadTailHash(originalPath);
        (ulong dstHead, ulong dstTail) = ChunkReader.ComputeHeadTailHash(corruptPath);
        Assert.Equal(srcHead, dstHead);
        Assert.Equal(srcTail, dstTail);

        // But DeepProbeHash detects the interior corruption!
        ulong origDeepHash = DeepProbeHasher.ComputeDeepHash(originalPath);
        ulong corruptDeepHash = DeepProbeHasher.ComputeDeepHash(corruptPath);
        Assert.NotEqual(origDeepHash, corruptDeepHash);
    }

    [Fact]
    public void ComputeDeepHash_ProducesDeterministicHash_AcrossMultipleInvocations()
    {
        string filePath = Path.Combine(_testDir, "deterministic_test.raw");
        const int FILE_LEN = 12 * 1024 * 1024; // 12 MB
        byte[] data = new byte[FILE_LEN];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(filePath, data);

        ulong hash1 = DeepProbeHasher.ComputeDeepHash(filePath);
        ulong hash2 = DeepProbeHasher.ComputeDeepHash(filePath);
        ulong hash3 = DeepProbeHasher.ComputeDeepHash(filePath);

        Assert.Equal(hash1, hash2);
        Assert.Equal(hash2, hash3);
        Assert.NotEqual(0UL, hash1);
    }
}
