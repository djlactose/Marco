using Marco.Core.Inventory;
using Marco.Core.Scanning;

namespace Marco.Core.Cli;

/// <summary>Parses the `scan` verb's arguments into <see cref="CliOptions"/>, or a <see cref="CliParseError"/>.
/// Pure and fully testable — no IO, no environment. Collector names are validated against the catalog and
/// concurrency is clamped exactly as the UI clamps it, so a scheduled scan behaves like an interactive one.</summary>
public static class CliArgumentParser
{
    public const string Usage =
        "Usage: Marco.exe scan --targets <file|token[,token...]> [--out <path.json>]\n" +
        "            [--csv <dir>] [--collectors Name,Name] [--concurrency N]\n" +
        "            [--no-inventory] [--credential-label <label>] [--client <name>]\n" +
        "            [--exit-code-on-change] [--quiet] [--log <path>]\n" +
        "\n" +
        "At least one of --out or --csv is required. Credentials come from the saved DPAPI\n" +
        "store, so the task must run as the Windows user that saved them.";

    public static object Parse(IReadOnlyList<string> args)
    {
        string? targets = null, outJson = null, csvDir = null, collectorsRaw = null,
            credentialLabel = null, client = null, logPath = null;
        int? concurrency = null;
        bool noInventory = false, quiet = false, exitOnChange = false;

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--no-inventory": noInventory = true; break;
                case "--quiet" or "-q": quiet = true; break;
                case "--exit-code-on-change": exitOnChange = true; break;

                case "--targets": if (!Next(args, ref i, out targets)) return Missing(arg); break;
                case "--out": if (!Next(args, ref i, out outJson)) return Missing(arg); break;
                case "--csv": if (!Next(args, ref i, out csvDir)) return Missing(arg); break;
                case "--collectors": if (!Next(args, ref i, out collectorsRaw)) return Missing(arg); break;
                case "--credential-label": if (!Next(args, ref i, out credentialLabel)) return Missing(arg); break;
                case "--client": if (!Next(args, ref i, out client)) return Missing(arg); break;
                case "--log": if (!Next(args, ref i, out logPath)) return Missing(arg); break;

                case "--concurrency":
                    if (!Next(args, ref i, out var cval)) return Missing(arg);
                    if (!int.TryParse(cval, out var c) || c < 1)
                        return new CliParseError($"--concurrency needs a positive integer, got '{cval}'.");
                    concurrency = Math.Clamp(c, 1, ConcurrencyLimits.Max);
                    break;

                default:
                    return new CliParseError($"Unknown argument '{arg}'.\n\n{Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(targets))
            return new CliParseError("--targets is required.\n\n" + Usage);
        if (outJson is null && csvDir is null)
            return new CliParseError("At least one of --out or --csv is required.\n\n" + Usage);

        IReadOnlyList<string>? collectors = null;
        if (collectorsRaw is not null)
        {
            var names = collectorsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var valid = CollectorCatalog.All.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = names.Where(n => !valid.Contains(n)).ToList();
            if (unknown.Count > 0)
                return new CliParseError(
                    $"Unknown collector(s): {string.Join(", ", unknown)}.\nValid: {string.Join(", ", valid.OrderBy(n => n))}.");
            collectors = names.ToList();
        }

        if (noInventory && collectors is not null)
            return new CliParseError("--collectors has no effect with --no-inventory.");

        return new CliOptions(targets!, outJson, csvDir, collectors, concurrency, noInventory,
            credentialLabel, client, quiet, logPath, exitOnChange);
    }

    private static bool Next(IReadOnlyList<string> args, ref int i, out string? value)
    {
        if (i + 1 >= args.Count) { value = null; return false; }
        value = args[++i];
        return true;
    }

    private static CliParseError Missing(string flag) => new($"{flag} needs a value.\n\n{Usage}");
}
