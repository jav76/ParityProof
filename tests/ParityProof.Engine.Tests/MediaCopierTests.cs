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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { missingFile },
            dstDir);

        Assert.Equal(1, copied);

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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { mediaFile },
            dstDir);

        Assert.Equal(1, copied);

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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { missingFile },
            targetDestinations);

        Assert.Equal(1, copied);

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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { missingFile },
            dstDir);

        Assert.Equal(1, copied);
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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir,
            progress);

        Assert.Equal(1, copied);
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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir);

        Assert.Equal(1, copied);
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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir);

        Assert.Equal(1, copied);
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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir,
            progress);

        Assert.Equal(1, copied);
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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir);

        Assert.Equal(1, copied);
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
        int copiedAgain = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir);

        Assert.Equal(1, copiedAgain);
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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            new List<string> { dstWithCollision, dstClean },
            progress);

        Assert.Equal(1, copied);
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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir,
            progress);

        Assert.Equal(1, copied);
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

        int copied = await _copier.CopyMissingFilesAsync(
            sourceFiles,
            dstDir,
            progress);

        Assert.Equal(FILE_COUNT, copied);
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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { upperFile, lowerFile },
            dstDir);

        Assert.Equal(2, copied);
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

        int copied = await _copier.CopyMissingFilesAsync(
            new[] { sourceFile },
            dstDir,
            progress);

        Assert.Equal(1, copied);
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

        int copied = await _copier.CopyMissingFilesAsync(
            missingFiles,
            ssdDir);

        Assert.Equal(1, copied);
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));

        (VerificationSummary secondSummary, _) = await verifier.VerifyAsync(
            cardDir,
            destinations,
            VerificationMode.Quick,
            FilterPreset.PhotosOnly);

        Assert.Equal(OverallSafetyStatus.SafeToFormat, secondSummary.SafetyStatus);
        Assert.Equal(archivedPayload, File.ReadAllBytes(archivedPath));
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
