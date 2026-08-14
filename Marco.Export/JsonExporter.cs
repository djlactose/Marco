using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marco.Export;

/// <summary>Writes/reads the full relational scan structure as JSON.</summary>
public sealed class JsonExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Export(ScanDocument document, string path)
    {
        var json = Serialize(document);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    public string Serialize(ScanDocument document) => JsonSerializer.Serialize(document, Options);

    public ScanDocument Load(string path)
    {
        var json = File.ReadAllText(path);
        return Deserialize(json);
    }

    public ScanDocument Deserialize(string json)
        => JsonSerializer.Deserialize<ScanDocument>(json, Options)
           ?? throw new InvalidDataException("The scan document was empty or malformed.");
}
