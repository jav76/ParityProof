using System;
using System.Globalization;
using Avalonia.Data;
using ParityProof.App.Converters;
using ParityProof.App.ViewModels;
using ParityProof.Core.Enums;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class EnumToBooleanConverterTests
{
    private readonly EnumToBooleanConverter _converter = EnumToBooleanConverter.Instance;

    [Fact]
    public void Convert_MatchingEnum_ReturnsTrue()
    {
        object? resultSuperFast = _converter.Convert(
            VerificationMode.SuperFast,
            typeof(bool),
            VerificationMode.SuperFast,
            CultureInfo.InvariantCulture);

        object? resultQuick = _converter.Convert(
            VerificationMode.Quick,
            typeof(bool),
            VerificationMode.Quick,
            CultureInfo.InvariantCulture);

        object? resultDeep = _converter.Convert(
            VerificationMode.Deep,
            typeof(bool),
            VerificationMode.Deep,
            CultureInfo.InvariantCulture);

        object? resultFull = _converter.Convert(
            VerificationMode.Full,
            typeof(bool),
            VerificationMode.Full,
            CultureInfo.InvariantCulture);

        Assert.Equal(true, resultSuperFast);
        Assert.Equal(true, resultQuick);
        Assert.Equal(true, resultDeep);
        Assert.Equal(true, resultFull);
    }

    [Fact]
    public void Convert_NonMatchingEnum_ReturnsFalse()
    {
        object? result = _converter.Convert(
            VerificationMode.Quick,
            typeof(bool),
            VerificationMode.SuperFast,
            CultureInfo.InvariantCulture);

        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_StringParameterMatchingEnum_ReturnsTrue()
    {
        object? result = _converter.Convert(
            VerificationMode.SuperFast,
            typeof(bool),
            "SuperFast",
            CultureInfo.InvariantCulture);

        Assert.Equal(true, result);
    }

    [Fact]
    public void Convert_StringParameterNonMatchingEnum_ReturnsFalse()
    {
        object? result = _converter.Convert(
            VerificationMode.Quick,
            typeof(bool),
            "SuperFast",
            CultureInfo.InvariantCulture);

        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_NullValueOrParameter_ReturnsFalse()
    {
        object? resultNullValue = _converter.Convert(
            null,
            typeof(bool),
            VerificationMode.Quick,
            CultureInfo.InvariantCulture);

        object? resultNullParameter = _converter.Convert(
            VerificationMode.Quick,
            typeof(bool),
            null,
            CultureInfo.InvariantCulture);

        Assert.Equal(false, resultNullValue);
        Assert.Equal(false, resultNullParameter);
    }

    [Fact]
    public void ConvertBack_WhenTrueWithEnumParameter_ReturnsEnumParameter()
    {
        object? result = _converter.ConvertBack(
            true,
            typeof(VerificationMode),
            VerificationMode.Full,
            CultureInfo.InvariantCulture);

        Assert.Equal(VerificationMode.Full, result);
    }

    [Fact]
    public void ConvertBack_WhenTrueWithStringParameter_ReturnsParsedEnum()
    {
        object? result = _converter.ConvertBack(
            true,
            typeof(VerificationMode),
            "SuperFast",
            CultureInfo.InvariantCulture);

        Assert.Equal(VerificationMode.SuperFast, result);
    }

    [Fact]
    public void ConvertBack_WhenFalse_ReturnsBindingOperationsDoNothing()
    {
        object? result = _converter.ConvertBack(
            false,
            typeof(VerificationMode),
            VerificationMode.Quick,
            CultureInfo.InvariantCulture);

        Assert.Same(BindingOperations.DoNothing, result);
    }

    [Theory]
    [InlineData(VerificationMode.SuperFast, "Super-Fast")]
    [InlineData(VerificationMode.Quick, "Quick")]
    [InlineData(VerificationMode.Deep, "Deep Probe")]
    [InlineData(VerificationMode.Full, "Full")]
    public void FormatScanMode_ReturnsExpectedUserFacingName(VerificationMode mode, string expected)
    {
        string formatted = MainViewModel.FormatScanMode(mode);
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void MainViewModel_SelectedMode_UpdatesDisplayedScanMode_WhenNoResults()
    {
        using MainViewModel vm = new();
        Assert.Equal(VerificationMode.Quick, vm.SelectedMode);
        Assert.Equal("Quick", vm.DisplayedScanMode);

        vm.SelectedMode = VerificationMode.SuperFast;
        Assert.Equal("Super-Fast", vm.DisplayedScanMode);

        vm.SelectedMode = VerificationMode.Deep;
        Assert.Equal("Deep Probe", vm.DisplayedScanMode);

        vm.SelectedMode = VerificationMode.Full;
        Assert.Equal("Full", vm.DisplayedScanMode);
    }

    [Fact]
    public void MainViewModel_SelectedMode_PreservesDisplayedScanMode_WhenResultsPresent()
    {
        using MainViewModel vm = new();
        vm.SelectedMode = VerificationMode.SuperFast;
        Assert.Equal("Super-Fast", vm.DisplayedScanMode);

        vm.HasResults = true;

        vm.SelectedMode = VerificationMode.Full;

        Assert.Equal("Super-Fast", vm.DisplayedScanMode);
        Assert.Equal(VerificationMode.Full, vm.SelectedMode);
    }

    [Fact]
    public async Task MainViewModel_VerifyAsync_ExecutesWithSelectedMode_AndUpdatesDisplayedScanMode()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pp_test_" + Guid.NewGuid().ToString("N"));
        string sourceDir = Path.Combine(tempDir, "source");
        string destDir = Path.Combine(tempDir, "dest");

        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        try
        {
            byte[] fileBytes = new byte[1024];
            Random.Shared.NextBytes(fileBytes);
            File.WriteAllBytes(Path.Combine(sourceDir, "DSC_0001.JPG"), fileBytes);
            File.WriteAllBytes(Path.Combine(destDir, "DSC_0001.JPG"), fileBytes);

            using MainViewModel vm = new();
            vm.SourcePath = sourceDir;
            vm.AddDestinationCommand.Execute(destDir);

            vm.SelectedMode = VerificationMode.SuperFast;
            Assert.Equal("Super-Fast", vm.DisplayedScanMode);

            await vm.VerifyCommand.ExecuteAsync(null);

            Assert.True(vm.HasResults);
            Assert.Equal(1, vm.VerifiedCount);
            Assert.Equal("Super-Fast", vm.DisplayedScanMode);

            vm.SelectedMode = VerificationMode.Full;
            Assert.Equal("Super-Fast", vm.DisplayedScanMode);

            await vm.VerifyCommand.ExecuteAsync(null);
            Assert.Equal("Full", vm.DisplayedScanMode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MainViewModel_ScanDuplicatesAsync_ExecutesWithSelectedMode_AndUpdatesDisplayedScanMode()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pp_dup_test_" + Guid.NewGuid().ToString("N"));
        string sourceDir = Path.Combine(tempDir, "source");
        string destDir = Path.Combine(tempDir, "dest");

        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        try
        {
            byte[] fileBytes = new byte[2048];
            Random.Shared.NextBytes(fileBytes);
            File.WriteAllBytes(Path.Combine(sourceDir, "PHOTO_1.JPG"), fileBytes);
            File.WriteAllBytes(Path.Combine(destDir, "PHOTO_1_COPY.JPG"), fileBytes);

            using MainViewModel vm = new();
            vm.SourcePath = sourceDir;
            vm.AddDestinationCommand.Execute(destDir);

            vm.SelectedMode = VerificationMode.Full;
            Assert.Equal("Full", vm.DisplayedScanMode);

            await vm.ScanDuplicatesCommand.ExecuteAsync(null);

            Assert.Equal("Full", vm.DisplayedScanMode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
