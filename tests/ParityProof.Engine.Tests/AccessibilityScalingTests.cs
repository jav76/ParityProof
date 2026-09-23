using System;
using ParityProof.App.ViewModels;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class AccessibilityScalingTests
{
    [Fact]
    public void TextScale_DefaultValue_Is100Percent()
    {
        using MainViewModel vm = new();

        Assert.Equal(MainViewModel.DEFAULT_TEXT_SCALE, vm.TextScale);
        Assert.Equal("100%", vm.TextScalePercentageText);
        Assert.True(vm.CanIncreaseScale);
        Assert.True(vm.CanDecreaseScale);
    }

    [Fact]
    public void IncreaseTextScale_IncrementsScaleByStep()
    {
        using MainViewModel vm = new();

        vm.IncreaseTextScaleCommand.Execute(null);

        Assert.Equal(1.10, vm.TextScale, 2);
        Assert.Equal("110%", vm.TextScalePercentageText);
    }

    [Fact]
    public void DecreaseTextScale_DecrementsScaleByStep()
    {
        using MainViewModel vm = new();

        vm.DecreaseTextScaleCommand.Execute(null);

        Assert.Equal(0.90, vm.TextScale, 2);
        Assert.Equal("90%", vm.TextScalePercentageText);
    }

    [Fact]
    public void TextScale_ClampsAtMaximum()
    {
        using MainViewModel vm = new();

        for (int i = 0; i < 20; i++)
        {
            vm.IncreaseTextScaleCommand.Execute(null);
        }

        Assert.Equal(MainViewModel.MAX_TEXT_SCALE, vm.TextScale, 2);
        Assert.Equal("160%", vm.TextScalePercentageText);
        Assert.False(vm.CanIncreaseScale);
    }

    [Fact]
    public void TextScale_ClampsAtMinimum()
    {
        using MainViewModel vm = new();

        for (int i = 0; i < 20; i++)
        {
            vm.DecreaseTextScaleCommand.Execute(null);
        }

        Assert.Equal(MainViewModel.MIN_TEXT_SCALE, vm.TextScale, 2);
        Assert.Equal("85%", vm.TextScalePercentageText);
        Assert.False(vm.CanDecreaseScale);
    }

    [Fact]
    public void ResetTextScale_RestoresDefaultScale()
    {
        using MainViewModel vm = new();

        vm.IncreaseTextScaleCommand.Execute(null);
        vm.IncreaseTextScaleCommand.Execute(null);
        Assert.Equal("120%", vm.TextScalePercentageText);

        vm.ResetTextScaleCommand.Execute(null);
        Assert.Equal(MainViewModel.DEFAULT_TEXT_SCALE, vm.TextScale);
        Assert.Equal("100%", vm.TextScalePercentageText);
    }

    [Theory]
    [InlineData("0.90", 0.90, "90%")]
    [InlineData("1.00", 1.00, "100%")]
    [InlineData("1.10", 1.10, "110%")]
    [InlineData("1.25", 1.25, "125%")]
    [InlineData("1.50", 1.50, "150%")]
    public void SetTextScale_AppliesPresetCorrectly(string presetParam, double expectedScale, string expectedPercentage)
    {
        using MainViewModel vm = new();

        vm.SetTextScaleCommand.Execute(presetParam);

        Assert.Equal(expectedScale, vm.TextScale, 2);
        Assert.Equal(expectedPercentage, vm.TextScalePercentageText);
    }

    [Fact]
    public void SetTextScale_IgnoresInvalidString()
    {
        using MainViewModel vm = new();

        vm.SetTextScaleCommand.Execute("invalid_number");

        Assert.Equal(MainViewModel.DEFAULT_TEXT_SCALE, vm.TextScale);
        Assert.Equal("100%", vm.TextScalePercentageText);
    }
}
