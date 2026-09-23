using System;
using System.IO;
using Serilog.Events;

namespace ParityProof.Core.Logging;

public sealed class LoggingOptions
{
    private const string DEFAULT_APP_FOLDER = "ParityProof";
    private const string DEFAULT_LOG_SUBFOLDER = "logs";
    private const string DEFAULT_LOG_FILENAME = "parityproof-.log";
    private const string DEFAULT_DB_FILENAME = "logs.db";
    private const string DEFAULT_EVENT_LOG_SOURCE = "ParityProof";
    private const string DEFAULT_EVENT_LOG_NAME = "Application";

#if DEBUG
    public const LogEventLevel DEFAULT_LOG_LEVEL = LogEventLevel.Debug;
    public const StackTracePolicy DEFAULT_EVENT_LOG_STACK_TRACE_POLICY = StackTracePolicy.Full;
#else
    public const LogEventLevel DEFAULT_LOG_LEVEL = LogEventLevel.Warning;
    public const StackTracePolicy DEFAULT_EVENT_LOG_STACK_TRACE_POLICY = StackTracePolicy.Sanitized;
#endif
    public const LogEventLevel DEFAULT_EVENT_LOG_LEVEL = LogEventLevel.Warning;

    public LogEventLevel MinimumLevel { get; set; } = DEFAULT_LOG_LEVEL;

    public bool EnableFile { get; set; } = true;

    public string? LogFilePath { get; set; }

    public bool EnableSqlite { get; set; } = true;

    public string? SqliteDbPath { get; set; }

    public bool EnableConsole { get; set; } = true;

    public bool EnableEventLog { get; set; } = true;

    public string EventLogSource { get; set; } = DEFAULT_EVENT_LOG_SOURCE;

    public string EventLogName { get; set; } = DEFAULT_EVENT_LOG_NAME;

    public LogEventLevel EventLogMinimumLevel { get; set; } = DEFAULT_EVENT_LOG_LEVEL;

    public StackTracePolicy EventLogStackTracePolicy { get; set; } = DEFAULT_EVENT_LOG_STACK_TRACE_POLICY;

    public static string GetDefaultLogDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, DEFAULT_APP_FOLDER, DEFAULT_LOG_SUBFOLDER);
    }

    public static string GetDefaultLogFilePath()
    {
        string logDir = GetDefaultLogDirectory();
        return Path.Combine(logDir, DEFAULT_LOG_FILENAME);
    }

    public static string GetDefaultSqliteDbPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, DEFAULT_APP_FOLDER, DEFAULT_DB_FILENAME);
    }

    public static LoggingOptions Parse(string[]? args)
    {
        LoggingOptions options = new();

        if (args is null || args.Length == 0)
        {
            return options;
        }

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (arg.Equals("-v", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase))
            {
                options.MinimumLevel = LogEventLevel.Debug;
                continue;
            }

            if (arg.Equals("--no-log-file", StringComparison.OrdinalIgnoreCase))
            {
                options.EnableFile = false;
                continue;
            }

            if (arg.Equals("--no-log-db", StringComparison.OrdinalIgnoreCase))
            {
                options.EnableSqlite = false;
                continue;
            }

            if (arg.Equals("--no-log-console", StringComparison.OrdinalIgnoreCase))
            {
                options.EnableConsole = false;
                continue;
            }

            if (TryParseOption(arg, "--log-level", "-l", args, ref i, out string? levelString))
            {
                if (TryParseLogLevel(levelString, out LogEventLevel parsedLevel))
                {
                    options.MinimumLevel = parsedLevel;
                }
                continue;
            }

            if (TryParseOption(arg, "--log-file", null, args, ref i, out string? filePath))
            {
                options.LogFilePath = filePath;
                options.EnableFile = true;
                continue;
            }

            if (TryParseOption(arg, "--log-db", null, args, ref i, out string? dbPath))
            {
                options.SqliteDbPath = dbPath;
                options.EnableSqlite = true;
                continue;
            }

            if (arg.Equals("--no-log-eventlog", StringComparison.OrdinalIgnoreCase))
            {
                options.EnableEventLog = false;
                continue;
            }

            if (arg.Equals("--log-eventlog", StringComparison.OrdinalIgnoreCase))
            {
                options.EnableEventLog = true;
                continue;
            }

            if (TryParseOption(arg, "--event-log-source", null, args, ref i, out string? eventSource))
            {
                if (!string.IsNullOrWhiteSpace(eventSource))
                {
                    options.EventLogSource = eventSource;
                    options.EnableEventLog = true;
                }
                continue;
            }

            if (TryParseOption(arg, "--event-log-name", null, args, ref i, out string? eventLogName))
            {
                if (!string.IsNullOrWhiteSpace(eventLogName))
                {
                    options.EventLogName = eventLogName;
                    options.EnableEventLog = true;
                }
                continue;
            }

            if (TryParseOption(arg, "--event-log-level", null, args, ref i, out string? eventLogLevelString))
            {
                if (TryParseLogLevel(eventLogLevelString, out LogEventLevel parsedEventLevel))
                {
                    options.EventLogMinimumLevel = parsedEventLevel;
                    options.EnableEventLog = true;
                }
                continue;
            }

            if (TryParseOption(arg, "--event-log-stack-trace", null, args, ref i, out string? stackTracePolicyString))
            {
                if (TryParseStackTracePolicy(stackTracePolicyString, out StackTracePolicy parsedPolicy))
                {
                    options.EventLogStackTracePolicy = parsedPolicy;
                    options.EnableEventLog = true;
                }
                continue;
            }
        }

        return options;
    }

    private static bool TryParseOption(
        string currentArg,
        string longName,
        string? shortName,
        string[] allArgs,
        ref int index,
        out string? value)
    {
        value = null;

        if (currentArg.StartsWith(longName + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = currentArg[(longName.Length + 1)..];
            return true;
        }

        if (shortName is not null && currentArg.StartsWith(shortName + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = currentArg[(shortName.Length + 1)..];
            return true;
        }

        if (currentArg.Equals(longName, StringComparison.OrdinalIgnoreCase) ||
            (shortName is not null && currentArg.Equals(shortName, StringComparison.OrdinalIgnoreCase)))
        {
            if (index + 1 < allArgs.Length && !allArgs[index + 1].StartsWith("-"))
            {
                index++;
                value = allArgs[index];
                return true;
            }
        }

        return false;
    }

    public static bool TryParseLogLevel(string? value, out LogEventLevel level)
    {
        level = DEFAULT_LOG_LEVEL;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "verbose":
            case "trace":
                level = LogEventLevel.Verbose;
                return true;

            case "debug":
                level = LogEventLevel.Debug;
                return true;

            case "info":
            case "information":
                level = LogEventLevel.Information;
                return true;

            case "warn":
            case "warning":
                level = LogEventLevel.Warning;
                return true;

            case "error":
                level = LogEventLevel.Error;
                return true;

            case "fatal":
            case "critical":
                level = LogEventLevel.Fatal;
                return true;

            default:
                return false;
        }
    }

    public static bool TryParseStackTracePolicy(string? value, out StackTracePolicy policy)
    {
        policy = DEFAULT_EVENT_LOG_STACK_TRACE_POLICY;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "sanitized":
            case "sanitize":
            case "masked":
                policy = StackTracePolicy.Sanitized;
                return true;

            case "full":
            case "raw":
            case "all":
                policy = StackTracePolicy.Full;
                return true;

            case "none":
            case "omit":
            case "disabled":
            case "off":
                policy = StackTracePolicy.None;
                return true;

            default:
                return false;
        }
    }
}
