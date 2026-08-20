using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marco.Export;

/// <summary>Writes/reads the full relational scan structure as JSON, optionally gzip-compressed (scan history
/// keeps every run, and software-heavy documents compress roughly 10:1). <see cref="Load"/> sniffs the gzip
/// magic bytes rather than trusting the extension, so a renamed file still opens.</summary>
public sealed class JsonExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Export(ScanDocument document, string path, bool compress = false)
    {
        if (!compress)
        {
            File.WriteAllText(path, Serialize(document), new UTF8Encoding(false));
            return;
        }
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        JsonSerializer.Serialize(gzip, document, Options);
    }

    public string Serialize(ScanDocument document) => JsonSerializer.Serialize(document, Options);

    public ScanDocument Load(string path)
    {
        using var stream = OpenPossiblyCompressed(path);
        return JsonSerializer.Deserialize<ScanDocument>(stream, Options)
               ?? throw new InvalidDataException("The scan document was empty or malformed.");
    }

    public ScanDocument Deserialize(string json)
        => JsonSerializer.Deserialize<ScanDocument>(json, Options)
           ?? throw new InvalidDataException("The scan document was empty or malformed.");

    /// <summary>Open a scan file for reading, transparently decompressing when the gzip magic (1F 8B) leads.</summary>
    internal static Stream OpenPossiblyCompressed(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            int b0 = file.ReadByte(), b1 = file.ReadByte();
            file.Position = 0;
            return b0 == 0x1F && b1 == 0x8B ? new GZipStream(file, CompressionMode.Decompress) : file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }
}
