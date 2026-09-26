using System;
using System.IO;
using System.Threading.Tasks;
using ParityProof.App.ViewModels;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class MainViewModelVerdictStateTests
{
    [Fact]
    public void ChangingSourcePath_AfterSafeVerdict_ClearsVerdictAndResults()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();

            harness.ViewModel.SourcePath = harness.CreateDirectory("CARD_B");

            harness.AssertVerdictCleared();
        });
    }

    [Fact]
    public void ReassigningEquivalentSourcePath_AfterSafeVerdict_KeepsVerdict()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();

            harness.ViewModel.SourcePath = harness.CardDir + Path.DirectorySeparatorChar;

            Assert.True(harness.ViewModel.HasResults);
            Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, harness.ViewModel.SafetyBadgeText);
        });
    }

    [Theory]
    [InlineData("preset")]
    [InlineData("mode")]
    [InlineData("disable-destination")]
    [InlineData("remove-destination")]
    [InlineData("add-destination")]
    [InlineData("destination-root-path")]
    public void ChangingVerificationInput_AfterSafeVerdict_ClearsVerdictAndResults(string input)
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;

            switch (input)
            {
                case "preset":
                    vm.SelectedPreset = vm.FilterPresets[1];
                    break;
                case "mode":
                    vm.SelectedMode = VerificationMode.Deep;
                    break;
                case "disable-destination":
                    vm.Destinations[0].IsEnabled = false;
                    break;
                case "remove-destination":
                    vm.RemoveDestinationCommand.Execute(vm.Destinations[0]);
                    break;
                case "add-destination":
                    vm.AddDestination(harness.CreateDirectory("second_ssd"));
                    break;
                case "destination-root-path":
                    vm.Destinations[0].RootPath = harness.CreateDirectory("other_ssd");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(input), input, "Unknown verification input");
            }

            harness.AssertVerdictCleared();
        });
    }

    [Fact]
    public void CardSwapAtSameMountPath_AfterSafeVerdict_ClearsVerdict()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;

            vm.ApplyDriveNotification(new DriveNotificationEventArgs(
                harness.CardDir,
                "EOS_DIGITAL",
                isRemovable: true,
                DriveEventType.Removed));

            File.Delete(Path.Combine(harness.CardDir, "IMG_0001.JPG"));
            harness.WritePhoto(harness.CardDir, "IMG_9001.JPG");

            vm.ApplyDriveNotification(new DriveNotificationEventArgs(
                harness.CardDir,
                "EOS_DIGITAL",
                isRemovable: true,
                DriveEventType.Inserted));

            Assert.Equal(harness.CardDir, vm.SourcePath);
            harness.AssertVerdictCleared();
        });
    }

    [Theory]
    [InlineData(DriveEventType.Removed)]
    [InlineData(DriveEventType.Inserted)]
    public void DriveEventForCardHoldingVerifiedSubfolder_ClearsVerdict(DriveEventType eventType)
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            string dcimDir = Path.Combine(harness.CardDir, "DCIM");
            Directory.CreateDirectory(dcimDir);
            harness.WritePhoto(dcimDir, "IMG_0001.JPG");
            harness.WritePhoto(harness.BackupDir, "IMG_0001.JPG");
            vm.SourcePath = dcimDir;
            vm.AddDestination(harness.BackupDir);

            await vm.VerifyCommand.ExecuteAsync(null);
            Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, vm.SafetyBadgeText);

            vm.ApplyDriveNotification(new DriveNotificationEventArgs(
                harness.CardDir,
                "EOS_DIGITAL",
                isRemovable: true,
                eventType));

            Assert.Equal(dcimDir, vm.SourcePath);
            harness.AssertVerdictCleared();
        });
    }

    [Fact]
    public void RemovingSourceDrive_AfterReportExport_HidesThatCardsReport()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;
            vm.LastExportedReportPath = Path.Combine(harness.RootDir, "ParityProof_Audit.html");
            Assert.True(vm.HasExportedReport);

            vm.ApplyDriveNotification(new DriveNotificationEventArgs(
                harness.CardDir,
                "EOS_DIGITAL",
                isRemovable: true,
                DriveEventType.Removed));

            harness.AssertVerdictCleared();
            Assert.Null(vm.LastExportedReportPath);
        });
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("duplicate-audit")]
    public void StartingOperation_AfterSourceFolderVanished_ClearsVerdict(string operation)
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;
            Directory.Delete(harness.CardDir, recursive: true);

            Task run = operation switch
            {
                "verify" => vm.VerifyCommand.ExecuteAsync(null),
                "duplicate-audit" => vm.ScanDuplicatesCommand.ExecuteAsync(null),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation")
            };
            await run;

            harness.AssertVerdictCleared();
            Assert.Equal(VerdictTestHarness.INPUTS_CHANGED_BADGE, vm.SafetyBadgeText);
            Assert.StartsWith("Source directory not found", vm.StatusMessage);
        });
    }

    [Fact]
    public void ClearingDestinations_StopsTrackingTheClearedDestinations()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;
            BackupDestinationViewModel clearedDestination = vm.Destinations[0];

            vm.Destinations.Clear();
            harness.AssertVerdictCleared();

            string secondBackup = harness.CreateDirectory("second_ssd");
            harness.WritePhoto(secondBackup, "IMG_0001.JPG");
            vm.AddDestination(secondBackup);
            await vm.VerifyCommand.ExecuteAsync(null);
            Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, vm.SafetyBadgeText);

            clearedDestination.IsEnabled = false;

            Assert.True(vm.HasResults);
            Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, vm.SafetyBadgeText);
        });
    }

    [Fact]
    public void RemovingUnrelatedDrive_KeepsVerdict()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();

            harness.ViewModel.ApplyDriveNotification(new DriveNotificationEventArgs(
                harness.CreateDirectory("OTHER_CARD"),
                "OTHER_CARD",
                isRemovable: true,
                DriveEventType.Removed));

            Assert.True(harness.ViewModel.HasResults);
            Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, harness.ViewModel.SafetyBadgeText);
        });
    }

    [Fact]
    public void SourceDriveRemovedWhileVerifying_DiscardsTheInFlightVerdict()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            harness.WriteBackedUpPhoto("IMG_0001.JPG");
            vm.SourcePath = harness.CardDir;
            vm.AddDestination(harness.BackupDir);

            harness.Engine.Gate.Close();
            Task run = vm.VerifyCommand.ExecuteAsync(null);
            Assert.True(vm.IsOperationActive);

            vm.ApplyDriveNotification(new DriveNotificationEventArgs(
                harness.CardDir,
                "EOS_DIGITAL",
                isRemovable: true,
                DriveEventType.Removed));
            harness.Engine.Gate.Open();
            await run;

            harness.AssertVerdictCleared();
        });
    }

    [Fact]
    public void CancelledVerifyOfNewCard_LeavesNoPreviousResults()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;
            string cardB = harness.CreateDirectory("CARD_B");
            harness.WritePhoto(cardB, "IMG_9001.JPG");
            vm.SourcePath = cardB;

            harness.Engine.Gate.Close();
            Task run = vm.VerifyCommand.ExecuteAsync(null);
            vm.ConfirmCancelCommand.Execute(null);
            await run;

            Assert.Equal("VERIFICATION CANCELLED", vm.SafetyBadgeText);
            harness.AssertVerdictCleared();
        });
    }

    [Fact]
    public void DuplicateAudit_AfterUnsafeVerdict_DoesNotPaintBannerGreen()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            harness.WriteBackedUpPhoto("IMG_0001.JPG");
            harness.WritePhoto(harness.CardDir, "IMG_0002.JPG");
            vm.SourcePath = harness.CardDir;
            vm.AddDestination(harness.BackupDir);

            await vm.VerifyCommand.ExecuteAsync(null);
            Assert.StartsWith(VerdictTestHarness.UNSAFE_BADGE_PREFIX, vm.SafetyBadgeText);
            string unsafeColor = vm.SafetyBadgeColor;

            await vm.ScanDuplicatesCommand.ExecuteAsync(null);

            Assert.NotEqual(VerdictTestHarness.COLOR_SAFE, vm.SafetyBadgeColor);
            Assert.StartsWith(VerdictTestHarness.UNSAFE_BADGE_PREFIX, vm.SafetyBadgeText);
            Assert.Equal(unsafeColor, vm.SafetyBadgeColor);
            Assert.True(vm.HasResults);
        });
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("fail")]
    public void DuplicateAudit_CancelledOrFailedAfterSafeVerdict_KeepsShowingTheVerdict(string outcome)
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            await harness.VerifySafeCardAsync();
            MainViewModel vm = harness.ViewModel;

            if (outcome == "cancel")
            {
                harness.DuplicateAudit.Gate.Close();
                Task run = vm.ScanDuplicatesCommand.ExecuteAsync(null);
                vm.ConfirmCancelCommand.Execute(null);
                await run;

                Assert.Equal("Duplicate scan cancelled by user.", vm.StatusMessage);
            }
            else
            {
                harness.DuplicateAudit.Gate.FailureToThrow = new IOException("Backup destination went offline");
                await vm.ScanDuplicatesCommand.ExecuteAsync(null);

                Assert.StartsWith("Duplicate scan error", vm.StatusMessage);
            }

            Assert.StartsWith(VerdictTestHarness.SAFE_BADGE_PREFIX, vm.SafetyBadgeText);
            Assert.Equal(VerdictTestHarness.COLOR_SAFE, vm.SafetyBadgeColor);
            Assert.True(vm.HasResults);
            Assert.NotNull(vm.LastVerificationSummary);
            Assert.True(vm.ExportReportCommand.CanExecute("html"));
        });
    }

    [Fact]
    public void DuplicateAudit_WithoutVerification_DoesNotPaintBannerGreen()
    {
        UiThreadSimulator.Run(async () =>
        {
            using VerdictTestHarness harness = new();
            MainViewModel vm = harness.ViewModel;
            harness.WriteBackedUpPhoto("IMG_0001.JPG");
            vm.SourcePath = harness.CardDir;
            vm.AddDestination(harness.BackupDir);

            await vm.ScanDuplicatesCommand.ExecuteAsync(null);

            Assert.NotEqual(VerdictTestHarness.COLOR_SAFE, vm.SafetyBadgeColor);
            Assert.DoesNotContain(VerdictTestHarness.SAFE_BADGE_PREFIX, vm.SafetyBadgeText);
            Assert.False(vm.ExportReportCommand.CanExecute("html"));
        });
    }
}
