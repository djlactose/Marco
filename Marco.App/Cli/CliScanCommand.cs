using System.IO;
using Marco.App.Services;
using Marco.Core.Cli;
using Marco.Core.Clients;
using Marco.Core.Diagnostics;
using Marco.Core.Inventory;
using Marco.Core.Scanning;
using Marco.Core.Storage;
using Marco.Core.Targets;
using Marco.Credentials;
using Marco.Export;
using Marco.Export.History;

namespace Marco.App.Cli;

/// <summary>
/// The headless `scan` verb. Reuses the same discovery/inventory engine and DPAPI credential store as the UI,
/// but never touches the update pipeline or the crash-loop sentinel — a scheduled scan must not swap the exe
/// under a running interactive instance. Credentials come from the saved store; plaintext passwords are never
/// accepted as arguments.
/// </summary>
public static class CliScanCommand
{
    public static async Task<int> RunAsync(CliOptions options, CancellationToken ct)
    {
        var paths = AppPaths.Resolve();
        var runLog = new RunLog(paths.RunLogFile);

        // --log gets everything; --quiet suppresses stdout progress but never the final summary/errors.
        StreamWriter? logFile = null;
        try { if (options.LogPath is not null) logFile = new StreamWriter(options.LogPath, append: true) { AutoFlush = true }; }
        catch { /* unwritable log path is not fatal */ }

        void Log(string line)
        {
            logFile?.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            if (!options.Quiet) Console.WriteLine(line);
        }
        void Error(string line)
        {
            logFile?.WriteLine($"{DateTime.Now:HH:mm:ss} ERROR {line}");
            Console.Error.WriteLine(line);
        }

        try
        {
            // Resolve the client (targets fallback + credential scope + history tag).
            ClientProfile? client = null;
            if (options.ClientName is not null)
            {
                client = new ClientProfileStore(paths.ClientsFile).Load()
                    .FirstOrDefault(c => string.Equals(c.Name, options.ClientName, StringComparison.OrdinalIgnoreCase));
                if (client is null) { Error($"No client named '{options.ClientName}'."); return (int)CliExitCode.Usage; }
            }

            // Targets: --targets overrides the client's; a file path is read, otherwise treated as inline tokens.
            var targetsText = ResolveTargetsText(options.TargetsValue, client);
            if (string.IsNullOrWhiteSpace(targetsText)) { Error("No targets."); return (int)CliExitCode.Usage; }

            List<ScanTarget> scanTargets;
            List<string> ranges;
            try
            {
                var opts = new TargetExpansionOptions { AllowLargeExpansion = true }; // no dialog to confirm; just report
                long estimate = TargetParser.EstimateCount(new[] { targetsText }, opts);
                if (estimate == 0) { Error("No valid targets parsed."); return (int)CliExitCode.Usage; }
                Log($"Expanding {estimate:N0} target address(es)…");
                scanTargets = TargetParser.Parse(new[] { targetsText }, opts).ToList();
                ranges = TargetParser.Tokenize(new[] { targetsText }).ToList();
            }
            catch (TargetParseException ex)
            {
                Error($"Invalid target '{ex.Token}': {ex.Message}");
                return (int)CliExitCode.Usage;
            }

            // Credentials from the DPAPI store, scoped to the client, optionally filtered by label.
            IReadOnlyList<CredentialCandidate> candidates;
            using (var store = new CredentialStore())
            {
                try { store.Load(paths.CredentialsFile); }
                catch (CredentialDecryptException ex)
                {
                    Error(ex.Message);
                    Error("The scheduled task must run as the same Windows user that saved the credentials.");
                    return (int)CliExitCode.CredentialDecrypt;
                }
                var scoped = store.ToCandidatesFor(client?.Id);
                candidates = options.CredentialLabel is { } label
                    ? scoped.Where(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase)).ToList()
                    : scoped;
                if (options.CredentialLabel is not null && candidates.Count == 0)
                { Error($"No credential labelled '{options.CredentialLabel}'."); return (int)CliExitCode.Usage; }
                // Same default as the interactive app: printers/network gear get the "public" community unless
                // the operator configured an SNMP credential (or pinned a specific credential by label).
                if (options.CredentialLabel is null && !candidates.Any(c => c.Kind == CredentialKind.Snmp))
                    candidates = candidates.Append(CredentialSet.SnmpDefault().ToCandidate()).ToList();
            }

            var settings = new ScanSettings
            {
                DiscoveryConcurrency = options.Concurrency ?? 32,
                InventoryConcurrency = options.Concurrency ?? 16,
            };
            var enabled = options.CollectorNames is { } names
                ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
                : null;

            runLog.ScanStarted(ranges, scanTargets.Count);
            runLog.CliScan("started", new { targets = scanTargets.Count, client = client?.Name, inventory = !options.NoInventory });

            var session = new HeadlessScanSession(
                DiscoveryFactory.CreateController(),
                InventoryFactory.CreateRunner(),
                InventoryFactory.CreateLinuxRunner(),
                InventoryFactory.CreateSnmpRunner(),
                Log);
            var result = await session.RunAsync(scanTargets, settings, candidates, enabled,
                inventory: !options.NoInventory, includeUnreachable: false, ct).ConfigureAwait(false);
            runLog.ScanFinished(result.Alive, result.Unreachable, 0);

            // Build the document once; write outputs.
            var meta = new ScanMetadata(DateTime.Now, Environment.UserName, ranges,
                result.Machines.Count, result.Alive, Version: Marco.Core.AppVersion.Display);
            var doc = ScanDocument.From(meta, result.Machines);

            try
            {
                if (options.OutJsonPath is { } outPath)
                {
                    new JsonExporter().Export(doc, outPath);
                    Log($"Wrote {outPath}");
                }
                if (options.CsvDirectory is { } csvDir)
                {
                    var files = new CsvExporter().Export(doc, csvDir);
                    Log($"Wrote {files.Count} CSV files to {csvDir}");
                }
            }
            catch (Exception ex)
            {
                Error($"Failed to write output: {ex.Message}");
                return (int)CliExitCode.WriteFailed;
            }

            // Also drop a copy into scan history so an interactive Marco sees the run.
            try
            {
                var store = new ScanHistoryStore(paths.ScansDirectory);
                store.Save(doc, ScanHistoryStore.NewRunId(DateTime.Now),
                    options.NoInventory ? ScanHistoryPhase.DiscoveryOnly : ScanHistoryPhase.Inventoried,
                    client: client?.Name);
            }
            catch { /* history is best-effort */ }

            // Prerequisite-doctor rollup: one line per cause (respects --quiet).
            var rollup = Marco.Core.Diagnosis.PrereqDoctor.Rollup(result.Machines);
            if (rollup.Count > 0 && !options.Quiet)
            {
                Log("Hosts needing target fixes:");
                foreach (var g in rollup) Log($"  {g.Machines.Count,4}  {g.Title}");
            }

            runLog.CliScan("finished", new { alive = result.Alive, authenticated = result.Authenticated });
            Log($"Done: {result.Alive} alive, {result.Authenticated} authenticated.");
            return (int)CliExitCode.Ok;
        }
        catch (OperationCanceledException)
        {
            Error("Cancelled.");
            return (int)CliExitCode.ScanFailed;
        }
        catch (Exception ex)
        {
            Error($"Scan failed: {ex.Message}");
            runLog.CliScan("failed", new { error = ex.Message });
            return (int)CliExitCode.ScanFailed;
        }
        finally
        {
            logFile?.Dispose();
        }
    }

    private static string ResolveTargetsText(string value, ClientProfile? client)
    {
        // A path that exists is a host file; the inline value can also carry comma/space-separated tokens.
        if (File.Exists(value))
        {
            try { return File.ReadAllText(value); }
            catch { return value; }
        }
        if (string.IsNullOrWhiteSpace(value) && client is not null) return client.TargetsText;
        return value;
    }
}
