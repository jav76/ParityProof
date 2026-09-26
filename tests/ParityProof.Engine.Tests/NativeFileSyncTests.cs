using System;
using System.IO;
using Microsoft.Win32.SafeHandles;
using ParityProof.Engine.IO;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class NativeFileSyncTests : IDisposable
{
    private const int EBADF = 9;

    private readonly string _tempDirectory;

    public NativeFileSyncTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ParityProof_SyncTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [LinuxFact]
    public void FlushToDisk_WhenTheSyncFails_Throws()
    {
        string path = Path.Combine(_tempDirectory, "unsyncable.bin");
        File.WriteAllBytes(path, new byte[16]);
        SafeFileHandle handle = LinuxFileHandles.TryOpen(path, LinuxFileHandles.O_PATH)
            ?? throw new IOException($"Could not open '{path}' with O_PATH.");
        using FileStream stream = new(handle, FileAccess.Write, bufferSize: 0);

        // FileStream.Flush(true) returns normally here on .NET 10, which is why the copier cannot rely on it: an
        // EIO or ENOSPC from write-back would be dropped the same way and the copy committed.
        IOException error = Assert.Throws<IOException>(() => NativeFileSync.FlushToDisk(stream));
        Assert.Equal(EBADF, error.HResult);
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
