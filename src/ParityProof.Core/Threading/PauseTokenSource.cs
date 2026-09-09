using System;
using System.Threading;
using System.Threading.Tasks;

namespace ParityProof.Core.Threading;

public sealed class PauseTokenSource
{
    private volatile TaskCompletionSource<bool>? _pausedTcs;

    public bool IsPaused => _pausedTcs is not null;

    public PauseToken Token => new(this);

    public void Pause()
    {
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.CompareExchange(ref _pausedTcs, tcs, null);
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? tcs = Interlocked.Exchange(ref _pausedTcs, null);
        tcs?.TrySetResult(true);
    }

    internal async ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool>? tcs = _pausedTcs;
        if (tcs is not null)
        {
            await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

public readonly struct PauseToken
{
    private readonly PauseTokenSource? _source;

    public PauseToken(PauseTokenSource source)
    {
        _source = source;
    }

    public bool IsPaused => _source?.IsPaused ?? false;

    public ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken = default)
    {
        return _source?.WaitWhilePausedAsync(cancellationToken) ?? ValueTask.CompletedTask;
    }
}
