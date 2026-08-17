using Marco.Core.Storage;

namespace Marco.Core.Update;

/// <summary>
/// Cross-process gate around the steps that touch the running exe or the crash-loop sentinel (startup rollback
/// check, staged-update swap, restart-to-apply). Several Marco windows may run at once; only these short,
/// synchronous critical sections need to be serialised — never the app's lifetime. Keyed by the data root because
/// that is the resource being protected (the updates directory and the exe beside it): two portable copies in
/// different folders don't serialise each other, while the %LOCALAPPDATA% fallback shares one root and one gate.
/// A named Mutex is thread-affine: acquire and dispose on the same thread, and never hold it across an await.
/// </summary>
public sealed class UpdateGate : IDisposable
{
    private readonly Mutex? _mutex;
    private bool _released;

    private UpdateGate(Mutex? mutex) => _mutex = mutex;

    public static string NameFor(string dataRoot) => "Marco.UpdateGate." + PathKey.For(dataRoot);

    /// <summary>True when the mutex was actually taken; false for the no-op gate handed out when the OS refused to
    /// create the mutex (in which case the caller proceeds unprotected — better than never updating).</summary>
    public bool IsHeld => _mutex is not null;

    /// <summary>Returns the held gate, or null if another instance kept it for longer than <paramref name="timeout"/>.
    /// An abandoned mutex (previous holder died) counts as acquired.</summary>
    public static UpdateGate? TryAcquire(string dataRoot, TimeSpan timeout)
    {
        Mutex mutex;
        try { mutex = new Mutex(initiallyOwned: false, NameFor(dataRoot)); }
        catch { return new UpdateGate(null); }

        bool owned;
        try { owned = mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { owned = true; }
        catch { mutex.Dispose(); return new UpdateGate(null); }

        if (!owned)
        {
            mutex.Dispose();
            return null;
        }
        return new UpdateGate(mutex);
    }

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); } catch { /* released on another thread or already released — nothing to do */ }
        _mutex.Dispose();
    }
}
