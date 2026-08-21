using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Marco.Core.Hardware;

/// <summary>
/// The bundled vendor spec-sheet table (hardware-specs.json, embedded — same pattern as the OS end-of-life table)
/// plus an optional operator override file whose entries are consulted first. Pure lookups; the app installs the
/// loaded instance as <see cref="Current"/> at startup and everything else (live model, report) reads through it,
/// so a table update improves previously saved scans too — nothing from the table is persisted.
/// </summary>
public sealed class HardwareSpecTable
{
    private const string Resource = "Marco.Core.Hardware.Resources.hardware-specs.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly Lazy<HardwareSpecTable> EmbeddedInstance = new(() => new HardwareSpecTable(LoadEmbedded(), Array.Empty<HardwareSpec>(), null, null));
    private static HardwareSpecTable? _current;

    /// <summary>The embedded table alone.</summary>
    public static HardwareSpecTable Embedded => EmbeddedInstance.Value;

    /// <summary>The table in use (embedded + override). Defaults to <see cref="Embedded"/>; the app replaces it at startup.</summary>
    public static HardwareSpecTable Current { get => _current ?? Embedded; set => _current = value; }

    /// <summary>An empty table, for tests that must not depend on the bundled data.</summary>
    public static HardwareSpecTable Empty { get; } = new(new HardwareSpecTableData(1, "", Array.Empty<HardwareSpec>()), Array.Empty<HardwareSpec>(), null, null);

    private readonly HardwareSpecTableData _data;
    private readonly IReadOnlyList<HardwareSpec> _overrides;
    private readonly ConcurrentDictionary<string, HardwareSpec?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private HardwareSpecTable(HardwareSpecTableData data, IReadOnlyList<HardwareSpec> overrides, string? overridePath, string? overrideError)
    {
        _data = data;
        _overrides = overrides;
        OverridePath = overridePath;
        OverrideError = overrideError;
    }

    /// <summary>Snapshot date of the bundled table (shown in the report footer so a stale table is visible).</summary>
    public string Updated => _data.Updated;
    public int Count => _data.Specs.Count + _overrides.Count;
    public int OverrideCount => _overrides.Count;
    public string? OverridePath { get; }
    /// <summary>Why the override file was ignored, when it exists but could not be read.</summary>
    public string? OverrideError { get; }

    /// <summary>Build a table from explicit entries (tests, tooling). Entries act as overrides over an empty base.</summary>
    public static HardwareSpecTable FromSpecs(params HardwareSpec[] specs)
        => new(new HardwareSpecTableData(1, "", Array.Empty<HardwareSpec>()), specs, null, null);

    /// <summary>Embedded table plus the override file at <paramref name="overridePath"/> when it exists. A broken
    /// override never takes the table down: the error is recorded and the bundled data is used alone.</summary>
    public static HardwareSpecTable LoadWithOverride(string? overridePath)
    {
        var data = LoadEmbedded();
        if (overridePath is null || !File.Exists(overridePath)) return new HardwareSpecTable(data, Array.Empty<HardwareSpec>(), overridePath, null);
        try
        {
            using var stream = File.OpenRead(overridePath);
            var extra = JsonSerializer.Deserialize<HardwareSpecTableData>(stream, JsonOptions);
            return new HardwareSpecTable(data, extra?.Specs ?? Array.Empty<HardwareSpec>(), overridePath, null);
        }
        catch (Exception ex)
        {
            return new HardwareSpecTable(data, Array.Empty<HardwareSpec>(), overridePath, ex.Message);
        }
    }

    /// <summary>First matching entry — overrides first, then the bundled table in file order — or null.</summary>
    /// <param name="hostModuleFormFactor">Form factor the host's installed memory modules report ("DIMM"/"SODIMM"),
    /// when known — see <see cref="HardwareSpec.ModuleFormFactor"/>.</param>
    public HardwareSpec? Match(string? manufacturer, string? model, string? productVersion, string? chassisType, string? boardModel,
        string? hostModuleFormFactor = null)
    {
        var vendor = VendorKey(manufacturer);
        if (vendor is null || (string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(productVersion) && string.IsNullOrWhiteSpace(boardModel)))
            return null;
        var key = string.Join("", vendor, model, productVersion, chassisType, boardModel, hostModuleFormFactor);
        return _cache.GetOrAdd(key, _ =>
            _overrides.FirstOrDefault(s => s.Matches(vendor, model, productVersion, chassisType, boardModel, hostModuleFormFactor))
            ?? _data.Specs.FirstOrDefault(s => s.Matches(vendor, model, productVersion, chassisType, boardModel, hostModuleFormFactor)));
    }

    /// <summary>The form factor a host's modules report, for <see cref="Match"/> — the first module that says.</summary>
    public static string? ModuleFormFactorOf(IEnumerable<Marco.Core.Model.MemoryModule> modules)
        => modules.Select(m => m.FormFactor).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));

    /// <summary>Whole-word aliases (matched against the manufacturer string split on spaces and punctuation).</summary>
    private static readonly (string Word, string Key)[] VendorAliases =
    {
        ("dell", "dell"), ("alienware", "dell"),
        ("hewlett", "hp"), ("hp", "hp"), ("compaq", "hp"),
        ("lenovo", "lenovo"), ("ibm", "lenovo"),
        ("microsoft", "microsoft"),
        ("apple", "apple"),
        ("asus", "asus"), ("asustek", "asus"),
        ("acer", "acer"),
        ("gigabyte", "gigabyte"),
        ("msi", "msi"),
        ("asrock", "asrock"),
        ("supermicro", "supermicro"),
        ("fujitsu", "fujitsu"),
        ("toshiba", "toshiba"), ("dynabook", "toshiba"),
        ("samsung", "samsung"),
        ("intel", "intel"),
        ("vmware", "vmware"), ("qemu", "qemu"), ("xen", "xen"), ("innotek", "virtualbox"), ("parallels", "parallels"),
    };

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
        { "inc", "inc.", "corp", "corp.", "corporation", "co", "co.", "ltd", "ltd.", "limited", "gmbh", "technology", "computer", "computers" };

    /// <summary>"Dell Inc." → "dell", "Hewlett-Packard"/"HP Inc." → "hp", "LENOVO" → "lenovo"; null when unknown/blank.</summary>
    public static string? VendorKey(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return null;
        var words = manufacturer.Trim().ToLowerInvariant()
            .Split(new[] { ' ', ',', '/', '(', ')', '-' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var (word, key) in VendorAliases)
            if (words.Contains(word, StringComparer.Ordinal)) return key;
        if (words.Contains("super", StringComparer.Ordinal) && words.Contains("micro", StringComparer.Ordinal)) return "supermicro";
        if (words.Contains("micro", StringComparer.Ordinal) && words.Contains("star", StringComparer.Ordinal)) return "msi";
        // Fall back to the first meaningful word so an override file can target any vendor.
        return words.FirstOrDefault(w => !Noise.Contains(w));
    }

    /// <summary>Wildcard pattern → anchored, case-insensitive regex. '*' any run, '?' one char, '|' alternatives.</summary>
    public static Regex Glob(string pattern)
    {
        var alts = pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => "^" + Regex.Escape(a).Replace("\\*", ".*").Replace("\\?", ".") + "$");
        return new Regex(string.Join("|", alts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static HardwareSpecTableData LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Embedded resource {Resource} missing.");
        return JsonSerializer.Deserialize<HardwareSpecTableData>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Hardware spec table is empty.");
    }

    /// <summary>Contents for a starter override file, written by the app when none exists so operators find the
    /// format beside their data instead of in documentation.</summary>
    public const string OverrideTemplate = """
        {
          // Operator additions and corrections to Marco's bundled hardware spec table. Entries here win over the
          // bundled ones. Match fields: Manufacturer (dell, hp, lenovo, ...), Model (wildcards: * ? and | for
          // alternatives, matched against the model string and the product version), optional Chassis and Board
          // patterns. Facts: MaxMemoryGb, MemorySlots (0 = soldered), MemoryType, MemoryFormFactor, DriveBays
          // (2.5/3.5-inch), M2Slots (storage M.2 only), BayNotes, Source. Restart Marco after editing.
          "SchemaVersion": 1,
          "Updated": "",
          "Specs": [
            // { "Manufacturer": "dell", "Model": "OptiPlex 9999*", "Name": "Dell OptiPlex 9999",
            //   "MaxMemoryGb": 64, "MemorySlots": 4, "MemoryType": "DDR5", "MemoryFormFactor": "DIMM",
            //   "DriveBays": 2, "M2Slots": 1, "Source": "Dell OptiPlex 9999 Setup and Specifications" }
          ]
        }
        """;
}
