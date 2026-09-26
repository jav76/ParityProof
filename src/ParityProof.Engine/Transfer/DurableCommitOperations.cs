using System;
using System.IO;
using Microsoft.Win32.SafeHandles;
using ParityProof.Core.Logging;
using ParityProof.Engine.IO;

namespace ParityProof.Engine.Transfer;

internal sealed class DurableCommitOperations : ICopyCommitOperations
{
    public void FlushToDisk(FileStream tempStream) => NativeFileSync.FlushToDisk(tempStream);

    public (ulong HeadHash, ulong TailHash) ReadBackHeadTail(string tempPath)
    {
        if (OperatingSystem.IsWindows())
        {
            SafeFileHandle? uncachedHandle = TryOpenUncached(tempPath);
            if (uncachedHandle is not null)
            {
                using (uncachedHandle)
                {
                    return UncachedHeadTailReader.ComputeHeadTailHash(
                        uncachedHandle,
                        RandomAccess.GetLength(uncachedHandle));
                }
            }
        }

        using SafeFileHandle handle = File.OpenHandle(
            tempPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        long fileLength = RandomAccess.GetLength(handle);
        NativeFileSync.DropCachedPages(handle, fileLength);
        return ChunkReader.ComputeHeadTailHash(handle, fileLength);
    }

    public void Rename(string tempPath, string finalPath) => File.Move(tempPath, finalPath, overwrite: false);

    public void FlushDirectory(string directoryPath) => NativeFileSync.FlushDirectory(directoryPath);

    // Some volumes (e.g. certain network redirectors) refuse uncached handles. The read-back then goes through the
    // system cache, after the flush has already put the data on the drive.
    private static SafeFileHandle? TryOpenUncached(string tempPath)
    {
        try
        {
            return UncachedHeadTailReader.OpenUncached(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Logger.Debug(
                ex,
                "Could not open {TempPath} without the system cache; reading it back through the cache",
                tempPath);
            return null;
        }
    }
}
