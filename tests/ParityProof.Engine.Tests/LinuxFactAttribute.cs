using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace ParityProof.Engine.Tests;

// Runs only on Linux and, with requiresDirectIO, only where the temp folder accepts O_DIRECT. Elsewhere the fact
// reports Skipped (not Passed), because xUnit 2.x cannot skip from inside a running test.
public sealed class LinuxFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> _tempSupportsDirectIO = new(ProbeTempDirectory);

    public LinuxFactAttribute(bool requiresDirectIO = false)
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Needs Linux.";
        }
        else if (requiresDirectIO && !_tempSupportsDirectIO.Value)
        {
            Skip = "The temp folder is on a volume without direct I/O (O_DIRECT).";
        }
    }

    private static bool ProbeTempDirectory()
    {
        string probeName = "ParityProof_DirectIOProbe_" + Guid.NewGuid().ToString("N") + ".tmp";
        string probePath = Path.Combine(Path.GetTempPath(), probeName);
        File.WriteAllBytes(probePath, new byte[1]);
        try
        {
            using SafeFileHandle? handle = LinuxFileHandles.TryOpen(probePath, LinuxFileHandles.ReadOnlyDirectFlags);
            return handle is not null;
        }
        finally
        {
            File.Delete(probePath);
        }
    }
}
