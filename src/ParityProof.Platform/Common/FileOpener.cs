using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ParityProof.Platform.Common;

public static class FileOpener
{
    public static void RevealInFileManager(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            string? folder = File.Exists(filePath) ? Path.GetDirectoryName(filePath) : filePath;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = File.Exists(filePath) ? $"/select,\"{filePath}\"" : $"\"{folder}\"",
                    UseShellExecute = true
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = File.Exists(filePath) ? $"-R \"{filePath}\"" : $"\"{folder}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{folder}\"",
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Suppress process execution errors on restricted environments
        }
    }
}
