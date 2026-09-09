using System;
using System.IO;
using System.IO.Hashing;
using System.Threading;

namespace ParityProof.Engine.Hashing;

public static class SimdHasher
{
    private const int BUFFER_SIZE = 1024 * 1024; // 1 MB buffer for stream hashing

    public static ulong Hash64(ReadOnlySpan<byte> data, long seed = 0)
    {
        return XxHash3.HashToUInt64(data, seed);
    }

    public static UInt128 Hash128(ReadOnlySpan<byte> data, long seed = 0)
    {
        return XxHash128.HashToUInt128(data, seed);
    }

    public static ulong HashStream(
        Stream stream,
        byte[] rentedBuffer,
        CancellationToken cancellationToken = default)
    {
        XxHash3 hasher = new();
        int bytesRead;

        while ((bytesRead = stream.Read(rentedBuffer, 0, rentedBuffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasher.Append(rentedBuffer.AsSpan(0, bytesRead));
        }

        return hasher.GetCurrentHashAsUInt64();
    }
}
