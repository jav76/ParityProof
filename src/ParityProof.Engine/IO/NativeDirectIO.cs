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
    private const int F_NOCACHE_ENABLE = 1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    // fcntl is variadic. On Apple arm64 variadic arguments are passed on the stack, not in registers, so the
    // flag value is declared after six padding arguments that fill x2-x7 and push it into the first stack slot.
    [DllImport("libSystem.dylib", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int MacFcntlX64(int fd, int cmd, int arg);

    [DllImport("libSystem.dylib", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int MacFcntlArm64(
        int fd,
        int cmd,
        nint unused2,
        nint unused3,
        nint unused4,
        nint unused5,
        nint unused6,
        nint unused7,
        nint arg);

    [DllImport("libc", SetLastError = true, EntryPoint = "posix_fadvise")]
    private static extern int LinuxPosixFadvise(int fd, long offset, long len, int advice);

    private const int POSIX_FADV_RANDOM = 1;
    private const int POSIX_FADV_SEQUENTIAL = 2;
    private const int POSIX_FADV_DONTNEED = 4;
    private const int POSIX_FADV_NOREUSE = 5;

    public static void EvictPageCache(SafeFileHandle handle, long offset, long length)
    {
        if (OperatingSystem.IsLinux() && !handle.IsInvalid && !handle.IsClosed)
        {
            try
            {
                int fd = (int)handle.DangerousGetHandle();
                _ = LinuxPosixFadvise(fd, offset, length, POSIX_FADV_DONTNEED);
            }
            catch
            {
                // Best-effort page cache eviction
            }
        }
    }

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
            DisableMacPageCache(handle);
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                int fd = (int)handle.DangerousGetHandle();
                _ = LinuxPosixFadvise(fd, 0, 0, POSIX_FADV_SEQUENTIAL);
                _ = LinuxPosixFadvise(fd, 0, 0, POSIX_FADV_NOREUSE);
            }
            catch
            {
                // Fallback gracefully to standard sequential caching
            }
        }

        return handle;
    }

    public static SafeFileHandle OpenForProbeHashing(string filePath)
    {
        SafeFileHandle handle = File.OpenHandle(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.None);

        if (OperatingSystem.IsMacOS())
        {
            // Probe reads must come from the media, not pages cached by a recent copy.
            DisableMacPageCache(handle);
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                int fd = (int)handle.DangerousGetHandle();
                _ = LinuxPosixFadvise(fd, 0, 0, POSIX_FADV_RANDOM);
            }
            catch
            {
                // Fallback gracefully to standard caching
            }
        }

        return handle;
    }

    private static void DisableMacPageCache(SafeFileHandle handle)
    {
        try
        {
            int fd = (int)handle.DangerousGetHandle();
            _ = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? MacFcntlArm64(fd, F_NOCACHE, 0, 0, 0, 0, 0, 0, F_NOCACHE_ENABLE)
                : MacFcntlX64(fd, F_NOCACHE, F_NOCACHE_ENABLE);
        }
        catch
        {
            // Fallback gracefully to standard caching
        }
    }
}
