using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;
using Marco.Inventory.Collectors;
using Xunit;
using static Marco.Tests.WmiFakeBuilders;

namespace Marco.Tests;

/// <summary>The Phase 3 collectors: updates, security, users, services, peripherals, USB history, scheduled tasks,
/// and the storage enrichment. Fakes are keyed by WMI class name; the registry fake by "Root:path".</summary>
public class Phase3CollectorTests
{
    private static InventoryContext Ctx(FakeWmiSession wmi, FakeRemoteRegistry? reg = null)
        => new(wmi, reg ?? new FakeRemoteRegistry());

    private static WmiException NotSupported() => new(WmiFailureKind.NotSupported, "missing");
    private static WmiException Denied() => new(WmiFailureKind.AccessDenied, "denied");

    // ---------------- Updates ----------------

    [Fact]
    public async Task Updates_ReadsHotfixesVersionAndPendingReboot()
    {
        var wmi = new FakeWmiSession().With("Win32_QuickFixEngineering",
            Obj(("HotFixID", "KB5040442"), ("Description", "Security Update"), ("InstalledOn", "7/10/2024"), ("InstalledBy", "NT AUTHORITY\\SYSTEM")),
            Obj(("HotFixID", "KB5039212"), ("Description", "Update"), ("InstalledOn", "6/12/2024")));
        var reg = new FakeRemoteRegistry();
        reg.KeyValues["LocalMachine:SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"] = new()
        {
            ["DisplayVersion"] = "23H2", ["UBR"] = 4037, ["EditionID"] = "Professional",
            ["InstallationType"] = "Client", ["ProductName"] = "Windows 10 Pro", ["CurrentBuild"] = "22631",
        };
        reg.SubkeyNames["LocalMachine:SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Component Based Servicing"] = new() { "RebootPending", "Packages" };
        reg.KeyValues["LocalMachine:SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate"] = new() { ["WUServer"] = "http://wsus:8530", ["TargetGroup"] = "Pilot", ["TargetGroupEnabled"] = 1 };
        reg.KeyValues["LocalMachine:SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU"] = new() { ["UseWUServer"] = 1L, ["AUOptions"] = 4 };

        var m = new Machine("10.0.0.1");
        m.Os.Version = "10.0.22631";
        await new UpdatesCollector().CollectAsync(Ctx(wmi, reg), m, default);

        Assert.Equal(2, m.Hotfixes.Count);
        Assert.Equal("KB5040442", m.Hotfixes[0].Id); // newest first
        Assert.Equal(new DateTime(2024, 7, 10), m.Updates.LastHotfixDate);
        Assert.Equal(2, m.Updates.HotfixCount);
        Assert.Equal("23H2", m.Updates.DisplayVersion);
        Assert.Equal("10.0.22631.4037", m.Updates.FullBuild);
        Assert.Equal("Professional", m.Updates.EditionId);
        Assert.True(m.Updates.PendingReboot);
        Assert.Contains("Component Based Servicing", m.Updates.PendingRebootReasons);
        Assert.Equal("http://wsus:8530", m.Updates.WsusServer);
        Assert.Equal("Pilot", m.Updates.WsusTargetGroup);
        Assert.True(m.Updates.UseWsus);
        Assert.Equal("Auto download and schedule install", m.Updates.AutoUpdateOption);
        Assert.Null(m.Updates.Notes);
        Assert.Equal(2, m.HotfixCount);
    }

    [Fact]
    public async Task Updates_RegistryUnavailable_KeepsHotfixesAndNotes()
    {
        var wmi = new FakeWmiSession().With("Win32_QuickFixEngineering", Obj(("HotFixID", "KB1"), ("InstalledOn", "2024-01-02")));
        var reg = new FakeRemoteRegistry { ThrowOnAccess = true };
        var m = new Machine("10.0.0.2");
        await new UpdatesCollector().CollectAsync(Ctx(wmi, reg), m, default); // must not throw

        Assert.Single(m.Hotfixes);
        Assert.Null(m.Updates.PendingReboot);      // unknown, not "no"
        Assert.Null(m.Updates.FullBuild);
        Assert.Contains("registry", m.Updates.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Updates_NothingAvailable_Throws()
    {
        var wmi = new FakeWmiSession().Throws("Win32_QuickFixEngineering", Denied());
        var reg = new FakeRemoteRegistry { ThrowOnAccess = true };
        var m = new Machine("10.0.0.3");
        var ex = await Assert.ThrowsAsync<WmiException>(() => new UpdatesCollector().CollectAsync(Ctx(wmi, reg), m, default));
        Assert.Equal(WmiFailureKind.AccessDenied, ex.Kind);
    }

    [Theory]
    [InlineData("7/10/2024", 2024, 7, 10)]
    [InlineData("20240710", 2024, 7, 10)]
    [InlineData("2024-07-10", 2024, 7, 10)]
    [InlineData("20240710120000.000000+000", 2024, 7, 10)]
    public void Updates_ParsesInstalledOnFormats(string raw, int y, int mo, int d)
        => Assert.Equal(new DateTime(y, mo, d), UpdatesCollector.ParseInstalledOn(raw));

    [Fact]
    public void Updates_ParsesHexFileTime()
        => Assert.NotNull(UpdatesCollector.ParseInstalledOn("01D9E1C4B2A3F000")); // some 2023 date; just must parse

    [Fact]
    public void Updates_ComposeFullBuild_FallsBackToRegistryBuild()
    {
        Assert.Equal("10.0.22631.4037", UpdatesCollector.ComposeFullBuild("10.0.22631", "22631", 4037));
        Assert.Equal("10.0.19045.1", UpdatesCollector.ComposeFullBuild(null, "19045", 1));
        Assert.Equal("10.0.19045", UpdatesCollector.ComposeFullBuild(null, "19045", null));
        Assert.Null(UpdatesCollector.ComposeFullBuild(null, null, 5));
    }

    [Fact]
    public void Updates_PendingReboot_FalseWhenKeysReadableButAbsent()
    {
        var reg = new FakeRemoteRegistry(); // every lookup returns empty → evaluated, nothing pending
        var (pending, reasons) = UpdatesCollector.ProbePendingReboot(reg);
        Assert.False(pending);
        Assert.Empty(reasons);
    }

    [Fact]
    public void Updates_PendingReboot_DetectsFileRenamesAndComputerRename()
    {
        var reg = new FakeRemoteRegistry();
        reg.KeyValues["LocalMachine:SYSTEM\\CurrentControlSet\\Control\\Session Manager"] = new() { ["PendingFileRenameOperations"] = new[] { "\\??\\C:\\x.tmp", "" } };
        reg.KeyValues["LocalMachine:SYSTEM\\CurrentControlSet\\Control\\ComputerName\\ActiveComputerName"] = new() { ["ComputerName"] = "OLD" };
        reg.KeyValues["LocalMachine:SYSTEM\\CurrentControlSet\\Control\\ComputerName\\ComputerName"] = new() { ["ComputerName"] = "NEW" };
        var (pending, reasons) = UpdatesCollector.ProbePendingReboot(reg);
        Assert.True(pending);
        Assert.Contains("Pending file renames", reasons);
        Assert.Contains("Computer rename", reasons);
    }

    // ---------------- Security ----------------

    [Theory]
    [InlineData(0x061100, true, true)]   // on, up to date
    [InlineData(0x060100, false, true)]  // off, up to date
    [InlineData(0x061110, true, false)]  // on, out of date
    [InlineData(397568, true, true)]     // the decimal WMI shows for 0x061100
    public void Security_DecodesProductState(int state, bool enabled, bool upToDate)
    {
        var (e, u) = SecurityCollector.DecodeProductState(state);
        Assert.Equal(enabled, e);
        Assert.Equal(upToDate, u);
    }

    [Fact]
    public async Task Security_ClientSku_ReadsAllProbes()
    {
        var wmi = new FakeWmiSession()
            .With("AntiVirusProduct", Obj(("displayName", "Windows Defender"), ("productState", 397568)),
                                     Obj(("displayName", "Acme AV"), ("productState", 0x060100)))
            .With("FirewallProduct", Obj(("displayName", "Acme Firewall"), ("productState", 0x061100)))
            .With("MSFT_MpComputerStatus", Obj(("AntivirusEnabled", true), ("RealTimeProtectionEnabled", true),
                ("AntivirusSignatureVersion", "1.415.100.0"), ("AntivirusSignatureAge", 1), ("IsTamperProtected", true), ("AMEngineVersion", "1.1.24060.5")))
            .With("MSFT_NetFirewallProfile", Obj(("Name", "Domain"), ("Enabled", 1)), Obj(("Name", "Private"), ("Enabled", 1)), Obj(("Name", "Public"), ("Enabled", 0)))
            .With("Win32_EncryptableVolume", Obj(("DriveLetter", "C:"), ("ProtectionStatus", 1), ("ConversionStatus", 1), ("EncryptionMethod", 7), ("VolumeType", 0)),
                                             Obj(("DriveLetter", "D:"), ("ProtectionStatus", 0), ("ConversionStatus", 0), ("EncryptionMethod", 0), ("VolumeType", 1)))
            .With("Win32_Tpm", Obj(("IsEnabled_InitialValue", true), ("IsActivated_InitialValue", true), ("IsOwned_InitialValue", true), ("SpecVersion", "2.0, 0, 1.59"), ("ManufacturerIdTxt", "INTC")))
            .With("MSFT_SmbServerConfiguration", Obj(("EnableSMB1Protocol", false), ("RequireSecuritySignature", true), ("EncryptData", false)))
            .With("Win32_DeviceGuard", Obj(("VirtualizationBasedSecurityStatus", 2), ("SecurityServicesRunning", new[] { 1, 2 })));
        var reg = new FakeRemoteRegistry();
        reg.KeyValues["LocalMachine:SYSTEM\\CurrentControlSet\\Control"] = new() { ["PEFirmwareType"] = 2 };
        reg.KeyValues["LocalMachine:SYSTEM\\CurrentControlSet\\Control\\SecureBoot\\State"] = new() { ["UEFISecureBootEnabled"] = 1 };
        reg.KeyValues["LocalMachine:SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System"] = new() { ["EnableLUA"] = 1, ["ConsentPromptBehaviorAdmin"] = 5, ["LocalAccountTokenFilterPolicy"] = 1L };
        reg.KeyValues["LocalMachine:SYSTEM\\CurrentControlSet\\Control\\Terminal Server"] = new() { ["fDenyTSConnections"] = 0 };
        reg.KeyValues["LocalMachine:SYSTEM\\CurrentControlSet\\Control\\Terminal Server\\WinStations\\RDP-Tcp"] = new() { ["UserAuthentication"] = 1, ["PortNumber"] = 3389 };
        reg.KeyValues["LocalMachine:SOFTWARE\\Microsoft\\Policies\\LAPS"] = new() { ["BackupDirectory"] = 2 };

        var m = new Machine("10.0.0.4");
        await new SecurityCollector().CollectAsync(Ctx(wmi, reg), m, default);
        var s = m.Security;

        Assert.Equal(3, m.Antivirus.Count);
        var acme = m.Antivirus.Single(a => a.Product == "Acme AV");
        Assert.False(acme.Enabled); Assert.True(acme.UpToDate); Assert.Equal("Antivirus", acme.Kind);
        Assert.Equal("Firewall", m.Antivirus.Single(a => a.Product == "Acme Firewall").Kind);
        Assert.Contains("Windows Defender (on)", m.AntivirusSummary);
        Assert.Contains("Acme AV (OFF)", m.AntivirusSummary);

        Assert.True(s.DefenderEnabled); Assert.True(s.DefenderRealTime); Assert.Equal(1, s.DefenderSignatureAgeDays); Assert.True(s.DefenderTamperProtected);
        Assert.True(s.FirewallDomain); Assert.True(s.FirewallPrivate); Assert.False(s.FirewallPublic);
        Assert.Equal("Domain on, Private on, Public OFF", s.FirewallSummary);

        Assert.Equal(2, s.BitLockerVolumes.Count);
        Assert.Equal("On", s.BitLockerVolumes[0].Protection);
        Assert.Equal("XTS-AES 256", s.BitLockerVolumes[0].Method);
        Assert.Equal("Fully encrypted", s.BitLockerVolumes[0].Status);
        Assert.Equal("C: On (XTS-AES 256), D: Off", s.BitLockerSummary);

        Assert.True(s.TpmPresent); Assert.Equal("2.0", s.TpmVersion); Assert.Equal("INTC", s.TpmManufacturer);
        Assert.Equal("UEFI", s.FirmwareType); Assert.True(s.SecureBoot);
        Assert.True(s.UacEnabled); Assert.Equal("prompt for consent for non-Windows binaries", s.UacAdminPrompt); Assert.True(s.LocalAccountTokenFilterPolicy);
        Assert.True(s.RdpEnabled); Assert.True(s.RdpNlaRequired); Assert.Equal(3389, s.RdpPort);
        Assert.Equal("Enabled, NLA required", s.RdpSummary);
        Assert.False(s.Smb1Enabled); Assert.True(s.SmbSigningRequired); Assert.False(s.SmbEncryptData);
        Assert.Equal("VBS running", s.VbsStatus); Assert.True(s.CredentialGuardRunning); Assert.True(s.HvciRunning);
        Assert.True(s.LapsManaged); Assert.Equal("Windows LAPS (Active Directory)", s.LapsKind);
        Assert.Null(s.Notes);
        Assert.True(s.HasData);
    }

    [Fact]
    public async Task Security_ServerSku_NoSecurityCenter_SynthesisesDefenderAndNotes()
    {
        var wmi = new FakeWmiSession()
            .Throws("AntiVirusProduct", NotSupported())            // namespace absent on Server
            .With("MSFT_MpComputerStatus", Obj(("AntivirusEnabled", true), ("RealTimeProtectionEnabled", true), ("AntivirusSignatureAge", 12)))
            .Throws("Win32_EncryptableVolume", Denied())
            .Throws("Win32_Tpm", NotSupported())
            .With("MSFT_NetFirewallProfile", Obj(("Name", "Domain"), ("Enabled", 1)));
        var reg = new FakeRemoteRegistry { ThrowOnAccess = true };

        var m = new Machine("10.0.0.5");
        await new SecurityCollector().CollectAsync(Ctx(wmi, reg), m, default); // must not throw: some probes worked

        var av = Assert.Single(m.Antivirus);
        Assert.Equal("Windows Defender", av.Product);
        Assert.True(av.Enabled);
        Assert.False(av.UpToDate); // 12 days old signatures
        Assert.True(m.Security.FirewallDomain);
        Assert.Null(m.Security.TpmPresent);   // not determined
        Assert.Null(m.Security.SecureBoot);   // registry unavailable
        Assert.Contains("Security Center: not available", m.Security.Notes);
        Assert.Contains("BitLocker: access denied", m.Security.Notes);
    }

    [Fact]
    public async Task Security_TpmAbsent_IsFalseNotNull()
    {
        var wmi = new FakeWmiSession().With("Win32_Tpm"); // class exists, no instances
        var m = new Machine("10.0.0.6");
        await new SecurityCollector().CollectAsync(Ctx(wmi, new FakeRemoteRegistry { ThrowOnAccess = true }), m, default);
        Assert.False(m.Security.TpmPresent);
        Assert.Equal("Not present", m.Security.TpmSummary);
    }

    [Fact]
    public async Task Security_EverythingUnavailable_ThrowsFirstFailure()
    {
        var wmi = new FakeWmiSession()
            .Throws("AntiVirusProduct", NotSupported()).Throws("MSFT_MpComputerStatus", NotSupported())
            .Throws("MSFT_NetFirewallProfile", NotSupported()).Throws("Win32_EncryptableVolume", NotSupported())
            .Throws("Win32_Tpm", NotSupported()).Throws("Win32_DeviceGuard", NotSupported())
            .Throws("MSFT_SmbServerConfiguration", NotSupported()).Throws("Win32_OptionalFeature", NotSupported());
        var m = new Machine("10.0.0.7");
        var ex = await Assert.ThrowsAsync<WmiException>(() =>
            new SecurityCollector().CollectAsync(Ctx(wmi, new FakeRemoteRegistry { ThrowOnAccess = true }), m, default));
        Assert.Equal(WmiFailureKind.NotSupported, ex.Kind);
    }

    // ---------------- Users ----------------

    [Fact]
    public async Task Users_ReadsAccountsAdminsProfilesAndSessions()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_UserAccount",
                Obj(("Name", "Administrator"), ("SID", "S-1-5-21-1-500"), ("Disabled", true), ("PasswordRequired", true), ("PasswordExpires", false)),
                Obj(("Name", "jdoe"), ("FullName", "Jane Doe"), ("SID", "S-1-5-21-1-1001"), ("Disabled", false), ("Lockout", false), ("PasswordRequired", true), ("PasswordExpires", true)),
                Obj(("Name", "Guest"), ("SID", "S-1-5-21-1-501"), ("Disabled", true)))
            .With("Win32_Group", Obj(("Domain", "PC1"), ("Name", "Administrators")))
            .With("Win32_GroupUser",
                Obj(("PartComponent", "\\\\PC1\\root\\cimv2:Win32_UserAccount.Domain=\"PC1\",Name=\"jdoe\"")),
                Obj(("PartComponent", "\\\\PC1\\root\\cimv2:Win32_UserAccount.Domain=\"PC1\",Name=\"Administrator\"")),
                Obj(("PartComponent", "\\\\PC1\\root\\cimv2:Win32_Group.Domain=\"CORP\",Name=\"Domain Admins\"")))
            .With("Win32_UserProfile",
                Obj(("LocalPath", "C:\\Users\\jdoe"), ("SID", "S-1-5-21-1-1001"), ("LastUseTime", "20240801120000.000000+000"), ("Loaded", true)),
                Obj(("LocalPath", "C:\\Users\\old.user"), ("SID", "S-1-5-21-1-1002"), ("LastUseTime", "20230101120000.000000+000"), ("Loaded", false)))
            .With("Win32_LogonSession",
                Obj(("LogonId", "999"), ("LogonType", 2), ("StartTime", "20240801080000.000000+000")),
                Obj(("LogonId", "1234"), ("LogonType", 10), ("StartTime", "20240801090000.000000+000")))
            .With("Win32_LoggedOnUser",
                Obj(("Antecedent", "\\\\PC1\\root\\cimv2:Win32_Account.Domain=\"CORP\",Name=\"jdoe\""), ("Dependent", "\\\\PC1\\root\\cimv2:Win32_LogonSession.LogonId=\"999\"")),
                Obj(("Antecedent", "\\\\PC1\\root\\cimv2:Win32_Account.Domain=\"CORP\",Name=\"admin2\""), ("Dependent", "\\\\PC1\\root\\cimv2:Win32_LogonSession.LogonId=\"1234\"")),
                Obj(("Antecedent", "\\\\PC1\\root\\cimv2:Win32_Account.Domain=\"Window Manager\",Name=\"DWM-1\""), ("Dependent", "\\\\PC1\\root\\cimv2:Win32_LogonSession.LogonId=\"999\"")))
            .With("Win32_NetworkLoginProfile",
                Obj(("Name", "PC1\\jdoe"), ("LastLogon", "20240801080000.000000+000")),
                Obj(("Name", "NT AUTHORITY\\SYSTEM"), ("LastLogon", "20240801000000.000000+000")));

        var m = new Machine("10.0.0.8") { Name = "PC1" };
        await new UsersCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Equal(3, m.LocalAccounts.Count);
        var jdoe = m.LocalAccounts.Single(a => a.Name == "jdoe");
        Assert.True(jdoe.IsAdmin);
        Assert.Equal(new DateTime(2024, 8, 1, 8, 0, 0), jdoe.LastLogon);
        Assert.False(m.LocalAccounts.Single(a => a.Name == "Guest").IsAdmin);
        Assert.Contains("admin", jdoe.Flags);

        Assert.Equal(new[] { "CORP\\Domain Admins (group)", "PC1\\Administrator", "PC1\\jdoe" }, m.LocalAdministrators);
        Assert.Equal("CORP\\Domain Admins (group), PC1\\Administrator, PC1\\jdoe", m.LocalAdministratorsDisplay);

        Assert.Equal(2, m.UserProfiles.Count);
        Assert.Equal("jdoe", m.UserProfiles[0].User); // most recently used first
        Assert.True(m.UserProfiles[0].Loaded);

        Assert.Equal(2, m.LogonSessions.Count); // DWM filtered out
        Assert.Contains(m.LogonSessions, s => s.Account == "CORP\\jdoe" && s.LogonType == "Interactive");
        Assert.Contains(m.LogonSessions, s => s.Account == "CORP\\admin2" && s.LogonType == "RemoteInteractive");
        Assert.Equal("PC1\\jdoe", m.System.LastLoggedOnUser); // filled from login profiles, SYSTEM ignored
        Assert.Equal(3, m.LocalAccountCount);
    }

    [Fact]
    public async Task Users_DomainController_SkipsAccountEnumeration()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_UserAccount", Obj(("Name", "should-not-be-read")))
            .With("Win32_Group", Obj(("Domain", "CORP"), ("Name", "Administrators")))
            .With("Win32_GroupUser", Obj(("PartComponent", "Win32_UserAccount.Domain=\"CORP\",Name=\"Administrator\"")))
            .With("Win32_UserProfile");
        var m = new Machine("10.0.0.9");
        m.System.IsDomainController = true;
        await new UsersCollector().CollectAsync(Ctx(wmi), m, default);
        Assert.Empty(m.LocalAccounts);
        Assert.Equal(new[] { "CORP\\Administrator" }, m.LocalAdministrators);
    }

    [Theory]
    [InlineData("\\\\PC1\\root\\cimv2:Win32_UserAccount.Domain=\"PC1\",Name=\"jdoe\"", "PC1\\jdoe")]
    [InlineData("Win32_Group.Domain=\"CORP\",Name=\"Domain Admins\"", "CORP\\Domain Admins (group)")]
    [InlineData("garbage", null)]
    public void Users_ParsesAccountReferences(string reference, string? expected)
        => Assert.Equal(expected, UsersCollector.ParseAccountRef(reference));

    [Fact]
    public void Users_ProfileLeaf() => Assert.Equal("jdoe", UsersCollector.ProfileLeaf("C:\\Users\\jdoe\\"));

    // ---------------- Services / tasks ----------------

    [Fact]
    public async Task Services_ReadsServicesAndCountsAutoStopped()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_Service",
                Obj(("Name", "Spooler"), ("DisplayName", "Print Spooler"), ("State", "Running"), ("StartMode", "Auto"), ("StartName", "LocalSystem"), ("PathName", "C:\\Windows\\System32\\spoolsv.exe"), ("ProcessId", 1234)),
                Obj(("Name", "AcmeSvc"), ("DisplayName", "Acme Agent"), ("State", "Stopped"), ("StartMode", "Auto"), ("StartName", "CORP\\svc_acme"), ("ProcessId", 0)),
                Obj(("Name", "BITS"), ("DisplayName", "Background Intelligent Transfer"), ("State", "Stopped"), ("StartMode", "Manual")))
            .With("Win32_StartupCommand", Obj(("Name", "OneDrive"), ("Command", "\"C:\\x\\OneDrive.exe\" /background"), ("Location", "HKU\\S-1-5-21\\..\\Run"), ("User", "PC1\\jdoe")));

        var m = new Machine("10.0.0.10");
        await new ServicesCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Equal(3, m.Services.Count);
        Assert.Equal("Acme Agent", m.Services[0].DisplayName); // sorted by display name
        Assert.Null(m.Services.Single(s => s.Name == "AcmeSvc").ProcessId);
        Assert.Equal(1234, m.Services.Single(s => s.Name == "Spooler").ProcessId);
        Assert.Equal(3, m.ServiceCount);
        Assert.Equal(1, m.StoppedAutoServiceCount);
        Assert.Contains("1 running of 3", m.ServicesSummary);
        Assert.Contains("1 automatic but stopped", m.ServicesSummary);
        Assert.Single(m.StartupItems);
    }

    [Fact]
    public async Task ScheduledTasks_FiltersMicrosoftTree_AndReadsPrincipal()
    {
        var wmi = new FakeWmiSession().With("MSFT_ScheduledTask",
            Obj(("TaskName", "Backup"), ("TaskPath", "\\Acme\\"), ("State", 3), ("Author", "CORP\\admin"),
                ("Principal", Obj(("UserId", "CORP\\svc_backup"), ("RunLevel", 1)))),
            Obj(("TaskName", "Defrag"), ("TaskPath", "\\Microsoft\\Windows\\Defrag\\"), ("State", 3)),
            Obj(("TaskName", "Rooted"), ("TaskPath", "\\"), ("State", 1)));

        var m = new Machine("10.0.0.11");
        await new ScheduledTasksCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Equal(2, m.ScheduledTasks.Count);
        var backup = m.ScheduledTasks.Single(t => t.Name == "Backup");
        Assert.Equal("Ready", backup.State);
        Assert.Equal("CORP\\svc_backup", backup.RunAs);
        Assert.Equal("Disabled", m.ScheduledTasks.Single(t => t.Name == "Rooted").State);
    }

    // ---------------- Peripherals ----------------

    private static ushort[] Codes(string s) => s.Select(c => (ushort)c).Concat(new ushort[] { 0, 0 }).ToArray();

    [Fact]
    public async Task Peripherals_DecodesMonitorsGpuPrintersUsbBattery()
    {
        var wmi = new FakeWmiSession()
            .With("WmiMonitorID",
                Obj(("InstanceName", "DISPLAY\\DELA0C0\\1&2&0_0"), ("Active", true), ("ManufacturerName", Codes("DEL")), ("ProductCodeID", Codes("A0C0")),
                    ("SerialNumberID", Codes("ABC123")), ("UserFriendlyName", Codes("DELL U2415")), ("YearOfManufacture", 2019), ("WeekOfManufacture", 12)))
            .With("WmiMonitorBasicDisplayParams", Obj(("InstanceName", "DISPLAY\\DELA0C0\\1&2&0_0"), ("MaxHorizontalImageSize", 52), ("MaxVerticalImageSize", 32)))
            .With("Win32_VideoController", Obj(("Name", "NVIDIA RTX A2000"), ("AdapterRAM", (uint)4293918720), ("DriverVersion", "31.0.15.3699"),
                ("CurrentHorizontalResolution", 1920), ("CurrentVerticalResolution", 1200), ("CurrentRefreshRate", 60)))
            .With("Win32_Printer",
                Obj(("Name", "HP LaserJet"), ("Default", true), ("PortName", "IP_10.0.0.50"), ("DriverName", "HP Universal"), ("Shared", false), ("Network", false), ("PrinterStatus", 3)),
                Obj(("Name", "Microsoft Print to PDF"), ("Default", false), ("PortName", "PORTPROMPT:")))
            .With("Win32_TCPIPPrinterPort", Obj(("Name", "IP_10.0.0.50"), ("HostAddress", "10.0.0.50")))
            .With("Win32_PnPEntity",
                Obj(("Name", "USB Root Hub (USB 3.0)"), ("DeviceID", "USB\\ROOT_HUB30\\4&1"), ("Service", "USBHUB3")),
                Obj(("Name", "SanDisk Cruzer"), ("DeviceID", "USB\\VID_0781&PID_5567\\4C53"), ("Manufacturer", "SanDisk"), ("PNPClass", "USB"), ("Status", "OK")))
            .With("Win32_Battery", Obj(("Name", "DELL 1VX1H"), ("EstimatedChargeRemaining", 87), ("BatteryStatus", 2)))
            .With("BatteryFullChargedCapacity", Obj(("FullChargedCapacity", 45000)))
            .With("BatteryStaticData", Obj(("DesignedCapacity", 60000)))
            .With("BatteryCycleCount", Obj(("CycleCount", 210)))
            .With("MSAcpi_ThermalZoneTemperature", Obj(("InstanceName", "ACPI\\ThermalZone\\TZ00_0"), ("CurrentTemperature", 3132)));
        var reg = new FakeRemoteRegistry();
        reg.Subkeys["LocalMachine:SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}"] = new()
        {
            Key("0000", null, ("DriverDesc", "NVIDIA RTX A2000"), ("HardwareInformation.qwMemorySize", 6442450944L)),
        };

        var m = new Machine("10.0.0.12");
        await new PeripheralsCollector().CollectAsync(Ctx(wmi, reg), m, default);

        var mon = Assert.Single(m.Monitors);
        Assert.Equal("Dell", mon.Manufacturer);
        Assert.Equal("DELL U2415", mon.Model);
        Assert.Equal("ABC123", mon.Serial);
        Assert.Equal(2019, mon.Year);
        Assert.Equal(24.0, mon.DiagonalInches);
        Assert.Equal(1, m.MonitorCount);

        var gpu = Assert.Single(m.Gpus);
        Assert.Equal(6442450944L, gpu.VramBytes); // registry QWORD beat the capped AdapterRAM
        Assert.Equal("1920x1200 @ 60 Hz", gpu.Resolution);
        Assert.Equal("NVIDIA RTX A2000", m.PrimaryGpu);

        var printer = Assert.Single(m.Printers); // Print-to-PDF filtered out
        Assert.Equal("10.0.0.50", printer.HostAddress);
        Assert.True(printer.IsDefault);
        Assert.Equal("Idle", printer.Status);

        var usb = Assert.Single(m.UsbDevices); // root hub filtered out
        Assert.Equal("SanDisk Cruzer", usb.Name);

        Assert.NotNull(m.Battery);
        Assert.Equal(87, m.Battery!.ChargePercent);
        Assert.Equal(75, m.Battery.HealthPercent);
        Assert.Equal(210, m.Battery.CycleCount);
        Assert.Contains("health 75%", m.Battery.Summary);
        Assert.Equal(40, m.ThermalTempC); // 313.2 K
    }

    [Fact]
    public async Task Peripherals_NoBattery_LeavesNull_AndRootWmiDenied_IsANote()
    {
        var wmi = new FakeWmiSession()
            .Throws("WmiMonitorID", Denied())
            .With("Win32_VideoController", Obj(("Name", "Intel UHD")))
            .With("Win32_Battery")
            .Throws("MSAcpi_ThermalZoneTemperature", NotSupported());
        var m = new Machine("10.0.0.13");
        await new PeripheralsCollector().CollectAsync(Ctx(wmi), m, default);
        Assert.Null(m.Battery);
        Assert.Null(m.ThermalTempC);
        Assert.Single(m.Gpus);
        Assert.Empty(m.Monitors);
    }

    [Fact]
    public void Peripherals_PureHelpers()
    {
        Assert.Equal("Samsung", PeripheralsCollector.MonitorVendorName("SAM"));
        Assert.Equal("XYZ", PeripheralsCollector.MonitorVendorName("XYZ"));
        Assert.Null(PeripheralsCollector.DecodeWmiString(new ushort[] { 0, 65 }));
        Assert.Equal("AB", PeripheralsCollector.DecodeWmiString(new ushort[] { 65, 66, 0, 67 }));
        Assert.Null(PeripheralsCollector.DiagonalInches(0, 30));
        Assert.Equal(27.0, PeripheralsCollector.DiagonalInches(60, 34)!.Value, 0);
        Assert.Equal(100, PeripheralsCollector.BatteryHealth(70000, 60000)); // capped
        Assert.Null(PeripheralsCollector.BatteryHealth(null, 60000));
        Assert.Null(PeripheralsCollector.ThermalZoneCelsius(0));
        Assert.True(PeripheralsCollector.IsBuiltInVirtualPrinter("Microsoft XPS Document Writer"));
        Assert.False(PeripheralsCollector.IsBuiltInVirtualPrinter("HP LaserJet"));
        Assert.True(PeripheralsCollector.IsUsbInfrastructure("Generic USB Hub", null));
        Assert.True(PeripheralsCollector.IsUsbInfrastructure("Something", "usbccgp"));
        Assert.False(PeripheralsCollector.IsUsbInfrastructure("USB Input Device", "HidUsb"));
    }

    // ---------------- USB history ----------------

    [Fact]
    public async Task UsbHistory_ParsesDeviceKeysAndSerials()
    {
        var reg = new FakeRemoteRegistry();
        reg.SubkeyNames["LocalMachine:SYSTEM\\CurrentControlSet\\Enum\\USBSTOR"] = new()
            { "Disk&Ven_SanDisk&Prod_Cruzer_Glide&Rev_1.00", "Disk&Ven_&Prod_USB_DISK&Rev_" };
        reg.Subkeys["LocalMachine:SYSTEM\\CurrentControlSet\\Enum\\USBSTOR\\Disk&Ven_SanDisk&Prod_Cruzer_Glide&Rev_1.00"] = new()
            { Key("4C530001231212&0", null, ("FriendlyName", "SanDisk Cruzer Glide USB Device")) };
        reg.Subkeys["LocalMachine:SYSTEM\\CurrentControlSet\\Enum\\USBSTOR\\Disk&Ven_&Prod_USB_DISK&Rev_"] = new()
            { Key("7&1a2b3c&0", null, ("DeviceDesc", "@disk.inf,%disk_devdesc%;Disk drive")) };

        var m = new Machine("10.0.0.14");
        await new UsbHistoryCollector().CollectAsync(new InventoryContext(new FakeWmiSession(), reg), m, default);

        Assert.Equal(2, m.UsbStorageHistory.Count);
        var sandisk = m.UsbStorageHistory.Single(u => u.Vendor == "SanDisk");
        Assert.Equal("Cruzer Glide", sandisk.Product);
        Assert.Equal("4C530001231212", sandisk.Serial);
        Assert.Equal("SanDisk Cruzer Glide USB Device", sandisk.FriendlyName);
        var generic = m.UsbStorageHistory.Single(u => u.Vendor is null);
        Assert.Equal("USB DISK", generic.Product);
        Assert.Null(generic.Serial);              // generated instance ID, not a serial
        Assert.Equal("Disk drive", generic.FriendlyName);
    }

    // ---------------- Storage enrichment ----------------

    [Fact]
    public async Task Storage_EnrichesFromStorageManagementAndSmart()
    {
        var smart = new byte[512];
        smart[2 + 12 * 3] = 194;      // 4th attribute slot: id 194 (temperature)
        smart[2 + 12 * 3 + 5] = 41;   // raw byte 0 = 41 °C
        var wmi = new FakeWmiSession()
            .With("Win32_DiskDrive",
                Obj(("Index", 0), ("Model", "Samsung SSD 980"), ("Size", (ulong)1_000_000_000_000), ("MediaType", "Fixed hard disk media"),
                    ("SerialNumber", "S1"), ("Status", "OK"), ("InterfaceType", "SCSI"), ("Partitions", 3), ("PNPDeviceID", "SCSI\\DISK&VEN_NVME&PROD_SAMSUNG\\5&1")),
                Obj(("Index", 1), ("Model", "WDC Blue"), ("Size", (ulong)2_000_000_000_000), ("MediaType", "Fixed hard disk media"),
                    ("SerialNumber", "S2"), ("Status", "OK"), ("InterfaceType", "IDE"), ("PNPDeviceID", "SCSI\\DISK&VEN_WDC\\4&2")))
            .With("Win32_LogicalDisk", Obj(("DeviceID", "C:"), ("FileSystem", "NTFS"), ("Size", (ulong)900_000_000_000), ("FreeSpace", (ulong)300_000_000_000), ("VolumeName", "Windows")))
            .With("Win32_DiskPartition", Obj(("DiskIndex", 0), ("Type", "GPT: System")), Obj(("DiskIndex", 0), ("Type", "GPT: Basic Data")), Obj(("DiskIndex", 1), ("Type", "Installable File System")))
            .With("MSFT_PhysicalDisk",
                Obj(("DeviceId", "0"), ("MediaType", 4), ("BusType", 17), ("HealthStatus", 0), ("FirmwareVersion", "3B4QFXO7")),
                Obj(("DeviceId", "1"), ("MediaType", 3), ("BusType", 11), ("HealthStatus", 1)))
            .With("MSFT_StorageReliabilityCounter", Obj(("DeviceId", "0"), ("Temperature", 38), ("Wear", 2), ("PowerOnHours", 5120)))
            .With("MSStorageDriver_FailurePredictStatus", Obj(("InstanceName", "SCSI\\DISK&VEN_WDC\\4&2_0"), ("PredictFailure", true)))
            .With("MSStorageDriver_FailurePredictData", Obj(("InstanceName", "SCSI\\DISK&VEN_WDC\\4&2_0"), ("VendorSpecific", smart)));

        var m = new Machine("10.0.0.15");
        await new StorageCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Equal(2, m.Disks.Count);
        var ssd = m.Disks[0]; var hdd = m.Disks[1];
        Assert.Equal("SSD", ssd.MediaType); Assert.Equal("NVMe", ssd.BusType); Assert.Equal("Healthy", ssd.HealthStatus);
        Assert.Equal("3B4QFXO7", ssd.Firmware); Assert.Equal("GPT", ssd.PartitionStyle); Assert.Equal(3, ssd.Partitions);
        Assert.Equal(38, ssd.TempC); Assert.Equal(2, ssd.WearPercent); Assert.Equal(5120, ssd.PowerOnHours);
        Assert.Equal("SSD · NVMe · GPT", ssd.KindDisplay);
        Assert.Contains("38 °C", ssd.HealthDisplay);

        Assert.Equal("HDD", hdd.MediaType); Assert.Equal("SATA", hdd.BusType); Assert.Equal("Warning", hdd.HealthStatus);
        Assert.Equal("MBR", hdd.PartitionStyle);
        Assert.True(hdd.SmartPredictFailure); Assert.Equal("Pred Fail", hdd.SmartStatus);
        Assert.Equal(41, hdd.TempC); // from the raw SMART block
        Assert.Equal("Windows", m.Volumes[0].Label);
    }

    [Fact]
    public async Task Storage_WithoutEnrichmentClasses_StillWorks()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_DiskDrive", Obj(("Index", 0), ("Model", "Old disk"), ("Size", (ulong)500_000_000_000), ("MediaType", "Fixed hard disk media"), ("Status", "OK")))
            .With("Win32_LogicalDisk", Obj(("DeviceID", "C:"), ("FileSystem", "NTFS")))
            .Throws("MSFT_PhysicalDisk", Denied());
        var m = new Machine("10.0.0.16");
        await new StorageCollector().CollectAsync(Ctx(wmi), m, default);
        Assert.Equal("Fixed hard disk media", m.Disks[0].MediaType);
        Assert.Null(m.Disks[0].HealthStatus);
    }

    [Fact]
    public void Storage_SmartTemperature_PrefersAttribute194ThenAirflow()
    {
        var block = new byte[512];
        block[2] = 190; block[2 + 5] = 35;          // airflow
        Assert.Equal(35, StorageCollector.SmartTemperature(block));
        block[14] = 194; block[14 + 5] = 44;        // real temperature in the second slot wins
        Assert.Equal(44, StorageCollector.SmartTemperature(block));
        Assert.Null(StorageCollector.SmartTemperature(null));
        Assert.Null(StorageCollector.SmartTemperature(new byte[10]));
    }

    // ---------------- Catalogue / registry helpers ----------------

    [Fact]
    public void RegistryValues_CoerceBothAccessPathShapes()
    {
        Assert.Equal(1, RegistryValues.AsInt(1));         // OpenRemoteBaseKey DWORD
        Assert.Equal(1, RegistryValues.AsInt(1L));        // StdRegProv DWORD
        Assert.Equal(1, RegistryValues.AsInt("1"));
        Assert.True(RegistryValues.AsBool(1L));
        Assert.Null(RegistryValues.AsBool(null));
        Assert.Equal("a, b", RegistryValues.AsString(new[] { "a", "b" }));
        Assert.Null(RegistryValues.AsString("  "));
        Assert.Equal(6442450944L, RegistryValues.AsLong(6442450944L));
    }
}
