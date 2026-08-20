using Marco.Core.Scanning;
using Xunit;

namespace Marco.Tests;

public class PauseControllerTests
{
    [Fact]
    public void PausedTime_StartsAtZero()
        => Assert.Equal(TimeSpan.Zero, new PauseController().PausedTime);

    [Fact]
    public void Resume_WhenNeverPaused_IsANoOp()
    {
        var pause = new PauseController();
        pause.Resume();
        Assert.Equal(TimeSpan.Zero, pause.PausedTime);
        Assert.False(pause.IsPaused);
    }

    [Fact]
    public async Task PausedTime_AccumulatesWhilePaused_AndStopsOnResume()
    {
        var pause = new PauseController();

        pause.Pause();
        await Task.Delay(100);
        pause.Resume();
        var afterFirst = pause.PausedTime;
        Assert.True(afterFirst >= TimeSpan.FromMilliseconds(80), $"expected >=80ms, was {afterFirst}");

        // Not growing while resumed.
        await Task.Delay(100);
        Assert.Equal(afterFirst, pause.PausedTime);

        // A second cycle accumulates on top.
        pause.Pause();
        await Task.Delay(100);
        pause.Resume();
        Assert.True(pause.PausedTime >= afterFirst + TimeSpan.FromMilliseconds(80),
            $"expected >= {afterFirst + TimeSpan.FromMilliseconds(80)}, was {pause.PausedTime}");
    }

    [Fact]
    public async Task PausedTime_StopsEvenWhenCancellationOpenedTheGateFirst()
    {
        // Cancel-while-paused: a worker's ct registration completes the gate before the owner's Resume().
        var pause = new PauseController();
        using var cts = new CancellationTokenSource();

        pause.Pause();
        var waiter = pause.WaitWhilePausedAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        pause.Resume(); // the owner's follow-up, as MainViewModel.StopScan does
        var settled = pause.PausedTime;
        await Task.Delay(100);
        Assert.Equal(settled, pause.PausedTime);
    }

    [Fact]
    public void DoublePause_DoesNotReset()
    {
        var pause = new PauseController();
        pause.Pause();
        pause.Pause(); // second call must be a no-op, not a stopwatch restart
        Assert.True(pause.IsPaused);
        pause.Resume();
        Assert.False(pause.IsPaused);
    }
}
