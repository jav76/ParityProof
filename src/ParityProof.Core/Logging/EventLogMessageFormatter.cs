using System;
using System.IO;
using System.Text;
using Serilog.Events;
using Serilog.Formatting;

namespace ParityProof.Core.Logging;

public sealed class EventLogMessageFormatter : ITextFormatter
{
    private const int MAX_EVENT_LOG_CHARS = 31000;
    private const string APP_NAME = "ParityProof";

    private readonly StackTracePolicy _policy;

    public EventLogMessageFormatter(StackTracePolicy policy)
    {
        _policy = policy;
    }

    public StackTracePolicy Policy => _policy;

    public void Format(LogEvent logEvent, TextWriter output)
    {
        if (logEvent is null)
        {
            throw new ArgumentNullException(nameof(logEvent));
        }

        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        StringBuilder builder = new();

        builder.AppendLine($"[{APP_NAME}] [{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{logEvent.Level}]");
        builder.AppendLine();

        string renderedMessage = logEvent.RenderMessage();
        if (_policy != StackTracePolicy.Full)
        {
            renderedMessage = StackTraceSanitizer.SanitizeText(renderedMessage);
        }

        builder.AppendLine("Message:");
        builder.AppendLine(renderedMessage);

        if (logEvent.Properties.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Context Properties:");
            foreach (System.Collections.Generic.KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
            {
                string propertyValue = property.Value.ToString();
                if (_policy != StackTracePolicy.Full)
                {
                    propertyValue = StackTraceSanitizer.SanitizeText(propertyValue);
                }
                builder.AppendLine($"  {property.Key}: {propertyValue}");
            }
        }

        if (logEvent.Exception is not null)
        {
            builder.AppendLine();
            builder.AppendLine("================================================================================");
            builder.AppendLine("Exception Details:");
            builder.AppendLine(StackTraceSanitizer.FormatException(logEvent.Exception, _policy).TrimEnd());
            builder.AppendLine("================================================================================");
        }

        string result = builder.ToString();
        if (result.Length > MAX_EVENT_LOG_CHARS)
        {
            string truncationSuffix = $"{Environment.NewLine}... [Payload truncated due to Windows Event Log size limit]";
            result = result[..(MAX_EVENT_LOG_CHARS - truncationSuffix.Length)] + truncationSuffix;
        }

        output.Write(result);
    }
}
