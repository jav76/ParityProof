using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Models;
using ParityProof.Core.Threading;

namespace ParityProof.Core.Interfaces;

public interface IMediaCopier
{
    Task<CopyBatchResult> CopyMissingFilesAsync(
        IReadOnlyList<MediaFile> missingFiles,
        string destinationRootPath,
        IProgress<CopyProgressInfo>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default);

    Task<CopyBatchResult> CopyMissingFilesAsync(
        IReadOnlyList<MediaFile> missingFiles,
        IReadOnlyList<string> destinationRootPaths,
        IProgress<CopyProgressInfo>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default);
}
