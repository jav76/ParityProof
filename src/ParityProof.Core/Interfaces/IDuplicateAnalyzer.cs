using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Core.Threading;

namespace ParityProof.Core.Interfaces;

public interface IDuplicateAnalyzer
{
    Task<DuplicateAnalysisResult> AnalyzeDuplicatesAsync(
        IReadOnlyList<MediaFile> sourceFiles,
        IReadOnlyList<BackupDestination> destinations,
        IReadOnlyDictionary<string, IReadOnlyList<MediaFile>> destinationFiles,
        VerificationMode mode,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default);
}
