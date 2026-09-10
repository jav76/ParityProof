using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Models;

namespace ParityProof.Engine.Reporting;

public sealed class CsvReportGenerator : IReportGenerator
{
    public string FileExtension => ".csv";
    public string DisplayName => "CSV Spreadsheet";

    public async Task GenerateReportAsync(
        VerificationSummary summary,
        IReadOnlyList<VerificationResultItem> results,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        StringBuilder sb = new();
        sb.AppendLine("RelativePath,FileSizeBytes,Category,OverallStatus,HeadHash,TailHash,DeepHash,FullHash,DestinationStatuses");

        foreach (VerificationResultItem item in results)
        {
            string overall = item.IsFullyVerified
                ? "Verified"
                : item.IsPartiallyVerified
                    ? "Partial"
                    : item.HasAnyCorruption
                        ? "Corrupt"
                        : "Missing";

            List<string> destPairs = new();
            foreach (KeyValuePair<string, FileMatchStatus> kvp in item.DestinationStatuses)
            {
                destPairs.Add($"{kvp.Key}:{kvp.Value.Status}");
            }
            string destsFormatted = string.Join(";", destPairs);

            sb.AppendLine($"\"{EscapeCsv(item.SourceFile.RelativePath)}\",{item.SourceFile.FileLength},{item.SourceFile.Category},{overall},{item.SourceFile.HeadHash},{item.SourceFile.TailHash},{item.SourceFile.DeepHash},{item.SourceFile.FullHash},\"{EscapeCsv(destsFormatted)}\"");
        }

        if (summary.DuplicateAnalysis is not null && summary.DuplicateAnalysis.Groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Destination,RelativePath,FileSizeBytes,IsPrimary,GroupKey,ReclaimableGroupBytes,RedundancyType");
            foreach (DuplicateGroup group in summary.DuplicateAnalysis.Groups)
            {
                string redundancyType = group.IsIntraDestination && group.IsCrossDestination
                    ? "Same-Drive and Multi-Drive"
                    : group.IsIntraDestination
                        ? "Same-Drive Duplicate"
                        : "Multi-Drive Redundant";

                foreach (DuplicateFileItem file in group.Files)
                {
                    sb.AppendLine($"\"{EscapeCsv(file.DestinationName)}\",\"{EscapeCsv(file.RelativePath)}\",{group.FileSize},{file.IsPrimary},\"{group.GroupKey}\",{group.ReclaimableBytes},\"{redundancyType}\"");
                }
            }
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static string EscapeCsv(string text)
    {
        return text.Replace("\"", "\"\"");
    }
}
