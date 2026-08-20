namespace Marco.Core.Threading;

/// <summary>
/// Await a task, but stop waiting the moment the token fires. The abandoned task keeps running detached —
/// blocking work (a WMI connect, a drain of in-flight hosts) cannot be interrupted, but nothing needs to sit
/// waiting for it either. The abandoned task's exception is always observed, and an optional callback receives
/// its late result (e.g. to dispose a connection nobody will own) or signals that the drain settled.
/// </summary>
public static class AbandonableTask
{
    /// <summary>For run-level drains. <paramref name="onDrained"/> fires (on a pool thread) when the abandoned
    /// task eventually settles, success or fault. Throws <see cref="OperationCanceledException"/> on abandon;
    /// a task that already completed (even cancelled/faulted) propagates its own outcome instead.</summary>
    public static async Task AwaitOrAbandonAsync(Task task, CancellationToken ct, Action? onDrained = null)
    {
        try
        {
            await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                t => { _ = t.Exception; onDrained?.Invoke(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>For operations producing a resource. If the result arrives after abandonment,
    /// <paramref name="onAbandonedResult"/> disposes/releases it so a connect that succeeds after a Stop
    /// leaks nothing.</summary>
    public static async Task<T> AwaitOrAbandonAsync<T>(
        Task<T> task, CancellationToken ct, Action<T>? onAbandonedResult = null)
    {
        try
        {
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully) onAbandonedResult?.Invoke(t.Result);
                    else _ = t.Exception; // observe so an abandoned fault never surfaces as unobserved
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}
