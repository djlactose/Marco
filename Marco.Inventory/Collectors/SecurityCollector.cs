using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

/// <summary>
/// Security posture from the sources an admin normally has to visit one by one: Security Center (client SKUs)
/// and Defender status, firewall profiles, BitLocker, TPM, Secure Boot / firmware type, UAC, Remote Desktop and
/// NLA, SMB1 / signing, virtualization-based security, and LAPS presence. Every probe is independent — a
/// namespace that doesn't exist on this SKU (SecurityCenter2 on Server, BitLocker on Home) is a note, not a
/// failure — and every field stays null when it could not be determined, so "off" always means "determined off".
/// Read-only throughout: no policy is changed, no password (LAPS or otherwise) is ever read.
/// </summary>
public sealed class SecurityCollector : IInventoryCollector
{
    public string Name => "Security";

    private const string SecureBootKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";
    private const string ControlKey = @"SYSTEM\CurrentControlSet\Control";
    private const string UacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string TerminalServerKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string RdpTcpKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    private const string LanmanKey = @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters";
    private const string LegacyLapsKey = @"SOFTWARE\Policies\Microsoft Services\AdmPwd";
    private const string WindowsLapsPolicyKey = @"SOFTWARE\Microsoft\Policies\LAPS";
    private const string WindowsLapsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\LAPS";

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var steps = new CollectorSteps();
        var s = machine.Security;
        var wmi = context.Wmi;
        var reg = context.Registry;

        // 1. Security Center products (client SKUs only — the namespace does not exist on Server).
        await steps.RunAsync("Security Center", async () =>
        {
            machine.Antivirus.Clear();
            foreach (var (cls, kind) in new[] { ("AntiVirusProduct", "Antivirus"), ("AntiSpywareProduct", "Antispyware"), ("FirewallProduct", "Firewall") })
            {
                IReadOnlyList<WmiObject> rows;
                if (cls == "AntiVirusProduct")
                    rows = await wmi.QueryAsync(WmiQueryHelpers.SecurityCenter2, $"SELECT displayName, productState FROM {cls}", ct);
                else
                    rows = await wmi.QueryOptionalAsync($"SELECT displayName, productState FROM {cls}", ct, WmiQueryHelpers.SecurityCenter2);
                foreach (var r in rows)
                {
                    var state = r.GetInt("productState");
                    var (enabled, upToDate) = state is { } st ? DecodeProductState(st) : (null, null);
                    machine.Antivirus.Add(new AntivirusEntry
                    {
                        Product = r.GetString("displayName")?.Trim(),
                        Kind = kind,
                        State = state is { } v ? $"0x{v:X6}" : null,
                        Enabled = enabled,
                        UpToDate = upToDate,
                    });
                }
            }
        });

        // 2. Defender itself (also on servers, where Security Center is absent).
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("Defender", async () =>
        {
            var d = await wmi.QueryFirstAsync(
                "SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureVersion, AntivirusSignatureLastUpdated, "
                + "AntivirusSignatureAge, QuickScanEndTime, IsTamperProtected, AMEngineVersion, AMRunningMode FROM MSFT_MpComputerStatus",
                ct, WmiQueryHelpers.Defender);
            if (d is null) throw new WmiException(WmiFailureKind.NotSupported, "MSFT_MpComputerStatus returned nothing.");
            s.DefenderEnabled = d.GetBool("AntivirusEnabled");
            s.DefenderRealTime = d.GetBool("RealTimeProtectionEnabled");
            s.DefenderSignatureVersion = d.GetString("AntivirusSignatureVersion");
            s.DefenderSignatureUpdated = d.GetDateTime("AntivirusSignatureLastUpdated");
            s.DefenderSignatureAgeDays = d.GetInt("AntivirusSignatureAge");
            s.DefenderLastQuickScan = d.GetDateTime("QuickScanEndTime");
            s.DefenderTamperProtected = d.GetBool("IsTamperProtected");
            s.DefenderEngineVersion = d.GetString("AMEngineVersion");
            s.DefenderRunningMode = d.GetString("AMRunningMode");

            // Servers have no Security Center row for Defender; synthesise one so the AV column is populated.
            if (!machine.Antivirus.Any(a => a.Kind == "Antivirus"))
            {
                machine.Antivirus.Add(new AntivirusEntry
                {
                    Product = "Windows Defender",
                    Kind = "Antivirus",
                    Enabled = s.DefenderEnabled,
                    UpToDate = s.DefenderSignatureAgeDays is { } age ? age <= 7 : null,
                });
            }
        });

        // 3. Firewall profiles.
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("Firewall", async () =>
        {
            var rows = await wmi.QueryAsync(WmiQueryHelpers.StandardCimV2,
                "SELECT Name, Enabled FROM MSFT_NetFirewallProfile", ct);
            if (rows.Count == 0) throw new WmiException(WmiFailureKind.NotSupported, "MSFT_NetFirewallProfile returned nothing.");
            foreach (var r in rows)
            {
                // Enabled: 0 = False, 1 = True, 2 = NotConfigured.
                var enabled = r.GetInt("Enabled") switch { 1 => true, 0 => (bool?)false, _ => null };
                switch (r.GetString("Name")?.Trim().ToLowerInvariant())
                {
                    case "domain": s.FirewallDomain = enabled; break;
                    case "private": s.FirewallPrivate = enabled; break;
                    case "public": s.FirewallPublic = enabled; break;
                }
            }
        });

        // 4. BitLocker.
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("BitLocker", async () =>
        {
            var rows = await wmi.QueryAsync(WmiQueryHelpers.VolumeEncryption,
                "SELECT DriveLetter, ProtectionStatus, ConversionStatus, EncryptionMethod, VolumeType FROM Win32_EncryptableVolume", ct);
            s.BitLockerVolumes.Clear();
            foreach (var r in rows)
            {
                s.BitLockerVolumes.Add(new BitLockerVolumeEntry
                {
                    Letter = r.GetString("DriveLetter"),
                    Protection = r.GetInt("ProtectionStatus") switch { 0 => "Off", 1 => "On", 2 => "Unknown", _ => null },
                    Status = DescribeConversionStatus(r.GetInt("ConversionStatus")),
                    Method = DescribeEncryptionMethod(r.GetInt("EncryptionMethod")),
                    VolumeType = r.GetInt("VolumeType") switch { 0 => "OS", 1 => "Fixed data", 2 => "Removable", _ => null },
                });
            }
            s.BitLockerVolumes.Sort((a, b) => string.CompareOrdinal(a.Letter, b.Letter));
        });

        // 5. TPM.
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("TPM", async () =>
        {
            var rows = await wmi.QueryAsync(WmiQueryHelpers.Tpm,
                "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, IsOwned_InitialValue, SpecVersion, ManufacturerIdTxt FROM Win32_Tpm", ct);
            if (rows.Count == 0) { s.TpmPresent = false; return; }
            var t = rows[0];
            s.TpmPresent = true;
            s.TpmEnabled = t.GetBool("IsEnabled_InitialValue");
            s.TpmActivated = t.GetBool("IsActivated_InitialValue");
            s.TpmOwned = t.GetBool("IsOwned_InitialValue");
            s.TpmVersion = TpmSpecVersion(t.GetString("SpecVersion"));
            s.TpmManufacturer = t.GetString("ManufacturerIdTxt")?.Trim();
        });

        // 6. Firmware / Secure Boot (registry).
        ct.ThrowIfCancellationRequested();
        steps.Run("Secure Boot (registry)", () =>
        {
            // PEFirmwareType is only present on some builds; the SecureBoot\State key itself only exists on UEFI
            // firmware, so its presence is the reliable UEFI signal (its value is the Secure Boot state).
            var fw = reg.GetValues(RegistryRoot.LocalMachine, ControlKey, new[] { "PEFirmwareType" });
            s.FirmwareType = RegistryValues.AsInt(RegistryValues.Get(fw, "PEFirmwareType")) switch { 1 => "BIOS", 2 => "UEFI", _ => null };
            var sb = reg.GetValues(RegistryRoot.LocalMachine, SecureBootKey, new[] { "UEFISecureBootEnabled" });
            var secureBoot = RegistryValues.AsBool(RegistryValues.Get(sb, "UEFISecureBootEnabled"));
            s.FirmwareType ??= secureBoot is not null ? "UEFI" : null;
            s.SecureBoot = secureBoot ?? (s.FirmwareType == "BIOS" ? false : null);
            if (s.FirmwareType is null && s.SecureBoot is null) throw new InvalidOperationException("Firmware keys not readable.");
        });

        // 7. UAC (registry).
        ct.ThrowIfCancellationRequested();
        steps.Run("UAC (registry)", () =>
        {
            var v = reg.GetValues(RegistryRoot.LocalMachine, UacKey,
                new[] { "EnableLUA", "ConsentPromptBehaviorAdmin", "LocalAccountTokenFilterPolicy" });
            if (v.Count == 0) throw new InvalidOperationException("Policies\\System key not readable.");
            s.UacEnabled = RegistryValues.AsBool(RegistryValues.Get(v, "EnableLUA")) ?? true; // absent = default on
            s.UacAdminPrompt = RegistryValues.AsInt(RegistryValues.Get(v, "ConsentPromptBehaviorAdmin")) is { } p ? DescribeAdminPrompt(p) : null;
            s.LocalAccountTokenFilterPolicy = RegistryValues.AsBool(RegistryValues.Get(v, "LocalAccountTokenFilterPolicy")) ?? false;
        });

        // 8. Remote Desktop (registry).
        ct.ThrowIfCancellationRequested();
        steps.Run("Remote Desktop (registry)", () =>
        {
            var ts = reg.GetValues(RegistryRoot.LocalMachine, TerminalServerKey, new[] { "fDenyTSConnections" });
            var deny = RegistryValues.AsBool(RegistryValues.Get(ts, "fDenyTSConnections"));
            if (deny is null) throw new InvalidOperationException("Terminal Server key not readable.");
            s.RdpEnabled = !deny.Value;
            var tcp = reg.GetValues(RegistryRoot.LocalMachine, RdpTcpKey, new[] { "UserAuthentication", "PortNumber" });
            s.RdpNlaRequired = RegistryValues.AsBool(RegistryValues.Get(tcp, "UserAuthentication"));
            s.RdpPort = RegistryValues.AsInt(RegistryValues.Get(tcp, "PortNumber"));
        });

        // 9. SMB server configuration (Win8/2012+), else registry.
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("SMB", async () =>
        {
            var cfg = await wmi.QueryOptionalAsync(
                "SELECT EnableSMB1Protocol, RequireSecuritySignature, EncryptData FROM MSFT_SmbServerConfiguration",
                ct, WmiQueryHelpers.Smb);
            if (cfg.Count > 0)
            {
                s.Smb1Enabled = cfg[0].GetBool("EnableSMB1Protocol");
                s.SmbSigningRequired = cfg[0].GetBool("RequireSecuritySignature");
                s.SmbEncryptData = cfg[0].GetBool("EncryptData");
                return;
            }
            // Fallbacks: the optional feature (Win10+ client) and the LanmanServer parameters.
            var feature = await wmi.QueryOptionalAsync(
                "SELECT InstallState FROM Win32_OptionalFeature WHERE Name = 'SMB1Protocol'", ct);
            if (feature.Count > 0)
                s.Smb1Enabled = feature[0].GetInt("InstallState") switch { 1 => true, 2 or 3 => (bool?)false, _ => null };
            var lm = reg.GetValues(RegistryRoot.LocalMachine, LanmanKey, new[] { "SMB1", "RequireSecuritySignature" });
            s.Smb1Enabled ??= RegistryValues.AsBool(RegistryValues.Get(lm, "SMB1"));
            s.SmbSigningRequired = RegistryValues.AsBool(RegistryValues.Get(lm, "RequireSecuritySignature"));
            if (s.Smb1Enabled is null && s.SmbSigningRequired is null)
                throw new WmiException(WmiFailureKind.NotSupported, "No SMB configuration source available.");
        });

        // 10. Virtualization-based security.
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("Device Guard", async () =>
        {
            var dg = await wmi.QueryFirstAsync(
                "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard", ct, WmiQueryHelpers.DeviceGuard);
            if (dg is null) throw new WmiException(WmiFailureKind.NotSupported, "Win32_DeviceGuard returned nothing.");
            s.VbsStatus = dg.GetInt("VirtualizationBasedSecurityStatus") switch { 0 => "VBS off", 1 => "VBS enabled, not running", 2 => "VBS running", _ => null };
            var running = ToIntArray(dg["SecurityServicesRunning"]);
            s.CredentialGuardRunning = running.Contains(1);
            s.HvciRunning = running.Contains(2);
        });

        // 11. LAPS presence (policy keys only — never the password). Windows 11 always has the
        //     ...\CurrentVersion\LAPS\Config and \State keys, so presence of those proves nothing; what matters is
        //     an effective BackupDirectory (1 = Entra ID, 2 = Active Directory) from GPO or CSP.
        ct.ThrowIfCancellationRequested();
        steps.Run("LAPS (registry)", () =>
        {
            var gpo = reg.GetValues(RegistryRoot.LocalMachine, WindowsLapsPolicyKey, new[] { "BackupDirectory" });
            var csp = reg.GetValues(RegistryRoot.LocalMachine, WindowsLapsKey + @"\Config", new[] { "BackupDirectory" });
            var legacy = reg.GetValues(RegistryRoot.LocalMachine, LegacyLapsKey, new[] { "AdmPwdEnabled" });
            var backup = RegistryValues.AsInt(RegistryValues.Get(gpo, "BackupDirectory"))
                      ?? RegistryValues.AsInt(RegistryValues.Get(csp, "BackupDirectory"));
            if (backup is > 0)
            { s.LapsManaged = true; s.LapsKind = backup == 1 ? "Windows LAPS (Entra ID)" : "Windows LAPS (Active Directory)"; }
            else if (RegistryValues.AsBool(RegistryValues.Get(legacy, "AdmPwdEnabled")) == true)
            { s.LapsManaged = true; s.LapsKind = "Legacy LAPS (AdmPwd)"; }
            else
            { s.LapsManaged = false; s.LapsKind = null; }
        });

        s.Notes = steps.Notes;
        steps.ThrowIfNothingSucceeded();
    }

    // --- pure decoders (unit-tested) ---

    /// <summary>Security Center productState: bit 0x1000 = product on, bit 0x10 = definitions out of date.</summary>
    public static (bool? Enabled, bool? UpToDate) DecodeProductState(int state)
        => ((state & 0x1000) != 0, (state & 0x10) == 0);

    public static string? DescribeConversionStatus(int? status) => status switch
    {
        0 => "Fully decrypted",
        1 => "Fully encrypted",
        2 => "Encryption in progress",
        3 => "Decryption in progress",
        4 => "Encryption paused",
        5 => "Decryption paused",
        _ => null,
    };

    public static string? DescribeEncryptionMethod(int? method) => method switch
    {
        0 => "None",
        1 => "AES-128 + diffuser",
        2 => "AES-256 + diffuser",
        3 => "AES-128",
        4 => "AES-256",
        5 => "Hardware",
        6 => "XTS-AES 128",
        7 => "XTS-AES 256",
        _ => null,
    };

    /// <summary>"2.0, 0, 1.59" → "2.0".</summary>
    public static string? TpmSpecVersion(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var first = spec.Split(',')[0].Trim();
        return first.Length == 0 ? null : first;
    }

    public static string DescribeAdminPrompt(int value) => value switch
    {
        0 => "elevate silently",
        1 => "prompt for credentials on secure desktop",
        2 => "prompt for consent on secure desktop",
        3 => "prompt for credentials",
        4 => "prompt for consent",
        5 => "prompt for consent for non-Windows binaries",
        _ => $"behaviour {value}",
    };

    private static int[] ToIntArray(object? v) => v switch
    {
        int[] a => a,
        uint[] a => a.Select(x => unchecked((int)x)).ToArray(),
        ushort[] a => a.Select(x => (int)x).ToArray(),
        long[] a => a.Select(x => (int)x).ToArray(),
        string[] a => a.Select(x => int.TryParse(x, out var p) ? p : -1).ToArray(),
        int i => new[] { i },
        _ => Array.Empty<int>(),
    };
}
