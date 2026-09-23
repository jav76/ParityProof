using System;
using System.IO;
using ParityProof.Core.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace ParityProof.Engine.Tests.Logging;

public sealed class EventLogMessageFormatterTests
{
    [Fact]
    public void Format_WithoutException_RendersExpectedHeaderAndMessage()
    {
        EventLogMessageFormatter formatter = new(StackTracePolicy.Sanitized);
        StringWriter writer = new();

        MessageTemplateParser parser = new();
        MessageTemplate template = parser.Parse("Ingest batch started for {ItemCount} items");
        LogEventProperty property = new("ItemCount", new ScalarValue(42));
        LogEvent logEvent = new(
            DateTimeOffset.Now,
            LogEventLevel.Information,
            exception: null,
            template,
            new LogEventProperty[] { property });

        formatter.Format(logEvent, writer);

        string output = writer.ToString();
        Assert.Contains("[ParityProof]", output);
        Assert.Contains("[Information]", output);
        Assert.Contains("Ingest batch started for 42 items", output);
        Assert.Contains("ItemCount: 42", output);
        Assert.DoesNotContain("Exception Details:", output);
    }

    [Fact]
    public void Format_WithException_RendersExceptionDetails()
    {
        EventLogMessageFormatter formatter = new(StackTracePolicy.Sanitized);
        StringWriter writer = new();

        InvalidOperationException ex = new(@"Failed reading C:\Users\Alice\Secret.txt");
        MessageTemplateParser parser = new();
        MessageTemplate template = parser.Parse("Fatal crash occurred");
        LogEvent logEvent = new(
            DateTimeOffset.Now,
            LogEventLevel.Fatal,
            ex,
            template,
            Array.Empty<LogEventProperty>());

        formatter.Format(logEvent, writer);

        string output = writer.ToString();
        Assert.Contains("[ParityProof]", output);
        Assert.Contains("[Fatal]", output);
        Assert.Contains("Fatal crash occurred", output);
        Assert.Contains("================================================================================", output);
        Assert.Contains("Exception Details:", output);
        Assert.Contains("Type: System.InvalidOperationException", output);
        Assert.Contains(@"Failed reading %USERPROFILE%\Secret.txt", output);
        Assert.DoesNotContain(@"C:\Users\Alice", output);
    }

    [Fact]
    public void Format_WithSensitiveProperty_SanitizesPropertyValue()
    {
        EventLogMessageFormatter formatter = new(StackTracePolicy.Sanitized);
        StringWriter writer = new();

        MessageTemplateParser parser = new();
        MessageTemplate template = parser.Parse("Processing file");
        LogEventProperty property = new("TargetDirectory", new ScalarValue(@"C:\Users\JohnDoe\Photos"));
        LogEvent logEvent = new(
            DateTimeOffset.Now,
            LogEventLevel.Warning,
            exception: null,
            template,
            new LogEventProperty[] { property });

        formatter.Format(logEvent, writer);

        string output = writer.ToString();
        Assert.Contains(@"TargetDirectory: ""%USERPROFILE%\Photos""", output);
        Assert.DoesNotContain(@"C:\Users\JohnDoe", output);
    }

    [Fact]
    public void Format_NullArguments_ThrowsArgumentNullException()
    {
        EventLogMessageFormatter formatter = new(StackTracePolicy.Sanitized);
        StringWriter writer = new();

        Assert.Throws<ArgumentNullException>(() => formatter.Format(null!, writer));

        MessageTemplateParser parser = new();
        MessageTemplate template = parser.Parse("Test");
        LogEvent logEvent = new(
            DateTimeOffset.Now,
            LogEventLevel.Information,
            exception: null,
            template,
            Array.Empty<LogEventProperty>());

        Assert.Throws<ArgumentNullException>(() => formatter.Format(logEvent, null!));
    }

    [Fact]
    public void Format_OversizedPayload_TruncatesSafely()
    {
        EventLogMessageFormatter formatter = new(StackTracePolicy.Sanitized);
        StringWriter writer = new();

        string hugeString = new('X', 40000);
        MessageTemplateParser parser = new();
        MessageTemplate template = parser.Parse(hugeString);
        LogEvent logEvent = new(
            DateTimeOffset.Now,
            LogEventLevel.Error,
            exception: null,
            template,
            Array.Empty<LogEventProperty>());

        formatter.Format(logEvent, writer);

        string output = writer.ToString();
        Assert.True(output.Length <= 31000, $"Expected length <= 31000, got {output.Length}");
        Assert.Contains("... [Payload truncated due to Windows Event Log size limit]", output);
    }
}
