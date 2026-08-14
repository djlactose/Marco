namespace Marco.Core.Scanning;

/// <summary>
/// A resettable async gate for pause/resume. Workers await <see cref="WaitWhilePausedAsync"/> at the top of
/// each host job; when paused the returned task doesn't complete until <see cref="Resume"/> is called. Starts
/// in the resumed (open) state.
/// </summary>
public sealed class PauseController
{
    private volatile TaskCompletionSource<bool> _tcs =
        CreateCompleted();

    public bool IsPaused => !_tcs.Task.IsCompleted;

    public void Pause()
    {
        // Only replace an already-open gate with a fresh, incomplete one.
        while (true)
        {
            var current = _tcs;
            if (!current.Task.IsCompleted) return; // already paused
            var next = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _tcs, next, current) == current) return;
        }
    }

    public void Resume() => _tcs.TrySetResult(true);

    public async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        var gate = _tcs;
        if (gate.Task.IsCompleted) return;
        using (ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), gate))
            await gate.Task.ConfigureAwait(false);
    }

    private static TaskCompletionSource<bool> CreateCompleted()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        return tcs;
    }
}
