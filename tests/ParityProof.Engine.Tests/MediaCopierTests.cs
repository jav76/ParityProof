using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using ParityProof.Engine.Matching;
using ParityProof.Engine.Tests.Logging;
using ParityProof.Engine.Transfer;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace ParityProof.Engine.Tests;

// Shares the Serilog collection because some tests swap the global Log.Logger to capture warnings.
[Collection("SerilogTestCollection")]
public sealed class MediaCopierTests : IDisposable
{
    private const string TEMP_FILE_EXTENSION = ".parityproof.tmp";
    private const long OVERSIZED_FILE_LENGTH = 1L << 60;
    private const string INJECTED_FLUSH_ERROR = "Injected flush failure";
    private const int PARALLEL_FLUSH_TIMEOUT_MS = 10_000;

    private readonly string _testDir;
    private readonly MediaCopier _copier = new();

    public MediaCopierTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ParityProof_CopierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_CopiesAndVerifiesIntegrity()
    {
        string srcDir = Path.Combine(_testDir, "card");
        string dstDir = Path.Combine(_testDir, "backup");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        string srcFilePath = Path.Combine(srcDir, "SUBDIR", "PHOTO_01.CR3");
        Directory.CreateDirectory(Path.GetDirectoryName(srcFilePath)!);

        byte[] payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(srcFilePath, payload);

        MediaFile missingFile = new(
            RelativePath: "SUBDIR/PHOTO_01.CR3",
            FullPath: srcFilePath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { missingFile },
            dstDir);

        Assert.Equal(1, copyResult.CopiedCount);

        string expectedDstPath = Path.Combine(dstDir, "SUBDIR", "PHOTO_01.CR3");
        Assert.True(File.Exists(expectedDstPath));
        Assert.Equal(payload.Length, new FileInfo(expectedDstPath).Length);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_PreservesSourceFileTimestamps()
    {
        string srcDir = Path.Combine(_testDir, "timestamp_src");
        string dstDir = Path.Combine(_testDir, "timestamp_dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        string srcFilePath = Path.Combine(srcDir, "IMG_HISTORIC.JPG");
        byte[] payload = new byte[8 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(srcFilePath, payload);

        DateTime historicLastWrite = new(2023, 5, 12, 14, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(srcFilePath, historicLastWrite);

        MediaFile mediaFile = new(
            RelativePath: "IMG_HISTORIC.JPG",
            FullPath: srcFilePath,
            FileLength: payload.Length,
            LastWriteTimeUtc: historicLastWrite,
            Category: MediaCategory.PhotoStandard);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { mediaFile },
            dstDir);

        Assert.Equal(1, copyResult.CopiedCount);

        string expectedDstPath = Path.Combine(dstDir, "IMG_HISTORIC.JPG");
        Assert.True(File.Exists(expectedDstPath));

        DateTime actualDstLastWrite = File.GetLastWriteTimeUtc(expectedDstPath);
        Assert.True(
            Math.Abs((actualDstLastWrite - historicLastWrite).TotalSeconds) < 2,
            $"Expected LastWriteTimeUtc near {historicLastWrite:O} but got {actualDstLastWrite:O}");
    }

    [Fact]
    public async Task CopyMissingFilesAsync_MultiDestinationFanOut_CopiesToAllDestinationsInSinglePass()
    {
        string srcDir = Path.Combine(_testDir, "src_card");
        string dstDir1 = Path.Combine(_testDir, "dst_nvme");
        string dstDir2 = Path.Combine(_testDir, "dst_ssd");
        string dstDir3 = Path.Combine(_testDir, "dst_archive");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir1);
        Directory.CreateDirectory(dstDir2);
        Directory.CreateDirectory(dstDir3);

        string srcFilePath = Path.Combine(srcDir, "DCIM", "100EOS", "CLIP_001.MOV");
        Directory.CreateDirectory(Path.GetDirectoryName(srcFilePath)!);

        // 256 KB file spanning multiple 64 KB chunks
        byte[] payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(srcFilePath, payload);

        MediaFile missingFile = new(
            RelativePath: "DCIM/100EOS/CLIP_001.MOV",
            FullPath: srcFilePath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.Video);

        List<string> targetDestinations = new() { dstDir1, dstDir2, dstDir3 };

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { missingFile },
            targetDestinations);

        Assert.Equal(1, copyResult.CopiedCount);

        foreach (string destDir in targetDestinations)
        {
            string expectedPath = Path.Combine(destDir, "DCIM", "100EOS", "CLIP_001.MOV");
            Assert.True(File.Exists(expectedPath));
            Assert.Equal(payload.Length, new FileInfo(expectedPath).Length);

            byte[] destBytes = File.ReadAllBytes(expectedPath);
            Assert.Equal(payload, destBytes);
        }
    }

    [Fact]
    public async Task CopyMissingFilesAsync_SmallFileUnder64Kb_CopiesAndVerifies()
    {
        string srcDir = Path.Combine(_testDir, "src_small");
        string dstDir = Path.Combine(_testDir, "dst_small");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        string srcFilePath = Path.Combine(srcDir, "THUMB.JPG");
        byte[] payload = new byte[16 * 1024]; // 16 KB (under 64 KB boundary)
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(srcFilePath, payload);

        MediaFile missingFile = new(
            RelativePath: "THUMB.JPG",
            FullPath: srcFilePath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoStandard);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { missingFile },
            dstDir);

        Assert.Equal(1, copyResult.CopiedCount);
        string expectedPath = Path.Combine(dstDir, "THUMB.JPG");
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(payload.Length, new FileInfo(expectedPath).Length);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_PreExistingBackupOnOneDestination_IsPreservedWhenTransferCancelled()
    {
        string srcDir = Path.Combine(_testDir, "src_qa001");
        string dstDir1 = Path.Combine(_testDir, "dst_existing");
        string dstDir2 = Path.Combine(_testDir, "dst_new");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir1);
        Directory.CreateDirectory(dstDir2);

        string fileName = "PHOTO_QA001.CR3";
        string srcFilePath = Path.Combine(srcDir, fileName);
        byte[] payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(srcFilePath, payload);

        // Pre-create identical intact backup on Destination 1
        string dst1FilePath = Path.Combine(dstDir1, fileName);
        File.WriteAllBytes(dst1FilePath, payload);

        MediaFile missingFile = new(
            RelativePath: fileName,
            FullPath: srcFilePath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Cancel immediately

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _copier.CopyMissingFilesAsync(
                new[] { missingFile },
                new List<string> { dstDir1, dstDir2 },
                cancellationToken: cts.Token);
        });

        // Pre-existing backup on Destination 1 MUST still exist and be intact!
        Assert.True(File.Exists(dst1FilePath), "Pre-existing backup file on Destination 1 must not be deleted on cancellation.");
        Assert.Equal(payload.Length, new FileInfo(dst1FilePath).Length);
        Assert.Equal(payload, File.ReadAllBytes(dst1FilePath));
    }

    [Fact]
    public async Task CopyMissingFilesAsync_SourceAndDestinationAreIdentical_ThrowsInvalidOperationExceptionAndPreservesSource()
    {
        string srcDir = Path.Combine(_testDir, "src_overlap");
        Directory.CreateDirectory(srcDir);

        string fileName = "SOURCE_FILE.CR3";
        string srcFilePath = Path.Combine(srcDir, fileName);
        byte[] payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(srcFilePath, payload);

        MediaFile mediaFile = new(
            RelativePath: fileName,
            FullPath: srcFilePath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);

        // Destination root is set to the source directory itself!
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _copier.CopyMissingFilesAsync(
                new[] { mediaFile },
                srcDir);
        });

        Assert.Contains("Safety violation", ex.Message);
        // Original file must NOT be deleted or truncated!
        Assert.True(File.Exists(srcFilePath));
        Assert.Equal(payload.Length, new FileInfo(srcFilePath).Length);
        Assert.Equal(payload, File.ReadAllBytes(srcFilePath));
    }

    [Theory]
    [InlineData("/volume/card", "/volume/card", false)]
    [InlineData("/volume/card/", "/volume/card", false)]
    [InlineData("/volume/card", "/volume/card/subbackup", false)]
    [InlineData("/volume/card/subfolder", "/volume/card", true)]
    [InlineData("/volume/card/DCIM/100CANON", "/volume/card", true)]
    [InlineData("/volume/card", "/volume/backup_drive", true)]
    public void TryValidateSourceAndDestinationPaths_ValidatesOverlapCorrectly(
        string source,
        string destination,
        bool expectedValid)
    {
        bool isValid = ParityProof.App.ViewModels.MainViewModel.TryValidateSourceAndDestinationPaths(
            source,
            destination,
            out string validationError);

        Assert.Equal(expectedValid, isValid);
        if (!expectedValid)
        {
            Assert.NotEmpty(validationError);
        }
    }

    [Fact]
    public async Task CopyMissingFilesAsync_DifferentFileAtTargetPath_IsPreservedAndSourceWrittenToSibling()
    {
        string dstDir = Path.Combine(_testDir, "collision_dst");
        string dstFolder = Path.Combine(dstDir, "DCIM", "100CANON");
        Directory.CreateDirectory(dstFolder);

        byte[] archivedPayload = CreateRandomPayload(200 * 1024);
        string archivedPath = Path.Combine(dstFolder, "IMG_0001.CR3");
        File.WriteAllBytes(archivedPath, archivedPayload);

        byte[] sourcePayload = CreateRandomPayload(300 * 1024);
        MediaFile sourceFile = CreateSourceFile("collision_src", "DCIM/100CANON/IMG_0001.CR3", sourcePayload);

        CopyProgressInfo? finalProgress = null;
        SynchronousProgress<CopyProgressInfo> progress = new(info => finalProgress = info);
        using WarningCapture warnings = new();

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir,
            progress);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.RenamedCopyCount);
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));
        string siblingPath = AssertSingleSiblingHoldsPayload(dstFolder, archivedPath, sourcePayload);
        Assert.Equal("IMG_0001_1.CR3", Path.GetFileName(siblingPath));

        Assert.NotNull(finalProgress);
        Assert.Equal(1, finalProgress.RenamedCopyCount);

        LogEvent renameWarning = Assert.Single(warnings.GetRenameWarnings());
        AssertPathProperty(renameWarning, "DestinationPath", archivedPath);
        AssertPathProperty(renameWarning, "SourcePath", sourceFile.FullPath);
        AssertPathProperty(renameWarning, "ResolvedPath", siblingPath);
        Assert.Equal(1, Assert.Single(warnings.GetRenamedCopySummaryCounts()));
    }

    [Fact]
    public async Task CopyMissingFilesAsync_SameSizeDifferentFileAtTargetPath_IsPreservedAndSourceWrittenToSibling()
    {
        string dstDir = Path.Combine(_testDir, "same_size_dst");
        string dstFolder = Path.Combine(dstDir, "DCIM", "100CANON");
        Directory.CreateDirectory(dstFolder);

        const int ALTERED_HEAD_BYTES = 32;
        byte[] sourcePayload = CreateRandomPayload(256 * 1024);
        byte[] archivedPayload = (byte[])sourcePayload.Clone();
        for (int i = 0; i < ALTERED_HEAD_BYTES; i++)
        {
            archivedPayload[i] ^= 0xFF;
        }

        string archivedPath = Path.Combine(dstFolder, "IMG_0001.CR3");
        File.WriteAllBytes(archivedPath, archivedPayload);

        MediaFile sourceFile = CreateSourceFile("same_size_src", "DCIM/100CANON/IMG_0001.CR3", sourcePayload);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));
        AssertSingleSiblingHoldsPayload(dstFolder, archivedPath, sourcePayload);
    }

    [CaseInsensitiveTempFact]
    public async Task CopyMissingFilesAsync_CaseInsensitiveDestinationWithDifferentlyCasedFile_IsPreserved()
    {
        string dstDir = Path.Combine(_testDir, "case_dst");
        Directory.CreateDirectory(dstDir);

        byte[] archivedPayload = CreateRandomPayload(96 * 1024);
        string archivedPath = Path.Combine(dstDir, "IMG_1.CR3");
        File.WriteAllBytes(archivedPath, archivedPayload);

        byte[] sourcePayload = CreateRandomPayload(128 * 1024);
        MediaFile sourceFile = CreateSourceFile("case_src", "img_1.cr3", sourcePayload);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));
        AssertSingleSiblingHoldsPayload(dstDir, archivedPath, sourcePayload);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_IdenticalFileAtTargetPath_IsSkippedWithoutCreatingSibling()
    {
        string dstDir = Path.Combine(_testDir, "identical_dst");
        string dstFolder = Path.Combine(dstDir, "DCIM", "100CANON");
        Directory.CreateDirectory(dstFolder);

        byte[] payload = CreateRandomPayload(192 * 1024);
        string existingPath = Path.Combine(dstFolder, "IMG_0001.CR3");
        File.WriteAllBytes(existingPath, payload);

        MediaFile sourceFile = CreateSourceFile("identical_src", "DCIM/100CANON/IMG_0001.CR3", payload);

        CopyProgressInfo? finalProgress = null;
        SynchronousProgress<CopyProgressInfo> progress = new(info => finalProgress = info);
        using WarningCapture warnings = new();

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir,
            progress);

        Assert.Equal(0, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.SkippedCount);
        Assert.Empty(copyResult.Failures);
        Assert.Equal(existingPath, Assert.Single(Directory.GetFiles(dstFolder)));
        Assert.Equal(payload, File.ReadAllBytes(existingPath));
        Assert.NotNull(finalProgress);
        Assert.Equal(0, finalProgress.RenamedCopyCount);
        Assert.Empty(warnings.GetRenameWarnings());
        Assert.Empty(warnings.GetRenamedCopySummaryCounts());
    }

    [Fact]
    public async Task CopyMissingFilesAsync_IdenticalSiblingAfterGapInNumbering_IsSkippedWithoutCreatingDuplicate()
    {
        string dstDir = Path.Combine(_testDir, "gap_dst");
        Directory.CreateDirectory(dstDir);

        byte[] archivedPayload = CreateRandomPayload(200 * 1024);
        string archivedPath = Path.Combine(dstDir, "IMG_0001.CR3");
        File.WriteAllBytes(archivedPath, archivedPayload);

        // IMG_0001_1.CR3 is absent (e.g. deleted by the user), so the identical copy sits past a gap.
        byte[] sourcePayload = CreateRandomPayload(300 * 1024);
        string laterSiblingPath = Path.Combine(dstDir, "IMG_0001_2.CR3");
        File.WriteAllBytes(laterSiblingPath, sourcePayload);

        MediaFile sourceFile = CreateSourceFile("gap_src", "IMG_0001.CR3", sourcePayload);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir);

        Assert.Equal(0, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.SkippedCount);
        Assert.Equal(
            new[] { archivedPath, laterSiblingPath }.Order(StringComparer.Ordinal),
            Directory.GetFiles(dstDir).Order(StringComparer.Ordinal));
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));
        Assert.Equal(sourcePayload, File.ReadAllBytes(laterSiblingPath));
    }

    [Fact]
    public async Task CopyMissingFilesAsync_RepeatedCopyAfterCollision_ReusesIdenticalSiblingInsteadOfDuplicating()
    {
        string dstDir = Path.Combine(_testDir, "repeat_dst");
        string dstFolder = Path.Combine(dstDir, "DCIM", "100CANON");
        Directory.CreateDirectory(dstFolder);

        byte[] archivedPayload = CreateRandomPayload(200 * 1024);
        string archivedPath = Path.Combine(dstFolder, "IMG_0001.CR3");
        File.WriteAllBytes(archivedPath, archivedPayload);

        byte[] sourcePayload = CreateRandomPayload(300 * 1024);
        MediaFile sourceFile = CreateSourceFile("repeat_src", "DCIM/100CANON/IMG_0001.CR3", sourcePayload);

        await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir);
        CopyBatchResult secondResult = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir);

        Assert.Equal(0, secondResult.CopiedCount);
        Assert.Equal(1, secondResult.SkippedCount);
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));
        AssertSingleSiblingHoldsPayload(dstFolder, archivedPath, sourcePayload);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_CollisionOnOneDestination_RenamesOnlyOnThatDestination()
    {
        string dstWithCollision = Path.Combine(_testDir, "fanout_collision_dst");
        string dstClean = Path.Combine(_testDir, "fanout_clean_dst");
        Directory.CreateDirectory(dstWithCollision);
        Directory.CreateDirectory(dstClean);

        byte[] archivedPayload = CreateRandomPayload(200 * 1024);
        string archivedPath = Path.Combine(dstWithCollision, "IMG_0001.CR3");
        File.WriteAllBytes(archivedPath, archivedPayload);

        byte[] sourcePayload = CreateRandomPayload(300 * 1024);
        MediaFile sourceFile = CreateSourceFile("fanout_src", "IMG_0001.CR3", sourcePayload);

        CopyProgressInfo? finalProgress = null;
        SynchronousProgress<CopyProgressInfo> progress = new(info => finalProgress = info);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            new List<string> { dstWithCollision, dstClean },
            progress);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.NotNull(finalProgress);
        Assert.Equal(1, finalProgress.RenamedCopyCount);
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));
        AssertSingleSiblingHoldsPayload(dstWithCollision, archivedPath, sourcePayload);

        string cleanPath = Path.Combine(dstClean, "IMG_0001.CR3");
        Assert.Equal(cleanPath, Assert.Single(Directory.GetFiles(dstClean)));
        Assert.Equal(sourcePayload, File.ReadAllBytes(cleanPath));
    }

    [Fact]
    public async Task CopyMissingFilesAsync_FolderAtTargetPath_IsPreservedAndSourceWrittenToSibling()
    {
        string dstDir = Path.Combine(_testDir, "folder_dst");
        string occupyingFolder = Path.Combine(dstDir, "DCIM", "100CANON", "IMG_0001.CR3");
        Directory.CreateDirectory(occupyingFolder);

        byte[] nestedPayload = CreateRandomPayload(16 * 1024);
        string nestedPath = Path.Combine(occupyingFolder, "notes.txt");
        File.WriteAllBytes(nestedPath, nestedPayload);

        byte[] sourcePayload = CreateRandomPayload(300 * 1024);
        MediaFile sourceFile = CreateSourceFile("folder_src", "DCIM/100CANON/IMG_0001.CR3", sourcePayload);

        CopyProgressInfo? finalProgress = null;
        SynchronousProgress<CopyProgressInfo> progress = new(info => finalProgress = info);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir,
            progress);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.RenamedCopyCount);
        Assert.True(Directory.Exists(occupyingFolder));
        Assert.Equal(nestedPath, Assert.Single(Directory.GetFileSystemEntries(occupyingFolder)));
        Assert.Equal(nestedPayload, File.ReadAllBytes(nestedPath));

        string siblingPath = Path.Combine(dstDir, "DCIM", "100CANON", "IMG_0001_1.CR3");
        Assert.Equal(sourcePayload, File.ReadAllBytes(siblingPath));
        Assert.NotNull(finalProgress);
        Assert.Equal(1, finalProgress.RenamedCopyCount);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_ManyCollisionsInOneFolder_PreservesArchivedFilesAndReusesSibling()
    {
        const int FILE_COUNT = 6;
        const int ALREADY_RENAMED_INDEX = 3;
        string dstDir = Path.Combine(_testDir, "batch_dst");
        string dstFolder = Path.Combine(dstDir, "DCIM", "100CANON");
        Directory.CreateDirectory(dstFolder);

        Dictionary<string, byte[]> expectedFiles = new(StringComparer.Ordinal);
        List<MediaFile> sourceFiles = new();
        for (int i = 1; i <= FILE_COUNT; i++)
        {
            string fileName = $"IMG_{i:D4}.CR3";
            byte[] archivedPayload = CreateRandomPayload(40 * 1024);
            File.WriteAllBytes(Path.Combine(dstFolder, fileName), archivedPayload);
            expectedFiles[fileName] = archivedPayload;

            byte[] sourcePayload = CreateRandomPayload(48 * 1024);
            sourceFiles.Add(CreateSourceFile("batch_src", $"DCIM/100CANON/{fileName}", sourcePayload));
            expectedFiles[$"IMG_{i:D4}_1.CR3"] = sourcePayload;

            if (i == ALREADY_RENAMED_INDEX)
            {
                // A previous run already saved this source under its renamed name.
                File.WriteAllBytes(Path.Combine(dstFolder, $"IMG_{i:D4}_1.CR3"), sourcePayload);
            }
        }

        CopyProgressInfo? finalProgress = null;
        SynchronousProgress<CopyProgressInfo> progress = new(info => finalProgress = info);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            sourceFiles,
            dstDir,
            progress);

        Assert.Equal(FILE_COUNT - 1, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.SkippedCount);
        Assert.Empty(copyResult.Failures);
        Assert.Equal(FILE_COUNT - 1, copyResult.RenamedCopyCount);
        Assert.NotNull(finalProgress);
        Assert.Equal(FILE_COUNT - 1, finalProgress.RenamedCopyCount);
        Assert.Equal(
            expectedFiles.Keys.Order(StringComparer.Ordinal),
            Directory.GetFiles(dstFolder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal));
        foreach ((string fileName, byte[] payload) in expectedFiles)
        {
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(dstFolder, fileName)));
        }
    }

    [Fact]
    public async Task CopyMissingFilesAsync_NamesDifferingOnlyByCaseInOneBatch_BothSurvive()
    {
        string dstDir = Path.Combine(_testDir, "case_batch_dst");
        Directory.CreateDirectory(dstDir);

        // Two source folders let the names differ only by case even when the temp volume is case-insensitive.
        byte[] upperPayload = CreateRandomPayload(64 * 1024);
        byte[] lowerPayload = CreateRandomPayload(72 * 1024);
        MediaFile upperFile = CreateSourceFile("case_batch_src_a", "IMG_1.CR3", upperPayload);
        MediaFile lowerFile = CreateSourceFile("case_batch_src_b", "img_1.cr3", lowerPayload);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { upperFile, lowerFile },
            dstDir);

        Assert.Equal(2, copyResult.CopiedCount);
        string[] writtenFiles = Directory.GetFiles(dstDir);
        Assert.Equal(2, writtenFiles.Length);
        Assert.Contains(writtenFiles, path => File.ReadAllBytes(path).AsSpan().SequenceEqual(upperPayload));
        Assert.Contains(writtenFiles, path => File.ReadAllBytes(path).AsSpan().SequenceEqual(lowerPayload));
    }

    [Fact]
    public async Task CopyMissingFilesAsync_TargetCreatedDuringTransfer_IsPreservedAndSourceCommittedToSibling()
    {
        string dstDir = Path.Combine(_testDir, "race_dst");
        Directory.CreateDirectory(dstDir);

        byte[] sourcePayload = CreateRandomPayload(300 * 1024);
        MediaFile sourceFile = CreateSourceFile("race_src", "IMG_0001.CR3", sourcePayload);

        string targetPath = Path.Combine(dstDir, "IMG_0001.CR3");
        byte[] intruderPayload = CreateRandomPayload(50 * 1024);
        bool intruderWritten = false;
        CopyProgressInfo? finalProgress = null;
        SynchronousProgress<CopyProgressInfo> progress = new(info =>
        {
            if (!intruderWritten && info.CurrentFileCopiedBytes > 0)
            {
                File.WriteAllBytes(targetPath, intruderPayload);
                intruderWritten = true;
            }

            finalProgress = info;
        });
        using WarningCapture warnings = new();

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir,
            progress);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.True(intruderWritten);
        Assert.Equal(intruderPayload, File.ReadAllBytes(targetPath));
        string siblingPath = AssertSingleSiblingHoldsPayload(dstDir, targetPath, sourcePayload);

        Assert.NotNull(finalProgress);
        Assert.Equal(1, finalProgress.RenamedCopyCount);

        LogEvent raceWarning = Assert.Single(warnings.GetRenameWarnings());
        Assert.IsAssignableFrom<IOException>(raceWarning.Exception);
        AssertPathProperty(raceWarning, "DestinationPath", targetPath);
        AssertPathProperty(raceWarning, "ResolvedPath", siblingPath);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_CameraFileNameCollision_ReverifiesSafeAndArchivedPhotoSurvives()
    {
        string cardDir = Path.Combine(_testDir, "e2e_card");
        string ssdDir = Path.Combine(_testDir, "e2e_ssd");
        string ssdFolder = Path.Combine(ssdDir, "DCIM", "100CANON");
        Directory.CreateDirectory(ssdFolder);

        byte[] archivedPayload = CreateRandomPayload(33 * 1024);
        string archivedPath = Path.Combine(ssdFolder, "IMG_0001.CR3");
        File.WriteAllBytes(archivedPath, archivedPayload);

        byte[] cardPayload = CreateRandomPayload(40 * 1024);
        string cardFilePath = Path.Combine(cardDir, "DCIM", "100CANON", "IMG_0001.CR3");
        Directory.CreateDirectory(Path.GetDirectoryName(cardFilePath)!);
        File.WriteAllBytes(cardFilePath, cardPayload);

        List<BackupDestination> destinations = new()
        {
            new BackupDestination("ssd", "Primary SSD", ssdDir),
        };
        MultiDestinationVerifier verifier = new();

        (VerificationSummary firstSummary, IReadOnlyList<VerificationResultItem> firstResults) =
            await verifier.VerifyAsync(
                cardDir,
                destinations,
                VerificationMode.Quick,
                FilterPreset.PhotosOnly);

        Assert.Equal(1, firstSummary.MissingFiles);

        List<MediaFile> missingFiles = firstResults
            .Where(result => !result.IsFullyVerified)
            .Select(result => result.SourceFile)
            .ToList();

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            missingFiles,
            ssdDir);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));

        (VerificationSummary secondSummary, _) = await verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Quick,
            FilterPreset.PhotosOnly);

        Assert.Equal(OverallSafetyStatus.SafeToFormat, secondSummary.SafetyStatus);
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));
    }

    [Fact]
    public async Task CopyMissingFilesAsync_UnreadableSourceFile_ContinuesWithRemainingFiles()
    {
        string dstDir = Path.Combine(_testDir, "unreadable_dst");
        Directory.CreateDirectory(dstDir);

        MediaFile unreadableFile = CreateSourceFile(
            "unreadable_src",
            "DCIM/100CANON/IMG_0012.CR3",
            CreateRandomPayload(96 * 1024));
        byte[] readablePayload = CreateRandomPayload(128 * 1024);
        MediaFile readableFile = CreateSourceFile("unreadable_src", "DCIM/100CANON/IMG_0013.CR3", readablePayload);
        File.Delete(unreadableFile.FullPath);

        CopyProgressInfo? finalProgress = null;
        SynchronousProgress<CopyProgressInfo> progress = new(info => finalProgress = info);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { unreadableFile, readableFile },
            dstDir,
            progress);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.FailedFileCount);
        CopyFailure failure = Assert.Single(copyResult.Failures);
        Assert.Equal(unreadableFile.RelativePath, failure.RelativePath);
        Assert.Equal(dstDir, failure.DestinationRootPath);

        // The final report must not claim the failed file was verified.
        Assert.NotNull(finalProgress);
        Assert.Equal(1, finalProgress.FilesCompleted);
        StageProgressInfo verifyStage = Assert.Single(
            finalProgress.Stages!,
            stage => stage.Id == PipelineStageId.PostTransferVerify);
        Assert.Equal("1 / 2 files verified", verifyStage.ProgressText);

        Assert.Equal(readablePayload, File.ReadAllBytes(Path.Combine(dstDir, readableFile.RelativePath)));
        Assert.Equal(
            Path.Combine(dstDir, readableFile.RelativePath),
            Assert.Single(Directory.GetFiles(dstDir, "*", SearchOption.AllDirectories)));
    }

    [Fact]
    public async Task CopyMissingFilesAsync_UnreadableSourceDuringSkipCheck_ContinuesAndPreservesExistingFile()
    {
        string dstDir = Path.Combine(_testDir, "skipcheck_dst");
        string dstFolder = Path.Combine(dstDir, "DCIM", "100CANON");
        Directory.CreateDirectory(dstFolder);

        byte[] unreadablePayload = CreateRandomPayload(96 * 1024);
        MediaFile unreadableFile = CreateSourceFile("skipcheck_src", "DCIM/100CANON/IMG_0012.CR3", unreadablePayload);
        byte[] readablePayload = CreateRandomPayload(128 * 1024);
        MediaFile readableFile = CreateSourceFile("skipcheck_src", "DCIM/100CANON/IMG_0013.CR3", readablePayload);

        // A same-length file at the target path forces a head/tail read of the source before any stream opens.
        byte[] archivedPayload = CreateRandomPayload(unreadablePayload.Length);
        string archivedPath = Path.Combine(dstFolder, "IMG_0012.CR3");
        File.WriteAllBytes(archivedPath, archivedPayload);

        // An exclusive lock makes every read of the source fail, like a card with a bad sector.
        using FileStream sourceLock = new(unreadableFile.FullPath, FileMode.Open, FileAccess.Read, FileShare.None);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { unreadableFile, readableFile },
            dstDir);

        Assert.Equal(1, copyResult.CopiedCount);
        CopyFailure failure = Assert.Single(copyResult.Failures);
        Assert.Equal(unreadableFile.RelativePath, failure.RelativePath);

        Assert.Equal(readablePayload, File.ReadAllBytes(Path.Combine(dstFolder, "IMG_0013.CR3")));
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));
        Assert.Equal(2, Directory.GetFiles(dstFolder).Length);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_UnreadableSourceDuringSiblingCheck_ContinuesAndPreservesExistingFiles()
    {
        string dstDir = Path.Combine(_testDir, "siblingcheck_dst");
        string dstFolder = Path.Combine(dstDir, "DCIM", "100CANON");
        Directory.CreateDirectory(dstFolder);

        byte[] unreadablePayload = CreateRandomPayload(96 * 1024);
        MediaFile unreadableFile = CreateSourceFile(
            "siblingcheck_src",
            "DCIM/100CANON/IMG_0012.CR3",
            unreadablePayload);
        byte[] readablePayload = CreateRandomPayload(128 * 1024);
        MediaFile readableFile = CreateSourceFile(
            "siblingcheck_src",
            "DCIM/100CANON/IMG_0013.CR3",
            readablePayload);

        // The target path holds a different-length file, so only the same-length renamed sibling forces a
        // head/tail read of the source.
        byte[] archivedPayload = CreateRandomPayload(64 * 1024);
        string archivedPath = Path.Combine(dstFolder, "IMG_0012.CR3");
        File.WriteAllBytes(archivedPath, archivedPayload);
        byte[] siblingPayload = CreateRandomPayload(unreadablePayload.Length);
        string siblingPath = Path.Combine(dstFolder, "IMG_0012_1.CR3");
        File.WriteAllBytes(siblingPath, siblingPayload);

        using FileStream sourceLock = new(unreadableFile.FullPath, FileMode.Open, FileAccess.Read, FileShare.None);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { unreadableFile, readableFile },
            dstDir);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.FailedFileCount);
        CopyFailure failure = Assert.Single(copyResult.Failures);
        Assert.Equal(unreadableFile.RelativePath, failure.RelativePath);

        Assert.Equal(readablePayload, File.ReadAllBytes(Path.Combine(dstFolder, "IMG_0013.CR3")));
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));
        Assert.Equal(siblingPayload, File.ReadAllBytes(siblingPath));
        Assert.Equal(3, Directory.GetFiles(dstFolder).Length);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_OneDestinationFails_OtherDestinationReceivesAllFiles()
    {
        string healthyDst = Path.Combine(_testDir, "healthy_dst");
        Directory.CreateDirectory(healthyDst);

        // A destination root nested under a regular file can never be created on any platform.
        string blockerFilePath = Path.Combine(_testDir, "blocker.bin");
        File.WriteAllBytes(blockerFilePath, CreateRandomPayload(16));
        string brokenDst = Path.Combine(blockerFilePath, "backup");

        List<(MediaFile File, byte[] Payload)> sources = new();
        for (int i = 1; i <= 3; i++)
        {
            byte[] payload = CreateRandomPayload(64 * 1024 + i);
            sources.Add((CreateSourceFile("broken_dest_src", $"DCIM/100CANON/IMG_000{i}.CR3", payload), payload));
        }

        using WarningCapture logCapture = new();

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            sources.Select(source => source.File).ToList(),
            new List<string> { healthyDst, brokenDst });

        foreach ((MediaFile file, byte[] payload) in sources)
        {
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(healthyDst, file.RelativePath)));
        }

        Assert.Equal(0, copyResult.CopiedCount);
        Assert.Equal(sources.Count, copyResult.FailedFileCount);
        Assert.Equal(sources.Count, copyResult.Failures.Count);
        Assert.All(copyResult.Failures, failure => Assert.Equal(brokenDst, failure.DestinationRootPath));
        Assert.Equal(
            sources.Select(source => source.File.RelativePath),
            copyResult.Failures.Select(failure => failure.RelativePath));

        // The unreachable root is left out after its first failure, so the later files are neither attempted nor
        // logged again (an unplugged stick must not log one stack trace per remaining file).
        string firstErrorMessage = copyResult.Failures[0].ErrorMessage;
        Assert.All(
            copyResult.Failures.Skip(1),
            failure => Assert.NotEqual(firstErrorMessage, failure.ErrorMessage));
        Assert.Single(copyResult.Failures.Skip(1).Select(failure => failure.ErrorMessage).Distinct());
        Assert.Single(logCapture.GetErrors());
    }

    [LinuxSpecialFileFact(LinuxSpecialFileFactAttribute.DEV_FULL_PATH)]
    public async Task CopyMissingFilesAsync_DestinationFullDuringWrite_OtherDestinationAndLaterFilesThatFitStillCopied()
    {
        string healthyDst = Path.Combine(_testDir, "full_healthy_dst");
        string fullDst = Path.Combine(_testDir, "full_dst");
        Directory.CreateDirectory(healthyDst);
        Directory.CreateDirectory(fullDst);

        byte[] firstPayload = CreateRandomPayload(256 * 1024);
        MediaFile firstFile = CreateSourceFile("full_src", "IMG_0001.CR3", firstPayload);
        byte[] secondPayload = CreateRandomPayload(128 * 1024);
        MediaFile secondFile = CreateSourceFile("full_src", "IMG_0002.CR3", secondPayload);

        // The copier streams into <target>.parityproof.tmp; pointing that at /dev/full makes the fan-out write
        // for this destination fail with ENOSPC mid-transfer while the healthy destination keeps writing.
        File.CreateSymbolicLink(
            Path.Combine(fullDst, "IMG_0001.CR3" + TEMP_FILE_EXTENSION),
            LinuxSpecialFileFactAttribute.DEV_FULL_PATH);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { firstFile, secondFile },
            new List<string> { healthyDst, fullDst });

        Assert.Equal(firstPayload, File.ReadAllBytes(Path.Combine(healthyDst, "IMG_0001.CR3")));
        Assert.Equal(secondPayload, File.ReadAllBytes(Path.Combine(healthyDst, "IMG_0002.CR3")));

        // The temp volume still has room for IMG_0002, so the ENOSPC on IMG_0001 does not leave it out.
        Assert.Equal(secondPayload, File.ReadAllBytes(Path.Combine(fullDst, "IMG_0002.CR3")));
        Assert.Equal(Path.Combine(fullDst, "IMG_0002.CR3"), Assert.Single(Directory.GetFileSystemEntries(fullDst)));
        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.FailedFileCount);
        CopyFailure failure = Assert.Single(copyResult.Failures);
        Assert.Equal("IMG_0001.CR3", failure.RelativePath);
        Assert.Equal(fullDst, failure.DestinationRootPath);
    }

    [LinuxSpecialFileFact(LinuxSpecialFileFactAttribute.DEV_FULL_PATH)]
    public async Task CopyMissingFilesAsync_DestinationFull_LeavesOutOnlyFilesLargerThanFreeSpace()
    {
        string fullDst = Path.Combine(_testDir, "fit_dst");
        Directory.CreateDirectory(fullDst);

        MediaFile firstFile = CreateSourceFile("fit_src", "IMG_0001.CR3", CreateRandomPayload(128 * 1024));

        // Its metadata claims more than any disk holds, standing in for a clip larger than the space left.
        MediaFile oversizedClip = CreateSourceFile("fit_src", "CLIP_0002.MOV", CreateRandomPayload(64 * 1024)) with
        {
            FileLength = OVERSIZED_FILE_LENGTH,
        };
        byte[] thirdPayload = CreateRandomPayload(96 * 1024);
        MediaFile thirdFile = CreateSourceFile("fit_src", "IMG_0003.CR3", thirdPayload);

        File.CreateSymbolicLink(
            Path.Combine(fullDst, "IMG_0001.CR3" + TEMP_FILE_EXTENSION),
            LinuxSpecialFileFactAttribute.DEV_FULL_PATH);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { firstFile, oversizedClip, thirdFile },
            fullDst);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(2, copyResult.FailedFileCount);
        Assert.Equal(
            new[] { "IMG_0001.CR3", "CLIP_0002.MOV" },
            copyResult.Failures.Select(failure => failure.RelativePath));

        // The clip is left out by the free-space check, not attempted and then failed by size verification.
        Assert.Contains("free space", copyResult.Failures[1].ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(thirdPayload, File.ReadAllBytes(Path.Combine(fullDst, "IMG_0003.CR3")));
        Assert.Equal(Path.Combine(fullDst, "IMG_0003.CR3"), Assert.Single(Directory.GetFileSystemEntries(fullDst)));
    }

    [Fact]
    public async Task CopyMissingFilesAsync_UnreadableSourceWithTwoDestinations_FailsFileOncePerDestination()
    {
        string dstWithSameLengthFile = Path.Combine(_testDir, "two_dest_source_dst1");
        string emptyDst = Path.Combine(_testDir, "two_dest_source_dst2");
        Directory.CreateDirectory(Path.Combine(dstWithSameLengthFile, "DCIM", "101CANON"));
        Directory.CreateDirectory(emptyDst);

        byte[] unreadablePayload = CreateRandomPayload(96 * 1024);
        MediaFile unreadableFile = CreateSourceFile(
            "two_dest_source_src",
            "DCIM/101CANON/IMG_0012.CR3",
            unreadablePayload);
        byte[] readablePayload = CreateRandomPayload(128 * 1024);
        MediaFile readableFile = CreateSourceFile(
            "two_dest_source_src",
            "DCIM/100CANON/IMG_0013.CR3",
            readablePayload);

        // The same-length file on the first destination forces a head/tail read of the locked source.
        byte[] archivedPayload = CreateRandomPayload(unreadablePayload.Length);
        string archivedPath = Path.Combine(dstWithSameLengthFile, "DCIM", "101CANON", "IMG_0012.CR3");
        File.WriteAllBytes(archivedPath, archivedPayload);

        using FileStream sourceLock = new(unreadableFile.FullPath, FileMode.Open, FileAccess.Read, FileShare.None);

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { unreadableFile, readableFile },
            new List<string> { dstWithSameLengthFile, emptyDst });

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.FailedFileCount);
        Assert.Equal(2, copyResult.Failures.Count);
        Assert.All(copyResult.Failures, failure => Assert.Equal(unreadableFile.RelativePath, failure.RelativePath));
        Assert.Equal(
            new[] { dstWithSameLengthFile, emptyDst },
            copyResult.Failures.Select(failure => failure.DestinationRootPath));

        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));
        foreach (string dstDir in new[] { dstWithSameLengthFile, emptyDst })
        {
            Assert.Equal(readablePayload, File.ReadAllBytes(Path.Combine(dstDir, readableFile.RelativePath)));
            Assert.Empty(Directory.GetFiles(dstDir, "*" + TEMP_FILE_EXTENSION, SearchOption.AllDirectories));
        }

        // The source is read once: its failure stops the file on every destination before the second one is
        // touched, instead of re-reading a failing card once per destination.
        Assert.False(Directory.Exists(Path.Combine(emptyDst, "DCIM", "101CANON")));
    }

    [LinuxSpecialFileFact(LinuxSpecialFileFactAttribute.PROC_SELF_MEM_PATH)]
    public async Task CopyMissingFilesAsync_SourceReadErrorMidStreamWithTwoDestinations_RemovesTempFilesAndContinues()
    {
        string dstDir1 = Path.Combine(_testDir, "eio_dst1");
        string dstDir2 = Path.Combine(_testDir, "eio_dst2");
        Directory.CreateDirectory(dstDir1);
        Directory.CreateDirectory(dstDir2);

        // Opening succeeds, so both temp files are created before the first read fails with EIO.
        MediaFile badSectorFile = new(
            RelativePath: "DCIM/100CANON/IMG_0012.CR3",
            FullPath: LinuxSpecialFileFactAttribute.PROC_SELF_MEM_PATH,
            FileLength: 96 * 1024,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);
        byte[] readablePayload = CreateRandomPayload(128 * 1024);
        MediaFile readableFile = CreateSourceFile("eio_src", "DCIM/100CANON/IMG_0013.CR3", readablePayload);

        using WarningCapture logCapture = new();

        CopyBatchResult copyResult = await _copier.CopyMissingFilesAsync(
            new[] { badSectorFile, readableFile },
            new List<string> { dstDir1, dstDir2 });

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.FailedFileCount);
        Assert.Equal(2, copyResult.Failures.Count);
        Assert.All(copyResult.Failures, failure => Assert.Equal(badSectorFile.RelativePath, failure.RelativePath));
        Assert.Equal(new[] { dstDir1, dstDir2 }, copyResult.Failures.Select(failure => failure.DestinationRootPath));

        // One source error is shared by both destinations and logged once, not once per destination.
        Assert.Single(copyResult.Failures.Select(failure => failure.ErrorMessage).Distinct());
        Assert.IsAssignableFrom<IOException>(Assert.Single(logCapture.GetErrors()).Exception);

        foreach (string dstDir in new[] { dstDir1, dstDir2 })
        {
            string dstFolder = Path.Combine(dstDir, "DCIM", "100CANON");
            Assert.Equal(readablePayload, File.ReadAllBytes(Path.Combine(dstFolder, "IMG_0013.CR3")));
            Assert.Equal(Path.Combine(dstFolder, "IMG_0013.CR3"), Assert.Single(Directory.GetFiles(dstFolder)));
        }
    }

    [Fact]
    public async Task CopyMissingFilesAsync_CancelledDuringMultiDestinationCopy_PropagatesAndLeavesNoPartialFiles()
    {
        string dstDir1 = Path.Combine(_testDir, "cancel_dst1");
        string dstDir2 = Path.Combine(_testDir, "cancel_dst2");
        Directory.CreateDirectory(dstDir1);
        Directory.CreateDirectory(dstDir2);

        MediaFile sourceFile = CreateSourceFile("cancel_src", "CLIP_0001.MOV", CreateRandomPayload(8 * 1024 * 1024));

        using CancellationTokenSource cts = new();
        SynchronousProgress<CopyProgressInfo> progress = new(info =>
        {
            if (info.CurrentFileCopiedBytes > 0)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _copier.CopyMissingFilesAsync(
                new[] { sourceFile },
                new List<string> { dstDir1, dstDir2 },
                progress,
                cts.Token);
        });

        Assert.Empty(Directory.GetFileSystemEntries(dstDir1));
        Assert.Empty(Directory.GetFileSystemEntries(dstDir2));
    }

    [Fact]
    public async Task CopyMissingFilesAsync_FlushesEachDestinationBeforeReadBackAndRename()
    {
        string dstDir1 = Path.Combine(_testDir, "durable_dst1");
        string dstDir2 = Path.Combine(_testDir, "durable_dst2");
        Directory.CreateDirectory(dstDir1);
        Directory.CreateDirectory(dstDir2);

        byte[] payload = CreateRandomPayload(256 * 1024);
        MediaFile sourceFile = CreateSourceFile("durable_src", "DCIM/100CANON/IMG_0001.CR3", payload);
        RecordingCommitOperations commitOperations = new();

        CopyBatchResult copyResult = await new MediaCopier(commitOperations).CopyMissingFilesAsync(
            new[] { sourceFile },
            new List<string> { dstDir1, dstDir2 });

        Assert.Equal(1, copyResult.CopiedCount);
        foreach (string dstDir in new[] { dstDir1, dstDir2 })
        {
            string finalPath = Path.GetFullPath(Path.Combine(dstDir, sourceFile.RelativePath));
            string tempPath = finalPath + TEMP_FILE_EXTENSION;
            string folderPath = Path.GetDirectoryName(finalPath)!;

            // The new DCIM/100CANON folders are linked into their parents first. The read-back must see what the
            // drive holds, not dirty pages in RAM, and the file only counts as copied once its new folder entry is
            // on the drive too.
            Assert.Equal(
                new[]
                {
                    new CommitStep(CommitStepKind.FlushDirectory, Path.GetDirectoryName(folderPath)!),
                    new CommitStep(CommitStepKind.FlushDirectory, Path.GetFullPath(dstDir)),
                    new CommitStep(CommitStepKind.FlushToDisk, tempPath),
                    new CommitStep(CommitStepKind.ReadBack, tempPath),
                    new CommitStep(CommitStepKind.Rename, finalPath),
                    new CommitStep(CommitStepKind.FlushDirectory, folderPath),
                },
                commitOperations.GetStepsUnder(dstDir));
            Assert.Equal(payload, File.ReadAllBytes(finalPath));
        }
    }

    [Fact]
    public async Task CopyMissingFilesAsync_FlushFailure_DoesNotCommit()
    {
        string healthyDst = Path.Combine(_testDir, "flush_healthy_dst");
        string failingDst = Path.Combine(_testDir, "flush_failing_dst");
        Directory.CreateDirectory(healthyDst);
        Directory.CreateDirectory(failingDst);

        byte[] payload = CreateRandomPayload(96 * 1024);
        MediaFile sourceFile = CreateSourceFile("flush_src", "DCIM/100CANON/IMG_0002.CR3", payload);
        RecordingCommitOperations commitOperations = new()
        {
            ShouldFail = step => step.Kind == CommitStepKind.FlushToDisk && IsAtOrUnder(step.Path, failingDst),
        };

        CopyBatchResult copyResult = await new MediaCopier(commitOperations).CopyMissingFilesAsync(
            new[] { sourceFile },
            new List<string> { healthyDst, failingDst });

        // Data the drive never confirmed is not read back or moved into place, and its temp file is removed.
        string failingPath = Path.Combine(failingDst, sourceFile.RelativePath);
        Assert.False(File.Exists(failingPath));
        Assert.False(File.Exists(failingPath + TEMP_FILE_EXTENSION));
        Assert.Equal(
            new[] { CommitStepKind.FlushDirectory, CommitStepKind.FlushDirectory, CommitStepKind.FlushToDisk },
            commitOperations.GetStepsUnder(failingDst).Select(step => step.Kind));

        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(healthyDst, sourceFile.RelativePath)));
        Assert.Equal(0, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.FailedFileCount);
        CopyFailure failure = Assert.Single(copyResult.Failures);
        Assert.Equal(failingDst, failure.DestinationRootPath);
        Assert.Equal(INJECTED_FLUSH_ERROR, failure.ErrorMessage);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_DirectoryFlushFailure_RemovesCopyAndReportsFailure()
    {
        string healthyDst = Path.Combine(_testDir, "dirflush_healthy_dst");
        string failingDst = Path.Combine(_testDir, "dirflush_failing_dst");
        Directory.CreateDirectory(healthyDst);
        Directory.CreateDirectory(failingDst);

        byte[] payload = CreateRandomPayload(96 * 1024);
        MediaFile sourceFile = CreateSourceFile("dirflush_src", "DCIM/100CANON/IMG_0003.CR3", payload);
        string failingFolder = Path.GetFullPath(Path.Combine(failingDst, "DCIM", "100CANON"));
        RecordingCommitOperations commitOperations = new()
        {
            ShouldFail = step => step.Kind == CommitStepKind.FlushDirectory && step.Path == failingFolder,
        };

        CopyBatchResult copyResult = await new MediaCopier(commitOperations).CopyMissingFilesAsync(
            new[] { sourceFile },
            new List<string> { healthyDst, failingDst });

        // The renamed copy is taken back out, so the verification that follows a copy cannot count a file whose
        // folder entry may be lost when the drive is unplugged.
        Assert.Equal(
            new[]
            {
                CommitStepKind.FlushDirectory,
                CommitStepKind.FlushDirectory,
                CommitStepKind.FlushToDisk,
                CommitStepKind.ReadBack,
                CommitStepKind.Rename,
                CommitStepKind.FlushDirectory,
            },
            commitOperations.GetStepsUnder(failingDst).Select(step => step.Kind));
        Assert.Empty(Directory.GetFiles(failingDst, "*", SearchOption.AllDirectories));

        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(healthyDst, sourceFile.RelativePath)));
        Assert.Equal(0, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.FailedFileCount);
        CopyFailure failure = Assert.Single(copyResult.Failures);
        Assert.Equal(failingDst, failure.DestinationRootPath);
        Assert.Equal(INJECTED_FLUSH_ERROR, failure.ErrorMessage);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_NewFolderFlushFailure_SkipsFileAndRetriesForNextFile()
    {
        string dstDir = Path.Combine(_testDir, "newfolder_dst");
        Directory.CreateDirectory(dstDir);

        byte[] firstPayload = CreateRandomPayload(32 * 1024);
        byte[] secondPayload = CreateRandomPayload(48 * 1024);
        MediaFile firstFile = CreateSourceFile("newfolder_src", "DCIM/100CANON/IMG_0004.CR3", firstPayload);
        MediaFile secondFile = CreateSourceFile("newfolder_src", "DCIM/100CANON/IMG_0005.CR3", secondPayload);

        string rootPath = Path.GetFullPath(dstDir);
        string dcimPath = Path.Combine(rootPath, "DCIM");
        string folderPath = Path.Combine(dcimPath, "100CANON");
        int rootFlushCount = 0;
        RecordingCommitOperations commitOperations = new()
        {
            // Only the first flush of the folder that holds the new DCIM entry fails.
            ShouldFail = step => step.Kind == CommitStepKind.FlushDirectory
                && step.Path == rootPath
                && ++rootFlushCount == 1,
        };

        CopyBatchResult copyResult = await new MediaCopier(commitOperations).CopyMissingFilesAsync(
            new[] { firstFile, secondFile },
            dstDir);

        // Nothing is written below a new folder whose entry the drive has not confirmed. The next file retries
        // that flush instead of trusting the folder because it now exists.
        string secondFinalPath = Path.Combine(folderPath, "IMG_0005.CR3");
        string secondTempPath = secondFinalPath + TEMP_FILE_EXTENSION;
        Assert.Equal(
            new[]
            {
                new CommitStep(CommitStepKind.FlushDirectory, dcimPath),
                new CommitStep(CommitStepKind.FlushDirectory, rootPath),
                new CommitStep(CommitStepKind.FlushDirectory, rootPath),
                new CommitStep(CommitStepKind.FlushToDisk, secondTempPath),
                new CommitStep(CommitStepKind.ReadBack, secondTempPath),
                new CommitStep(CommitStepKind.Rename, secondFinalPath),
                new CommitStep(CommitStepKind.FlushDirectory, folderPath),
            },
            commitOperations.GetStepsUnder(dstDir));

        Assert.False(File.Exists(Path.Combine(folderPath, "IMG_0004.CR3")));
        Assert.Equal(secondPayload, File.ReadAllBytes(secondFinalPath));
        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(1, copyResult.FailedFileCount);
        CopyFailure failure = Assert.Single(copyResult.Failures);
        Assert.Equal(firstFile.RelativePath, failure.RelativePath);
        Assert.Equal(INJECTED_FLUSH_ERROR, failure.ErrorMessage);
    }

    [Fact]
    public async Task CopyMissingFilesAsync_SetsTimestampsBeforeFlush()
    {
        string dstDir = Path.Combine(_testDir, "stamp_dst");
        Directory.CreateDirectory(dstDir);

        MediaFile writtenFile = CreateSourceFile("stamp_src", "IMG_0006.JPG", CreateRandomPayload(16 * 1024));
        DateTime historicLastWrite = new(2022, 8, 1, 9, 15, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(writtenFile.FullPath, historicLastWrite);
        MediaFile sourceFile = writtenFile with { LastWriteTimeUtc = historicLastWrite };
        RecordingCommitOperations commitOperations = new();

        CopyBatchResult copyResult = await new MediaCopier(commitOperations).CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir);

        // Setting them after the flush would leave dirty metadata behind once the copy is reported safe, and an
        // unplug could revert the time that a later quick verify compares.
        Assert.Equal(1, copyResult.CopiedCount);
        DateTime lastWriteAtFlush = Assert.Single(commitOperations.GetLastWriteTimesAtFlush());
        Assert.True(
            Math.Abs((lastWriteAtFlush - historicLastWrite).TotalSeconds) < 2,
            $"Expected LastWriteTimeUtc near {historicLastWrite:O} at the flush but got {lastWriteAtFlush:O}");
    }

    [Fact]
    public async Task CopyMissingFilesAsync_FlushesDestinationsInParallel()
    {
        string dstDir1 = Path.Combine(_testDir, "parallel_dst1");
        string dstDir2 = Path.Combine(_testDir, "parallel_dst2");
        Directory.CreateDirectory(dstDir1);
        Directory.CreateDirectory(dstDir2);

        MediaFile sourceFile = CreateSourceFile("parallel_src", "IMG_0007.CR3", CreateRandomPayload(64 * 1024));
        using Barrier bothFlushing = new(participantCount: 2);
        bool flushesOverlapped = true;
        RecordingCommitOperations commitOperations = new()
        {
            OnFlushToDisk = () =>
            {
                if (!bothFlushing.SignalAndWait(PARALLEL_FLUSH_TIMEOUT_MS))
                {
                    flushesOverlapped = false;
                }
            },
        };

        CopyBatchResult copyResult = await new MediaCopier(commitOperations).CopyMissingFilesAsync(
            new[] { sourceFile },
            new List<string> { dstDir1, dstDir2 });

        // One slow drive must not add its whole flush time to every other destination's.
        Assert.True(flushesOverlapped, "The destinations were flushed one after another.");
        Assert.Equal(1, copyResult.CopiedCount);
    }

    private MediaFile CreateSourceFile(string sourceFolderName, string relativePath, byte[] payload)
    {
        string sourcePath = Path.Combine(_testDir, sourceFolderName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllBytes(sourcePath, payload);

        return new MediaFile(
            RelativePath: relativePath,
            FullPath: sourcePath,
            FileLength: payload.Length,
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: MediaCategory.PhotoRaw);
    }

    private static byte[] CreateRandomPayload(int length)
    {
        byte[] payload = new byte[length];
        Random.Shared.NextBytes(payload);
        return payload;
    }

    private static string AssertSingleSiblingHoldsPayload(string folder, string preservedPath, byte[] expectedPayload)
    {
        string preservedName = Path.GetFileName(preservedPath);
        string[] otherFiles = Directory.GetFiles(folder)
            .Where(path => !string.Equals(Path.GetFileName(path), preservedName, StringComparison.Ordinal))
            .ToArray();

        string siblingPath = Assert.Single(otherFiles);
        Assert.Equal(expectedPayload, File.ReadAllBytes(siblingPath));
        return siblingPath;
    }

    private static void AssertPathProperty(LogEvent logEvent, string propertyName, string expectedPath)
    {
        ScalarValue scalar = Assert.IsType<ScalarValue>(logEvent.Properties[propertyName]);
        string actualPath = Assert.IsType<string>(scalar.Value);
        Assert.Equal(Path.GetFullPath(expectedPath), Path.GetFullPath(actualPath));
    }

    private sealed class WarningCapture : IDisposable
    {
        private readonly ILogger _previousLogger;
        private readonly Logger _captureLogger;
        private readonly TestLogSink _sink = new();

        public WarningCapture()
        {
            _previousLogger = Log.Logger;
            _captureLogger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Sink(_sink)
                .CreateLogger();
            Log.Logger = _captureLogger;
        }

        public IReadOnlyList<LogEvent> GetRenameWarnings()
        {
            return _sink.Events
                .Where(logEvent => logEvent.Level == LogEventLevel.Warning)
                .Where(logEvent => logEvent.Properties.ContainsKey("ResolvedPath"))
                .ToList();
        }

        public IReadOnlyList<LogEvent> GetErrors()
        {
            return _sink.Events
                .Where(logEvent => logEvent.Level == LogEventLevel.Error)
                .ToList();
        }

        public IReadOnlyList<int> GetRenamedCopySummaryCounts()
        {
            return _sink.Events
                .Where(logEvent => logEvent.Level == LogEventLevel.Warning)
                .Select(logEvent => logEvent.Properties.GetValueOrDefault("RenamedCopyCount"))
                .OfType<ScalarValue>()
                .Select(scalar => Assert.IsType<int>(scalar.Value))
                .ToList();
        }

        public void Dispose()
        {
            Log.Logger = _previousLogger;
            _captureLogger.Dispose();
        }
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }

    private enum CommitStepKind
    {
        FlushToDisk,
        ReadBack,
        Rename,
        FlushDirectory,
    }

    private sealed record CommitStep(CommitStepKind Kind, string Path);

    private static bool IsAtOrUnder(string fullPath, string root)
    {
        string rootPath = Path.GetFullPath(root);
        return string.Equals(fullPath, rootPath, StringComparison.Ordinal)
            || fullPath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // Records each commit step, then runs the real one unless ShouldFail picks it. Destinations flush in parallel,
    // so recording is locked.
    private sealed class RecordingCommitOperations : ICopyCommitOperations
    {
        private readonly DurableCommitOperations _inner = new();
        private readonly List<CommitStep> _steps = new();
        private readonly List<DateTime> _lastWriteTimesAtFlush = new();
        private readonly object _gate = new();

        public Func<CommitStep, bool>? ShouldFail { get; init; }

        public Action? OnFlushToDisk { get; init; }

        public void FlushToDisk(FileStream tempStream)
        {
            Record(CommitStepKind.FlushToDisk, tempStream.Name);
            DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(tempStream.SafeFileHandle);
            lock (_gate)
            {
                _lastWriteTimesAtFlush.Add(lastWriteTimeUtc);
            }

            OnFlushToDisk?.Invoke();
            _inner.FlushToDisk(tempStream);
        }

        public (ulong HeadHash, ulong TailHash) ReadBackHeadTail(string tempPath)
        {
            Record(CommitStepKind.ReadBack, tempPath);
            return _inner.ReadBackHeadTail(tempPath);
        }

        public void Rename(string tempPath, string finalPath)
        {
            Record(CommitStepKind.Rename, finalPath);
            _inner.Rename(tempPath, finalPath);
        }

        public void FlushDirectory(string directoryPath)
        {
            Record(CommitStepKind.FlushDirectory, directoryPath);
            _inner.FlushDirectory(directoryPath);
        }

        public IReadOnlyList<CommitStep> GetStepsUnder(string destinationRoot)
        {
            lock (_gate)
            {
                return _steps
                    .Where(step => IsAtOrUnder(step.Path, destinationRoot))
                    .ToList();
            }
        }

        public IReadOnlyList<DateTime> GetLastWriteTimesAtFlush()
        {
            lock (_gate)
            {
                return _lastWriteTimesAtFlush.ToList();
            }
        }

        private void Record(CommitStepKind kind, string path)
        {
            CommitStep step = new(kind, Path.GetFullPath(path));
            lock (_gate)
            {
                _steps.Add(step);
            }

            if (ShouldFail?.Invoke(step) == true)
            {
                throw new IOException(INJECTED_FLUSH_ERROR);
            }
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
