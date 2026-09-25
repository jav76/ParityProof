using System;
using System.IO;

namespace ParityProof.Engine.Tests;

// xUnit 2.x cannot skip from inside a running test, so the volume holding the temp folder is probed when the
// test is discovered and the fact reports Skipped (not Passed) on case-sensitive volumes such as ext4.
public sealed class CaseInsensitiveTempFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> _tempIsCaseInsensitive = new(ProbeTempDirectory);

    public CaseInsensitiveTempFactAttribute()
    {
        if (!_tempIsCaseInsensitive.Value)
        {
            Skip = "The temp folder is on a case-sensitive volume; this test needs NTFS, exFAT or default APFS.";
        }
    }

    private static bool ProbeTempDirectory()
    {
        string probeName = "ParityProof_CaseProbe_" + Guid.NewGuid().ToString("N") + ".tmp";
        string probePath = Path.Combine(Path.GetTempPath(), probeName);
        File.WriteAllBytes(probePath, Array.Empty<byte>());
        try
        {
            return File.Exists(Path.Combine(Path.GetTempPath(), probeName.ToUpperInvariant()));
        }
        finally
        {
            File.Delete(probePath);
        }
    }
}
