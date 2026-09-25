using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Models;

namespace ParityProof.Engine.Reporting;

public sealed class CsvReportGenerator : IReportGenerator
{
    private const string FORMULA_TRIGGER_CHARACTERS = "=+-@\t\r";
    private const char FORMULA_ESCAPE_PREFIX = '\'';
    private const string FILE_SECTION_HEADER =
        "RelativePath,FileSizeBytes,Category,OverallStatus,HeadHash,TailHash,DeepHash,FullHash,DestinationStatuses";

    public string FileExtension => ".csv";
    public string DisplayName => "CSV Spreadsheet";

    public async Task GenerateReportAsync(
        VerificationSummary summary,
        IReadOnlyList<VerificationResultItem> results,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        StringBuilder sb = new();
        IReadOnlyList<ReportDestination> destinations = ReportFormatting.CollectDestinations(results);

        // Root paths may contain ':' and ';', so each destination also gets an unambiguous status column.
        List<string> headerFields = new() { FILE_SECTION_HEADER };
        foreach (ReportDestination destination in destinations)
        {
            headerFields.Add(FormatCsvField(destination.Label));
        }
        sb.AppendLine(string.Join(",", headerFields));

        foreach (VerificationResultItem item in results)
        {
            List<string> destPairs = new();
            foreach (FileMatchStatus status in item.DestinationStatuses.Values)
            {
                destPairs.Add($"{ReportFormatting.GetDestinationLabel(status)}:{status.Status}");
            }
            string destsFormatted = string.Join(";", destPairs);

            MediaFile file = item.SourceFile;
            List<string> fields = new()
            {
                FormatCsvField(file.RelativePath),
                file.FileLength.ToString(CultureInfo.InvariantCulture),
                file.Category.ToString(),
                item.OverallStatus.ToString(),
                ReportFormatting.FormatHash(file.HeadHash),
                ReportFormatting.FormatHash(file.TailHash),
                ReportFormatting.FormatHash(file.DeepHash),
                ReportFormatting.FormatHash(file.FullHash),
                FormatCsvField(destsFormatted),
            };
            foreach (ReportDestination destination in destinations)
            {
                fields.Add(item.DestinationStatuses.TryGetValue(destination.Id, out FileMatchStatus? destinationStatus)
                    ? destinationStatus.Status.ToString()
                    : string.Empty);
            }
            sb.AppendLine(string.Join(",", fields));
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
                    string[] fields =
                    {
                        FormatCsvField(file.DestinationName),
                        FormatCsvField(file.RelativePath),
                        group.FileSize.ToString(CultureInfo.InvariantCulture),
                        file.IsPrimary.ToString(),
                        FormatCsvField(group.GroupKey),
                        group.ReclaimableBytes.ToString(CultureInfo.InvariantCulture),
                        FormatCsvField(redundancyType),
                    };
                    sb.AppendLine(string.Join(",", fields));
                }
            }
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    // Spreadsheet apps evaluate quoted fields that start with a formula trigger, so those get a leading apostrophe.
    private static string FormatCsvField(string text)
    {
        string safeText = text.Length > 0 && FORMULA_TRIGGER_CHARACTERS.Contains(text[0])
            ? FORMULA_ESCAPE_PREFIX + text
            : text;
        return $"\"{safeText.Replace("\"", "\"\"")}\"";
    }
}
