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

    [Fact]
    public void Parse_EventLogDefaults_AreProperlySet()
    {
        LoggingOptions options = LoggingOptions.Parse(Array.Empty<string>());

        Assert.True(options.EnableEventLog);
        Assert.Equal("ParityProof", options.EventLogSource);
        Assert.Equal("Application", options.EventLogName);
        Assert.Equal(LogEventLevel.Warning, options.EventLogMinimumLevel);
#if DEBUG
        Assert.Equal(StackTracePolicy.Full, options.EventLogStackTracePolicy);
#else
        Assert.Equal(StackTracePolicy.Sanitized, options.EventLogStackTracePolicy);
#endif
    }

    [Fact]
    public void Parse_DisableEventLogFlag_DisablesEventLog()
    {
        string[] args = new string[] { "--no-log-eventlog" };
        LoggingOptions options = LoggingOptions.Parse(args);

        Assert.False(options.EnableEventLog);
    }

    [Fact]
    public void Parse_EnableEventLogFlag_EnablesEventLog()
    {
        string[] args = new string[] { "--no-log-eventlog", "--log-eventlog" };
        LoggingOptions options = LoggingOptions.Parse(args);

        Assert.True(options.EnableEventLog);
    }

    [Theory]
    [InlineData("--event-log-source", "CustomSource", "CustomSource")]
    [InlineData("--event-log-source=CustomSource", "", "CustomSource")]
    public void Parse_EventLogSource_SetsExpectedSource(string flag, string value, string expected)
    {
        string[] args = string.IsNullOrEmpty(value) ? new string[] { flag } : new string[] { flag, value };
        LoggingOptions options = LoggingOptions.Parse(args);

        Assert.Equal(expected, options.EventLogSource);
        Assert.True(options.EnableEventLog);
    }

    [Theory]
    [InlineData("--event-log-name", "CustomLog", "CustomLog")]
    [InlineData("--event-log-name=CustomLog", "", "CustomLog")]
    public void Parse_EventLogName_SetsExpectedLogName(string flag, string value, string expected)
    {
        string[] args = string.IsNullOrEmpty(value) ? new string[] { flag } : new string[] { flag, value };
        LoggingOptions options = LoggingOptions.Parse(args);

        Assert.Equal(expected, options.EventLogName);
        Assert.True(options.EnableEventLog);
    }

    [Theory]
    [InlineData("--event-log-level", "error", LogEventLevel.Error)]
    [InlineData("--event-log-level=fatal", "", LogEventLevel.Fatal)]
    [InlineData("--event-log-level", "info", LogEventLevel.Information)]
    public void Parse_EventLogLevel_SetsExpectedLevel(string flag, string value, LogEventLevel expected)
    {
        string[] args = string.IsNullOrEmpty(value) ? new string[] { flag } : new string[] { flag, value };
        LoggingOptions options = LoggingOptions.Parse(args);

        Assert.Equal(expected, options.EventLogMinimumLevel);
        Assert.True(options.EnableEventLog);
    }

    [Theory]
    [InlineData("--event-log-stack-trace", "sanitized", StackTracePolicy.Sanitized)]
    [InlineData("--event-log-stack-trace=full", "", StackTracePolicy.Full)]
    [InlineData("--event-log-stack-trace", "none", StackTracePolicy.None)]
    [InlineData("--event-log-stack-trace", "omit", StackTracePolicy.None)]
    public void Parse_EventLogStackTracePolicy_SetsExpectedPolicy(string flag, string value, StackTracePolicy expected)
    {
        string[] args = string.IsNullOrEmpty(value) ? new string[] { flag } : new string[] { flag, value };
        LoggingOptions options = LoggingOptions.Parse(args);

        Assert.Equal(expected, options.EventLogStackTracePolicy);
        Assert.True(options.EnableEventLog);
    }

    [Theory]
    [InlineData("sanitized", StackTracePolicy.Sanitized)]
    [InlineData("sanitize", StackTracePolicy.Sanitized)]
    [InlineData("masked", StackTracePolicy.Sanitized)]
    [InlineData("full", StackTracePolicy.Full)]
    [InlineData("raw", StackTracePolicy.Full)]
    [InlineData("all", StackTracePolicy.Full)]
    [InlineData("none", StackTracePolicy.None)]
    [InlineData("omit", StackTracePolicy.None)]
    [InlineData("disabled", StackTracePolicy.None)]
    [InlineData("off", StackTracePolicy.None)]
    public void TryParseStackTracePolicy_ValidInput_ReturnsTrue(string input, StackTracePolicy expected)
    {
        bool result = LoggingOptions.TryParseStackTracePolicy(input, out StackTracePolicy parsed);

        Assert.True(result);
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown_policy")]
    public void TryParseStackTracePolicy_InvalidInput_ReturnsFalse(string? input)
    {
        bool result = LoggingOptions.TryParseStackTracePolicy(input, out _);

        Assert.False(result);
    }
}
