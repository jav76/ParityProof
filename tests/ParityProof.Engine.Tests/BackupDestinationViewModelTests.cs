using System;
using System.IO;
using ParityProof.App.ViewModels;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class BackupDestinationViewModelTests
{
    [Fact]
    public void Constructor_InitializesCapacityInfo()
    {
        string tempDir = Path.GetTempPath();
        BackupDestinationViewModel vm = new("dest_1", "Test Backup", tempDir);

        Assert.Equal("dest_1", vm.Id);
        Assert.Equal("Test Backup", vm.Name);
        Assert.Equal(tempDir, vm.RootPath);
        Assert.True(vm.HasSpaceInfo);
        Assert.True(vm.TotalSpaceBytes > 0);
        Assert.True(vm.FreeSpaceBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(vm.CapacityFormatted));
    }

    [Fact]
    public void CheckRequiredSpace_ExceedsFreeSpace_SetsCapacityWarning()
    {
        string tempDir = Path.GetTempPath();
        BackupDestinationViewModel vm = new("dest_1", "Test Backup", tempDir);

        long hugePayloadBytes = vm.FreeSpaceBytes + (10L * 1024 * 1024 * 1024); // 10 GB over free space
        vm.CheckRequiredSpace(hugePayloadBytes);

        Assert.True(vm.HasCapacityWarning);
        Assert.Contains("Insufficient space", vm.CapacityWarningText);
    }

    [Fact]
    public void CheckRequiredSpace_WithinFreeSpace_ClearsCapacityWarning()
    {
        string tempDir = Path.GetTempPath();
        BackupDestinationViewModel vm = new("dest_1", "Test Backup", tempDir);

        long smallPayloadBytes = 1024; // 1 KB
        vm.CheckRequiredSpace(smallPayloadBytes);

        Assert.False(vm.HasCapacityWarning);
        Assert.Equal(string.Empty, vm.CapacityWarningText);
    }
}
