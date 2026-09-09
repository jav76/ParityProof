using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Models;

namespace ParityProof.Core.Interfaces;

public interface IIndexCache : IAsyncDisposable
{
    Task<MediaFile?> GetAsync(
        string filePath,
        long fileLength,
        DateTime lastWriteTimeUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, MediaFile>> GetBatchAsync(
        IReadOnlyList<MediaFile> files,
        CancellationToken cancellationToken = default);

    Task UpsertBatchAsync(
        IEnumerable<MediaFile> files,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaFile>> GetByLengthAsync(
        long fileLength,
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
