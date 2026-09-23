using System;
using ParityProof.Core.Utils;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class EtaSmootherTests
{
    [Fact]
    public void InitialState_ReturnsDefaultText()
    {
        EtaSmoother smoother = new();
        Assert.Equal(EtaSmoother.DEFAULT_ETA_TEXT, smoother.CurrentEtaText);
        Assert.Equal(-1.0, smoother.SmoothedSeconds);
    }

    [Fact]
    public void RegisterSample_ZeroOrNegative_ReturnsDefaultText()
    {
        long currentTimeMs = 1000;
        EtaSmoother smoother = new(timestampProviderMs: () => currentTimeMs);

        string resultZero = smoother.RegisterSample(TimeSpan.Zero);
        Assert.Equal(EtaSmoother.DEFAULT_ETA_TEXT, resultZero);

        string resultNegative = smoother.RegisterSample(TimeSpan.FromSeconds(-5));
        Assert.Equal(EtaSmoother.DEFAULT_ETA_TEXT, resultNegative);
    }

    [Fact]
    public void RegisterSample_FirstPositiveEstimate_ImmediatelyUpdates()
    {
        long currentTimeMs = 1000;
        EtaSmoother smoother = new(timestampProviderMs: () => currentTimeMs);

        string result = smoother.RegisterSample(TimeSpan.FromSeconds(65));
        Assert.Equal("01:05", result);
        Assert.Equal(65.0, smoother.SmoothedSeconds);
        Assert.Equal("01:05", smoother.CurrentEtaText);
    }

    [Fact]
    public void RegisterSample_SubsequentEstimates_ThrottledByInterval()
    {
        long currentTimeMs = 0;
        EtaSmoother smoother = new(
            alpha: 0.5,
            minUpdateIntervalMs: 2000,
            timestampProviderMs: () => currentTimeMs);

        // Initial sample at t=0ms -> immediate update
        string initial = smoother.RegisterSample(TimeSpan.FromSeconds(100));
        Assert.Equal("01:40", initial);

        // Sample at t=500ms (within 2000ms threshold)
        currentTimeMs = 500;
        string throttled1 = smoother.RegisterSample(TimeSpan.FromSeconds(80));
        // Smoothed value: 0.5 * 80 + 0.5 * 100 = 90
        Assert.Equal(90.0, smoother.SmoothedSeconds);
        // Displayed text should remain unchanged from initial update
        Assert.Equal("01:40", throttled1);

        // Sample at t=1500ms (still within 2000ms threshold)
        currentTimeMs = 1500;
        string throttled2 = smoother.RegisterSample(TimeSpan.FromSeconds(70));
        // Smoothed value: 0.5 * 70 + 0.5 * 90 = 80
        Assert.Equal(80.0, smoother.SmoothedSeconds);
        Assert.Equal("01:40", throttled2);

        // Sample at t=2100ms (interval >= 2000ms reached)
        currentTimeMs = 2100;
        string updated = smoother.RegisterSample(TimeSpan.FromSeconds(60));
        // Smoothed value: 0.5 * 60 + 0.5 * 80 = 70 (01:10)
        Assert.Equal(70.0, smoother.SmoothedSeconds);
        Assert.Equal("01:10", updated);
        Assert.Equal("01:10", smoother.CurrentEtaText);
    }

    [Fact]
    public void RegisterSample_SpikeIsDampenedByEma()
    {
        long currentTimeMs = 0;
        EtaSmoother smoother = new(
            alpha: 0.2,
            minUpdateIntervalMs: 2000,
            timestampProviderMs: () => currentTimeMs);

        smoother.RegisterSample(TimeSpan.FromSeconds(100));
        Assert.Equal(100.0, smoother.SmoothedSeconds);

        currentTimeMs = 2500;
        // Sudden spike to 500s: 0.2 * 500 + 0.8 * 100 = 100 + 80 = 180s
        string afterSpike = smoother.RegisterSample(TimeSpan.FromSeconds(500));
        Assert.Equal(180.0, smoother.SmoothedSeconds);
        Assert.Equal("03:00", afterSpike);
    }

    [Fact]
    public void Reset_ClearsSmoothedStateAndReturnsDefault()
    {
        long currentTimeMs = 0;
        EtaSmoother smoother = new(timestampProviderMs: () => currentTimeMs);

        smoother.RegisterSample(TimeSpan.FromSeconds(120));
        Assert.Equal("02:00", smoother.CurrentEtaText);

        smoother.Reset();
        Assert.Equal(EtaSmoother.DEFAULT_ETA_TEXT, smoother.CurrentEtaText);
        Assert.Equal(-1.0, smoother.SmoothedSeconds);
    }

    [Theory]
    [InlineData(0, "--:--")]
    [InlineData(-10, "--:--")]
    [InlineData(5, "00:05")]
    [InlineData(65, "01:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "01:00:00")]
    [InlineData(3665, "01:01:05")]
    [InlineData(86400, "24:00:00")]
    public void FormatTimeSpan_ProducesExpectedFormat(int totalSeconds, string expected)
    {
        TimeSpan span = TimeSpan.FromSeconds(totalSeconds);
        string formatted = EtaSmoother.FormatTimeSpan(span);
        Assert.Equal(expected, formatted);
    }
}
