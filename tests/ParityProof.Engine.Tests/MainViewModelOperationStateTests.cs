using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ParityProof.App.ViewModels;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class MainViewModelOperationStateTests
{
    private const string DUPLICATE_AUDIT_TITLE = "DUPLICATE STORAGE AUDIT";
    private const string VERIFICATION_TITLE = "MEDIA VERIFICATION";
    private const string RESUME_BUTTON_TEXT = "▶ RESUME";
    private const string PAUSE_BUTTON_TEXT = "⏸ PAUSE";
    private const string DUPLICATE_AUDIT_COMPLETE_BADGE = "DUPLICATE AUDIT COMPLETE";
    private const string DUPLICATE_AUDIT_PAUSED_BADGE = "DUPLICATE AUDIT PAUSED";
    private const string DUPLICATE_AUDIT_RUNNING_BADGE = "DUPLICATE AUDIT IN PROGRESS";
    private const string PARTIAL_BADGE_PREFIX = "PARTIALLY BACKED UP";
    private const string BACKED_UP_PHOTO = "IMG_0001.JPG";
    private const int POLL_INTERVAL_MS = 10;

    // Several missing files give the copier more than one file boundary at which to observe a pause.
    private static readonly string[] _missingPhotos = { "IMG_0002.JPG", "IMG_0003.JPG", "IMG_0004.JPG" };

    // Same-size photos on the card and the backup, so the duplicate analyzer has candidates to hash.
    private static readonly string[] _backedUpPhotos = { "IMG_0001.JPG", "IMG_0002.JPG", "IMG_0003.JPG" };
    private static readonly TimeSpan _stateTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _completionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void OperationCommands_WhileAnOperationIsActive_CannotExecute()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            await VerifyCardWithMissingPhotosAsync(harness);
            Assert.True(vm.CopyMissingCommand.CanExecute(null));

            harness.DuplicateAudit.Gate.Close();
            Task audit = vm.ScanDuplicatesCommand.ExecuteAsync(null);
            Assert.True(vm.IsOperationActive);

            Assert.False(vm.VerifyCommand.CanExecute(null));
            Assert.False(vm.ScanDuplicatesCommand.CanExecute(null));
            Assert.False(vm.CopyMissingCommand.CanExecute(null));

            harness.DuplicateAudit.Gate.Open();
            await audit.WaitAsync(_completionTimeout);

            Assert.False(vm.IsOperationActive);
            Assert.True(vm.VerifyCommand.CanExecute(null));
            Assert.True(vm.ScanDuplicatesCommand.CanExecute(null));
            Assert.True(vm.CopyMissingCommand.CanExecute(null));
        });
    }

    // Key bindings and buttons re-query CanExecute only when CanExecuteChanged is raised. A command that runs
    // raises it for itself, so each case checks the commands of the operations that are not running.
    [Theory]
    [InlineData("verify")]
    [InlineData("duplicate-audit")]
    public void OperationCommands_RaiseCanExecuteChanged_WhenAnOperationStartsAndEnds(string operation)
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            await VerifyCardWithMissingPhotosAsync(harness);

            string[] names = { "VerifyCommand", "ScanDuplicatesCommand", "CopyMissingCommand" };
            IAsyncRelayCommand[] commands = { vm.VerifyCommand, vm.ScanDuplicatesCommand, vm.CopyMissingCommand };
            int[] raisedCounts = new int[commands.Length];
            for (int i = 0; i < commands.Length; i++)
            {
                int index = i;
                commands[i].CanExecuteChanged += (_, _) => raisedCounts[index]++;
            }

            (OperationGate gate, IAsyncRelayCommand runCommand) = operation switch
            {
                "verify" => (harness.Engine.Gate, vm.VerifyCommand),
                "duplicate-audit" => (harness.DuplicateAudit.Gate, vm.ScanDuplicatesCommand),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation")
            };

            gate.Close();
            Task run = runCommand.ExecuteAsync(null);

            for (int i = 0; i < commands.Length; i++)
            {
                Assert.True(raisedCounts[i] > 0, $"{names[i]}.CanExecuteChanged was not raised when {operation} started");
                Assert.False(commands[i].CanExecute(null), $"{names[i]} is executable while {operation} runs");
            }

            Array.Clear(raisedCounts);
            gate.Open();
            await run.WaitAsync(_completionTimeout);

            for (int i = 0; i < commands.Length; i++)
            {
                Assert.True(raisedCounts[i] > 0, $"{names[i]}.CanExecuteChanged was not raised when {operation} ended");
                Assert.True(commands[i].CanExecute(null), $"{names[i]} is not executable after {operation} ended");
            }
        });
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("copy")]
    public void ShortcutDuringPausedDuplicateAudit_DoesNotStartASecondOperation(string shortcut)
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            await VerifyCardWithMissingPhotosAsync(harness);

            harness.DuplicateAudit.Gate.Close();
            Task audit = vm.ScanDuplicatesCommand.ExecuteAsync(null);
            vm.RequestPauseResumeCommand.Execute(null);

            IAsyncRelayCommand command = shortcut switch
            {
                "verify" => vm.VerifyCommand,
                "copy" => vm.CopyMissingCommand,
                _ => throw new ArgumentOutOfRangeException(nameof(shortcut), shortcut, "Unknown shortcut")
            };
            Task secondRun = command.ExecuteAsync(null);
            await secondRun.WaitAsync(_completionTimeout);

            Assert.Equal(DUPLICATE_AUDIT_TITLE, vm.OperationTitle);
            Assert.True(vm.IsOperationActive);
            Assert.False(command.CanExecute(null));

            vm.ConfirmCancelCommand.Execute(null);
            await audit.WaitAsync(_stateTimeout);

            Assert.False(vm.IsOperationActive);
            Assert.Equal("Duplicate scan cancelled by user.", vm.StatusMessage);
            foreach (string photo in _missingPhotos)
            {
                Assert.False(File.Exists(Path.Combine(harness.BackupDir, photo)));
            }
        });
    }

    [Fact]
    public void DuplicateAuditRequestedDuringVerification_IsIgnored()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            harness.WriteBackedUpPhoto(BACKED_UP_PHOTO);
            vm.SourcePath = harness.CardDir;
            vm.AddDestination(harness.BackupDir);

            harness.Engine.Gate.Close();
            Task verify = vm.VerifyCommand.ExecuteAsync(null);

            Task audit = vm.ScanDuplicatesCommand.ExecuteAsync(null);
            await audit.WaitAsync(_completionTimeout);

            Assert.Equal(VERIFICATION_TITLE, vm.OperationTitle);
            Assert.True(vm.IsOperationActive);

            harness.Engine.Gate.Open();
            await verify.WaitAsync(_completionTimeout);

            Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, vm.SafetyBadgeText);
        });
    }

    [Fact]
    public void VerifyShortcutDuringPostCopyVerification_IsIgnoredAndTheReverifyStillRuns()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            await VerifyCardWithMissingPhotosAsync(harness);

            harness.Engine.Gate.Close();
            Task copy = vm.CopyMissingCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => vm.OperationTitle == VERIFICATION_TITLE && vm.IsOperationActive);

            Assert.False(vm.VerifyCommand.CanExecute(null));
            Task secondVerify = vm.VerifyCommand.ExecuteAsync(null);
            Assert.True(secondVerify.IsCompleted);

            harness.Engine.Gate.Open();
            await copy.WaitAsync(_completionTimeout);

            Assert.False(vm.IsOperationActive);
            Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, vm.SafetyBadgeText);
            foreach (string photo in _missingPhotos)
            {
                Assert.True(File.Exists(Path.Combine(harness.BackupDir, photo)));
            }
        });
    }

    [Theory]
    [InlineData("verify", "VERIFICATION PAUSED", "VERIFICATION IN PROGRESS", "#0284C7")]
    [InlineData("duplicate-audit", DUPLICATE_AUDIT_PAUSED_BADGE, DUPLICATE_AUDIT_RUNNING_BADGE, "#D97706")]
    [InlineData("copy", "BACKUP COPY PAUSED", "BACKUP COPY IN PROGRESS", "#0284C7")]
    public void PausingAnOperation_ReachesPausedStateAndResumesToCompletion(
        string operation,
        string pausedBadgeText,
        string runningBadgeText,
        string runningBadgeColor)
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            Task run;
            switch (operation)
            {
                case "verify":
                    StageCardWithMissingPhotos(harness);
                    run = vm.VerifyCommand.ExecuteAsync(null);
                    break;
                case "duplicate-audit":
                    StageCardWithMissingPhotos(harness);
                    run = vm.ScanDuplicatesCommand.ExecuteAsync(null);
                    break;
                case "copy":
                    await VerifyCardWithMissingPhotosAsync(harness);
                    run = vm.CopyMissingCommand.ExecuteAsync(null);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation");
            }

            vm.RequestPauseResumeCommand.Execute(null);
            await WaitUntilAsync(() => vm.IsPaused);

            Assert.False(vm.IsPausing);
            Assert.True(vm.CanPauseResume);
            Assert.Equal(RESUME_BUTTON_TEXT, vm.PauseResumeButtonText);
            Assert.Equal("PAUSED", vm.StateBadgeText);
            Assert.Equal(pausedBadgeText, vm.SafetyBadgeText);
            Assert.False(run.IsCompleted);

            vm.RequestPauseResumeCommand.Execute(null);

            Assert.Equal("RUNNING", vm.StateBadgeText);
            Assert.Equal(runningBadgeText, vm.SafetyBadgeText);
            Assert.Equal(runningBadgeColor, vm.SafetyBadgeColor);
            Assert.Equal(PAUSE_BUTTON_TEXT, vm.PauseResumeButtonText);

            await run.WaitAsync(_completionTimeout);

            Assert.False(vm.IsOperationActive);
            switch (operation)
            {
                case "verify":
                    Assert.StartsWith(VerdictTestHarness.UNSAFE_BADGE_PREFIX, vm.SafetyBadgeText);
                    break;
                case "duplicate-audit":
                    Assert.Equal(DUPLICATE_AUDIT_COMPLETE_BADGE, vm.SafetyBadgeText);
                    break;
                case "copy":
                    Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, vm.SafetyBadgeText);
                    break;
            }
        });
    }

    // The pause is requested at analyzer entry, after the scans and their periodic progress reporter have stopped,
    // so only the duplicate analyzer can report it.
    [Theory]
    [InlineData("duplicate-audit", DUPLICATE_AUDIT_PAUSED_BADGE, DUPLICATE_AUDIT_RUNNING_BADGE)]
    [InlineData("verify-with-duplicate-scan", "VERIFICATION PAUSED", "VERIFICATION IN PROGRESS")]
    public void PausingInTheDuplicateAnalyzer_ReachesPausedStateAndResumesToCompletion(
        string operation,
        string pausedBadgeText,
        string runningBadgeText)
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            foreach (string photo in _backedUpPhotos)
            {
                harness.WriteBackedUpPhoto(photo);
            }

            vm.SourcePath = harness.CardDir;
            vm.AddDestination(harness.BackupDir);
            vm.SelectedMode = VerificationMode.Full;
            vm.ScanDuplicatesDuringVerification = operation == "verify-with-duplicate-scan";

            harness.DuplicateAudit.Gate.Close();
            Task run = operation switch
            {
                "duplicate-audit" => vm.ScanDuplicatesCommand.ExecuteAsync(null),
                "verify-with-duplicate-scan" => vm.VerifyCommand.ExecuteAsync(null),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation")
            };
            await harness.DuplicateAudit.Entered.WaitAsync(_stateTimeout);

            vm.RequestPauseResumeCommand.Execute(null);
            harness.DuplicateAudit.Gate.Open();
            await WaitUntilAsync(() => vm.IsPaused);

            Assert.False(vm.IsPausing);
            Assert.True(vm.CanPauseResume);
            Assert.Equal(RESUME_BUTTON_TEXT, vm.PauseResumeButtonText);
            Assert.Equal("PAUSED", vm.StateBadgeText);
            Assert.Equal(pausedBadgeText, vm.SafetyBadgeText);
            Assert.False(run.IsCompleted);

            vm.RequestPauseResumeCommand.Execute(null);
            Assert.Equal(runningBadgeText, vm.SafetyBadgeText);
            await run.WaitAsync(_completionTimeout);

            Assert.False(vm.IsOperationActive);
            if (operation == "duplicate-audit")
            {
                Assert.Equal(DUPLICATE_AUDIT_COMPLETE_BADGE, vm.SafetyBadgeText);
            }
            else
            {
                Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, vm.SafetyBadgeText);
                Assert.NotNull(vm.LastVerificationSummary?.DuplicateAnalysis);
            }
        });
    }

    // Progress is queued to the UI thread, so a report the engine posted before the user paused or resumed can
    // arrive afterwards. The gated analyzer holds the audit while the test posts those reports itself.
    [Fact]
    public void StaleProgressReports_DoNotOverrideThePauseStateTheUserChose()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            StageCardWithMissingPhotos(harness);

            harness.DuplicateAudit.Gate.Close();
            Task audit = vm.ScanDuplicatesCommand.ExecuteAsync(null);
            IProgress<VerificationProgress>? progress = await harness.DuplicateAudit.Entered.WaitAsync(_stateTimeout);
            Assert.NotNull(progress);

            vm.RequestPauseResumeCommand.Execute(null);
            progress.Report(AuditProgress("Paused", isPaused: true));
            await WaitUntilAsync(() => vm.IsPaused);

            progress.Report(AuditProgress("Running report queued before the pause", isPaused: false));
            await WaitUntilAsync(() => vm.CurrentProgressPhase == "Running report queued before the pause");

            Assert.True(vm.IsPaused);
            Assert.True(vm.CanPauseResume);
            Assert.Equal(RESUME_BUTTON_TEXT, vm.PauseResumeButtonText);
            Assert.Equal("PAUSED", vm.StateBadgeText);
            Assert.Equal(DUPLICATE_AUDIT_PAUSED_BADGE, vm.SafetyBadgeText);

            progress.Report(AuditProgress("Paused report queued before the resume", isPaused: true));
            vm.RequestPauseResumeCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentProgressPhase == "Paused report queued before the resume");

            Assert.False(vm.IsPaused);
            Assert.False(vm.IsPausing);
            Assert.Equal(PAUSE_BUTTON_TEXT, vm.PauseResumeButtonText);
            Assert.Equal("RUNNING", vm.StateBadgeText);
            Assert.Equal(DUPLICATE_AUDIT_RUNNING_BADGE, vm.SafetyBadgeText);

            harness.DuplicateAudit.Gate.Open();
            await audit.WaitAsync(_completionTimeout);

            Assert.Equal(DUPLICATE_AUDIT_COMPLETE_BADGE, vm.SafetyBadgeText);
        });
    }

    [Fact]
    public void PartiallyBackedUpVerdict_OffersCopyThatCompletesTheSecondDestination()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            string[] photos = { "IMG_0001.JPG", "IMG_0002.JPG", "IMG_0003.JPG" };
            foreach (string photo in photos)
            {
                harness.WriteBackedUpPhoto(photo);
            }

            string secondBackup = harness.CreateDirectory("ssd2");
            vm.SourcePath = harness.CardDir;
            vm.AddDestination(harness.BackupDir);
            vm.AddDestination(secondBackup);

            await vm.VerifyCommand.ExecuteAsync(null);

            Assert.StartsWith(PARTIAL_BADGE_PREFIX, vm.SafetyBadgeText);
            Assert.True(vm.HasUnprotectedFiles);
            Assert.Equal("COPY TO BACKUP (3 FILES)", vm.CopyToBackupButtonText);
            Assert.True(vm.CanStartCopy);
            Assert.True(vm.CopyMissingCommand.CanExecute(null));

            await vm.CopyMissingCommand.ExecuteAsync(null);

            Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, vm.SafetyBadgeText);
            Assert.Equal(photos.Length, vm.LastVerificationSummary?.FullyVerifiedFiles);
            foreach (string photo in photos)
            {
                Assert.True(File.Exists(Path.Combine(secondBackup, photo)));
            }

            Assert.False(vm.HasUnprotectedFiles);
            Assert.False(vm.CanStartCopy);
        });
    }

    // The copy must never overwrite the mismatching backup. That guarantee belongs to MediaCopier and is tested
    // there; this test only checks that the remediation is offered.
    [Fact]
    public void CorruptBackupVerdict_OffersCopy()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            harness.WriteBackedUpPhoto("IMG_0001.JPG");
            harness.WriteBackedUpPhoto("IMG_0002.JPG");
            harness.WriteBackedUpPhoto("IMG_0003.JPG");
            string corruptBackup = Path.Combine(harness.BackupDir, "IMG_0003.JPG");
            byte[] corruptContent = File.ReadAllBytes(corruptBackup);
            corruptContent[0] ^= 0xFF;
            File.WriteAllBytes(corruptBackup, corruptContent);

            vm.SourcePath = harness.CardDir;
            vm.AddDestination(harness.BackupDir);
            await vm.VerifyCommand.ExecuteAsync(null);

            Assert.StartsWith(VerdictTestHarness.UNSAFE_BADGE_PREFIX, vm.SafetyBadgeText);
            Assert.Equal(1, vm.CorruptCount);
            Assert.Equal(0, vm.MissingCount);
            Assert.True(vm.HasUnprotectedFiles);
            Assert.Equal("COPY TO BACKUP (1 FILE)", vm.CopyToBackupButtonText);
            Assert.True(vm.CanStartCopy);
            Assert.True(vm.CopyMissingCommand.CanExecute(null));
        });
    }

    private static VerificationProgress AuditProgress(string phase, bool isPaused) => new(
        CurrentFile: string.Empty,
        ProcessedFiles: 0,
        TotalFiles: 0,
        ProcessedBytes: 0,
        TotalBytes: 0,
        Phase: phase,
        IsPaused: isPaused);

    private static void StageCardWithMissingPhotos(VerdictTestHarness harness)
    {
        harness.WriteBackedUpPhoto(BACKED_UP_PHOTO);
        foreach (string photo in _missingPhotos)
        {
            harness.WritePhoto(harness.CardDir, photo);
        }

        harness.ViewModel.SourcePath = harness.CardDir;
        harness.ViewModel.AddDestination(harness.BackupDir);
    }

    private static async Task VerifyCardWithMissingPhotosAsync(VerdictTestHarness harness)
    {
        StageCardWithMissingPhotos(harness);

        await harness.ViewModel.VerifyCommand.ExecuteAsync(null);

        Assert.StartsWith(VerdictTestHarness.UNSAFE_BADGE_PREFIX, harness.ViewModel.SafetyBadgeText);
        Assert.True(harness.ViewModel.CanStartCopy);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > _stateTimeout)
            {
                Assert.Fail($"Condition not reached within {_stateTimeout.TotalSeconds:F0}s");
            }

            await Task.Delay(POLL_INTERVAL_MS);
        }
    }
}
