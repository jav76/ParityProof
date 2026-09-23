using System;

namespace ParityProof.Core.Utils;

public static class ByteSizeFormatter
{
    private const double KILOBYTE = 1024.0;
    private const double MEGABYTE = 1024.0 * 1024.0;
    private const double GIGABYTE = 1024.0 * 1024.0 * 1024.0;
    private const double TERABYTE = 1024.0 * 1024.0 * 1024.0 * 1024.0;

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        if (bytes >= TERABYTE)
        {
            return $"{(bytes / TERABYTE):F2} TB";
        }

        if (bytes >= GIGABYTE)
        {
            return $"{(bytes / GIGABYTE):F2} GB";
        }

        if (bytes >= MEGABYTE)
        {
            return $"{(bytes / MEGABYTE):F1} MB";
        }

        if (bytes >= KILOBYTE)
        {
            return $"{(bytes / KILOBYTE):F1} KB";
        }

        return $"{bytes} B";
    }
}
