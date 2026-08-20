using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marco.Core.Compliance;

/// <summary>
/// Assembles the effective rule set: the embedded default pack, then any user packs from the compliance
/// directory (alphabetical; a later rule with the same id replaces the earlier one), then the operator's
/// per-rule enable/disable overrides (deltas only, like CollectorOverrides). Rules referencing a check id the
/// catalog doesn't know are dropped with a warning rather than failing the whole pack — a pack written for a
/// newer Marco should degrade, not explode.
/// </summary>
public static class RulePackLoader
{
    private const string DefaultResource = "Marco.Core.Compliance.Resources.default-rules.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static RulePack LoadDefaultPack()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefaultResource)
            ?? throw new InvalidOperationException($"Embedded resource {DefaultResource} missing.");
        return JsonSerializer.Deserialize<RulePack>(stream, Options)
            ?? throw new InvalidOperationException("Default rule pack is empty.");
    }

    /// <summary>The rule set evaluation should run with right now. Never throws: unreadable user packs are
    /// skipped with a warning, and the embedded defaults always exist.</summary>
    public static IReadOnlyList<RuleDefinition> LoadEffectiveRules(
        string? userPackDirectory,
        IReadOnlyDictionary<string, bool>? overrides = null,
        Action<string>? warn = null)
    {
        var byId = new Dictionary<string, RuleDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in LoadDefaultPack().Rules)
            byId[rule.Id] = rule;

        if (userPackDirectory is not null && Directory.Exists(userPackDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(userPackDirectory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var pack = JsonSerializer.Deserialize<RulePack>(File.ReadAllText(path), Options);
                    if (pack?.Rules is null) { warn?.Invoke($"Rule pack {Path.GetFileName(path)} has no rules."); continue; }
                    foreach (var rule in pack.Rules)
                        byId[rule.Id] = rule;
                }
                catch (Exception ex)
                {
                    warn?.Invoke($"Rule pack {Path.GetFileName(path)} skipped: {ex.Message}");
                }
            }
        }

        var effective = new List<RuleDefinition>();
        foreach (var rule in byId.Values)
        {
            if (!RuleCheckCatalog.Checks.ContainsKey(rule.Check))
            {
                warn?.Invoke($"Rule '{rule.Id}' skipped: unknown check '{rule.Check}'.");
                continue;
            }
            bool enabled = overrides is not null && overrides.TryGetValue(rule.Id, out var o) ? o : rule.Enabled;
            effective.Add(rule with { Enabled = enabled });
        }
        return effective;
    }

    /// <summary>Deltas from pack defaults (the CollectorOverrides pattern): only rules whose enabled state
    /// differs from their definition are stored, so pack updates keep their own defaults.</summary>
    public static Dictionary<string, bool>? OverridesFor(IReadOnlyList<RuleDefinition> packDefaults, IReadOnlyDictionary<string, bool> current)
    {
        var deltas = new Dictionary<string, bool>();
        foreach (var rule in packDefaults)
            if (current.TryGetValue(rule.Id, out var enabled) && enabled != rule.Enabled)
                deltas[rule.Id] = enabled;
        return deltas.Count == 0 ? null : deltas;
    }
}
