using System;
using System.Diagnostics;

namespace ParityProof.Core.Utils;

public sealed class EtaSmoother
{
    public const double DEFAULT_ALPHA = 0.25;
    public const long DEFAULT_MIN_UPDATE_INTERVAL_MS = 2000;
    public const string DEFAULT_ETA_TEXT = "--:--";

    private readonly double _alpha;
    private readonly long _minUpdateIntervalMs;
    private readonly Func<long> _timestampProviderMs;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private double _smoothedSeconds = -1.0;
    private long _lastUiUpdateTimestampMs = -1;
    private string _currentEtaText = DEFAULT_ETA_TEXT;

    public EtaSmoother(
        double alpha = DEFAULT_ALPHA,
        long minUpdateIntervalMs = DEFAULT_MIN_UPDATE_INTERVAL_MS,
        Func<long>? timestampProviderMs = null)
    {
        _alpha = Math.Clamp(alpha, 0.05, 0.95);
        _minUpdateIntervalMs = Math.Max(500, minUpdateIntervalMs);
        _timestampProviderMs = timestampProviderMs ?? (() => _stopwatch.ElapsedMilliseconds);
    }

    public string CurrentEtaText => _currentEtaText;

    public double SmoothedSeconds => _smoothedSeconds;

    public void Reset()
    {
        _smoothedSeconds = -1.0;
        _lastUiUpdateTimestampMs = -1;
        _currentEtaText = DEFAULT_ETA_TEXT;
        _stopwatch.Restart();
    }

    public string RegisterSample(TimeSpan rawEstimate)
    {
        long now = _timestampProviderMs();

        if (rawEstimate <= TimeSpan.Zero)
        {
            if (_smoothedSeconds < 0.0)
            {
                _currentEtaText = DEFAULT_ETA_TEXT;
            }

            return _currentEtaText;
        }

        double rawSeconds = rawEstimate.TotalSeconds;

        if (_smoothedSeconds < 0.0)
        {
            _smoothedSeconds = rawSeconds;
        }
        else
        {
            _smoothedSeconds = (_alpha * rawSeconds) + ((1.0 - _alpha) * _smoothedSeconds);
        }

        if (_lastUiUpdateTimestampMs < 0 || (now - _lastUiUpdateTimestampMs) >= _minUpdateIntervalMs)
        {
            _lastUiUpdateTimestampMs = now;
            TimeSpan smoothedSpan = TimeSpan.FromSeconds(Math.Max(0.0, _smoothedSeconds));
            _currentEtaText = FormatTimeSpan(smoothedSpan);
        }

        return _currentEtaText;
    }

    public static string FormatTimeSpan(TimeSpan timeSpan)
    {
        if (timeSpan <= TimeSpan.Zero)
        {
            return DEFAULT_ETA_TEXT;
        }

        if ((int)timeSpan.TotalHours > 0)
        {
            return $"{(int)timeSpan.TotalHours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
        }

        return $"{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
    }
}
