using System;
using System.Threading;
using System.Threading.Tasks;
using ParityProof.Core.Threading;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class PauseTokenTests
{
    [Fact]
    public void PauseToken_InitialState_IsNotPaused()
    {
        PauseTokenSource source = new();
        PauseToken token = source.Token;

        Assert.False(source.IsPaused);
        Assert.False(token.IsPaused);
    }

    [Fact]
    public void PauseToken_PauseAndResume_UpdatesStateCorrectly()
    {
        PauseTokenSource source = new();
        PauseToken token = source.Token;

        source.Pause();
        Assert.True(source.IsPaused);
        Assert.True(token.IsPaused);

        source.Resume();
        Assert.False(source.IsPaused);
        Assert.False(token.IsPaused);
    }

    [Fact]
    public async Task WaitWhilePausedAsync_WhenNotPaused_CompletesImmediately()
    {
        PauseTokenSource source = new();
        PauseToken token = source.Token;

        ValueTask task = token.WaitWhilePausedAsync(CancellationToken.None);
        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public async Task WaitWhilePausedAsync_WhenPaused_WaitsUntilResumed()
    {
        PauseTokenSource source = new();
        PauseToken token = source.Token;

        source.Pause();

        Task waitTask = Task.Run(async () =>
        {
            await token.WaitWhilePausedAsync(CancellationToken.None);
        });

        // Ensure waitTask is still waiting
        await Task.Delay(50);
        Assert.False(waitTask.IsCompleted);

        source.Resume();
        await waitTask;
        Assert.True(waitTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitWhilePausedAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        PauseTokenSource source = new();
        PauseToken token = source.Token;

        source.Pause();

        using CancellationTokenSource cts = new();
        Task waitTask = Task.Run(async () =>
        {
            await token.WaitWhilePausedAsync(cts.Token);
        });

        await Task.Delay(50);
        Assert.False(waitTask.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);
    }
}
