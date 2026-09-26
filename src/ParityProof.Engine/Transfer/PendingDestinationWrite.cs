using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ParityProof.Engine.Transfer;

// One destination's temp-file write for a single source file. Kept out of the [LogMethod]-woven MediaCopier:
// these members run for every chunk, and the aspect would wrap each call with logging and argument boxing.
internal sealed class PendingDestinationWrite
{
    public PendingDestinationWrite(
        string destinationRootPath,
        string requestedPath,
        string targetPath,
        string tempPath)
    {
        DestinationRootPath = destinationRootPath;
        RequestedPath = requestedPath;
        TargetPath = targetPath;
        TempPath = tempPath;
    }

    public string DestinationRootPath { get; }

    public string RequestedPath { get; }

    public string TargetPath { get; }

    public string TempPath { get; }

    public FileStream? Stream { get; private set; }

    public Exception? WriteError { get; private set; }

    public bool IsFailed { get; private set; }

    public bool IsCommitted { get; private set; }

    public bool IsWriting => Stream is not null;

    public void OpenTempStream()
    {
        Stream = new FileStream(
            TempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 0,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
    }

    // Captures the error instead of throwing so one failing destination cannot fault the shared fan-out.
    public async Task WriteChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        try
        {
            await Stream!.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteError = ex;
        }
    }

    // Set before the flush so the timestamps (QA-006) reach the drive with the data. A default value is left unset.
    public void SetTimestamps(DateTime lastWriteTimeUtc, DateTime creationTimeUtc)
    {
        SafeFileHandle handle = Stream!.SafeFileHandle;
        if (lastWriteTimeUtc != default)
        {
            File.SetLastWriteTimeUtc(handle, lastWriteTimeUtc);
        }

        if (creationTimeUtc != default)
        {
            File.SetCreationTimeUtc(handle, creationTimeUtc);
        }
    }

    // Runs on the thread pool so every destination's drive flushes at the same time. Like WriteChunkAsync, it
    // captures the error in WriteError instead of throwing.
    public Task FlushToDiskAsync(ICopyCommitOperations commitOperations)
    {
        FileStream stream = Stream!;
        return Task.Run(() =>
        {
            try
            {
                commitOperations.FlushToDisk(stream);
            }
            catch (Exception ex)
            {
                WriteError = ex;
            }
        });
    }

    public async Task CloseStreamAsync()
    {
        FileStream? stream = Stream;
        Stream = null;
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task AbandonAsync()
    {
        IsFailed = true;
        try
        {
            await CloseStreamAsync().ConfigureAwait(false);
        }
        catch
        {
            // The write has already failed; a close error must not hide the original one.
        }

        DeleteTempFile();
    }

    public void MarkCommitted() => IsCommitted = true;

    public void DeleteTempFile()
    {
        try
        {
            if (File.Exists(TempPath))
            {
                File.Delete(TempPath);
            }
        }
        catch
        {
            // Best effort: a leftover temp file never replaces or hides a real backup file.
        }
    }
}
