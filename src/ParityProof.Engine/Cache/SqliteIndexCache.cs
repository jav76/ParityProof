using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Logging;
using ParityProof.Core.Models;
using Microsoft.Data.Sqlite;

namespace ParityProof.Engine.Cache;

[LogMethod]
public sealed class SqliteIndexCache : IIndexCache
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _initialized;

    public SqliteIndexCache(string? dbPath = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string baseDir = Path.Combine(appData, "ParityProof");
            Directory.CreateDirectory(baseDir);
            dbPath = Path.Combine(baseDir, "cache_index.db");
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        _connectionString = builder.ConnectionString;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using SqliteConnection connection = new(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            await pragmaCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand tableCmd = connection.CreateCommand();
            tableCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS MediaCache (
                    FilePath TEXT PRIMARY KEY,
                    RelativePath TEXT NOT NULL,
                    FileLength INTEGER NOT NULL,
                    LastWriteTimeUtc INTEGER NOT NULL,
                    Category INTEGER NOT NULL,
                    HeadHash INTEGER,
                    TailHash INTEGER,
                    FullHash INTEGER
                );
                CREATE INDEX IF NOT EXISTS IX_MediaCache_Length ON MediaCache(FileLength);";
            await tableCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<MediaFile?> GetAsync(
        string filePath,
        long fileLength,
        DateTime lastWriteTimeUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"
            SELECT RelativePath, Category, HeadHash, TailHash, FullHash
            FROM MediaCache
            WHERE FilePath = @path AND FileLength = @length AND LastWriteTimeUtc = @lastWrite;";

        command.Parameters.AddWithValue("@path", filePath);
        command.Parameters.AddWithValue("@length", fileLength);
        command.Parameters.AddWithValue("@lastWrite", lastWriteTimeUtc.Ticks);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string relativePath = reader.GetString(0);
            MediaCategory category = (MediaCategory)reader.GetInt32(1);
            ulong? headHash = reader.IsDBNull(2) ? null : (ulong)reader.GetInt64(2);
            ulong? tailHash = reader.IsDBNull(3) ? null : (ulong)reader.GetInt64(3);
            ulong? fullHash = reader.IsDBNull(4) ? null : (ulong)reader.GetInt64(4);

            return new MediaFile(
                RelativePath: relativePath,
                FullPath: filePath,
                FileLength: fileLength,
                LastWriteTimeUtc: lastWriteTimeUtc,
                Category: category,
                HeadHash: headHash,
                TailHash: tailHash,
                FullHash: fullHash);
        }

        return null;
    }

    public async Task<IReadOnlyDictionary<string, MediaFile>> GetBatchAsync(
        IReadOnlyList<MediaFile> files,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, MediaFile> result = new(StringComparer.OrdinalIgnoreCase);
        if (files.Count == 0)
        {
            return result;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, MediaFile> inputLookup = new(files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (MediaFile file in files)
        {
            inputLookup[file.FullPath] = file;
        }

        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const int CHUNK_SIZE = 500;
        List<MediaFile> filesList = files as List<MediaFile> ?? files.ToList();

        for (int i = 0; i < filesList.Count; i += CHUNK_SIZE)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentBatchCount = Math.Min(CHUNK_SIZE, filesList.Count - i);

            await using SqliteCommand command = connection.CreateCommand();
            List<string> parameterNames = new(currentBatchCount);

            for (int j = 0; j < currentBatchCount; j++)
            {
                string paramName = "@p" + j;
                parameterNames.Add(paramName);
                command.Parameters.AddWithValue(paramName, filesList[i + j].FullPath);
            }

            command.CommandText = $@"
                SELECT FilePath, RelativePath, FileLength, LastWriteTimeUtc, Category, HeadHash, TailHash, FullHash
                FROM MediaCache
                WHERE FilePath IN ({string.Join(",", parameterNames)});";

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string path = reader.GetString(0);
                if (inputLookup.TryGetValue(path, out MediaFile? candidate))
                {
                    long length = reader.GetInt64(2);
                    long lastWriteTicks = reader.GetInt64(3);

                    if (candidate.FileLength == length && candidate.LastWriteTimeUtc.Ticks == lastWriteTicks)
                    {
                        string relativePath = reader.GetString(1);
                        MediaCategory category = (MediaCategory)reader.GetInt32(4);
                        ulong? headHash = reader.IsDBNull(5) ? null : (ulong)reader.GetInt64(5);
                        ulong? tailHash = reader.IsDBNull(6) ? null : (ulong)reader.GetInt64(6);
                        ulong? fullHash = reader.IsDBNull(7) ? null : (ulong)reader.GetInt64(7);

                        result[path] = new MediaFile(
                            RelativePath: relativePath,
                            FullPath: path,
                            FileLength: length,
                            LastWriteTimeUtc: candidate.LastWriteTimeUtc,
                            Category: category,
                            HeadHash: headHash,
                            TailHash: tailHash,
                            FullHash: fullHash);
                    }
                }
            }
        }

        return result;
    }

    public async Task UpsertBatchAsync(
        IEnumerable<MediaFile> files,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO MediaCache (
                FilePath, RelativePath, FileLength, LastWriteTimeUtc, Category, HeadHash, TailHash, FullHash
            ) VALUES (
                @path, @relPath, @length, @lastWrite, @category, @headHash, @tailHash, @fullHash
            ) ON CONFLICT(FilePath) DO UPDATE SET
                RelativePath = excluded.RelativePath,
                FileLength = excluded.FileLength,
                LastWriteTimeUtc = excluded.LastWriteTimeUtc,
                Category = excluded.Category,
                HeadHash = excluded.HeadHash,
                TailHash = excluded.TailHash,
                FullHash = excluded.FullHash;";

        SqliteParameter pathParam = command.Parameters.Add("@path", SqliteType.Text);
        SqliteParameter relPathParam = command.Parameters.Add("@relPath", SqliteType.Text);
        SqliteParameter lengthParam = command.Parameters.Add("@length", SqliteType.Integer);
        SqliteParameter lastWriteParam = command.Parameters.Add("@lastWrite", SqliteType.Integer);
        SqliteParameter categoryParam = command.Parameters.Add("@category", SqliteType.Integer);
        SqliteParameter headHashParam = command.Parameters.Add("@headHash", SqliteType.Integer);
        SqliteParameter tailHashParam = command.Parameters.Add("@tailHash", SqliteType.Integer);
        SqliteParameter fullHashParam = command.Parameters.Add("@fullHash", SqliteType.Integer);

        foreach (MediaFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            pathParam.Value = file.FullPath;
            relPathParam.Value = file.RelativePath;
            lengthParam.Value = file.FileLength;
            lastWriteParam.Value = file.LastWriteTimeUtc.Ticks;
            categoryParam.Value = (int)file.Category;
            headHashParam.Value = file.HeadHash.HasValue ? (long)file.HeadHash.Value : DBNull.Value;
            tailHashParam.Value = file.TailHash.HasValue ? (long)file.TailHash.Value : DBNull.Value;
            fullHashParam.Value = file.FullHash.HasValue ? (long)file.FullHash.Value : DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaFile>> GetByLengthAsync(
        long fileLength,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        List<MediaFile> list = new();
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"
            SELECT FilePath, RelativePath, LastWriteTimeUtc, Category, HeadHash, TailHash, FullHash
            FROM MediaCache
            WHERE FileLength = @length;";
        command.Parameters.AddWithValue("@length", fileLength);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string filePath = reader.GetString(0);
            string relativePath = reader.GetString(1);
            long lastWriteTicks = reader.GetInt64(2);
            MediaCategory category = (MediaCategory)reader.GetInt32(3);
            ulong? headHash = reader.IsDBNull(4) ? null : (ulong)reader.GetInt64(4);
            ulong? tailHash = reader.IsDBNull(5) ? null : (ulong)reader.GetInt64(5);
            ulong? fullHash = reader.IsDBNull(6) ? null : (ulong)reader.GetInt64(6);

            list.Add(new MediaFile(
                RelativePath: relativePath,
                FullPath: filePath,
                FileLength: fileLength,
                LastWriteTimeUtc: new DateTime(lastWriteTicks, DateTimeKind.Utc),
                Category: category,
                HeadHash: headHash,
                TailHash: tailHash,
                FullHash: fullHash));
        }

        return list;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM MediaCache;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _semaphore.Dispose();
        return ValueTask.CompletedTask;
    }
}
