using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ParityProof.App.ViewModels;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class MainViewModelExportStateTests
{
    [Fact]
    public void ExportReport_BeforeAnyVerification_IsRefused()
    {
        using VerdictTestHarness harness = new();

        Assert.False(harness.ViewModel.ExportReportCommand.CanExecute("html"));
    }

    [Fact]
    public void ExportReportCommand_RaisesCanExecuteChanged_WhenVerdictIsSetAndInvalidated()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            List<bool> observedCanExecute = new();
            vm.ExportReportCommand.CanExecuteChanged += (_, _) =>
                observedCanExecute.Add(vm.ExportReportCommand.CanExecute("html"));

            await harness.VerifySafeCardAsync();

            Assert.NotEmpty(observedCanExecute);
            Assert.True(observedCanExecute[^1]);

            observedCanExecute.Clear();
            vm.SourcePath = harness.CreateDirectory("CARD_B");

            Assert.NotEmpty(observedCanExecute);
            Assert.False(observedCanExecute[^1]);
        });
    }

    [Fact]
    public void ExportReport_WhileReverifyRunsAndAfterItIsCancelled_IsRefused()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;
            harness.WritePhoto(harness.CardDir, "IMG_0002.JPG");

            harness.Engine.Gate.Close();
            Task run = vm.VerifyCommand.ExecuteAsync(null);

            Assert.True(vm.IsOperationActive);
            Assert.False(vm.ExportReportCommand.CanExecute("html"));
            Assert.Null(vm.LastVerificationSummary);

            vm.ConfirmCancelCommand.Execute(null);
            await run;

            Assert.Equal("VERIFICATION CANCELLED", vm.SafetyBadgeText);
            harness.AssertVerdictCleared();
        });
    }

    [Fact]
    public void ExportReport_AfterFailedReverify_IsRefused()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;
            harness.WritePhoto(harness.CardDir, "IMG_0002.JPG");

            harness.Engine.Gate.FailureToThrow = new IOException("Backup destination went offline");
            await vm.VerifyCommand.ExecuteAsync(null);

            Assert.Equal("VERIFICATION FAILED", vm.SafetyBadgeText);
            harness.AssertVerdictCleared();
        });
    }

    [Fact]
    public void ExportReport_WhileDuplicateAuditRuns_IsRefusedUntilItFinishes()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;
            List<bool> observedCanExecute = new();
            vm.ExportReportCommand.CanExecuteChanged += (_, _) =>
                observedCanExecute.Add(vm.ExportReportCommand.CanExecute("html"));

            harness.DuplicateAudit.Gate.Close();
            Task run = vm.ScanDuplicatesCommand.ExecuteAsync(null);

            Assert.True(vm.IsOperationActive);
            Assert.NotNull(vm.LastVerificationSummary);
            Assert.False(vm.ExportReportCommand.CanExecute("html"));
            Assert.NotEmpty(observedCanExecute);
            Assert.False(observedCanExecute[^1]);

            harness.DuplicateAudit.Gate.Open();
            await run;

            Assert.False(vm.IsOperationActive);
            Assert.True(vm.ExportReportCommand.CanExecute("html"));
            Assert.True(observedCanExecute[^1]);
        });
    }

    [Fact]
    public void ExportReport_AfterNoMediaVerdict_IsRefused()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            vm.SourcePath = harness.CardDir;
            vm.AddDestination(harness.BackupDir);

            await vm.VerifyCommand.ExecuteAsync(null);

            VerificationSummary? summary = vm.LastVerificationSummary;
            Assert.NotNull(summary);
            Assert.Equal(OverallSafetyStatus.NoMediaFound, summary.SafetyStatus);
            Assert.False(vm.ExportReportCommand.CanExecute("html"));
        });
    }

    [Fact]
    public void ExportReport_AfterCompletedReverify_UsesNewSummary()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;
            harness.WritePhoto(harness.CardDir, "IMG_0002.JPG");

            await vm.VerifyCommand.ExecuteAsync(null);

            Assert.True(vm.ExportReportCommand.CanExecute("html"));
            VerificationSummary? summary = vm.LastVerificationSummary;
            Assert.NotNull(summary);
            Assert.Equal(2, summary.TotalFiles);
            Assert.Equal(OverallSafetyStatus.UnsafeToFormat, summary.SafetyStatus);
            Assert.StartsWith(VerdictTestHarness.UNSAFE_BADGE_PREFIX, vm.SafetyBadgeText);
        });
    }

    [Fact]
    public void DuplicateAudit_AfterVerification_ExportSnapshotMatchesDuplicatesTab()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;
            Assert.Null(vm.LastVerificationSummary?.DuplicateAnalysis);
            File.Copy(
                Path.Combine(harness.BackupDir, "IMG_0001.JPG"),
                Path.Combine(harness.BackupDir, "IMG_0001_copy.JPG"));

            await vm.ScanDuplicatesCommand.ExecuteAsync(null);

            Assert.True(vm.HasDuplicateGroups);
            DuplicateAnalysisResult? exported = vm.LastVerificationSummary?.DuplicateAnalysis;
            Assert.NotNull(exported);
            Assert.Equal(vm.AllDuplicateGroups.Count, exported.Groups.Count);
            Assert.Equal(vm.DuplicateFilesCount, exported.TotalDuplicateCopies);
            Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, vm.SafetyBadgeText);
            Assert.True(vm.ExportReportCommand.CanExecute("html"));
        });
    }
}
