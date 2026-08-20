using System.Text;
using System.Text.Json;
using Marco.Core.Storage;

namespace Marco.Core.Diagnostics;

/// <summary>
/// Append-only, thread-safe run log (JSON lines) providing an access-attribution trail: what was scanned, when,
/// by whom, and with what outcome. It records targets, timestamps, the operator, and success/failure — and
/// **never** credential material. This matters in regulated environments.
/// Several Marco windows may share one log file: every entry carries the writing process id, and appends are
/// serialised across processes with a named mutex keyed by the file path (.NET's FileMode.Append issues positional
/// writes, so two processes that open at the same length would otherwise overwrite each other's line).
/// </summary>
public sealed class RunLog
{
    private static readonly int Pid = Environment.ProcessId;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly string _operator;
    private readonly Mutex? _fileMutex;

    public RunLog(string path, string? operatorName = null)
    {
        _path = path;
        _operator = operatorName ?? Environment.UserName;
        try { _fileMutex = new Mutex(initiallyOwned: false, "Marco.RunLog." + PathKey.For(path)); }
        catch { _fileMutex = null; } // best effort without the cross-process lock
    }

    public void ScanStarted(IReadOnlyList<string> ranges, int targetCount)
        => Write("scan_started", new { ranges, targetCount });

    public void ScanFinished(int alive, int unreachable, double seconds)
        => Write("scan_finished", new { alive, unreachable, seconds });

    public void ScanCancelled(int alive, int unreachable, int completed, int total, double seconds)
        => Write("scan_cancelled", new { alive, unreachable, completed, total, seconds });

    public void InventoryAttempt(string host, bool authenticated, string status, string? credentialLabel)
        => Write("inventory", new { host, authenticated, status, credentialLabel });

    public void Note(string message) => Write("note", new { message });

    /// <summary>Auto-update lifecycle: check_started, up_to_date, available, staged, stage_deferred, partial_reused,
    /// sha_mismatch, stage_failed, check_failed, applied, apply_failed, gate_busy, rollback, notify_only, cleanup.</summary>
    public void UpdateEvent(string stage, string detail) => Write("update", new { stage, detail });

    /// <summary>Prerequisite-doctor rollup after an inventory run: cause name → affected host count.
    /// Counts only — no hostnames, to keep the log small and unrevealing.</summary>
    public void Doctor(IReadOnlyDictionary<string, int> causeCounts) => Write("doctor", new { causes = causeCounts });

    /// <summary>Baseline lifecycle: blessed / trusted / evaluated, with counts only.</summary>
    public void Baseline(string action, int known, int unknown) => Write("baseline", new { action, known, unknown });

    /// <summary>Unhandled exception, from the dispatcher, appdomain, or task scheduler.</summary>
    public void Crash(string source, string detail) => Write("crash", new { source, detail });

    private void Write(string @event, object data)
    {
        var entry = new
        {
            ts = DateTime.Now.ToString("o"),
            op = _operator,
            pid = Pid,
            @event,
            data,
        };
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(entry) + Environment.NewLine);
        lock (_lock)
        {
            bool owned = false;
            try
            {
                owned = AcquireFileMutex();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        stream.Write(bytes, 0, bytes.Length);
                        return;
                    }
                    catch (IOException) when (attempt < 2)
                    {
                        Thread.Sleep(20 * (attempt + 1)); // another writer / AV scanner has the file; retry briefly
                    }
                }
            }
            catch { /* logging must never break a scan */ }
            finally
            {
                if (owned) { try { _fileMutex!.ReleaseMutex(); } catch { } }
            }
        }
    }

    private bool AcquireFileMutex()
    {
        if (_fileMutex is null) return false;
        try { return _fileMutex.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { return true; } // previous holder died mid-write; we own it now
        catch { return false; }
    }
}
