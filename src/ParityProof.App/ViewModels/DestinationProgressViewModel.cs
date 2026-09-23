using CommunityToolkit.Mvvm.ComponentModel;
using ParityProof.Core.Models;

namespace ParityProof.App.ViewModels;

public sealed partial class DestinationProgressViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _destinationId = string.Empty;

    [ObservableProperty]
    private string _destinationName = string.Empty;

    [ObservableProperty]
    private long _processedBytes;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private double _megaBytesPerSecond;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public DestinationProgressViewModel(DestinationProgressInfo info)
    {
        UpdateFrom(info);
    }

    public void UpdateFrom(DestinationProgressInfo info)
    {
        DestinationId = info.DestinationId;
        DestinationName = info.DestinationName;
        ProcessedBytes = info.ProcessedBytes;
        TotalBytes = info.TotalBytes;
        Percentage = info.Percentage;
        MegaBytesPerSecond = info.MegaBytesPerSecond;
        StatusText = info.StatusText;
    }
}
