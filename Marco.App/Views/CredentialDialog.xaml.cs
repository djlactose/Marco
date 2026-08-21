using System.Security;
using System.Windows;
using System.Windows.Media;
using Marco.Core.Inventory;
using Marco.Credentials;

namespace Marco.App.Views;

public partial class CredentialDialog : Window
{
    private readonly CredentialVerifier? _verifier;
    private readonly CredentialSet? _editing;

    public CredentialSet? Result { get; private set; }

    private string? _verifiedSignature;
    private string? _emptyPasswordWarnedSignature;

    private sealed record ClientOption(string? Id, string Display) { public override string ToString() => Display; }

    public CredentialDialog(CredentialVerifier? verifier = null, string? defaultHost = null,
        CredentialKind defaultKind = CredentialKind.Windows, CredentialSet? editing = null,
        IReadOnlyList<Marco.Core.Clients.ClientProfile>? clients = null, string? activeClientId = null)
    {
        InitializeComponent();
        _verifier = verifier;
        _editing = editing;
        if (!string.IsNullOrWhiteSpace(defaultHost)) TestHostBox.Text = defaultHost;
        SetupCommands.Text = TargetEnablementScript;

        if (clients is { Count: > 0 })
        {
            ClientRow.Visibility = Visibility.Visible;
            var options = new List<ClientOption> { new(null, "(Shared — all clients)") };
            options.AddRange(clients.Select(c => new ClientOption(c.Id, c.Name)));
            ClientCombo.ItemsSource = options;
            var preselect = editing?.ClientId ?? activeClientId;
            ClientCombo.SelectedItem = options.FirstOrDefault(o =>
                string.Equals(o.Id, preselect, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }

        if (editing is not null) LoadFrom(editing);
        else if (defaultKind == CredentialKind.Linux) LinuxModeRadio.IsChecked = true;
        else if (defaultKind == CredentialKind.Snmp) SnmpModeRadio.IsChecked = true;
        ApplyMode();
        Loaded += (_, _) => LabelBox.Focus();
    }

    private string? SelectedClientId => (ClientCombo.SelectedItem as ClientOption)?.Id;

    public CredentialDialog() : this(null, null) { }

    private void LoadFrom(CredentialSet set)
    {
        Title = "Edit credential";
        OkButton.Content = "Save";
        if (set.Kind == CredentialKind.Linux) LinuxModeRadio.IsChecked = true;
        else if (set.Kind == CredentialKind.Snmp) SnmpModeRadio.IsChecked = true;
        LabelBox.Text = set.Label;
        DomainBox.Text = set.Domain ?? "";
        UserBox.Text = set.Username ?? "";
        PortBox.Text = set.SshPort.ToString();
        SnmpVersionCombo.SelectedIndex = set.SnmpVersion switch
        {
            Marco.Core.Snmp.SnmpVersion.V2c => 1,
            Marco.Core.Snmp.SnmpVersion.V1 => 2,
            _ => 0,
        };
        if (set.Password is { Length: > 0 }) PasswordKeepHint.Visibility = Visibility.Visible;
    }

    private bool IsLinux => LinuxModeRadio.IsChecked == true;
    private bool IsSnmp => SnmpModeRadio.IsChecked == true;

    private Marco.Core.Snmp.SnmpVersion? SelectedSnmpVersion => SnmpVersionCombo.SelectedIndex switch
    {
        1 => Marco.Core.Snmp.SnmpVersion.V2c,
        2 => Marco.Core.Snmp.SnmpVersion.V1,
        _ => null,
    };

    // The text lives in Core so the prerequisite doctor cites the same commands.
    public const string TargetEnablementScript = Marco.Core.Diagnosis.PrereqFixes.TargetEnablement;

    private void OnModeChanged(object sender, RoutedEventArgs e) => ApplyMode();

    private void ApplyMode()
    {
        if (!IsInitialized) return; // Checked fires during XAML parse before all controls exist
        var lin = IsLinux;
        var snmp = IsSnmp;
        DomainRow.Visibility = lin || snmp ? Visibility.Collapsed : Visibility.Visible;
        UserRow.Visibility = snmp ? Visibility.Collapsed : Visibility.Visible;
        PasswordLabel.Text = snmp ? "Community string" : "Password";
        SnmpVersionRow.Visibility = snmp ? Visibility.Visible : Visibility.Collapsed;
        PortRow.Visibility = lin ? Visibility.Visible : Visibility.Collapsed;
        WindowsPresets.Visibility = lin || snmp ? Visibility.Collapsed : Visibility.Visible;
        WindowsHelp.Visibility = lin || snmp ? Visibility.Collapsed : Visibility.Visible;
        LinuxHelp.Visibility = lin ? Visibility.Visible : Visibility.Collapsed;
        SnmpHelp.Visibility = snmp ? Visibility.Visible : Visibility.Collapsed;
        PresetHint.Visibility = Visibility.Collapsed;
        ResultText.Visibility = Visibility.Collapsed;
        _verifiedSignature = null;
        _emptyPasswordWarnedSignature = null;
    }

    // --- credential build ---

    private CredentialSet BuildSet()
    {
        var user = UserBox.Text.Trim();
        if (IsSnmp)
        {
            var version = SelectedSnmpVersion;
            var label = string.IsNullOrWhiteSpace(LabelBox.Text)
                ? "SNMP community" + (version is { } v ? $" ({(v == Marco.Core.Snmp.SnmpVersion.V1 ? "v1" : "v2c")})" : "")
                : LabelBox.Text.Trim();
            return new CredentialSet(label, null, null, ResolvePassword())
            {
                Kind = CredentialKind.Snmp,
                SnmpVersion = version,
                ClientId = SelectedClientId,
            };
        }
        if (IsLinux)
        {
            var port = TryGetPort(out var p) ? p : 22;
            // The list pill already says SSH, so the auto-label is just the user (plus port when non-default).
            var label = string.IsNullOrWhiteSpace(LabelBox.Text)
                ? (port == 22 ? user : $"{user}:{port}")
                : LabelBox.Text.Trim();
            return new CredentialSet(label, null, user, ResolvePassword())
            {
                Kind = CredentialKind.Linux,
                SshPort = port,
                ClientId = SelectedClientId,
            };
        }
        else
        {
            var label = string.IsNullOrWhiteSpace(LabelBox.Text)
                ? (string.IsNullOrWhiteSpace(DomainBox.Text) ? user : $"{DomainBox.Text}\\{user}")
                : LabelBox.Text.Trim();
            return new CredentialSet(label,
                string.IsNullOrWhiteSpace(DomainBox.Text) ? null : DomainBox.Text.Trim(),
                user, ResolvePassword())
            { Kind = CredentialKind.Windows, ClientId = SelectedClientId };
        }
    }

    /// <summary>The typed password, or (when editing with the box left blank) a copy of the existing one.</summary>
    private SecureString ResolvePassword()
    {
        var typed = PasswordBoxCtrl.SecurePassword;
        if (typed.Length > 0 || _editing?.Password is not { Length: > 0 }) return typed;
        typed.Dispose();
        return _editing.Password.Copy();
    }

    private int TypedPasswordLength()
    {
        using var s = PasswordBoxCtrl.SecurePassword;
        return s.Length;
    }

    private string Signature()
        => $"{IsLinux}|{IsSnmp}|{SnmpVersionCombo.SelectedIndex}|{DomainBox.Text}|{UserBox.Text}|{TypedPasswordLength()}|{TestHostBox.Text}|{PortBox.Text}";

    /// <summary>Valid port (or default 22 when blank / Windows mode); false when the text is not a usable port.</summary>
    private bool TryGetPort(out int port)
    {
        port = 22;
        var text = PortBox.Text.Trim();
        if (!IsLinux || text.Length == 0) return true;
        return int.TryParse(text, out port) && port is >= 1 and <= 65535;
    }

    // --- verify ---

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (IsSnmp && TypedPasswordLength() == 0 && _editing?.Password is not { Length: > 0 }) { ShowResult(VerifyOutcome.Error, "Enter a community string first.", null); return; }
        if (!IsSnmp && string.IsNullOrWhiteSpace(UserBox.Text)) { ShowResult(VerifyOutcome.Error, "Enter a username first.", null); return; }
        if (!TryGetPort(out _)) { ShowResult(VerifyOutcome.Error, "SSH port must be a whole number from 1 to 65535.", null); return; }
        var host = TestHostBox.Text.Trim();
        using var set = BuildSet();

        TestButton.IsEnabled = false;
        var old = TestButton.Content; TestButton.Content = "…";
        ShowResult(VerifyOutcome.Success, "Testing…", null, neutral: true);
        try
        {
            var result = await VerifyAsync(set, host);
            ShowResult(result.Outcome, result.Message, result.Hint);
            _verifiedSignature = result.Success ? Signature() : null;
        }
        finally { TestButton.Content = old; TestButton.IsEnabled = true; }
    }

    private async Task<VerifyResult> VerifyAsync(CredentialSet set, string host)
    {
        if (_verifier is null) return new VerifyResult(VerifyOutcome.Error, "Verification is unavailable.");
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));

        if (IsSnmp)
        {
            if (string.IsNullOrWhiteSpace(host)) return new VerifyResult(VerifyOutcome.Error, "Enter a printer or device address to test SNMP against.");
            return await _verifier.VerifySnmpHostAsync(set, host, cts.Token);
        }

        if (IsLinux)
        {
            if (string.IsNullOrWhiteSpace(host)) return new VerifyResult(VerifyOutcome.Error, "Enter a host to test SSH against.");
            return await _verifier.VerifyLinuxHostAsync(set, host, set.SshPort, cts.Token);
        }

        // Windows: real WMI test against a host, else a target-less LogonUser check.
        if (string.IsNullOrWhiteSpace(host))
        {
            var logon = CredentialVerifier.ValidateLogon(set);
            return logon.Success
                ? logon with { Message = logon.Message + " (account/password only — test on a host to confirm remote access)." }
                : logon;
        }
        return await _verifier.VerifyAgainstHostAsync(set, host, cts.Token);
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        if (IsSnmp)
        {
            if (TypedPasswordLength() == 0 && _editing?.Password is not { Length: > 0 })
            {
                ShowResult(VerifyOutcome.Error, "A community string is required.", null);
                PasswordBoxCtrl.Focus();
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(UserBox.Text))
        {
            ShowResult(VerifyOutcome.Error, "A username is required.", null);
            UserBox.Focus();
            return;
        }
        if (!TryGetPort(out _))
        {
            ShowResult(VerifyOutcome.Error, "SSH port must be a whole number from 1 to 65535.", null);
            PortBox.Focus();
            return;
        }

        // A blank password is almost always a mistake — warn once inline, proceed on the second click.
        // (When editing, blank means "keep the current password", which needs no warning.)
        var typedLen = TypedPasswordLength();
        var keepingExisting = typedLen == 0 && _editing?.Password is { Length: > 0 };
        if (typedLen == 0 && !keepingExisting && _emptyPasswordWarnedSignature != Signature())
        {
            _emptyPasswordWarnedSignature = Signature();
            ShowWarning($"Password is empty — it will be used as a blank password. Click {OkButton.Content} again to confirm.");
            PasswordBoxCtrl.Focus();
            return;
        }

        var host = TestHostBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(host) && _verifier is not null && _verifiedSignature != Signature())
        {
            using var probe = BuildSet();
            TestButton.IsEnabled = false;
            ShowResult(VerifyOutcome.Success, "Verifying before saving…", null, neutral: true);
            var result = await VerifyAsync(probe, host);
            TestButton.IsEnabled = true;
            if (!result.Success)
            {
                ShowResult(result.Outcome, result.Message, result.Hint);
                var msg = result.Message + (result.Hint is null ? "" : "\n\n" + result.Hint) + "\n\nAdd this credential anyway?";
                if (MessageBox.Show(msg, "Verification failed", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }
        }

        Result = BuildSet();
        DialogResult = true;
    }

    // --- Windows quick-fill presets ---

    private void OnPresetDomain(object sender, RoutedEventArgs e)
    {
        DomainBox.Text = "";
        ShowPresetHint("AD domain account: enter the NetBIOS domain (e.g. CORP) and your domain username. " +
                       "Domain accounts are exempt from UAC token filtering, so no target changes are needed.");
        DomainBox.Focus();
    }

    private void OnPresetMicrosoft(object sender, RoutedEventArgs e)
    {
        DomainBox.Text = "MicrosoftAccount";
        ShowPresetHint("Microsoft account: Username = your full email; Password = your account password (NOT a PIN). " +
                       "Also needs the target enablement below (it's a local account for filtering).");
        UserBox.Focus();
    }

    private void OnPresetEntra(object sender, RoutedEventArgs e)
    {
        DomainBox.Text = "AzureAD";
        ShowPresetHint("Entra/Intune (Azure AD): Username = user@domain. In practice a local admin per device is " +
                       "usually more reliable for WMI. Needs the target enablement below (or push it via Intune).");
        UserBox.Focus();
    }

    private void OnPresetLocal(object sender, RoutedEventArgs e)
    {
        DomainBox.Text = string.IsNullOrWhiteSpace(TestHostBox.Text) ? "" : TestHostBox.Text.Trim();
        ShowPresetHint("Local admin: put the TARGET computer name in Domain and the local username, e.g. PC01\\admin. " +
                       "Needs the target enablement below (UAC token filtering).");
        UserBox.Focus();
    }

    private void ShowPresetHint(string text)
    {
        PresetHint.Text = text;
        PresetHint.Visibility = Visibility.Visible;
    }

    private void OnCopySetup(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TargetEnablementScript); CopyHint.Text = "Copied."; }
        catch { CopyHint.Text = "Couldn't access the clipboard."; }
    }

    private void ShowWarning(string message)
    {
        ResultText.Visibility = Visibility.Visible;
        ResultText.Text = message;
        ResultText.Foreground = Brushes.DarkGoldenrod;
    }

    private void ShowResult(VerifyOutcome outcome, string message, string? hint, bool neutral = false)
    {
        ResultText.Visibility = Visibility.Visible;
        ResultText.Text = hint is null ? message : $"{message}\n{hint}";
        ResultText.Foreground = neutral ? Brushes.Gray
            : outcome == VerifyOutcome.Success ? Brushes.SeaGreen
            : outcome is VerifyOutcome.Unreachable or VerifyOutcome.Unsupported ? Brushes.DarkGoldenrod
            : Brushes.Firebrick;
    }
}
