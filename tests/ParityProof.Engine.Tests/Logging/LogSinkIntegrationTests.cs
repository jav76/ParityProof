using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ParityProof.Core.Logging;
using Serilog;
using Serilog.Events;
using Xunit;

namespace ParityProof.Engine.Tests.Logging;

[Collection("SerilogTestCollection")]
public sealed class LogSinkIntegrationTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _dbPath;
    private readonly string _logFilePath;

    public LogSinkIntegrationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "ParityProofTestLogs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
        _dbPath = Path.Combine(_testDirectory, "test_logs.db");
        _logFilePath = Path.Combine(_testDirectory, "test_logs.log");
    }

    public void Dispose()
    {
        AppLogger.CloseAndFlush();
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task FileSink_WritesExpectedLogEntriesToFile()
    {
        LoggingOptions options = new()
        {
            MinimumLevel = LogEventLevel.Debug,
            EnableFile = true,
            LogFilePath = _logFilePath,
            EnableSqlite = false,
            EnableConsole = false
        };

        AppLogger.Initialize(options);

        string uniqueMessage = "Unique file message " + Guid.NewGuid().ToString();
        AppLogger.Logger.Information(uniqueMessage);

        AppLogger.CloseAndFlush();

        // Serilog rolling file appends date suffix if interval is Day, or writes directly
        string[] matchingFiles = Directory.GetFiles(_testDirectory, "*test_logs*");
        Assert.NotEmpty(matchingFiles);

        string content = await File.ReadAllTextAsync(matchingFiles[0]);
        Assert.Contains(uniqueMessage, content);
        Assert.Contains("INF]", content);
    }

    [Fact]
    public async Task SqliteSink_WritesExpectedLogEntriesToDatabase()
    {
        LoggingOptions options = new()
        {
            MinimumLevel = LogEventLevel.Debug,
            EnableFile = false,
            EnableSqlite = true,
            SqliteDbPath = _dbPath,
            EnableConsole = false
        };

        AppLogger.Initialize(options);

        string uniqueMessage = "Unique sqlite message " + Guid.NewGuid().ToString();
        AppLogger.Logger.Warning(uniqueMessage);

        AppLogger.CloseAndFlush();

        // Give SQLite sink a moment to flush its background worker if needed
        await Task.Delay(200);

        Assert.True(File.Exists(_dbPath), "SQLite database file should exist");

        string connectionString = $"Data Source={_dbPath};Mode=ReadOnly";
        await using SqliteConnection conn = new(connectionString);
        await conn.OpenAsync();

        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT RenderedMessage, Level FROM Logs WHERE RenderedMessage LIKE @msg";
        cmd.Parameters.AddWithValue("@msg", $"%{uniqueMessage}%");

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        bool hasRow = await reader.ReadAsync();
        Assert.True(hasRow, "Expected to find inserted log row in Logs table");

        string renderedMessage = reader.GetString(0);
        string level = reader.GetString(1);

        Assert.Contains(uniqueMessage, renderedMessage);
        Assert.Equal("Warning", level);
    }
}
