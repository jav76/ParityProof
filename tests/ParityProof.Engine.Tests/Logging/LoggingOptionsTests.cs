using System;
using ParityProof.Core.Logging;
using Serilog.Events;
using Xunit;

namespace ParityProof.Engine.Tests.Logging;

public sealed class LoggingOptionsTests
{
    [Fact]
    public void Parse_EmptyOrNullArgs_ReturnsDefaultOptions()
    {
        LoggingOptions optionsNull = LoggingOptions.Parse(null);
        LoggingOptions optionsEmpty = LoggingOptions.Parse(Array.Empty<string>());

        Assert.True(optionsNull.EnableFile);
        Assert.True(optionsNull.EnableSqlite);
        Assert.True(optionsNull.EnableConsole);

#if DEBUG
        Assert.Equal(LogEventLevel.Debug, optionsNull.MinimumLevel);
        Assert.Equal(LogEventLevel.Debug, optionsEmpty.MinimumLevel);
#else
        Assert.Equal(LogEventLevel.Warning, optionsNull.MinimumLevel);
        Assert.Equal(LogEventLevel.Warning, optionsEmpty.MinimumLevel);
#endif
    }

    [Theory]
    [InlineData("--log-level", "verbose", LogEventLevel.Verbose)]
    [InlineData("--log-level", "trace", LogEventLevel.Verbose)]
    [InlineData("--log-level", "debug", LogEventLevel.Debug)]
    [InlineData("--log-level", "info", LogEventLevel.Information)]
    [InlineData("--log-level", "information", LogEventLevel.Information)]
    [InlineData("--log-level", "warn", LogEventLevel.Warning)]
    [InlineData("--log-level", "warning", LogEventLevel.Warning)]
    [InlineData("--log-level", "error", LogEventLevel.Error)]
    [InlineData("--log-level", "fatal", LogEventLevel.Fatal)]
    [InlineData("--log-level", "critical", LogEventLevel.Fatal)]
    [InlineData("-l", "debug", LogEventLevel.Debug)]
    [InlineData("-l", "error", LogEventLevel.Error)]
    public void Parse_ExplicitLogLevelArgs_SetsExpectedLevel(string flag, string value, LogEventLevel expected)
    {
        string[] args = new string[] { flag, value };
        LoggingOptions options = LoggingOptions.Parse(args);

        Assert.Equal(expected, options.MinimumLevel);
    }

    [Theory]
    [InlineData("--log-level=debug", LogEventLevel.Debug)]
    [InlineData("--log-level=error", LogEventLevel.Error)]
    [InlineData("-l=warning", LogEventLevel.Warning)]
    [InlineData("-l=info", LogEventLevel.Information)]
    public void Parse_EqualsSyntaxLogLevelArgs_SetsExpectedLevel(string arg, LogEventLevel expected)
    {
        string[] args = new string[] { arg };
        LoggingOptions options = LoggingOptions.Parse(args);

        Assert.Equal(expected, options.MinimumLevel);
    }

    [Theory]
    [InlineData("-v")]
    [InlineData("--verbose")]
    public void Parse_VerboseFlag_SetsDebugLevel(string flag)
    {
        string[] args = new string[] { flag };
        LoggingOptions options = LoggingOptions.Parse(args);

        Assert.Equal(LogEventLevel.Debug, options.MinimumLevel);
    }

    [Fact]
    public void Parse_DisableFlags_DisablesSinks()
    {
        string[] args = new string[] { "--no-log-file", "--no-log-db", "--no-log-console" };
        LoggingOptions options = LoggingOptions.Parse(args);

        Assert.False(options.EnableFile);
        Assert.False(options.EnableSqlite);
        Assert.False(options.EnableConsole);
    }

    [Fact]
    public void Parse_CustomPaths_SetsExpectedPaths()
    {
        string customLog = "/tmp/test-logs/custom.log";
        string customDb = "/tmp/test-logs/custom.db";
        string[] args = new string[] { "--log-file", customLog, "--log-db", customDb };

        LoggingOptions options = LoggingOptions.Parse(args);

        Assert.Equal(customLog, options.LogFilePath);
        Assert.Equal(customDb, options.SqliteDbPath);
        Assert.True(options.EnableFile);
        Assert.True(options.EnableSqlite);
    }

    [Fact]
    public void TryParseLogLevel_InvalidInput_ReturnsFalse()
    {
        bool nullResult = LoggingOptions.TryParseLogLevel(null, out _);
        bool emptyResult = LoggingOptions.TryParseLogLevel("   ", out _);
        bool invalidResult = LoggingOptions.TryParseLogLevel("nonexistent_level", out _);

        Assert.False(nullResult);
        Assert.False(emptyResult);
        Assert.False(invalidResult);
    }
}
