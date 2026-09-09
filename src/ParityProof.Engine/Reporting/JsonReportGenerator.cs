using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Models;

namespace ParityProof.Engine.Reporting;

public sealed record JsonReportPayload(
    VerificationSummary Summary,
    IReadOnlyList<VerificationResultItem> Results);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonReportPayload))]
[JsonSerializable(typeof(DuplicateAnalysisResult))]
[JsonSerializable(typeof(DuplicateGroup))]
[JsonSerializable(typeof(DuplicateFileItem))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;

public sealed class JsonReportGenerator : IReportGenerator
{
    public string FileExtension => ".json";
    public string DisplayName => "JSON Manifest";

    public async Task GenerateReportAsync(
        VerificationSummary summary,
        IReadOnlyList<VerificationResultItem> results,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        JsonReportPayload payload = new(summary, results);
        await using FileStream stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(
            stream,
            payload,
            ReportJsonContext.Default.JsonReportPayload,
            cancellationToken).ConfigureAwait(false);
    }
}
