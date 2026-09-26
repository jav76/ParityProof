using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Models;
using ParityProof.Engine.Reporting;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class ReportGeneratorTests : IDisposable
{
    private const ulong LARGE_HEAD_HASH = 0xF3A1000012345678UL;
    private const ulong LARGE_FULL_HASH = 0xFFFFFFFFFFFFFFF1UL;
    private const double MAX_EXACT_DOUBLE_INTEGER = 9_007_199_254_740_992d;
    private const int CSV_OVERALL_STATUS_COLUMN = 3;
    private const int CSV_HEAD_HASH_COLUMN = 4;
    private const int CSV_TAIL_HASH_COLUMN = 5;
    private const int CSV_DEEP_HASH_COLUMN = 6;
    private const int CSV_FULL_HASH_COLUMN = 7;
    private const int CSV_DESTINATION_STATUSES_COLUMN = 8;
    private const int CSV_FIRST_DESTINATION_COLUMN = 9;
    private const string HTML_DESTINATION_LINE_PATTERN =
        "<div class=\"dest-line\"><code class=\"dest-label\">([^<]*)</code>: (\\w+)</div>";

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
        Assert.Contains("Checksum (xxHash)", htmlContent);
        Assert.Contains("0x000000000000014D", htmlContent); // 333UL in hex

        Assert.True(File.Exists(csvPath));
        Assert.True(new FileInfo(csvPath).Length > 0);
        string csvContent = await File.ReadAllTextAsync(csvPath);
        Assert.Contains("RelativePath,FileSizeBytes,Category,OverallStatus,HeadHash,TailHash,DeepHash,FullHash,DestinationStatuses", csvContent);

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

    [Fact]
    public async Task HtmlAndCsv_CorruptBackups_ReportedAsCorruptWithCountsMatchingEngineSummary()
    {
        VerificationResultItem allVerified = CreateItem(
            "DCIM/IMG_0001.CR3",
            ("/mnt/T7", MediaStatus.Verified),
            ("/mnt/NAS", MediaStatus.Verified));
        VerificationResultItem allCorrupt = CreateItem(
            "DCIM/IMG_0002.CR3",
            ("/mnt/T7", MediaStatus.Corrupt),
            ("/mnt/NAS", MediaStatus.Corrupt));
        VerificationResultItem verifiedAndCorrupt = CreateItem(
            "DCIM/IMG_0003.CR3",
            ("/mnt/T7", MediaStatus.Verified),
            ("/mnt/NAS", MediaStatus.Corrupt));
        VerificationResultItem[] results = { allVerified, allCorrupt, verifiedAndCorrupt };

        VerificationSummary summary = CreateSummary(
            totalFiles: 3,
            fullyVerified: 1,
            partiallyVerified: 0,
            missing: 0,
            corrupt: 2,
            OverallSafetyStatus.UnsafeToFormat);

        string html = await GenerateAsync(new HtmlReportGenerator(), summary, results, "corrupt.html");
        string csvPath = Path.Combine(_testDir, "corrupt.csv");
        await new CsvReportGenerator().GenerateReportAsync(summary, results, csvPath);

        Assert.DoesNotContain("badge-err\">Missing", html);
        Assert.Equal(2, Regex.Matches(html, Regex.Escape("<span class=\"badge badge-err\">Corrupt</span>")).Count);
        Assert.DoesNotContain("MISSING MEDIA", html);
        Assert.Contains("UNSAFE TO FORMAT - 0 MISSING, 2 CORRUPT", html);
        Assert.Matches("<div>Verified</div><div class=\"val\"[^>]*>1</div>", html);
        Assert.Matches("<div>Partial</div><div class=\"val\"[^>]*>0</div>", html);
        Assert.Matches("<div>Missing</div><div class=\"val\"[^>]*>0</div>", html);
        Assert.Matches("<div>Corrupt</div><div class=\"val\"[^>]*>2</div>", html);
        AssertDestinationRow(html, "/mnt/T7", verified: 2, missing: 0, corrupt: 1);
        AssertDestinationRow(html, "/mnt/NAS", verified: 1, missing: 0, corrupt: 2);

        List<string[]> rows = ParseCsv(csvPath);
        Assert.Equal("Verified", rows[1][CSV_OVERALL_STATUS_COLUMN]);
        Assert.Equal("Corrupt", rows[2][CSV_OVERALL_STATUS_COLUMN]);
        Assert.Equal("Corrupt", rows[3][CSV_OVERALL_STATUS_COLUMN]);
    }

    [Fact]
    public async Task HtmlAndCsv_IdentifyDestinationsByRootPath_NotSessionIds()
    {
        VerificationResultItem item = CreateItem(
            "DCIM/IMG_0001.CR3",
            ("/mnt/T7", MediaStatus.Verified),
            ("/mnt/NAS <&>", MediaStatus.Missing));
        VerificationResultItem[] results = { item };

        VerificationSummary summary = CreateSummary(
            totalFiles: 1,
            fullyVerified: 0,
            partiallyVerified: 1,
            missing: 0,
            corrupt: 0,
            OverallSafetyStatus.PartiallyBackedUp);

        string html = await GenerateAsync(new HtmlReportGenerator(), summary, results, "dests.html");
        string csvPath = Path.Combine(_testDir, "dests.csv");
        await new CsvReportGenerator().GenerateReportAsync(summary, results, csvPath);

        Assert.Contains(DestinationLine("/mnt/T7", MediaStatus.Verified), html);
        Assert.Contains(DestinationLine("/mnt/NAS &lt;&amp;&gt;", MediaStatus.Missing), html);
        Assert.DoesNotContain("dest_1", html);
        Assert.DoesNotContain("dest_2", html);

        int destinationsStart = html.IndexOf("Backup Destinations", StringComparison.Ordinal);
        int detailsStart = html.IndexOf("Media Verification Details", StringComparison.Ordinal);
        Assert.True(destinationsStart >= 0, "HTML report is missing the Backup Destinations section.");
        Assert.True(detailsStart > destinationsStart);
        string destinationsSection = html[destinationsStart..detailsStart];
        AssertDestinationRow(destinationsSection, "/mnt/T7", verified: 1, missing: 0, corrupt: 0);
        AssertDestinationRow(destinationsSection, "/mnt/NAS &lt;&amp;&gt;", verified: 0, missing: 1, corrupt: 0);

        string csv = await File.ReadAllTextAsync(csvPath);
        Assert.DoesNotContain("dest_", csv);
        List<string[]> rows = ParseCsv(csvPath);
        Assert.Equal("/mnt/T7:Verified;/mnt/NAS <&>:Missing", rows[1][CSV_DESTINATION_STATUSES_COLUMN]);
        Assert.Equal(new[] { "/mnt/T7", "/mnt/NAS <&>" }, rows[0][CSV_FIRST_DESTINATION_COLUMN..]);
        Assert.Equal(new[] { "Verified", "Missing" }, rows[1][CSV_FIRST_DESTINATION_COLUMN..]);
    }

    [Fact]
    public async Task Html_DestinationDetails_KeepRootPathsWithSeparatorsApart()
    {
        const string COMMA_COLON_ROOT = "E:\\Backups, 2024: T7";
        const string FORMULA_ROOT = "=cmd|' /C calc'!A0 : NAS, Main";
        VerificationResultItem item = CreateItem(
            "DCIM/IMG_0001.CR3",
            (COMMA_COLON_ROOT, MediaStatus.Verified),
            (FORMULA_ROOT, MediaStatus.Corrupt));
        VerificationResultItem[] results = { item };
        VerificationSummary summary = CreateSummary(1, 0, 0, 0, 1, OverallSafetyStatus.UnsafeToFormat);

        string html = await GenerateAsync(new HtmlReportGenerator(), summary, results, "separators.html");

        MatchCollection lines = Regex.Matches(html, HTML_DESTINATION_LINE_PATTERN);
        Assert.Equal(2, lines.Count);
        Assert.Equal(COMMA_COLON_ROOT, WebUtility.HtmlDecode(lines[0].Groups[1].Value));
        Assert.Equal(nameof(MediaStatus.Verified), lines[0].Groups[2].Value);
        Assert.Equal(FORMULA_ROOT, WebUtility.HtmlDecode(lines[1].Groups[1].Value));
        Assert.Equal(nameof(MediaStatus.Corrupt), lines[1].Groups[2].Value);
    }

    [Fact]
    public async Task Csv_PerDestinationStatusColumns_StayUnambiguousForPathsWithColonsAndSemicolons()
    {
        VerificationResultItem[] results =
        {
            CreateItem(
                "DCIM/IMG_0001.CR3",
                ("E:\\Backups;2024", MediaStatus.Verified),
                ("E:\\", MediaStatus.Missing)),
            CreateItem(
                "DCIM/IMG_0002.CR3",
                ("E:\\Backups;2024", MediaStatus.Corrupt)),
        };
        VerificationSummary summary = CreateSummary(2, 0, 1, 0, 1, OverallSafetyStatus.UnsafeToFormat);

        string csvPath = Path.Combine(_testDir, "separators.csv");
        await new CsvReportGenerator().GenerateReportAsync(summary, results, csvPath);

        List<string[]> rows = ParseCsv(csvPath);
        Assert.Equal(new[] { "E:\\Backups;2024", "E:\\" }, rows[0][CSV_FIRST_DESTINATION_COLUMN..]);
        Assert.Equal(new[] { "Verified", "Missing" }, rows[1][CSV_FIRST_DESTINATION_COLUMN..]);
        Assert.Equal(new[] { "Corrupt", string.Empty }, rows[2][CSV_FIRST_DESTINATION_COLUMN..]);
    }

    [Fact]
    public async Task HtmlAndCsv_BlankDestinationRootPath_FallsBackToDestinationId()
    {
        VerificationResultItem item = CreateItemWithStatuses(
            "DCIM/IMG_0001.CR3",
            new FileMatchStatus("archive-01", string.Empty, MediaStatus.Verified),
            new FileMatchStatus("archive-02", "   ", MediaStatus.Missing));
        VerificationResultItem[] results = { item };
        VerificationSummary summary = CreateSummary(1, 0, 1, 0, 0, OverallSafetyStatus.PartiallyBackedUp);

        string html = await GenerateAsync(new HtmlReportGenerator(), summary, results, "fallback.html");
        string csvPath = Path.Combine(_testDir, "fallback.csv");
        await new CsvReportGenerator().GenerateReportAsync(summary, results, csvPath);

        Assert.Contains(
            DestinationLine("archive-01", MediaStatus.Verified) + DestinationLine("archive-02", MediaStatus.Missing),
            html);
        AssertDestinationRow(html, "archive-01", verified: 1, missing: 0, corrupt: 0);
        AssertDestinationRow(html, "archive-02", verified: 0, missing: 1, corrupt: 0);

        List<string[]> rows = ParseCsv(csvPath);
        Assert.Equal("archive-01:Verified;archive-02:Missing", rows[1][CSV_DESTINATION_STATUSES_COLUMN]);
        Assert.Equal(new[] { "archive-01", "archive-02" }, rows[0][CSV_FIRST_DESTINATION_COLUMN..]);
    }

    [Fact]
    public async Task Csv_WritesHashesAsPrefixedHex_NotRoundableDecimals()
    {
        VerificationResultItem item = CreateItem(
            "DCIM/IMG_0001.CR3",
            headHash: LARGE_HEAD_HASH,
            tailHash: 1UL,
            fullHash: LARGE_FULL_HASH,
            ("/mnt/T7", MediaStatus.Verified));
        VerificationResultItem[] results = { item };
        VerificationSummary summary = CreateSummary(1, 1, 0, 0, 0, OverallSafetyStatus.SafeToFormat);

        string csvPath = Path.Combine(_testDir, "hashes.csv");
        await new CsvReportGenerator().GenerateReportAsync(summary, results, csvPath);

        string csv = await File.ReadAllTextAsync(csvPath);
        Assert.DoesNotContain("17555312822772323960", csv);

        List<string[]> rows = ParseCsv(csvPath);
        Assert.Equal("0xF3A1000012345678", rows[1][CSV_HEAD_HASH_COLUMN]);
        Assert.Equal("0x0000000000000001", rows[1][CSV_TAIL_HASH_COLUMN]);
        Assert.Equal(string.Empty, rows[1][CSV_DEEP_HASH_COLUMN]);
        Assert.Equal("0xFFFFFFFFFFFFFFF1", rows[1][CSV_FULL_HASH_COLUMN]);
    }

    [Fact]
    public async Task Json_WritesHashesAsHexStringsAndEnumsAsNames()
    {
        VerificationResultItem item = CreateItem(
            "DCIM/IMG_0001.CR3",
            headHash: LARGE_HEAD_HASH,
            tailHash: 1UL,
            fullHash: LARGE_FULL_HASH,
            ("/mnt/T7", MediaStatus.Verified));
        VerificationResultItem[] results = { item };
        VerificationSummary summary = CreateSummary(1, 1, 0, 0, 0, OverallSafetyStatus.SafeToFormat);

        string json = await GenerateAsync(new JsonReportGenerator(), summary, results, "enums.json");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement summaryElement = document.RootElement.GetProperty("summary");
        JsonElement safetyStatus = summaryElement.GetProperty("safetyStatus");
        Assert.Equal(JsonValueKind.String, safetyStatus.ValueKind);
        Assert.Equal(nameof(OverallSafetyStatus.SafeToFormat), safetyStatus.GetString());
        Assert.Equal(nameof(VerificationMode.Full), summaryElement.GetProperty("mode").GetString());

        JsonElement resultElement = document.RootElement.GetProperty("results")[0];
        JsonElement sourceFile = resultElement.GetProperty("sourceFile");
        Assert.Equal(nameof(MediaCategory.PhotoRaw), sourceFile.GetProperty("category").GetString());
        Assert.Equal("0xF3A1000012345678", sourceFile.GetProperty("headHash").GetString());
        Assert.Equal("0x0000000000000001", sourceFile.GetProperty("tailHash").GetString());
        Assert.Equal(JsonValueKind.Null, sourceFile.GetProperty("deepHash").ValueKind);
        Assert.Equal("0xFFFFFFFFFFFFFFF1", sourceFile.GetProperty("fullHash").GetString());

        JsonElement destination = resultElement.GetProperty("destinationStatuses").GetProperty("dest_1");
        Assert.Equal(nameof(MediaStatus.Verified), destination.GetProperty("status").GetString());
        Assert.Equal("0xFFFFFFFFFFFFFFF1", destination.GetProperty("destinationFullHash").GetString());
        Assert.Equal(JsonValueKind.Null, destination.GetProperty("destinationDeepHash").ValueKind);
    }

    [Fact]
    public async Task Json_ContainsNoNumbersBeyondExactDoublePrecision()
    {
        VerificationResultItem item = CreateItem(
            "DCIM/IMG_0001.CR3",
            headHash: LARGE_HEAD_HASH,
            tailHash: LARGE_FULL_HASH,
            fullHash: LARGE_FULL_HASH,
            ("/mnt/T7", MediaStatus.Verified),
            ("/mnt/NAS", MediaStatus.Verified));
        VerificationResultItem[] results = { item };
        VerificationSummary summary = CreateSummary(1, 1, 0, 0, 0, OverallSafetyStatus.SafeToFormat);

        string json = await GenerateAsync(new JsonReportGenerator(), summary, results, "precision.json");

        using JsonDocument document = JsonDocument.Parse(json);
        List<string> unsafeNumbers = new();
        CollectUnsafeNumbers(document.RootElement, unsafeNumbers);
        Assert.Empty(unsafeNumbers);
    }

    [Fact]
    public void JsonReportModels_ReserveUInt64ForHashes()
    {
        JsonSerializerOptions options = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        List<string> uint64Members = new();
        CollectUInt64Members(typeof(JsonReportPayload), options, new HashSet<Type>(), uint64Members);

        Assert.Contains("MediaFile.FullHash", uint64Members);
        Assert.Contains("FileMatchStatus.DestinationFullHash", uint64Members);
        Assert.All(uint64Members, member => Assert.EndsWith("Hash", member, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"0xF3A1000012345678\"", LARGE_HEAD_HASH)]
    [InlineData("\"0x0000000000000001\"", 1UL)]
    [InlineData("17555312822772323960", LARGE_HEAD_HASH)]
    public void HexUInt64JsonConverter_ReadsHexStringsAndLegacyNumbers(string json, ulong expected)
    {
        Assert.Equal(expected, ReadHash(json));
    }

    [Theory]
    [InlineData("\"F3A1000012345678\"")]
    [InlineData("\"0xNOTHEX\"")]
    [InlineData("true")]
    public void HexUInt64JsonConverter_RejectsValuesThatAreNotPrefixedHex(string json)
    {
        Assert.Throws<JsonException>(() => ReadHash(json));
    }

    [Theory]
    [InlineData("=1+1.jpg")]
    [InlineData("+2EV.jpg")]
    [InlineData("-1EV.jpg")]
    [InlineData("@home/x.jpg")]
    [InlineData("\tx.jpg")]
    [InlineData("\rx.jpg")]
    public async Task Csv_FreeTextFieldsStartingWithFormulaTriggers_ArePrefixedWithApostrophe(string text)
    {
        VerificationResultItem item = CreateItem(text, ("/mnt/T7", MediaStatus.Verified));
        VerificationResultItem[] results = { item };

        MediaFile duplicateFile = item.SourceFile with { FullPath = "/mnt/T7/" + text };
        DuplicateGroup group = new(
            GroupKey: text,
            FileSize: 1024,
            ReclaimableBytes: 1024,
            IsIntraDestination: true,
            IsCrossDestination: false,
            Files: new[]
            {
                new DuplicateFileItem(duplicateFile, "dest_1", text, IsPrimary: true, text),
                new DuplicateFileItem(duplicateFile, "dest_1", text, IsPrimary: false, text),
            });
        DuplicateAnalysisResult duplicates = new(new[] { group }, 1, 1024, 0);
        VerificationSummary summary = CreateSummary(1, 1, 0, 0, 0, OverallSafetyStatus.SafeToFormat) with
        {
            DuplicateAnalysis = duplicates,
        };

        string csv = await GenerateAsync(new CsvReportGenerator(), summary, results, "formula.csv");

        string neutralisedField = "\"'" + text + "\"";
        const int FREE_TEXT_FIELDS_PER_DUPLICATE_ROW = 3;
        int expectedOccurrences = 1 + (group.Files.Count * FREE_TEXT_FIELDS_PER_DUPLICATE_ROW);
        Assert.Equal(expectedOccurrences, Regex.Matches(csv, Regex.Escape(neutralisedField)).Count);
        Assert.DoesNotContain("\"" + text, csv);
    }

    [Fact]
    public async Task Csv_DestinationLabelsStartingWithFormulaTriggers_ArePrefixedWithApostrophe()
    {
        const string FORMULA_ROOT = "=HYPERLINK(\"http://x\",\"y\")";
        const string FORMULA_ID = "@dest_2";
        VerificationResultItem item = CreateItemWithStatuses(
            "DCIM/IMG_0001.CR3",
            new FileMatchStatus("dest_1", FORMULA_ROOT, MediaStatus.Verified),
            new FileMatchStatus(FORMULA_ID, string.Empty, MediaStatus.Missing));
        VerificationResultItem[] results = { item };
        VerificationSummary summary = CreateSummary(1, 0, 1, 0, 0, OverallSafetyStatus.PartiallyBackedUp);

        string csvPath = Path.Combine(_testDir, "formula_destinations.csv");
        await new CsvReportGenerator().GenerateReportAsync(summary, results, csvPath);

        string csv = await File.ReadAllTextAsync(csvPath);
        Assert.DoesNotContain("\"=", csv);
        Assert.DoesNotContain("\"@", csv);

        List<string[]> rows = ParseCsv(csvPath);
        Assert.Equal(new[] { "'" + FORMULA_ROOT, "'" + FORMULA_ID }, rows[0][CSV_FIRST_DESTINATION_COLUMN..]);
        Assert.Equal(
            "'" + FORMULA_ROOT + ":Verified;" + FORMULA_ID + ":Missing",
            rows[1][CSV_DESTINATION_STATUSES_COLUMN]);
    }

    [Fact]
    public async Task Csv_FieldsWithQuotesNewlinesAndFormulas_RoundTripThroughStandardParser()
    {
        const string QUOTED_NAME = "DCIM/say \"cheese\"\nIMG_0001.jpg";
        const string FORMULA_NAME = "=HYPERLINK(\"http://x\",\"y\").jpg";
        VerificationResultItem[] results =
        {
            CreateItem(QUOTED_NAME, ("/mnt/T7", MediaStatus.Verified)),
            CreateItem(FORMULA_NAME, ("/mnt/T7", MediaStatus.Verified)),
        };
        VerificationSummary summary = CreateSummary(2, 2, 0, 0, 0, OverallSafetyStatus.SafeToFormat);

        string csvPath = Path.Combine(_testDir, "roundtrip.csv");
        await new CsvReportGenerator().GenerateReportAsync(summary, results, csvPath);

        List<string[]> rows = ParseCsv(csvPath);
        Assert.Equal(3, rows.Count);
        Assert.Equal(QUOTED_NAME, rows[1][0]);
        Assert.Equal("'" + FORMULA_NAME, rows[2][0]);
    }

    private static VerificationResultItem CreateItem(
        string relativePath,
        params (string RootPath, MediaStatus Status)[] destinations)
    {
        return CreateItem(relativePath, 111UL, 222UL, 333UL, destinations);
    }

    private static VerificationResultItem CreateItem(
        string relativePath,
        ulong? headHash,
        ulong? tailHash,
        ulong? fullHash,
        params (string RootPath, MediaStatus Status)[] destinations)
    {
        MediaFile file = new(
            RelativePath: relativePath,
            FullPath: "/card/" + relativePath,
            FileLength: 1024,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw,
            HeadHash: headHash,
            TailHash: tailHash,
            FullHash: fullHash);

        Dictionary<string, FileMatchStatus> statuses = new();
        for (int index = 0; index < destinations.Length; index++)
        {
            string destinationId = $"dest_{index + 1}";
            (string rootPath, MediaStatus status) = destinations[index];
            statuses[destinationId] = new FileMatchStatus(
                destinationId,
                rootPath,
                status,
                MatchedFilePath: status == MediaStatus.Missing ? null : rootPath + "/" + relativePath,
                DestinationHeadHash: headHash,
                DestinationTailHash: tailHash,
                DestinationFullHash: fullHash);
        }

        return new VerificationResultItem(file, statuses);
    }

    private static VerificationResultItem CreateItemWithStatuses(
        string relativePath,
        params FileMatchStatus[] statuses)
    {
        VerificationResultItem template = CreateItem(relativePath);
        Dictionary<string, FileMatchStatus> statusesById = new();
        foreach (FileMatchStatus status in statuses)
        {
            statusesById[status.DestinationId] = status;
        }

        return new VerificationResultItem(template.SourceFile, statusesById);
    }

    private static void AssertDestinationRow(string html, string encodedLabel, int verified, int missing, int corrupt)
    {
        string pattern = $"<td[^>]*>{Regex.Escape(encodedLabel)}</td>\\s*<td>{verified}</td>\\s*<td>{missing}</td>"
            + $"\\s*<td>{corrupt}</td>";
        Assert.Matches(pattern, html);
    }

    private static string DestinationLine(string encodedLabel, MediaStatus status) =>
        $"<div class=\"dest-line\"><code class=\"dest-label\">{encodedLabel}</code>: {status}</div>";

    private static VerificationSummary CreateSummary(
        int totalFiles,
        int fullyVerified,
        int partiallyVerified,
        int missing,
        int corrupt,
        OverallSafetyStatus safetyStatus)
    {
        return new VerificationSummary(
            TimestampUtc: DateTime.UtcNow,
            SourcePath: "/card",
            Mode: VerificationMode.Full,
            TotalFiles: totalFiles,
            TotalBytes: totalFiles * 1024L,
            FullyVerifiedFiles: fullyVerified,
            PartiallyVerifiedFiles: partiallyVerified,
            MissingFiles: missing,
            CorruptFiles: corrupt,
            SafetyStatus: safetyStatus,
            Duration: TimeSpan.FromSeconds(1));
    }

    private async Task<string> GenerateAsync(
        IReportGenerator generator,
        VerificationSummary summary,
        IReadOnlyList<VerificationResultItem> results,
        string fileName)
    {
        string outputPath = Path.Combine(_testDir, fileName);
        await generator.GenerateReportAsync(summary, results, outputPath);
        return await File.ReadAllTextAsync(outputPath);
    }

    private static ulong ReadHash(string json)
    {
        Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(json));
        reader.Read();
        return new HexUInt64JsonConverter().Read(ref reader, typeof(ulong), new JsonSerializerOptions());
    }

    private static List<string[]> ParseCsv(string path)
    {
        List<string[]> rows = new();
        using TextFieldParser parser = new(path);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        parser.TrimWhiteSpace = false;
        while (!parser.EndOfData)
        {
            string[]? fields = parser.ReadFields();
            if (fields is not null)
            {
                rows.Add(fields);
            }
        }

        return rows;
    }

    // Walks the serialized model graph because the report's hex converter rewrites every ulong it meets.
    private static void CollectUInt64Members(
        Type type,
        JsonSerializerOptions options,
        HashSet<Type> visited,
        List<string> uint64Members)
    {
        if (!visited.Add(type))
        {
            return;
        }

        JsonTypeInfo typeInfo = options.GetTypeInfo(type);
        foreach (Type? childType in new[] { typeInfo.ElementType, typeInfo.KeyType })
        {
            if (childType is null)
            {
                continue;
            }

            if (IsUInt64(childType))
            {
                uint64Members.Add($"{type.Name} element or key");
                continue;
            }

            CollectUInt64Members(childType, options, visited, uint64Members);
        }

        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (IsUInt64(property.PropertyType))
            {
                uint64Members.Add($"{type.Name}.{property.Name}");
                continue;
            }

            CollectUInt64Members(property.PropertyType, options, visited, uint64Members);
        }
    }

    private static bool IsUInt64(Type type) => (Nullable.GetUnderlyingType(type) ?? type) == typeof(ulong);

    private static void CollectUnsafeNumbers(JsonElement element, List<string> unsafeNumbers)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    CollectUnsafeNumbers(property.Value, unsafeNumbers);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    CollectUnsafeNumbers(child, unsafeNumbers);
                }
                break;
            case JsonValueKind.Number:
                if (Math.Abs(element.GetDouble()) > MAX_EXACT_DOUBLE_INTEGER)
                {
                    unsafeNumbers.Add(element.GetRawText());
                }
                break;
        }
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
