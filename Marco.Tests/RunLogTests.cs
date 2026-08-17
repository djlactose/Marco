using System.Text.Json;
using Marco.Core.Diagnostics;
using Xunit;

namespace Marco.Tests;

public class RunLogTests
{
    private static string TempLog() => Path.Combine(Path.GetTempPath(), "marco-runlog-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");

    [Fact]
    public void ScanCancelled_WritesEvent_WithPid()
    {
        var path = TempLog();
        try
        {
            new RunLog(path, "tester").ScanCancelled(alive: 3, unreachable: 5, completed: 8, total: 254, seconds: 1.5);

            var line = File.ReadAllLines(path).Single();
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.Equal("scan_cancelled", root.GetProperty("event").GetString());
            Assert.Equal("tester", root.GetProperty("op").GetString());
            Assert.Equal(Environment.ProcessId, root.GetProperty("pid").GetInt32());
            var data = root.GetProperty("data");
            Assert.Equal(3, data.GetProperty("alive").GetInt32());
            Assert.Equal(254, data.GetProperty("total").GetInt32());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ConcurrentAppends_FromTwoInstances_LoseNoLines()
    {
        // Two RunLog instances on one file, written from two threads at once — the same shape as two Marco
        // windows sharing Marco.Data\logs\runlog.jsonl. Every line must survive intact and parse.
        var path = TempLog();
        try
        {
            var a = new RunLog(path, "a");
            var b = new RunLog(path, "b");
            const int perWriter = 500;

            await Task.WhenAll(
                Task.Run(() => { for (int i = 0; i < perWriter; i++) a.Note($"a-{i}"); }),
                Task.Run(() => { for (int i = 0; i < perWriter; i++) b.Note($"b-{i}"); }));

            var lines = File.ReadAllLines(path);
            Assert.Equal(perWriter * 2, lines.Length);
            var messages = new HashSet<string>();
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line); // throws on a torn/interleaved line
                Assert.Equal(Environment.ProcessId, doc.RootElement.GetProperty("pid").GetInt32());
                messages.Add(doc.RootElement.GetProperty("data").GetProperty("message").GetString()!);
            }
            Assert.Equal(perWriter * 2, messages.Count);
        }
        finally { File.Delete(path); }
    }
}
