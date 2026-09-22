using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ParityProof.Core.Enums;
using ParityProof.Core.Models;

namespace ParityProof.App.ViewModels;

public sealed partial class StageProgressViewModel : ViewModelBase
{
    private const string COLOR_DISCOVERY = "#A855F7"; // Purple
    private const string COLOR_HASHING = "#38BDF8"; // Sky Blue
    private const string COLOR_MATCHING = "#10B981"; // Emerald
    private const string COLOR_DUPLICATES = "#F59E0B"; // Amber
    private const string COLOR_TRANSFER = "#38BDF8"; // Sky Blue
    private const string COLOR_VERIFY = "#10B981"; // Emerald

    private const string BADGE_BG_PENDING = "#1E293B";
    private const string BADGE_FG_PENDING = "#64748B";

    private const string BADGE_BG_RUNNING = "#0C4A6E";
    private const string BADGE_FG_RUNNING = "#38BDF8";

    private const string BADGE_BG_COMPLETED = "#064E3B";
    private const string BADGE_FG_COMPLETED = "#10B981";

    private const string BADGE_BG_FAILED = "#7F1D1D";
    private const string BADGE_FG_FAILED = "#EF4444";

    [ObservableProperty]
    private PipelineStageId _stageId;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(HasActiveFile))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeBackground))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeForeground))]
    private StageStatus _status;

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private long _processedUnits;

    [ObservableProperty]
    private long _totalUnits;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _telemetryText = string.Empty;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string _accentBrushHex = COLOR_HASHING;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFile))]
    private string? _activeFileName;

    [ObservableProperty]
    private double _activeFilePercentage;

    [ObservableProperty]
    private string? _activeFileProgressText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDestinationMeters))]
    private ObservableCollection<DestinationProgressViewModel> _destinationMeters = new();

    public bool IsActive => Status == StageStatus.Running;
    public bool IsCompleted => Status == StageStatus.Completed;
    public bool IsPending => Status == StageStatus.Pending;
    public bool HasActiveFile => !string.IsNullOrEmpty(ActiveFileName) && IsActive;
    public bool HasDestinationMeters => DestinationMeters.Count > 0;

    public string StatusBadgeText => Status switch
    {
        StageStatus.Pending => "PENDING",
        StageStatus.Running => "RUNNING",
        StageStatus.Completed => "DONE ✓",
        StageStatus.Failed => "FAILED ⚠️",
        _ => "IDLE"
    };

    public string StatusBadgeBackground => Status switch
    {
        StageStatus.Pending => BADGE_BG_PENDING,
        StageStatus.Running => BADGE_BG_RUNNING,
        StageStatus.Completed => BADGE_BG_COMPLETED,
        StageStatus.Failed => BADGE_BG_FAILED,
        _ => BADGE_BG_PENDING
    };

    public string StatusBadgeForeground => Status switch
    {
        StageStatus.Pending => BADGE_FG_PENDING,
        StageStatus.Running => BADGE_FG_RUNNING,
        StageStatus.Completed => BADGE_FG_COMPLETED,
        StageStatus.Failed => BADGE_FG_FAILED,
        _ => BADGE_FG_PENDING
    };

    public StageProgressViewModel(StageProgressInfo info)
    {
        UpdateFrom(info);
    }

    public void UpdateFrom(StageProgressInfo info)
    {
        StageId = info.Id;
        Title = info.Title;
        Status = info.Status;
        Percentage = info.Percentage;
        ProcessedUnits = info.ProcessedUnits;
        TotalUnits = info.TotalUnits;
        ProgressText = info.ProgressText;
        TelemetryText = info.TelemetryText;
        IsIndeterminate = info.IsIndeterminate;
        ActiveFileName = info.ActiveFileName;
        ActiveFilePercentage = info.ActiveFilePercentage;
        ActiveFileProgressText = info.ActiveFileProgressText;

        AccentBrushHex = info.Id switch
        {
            PipelineStageId.Discovery => COLOR_DISCOVERY,
            PipelineStageId.Hashing => COLOR_HASHING,
            PipelineStageId.DestinationMatching => COLOR_MATCHING,
            PipelineStageId.DuplicateAnalysis => COLOR_DUPLICATES,
            PipelineStageId.TransferWrite => COLOR_TRANSFER,
            PipelineStageId.PostTransferVerify => COLOR_VERIFY,
            _ => COLOR_HASHING
        };

        if (info.DestinationMeters is not null && info.DestinationMeters.Count > 0)
        {
            Dictionary<string, DestinationProgressInfo> incoming = new(StringComparer.OrdinalIgnoreCase);
            foreach (DestinationProgressInfo d in info.DestinationMeters)
            {
                incoming[d.DestinationId] = d;
            }

            for (int i = DestinationMeters.Count - 1; i >= 0; i--)
            {
                if (!incoming.ContainsKey(DestinationMeters[i].DestinationId))
                {
                    DestinationMeters.RemoveAt(i);
                }
            }

            foreach (DestinationProgressInfo d in info.DestinationMeters)
            {
                DestinationProgressViewModel? existing = null;
                foreach (DestinationProgressViewModel vm in DestinationMeters)
                {
                    if (string.Equals(vm.DestinationId, d.DestinationId, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = vm;
                        break;
                    }
                }

                if (existing is not null)
                {
                    existing.UpdateFrom(d);
                }
                else
                {
                    DestinationMeters.Add(new DestinationProgressViewModel(d));
                }
            }
        }
        else if (DestinationMeters.Count > 0)
        {
            DestinationMeters.Clear();
        }
    }
}
