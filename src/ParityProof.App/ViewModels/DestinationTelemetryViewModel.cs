using CommunityToolkit.Mvvm.ComponentModel;
using ParityProof.Core.Models;
using ParityProof.Core.Utils;

namespace ParityProof.App.ViewModels;

public sealed partial class DestinationTelemetryViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _destinationId = string.Empty;

    [ObservableProperty]
    private string _destinationName = string.Empty;

    [ObservableProperty]
    private string _rootPath = string.Empty;

    [ObservableProperty]
    private string _scanStatus = "PENDING";

    [ObservableProperty]
    private int _scanFilesCount;

    [ObservableProperty]
    private long _scanBytesCount;

    [ObservableProperty]
    private double _scanSpeed;

    [ObservableProperty]
    private string _indexStatus = "PENDING";

    [ObservableProperty]
    private string _verifyStatus = "PENDING";

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private int _verifiedFiles;

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private long _bytesRead;

    [ObservableProperty]
    private double _speedMbPerSec;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool HasSpeed => SpeedMbPerSec > 0;

    public string SpeedFormatted => SpeedMbPerSec > 0 ? $"{SpeedMbPerSec:F1} MB/s" : "-- MB/s";

    public string ScanSummaryFormatted => ScanFilesCount > 0
        ? $"{ScanFilesCount:N0} files ({ByteSizeFormatter.Format(ScanBytesCount)})"
        : (ScanStatus == "COMPLETE" ? "0 files" : "Scanning...");

    public string VerifySummaryFormatted => TotalFiles > 0
        ? $"{VerifiedFiles:N0} / {TotalFiles:N0} ({Percentage:F0}%)"
        : "-- / --";

    public string VerifyStatusBadgeBackground => VerifyStatus switch
    {
        "COMPLETE" or "VERIFIED" => "#064E3B",
        "VERIFYING" or "WRITING" => "#065F46",
        "PAUSED" => "#78350F",
        "SCANNING" => "#3B0764",
        _ => "#1F2937"
    };

    public string VerifyStatusBadgeForeground => VerifyStatus switch
    {
        "COMPLETE" or "VERIFIED" => "#34D399",
        "VERIFYING" or "WRITING" => "#10B981",
        "PAUSED" => "#FBBF24",
        "SCANNING" => "#C084FC",
        _ => "#9CA3AF"
    };

    public string ScanStatusBadgeBackground => ScanStatus switch
    {
        "COMPLETE" => "#064E3B",
        "SCANNING" => "#3B0764",
        _ => "#1F2937"
    };

    public string ScanStatusBadgeForeground => ScanStatus switch
    {
        "COMPLETE" => "#34D399",
        "SCANNING" => "#C084FC",
        _ => "#9CA3AF"
    };

    public DestinationTelemetryViewModel()
    {
    }

    public DestinationTelemetryViewModel(DestinationTelemetryInfo info)
    {
        UpdateFrom(info);
    }

    public void UpdateFrom(DestinationTelemetryInfo info)
    {
        DestinationId = info.DestinationId;
        DestinationName = info.DestinationName;
        RootPath = info.RootPath;
        ScanStatus = info.ScanStatus;
        ScanFilesCount = info.ScanFilesCount;
        ScanBytesCount = info.ScanBytesCount;
        ScanSpeed = info.ScanSpeed;
        IndexStatus = info.IndexStatus;
        VerifyStatus = info.VerifyStatus;
        Percentage = info.Percentage;
        VerifiedFiles = info.VerifiedFiles;
        TotalFiles = info.TotalFiles;
        BytesRead = info.BytesRead;
        SpeedMbPerSec = info.SpeedMbPerSec;
        StatusText = info.StatusText;

        OnPropertyChanged(nameof(HasSpeed));
        OnPropertyChanged(nameof(SpeedFormatted));
        OnPropertyChanged(nameof(ScanSummaryFormatted));
        OnPropertyChanged(nameof(VerifySummaryFormatted));
        OnPropertyChanged(nameof(VerifyStatusBadgeBackground));
        OnPropertyChanged(nameof(VerifyStatusBadgeForeground));
        OnPropertyChanged(nameof(ScanStatusBadgeBackground));
        OnPropertyChanged(nameof(ScanStatusBadgeForeground));
    }
}

