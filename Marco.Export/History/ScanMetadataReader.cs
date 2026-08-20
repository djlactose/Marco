using System.Text.Json;

namespace Marco.Export.History;

/// <summary>Reads just the <see cref="ScanMetadata"/> head of a scan document without deserializing its machines.
/// <see cref="ScanDocument"/> serializes Metadata before Machines (positional record order), so the object sits in
/// the first few KB even of a multi-megabyte file — this is what lets the history index self-heal from a directory
/// of documents without loading each one fully.</summary>
public static class ScanMetadataReader
{
    /// <summary>Growth cap: a Metadata object bigger than this means the file is not one of ours.</summary>
    private const int MaxHeaderBytes = 1024 * 1024;

    /// <summary>Null when the file is missing, malformed, or has no leading Metadata object — never throws.</summary>
    public static ScanMetadata? TryRead(string path)
    {
        try
        {
            using var stream = JsonExporter.OpenPossiblyCompressed(path);
            var buffer = new byte[8 * 1024];
            int filled = 0;
            while (true)
            {
                if (filled == buffer.Length)
                    Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxHeaderBytes));
                int read = stream.Read(buffer, filled, buffer.Length - filled);
                filled += read;
                if (TryParse(buffer.AsSpan(0, filled), out var meta)) return meta;
                if (read == 0 || filled >= MaxHeaderBytes) return null; // whole file read (or cap hit) and still no luck
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParse(ReadOnlySpan<byte> data, out ScanMetadata? meta)
    {
        meta = null;
        // Utf8JsonReader does not skip a UTF-8 BOM (editors and PowerShell's utf8 encoding add one).
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            data = data[3..];
        try
        {
            var reader = new Utf8JsonReader(data, isFinalBlock: false, default);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName
                    && reader.CurrentDepth == 1
                    && reader.ValueTextEquals("Metadata"u8))
                {
                    if (!reader.Read()) return false;
                    meta = JsonSerializer.Deserialize<ScanMetadata>(ref reader);
                    return meta is not null;
                }
            }
            return false;
        }
        catch (JsonException)
        {
            return false; // object not fully in the buffer yet — the caller grows it and retries
        }
    }
}
