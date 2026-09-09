using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ParityProof.Engine.IO;

public static class NativeDirectIO
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

    private const int F_NOCACHE = 48; // macOS fcntl F_NOCACHE flag

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("libSystem.dylib", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int MacFcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true, EntryPoint = "posix_fadvise")]
    private static extern int LinuxPosixFadvise(int fd, long offset, long len, int advice);

    private const int POSIX_FADV_SEQUENTIAL = 2;
    private const int POSIX_FADV_NOREUSE = 5;

    public static SafeFileHandle OpenDirectOrSequential(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            SafeFileHandle winHandle = CreateFileW(
                filePath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_SEQUENTIAL_SCAN,
                IntPtr.Zero);

            if (!winHandle.IsInvalid)
            {
                return winHandle;
            }
        }

        SafeFileHandle handle = File.OpenHandle(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.SequentialScan);

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                int fd = (int)handle.DangerousGetHandle();
                _ = MacFcntl(fd, F_NOCACHE, 1);
            }
            catch
            {
                // Fallback gracefully to standard sequential caching
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                int fd = (int)handle.DangerousGetHandle();
                _ = LinuxPosixFadvise(fd, 0, 0, POSIX_FADV_SEQUENTIAL | POSIX_FADV_NOREUSE);
            }
            catch
            {
                // Fallback gracefully to standard sequential caching
            }
        }

        return handle;
    }
}
