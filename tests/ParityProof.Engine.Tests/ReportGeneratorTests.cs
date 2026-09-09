using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Engine.Reporting;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class ReportGeneratorTests : IDisposable
{
    private readonly string _testDir;

    public ReportGeneratorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_ReportTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task ReportGenerators_ProduceValidOutputs()
    {
        MediaFile file = new(
            RelativePath: "DCIM/IMG_0001.CR3",
            FullPath: "/card/DCIM/IMG_0001.CR3",
            FileLength: 30_000_000,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw,
            HeadHash: 111UL,
            TailHash: 222UL,
            FullHash: 333UL);

        Dictionary<string, FileMatchStatus> statuses = new()
        {
            ["ssd"] = new FileMatchStatus("ssd", "/backup/ssd", MediaStatus.Verified, "/backup/ssd/IMG_0001.CR3")
        };

        VerificationResultItem item = new(file, statuses);
        VerificationSummary summary = new(
            TimestampUtc: DateTime.UtcNow,
            SourcePath: "/card",
            Mode: VerificationMode.Quick,
            TotalFiles: 1,
            TotalBytes: 30_000_000,
            FullyVerifiedFiles: 1,
            PartiallyVerifiedFiles: 0,
            MissingFiles: 0,
            CorruptFiles: 0,
            SafetyStatus: OverallSafetyStatus.SafeToFormat,
            Duration: TimeSpan.FromSeconds(1.5));

        string htmlPath = Path.Combine(_testDir, "report.html");
        string csvPath = Path.Combine(_testDir, "report.csv");
        string jsonPath = Path.Combine(_testDir, "report.json");

        HtmlReportGenerator htmlGen = new();
        CsvReportGenerator csvGen = new();
        JsonReportGenerator jsonGen = new();

        await htmlGen.GenerateReportAsync(summary, new[] { item }, htmlPath);
        await csvGen.GenerateReportAsync(summary, new[] { item }, csvPath);
        await jsonGen.GenerateReportAsync(summary, new[] { item }, jsonPath);

        Assert.True(File.Exists(htmlPath));
        Assert.True(new FileInfo(htmlPath).Length > 0);
        string htmlContent = await File.ReadAllTextAsync(htmlPath);
        Assert.Contains("SAFE TO FORMAT", htmlContent);

        Assert.True(File.Exists(csvPath));
        Assert.True(new FileInfo(csvPath).Length > 0);

        Assert.True(File.Exists(jsonPath));
        Assert.True(new FileInfo(jsonPath).Length > 0);
    }

    [Fact]
    public async Task ReportGenerators_WithDuplicateAnalysis_ProduceValidOutputsWithDuplicateSections()
    {
        MediaFile file1 = new(
            RelativePath: "DCIM/IMG_0001.CR3",
            FullPath: "/backup/ssd/DCIM/IMG_0001.CR3",
            FileLength: 20_000_000,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw,
            HeadHash: 111UL,
            TailHash: 222UL,
            FullHash: 333UL);

        MediaFile file2 = new(
            RelativePath: "DCIM/IMG_0001_copy.CR3",
            FullPath: "/backup/ssd/DCIM/IMG_0001_copy.CR3",
            FileLength: 20_000_000,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw,
            HeadHash: 111UL,
            TailHash: 222UL,
            FullHash: 333UL);

        DuplicateFileItem item1 = new(file1, "ssd", "Fast SSD", IsPrimary: true, "DCIM/IMG_0001.CR3");
        DuplicateFileItem item2 = new(file2, "ssd", "Fast SSD", IsPrimary: false, "DCIM/IMG_0001_copy.CR3");

        DuplicateGroup group = new(
            GroupKey: "3330000000000000",
            FileSize: 20_000_000,
            ReclaimableBytes: 20_000_000,
            IsIntraDestination: true,
            IsCrossDestination: false,
            Files: new[] { item1, item2 });

        DuplicateAnalysisResult duplicateAnalysis = new(
            Groups: new[] { group },
            TotalDuplicateCopies: 1,
            TotalReclaimableBytes: 20_000_000,
            CrossDestinationRedundantFileCount: 0);

        Dictionary<string, FileMatchStatus> statuses = new()
        {
            ["ssd"] = new FileMatchStatus("ssd", "/backup/ssd", MediaStatus.Verified, "/backup/ssd/DCIM/IMG_0001.CR3")
        };

        VerificationResultItem resultItem = new(file1, statuses);
        VerificationSummary summary = new(
            TimestampUtc: DateTime.UtcNow,
            SourcePath: "/card",
            Mode: VerificationMode.Quick,
            TotalFiles: 1,
            TotalBytes: 20_000_000,
            FullyVerifiedFiles: 1,
            PartiallyVerifiedFiles: 0,
            MissingFiles: 0,
            CorruptFiles: 0,
            SafetyStatus: OverallSafetyStatus.SafeToFormat,
            Duration: TimeSpan.FromSeconds(1.0),
            DuplicateAnalysis: duplicateAnalysis);

        string htmlPath = Path.Combine(_testDir, "report_dup.html");
        string csvPath = Path.Combine(_testDir, "report_dup.csv");
        string jsonPath = Path.Combine(_testDir, "report_dup.json");

        HtmlReportGenerator htmlGen = new();
        CsvReportGenerator csvGen = new();
        JsonReportGenerator jsonGen = new();

        await htmlGen.GenerateReportAsync(summary, new[] { resultItem }, htmlPath);
        await csvGen.GenerateReportAsync(summary, new[] { resultItem }, csvPath);
        await jsonGen.GenerateReportAsync(summary, new[] { resultItem }, jsonPath);

        string htmlContent = await File.ReadAllTextAsync(htmlPath);
        Assert.Contains("Destination Duplicate Files", htmlContent);
        Assert.Contains("Reclaimable Space", htmlContent);

        string csvContent = await File.ReadAllTextAsync(csvPath);
        Assert.Contains("Fast SSD", csvContent);
        Assert.Contains("Same-Drive Duplicate", csvContent);

        string jsonContent = await File.ReadAllTextAsync(jsonPath);
        Assert.Contains("totalReclaimableBytes", jsonContent);
        Assert.Contains("20000000", jsonContent);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
