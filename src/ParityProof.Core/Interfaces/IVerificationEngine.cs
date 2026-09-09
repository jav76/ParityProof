using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Core.Threading;

namespace ParityProof.Core.Interfaces;

public interface IVerificationEngine
{
    Task<(VerificationSummary Summary, IReadOnlyList<VerificationResultItem> Results)> VerifyAsync(
        string sourcePath,
        IReadOnlyList<BackupDestination> destinations,
        VerificationMode mode,
        FilterPreset filterPreset,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default,
        bool scanDuplicates = false);
}
