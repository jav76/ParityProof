using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Models;

namespace ParityProof.Core.Interfaces;

public interface IReportGenerator
{
    string FileExtension { get; }
    string DisplayName { get; }

    Task GenerateReportAsync(
        VerificationSummary summary,
        IReadOnlyList<VerificationResultItem> results,
        string outputPath,
        CancellationToken cancellationToken = default);
}
