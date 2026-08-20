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
    private readonly System.Diagnostics.Stopwatch _pausedTime = new();

    public bool IsPaused => !_tcs.Task.IsCompleted;

    /// <summary>Total wall-clock time this run has spent paused. The ETA rate math subtracts it from elapsed
    /// so a long pause doesn't inflate the per-item cost (hosts already in flight when Pause hits keep
    /// finishing "on paused time" — accepted imprecision).</summary>
    public TimeSpan PausedTime => _pausedTime.Elapsed;

    public void Pause()
    {
        // Only replace an already-open gate with a fresh, incomplete one.
        while (true)
        {
            var current = _tcs;
            if (!current.Task.IsCompleted) return; // already paused
            var next = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _tcs, next, current) == current)
            {
                _pausedTime.Start();
                return;
            }
        }
    }

    public void Resume()
    {
        _tcs.TrySetResult(true);
        // IsRunning guard keeps this idempotent, and correct when a worker's ct registration opened the
        // gate first (cancel-while-paused) before the owner's Resume() lands.
        if (_pausedTime.IsRunning) _pausedTime.Stop();
    }

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
