using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ParityProof.Engine.IO;

// Makes copied files survive an unplug or power loss before they are reported as backed up.
internal static class NativeFileSync
{
    private const string LINUX_LIBC = "libc";
    private const string MAC_LIB_SYSTEM = "libSystem.dylib";

    private const int O_RDONLY = 0;
    private const int LINUX_O_CLOEXEC = 0x80000;
    private const int MAC_O_CLOEXEC = 0x1000000;

    private const int MAC_F_FULLFSYNC = 51;

    private const int LINUX_POSIX_FADV_DONTNEED = 4;

    private const int MAC_PROT_READ = 0x1;
    private const int MAC_MAP_SHARED = 0x1;
    private const int MAC_MS_INVALIDATE = 0x2;
    private const int MAC_MS_SYNC = 0x10;
    private const nint MAP_FAILED = -1;

    private const int EINTR = 4;
    private const int EINVAL = 22;
    private const int ENOTTY = 25;
    private const int MAC_ENOTSUP = 45;
    private const int LINUX_ENOTSUP = 95;

    // open and fcntl are variadic. Only their fixed parameters are declared: Apple arm64 passes variadic arguments
    // on the stack, where a declared extra parameter would never arrive. Neither call here needs one.
    [DllImport(LINUX_LIBC, SetLastError = true, EntryPoint = "open")]
    private static extern int LinuxOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport(LINUX_LIBC, SetLastError = true, EntryPoint = "fsync")]
    private static extern int LinuxFsync(int fd);

    [DllImport(LINUX_LIBC, SetLastError = true, EntryPoint = "close")]
    private static extern int LinuxClose(int fd);

    [DllImport(LINUX_LIBC, SetLastError = true, EntryPoint = "posix_fadvise")]
    private static extern int LinuxPosixFadvise(int fd, long offset, long length, int advice);

    [DllImport(MAC_LIB_SYSTEM, SetLastError = true, EntryPoint = "open")]
    private static extern int MacOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport(MAC_LIB_SYSTEM, SetLastError = true, EntryPoint = "fsync")]
    private static extern int MacFsync(int fd);

    [DllImport(MAC_LIB_SYSTEM, SetLastError = true, EntryPoint = "close")]
    private static extern int MacClose(int fd);

    [DllImport(MAC_LIB_SYSTEM, SetLastError = true, EntryPoint = "fcntl")]
    private static extern int MacFcntl(int fd, int command);

    [DllImport(MAC_LIB_SYSTEM, SetLastError = true, EntryPoint = "mmap")]
    private static extern nint MacMmap(nint address, nuint length, int protection, int flags, int fd, long offset);

    [DllImport(MAC_LIB_SYSTEM, SetLastError = true, EntryPoint = "msync")]
    private static extern int MacMsync(nint address, nuint length, int flags);

    [DllImport(MAC_LIB_SYSTEM, SetLastError = true, EntryPoint = "munmap")]
    private static extern int MacMunmap(nint address, nuint length);

    // Throws when the drive does not confirm the data. On Unix the sync is issued directly: .NET 10's
    // FileStream.Flush(true) discards the fsync result (dotnet/runtime#124725), so a write-back error such as
    // EIO or ENOSPC would otherwise go unnoticed.
    public static void FlushToDisk(FileStream stream)
    {
        bool isMacOS = OperatingSystem.IsMacOS();
        if (!isMacOS && !OperatingSystem.IsLinux())
        {
            // FlushFileBuffers, whose failure .NET reports on Windows.
            stream.Flush(flushToDisk: true);
            return;
        }

        stream.Flush(flushToDisk: false);

        SafeFileHandle handle = stream.SafeFileHandle;
        bool addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            SyncDescriptor((int)handle.DangerousGetHandle(), stream.Name, isMacOS, "flush to the drive");
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }
    }

    // A rename or a new folder is only durable once the folder that holds its entry is flushed. Only Unix exposes
    // that (a sync on a directory descriptor), so this does nothing on Windows.
    public static void FlushDirectory(string directoryPath)
    {
        bool isMacOS = OperatingSystem.IsMacOS();
        if (!isMacOS && !OperatingSystem.IsLinux())
        {
            return;
        }

        int fd;
        do
        {
            fd = isMacOS
                ? MacOpen(directoryPath, O_RDONLY | MAC_O_CLOEXEC)
                : LinuxOpen(directoryPath, O_RDONLY | LINUX_O_CLOEXEC);
        }
        while (fd == -1 && Marshal.GetLastPInvokeError() == EINTR);

        if (fd == -1)
        {
            throw CreateSyncException("open the folder", directoryPath, Marshal.GetLastPInvokeError());
        }

        try
        {
            SyncDescriptor(fd, directoryPath, isMacOS, "flush the folder");
        }
        finally
        {
            _ = isMacOS ? MacClose(fd) : LinuxClose(fd);
        }
    }

    // Best effort. Once the file has been flushed its cached pages are clean, and dropping them makes the next read
    // come from the device instead of RAM. Windows reads back through an uncached handle instead.
    public static void DropCachedPages(SafeFileHandle handle, long fileLength)
    {
        if (fileLength <= 0 || handle.IsInvalid || handle.IsClosed)
        {
            return;
        }

        try
        {
            int fd = (int)handle.DangerousGetHandle();
            if (OperatingSystem.IsLinux())
            {
                _ = LinuxPosixFadvise(fd, 0, 0, LINUX_POSIX_FADV_DONTNEED);
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS has no fadvise. Invalidating a mapping of the file drops its cached pages, and MS_SYNC
                // writes back anything still dirty first, so nothing is discarded. F_NOCACHE is not enough here:
                // it stops new caching, but reads can still be served from pages cached by the write.
                nuint length = (nuint)fileLength;
                nint mapping = MacMmap(0, length, MAC_PROT_READ, MAC_MAP_SHARED, fd, 0);
                if (mapping != MAP_FAILED)
                {
                    try
                    {
                        _ = MacMsync(mapping, length, MAC_MS_SYNC | MAC_MS_INVALIDATE);
                    }
                    finally
                    {
                        _ = MacMunmap(mapping, length);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // The read-back still runs; it may be served from the cache.
        }
    }

    // On macOS fsync leaves the data in the drive's own write cache, so F_FULLFSYNC is used, with fsync only for
    // file systems (SMB, FUSE, some USB formats) that do not implement it.
    private static void SyncDescriptor(int fd, string path, bool isMacOS, string action)
    {
        if (isMacOS)
        {
            int fullSyncErrno = RetryOnInterrupt(() => MacFcntl(fd, MAC_F_FULLFSYNC));
            if (fullSyncErrno == 0)
            {
                return;
            }

            if (!IsSyncUnsupported(fullSyncErrno, isMacOS))
            {
                throw CreateSyncException(action, path, fullSyncErrno);
            }
        }

        int syncErrno = RetryOnInterrupt(() => isMacOS ? MacFsync(fd) : LinuxFsync(fd));
        if (syncErrno != 0 && !IsSyncUnsupported(syncErrno, isMacOS))
        {
            throw CreateSyncException(action, path, syncErrno);
        }
    }

    // Returns 0 on success, otherwise the errno of the last attempt.
    private static int RetryOnInterrupt(Func<int> syncCall)
    {
        while (syncCall() == -1)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno != EINTR)
            {
                return errno;
            }
        }

        return 0;
    }

    // The file system cannot sync this way, so there is nothing more to flush. EROFS is deliberately not in this
    // list: the file or folder was just written, so a read-only file system here means it shut down after an error.
    // ENOTTY is what macOS file systems without F_FULLFSYNC return.
    private static bool IsSyncUnsupported(int errno, bool isMacOS)
    {
        return errno is EINVAL or ENOTTY || errno == (isMacOS ? MAC_ENOTSUP : LINUX_ENOTSUP);
    }

    // .NET stores the raw errno in HResult on Unix, so callers can classify this like any other I/O error.
    private static IOException CreateSyncException(string action, string path, int errno)
    {
        return new IOException(
            $"Could not {action} '{path}': {Marshal.GetPInvokeErrorMessage(errno)}",
            errno);
    }
}
