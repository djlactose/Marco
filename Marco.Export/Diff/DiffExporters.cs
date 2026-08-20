using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marco.Export.Diff;

/// <summary>Writes a <see cref="ScanDiffResult"/> as JSON (full structure) or CSV (one flat row per change,
/// with the added/removed machines as pseudo-rows) — same encoding conventions as the scan exporters.</summary>
public static class DiffExporters
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void ExportJson(ScanDiffResult diff, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(diff, JsonOptions), new UTF8Encoding(false));

    public static void ExportCsv(ScanDiffResult diff, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Machine,Address,MatchedBy,Category,Item,Change,Old,New,Severity");
        foreach (var m in diff.Added)
            AppendRow(sb, m.DisplayName, m.Address, "", "Identity", "Machine", "Added", "", m.Name ?? m.Address, "Notable");
        foreach (var m in diff.Removed)
            AppendRow(sb, m.DisplayName, m.Address, "", "Identity", "Machine", "Removed", m.Name ?? m.Address, "", "Notable");
        foreach (var machine in diff.Changed)
            foreach (var c in machine.Changes)
                AppendRow(sb, machine.Machine.DisplayName, machine.Machine.Address,
                    machine.Machine.MatchedBy?.ToString() ?? "", c.Category.ToString(), c.Item,
                    c.Kind.ToString(), c.OldValue ?? "", c.NewValue ?? "", c.Severity.ToString());
        // BOM so Excel opens it as UTF-8, matching CsvExporter.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static void AppendRow(StringBuilder sb, params string[] fields)
        => sb.AppendLine(string.Join(",", fields.Select(Quote)));

    private static string Quote(string field)
        => field.Contains(',') || field.Contains('"') || field.Contains('\n')
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
}
