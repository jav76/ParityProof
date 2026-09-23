using CommunityToolkit.Mvvm.ComponentModel;
using ParityProof.Core.Models;
using ParityProof.Core.Utils;

namespace ParityProof.App.ViewModels;

public sealed partial class SourceTelemetryViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private string _scanStatus = "IDLE";

    [ObservableProperty]
    private int _scanFilesCount;

    [ObservableProperty]
    private long _scanBytesCount;

    [ObservableProperty]
    private double _scanSpeed;

    [ObservableProperty]
    private string _indexStatus = "PENDING";

    [ObservableProperty]
    private string _hashStatus = "PENDING";

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private long _processedBytes;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private double _speedMbPerSec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFile))]
    private string? _currentFile;

    [ObservableProperty]
    private double _currentFilePercentage;

    [ObservableProperty]
    private string? _currentFileProgressText;

    public bool HasActiveFile => !string.IsNullOrWhiteSpace(CurrentFile);

    public string SpeedFormatted => SpeedMbPerSec > 0 ? $"{SpeedMbPerSec:F1} MB/s" : "-- MB/s";

    public string ScanSummaryFormatted => ScanFilesCount > 0
        ? $"{ScanFilesCount:N0} files ({ByteSizeFormatter.Format(ScanBytesCount)})"
        : (ScanStatus == "COMPLETE" ? "0 files" : "Scanning...");

    public string HashSummaryFormatted => TotalBytes > 0
        ? $"{ByteSizeFormatter.Format(ProcessedBytes)} / {ByteSizeFormatter.Format(TotalBytes)}"
        : "-- / --";

    public string HashStatusBadgeBackground => HashStatus switch
    {
        "COMPLETE" => "#064E3B",
        "HASHING" or "STREAMING" => "#075985",
        "PAUSED" => "#78350F",
        "SCANNING" => "#3B0764",
        _ => "#1F2937"
    };

    public string HashStatusBadgeForeground => HashStatus switch
    {
        "COMPLETE" => "#34D399",
        "HASHING" or "STREAMING" => "#38BDF8",
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

    public string IndexStatusBadgeBackground => IndexStatus switch
    {
        "COMPLETE" or "INDEXED" => "#064E3B",
        "INDEXING" => "#1E1B4B",
        _ => "#1F2937"
    };

    public string IndexStatusBadgeForeground => IndexStatus switch
    {
        "COMPLETE" or "INDEXED" => "#34D399",
        "INDEXING" => "#818CF8",
        _ => "#9CA3AF"
    };

    public SourceTelemetryViewModel()
    {
    }

    public SourceTelemetryViewModel(SourceTelemetryInfo info)
    {
        UpdateFrom(info);
    }

    public void UpdateFrom(SourceTelemetryInfo info)
    {
        Path = info.Path;
        ScanStatus = info.ScanStatus;
        ScanFilesCount = info.ScanFilesCount;
        ScanBytesCount = info.ScanBytesCount;
        ScanSpeed = info.ScanSpeed;
        IndexStatus = info.IndexStatus;
        HashStatus = info.HashStatus;
        Percentage = info.Percentage;
        ProcessedBytes = info.ProcessedBytes;
        TotalBytes = info.TotalBytes;
        SpeedMbPerSec = info.SpeedMbPerSec;
        CurrentFile = info.CurrentFile;
        CurrentFilePercentage = info.CurrentFilePercentage;
        CurrentFileProgressText = info.CurrentFileProgressText;

        OnPropertyChanged(nameof(SpeedFormatted));
        OnPropertyChanged(nameof(ScanSummaryFormatted));
        OnPropertyChanged(nameof(HashSummaryFormatted));
        OnPropertyChanged(nameof(HashStatusBadgeBackground));
        OnPropertyChanged(nameof(HashStatusBadgeForeground));
        OnPropertyChanged(nameof(ScanStatusBadgeBackground));
        OnPropertyChanged(nameof(ScanStatusBadgeForeground));
        OnPropertyChanged(nameof(IndexStatusBadgeBackground));
        OnPropertyChanged(nameof(IndexStatusBadgeForeground));
    }
}

