using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Models;

namespace ParityProof.Engine.Reporting;

public sealed class HtmlReportGenerator : IReportGenerator
{
    private const string CHECKSUM_CODE_OPEN_TAG = "<code style=\"font-family: monospace; font-size: 0.85rem;\">";

    public string FileExtension => ".html";
    public string DisplayName => "HTML Audit Certificate";

    public async Task GenerateReportAsync(
        VerificationSummary summary,
        IReadOnlyList<VerificationResultItem> results,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        StringBuilder sb = new();

        string badgeBg = summary.SafetyStatus switch
        {
            OverallSafetyStatus.SafeToFormat => "#059669",
            OverallSafetyStatus.PartiallyBackedUp => "#d97706",
            OverallSafetyStatus.NoMediaFound => "#64748b",
            _ => "#dc2626"
        };

        string badgeText = summary.SafetyStatus switch
        {
            OverallSafetyStatus.SafeToFormat => "SAFE TO FORMAT - ALL MEDIA VERIFIED",
            OverallSafetyStatus.PartiallyBackedUp => "PARTIALLY BACKED UP - ATTENTION REQUIRED",
            OverallSafetyStatus.NoMediaFound => "NO MEDIA DETECTED - DO NOT FORMAT",
            _ => $"UNSAFE TO FORMAT - {summary.MissingFiles:N0} MISSING, {summary.CorruptFiles:N0} CORRUPT"
        };

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("  <title>ParityProof Audit Certificate</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0f172a; color: #f8fafc; margin: 0; padding: 2rem; }");
        sb.AppendLine("    .container { max-width: 1100px; margin: 0 auto; }");
        sb.AppendLine("    .hero-badge { padding: 1.5rem; border-radius: 8px; text-align: center; font-size: 1.5rem; font-weight: 700; letter-spacing: 0.05em; margin-bottom: 2rem; }");
        sb.AppendLine("    .meta-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 1rem; margin-bottom: 2rem; }");
        sb.AppendLine("    .card { background: #1e293b; padding: 1.25rem; border-radius: 8px; border: 1px solid #334155; }");
        sb.AppendLine("    .card .val { font-size: 1.5rem; font-weight: 700; color: #38bdf8; margin-top: 0.5rem; }");
        sb.AppendLine("    table { width: 100%; border-collapse: collapse; background: #1e293b; border-radius: 8px; overflow: hidden; margin-top: 1rem; }");
        sb.AppendLine("    th, td { padding: 0.75rem 1rem; text-align: left; border-bottom: 1px solid #334155; }");
        sb.AppendLine("    th { background: #0f172a; font-weight: 600; color: #94a3b8; }");
        sb.AppendLine("    .badge { display: inline-block; padding: 0.25rem 0.5rem; border-radius: 4px; font-size: 0.8rem; font-weight: 600; }");
        sb.AppendLine("    .badge-ok { background: #065f46; color: #34d399; }");
        sb.AppendLine("    .badge-warn { background: #78350f; color: #fbbf24; }");
        sb.AppendLine("    .badge-err { background: #881337; color: #fb7185; }");
        sb.AppendLine("    .dest-line { margin: 0.15rem 0; }");
        sb.AppendLine("    .dest-label { font-family: monospace; font-size: 0.85rem; background: #0f172a; padding: 0.1rem 0.3rem; border-radius: 4px; word-break: break-all; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"container\">");
        sb.AppendLine($"    <div class=\"hero-badge\" style=\"background: {badgeBg}; color: #ffffff;\">{WebUtility.HtmlEncode(badgeText)}</div>");

        sb.AppendLine("    <div class=\"meta-grid\">");
        sb.AppendLine($"      <div class=\"card\"><div>Source Path</div><div class=\"val\" style=\"font-size: 1rem; word-break: break-all;\">{WebUtility.HtmlEncode(summary.SourcePath)}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div>Total Files</div><div class=\"val\">{summary.TotalFiles:N0}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div>Total Size</div><div class=\"val\">{(summary.TotalBytes / (1024.0 * 1024.0 * 1024.0)):F2} GB</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div>Verification Mode</div><div class=\"val\" style=\"font-size: 1.1rem;\">{summary.Mode}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div>Verified</div><div class=\"val\" style=\"color: #34d399;\">{summary.FullyVerifiedFiles:N0}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div>Partial</div><div class=\"val\" style=\"color: #fbbf24;\">{summary.PartiallyVerifiedFiles:N0}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div>Missing</div><div class=\"val\" style=\"color: #fb7185;\">{summary.MissingFiles:N0}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div>Corrupt</div><div class=\"val\" style=\"color: #fb7185;\">{summary.CorruptFiles:N0}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div>Timestamp</div><div class=\"val\" style=\"font-size: 0.9rem;\">{summary.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC</div></div>");
        if (summary.DuplicateAnalysis is not null)
        {
            sb.AppendLine($"      <div class=\"card\"><div>Duplicate Copies</div><div class=\"val\" style=\"color: #f59e0b;\">{summary.DuplicateAnalysis.TotalDuplicateCopies:N0}</div></div>");
            sb.AppendLine($"      <div class=\"card\"><div>Reclaimable Space</div><div class=\"val\" style=\"color: #f59e0b;\">{FormatBytes(summary.DuplicateAnalysis.TotalReclaimableBytes)}</div></div>");
            sb.AppendLine($"      <div class=\"card\"><div>Multi-Drive Redundant</div><div class=\"val\" style=\"color: #38bdf8;\">{summary.DuplicateAnalysis.CrossDestinationRedundantFileCount:N0} files</div></div>");
        }
        sb.AppendLine("    </div>");

        AppendDestinationsTable(sb, results);

        sb.AppendLine("    <h2>Media Verification Details</h2>");
        sb.AppendLine("    <table>");
        sb.AppendLine("      <thead>");
        sb.AppendLine("        <tr>");
        sb.AppendLine("          <th>File</th>");
        sb.AppendLine("          <th>Size</th>");
        sb.AppendLine("          <th>Category</th>");
        sb.AppendLine("          <th>Status</th>");
        sb.AppendLine("          <th>Checksum (xxHash)</th>");
        sb.AppendLine("          <th>Destination Details</th>");
        sb.AppendLine("        </tr>");
        sb.AppendLine("      </thead>");
        sb.AppendLine("      <tbody>");

        foreach (VerificationResultItem item in results)
        {
            string statusBadge = item.OverallStatus switch
            {
                FileOverallStatus.Verified => "<span class=\"badge badge-ok\">Verified</span>",
                FileOverallStatus.Partial => "<span class=\"badge badge-warn\">Partial</span>",
                FileOverallStatus.Corrupt => "<span class=\"badge badge-err\">Corrupt</span>",
                _ => "<span class=\"badge badge-err\">Missing</span>"
            };

            string checksumDisplay = item.SourceFile.FullHash.HasValue
                ? FormatChecksumCell(item.SourceFile.FullHash.Value, string.Empty)
                : item.SourceFile.DeepHash.HasValue
                    ? FormatChecksumCell(item.SourceFile.DeepHash.Value, " (Deep)")
                    : item.SourceFile.HeadHash.HasValue
                        ? FormatChecksumCell(item.SourceFile.HeadHash.Value, " (Head)")
                        : "<span style=\"color: #64748b;\">-</span>";

            StringBuilder destDetails = new();
            foreach (FileMatchStatus status in item.DestinationStatuses.Values)
            {
                destDetails.Append(FormatDestinationLine(status));
            }
            string destText = destDetails.ToString();

            sb.AppendLine("        <tr>");
            sb.AppendLine($"          <td>{WebUtility.HtmlEncode(item.SourceFile.RelativePath)}</td>");
            sb.AppendLine($"          <td>{(item.SourceFile.FileLength / (1024.0 * 1024.0)):F2} MB</td>");
            sb.AppendLine($"          <td>{item.SourceFile.Category}</td>");
            sb.AppendLine($"          <td>{statusBadge}</td>");
            sb.AppendLine($"          <td>{checksumDisplay}</td>");
            sb.AppendLine($"          <td>{destText}</td>");
            sb.AppendLine("        </tr>");
        }

        sb.AppendLine("      </tbody>");
        sb.AppendLine("    </table>");

        if (summary.DuplicateAnalysis is not null && summary.DuplicateAnalysis.Groups.Count > 0)
        {
            sb.AppendLine("    <h2 style=\"margin-top: 2rem;\">Destination Duplicate Files &amp; Storage Analysis</h2>");
            sb.AppendLine($"    <p style=\"color: #94a3b8;\">Potential space saved by removing redundant same-drive copies: <strong style=\"color: #f59e0b;\">{FormatBytes(summary.DuplicateAnalysis.TotalReclaimableBytes)}</strong>.</p>");
            sb.AppendLine("    <table>");
            sb.AppendLine("      <thead>");
            sb.AppendLine("        <tr>");
            sb.AppendLine("          <th>File Name</th>");
            sb.AppendLine("          <th>Size</th>");
            sb.AppendLine("          <th>Reclaimable Space</th>");
            sb.AppendLine("          <th>Redundancy Type</th>");
            sb.AppendLine("          <th>Locations</th>");
            sb.AppendLine("        </tr>");
            sb.AppendLine("      </thead>");
            sb.AppendLine("      <tbody>");

            foreach (DuplicateGroup group in summary.DuplicateAnalysis.Groups)
            {
                string firstFileName = Path.GetFileName(group.Files[0].File.FullPath);
                string redundancyBadge = group.IsIntraDestination && group.IsCrossDestination
                    ? "<span class=\"badge badge-warn\">Same-Drive + Multi-Drive</span>"
                    : group.IsIntraDestination
                        ? "<span class=\"badge badge-warn\">Same-Drive Waste</span>"
                        : "<span class=\"badge badge-ok\">Multi-Drive Redundant</span>";

                List<string> locs = new();
                foreach (DuplicateFileItem file in group.Files)
                {
                    string primaryTag = file.IsPrimary ? " (Primary)" : " (Duplicate)";
                    locs.Add($"[{WebUtility.HtmlEncode(file.DestinationName)}] {WebUtility.HtmlEncode(file.RelativePath)}{primaryTag}");
                }
                string locsText = string.Join("<br>", locs);

                sb.AppendLine("        <tr>");
                sb.AppendLine($"          <td>{WebUtility.HtmlEncode(firstFileName)}</td>");
                sb.AppendLine($"          <td>{FormatBytes(group.FileSize)}</td>");
                sb.AppendLine($"          <td style=\"color: #f59e0b; font-weight: 600;\">{FormatBytes(group.ReclaimableBytes)}</td>");
                sb.AppendLine($"          <td>{redundancyBadge}</td>");
                sb.AppendLine($"          <td style=\"font-size: 0.85rem;\">{locsText}</td>");
                sb.AppendLine("        </tr>");
            }

            sb.AppendLine("      </tbody>");
            sb.AppendLine("    </table>");
        }
        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static void AppendDestinationsTable(StringBuilder sb, IReadOnlyList<VerificationResultItem> results)
    {
        IReadOnlyList<ReportDestination> destinations = ReportFormatting.CollectDestinations(results);
        if (destinations.Count == 0)
        {
            return;
        }

        sb.AppendLine("    <h2>Backup Destinations</h2>");
        sb.AppendLine("    <table>");
        sb.AppendLine("      <thead>");
        sb.AppendLine("        <tr>");
        sb.AppendLine("          <th>Destination</th>");
        sb.AppendLine("          <th>Verified</th>");
        sb.AppendLine("          <th>Missing</th>");
        sb.AppendLine("          <th>Corrupt</th>");
        sb.AppendLine("        </tr>");
        sb.AppendLine("      </thead>");
        sb.AppendLine("      <tbody>");
        foreach (ReportDestination destination in destinations)
        {
            string encodedLabel = WebUtility.HtmlEncode(destination.Label);
            sb.AppendLine("        <tr>");
            sb.AppendLine($"          <td style=\"word-break: break-all;\">{encodedLabel}</td>");
            sb.AppendLine($"          <td>{CountStatus(results, destination.Id, MediaStatus.Verified):N0}</td>");
            sb.AppendLine($"          <td>{CountStatus(results, destination.Id, MediaStatus.Missing):N0}</td>");
            sb.AppendLine($"          <td>{CountStatus(results, destination.Id, MediaStatus.Corrupt):N0}</td>");
            sb.AppendLine("        </tr>");
        }
        sb.AppendLine("      </tbody>");
        sb.AppendLine("    </table>");
    }

    private static int CountStatus(
        IReadOnlyList<VerificationResultItem> results,
        string destinationId,
        MediaStatus mediaStatus)
    {
        int count = 0;
        foreach (VerificationResultItem item in results)
        {
            if (item.DestinationStatuses.TryGetValue(destinationId, out FileMatchStatus? status)
                && status.Status == mediaStatus)
            {
                count++;
            }
        }

        return count;
    }

    // Root paths can contain ": " and ", ", so each destination gets its own line with the path set apart.
    private static string FormatDestinationLine(FileMatchStatus status)
    {
        string encodedLabel = WebUtility.HtmlEncode(ReportFormatting.GetDestinationLabel(status));
        return $"<div class=\"dest-line\"><code class=\"dest-label\">{encodedLabel}</code>: {status.Status}</div>";
    }

    private static string FormatChecksumCell(ulong hash, string suffix) =>
        $"{CHECKSUM_CODE_OPEN_TAG}{ReportFormatting.FormatHash(hash)}</code>{suffix}";

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024 * 1024)):F2} GB",
            >= 1024L * 1024 => $"{(bytes / (1024.0 * 1024)):F1} MB",
            >= 1024L => $"{(bytes / 1024.0):F0} KB",
            _ => $"{bytes} B"
        };
    }
}
