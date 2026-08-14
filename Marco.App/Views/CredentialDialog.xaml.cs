using System.Windows;
using System.Windows.Media;
using Marco.Credentials;

namespace Marco.App.Views;

public partial class CredentialDialog : Window
{
    private readonly CredentialVerifier? _verifier;

    public CredentialSet? Result { get; private set; }

    /// <summary>Tracks a successful host verification so Add doesn't re-test unnecessarily. Reset when the inputs change.</summary>
    private string? _verifiedSignature;

    public CredentialDialog(CredentialVerifier? verifier = null, string? defaultHost = null)
    {
        InitializeComponent();
        _verifier = verifier;
        if (!string.IsNullOrWhiteSpace(defaultHost)) TestHostBox.Text = defaultHost;
        SetupCommands.Text = TargetEnablementScript;
        Loaded += (_, _) => LabelBox.Focus();
    }

    // Parameterless ctor for XAML/designer.
    public CredentialDialog() : this(null, null) { }

    /// <summary>The three commands that make a target reachable for authenticated inventory. Also deployable via Intune.</summary>
    public const string TargetEnablementScript =
        "# Run elevated on each TARGET (or push via Intune):\r\n" +
        "reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f\r\n" +
        "netsh advfirewall firewall set rule group=\"windows management instrumentation (wmi)\" new enable=yes\r\n" +
        "Set-Service RemoteRegistry -StartupType Automatic; Start-Service RemoteRegistry";

    // --- Quick-fill presets ---

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
        // Prefill the domain with the test host (or leave blank) — a local account authenticates as HOST\user.
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
        try
        {
            Clipboard.SetText(TargetEnablementScript);
            CopyHint.Text = "Copied.";
        }
        catch
        {
            CopyHint.Text = "Couldn't access the clipboard.";
        }
    }

    private CredentialSet BuildSet()
    {
        var user = UserBox.Text.Trim();
        var label = string.IsNullOrWhiteSpace(LabelBox.Text)
            ? (string.IsNullOrWhiteSpace(DomainBox.Text) ? user : $"{DomainBox.Text}\\{user}")
            : LabelBox.Text.Trim();
        return new CredentialSet(label,
            string.IsNullOrWhiteSpace(DomainBox.Text) ? null : DomainBox.Text.Trim(),
            user, PasswordBoxCtrl.SecurePassword);
    }

    private string Signature() => $"{DomainBox.Text}|{UserBox.Text}|{PasswordBoxCtrl.SecurePassword.Length}|{TestHostBox.Text}";

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserBox.Text))
        {
            ShowResult(VerifyOutcome.Error, "Enter a username first.", null);
            return;
        }

        var host = TestHostBox.Text.Trim();
        using var set = BuildSet();

        TestButton.IsEnabled = false;
        var oldContent = TestButton.Content;
        TestButton.Content = "…";
        ShowResult(VerifyOutcome.Success, "Testing…", null, neutral: true);
        try
        {
            VerifyResult result;
            if (string.IsNullOrWhiteSpace(host))
            {
                // No host: fall back to a target-less account/password validation (best for domain accounts).
                result = CredentialVerifier.ValidateLogon(set);
                if (result.Success)
                    result = result with { Message = result.Message + " (account/password only — test on a host to confirm remote access)." };
            }
            else if (_verifier is not null)
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                result = await _verifier.VerifyAgainstHostAsync(set, host, cts.Token);
            }
            else
            {
                result = new VerifyResult(VerifyOutcome.Error, "Verification is unavailable.");
            }

            ShowResult(result.Outcome, result.Message, result.Hint);
            _verifiedSignature = result.Success ? Signature() : null;
        }
        finally
        {
            TestButton.Content = oldContent;
            TestButton.IsEnabled = true;
        }
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var user = UserBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(user))
        {
            MessageBox.Show("A username is required.", "Add credential", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // If a test host is given and these exact inputs haven't already verified, verify now before saving.
        var host = TestHostBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(host) && _verifier is not null && _verifiedSignature != Signature())
        {
            using var probe = BuildSet();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            TestButton.IsEnabled = false;
            ShowResult(VerifyOutcome.Success, "Verifying before saving…", null, neutral: true);
            var result = await _verifier.VerifyAgainstHostAsync(probe, host, cts.Token);
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
