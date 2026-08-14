using System.Text;
using System.Text.Json;

namespace Marco.Core.Diagnostics;

/// <summary>
/// Append-only, thread-safe run log (JSON lines) providing an access-attribution trail: what was scanned, when,
/// by whom, and with what outcome. It records targets, timestamps, the operator, and success/failure — and
/// **never** credential material. This matters in regulated environments.
/// </summary>
public sealed class RunLog
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly string _operator;

    public RunLog(string path, string? operatorName = null)
    {
        _path = path;
        _operator = operatorName ?? Environment.UserName;
    }

    public void ScanStarted(IReadOnlyList<string> ranges, int targetCount)
        => Write("scan_started", new { ranges, targetCount });

    public void ScanFinished(int alive, int unreachable, double seconds)
        => Write("scan_finished", new { alive, unreachable, seconds });

    public void InventoryAttempt(string host, bool authenticated, string status, string? credentialLabel)
        => Write("inventory", new { host, authenticated, status, credentialLabel });

    public void Note(string message) => Write("note", new { message });

    private void Write(string @event, object data)
    {
        var entry = new
        {
            ts = DateTime.Now.ToString("o"),
            op = _operator,
            @event,
            data,
        };
        var line = JsonSerializer.Serialize(entry);
        lock (_lock)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false)); }
            catch { /* logging must never break a scan */ }
        }
    }
}
