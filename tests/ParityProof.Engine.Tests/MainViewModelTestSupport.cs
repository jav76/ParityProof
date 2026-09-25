using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.App.ViewModels;
using ParityProof.Core.Enums;
using ParityProof.Core.Interfaces;
using ParityProof.Core.Models;
using ParityProof.Core.Threading;
using ParityProof.Engine.Matching;

namespace ParityProof.Engine.Tests;

/// <summary>
/// Runs a test body on a single pumping thread so Progress callbacks and await continuations are
/// delivered in order, the same way the Avalonia UI thread delivers them in the app.
/// </summary>
internal static class UiThreadSimulator
{
    public static void Run(Func<Task> testBody)
    {
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        PumpingSynchronizationContext context = new();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            Task bodyTask = testBody();
            bodyTask.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.RunUntilComplete();
            bodyTask.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private sealed class PumpingSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // The test body already finished; late callbacks have nothing left to update.
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            throw new NotSupportedException("Synchronous dispatch is not supported by the test UI thread.");
        }

        public override SynchronizationContext CreateCopy() => this;

        public void Complete() => _queue.CompleteAdding();

        public void RunUntilComplete()
        {
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }
}

/// <summary>
/// Lets a test hold an operation open to cancel it or fail it deterministically instead of racing a fast
/// run over a handful of files.
/// </summary>
internal sealed class OperationGate
{
    private TaskCompletionSource? _pending;

    public Exception? FailureToThrow { get; set; }

    public void Close()
    {
        _pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Open()
    {
        TaskCompletionSource? pending = _pending;
        _pending = null;
        pending?.TrySetResult();
    }

    public async Task PassAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? pending = _pending;
        if (pending is not null)
        {
            await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (FailureToThrow is not null)
        {
            throw FailureToThrow;
        }
    }
}

/// <summary>
/// Real verifier behind an <see cref="OperationGate"/>.
/// </summary>
internal sealed class GatedVerificationEngine : IVerificationEngine
{
    private readonly MultiDestinationVerifier _inner;

    public GatedVerificationEngine(IDuplicateAnalyzer duplicateAnalyzer)
    {
        _inner = new MultiDestinationVerifier(cache: null, duplicateAnalyzer: duplicateAnalyzer);
    }

    public OperationGate Gate { get; } = new();

    public async Task<(VerificationSummary Summary, IReadOnlyList<VerificationResultItem> Results)> VerifyAsync(
        string sourcePath,
        IReadOnlyList<BackupDestination> destinations,
        VerificationMode mode,
        FilterPreset filterPreset,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default,
        bool scanDuplicates = false)
    {
        await Gate.PassAsync(cancellationToken).ConfigureAwait(false);

        return await _inner.VerifyAsync(
            sourcePath,
            destinations,
            mode,
            filterPreset,
            progress,
            cancellationToken,
            pauseToken,
            scanDuplicates).ConfigureAwait(false);
    }
}

/// <summary>
/// Real duplicate analyzer behind an <see cref="OperationGate"/>, for holding a duplicate audit open. The
/// harness uses it for the standalone audit and for "scan for duplicates during verification".
/// <see cref="Entered"/> completes with the caller's progress sink when the analyzer is first called, before
/// the gate.
/// </summary>
internal sealed class GatedDuplicateAnalyzer : IDuplicateAnalyzer
{
    private readonly IDuplicateAnalyzer _inner;
    private readonly TaskCompletionSource<IProgress<VerificationProgress>?> _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GatedDuplicateAnalyzer(IDuplicateAnalyzer inner)
    {
        _inner = inner;
    }

    public OperationGate Gate { get; } = new();

    public Task<IProgress<VerificationProgress>?> Entered => _entered.Task;

    public async Task<DuplicateAnalysisResult> AnalyzeDuplicatesAsync(
        IReadOnlyList<MediaFile> sourceFiles,
        IReadOnlyList<BackupDestination> destinations,
        IReadOnlyDictionary<string, IReadOnlyList<MediaFile>> destinationFiles,
        VerificationMode mode,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        PauseToken pauseToken = default)
    {
        _entered.TrySetResult(progress);
        await Gate.PassAsync(cancellationToken).ConfigureAwait(false);

        return await _inner.AnalyzeDuplicatesAsync(
            sourceFiles,
            destinations,
            destinationFiles,
            mode,
            progress,
            cancellationToken,
            pauseToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Temp card and backup directories plus a MainViewModel wired to a gated real verifier and a gated real
/// duplicate analyzer.
/// </summary>
internal sealed class VerdictTestHarness : IDisposable
{
    public const string SAFE_BADGE_PREFIX = "SAFE TO FORMAT";
    public const string UNSAFE_BADGE_PREFIX = "UNSAFE TO FORMAT";
    public const string INPUTS_CHANGED_BADGE = "INPUTS CHANGED - RE-VERIFY BEFORE FORMATTING";
    public const string COLOR_SAFE = "#10B981";

    private const int PHOTO_SIZE_BYTES = 64 * 1024;

    private readonly Dictionary<string, byte[]> _photoContents = new(StringComparer.Ordinal);

    public VerdictTestHarness()
    {
        RootDir = Path.Combine(Path.GetTempPath(), "ParityProof_VerdictState_" + Guid.NewGuid().ToString("N"));
        CardDir = Path.Combine(RootDir, "EOS_DIGITAL");
        BackupDir = Path.Combine(RootDir, "ssd");
        Directory.CreateDirectory(CardDir);
        Directory.CreateDirectory(BackupDir);

        DuplicateAudit = new GatedDuplicateAnalyzer(new DuplicateAnalyzer());
        Engine = new GatedVerificationEngine(DuplicateAudit);
        ViewModel = new MainViewModel(Engine, DuplicateAudit);
    }

    public string RootDir { get; }

    public string CardDir { get; }

    public string BackupDir { get; }

    public GatedVerificationEngine Engine { get; }

    public GatedDuplicateAnalyzer DuplicateAudit { get; }

    public MainViewModel ViewModel { get; }

    public string CreateDirectory(string name)
    {
        string path = Path.Combine(RootDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void WritePhoto(string directory, string fileName)
    {
        if (!_photoContents.TryGetValue(fileName, out byte[]? content))
        {
            content = new byte[PHOTO_SIZE_BYTES];
            Random.Shared.NextBytes(content);
            _photoContents[fileName] = content;
        }

        File.WriteAllBytes(Path.Combine(directory, fileName), content);
    }

    public void WriteBackedUpPhoto(string fileName)
    {
        WritePhoto(CardDir, fileName);
        WritePhoto(BackupDir, fileName);
    }

    public async Task VerifySafeCardAsync()
    {
        WriteBackedUpPhoto("IMG_0001.JPG");
        ViewModel.SourcePath = CardDir;
        ViewModel.AddDestination(BackupDir);

        await ViewModel.VerifyCommand.ExecuteAsync(null);

        Assert.StartsWith(SAFE_BADGE_PREFIX, ViewModel.SafetyBadgeText);
        Assert.Equal(COLOR_SAFE, ViewModel.SafetyBadgeColor);
        Assert.True(ViewModel.HasResults);
        Assert.True(ViewModel.ExportReportCommand.CanExecute("html"));
    }

    public void AssertVerdictCleared()
    {
        MainViewModel vm = ViewModel;
        Assert.False(vm.HasResults);
        Assert.False(vm.HasUnprotectedFiles);
        Assert.False(vm.CanStartCopy);
        Assert.DoesNotContain(SAFE_BADGE_PREFIX, vm.SafetyBadgeText);
        Assert.NotEqual(COLOR_SAFE, vm.SafetyBadgeColor);
        Assert.Null(vm.LastVerificationSummary);
        Assert.Empty(vm.AllItems);
        Assert.Empty(vm.FilteredItems);
        Assert.Equal(0, vm.TotalFiles);
        Assert.Equal(0, vm.VerifiedCount);
        Assert.False(vm.ExportReportCommand.CanExecute("html"));
        Assert.False(vm.HasExportedReport);
    }

    public void Dispose()
    {
        ViewModel.Dispose();
        try
        {
            if (Directory.Exists(RootDir))
            {
                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
