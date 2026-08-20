using System.Diagnostics;
using System.Windows;
using Marco.Core.Actions;
using Marco.Core.Model;

namespace Marco.App.Services;

/// <summary>Launches the per-host actions (RDP, SSH, web admin, shares, ping, clipboard). Strictly
/// launch-and-navigate — nothing here changes a target's state. Every launch is wrapped so a missing tool or a
/// blocked shell verb reports to the status line instead of crashing the app.</summary>
public static class HostActions
{
    public static string Label(HostActionKind kind) => kind switch
    {
        HostActionKind.Rdp => "Remote Desktop",
        HostActionKind.Ssh => "SSH terminal",
        HostActionKind.WebAdminHttp => "Open web admin (http)",
        HostActionKind.WebAdminHttps => "Open web admin (https)",
        HostActionKind.AdminShare => "Open \\\\host\\c$",
        HostActionKind.ComputerManagement => "Computer Management",
        HostActionKind.Ping => "Ping (continuous)",
        HostActionKind.Traceroute => "Traceroute",
        HostActionKind.CopyIp => "Copy IP address",
        HostActionKind.CopyHostname => "Copy hostname",
        HostActionKind.CopyMac => "Copy MAC address",
        HostActionKind.CopySerial => "Copy serial number",
        HostActionKind.Wake => "Wake (Wake-on-LAN)",
        _ => kind.ToString(),
    };

    /// <summary>Run the action. Returns a short status message. Wake is handled by the view model (it needs the
    /// sender + rescan), so it is not launched here.</summary>
    public static string Launch(HostActionKind kind, Machine m)
    {
        try
        {
            switch (kind)
            {
                case HostActionKind.Rdp: Start("mstsc.exe", $"/v:{m.Address}"); return $"Opening Remote Desktop to {m.Address}…";
                case HostActionKind.Ssh: LaunchSsh(m.Address); return $"Opening SSH to {m.Address}…";
                case HostActionKind.WebAdminHttp: Shell($"http://{m.Address}"); return $"Opening http://{m.Address}…";
                case HostActionKind.WebAdminHttps: Shell($"https://{m.Address}"); return $"Opening https://{m.Address}…";
                case HostActionKind.AdminShare: Shell($@"\\{m.Address}\c$"); return $"Opening \\\\{m.Address}\\c$…";
                case HostActionKind.ComputerManagement: Start("mmc.exe", $"compmgmt.msc /computer:{m.Address}"); return $"Opening Computer Management for {m.Address}…";
                case HostActionKind.Ping: Start("cmd.exe", $"/k ping -t {m.Address}"); return $"Pinging {m.Address}…";
                case HostActionKind.Traceroute: Start("cmd.exe", $"/k tracert {m.Address}"); return $"Tracing route to {m.Address}…";
                case HostActionKind.CopyIp: return Copy(m.Address, "IP address");
                case HostActionKind.CopyHostname: return Copy(m.Name ?? m.Address, "hostname");
                case HostActionKind.CopyMac: return Copy(m.PrimaryMac ?? "", "MAC address");
                case HostActionKind.CopySerial: return Copy(m.System.SerialNumber ?? "", "serial number");
                default: return "";
            }
        }
        catch (Exception ex)
        {
            return $"Couldn't launch {Label(kind)}: {ex.Message}";
        }
    }

    private static void Start(string file, string args)
        => Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });

    private static void Shell(string target)
        => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    /// <summary>Prefer Windows Terminal; fall back to a cmd window running ssh.exe when wt isn't installed.</summary>
    private static void LaunchSsh(string address)
    {
        try { Process.Start(new ProcessStartInfo("wt.exe", $"ssh {address}") { UseShellExecute = false }); }
        catch { Process.Start(new ProcessStartInfo("cmd.exe", $"/k ssh {address}") { UseShellExecute = false }); }
    }

    private static string Copy(string text, string what)
    {
        if (string.IsNullOrEmpty(text)) return $"No {what} to copy.";
        try { Clipboard.SetText(text); return $"Copied {what}: {text}"; }
        catch { return $"Couldn't access the clipboard."; }
    }
}
