using System;
using System.IO;
using ParityProof.Core.Logging;
using Xunit;

namespace ParityProof.Engine.Tests.Logging;

public sealed class StackTraceSanitizerTests
{
    [Theory]
    [InlineData(@"C:\Users\JohnDoe\AppData\Local\ParityProof\logs.db", @"%USERPROFILE%\AppData\Local\ParityProof\logs.db")]
    [InlineData(@"c:\users\alice.smith\Desktop\file.raw", @"%USERPROFILE%\Desktop\file.raw")]
    [InlineData(@"D:\Users\backup_user\vault\item.mp4", @"%USERPROFILE%\vault\item.mp4")]
    [InlineData(@"C:\Documents and Settings\Admin\test.txt", @"%USERPROFILE%\test.txt")]
    public void SanitizeText_RedactsWindowsUserPaths(string input, string expected)
    {
        string actual = StackTraceSanitizer.SanitizeText(input);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/home/jaret/projects/ImageVerifier/file.cs", "~/projects/ImageVerifier/file.cs")]
    [InlineData("/Users/steve/Documents/photo.cr3", "~/Documents/photo.cr3")]
    public void SanitizeText_RedactsUnixAndMacHomePaths(string input, string expected)
    {
        string actual = StackTraceSanitizer.SanitizeText(input);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(@"C:\Program Files\ParityProof\ParityProof.App.exe")]
    [InlineData(@"C:\Windows\System32\ntdll.dll")]
    [InlineData("/usr/bin/dotnet")]
    [InlineData("")]
    [InlineData(null)]
    public void SanitizeText_PreservesSystemAndNeutralPaths(string? input)
    {
        string actual = StackTraceSanitizer.SanitizeText(input);

        if (string.IsNullOrEmpty(input))
        {
            Assert.Equal(string.Empty, actual);
        }
        else
        {
            Assert.Equal(input, actual);
        }
    }

    [Fact]
    public void FormatException_Null_ReturnsEmpty()
    {
        string result = StackTraceSanitizer.FormatException(null, StackTracePolicy.Sanitized);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatException_NonePolicy_OmitsStackTrace()
    {
        InvalidOperationException exception = CreateExceptionWithStackTrace(@"C:\Users\JohnDoe\Secret\Code.cs");

        string formatted = StackTraceSanitizer.FormatException(exception, StackTracePolicy.None);

        Assert.Contains("Type: System.InvalidOperationException", formatted);
        Assert.Contains("Message: Simulated failure", formatted);
        Assert.Contains("[Stack trace omitted per security policy]", formatted);
        Assert.DoesNotContain(@"C:\Users\JohnDoe", formatted);
    }

    [Fact]
    public void FormatException_FullPolicy_PreservesPaths()
    {
        InvalidOperationException exception = CreateExceptionWithStackTrace(@"C:\Users\JohnDoe\Secret\Code.cs");

        string formatted = StackTraceSanitizer.FormatException(exception, StackTracePolicy.Full);

        Assert.Contains("Type: System.InvalidOperationException", formatted);
        Assert.Contains(@"C:\Users\JohnDoe", formatted);
    }

    [Fact]
    public void FormatException_SanitizedPolicy_RedactsPathsInStackTraceAndMessage()
    {
        InvalidOperationException exception = new(@"Failed to open C:\Users\JohnDoe\Private\data.bin");

        string formatted = StackTraceSanitizer.FormatException(exception, StackTracePolicy.Sanitized);

        Assert.Contains(@"Failed to open %USERPROFILE%\Private\data.bin", formatted);
        Assert.DoesNotContain(@"C:\Users\JohnDoe", formatted);
    }

    [Fact]
    public void FormatException_InnerException_RecursivelyFormatted()
    {
        IOException inner = new("Disk sector failed");
        InvalidOperationException outer = new("Verification aborted", inner);

        string formatted = StackTraceSanitizer.FormatException(outer, StackTracePolicy.Sanitized);

        Assert.Contains("Type: System.InvalidOperationException", formatted);
        Assert.Contains("--- Inner Exception (Depth 1) ---", formatted);
        Assert.Contains("Type: System.IO.IOException", formatted);
        Assert.Contains("Message: Disk sector failed", formatted);
    }

    [Fact]
    public void FormatException_AggregateException_AllChildrenFormatted()
    {
        IOException error1 = new("I/O error 1");
        UnauthorizedAccessException error2 = new("Access denied 2");
        AggregateException aggregate = new("Multiple operations failed", error1, error2);

        string formatted = StackTraceSanitizer.FormatException(aggregate, StackTracePolicy.Sanitized);

        Assert.Contains("Type: System.AggregateException", formatted);
        Assert.Contains("--- Inner Exception (Depth 1) ---", formatted);
        Assert.Contains("Type: System.IO.IOException", formatted);
        Assert.Contains("Type: System.UnauthorizedAccessException", formatted);
    }

    private static InvalidOperationException CreateExceptionWithStackTrace(string pathInMessage)
    {
        try
        {
            ThrowSimulatedException(pathInMessage);
            throw new Exception("Unreachable");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static void ThrowSimulatedException(string path)
    {
        throw new InvalidOperationException($"Simulated failure at {path}");
    }
}
