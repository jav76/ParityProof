using System;
using System.IO;

namespace ParityProof.Engine.Tests;

// Runs only where the given Linux special file exists. Elsewhere the fact reports Skipped (not Passed), because
// xUnit 2.x cannot skip from inside a running test.
public sealed class LinuxSpecialFileFactAttribute : FactAttribute
{
    // Every write fails with ENOSPC.
    public const string DEV_FULL_PATH = "/dev/full";

    // Opens normally, then reads at offset 0 (an unmapped address) fail with EIO, like a bad sector on a card.
    public const string PROC_SELF_MEM_PATH = "/proc/self/mem";

    public LinuxSpecialFileFactAttribute(string specialFilePath)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(specialFilePath))
        {
            Skip = $"Needs {specialFilePath}, which is only available on Linux.";
        }
    }
}
