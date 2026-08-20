using Marco.Core.Threading;
using Xunit;

namespace Marco.Tests;

public class AbandonableTaskTests
{
    [Fact]
    public async Task CompletedTask_PassesThrough()
    {
        await AbandonableTask.AwaitOrAbandonAsync(Task.CompletedTask, CancellationToken.None);
        Assert.Equal(42, await AbandonableTask.AwaitOrAbandonAsync(Task.FromResult(42), CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_ReturnsPromptly_WhileTaskStillPending()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var awaiting = AbandonableTask.AwaitOrAbandonAsync(gate.Task, cts.Token);
        cts.Cancel();

        // Must observe the cancellation without the gate ever completing.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => awaiting.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(gate.Task.IsCompleted);
        gate.SetResult(); // let the orphan settle so nothing leaks into other tests
    }

    [Fact]
    public async Task OnDrained_Fires_WhenAbandonedTaskLaterCompletes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var awaiting = AbandonableTask.AwaitOrAbandonAsync(gate.Task, cts.Token, onDrained: () => drained.SetResult());
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => awaiting);

        Assert.False(drained.Task.IsCompleted);
        gate.SetResult();
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task OnDrained_Fires_WhenAbandonedTaskLaterFaults_AndFaultIsObserved()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var awaiting = AbandonableTask.AwaitOrAbandonAsync(gate.Task, cts.Token, onDrained: () => drained.SetResult());
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => awaiting);

        gate.SetException(new InvalidOperationException("late failure"));
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(3)); // and no unobserved-exception escalation
    }

    [Fact]
    public async Task Generic_LateResult_IsHandedToCallback()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var awaiting = AbandonableTask.AwaitOrAbandonAsync(gate.Task, cts.Token,
            onAbandonedResult: r => received.SetResult(r));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => awaiting);

        gate.SetResult("session");
        Assert.Equal("session", await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task AlreadyFaultedTask_PropagatesItsOwnException_NotCancellation()
    {
        var faulted = Task.FromException(new InvalidOperationException("boom"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Task completed first → its outcome wins even though the token is signalled.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AbandonableTask.AwaitOrAbandonAsync(faulted, cts.Token));
    }
}
