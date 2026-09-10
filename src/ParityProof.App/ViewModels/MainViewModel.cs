using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Logging;
using ParityProof.Core.Models;
using ParityProof.Core.Threading;
using ParityProof.Engine.Cache;
using ParityProof.Engine.IO;
using ParityProof.Engine.Matching;
using ParityProof.Engine.Reporting;
using ParityProof.Engine.Transfer;
using ParityProof.Platform;
using ParityProof.Platform.Common;
using Serilog;

namespace ParityProof.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger _logger = AppLogger.ForContext<MainViewModel>();

    private const string COLOR_SAFE = "#10B981"; // Emerald
    private const string COLOR_PARTIAL = "#F59E0B"; // Amber
    private const string COLOR_UNSAFE = "#F43F5E"; // Rose
    private const string COLOR_IDLE = "#3B82F6"; // Blue

    private readonly IVerificationEngine _verificationEngine;
    private readonly IMediaCopier _mediaCopier;
    private readonly IDriveDetector _driveDetector;
    private readonly SqliteIndexCache _indexCache;
    private readonly IDuplicateAnalyzer _duplicateAnalyzer;
    private readonly Stopwatch _operationStopwatch = new();
    private readonly DispatcherTimer _elapsedTimer;

    private CancellationTokenSource? _cts;
    private PauseTokenSource? _pauseTokenSource;
    private VerificationSummary? _lastSummary;
    private IReadOnlyList<VerificationResultItem> _lastResults = Array.Empty<VerificationResultItem>();

    [ObservableProperty]
    private string _sourcePath = string.Empty;

    private int _destinationSequence = 0;

    [ObservableProperty]
    private ObservableCollection<BackupDestinationViewModel> _destinations = new();

    [ObservableProperty]
    private VerificationMode _selectedMode = VerificationMode.Quick;

    [ObservableProperty]
    private string _displayedScanMode = "Quick";

    partial void OnSelectedModeChanged(VerificationMode value)
    {
        if (!HasResults)
        {
            DisplayedScanMode = FormatScanMode(value);
        }
    }

    [ObservableProperty]
    private bool _scanDuplicatesDuringVerification;

    [ObservableProperty]
    private ObservableCollection<FilterPreset> _filterPresets = new()
    {
        FilterPreset.PhotosOnly,
        FilterPreset.PhotosAndVideos,
        FilterPreset.AllCameraMediaWithSidecars
    };

    [ObservableProperty]
    private FilterPreset _selectedPreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOperation))]
    [NotifyPropertyChangedFor(nameof(CanStartCopy))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOperation))]
    [NotifyPropertyChangedFor(nameof(CanStartCopy))]
    private bool _isCopying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOperation))]
    [NotifyPropertyChangedFor(nameof(CanStartCopy))]
    private bool _isOperationActive;

    [ObservableProperty]
    private bool _isPausing;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isCancelling;

    [ObservableProperty]
    private bool _showCancelConfirmation;

    [ObservableProperty]
    private string _operationTitle = "OPERATION STATUS";

    [ObservableProperty]
    private string _operationAccentColor = "#3B82F6";

    [ObservableProperty]
    private string _stateBadgeText = "IDLE";

    [ObservableProperty]
    private string _stateBadgeBackground = "#1E293B";

    [ObservableProperty]
    private string _stateBadgeForeground = "#94A3B8";

    [ObservableProperty]
    private string _currentFileDetailText = "No active background operation.";

    [ObservableProperty]
    private double _batchProgressPercentage;

    [ObservableProperty]
    private bool _isBatchIndeterminate;

    [ObservableProperty]
    private string _batchProgressSummaryText = "Waiting for operation...";

    [ObservableProperty]
    private double _currentFileProgressPercentage;

    [ObservableProperty]
    private string _currentFileLabelText = "Current File";

    [ObservableProperty]
    private string _currentFileProgressText = string.Empty;

    [ObservableProperty]
    private string _throughputText = "0.0 MB/s";

    [ObservableProperty]
    private string _etaText = "--:--";

    [ObservableProperty]
    private string _filesCounterText = "0 files";

    [ObservableProperty]
    private string _pauseResumeButtonText = "⏸ PAUSE";

    [ObservableProperty]
    private string _pauseResumeButtonBackground = "#334155";

    [ObservableProperty]
    private bool _canPauseResume;

    [ObservableProperty]
    private string _statusMessage = "Ready. Select an SD card or directory to verify.";

    [ObservableProperty]
    private string _currentProgressPhase = "Ready. Select an SD card or directory to verify.";

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _safetyBadgeText = "READY FOR VERIFICATION";

    [ObservableProperty]
    private string _safetyBadgeColor = COLOR_IDLE;

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private string _totalSizeFormatted = "0.0 GB";

    [ObservableProperty]
    private int _verifiedCount;

    [ObservableProperty]
    private int _missingCount;

    [ObservableProperty]
    private int _corruptCount;

    [ObservableProperty]
    private string _durationFormatted = "--:--";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _activeTab = "All";

    [ObservableProperty]
    private bool _isDuplicatesTabActive;

    [ObservableProperty]
    private bool _hasResults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartCopy))]
    private bool _hasMissingFiles;

    [ObservableProperty]
    private string _reclaimableSpaceFormatted = "0 B";

    [ObservableProperty]
    private int _duplicateFilesCount;

    [ObservableProperty]
    private int _crossDestinationRedundantCount;

    [ObservableProperty]
    private bool _hasDuplicates;

    [ObservableProperty]
    private bool _hasDuplicateGroups;

    [ObservableProperty]
    private string _duplicateFilterMode = "All";

    partial void OnDuplicateFilterModeChanged(string value)
    {
        ApplyFilter();
    }

    [RelayCommand]
    public void SetDuplicateFilterMode(string mode)
    {
        DuplicateFilterMode = mode;
    }

    [ObservableProperty]
    private string _copyProgressText = string.Empty;

    [ObservableProperty]
    private double _copyProgressPercentage;

    [ObservableProperty]
    private string _copySpeedText = string.Empty;

    [ObservableProperty]
    private string _copyEtaText = string.Empty;

    public bool CanStartOperation => !IsOperationActive;
    public bool CanStartCopy => HasMissingFiles && !IsOperationActive;

    public ObservableRangeCollection<MediaItemViewModel> AllItems { get; } = new();
    public ObservableRangeCollection<MediaItemViewModel> FilteredItems { get; } = new();
    public ObservableRangeCollection<DuplicateGroupViewModel> AllDuplicateGroups { get; } = new();
    public ObservableRangeCollection<DuplicateGroupViewModel> FilteredDuplicateGroups { get; } = new();

    [ObservableProperty]
    private MediaItemViewModel? _selectedMediaItem;

    partial void OnSelectedMediaItemChanged(MediaItemViewModel? value)
    {
        if (value is null)
        {
            IsInspectorOpen = false;
            InspectorThumbnail?.Dispose();
            InspectorThumbnail = null;
            InspectorMetadata = ExifMetadataInfo.Empty;
        }
        else
        {
            IsInspectorOpen = true;
            _ = LoadInspectorDetailsAsync(value);
        }
    }

    [ObservableProperty]
    private bool _isInspectorOpen;

    partial void OnIsInspectorOpenChanged(bool value)
    {
        if (value && SelectedMediaItem is not null && InspectorThumbnail is null && !IsLoadingThumbnail)
        {
            _ = LoadInspectorDetailsAsync(SelectedMediaItem);
        }
    }

    [ObservableProperty]
    private double _sidebarWidth = 340;

    [ObservableProperty]
    private double _inspectorWidth = 360;

    [ObservableProperty]
    private Bitmap? _inspectorThumbnail;

    [ObservableProperty]
    private bool _isLoadingThumbnail;

    [ObservableProperty]
    private ExifMetadataInfo _inspectorMetadata = ExifMetadataInfo.Empty;

    private CancellationTokenSource? _thumbnailCts;

    private async Task LoadInspectorDetailsAsync(MediaItemViewModel item)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        CancellationToken ct = _thumbnailCts.Token;

        IsLoadingThumbnail = true;
        InspectorThumbnail?.Dispose();
        InspectorThumbnail = null;
        InspectorMetadata = ExifMetadataInfo.Empty;

        string filePath = item.FullPath;

        try
        {
            ExifMetadataInfo metadata = await Task.Run(
                () => ExifMetadataExtractor.ExtractMetadata(filePath),
                ct).ConfigureAwait(true);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            InspectorMetadata = metadata;

            byte[]? thumbBytes = await Task.Run(
                () => ExifMetadataExtractor.ExtractThumbnailBytes(filePath),
                ct).ConfigureAwait(true);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (thumbBytes is not null && thumbBytes.Length > 0)
            {
                try
                {
                    using MemoryStream ms = new(thumbBytes);
                    Bitmap bitmap = Bitmap.DecodeToWidth(ms, 360);
                    if (ct.IsCancellationRequested)
                    {
                        bitmap.Dispose();
                        return;
                    }

                    InspectorThumbnail = bitmap;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Embedded EXIF thumbnail decoding failed for {FilePath}; falling back to full stream decoding", filePath);
                }
            }

            if (InspectorThumbnail is null && !ct.IsCancellationRequested)
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".tif" or ".tiff")
                {
                    Bitmap? streamedBitmap = await Task.Run(() =>
                    {
                        try
                        {
                            using FileStream fs = new(
                                filePath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete,
                                4096,
                                FileOptions.SequentialScan);
                            return Bitmap.DecodeToWidth(fs, 360);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "Direct stream image decoding failed for {FilePath}", filePath);
                            return null;
                        }
                    }, ct).ConfigureAwait(true);

                    if (ct.IsCancellationRequested)
                    {
                        streamedBitmap?.Dispose();
                        return;
                    }

                    InspectorThumbnail = streamedBitmap;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation when user selects a different file
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load thumbnail preview for {FilePath}", filePath);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingThumbnail = false;
            }
        }
    }

    [RelayCommand]
    private void CloseInspector()
    {
        IsInspectorOpen = false;
    }

    [RelayCommand]
    private void ToggleInspector()
    {
        IsInspectorOpen = !IsInspectorOpen;
    }

    [ObservableProperty]
    private bool _showTypeColumn = true;

    [ObservableProperty]
    private bool _showFileColumn = true;

    [ObservableProperty]
    private bool _showFormatColumn = true;

    [ObservableProperty]
    private bool _showSizeColumn = true;

    [ObservableProperty]
    private bool _showStatusColumn = true;

    [ObservableProperty]
    private bool _showDestinationsColumn = true;

    [ObservableProperty]
    private bool _showRelativePathColumn = true;

    [RelayCommand]
    private void OpenFile(MediaItemViewModel? item)
    {
        MediaItemViewModel? target = item ?? SelectedMediaItem;
        if (target is not null)
        {
            FileOpener.OpenFile(target.FullPath);
        }
    }

    [RelayCommand]
    private void RevealInFileManager(MediaItemViewModel? item)
    {
        MediaItemViewModel? target = item ?? SelectedMediaItem;
        if (target is not null)
        {
            FileOpener.RevealInFileManager(target.FullPath);
        }
    }

    [RelayCommand]
    private async Task CopyRelativePathAsync(MediaItemViewModel? item)
    {
        MediaItemViewModel? target = item ?? SelectedMediaItem;
        if (target is not null)
        {
            await SetClipboardTextAsync(target.RelativePath).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task CopyFullPathAsync(MediaItemViewModel? item)
    {
        MediaItemViewModel? target = item ?? SelectedMediaItem;
        if (target is not null)
        {
            await SetClipboardTextAsync(target.FullPath).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task CopyHashFingerprintAsync(MediaItemViewModel? item)
    {
        MediaItemViewModel? target = item ?? SelectedMediaItem;
        if (target is null)
        {
            return;
        }

        string hashText = target.HasFullHash
            ? target.FullHashHex
            : target.HasDeepHash
                ? target.DeepHashHex
                : target.HeadHashHex;

        await SetClipboardTextAsync(hashText).ConfigureAwait(false);
    }

    [RelayCommand]
    private void InspectDetails(MediaItemViewModel? item)
    {
        if (item is not null)
        {
            SelectedMediaItem = item;
            IsInspectorOpen = true;
        }
        else if (SelectedMediaItem is not null)
        {
            IsInspectorOpen = true;
        }
    }

    private static async Task SetClipboardTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is not null)
            {
                await desktop.MainWindow.Clipboard.SetTextAsync(text);
            }
        });
    }

    public MainViewModel()
    {
        _selectedPreset = FilterPresets[0];

        _indexCache = new SqliteIndexCache();
        _duplicateAnalyzer = new DuplicateAnalyzer(_indexCache);
        _verificationEngine = new MultiDestinationVerifier(_indexCache, _duplicateAnalyzer);
        _mediaCopier = new MediaCopier();

        _driveDetector = DriveDetectorFactory.Create();
        _driveDetector.DriveChanged += OnDriveChanged;
        _driveDetector.Start();

        _elapsedTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _elapsedTimer.Tick += OnElapsedTimerTick;
    }

    private void OnElapsedTimerTick(object? sender, EventArgs e)
    {
        if (!IsPaused && _operationStopwatch.IsRunning)
        {
            DurationFormatted = $"{_operationStopwatch.Elapsed.TotalSeconds:F1}s";
        }
    }

    private void OnDriveChanged(object? sender, DriveNotificationEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.EventType == DriveEventType.Inserted)
            {
                if (string.IsNullOrEmpty(SourcePath))
                {
                    SourcePath = e.DrivePath;
                }
                StatusMessage = $"Removable media detected: {e.VolumeLabel} ({e.DrivePath})";
            }
            else if (e.EventType == DriveEventType.Removed)
            {
                StatusMessage = $"Drive removed: {e.DrivePath}";
                if (string.Equals(SourcePath, e.DrivePath, StringComparison.OrdinalIgnoreCase))
                {
                    SourcePath = string.Empty;
                }
            }
        });
    }

    private DispatcherTimer? _searchDebounceTimer;

    partial void OnSearchTextChanged(string value)
    {
        if (_searchDebounceTimer is null)
        {
            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _searchDebounceTimer.Tick += (s, e) =>
            {
                _searchDebounceTimer.Stop();
                ApplyFilter();
            };
        }
        else
        {
            _searchDebounceTimer.Stop();
        }

        _searchDebounceTimer.Start();
    }

    partial void OnActiveTabChanged(string value)
    {
        IsDuplicatesTabActive = string.Equals(value, "Duplicates", StringComparison.OrdinalIgnoreCase);
        ApplyFilter();
    }

    [RelayCommand]
    public void ApplyFilter()
    {
        string filter = SearchText.Trim();

        List<MediaItemViewModel> matchedItems = new();
        foreach (MediaItemViewModel item in AllItems)
        {
            bool matchesTab = ActiveTab switch
            {
                "Verified" => item.IsVerified,
                "Missing" => item.IsMissing,
                "Corrupt" => item.IsCorrupt,
                _ => true
            };

            if (!matchesTab)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(filter) &&
                !item.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matchedItems.Add(item);
        }

        FilteredItems.ReplaceAll(matchedItems);

        List<DuplicateGroupViewModel> matchedGroups = new();
        foreach (DuplicateGroupViewModel group in AllDuplicateGroups)
        {
            bool matchesMode = DuplicateFilterMode switch
            {
                "DuplicatesOnly" => group.IsIntraDestination,
                "RedundancyOnly" => !group.IsIntraDestination,
                _ => true
            };

            if (!matchesMode)
            {
                continue;
            }

            if (string.IsNullOrEmpty(filter) ||
                group.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                group.Files.Any(f => f.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                     f.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                matchedGroups.Add(group);
            }
        }

        FilteredDuplicateGroups.ReplaceAll(matchedGroups);
    }

    [RelayCommand]
    private void SetActiveTab(string tabName)
    {
        ActiveTab = tabName;
    }

    public static bool TryValidateSourceAndDestinationPaths(
        string? sourcePath,
        string? destinationPath,
        out string validationError)
    {
        validationError = string.Empty;
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return true;
        }

        string canonicalSource;
        string canonicalDest;
        try
        {
            canonicalSource = Path.GetFullPath(sourcePath.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            canonicalDest = Path.GetFullPath(destinationPath.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to canonicalize path for validation: source {SourcePath}, dest {DestPath}", sourcePath, destinationPath);
            validationError = $"Invalid path format: {ex.Message}";
            return false;
        }

        if (string.Equals(canonicalSource, canonicalDest, StringComparison.OrdinalIgnoreCase))
        {
            validationError = "Destination path cannot be identical to the source storage path.";
            return false;
        }

        string sourceWithSlash = canonicalSource + Path.DirectorySeparatorChar;
        if (canonicalDest.StartsWith(sourceWithSlash, StringComparison.OrdinalIgnoreCase))
        {
            validationError = "Destination path cannot be located inside the source storage path.";
            return false;
        }

        return true;
    }

    [RelayCommand]
    public void AddDestination(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string trimmed = path.Trim();
        if (!TryValidateSourceAndDestinationPaths(SourcePath, trimmed, out string validationError))
        {
            StatusMessage = validationError;
            return;
        }

        string canonicalDest;
        try
        {
            canonicalDest = Path.GetFullPath(trimmed)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to resolve full path for destination {Path}", trimmed);
            StatusMessage = $"Invalid path: {ex.Message}";
            return;
        }

        bool isDuplicate = Destinations.Any(d =>
            string.Equals(
                Path.GetFullPath(d.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                canonicalDest,
                StringComparison.OrdinalIgnoreCase));

        if (isDuplicate)
        {
            StatusMessage = "Destination path has already been added.";
            return;
        }

        int seq = Interlocked.Increment(ref _destinationSequence);
        string id = $"dest_{seq}";
        while (Destinations.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            seq = Interlocked.Increment(ref _destinationSequence);
            id = $"dest_{seq}";
        }
        string name = Path.GetFileName(trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            name = trimmed;
        }

        Destinations.Add(new BackupDestinationViewModel(id, name, trimmed));
    }

    [RelayCommand]
    private void RemoveDestination(BackupDestinationViewModel dest)
    {
        Destinations.Remove(dest);
    }

    [RelayCommand]
    private void RequestPauseResume()
    {
        if (_pauseTokenSource is null)
        {
            return;
        }

        if (!IsPaused && !IsPausing)
        {
            IsPausing = true;
            CanPauseResume = false;
            PauseResumeButtonText = "⏳ PAUSING...";
            StateBadgeText = "PAUSING...";
            StateBadgeBackground = "#78350F";
            StateBadgeForeground = "#FBBF24";
            StatusMessage = "Pausing operation...";
            _pauseTokenSource.Pause();
            _operationStopwatch.Stop();
        }
        else if (IsPaused)
        {
            IsPaused = false;
            IsPausing = false;
            CanPauseResume = true;
            PauseResumeButtonText = "⏸ PAUSE";
            PauseResumeButtonBackground = "#D97706";
            StateBadgeText = "RUNNING";
            StateBadgeBackground = "#064E3B";
            StateBadgeForeground = "#10B981";
            StatusMessage = "Operation resumed.";
            _operationStopwatch.Start();
            _pauseTokenSource.Resume();
        }
    }

    [RelayCommand]
    private void RequestCancel()
    {
        if (!IsOperationActive)
        {
            return;
        }

        ShowCancelConfirmation = true;
    }

    [RelayCommand]
    private void ConfirmCancel()
    {
        ShowCancelConfirmation = false;
        IsCancelling = true;
        CanPauseResume = false;
        StateBadgeText = "CANCELLING...";
        StateBadgeBackground = "#7F1D1D";
        StateBadgeForeground = "#F87171";
        StatusMessage = "Cancelling operation and cleaning up...";

        _pauseTokenSource?.Resume();
        _cts?.Cancel();
    }

    [RelayCommand]
    private void DismissCancel()
    {
        ShowCancelConfirmation = false;
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestCancel();
    }

    [RelayCommand]
    private async Task VerifyAsync()
    {
        string trimmedSource = SourcePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedSource) || !Directory.Exists(trimmedSource))
        {
            StatusMessage = string.IsNullOrWhiteSpace(trimmedSource)
                ? "Please select a valid source directory."
                : $"Source directory not found: {trimmedSource}";
            return;
        }

        if (Destinations.Count == 0)
        {
            StatusMessage = "Please add at least one backup destination.";
            return;
        }

        foreach (BackupDestinationViewModel dest in Destinations.Where(d => d.IsEnabled))
        {
            if (!TryValidateSourceAndDestinationPaths(trimmedSource, dest.RootPath, out string validationError))
            {
                StatusMessage = $"Destination '{dest.Name}' conflict: {validationError}";
                return;
            }
        }

        SourcePath = trimmedSource;

        IsRunning = true;
        IsOperationActive = true;
        IsBatchIndeterminate = true;
        IsPausing = false;
        IsPaused = false;
        IsCancelling = false;
        ShowCancelConfirmation = false;
        CanPauseResume = true;
        OperationTitle = "MEDIA VERIFICATION";
        OperationAccentColor = "#38BDF8";
        DisplayedScanMode = FormatScanMode(SelectedMode);
        StateBadgeText = "RUNNING";
        StateBadgeBackground = "#064E3B";
        StateBadgeForeground = "#10B981";
        PauseResumeButtonText = "⏸ PAUSE";
        PauseResumeButtonBackground = "#D97706";
        BatchProgressPercentage = 0;
        BatchProgressSummaryText = "Scanning directory...";
        CurrentFileProgressPercentage = 0;
        ThroughputText = "0.0 MB/s";
        EtaText = "--:--";
        FilesCounterText = "0 found";
        CurrentProgressPhase = "Scanning Source Directory...";
        CurrentFileDetailText = "Traversing file tree...";

        _cts = new CancellationTokenSource();
        _pauseTokenSource = new PauseTokenSource();
        _operationStopwatch.Restart();
        _elapsedTimer.Start();

        List<BackupDestination> destinationModels = Destinations
            .Where(d => d.IsEnabled)
            .Select(d => d.ToModel())
            .ToList();

        _logger.Information(
            "Starting verification for source {SourcePath} with {DestCount} destinations in {Mode} mode",
            trimmedSource,
            destinationModels.Count,
            SelectedMode);

        try
        {
            Progress<VerificationProgress> progress = new(p =>
            {
                CurrentProgressPhase = p.Phase;
                if (p.IsPaused)
                {
                    IsPausing = false;
                    IsPaused = true;
                    CanPauseResume = true;
                    IsBatchIndeterminate = false;
                    PauseResumeButtonText = "▶ RESUME";
                    PauseResumeButtonBackground = "#059669";
                    StateBadgeText = "PAUSED";
                    StateBadgeBackground = "#78350F";
                    StateBadgeForeground = "#FBBF24";
                    StatusMessage = "Operation paused.";
                }
                else if (!IsPausing && !IsCancelling)
                {
                    StateBadgeText = "RUNNING";
                    StateBadgeBackground = "#064E3B";
                    StateBadgeForeground = "#10B981";
                }

                if (p.TotalFiles > 0)
                {
                    IsBatchIndeterminate = false;
                    BatchProgressPercentage = (p.ProcessedFiles / (double)p.TotalFiles) * 100.0;
                    ProgressPercentage = BatchProgressPercentage;
                    BatchProgressSummaryText = $"{BatchProgressPercentage:F0}% ({p.ProcessedFiles} / {p.TotalFiles})";
                    FilesCounterText = $"{p.ProcessedFiles} / {p.TotalFiles}";
                }
                else
                {
                    IsBatchIndeterminate = !p.IsPaused;
                    BatchProgressPercentage = 0;
                    BatchProgressSummaryText = p.ProcessedBytes > 0
                        ? $"Scanning: {p.ProcessedFiles} files ({FormatBytes(p.ProcessedBytes)})"
                        : $"Scanning: {p.ProcessedFiles} files";
                    FilesCounterText = $"{p.ProcessedFiles} found";
                }

                if (!string.IsNullOrEmpty(p.CurrentFile))
                {
                    CurrentFileDetailText = p.TotalFiles == 0 ? $"Traversing: {p.CurrentFile}" : p.CurrentFile;
                    CurrentFileLabelText = Path.GetFileName(p.CurrentFile);
                    if (string.IsNullOrEmpty(CurrentFileLabelText))
                    {
                        CurrentFileLabelText = p.CurrentFile;
                    }
                }

                if (p.CurrentFileBytes > 0)
                {
                    CurrentFileProgressPercentage = Math.Min(100.0, (p.CurrentFileProcessedBytes / (double)p.CurrentFileBytes) * 100.0);
                    CurrentFileProgressText = $"{FormatBytes(p.CurrentFileProcessedBytes)} / {FormatBytes(p.CurrentFileBytes)}";
                }
                else
                {
                    CurrentFileProgressPercentage = 0;
                    CurrentFileProgressText = string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(p.MultiDriveThroughputText))
                {
                    ThroughputText = p.MultiDriveThroughputText;
                }
                else if (p.MegaBytesPerSecond > 0)
                {
                    ThroughputText = $"{p.MegaBytesPerSecond:F1} MB/s";
                }
                else if (p.ScanRateFilesPerSecond > 0)
                {
                    ThroughputText = $"{p.ScanRateFilesPerSecond:N0} files/s";
                }
                else
                {
                    ThroughputText = "-- MB/s";
                }

                if (p.EstimatedTimeRemaining > TimeSpan.Zero)
                {
                    EtaText = $"{p.EstimatedTimeRemaining:mm\\:ss}";
                }
                else
                {
                    EtaText = "--:--";
                }
            });

            // Offload to background thread pool to ensure UI Dispatcher never blocks during I/O
            (VerificationSummary summary, IReadOnlyList<VerificationResultItem> results) =
                await Task.Run(() => _verificationEngine.VerifyAsync(
                    trimmedSource,
                    destinationModels,
                    SelectedMode,
                    SelectedPreset,
                    progress,
                    _cts.Token,
                    _pauseTokenSource.Token,
                    scanDuplicates: ScanDuplicatesDuringVerification)).ConfigureAwait(true);

            _lastSummary = summary;
            _lastResults = results;

            TotalFiles = summary.TotalFiles;
            TotalSizeFormatted = $"{(summary.TotalBytes / (1024.0 * 1024.0 * 1024.0)):F2} GB";
            VerifiedCount = summary.FullyVerifiedFiles;
            MissingCount = summary.MissingFiles;
            CorruptCount = summary.CorruptFiles;
            DurationFormatted = $"{summary.Duration.TotalSeconds:F1}s";
            DisplayedScanMode = FormatScanMode(summary.Mode);

            int verifiedWithAtLeastOneCopy = summary.FullyVerifiedFiles + summary.PartiallyVerifiedFiles;
            double backupPercentage = summary.TotalFiles > 0
                ? (double)verifiedWithAtLeastOneCopy / summary.TotalFiles * 100.0
                : 0.0;
            int unprotectedCount = summary.MissingFiles + summary.CorruptFiles;

            SafetyBadgeText = summary.SafetyStatus switch
            {
                OverallSafetyStatus.SafeToFormat => "SAFE TO FORMAT - 100% BACKED UP",
                OverallSafetyStatus.PartiallyBackedUp => $"PARTIALLY BACKED UP - 100% SINGLE COPY ({(summary.PartiallyVerifiedFiles == 1 ? "1 NEEDS" : $"{summary.PartiallyVerifiedFiles} NEED")} REDUNDANCY)",
                OverallSafetyStatus.NoMediaFound => "NO MEDIA DETECTED - DO NOT FORMAT",
                _ => $"UNSAFE TO FORMAT - {backupPercentage:F0}% BACKED UP ({unprotectedCount} UNPROTECTED)"
            };

            SafetyBadgeColor = summary.SafetyStatus switch
            {
                OverallSafetyStatus.SafeToFormat => COLOR_SAFE,
                OverallSafetyStatus.PartiallyBackedUp => COLOR_PARTIAL,
                OverallSafetyStatus.NoMediaFound => "#64748B",
                _ => COLOR_UNSAFE
            };

            List<MediaItemViewModel> newItems = new(results.Count);
            foreach (VerificationResultItem item in results)
            {
                newItems.Add(new MediaItemViewModel(item));
            }
            AllItems.ReplaceAll(newItems);

            if (summary.DuplicateAnalysis is not null)
            {
                DuplicateFilesCount = summary.DuplicateAnalysis.TotalDuplicateCopies;
                ReclaimableSpaceFormatted = FormatBytes(summary.DuplicateAnalysis.TotalReclaimableBytes);
                CrossDestinationRedundantCount = summary.DuplicateAnalysis.CrossDestinationRedundantFileCount;
                HasDuplicates = summary.DuplicateAnalysis.TotalDuplicateCopies > 0;
                HasDuplicateGroups = summary.DuplicateAnalysis.Groups.Count > 0;

                List<DuplicateGroupViewModel> newGroups = new(summary.DuplicateAnalysis.Groups.Count);
                foreach (DuplicateGroup group in summary.DuplicateAnalysis.Groups)
                {
                    newGroups.Add(new DuplicateGroupViewModel(group));
                }
                AllDuplicateGroups.ReplaceAll(newGroups);
            }
            else
            {
                DuplicateFilesCount = 0;
                ReclaimableSpaceFormatted = "0 B";
                CrossDestinationRedundantCount = 0;
                HasDuplicates = false;
                HasDuplicateGroups = false;
            }

            HasResults = true;
            HasMissingFiles = summary.MissingFiles > 0;
            StatusMessage = $"Verification complete: {VerifiedCount} verified, {MissingCount} missing in {DurationFormatted}.";

            OperationTitle = "VERIFICATION COMPLETE";
            OperationAccentColor = summary.SafetyStatus switch
            {
                OverallSafetyStatus.SafeToFormat => COLOR_SAFE,
                OverallSafetyStatus.PartiallyBackedUp => COLOR_PARTIAL,
                OverallSafetyStatus.NoMediaFound => "#64748B",
                _ => COLOR_UNSAFE
            };
            StateBadgeText = "FINISHED";
            StateBadgeBackground = "#064E3B";
            StateBadgeForeground = "#10B981";
            CurrentProgressPhase = StatusMessage;
            BatchProgressPercentage = 100.0;
            BatchProgressSummaryText = $"100% ({summary.TotalFiles} / {summary.TotalFiles})";

            ApplyFilter();

            _logger.Information(
                "Verification completed in {Duration:F1}s: {Verified} verified, {Missing} missing, {Corrupt} corrupt, safety status {SafetyStatus}",
                summary.Duration.TotalSeconds,
                summary.FullyVerifiedFiles,
                summary.MissingFiles,
                summary.CorruptFiles,
                summary.SafetyStatus);

            // Refresh destination capacities and check for potential space exhaustion
            long missingBytes = results
                .Where(r => !r.IsFullyVerified)
                .Sum(r => r.SourceFile.FileLength);

            foreach (BackupDestinationViewModel dest in Destinations)
            {
                dest.RefreshCapacity();
                if (HasMissingFiles)
                {
                    dest.CheckRequiredSpace(missingBytes);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Verification cancelled by user for source {SourcePath}", SourcePath);
            StatusMessage = "Verification cancelled by user.";
            CurrentProgressPhase = "Cancelled.";
            StateBadgeText = "CANCELLED";
            StateBadgeBackground = "#7F1D1D";
            StateBadgeForeground = "#F87171";
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "Verification failed for source {SourcePath} across {DestCount} destinations",
                SourcePath,
                Destinations.Count);
            StatusMessage = $"Error: {ex.Message}";
            StateBadgeText = "ERROR";
            StateBadgeBackground = "#7F1D1D";
            StateBadgeForeground = "#F87171";
        }
        finally
        {
            _elapsedTimer.Stop();
            _operationStopwatch.Stop();
            DurationFormatted = $"{_operationStopwatch.Elapsed.TotalSeconds:F1}s";
            IsRunning = false;
            IsOperationActive = false;
            IsBatchIndeterminate = false;
            IsPausing = false;
            IsPaused = false;
            IsCancelling = false;
            ShowCancelConfirmation = false;
            CanPauseResume = false;
            PauseResumeButtonText = "⏸ PAUSE";
            PauseResumeButtonBackground = "#334155";
        }
    }

    [RelayCommand]
    private async Task ScanDuplicatesAsync()
    {
        string trimmedSource = SourcePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedSource) || !Directory.Exists(trimmedSource))
        {
            StatusMessage = string.IsNullOrWhiteSpace(trimmedSource)
                ? "Please select a valid source directory to scan for duplicates."
                : $"Source directory not found: {trimmedSource}";
            return;
        }

        if (Destinations.Count == 0)
        {
            StatusMessage = "Please add at least one backup destination to scan for duplicates.";
            return;
        }

        List<BackupDestination> destinationModels = Destinations
            .Where(d => d.IsEnabled)
            .Select(d => d.ToModel())
            .ToList();

        if (destinationModels.Count == 0)
        {
            StatusMessage = "Please enable at least one backup destination.";
            return;
        }

        IsRunning = true;
        IsOperationActive = true;
        IsBatchIndeterminate = true;
        IsPausing = false;
        IsPaused = false;
        IsCancelling = false;
        ShowCancelConfirmation = false;
        CanPauseResume = true;
        OperationTitle = "DUPLICATE STORAGE AUDIT";
        OperationAccentColor = "#F59E0B";
        DisplayedScanMode = FormatScanMode(SelectedMode);
        StateBadgeText = "SCANNING";
        StateBadgeBackground = "#78350F";
        StateBadgeForeground = "#FBBF24";
        PauseResumeButtonText = "⏸ PAUSE";
        PauseResumeButtonBackground = "#D97706";
        BatchProgressPercentage = 0;
        BatchProgressSummaryText = "Scanning destinations...";
        CurrentFileProgressPercentage = 0;
        ThroughputText = "-- MB/s";
        EtaText = "--:--";
        FilesCounterText = "0 files";
        CurrentProgressPhase = "Scanning Destination Directories...";
        CurrentFileDetailText = "Traversing destination file trees...";

        _cts = new CancellationTokenSource();
        _pauseTokenSource = new PauseTokenSource();
        _operationStopwatch.Restart();
        _elapsedTimer.Start();

        _logger.Information(
            "Starting duplicate scan for source {SourcePath} across {DestCount} destinations",
            trimmedSource,
            destinationModels.Count);

        try
        {
            Progress<VerificationProgress> progress = new(p =>
            {
                CurrentProgressPhase = p.Phase;
                if (!string.IsNullOrEmpty(p.CurrentFile))
                {
                    CurrentFileDetailText = p.CurrentFile;
                    CurrentFileLabelText = Path.GetFileName(p.CurrentFile);
                }

                if (p.TotalFiles > 0)
                {
                    IsBatchIndeterminate = false;
                    BatchProgressPercentage = (p.ProcessedFiles / (double)p.TotalFiles) * 100.0;
                    BatchProgressSummaryText = $"{BatchProgressPercentage:F0}% ({p.ProcessedFiles} / {p.TotalFiles})";
                    FilesCounterText = $"{p.ProcessedFiles} / {p.TotalFiles}";
                }
                else
                {
                    IsBatchIndeterminate = !p.IsPaused;
                    BatchProgressPercentage = 0;
                    BatchProgressSummaryText = p.ProcessedBytes > 0
                        ? $"Scanning: {p.ProcessedFiles} files ({FormatBytes(p.ProcessedBytes)})"
                        : $"Scanning: {p.ProcessedFiles} files";
                    FilesCounterText = $"{p.ProcessedFiles} found";
                }

                if (p.CurrentFileBytes > 0)
                {
                    CurrentFileProgressPercentage = Math.Min(100.0, (p.CurrentFileProcessedBytes / (double)p.CurrentFileBytes) * 100.0);
                    CurrentFileProgressText = $"{FormatBytes(p.CurrentFileProcessedBytes)} / {FormatBytes(p.CurrentFileBytes)}";
                }
                else
                {
                    CurrentFileProgressPercentage = 0;
                    CurrentFileProgressText = string.Empty;
                }

                if (p.MegaBytesPerSecond > 0)
                {
                    ThroughputText = $"{p.MegaBytesPerSecond:F1} MB/s";
                }
                else if (p.ScanRateFilesPerSecond > 0)
                {
                    ThroughputText = $"{p.ScanRateFilesPerSecond:N0} files/s";
                }
                else
                {
                    ThroughputText = "-- MB/s";
                }

                if (p.EstimatedTimeRemaining > TimeSpan.Zero)
                {
                    EtaText = $"{p.EstimatedTimeRemaining:mm\\:ss}";
                }
                else
                {
                    EtaText = "--:--";
                }
            });

            DuplicateAnalysisResult result = await Task.Run(async () =>
            {
                _cts.Token.ThrowIfCancellationRequested();
                IReadOnlyList<MediaFile> sourceFiles = await FastDirectoryScanner.ScanDirectoryAsync(
                    trimmedSource,
                    SelectedPreset,
                    phaseName: "Scanning Source Directory...",
                    referenceTotalFiles: 0,
                    referenceTotalBytes: 0,
                    progress: progress,
                    cancellationToken: _cts.Token,
                    pauseToken: _pauseTokenSource.Token).ConfigureAwait(false);

                if (sourceFiles.Count == 0)
                {
                    return DuplicateAnalysisResult.Empty;
                }

                Task<(string Id, IReadOnlyList<MediaFile> Files)>[] destScanTasks = destinationModels
                    .Select(async dest =>
                    {
                        IReadOnlyList<MediaFile> files = await FastDirectoryScanner.ScanDirectoryAsync(
                            dest.RootPath,
                            SelectedPreset,
                            phaseName: $"Indexing: {dest.Name}",
                            referenceTotalFiles: 0,
                            referenceTotalBytes: 0,
                            progress: progress,
                            cancellationToken: _cts.Token,
                            pauseToken: _pauseTokenSource.Token).ConfigureAwait(false);
                        return (dest.Id, files);
                    })
                    .ToArray();

                (string Id, IReadOnlyList<MediaFile> Files)[] scannedResults =
                    await Task.WhenAll(destScanTasks).ConfigureAwait(false);

                Dictionary<string, IReadOnlyList<MediaFile>> destinationFiles = new();
                foreach ((string destId, IReadOnlyList<MediaFile> files) in scannedResults)
                {
                    destinationFiles[destId] = files;
                }

                return await _duplicateAnalyzer.AnalyzeDuplicatesAsync(
                    sourceFiles,
                    destinationModels,
                    destinationFiles,
                    SelectedMode,
                    progress,
                    _cts.Token,
                    _pauseTokenSource.Token).ConfigureAwait(false);
            }).ConfigureAwait(true);

            DuplicateFilesCount = result.TotalDuplicateCopies;
            ReclaimableSpaceFormatted = FormatBytes(result.TotalReclaimableBytes);
            CrossDestinationRedundantCount = result.CrossDestinationRedundantFileCount;
            HasDuplicates = result.TotalDuplicateCopies > 0;
            HasDuplicateGroups = result.Groups.Count > 0;
            DisplayedScanMode = FormatScanMode(SelectedMode);

            List<DuplicateGroupViewModel> newDuplicateGroups = new(result.Groups.Count);
            foreach (DuplicateGroup group in result.Groups)
            {
                newDuplicateGroups.Add(new DuplicateGroupViewModel(group));
            }
            AllDuplicateGroups.ReplaceAll(newDuplicateGroups);

            ActiveTab = "Duplicates";
            ApplyFilter();

            StatusMessage = $"Duplicate scan complete: {result.TotalDuplicateCopies} duplicates found. {ReclaimableSpaceFormatted} reclaimable.";
            StateBadgeText = "FINISHED";
            StateBadgeBackground = "#064E3B";
            StateBadgeForeground = "#10B981";
            CurrentProgressPhase = StatusMessage;
            BatchProgressPercentage = 100;
            BatchProgressSummaryText = $"{result.Groups.Count} duplicate groups found";

            _logger.Information(
                "Duplicate scan completed: {TotalDuplicates} duplicates across {GroupCount} groups, {Reclaimable} reclaimable",
                result.TotalDuplicateCopies,
                result.Groups.Count,
                ReclaimableSpaceFormatted);
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Duplicate scan cancelled by user for source {SourcePath}", SourcePath);
            StatusMessage = "Duplicate scan cancelled by user.";
            CurrentProgressPhase = "Cancelled.";
            StateBadgeText = "CANCELLED";
            StateBadgeBackground = "#7F1D1D";
            StateBadgeForeground = "#F87171";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Duplicate scan failed for source {SourcePath}", SourcePath);
            StatusMessage = $"Duplicate scan error: {ex.Message}";
            StateBadgeText = "ERROR";
            StateBadgeBackground = "#7F1D1D";
            StateBadgeForeground = "#F87171";
        }
        finally
        {
            _elapsedTimer.Stop();
            _operationStopwatch.Stop();
            DurationFormatted = $"{_operationStopwatch.Elapsed.TotalSeconds:F1}s";
            IsRunning = false;
            IsOperationActive = false;
            IsBatchIndeterminate = false;
            IsPausing = false;
            IsPaused = false;
            IsCancelling = false;
            ShowCancelConfirmation = false;
            CanPauseResume = false;
            PauseResumeButtonText = "⏸ PAUSE";
            PauseResumeButtonBackground = "#334155";
        }
    }

    [RelayCommand]
    private async Task CopyMissingAsync()
    {
        if (_lastSummary is null || _lastResults.Count == 0 || Destinations.Count == 0)
        {
            return;
        }

        List<BackupDestinationViewModel> activeDestinations = Destinations.Where(d => d.IsEnabled).ToList();
        if (activeDestinations.Count == 0)
        {
            StatusMessage = "No active backup destinations enabled to copy to.";
            return;
        }

        foreach (BackupDestinationViewModel dest in activeDestinations)
        {
            if (!TryValidateSourceAndDestinationPaths(SourcePath, dest.RootPath, out string validationError))
            {
                StatusMessage = $"Cannot copy to '{dest.Name}': {validationError}";
                return;
            }
        }

        List<MediaFile> missingFiles = _lastResults
            .Where(r => !r.IsFullyVerified)
            .Select(r => r.SourceFile)
            .ToList();

        if (missingFiles.Count == 0)
        {
            StatusMessage = "No missing files to copy.";
            return;
        }

        long requiredBytes = missingFiles.Sum(f => f.FileLength);
        foreach (BackupDestinationViewModel dest in activeDestinations)
        {
            dest.RefreshCapacity();
            dest.CheckRequiredSpace(requiredBytes);
        }

        BackupDestinationViewModel? overflowDest = activeDestinations.FirstOrDefault(d => d.HasCapacityWarning);
        if (overflowDest is not null)
        {
            StatusMessage = $"Warning: '{overflowDest.Name}' has low space ({overflowDest.CapacityWarningText}).";
        }

        IsCopying = true;
        IsOperationActive = true;
        IsBatchIndeterminate = false;
        IsPausing = false;
        IsPaused = false;
        IsCancelling = false;
        ShowCancelConfirmation = false;
        CanPauseResume = true;
        OperationTitle = "BACKUP COPY TRANSFER";
        OperationAccentColor = "#F59E0B";
        StateBadgeText = "RUNNING";
        StateBadgeBackground = "#064E3B";
        StateBadgeForeground = "#10B981";
        PauseResumeButtonText = "⏸ PAUSE";
        PauseResumeButtonBackground = "#D97706";
        BatchProgressPercentage = 0;
        BatchProgressSummaryText = "0% (0 / 0)";
        CurrentFileProgressPercentage = 0;
        ThroughputText = "0.0 MB/s";
        EtaText = "--:--";
        FilesCounterText = $"0 / {missingFiles.Count}";
        CurrentProgressPhase = "Starting copy transfer...";
        CurrentFileDetailText = "Preparing file streams...";

        _cts = new CancellationTokenSource();
        _pauseTokenSource = new PauseTokenSource();
        _operationStopwatch.Restart();
        _elapsedTimer.Start();

        _logger.Information(
            "Starting copy of {MissingCount} missing files across {DestCount} destinations",
            missingFiles.Count,
            activeDestinations.Count);

        List<string> targetPaths = activeDestinations.Select(d => d.RootPath).ToList();
        bool copySucceeded = false;
        try
        {
            Progress<CopyProgressInfo> copyProgress = new(p =>
            {
                if (p.IsPaused)
                {
                    IsPausing = false;
                    IsPaused = true;
                    CanPauseResume = true;
                    PauseResumeButtonText = "▶ RESUME";
                    PauseResumeButtonBackground = "#059669";
                    StateBadgeText = "PAUSED";
                    StateBadgeBackground = "#78350F";
                    StateBadgeForeground = "#FBBF24";
                    StatusMessage = "Copy paused at file boundary.";
                }
                else if (!IsPausing && !IsCancelling)
                {
                    StateBadgeText = "RUNNING";
                    StateBadgeBackground = "#064E3B";
                    StateBadgeForeground = "#10B981";
                }

                CurrentProgressPhase = $"Copying: {p.CurrentFileName}";
                CurrentFileDetailText = p.CurrentFileName;
                CurrentFileLabelText = Path.GetFileName(p.CurrentFileName);
                CopyProgressText = $"Copying ({p.FilesCompleted}/{p.TotalFiles}): {p.CurrentFileName}";

                if (p.TotalBytes > 0)
                {
                    BatchProgressPercentage = (p.CopiedBytes / (double)p.TotalBytes) * 100.0;
                    CopyProgressPercentage = BatchProgressPercentage;
                    BatchProgressSummaryText = $"{BatchProgressPercentage:F0}% ({FormatBytes(p.CopiedBytes)} / {FormatBytes(p.TotalBytes)})";
                }

                if (p.CurrentFileBytes > 0)
                {
                    CurrentFileProgressPercentage = Math.Min(100.0, (p.CurrentFileCopiedBytes / (double)p.CurrentFileBytes) * 100.0);
                    CurrentFileProgressText = $"{FormatBytes(p.CurrentFileCopiedBytes)} / {FormatBytes(p.CurrentFileBytes)}";
                }
                else
                {
                    CurrentFileProgressPercentage = 0;
                    CurrentFileProgressText = string.Empty;
                }

                FilesCounterText = $"{p.FilesCompleted} / {p.TotalFiles}";
                CopySpeedText = $"{p.MegaBytesPerSecond:F1} MB/s";
                ThroughputText = CopySpeedText;

                CopyEtaText = $"ETA: {p.EstimatedTimeRemaining:mm\\:ss}";
                EtaText = $"{p.EstimatedTimeRemaining:mm\\:ss}";
            });

            int copiedCount = await Task.Run(() => _mediaCopier.CopyMissingFilesAsync(
                missingFiles,
                targetPaths,
                copyProgress,
                _cts.Token,
                _pauseTokenSource.Token)).ConfigureAwait(true);

            copySucceeded = true;
            StatusMessage = $"Copied and verified {copiedCount} files. Re-running verification...";

            _logger.Information(
                "Successfully copied and verified {CopiedCount} missing files",
                copiedCount);
        }
        catch (OperationCanceledException)
        {
            _logger.Information("File copy cancelled by user for {FileCount} missing files", missingFiles.Count);
            StatusMessage = "File copy cancelled by user.";
            CurrentProgressPhase = "Cancelled.";
            StateBadgeText = "CANCELLED";
            StateBadgeBackground = "#7F1D1D";
            StateBadgeForeground = "#F87171";
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "Failed to copy missing files to destinations: {Destinations}",
                string.Join(", ", targetPaths));
            StatusMessage = $"Copy error: {ex.Message}";
            StateBadgeText = "ERROR";
            StateBadgeBackground = "#7F1D1D";
            StateBadgeForeground = "#F87171";
        }
        finally
        {
            _elapsedTimer.Stop();
            _operationStopwatch.Stop();
            IsCopying = false;
            IsOperationActive = false;
            IsPausing = false;
            IsPaused = false;
            IsCancelling = false;
            ShowCancelConfirmation = false;
            CanPauseResume = false;
            PauseResumeButtonText = "⏸ PAUSE";
            PauseResumeButtonBackground = "#334155";
        }

        if (copySucceeded)
        {
            await VerifyAsync();
        }
    }

    [ObservableProperty]
    private string? _lastExportedReportPath;

    public bool HasExportedReport => !string.IsNullOrEmpty(LastExportedReportPath);

    partial void OnLastExportedReportPathChanged(string? value) => OnPropertyChanged(nameof(HasExportedReport));

    [RelayCommand]
    private void OpenLastReport()
    {
        if (!string.IsNullOrEmpty(LastExportedReportPath) && File.Exists(LastExportedReportPath))
        {
            try
            {
                FileOpener.OpenFile(LastExportedReportPath);
                _logger.Information("Opened exported audit report: {Path}", LastExportedReportPath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to open exported report {Path}", LastExportedReportPath);
                StatusMessage = $"Could not open report: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private async Task ExportReportAsync(string format)
    {
        if (_lastSummary is null || _lastResults.Count == 0)
        {
            StatusMessage = "No verification results available to export.";
            return;
        }

        IReportGenerator generator = format.ToLowerInvariant() switch
        {
            "csv" => new CsvReportGenerator(),
            "json" => new JsonReportGenerator(),
            _ => new HtmlReportGenerator()
        };

        string? outputPath = null;
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.StorageProvider is { } storageProvider)
        {
            FilePickerSaveOptions options = new()
            {
                Title = $"Save {generator.DisplayName}",
                DefaultExtension = generator.FileExtension.TrimStart('.'),
                SuggestedFileName = $"ParityProof_Audit_{DateTime.UtcNow:yyyyMMdd_HHmm}",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new(generator.DisplayName)
                    {
                        Patterns = new List<string> { $"*{generator.FileExtension}" }
                    }
                }
            };

            IStorageFile? targetFile = await storageProvider.SaveFilePickerAsync(options);
            if (targetFile is null)
            {
                return;
            }

            outputPath = targetFile.Path.LocalPath;
        }

        if (string.IsNullOrEmpty(outputPath))
        {
            string docsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(docsDir) || !Directory.Exists(docsDir))
            {
                docsDir = Path.GetTempPath();
            }
            string fileName = $"ParityProof_Audit_{DateTime.UtcNow:yyyyMMdd_HHmm}{generator.FileExtension}";
            outputPath = Path.Combine(docsDir, fileName);
        }

        try
        {
            await generator.GenerateReportAsync(_lastSummary, _lastResults, outputPath);
            LastExportedReportPath = outputPath;
            StatusMessage = $"Audit report exported to: {outputPath}";
            _logger.Information("Audit report exported successfully to {OutputPath}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export audit report to {OutputPath}", outputPath);
            StatusMessage = $"Export error: {ex.Message}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024 * 1024)):F2} GB",
            >= 1024L * 1024 => $"{(bytes / (1024.0 * 1024)):F1} MB",
            >= 1024L => $"{(bytes / 1024.0):F0} KB",
            _ => $"{bytes} B"
        };
    }

    public static string FormatScanMode(VerificationMode mode) => mode switch
    {
        VerificationMode.SuperFast => "Super-Fast",
        VerificationMode.Quick => "Quick",
        VerificationMode.Deep => "Deep Probe",
        VerificationMode.Full => "Full",
        _ => mode.ToString()
    };

    public void Dispose()
    {
        _searchDebounceTimer?.Stop();
        _elapsedTimer.Stop();
        _driveDetector.DriveChanged -= OnDriveChanged;
        _driveDetector.Dispose();
        _cts?.Dispose();
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        InspectorThumbnail?.Dispose();
    }
}
