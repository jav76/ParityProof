using System;
using System.Collections.Generic;
using ParityProof.Core.Models;

namespace ParityProof.Engine.Reporting;

internal sealed record ReportDestination(string Id, string Label);

internal static class ReportFormatting
{
    public static string FormatHash(ulong hash) => $"0x{hash:X16}";

    public static string FormatHash(ulong? hash) => hash.HasValue ? FormatHash(hash.Value) : string.Empty;

    public static string GetDestinationLabel(FileMatchStatus status) =>
        string.IsNullOrWhiteSpace(status.DestinationRootPath) ? status.DestinationId : status.DestinationRootPath;

    public static IReadOnlyList<ReportDestination> CollectDestinations(IReadOnlyList<VerificationResultItem> results)
    {
        List<ReportDestination> destinations = new();
        HashSet<string> seenIds = new(StringComparer.Ordinal);
        foreach (VerificationResultItem item in results)
        {
            foreach (KeyValuePair<string, FileMatchStatus> entry in item.DestinationStatuses)
            {
                if (seenIds.Add(entry.Key))
                {
                    destinations.Add(new ReportDestination(entry.Key, GetDestinationLabel(entry.Value)));
                }
            }
        }

        return destinations;
    }
}
