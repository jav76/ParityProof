using System;
using System.Collections;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ParityProof.Core.Logging;

public static class AppLogger
{
    private const string OUTPUT_TEMPLATE =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}";
    private const int MAX_STRING_LENGTH = 256;
    private const int MAX_COLLECTION_ITEMS = 8;
    private const string SQLITE_TABLE_NAME = "Logs";
    private const int DEFAULT_RETAINED_FILE_COUNT = 14;

    private static readonly LoggingLevelSwitch _levelSwitch = new();

    public static ILogger Logger => Log.Logger;

    public static LoggingLevelSwitch LevelSwitch => _levelSwitch;

    public static void Initialize(LoggingOptions options)
    {
        _levelSwitch.MinimumLevel = options.MinimumLevel;

        LoggerConfiguration config = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_levelSwitch);

        if (options.EnableConsole)
        {
            config.WriteTo.Console(
                outputTemplate: OUTPUT_TEMPLATE,
                restrictedToMinimumLevel: options.MinimumLevel);
        }

        if (options.EnableFile)
        {
            string filePath = options.LogFilePath ?? LoggingOptions.GetDefaultLogFilePath();
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            config.WriteTo.File(
                filePath,
                restrictedToMinimumLevel: options.MinimumLevel,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: DEFAULT_RETAINED_FILE_COUNT,
                outputTemplate: OUTPUT_TEMPLATE);
        }

        if (options.EnableSqlite)
        {
            string dbPath = options.SqliteDbPath ?? LoggingOptions.GetDefaultSqliteDbPath();
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            config.WriteTo.SQLite(
                sqliteDbPath: dbPath,
                tableName: SQLITE_TABLE_NAME,
                restrictedToMinimumLevel: options.MinimumLevel);
        }

        Log.Logger = config.CreateLogger();
    }

    public static ILogger ForContext<T>() => Log.ForContext<T>();

    public static ILogger ForContext(Type type) => Log.ForContext(type);

    public static bool IsEnabled(LogEventLevel level) => Log.IsEnabled(level);

    public static void CloseAndFlush()
    {
        Log.CloseAndFlush();
    }

    public static string FormatValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is byte[] bytes)
        {
            return $"<byte[{bytes.Length}]>";
        }

        if (value is ReadOnlyMemory<byte> rom)
        {
            return $"<ReadOnlyMemory<byte>[{rom.Length}]>";
        }

        if (value is Memory<byte> mem)
        {
            return $"<Memory<byte>[{mem.Length}]>";
        }

        if (value is string str)
        {
            if (str.Length > MAX_STRING_LENGTH)
            {
                return $"\"{str[..MAX_STRING_LENGTH]}... (length: {str.Length})\"";
            }
            return $"\"{str}\"";
        }

        if (value is IEnumerable enumerable and not string)
        {
            System.Text.StringBuilder builder = new();
            builder.Append('[');
            int count = 0;
            bool truncated = false;

            foreach (object? item in enumerable)
            {
                if (count >= MAX_COLLECTION_ITEMS)
                {
                    truncated = true;
                    break;
                }

                if (count > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(FormatValue(item));
                count++;
            }

            if (truncated)
            {
                builder.Append(", ... (truncated)");
            }

            builder.Append(']');
            return builder.ToString();
        }

        return value.ToString() ?? value.GetType().Name;
    }
}
