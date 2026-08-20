namespace Marco.Core.Diagnosis;

/// <summary>
/// The copy-paste remediation scripts the prerequisite doctor hands out. Text only — Marco never executes any
/// of these; the operator (or their RMM/GPO/Intune pipeline) does. Kept here so the credential dialog's help
/// block and the doctor cite the same commands.
/// </summary>
public static class PrereqFixes
{
    /// <summary>The full "enable a target" block (token filtering + WMI firewall group + Remote Registry) shown
    /// in the credential dialog.</summary>
    public const string TargetEnablement =
        "# Run elevated on each TARGET (or push via Intune):\r\n" +
        "reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f\r\n" +
        "netsh advfirewall firewall set rule group=\"windows management instrumentation (wmi)\" new enable=yes\r\n" +
        "Set-Service RemoteRegistry -StartupType Automatic; Start-Service RemoteRegistry";

    public const string TokenFiltering =
        "# Local admin over the network is filtered by UAC until this is set. Run elevated on the TARGET\r\n" +
        "# (or push via Intune/GPO as a Policy CSP / registry preference):\r\n" +
        "reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f";

    public const string FirewallWmi =
        "# Allow WMI (RPC 135 + dynamic ports) through the target's firewall. Run elevated on the TARGET,\r\n" +
        "# or enable the same rule group in GPO: Computer Configuration > Windows Settings > Security Settings >\r\n" +
        "# Windows Defender Firewall > Inbound Rules > 'Windows Management Instrumentation (WMI)'.\r\n" +
        "netsh advfirewall firewall set rule group=\"windows management instrumentation (wmi)\" new enable=yes";

    public const string RemoteRegistry =
        "# Software/updates/USB-history read the remote registry. Start the service on the TARGET (elevated),\r\n" +
        "# and allow SMB (TCP 445) through its firewall if it is blocked:\r\n" +
        "Set-Service RemoteRegistry -StartupType Automatic; Start-Service RemoteRegistry\r\n" +
        "netsh advfirewall firewall set rule group=\"File and Printer Sharing\" new enable=yes";
}
