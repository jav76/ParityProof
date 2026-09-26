using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ParityProof.Engine.Tests;

// Opens files with Linux open(2) flags that .NET does not expose.
internal static class LinuxFileHandles
{
    // A descriptor that names the file but allows no I/O on it, so fsync fails with EBADF.
    public const int O_PATH = 0x200000;

    private const int X86_O_DIRECT = 0x4000;
    private const int ARM_O_DIRECT = 0x10000;

    // Read-only with O_DIRECT, which rejects reads that are not sector-aligned, like FILE_FLAG_NO_BUFFERING.
    public static int ReadOnlyDirectFlags =>
        RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? ARM_O_DIRECT : X86_O_DIRECT;

    // Returns null when the file system refuses the flags, such as O_DIRECT on a volume without direct I/O.
    public static SafeFileHandle? TryOpen(string path, int flags)
    {
        int fd = Open(path, flags);
        return fd == -1 ? null : new SafeFileHandle(fd, ownsHandle: true);
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
