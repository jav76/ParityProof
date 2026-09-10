using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Engine.Cache;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class SqliteIndexCacheTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly SqliteIndexCache _cache;

    public SqliteIndexCacheTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "ParityProof_CacheTest_" + Guid.NewGuid().ToString("N") + ".db");
        _cache = new SqliteIndexCache(_dbPath);
    }

    [Fact]
    public async Task UpsertAndGet_PersistsAndRetrievesCachedFile()
    {
        DateTime now = DateTime.UtcNow;
        MediaFile file = new(
            RelativePath: "DCIM/100CANON/IMG_0001.CR3",
            FullPath: "/media/card/DCIM/100CANON/IMG_0001.CR3",
            FileLength: 45_000_000,
            LastWriteTimeUtc: now,
            Category: MediaCategory.PhotoRaw,
            HeadHash: 123456789UL,
            TailHash: 987654321UL,
            DeepHash: 777777777UL,
            FullHash: 555555555UL);

        await _cache.UpsertBatchAsync(new[] { file });

        MediaFile? retrieved = await _cache.GetAsync(file.FullPath, file.FileLength, now);

        Assert.NotNull(retrieved);
        Assert.Equal(file.FullPath, retrieved.FullPath);
        Assert.Equal(file.FileLength, retrieved.FileLength);
        Assert.Equal(file.HeadHash, retrieved.HeadHash);
        Assert.Equal(file.TailHash, retrieved.TailHash);
        Assert.Equal(file.DeepHash, retrieved.DeepHash);
        Assert.Equal(file.FullHash, retrieved.FullHash);

        IReadOnlyList<MediaFile> byLength = await _cache.GetByLengthAsync(file.FileLength);
        Assert.Single(byLength);
        Assert.Equal(file.FullPath, byLength[0].FullPath);
        Assert.Equal(file.DeepHash, byLength[0].DeepHash);
    }

    [Fact]
    public async Task GetBatchAsync_ResolvesCachedHashesInBulk()
    {
        DateTime now = DateTime.UtcNow;
        List<MediaFile> toInsert = new();
        for (int i = 0; i < 50; i++)
        {
            toInsert.Add(new MediaFile(
                RelativePath: $"photo_{i}.jpg",
                FullPath: $"/storage/dest/photo_{i}.jpg",
                FileLength: 1000 + i,
                LastWriteTimeUtc: now,
                Category: MediaCategory.PhotoStandard,
                HeadHash: (ulong)(100 + i),
                TailHash: (ulong)(200 + i),
                DeepHash: (ulong)(250 + i),
                FullHash: (ulong)(300 + i)));
        }

        await _cache.UpsertBatchAsync(toInsert);

        List<MediaFile> queryFiles = new(toInsert);
        queryFiles.Add(new MediaFile(
            RelativePath: "not_cached.jpg",
            FullPath: "/storage/dest/not_cached.jpg",
            FileLength: 9999,
            LastWriteTimeUtc: now,
            Category: MediaCategory.PhotoStandard));

        IReadOnlyDictionary<string, MediaFile> batchResult = await _cache.GetBatchAsync(queryFiles);

        Assert.Equal(50, batchResult.Count);
        Assert.False(batchResult.ContainsKey("/storage/dest/not_cached.jpg"));

        for (int i = 0; i < 50; i++)
        {
            string path = $"/storage/dest/photo_{i}.jpg";
            Assert.True(batchResult.TryGetValue(path, out MediaFile? cached));
            Assert.NotNull(cached);
            Assert.Equal((ulong)(100 + i), cached.HeadHash);
            Assert.Equal((ulong)(200 + i), cached.TailHash);
            Assert.Equal((ulong)(250 + i), cached.DeepHash);
            Assert.Equal((ulong)(300 + i), cached.FullHash);
        }
    }

    [Fact]
    public async Task GetBatchAsync_HandlesLargeBatchAcrossChunkBoundaries()
    {
        DateTime now = DateTime.UtcNow;
        List<MediaFile> largeBatch = new();
        for (int i = 0; i < 600; i++)
        {
            largeBatch.Add(new MediaFile(
                RelativePath: $"img_{i}.cr3",
                FullPath: $"/media/card/img_{i}.cr3",
                FileLength: 5000 + i,
                LastWriteTimeUtc: now,
                Category: MediaCategory.PhotoRaw,
                HeadHash: (ulong)(1000 + i),
                TailHash: (ulong)(2000 + i),
                DeepHash: (ulong)(2500 + i),
                FullHash: (ulong)(3000 + i)));
        }

        await _cache.UpsertBatchAsync(largeBatch);

        IReadOnlyDictionary<string, MediaFile> results = await _cache.GetBatchAsync(largeBatch);
        Assert.Equal(600, results.Count);
    }

    [Fact]
    public async Task EnsureInitializedAsync_MigratesLegacyDatabaseWithoutDeepHashColumn()
    {
        string legacyDbPath = Path.Combine(Path.GetTempPath(), "ParityProof_LegacyTest_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // Create legacy database without DeepHash column
            await using (Microsoft.Data.Sqlite.SqliteConnection conn = new($"Data Source={legacyDbPath}"))
            {
                await conn.OpenAsync();
                await using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE MediaCache (
                        FilePath TEXT PRIMARY KEY,
                        RelativePath TEXT NOT NULL,
                        FileLength INTEGER NOT NULL,
                        LastWriteTimeUtc INTEGER NOT NULL,
                        Category INTEGER NOT NULL,
                        HeadHash INTEGER,
                        TailHash INTEGER,
                        FullHash INTEGER
                    );
                    INSERT INTO MediaCache VALUES (
                        '/legacy/file.jpg', 'file.jpg', 12345, 67890, 0, 111, 222, 333
                    );";
                await cmd.ExecuteNonQueryAsync();
            }

            // Open with SqliteIndexCache which should automatically execute the ALTER TABLE migration
            SqliteIndexCache legacyCache = new(legacyDbPath);
            await using (legacyCache)
            {
                MediaFile? existing = await legacyCache.GetAsync("/legacy/file.jpg", 12345, new DateTime(67890, DateTimeKind.Utc));
                Assert.NotNull(existing);
                Assert.Null(existing.DeepHash);
                Assert.Equal(111UL, existing.HeadHash);

                // Now upsert with DeepHash
                MediaFile updated = existing with { DeepHash = 999999UL };
                await legacyCache.UpsertBatchAsync(new[] { updated });

                MediaFile? reloaded = await legacyCache.GetAsync("/legacy/file.jpg", 12345, new DateTime(67890, DateTimeKind.Utc));
                Assert.NotNull(reloaded);
                Assert.Equal(999999UL, reloaded.DeepHash);
            }
        }
        finally
        {
            if (File.Exists(legacyDbPath))
            {
                File.Delete(legacyDbPath);
            }
        }
    }

    [Fact]
    public async Task UpsertBatchAsync_HandlesConcurrentWritesGracefully()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), "ParityProof_ConcurrentTest_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            List<Task> concurrentUpsertTasks = new();
            const int TASK_COUNT = 5;
            const int FILES_PER_TASK = 20;

            for (int t = 0; t < TASK_COUNT; t++)
            {
                int taskId = t;
                concurrentUpsertTasks.Add(Task.Run(async () =>
                {
                    await using SqliteIndexCache localCache = new(dbPath);
                    List<MediaFile> files = new();
                    for (int f = 0; f < FILES_PER_TASK; f++)
                    {
                        files.Add(new MediaFile(
                            RelativePath: $"task_{taskId}/file_{f}.jpg",
                            FullPath: $"/media/task_{taskId}/file_{f}.jpg",
                            FileLength: 1000 + f,
                            LastWriteTimeUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            Category: MediaCategory.PhotoStandard,
                            HeadHash: (ulong)(taskId * 100 + f)));
                    }

                    await localCache.UpsertBatchAsync(files);
                }));
            }

            await Task.WhenAll(concurrentUpsertTasks);

            await using SqliteIndexCache verifyCache = new(dbPath);
            for (int t = 0; t < TASK_COUNT; t++)
            {
                for (int f = 0; f < FILES_PER_TASK; f++)
                {
                    MediaFile? retrieved = await verifyCache.GetAsync(
                        $"/media/task_{t}/file_{f}.jpg",
                        1000 + f,
                        new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                    Assert.NotNull(retrieved);
                    Assert.Equal((ulong)(t * 100 + f), retrieved.HeadHash);
                }
            }
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cache.DisposeAsync();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // Ignore file deletion error during test cleanup
        }
    }
}
