using System.Windows;
using System.Windows.Media;
using Marco.Core.Inventory;
using Marco.Credentials;

namespace Marco.App.Views;

public partial class CredentialDialog : Window
{
    private readonly CredentialVerifier? _verifier;

    public CredentialSet? Result { get; private set; }

    private string? _verifiedSignature;

    public CredentialDialog(CredentialVerifier? verifier = null, string? defaultHost = null)
    {
        InitializeComponent();
        _verifier = verifier;
        if (!string.IsNullOrWhiteSpace(defaultHost)) TestHostBox.Text = defaultHost;
        SetupCommands.Text = TargetEnablementScript;
        ApplyMode();
        Loaded += (_, _) => LabelBox.Focus();
    }

    public CredentialDialog() : this(null, null) { }

    private bool IsLinux => LinuxModeRadio.IsChecked == true;

    public const string TargetEnablementScript =
        "# Run elevated on each TARGET (or push via Intune):\r\n" +
        "reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f\r\n" +
        "netsh advfirewall firewall set rule group=\"windows management instrumentation (wmi)\" new enable=yes\r\n" +
        "Set-Service RemoteRegistry -StartupType Automatic; Start-Service RemoteRegistry";

    private void OnModeChanged(object sender, RoutedEventArgs e) => ApplyMode();

    private void ApplyMode()
    {
        if (!IsInitialized) return; // Checked fires during XAML parse before all controls exist
        var lin = IsLinux;
        DomainRow.Visibility = lin ? Visibility.Collapsed : Visibility.Visible;
        PortRow.Visibility = lin ? Visibility.Visible : Visibility.Collapsed;
        WindowsPresets.Visibility = lin ? Visibility.Collapsed : Visibility.Visible;
        WindowsHelp.Visibility = lin ? Visibility.Collapsed : Visibility.Visible;
        LinuxHelp.Visibility = lin ? Visibility.Visible : Visibility.Collapsed;
        PresetHint.Visibility = Visibility.Collapsed;
        ResultText.Visibility = Visibility.Collapsed;
        _verifiedSignature = null;
    }

    // --- credential build ---

    private CredentialSet BuildSet()
    {
        var user = UserBox.Text.Trim();
        if (IsLinux)
        {
            var label = string.IsNullOrWhiteSpace(LabelBox.Text) ? $"{user} (Linux)" : LabelBox.Text.Trim();
            return new CredentialSet(label, null, user, PasswordBoxCtrl.SecurePassword)
            {
                Kind = CredentialKind.Linux,
                SshPort = int.TryParse(PortBox.Text, out var p) && p > 0 ? p : 22,
            };
        }
        else
        {
            var label = string.IsNullOrWhiteSpace(LabelBox.Text)
                ? (string.IsNullOrWhiteSpace(DomainBox.Text) ? user : $"{DomainBox.Text}\\{user}")
                : LabelBox.Text.Trim();
            return new CredentialSet(label,
                string.IsNullOrWhiteSpace(DomainBox.Text) ? null : DomainBox.Text.Trim(),
                user, PasswordBoxCtrl.SecurePassword)
            { Kind = CredentialKind.Windows };
        }
    }

    private string Signature()
        => $"{IsLinux}|{DomainBox.Text}|{UserBox.Text}|{PasswordBoxCtrl.SecurePassword.Length}|{TestHostBox.Text}|{PortBox.Text}";

    private int Port() => int.TryParse(PortBox.Text, out var p) && p > 0 ? p : 22;

    // --- verify ---

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserBox.Text)) { ShowResult(VerifyOutcome.Error, "Enter a username first.", null); return; }
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

        if (IsLinux)
        {
            if (string.IsNullOrWhiteSpace(host)) return new VerifyResult(VerifyOutcome.Error, "Enter a host to test SSH against.");
            return await _verifier.VerifyLinuxHostAsync(set, host, Port(), cts.Token);
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
        if (string.IsNullOrWhiteSpace(UserBox.Text))
        {
            MessageBox.Show("A username is required.", "Add credential", MessageBoxButton.OK, MessageBoxImage.Warning);
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
