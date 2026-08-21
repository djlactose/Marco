using System.Net;
using System.Runtime.InteropServices;
using Marco.Core.Ssh;
using Marco.Core.Wmi;

namespace Marco.Credentials;

public enum VerifyOutcome { Success, BadCredentials, AccessDenied, Unreachable, Unsupported, Error }

/// <summary>Result of verifying a credential set, with an operator-facing message and optional remediation hint.</summary>
public sealed record VerifyResult(VerifyOutcome Outcome, string Message, string? Hint = null)
{
    public bool Success => Outcome == VerifyOutcome.Success;
}

/// <summary>
/// Verifies a credential set two ways:
///  • <see cref="VerifyAgainstHostAsync"/> — the authentic test: a real WMI connect via the same factory inventory
///    uses, so it proves the account can actually do what Marco needs and surfaces the exact failure (including the
///    LocalAccountTokenFilterPolicy hint for a denied local admin).
///  • <see cref="ValidateLogon"/> — a target-less check via LogonUser(NETWORK). Meaningful for DOMAIN accounts
///    (validated against the domain); for a local account it only checks the operator's own machine, so it can't
///    confirm remote access — the host test is required there.
/// </summary>
public sealed class CredentialVerifier
{
    private readonly IWmiSessionFactory _factory;
    private readonly ISshSessionFactory? _ssh;
    private readonly Marco.Core.Snmp.ISnmpSessionFactory? _snmp;

    public CredentialVerifier(IWmiSessionFactory factory, ISshSessionFactory? ssh = null, Marco.Core.Snmp.ISnmpSessionFactory? snmp = null)
    {
        _factory = factory;
        _ssh = ssh;
        _snmp = snmp;
    }

    /// <summary>Verify an SNMP community string by probing the device's system group — the same exchange the
    /// printer/network-device runner opens with, so a green result means inventory will read it.</summary>
    public async Task<VerifyResult> VerifySnmpHostAsync(CredentialSet set, string host, CancellationToken ct)
    {
        if (_snmp is null) return new VerifyResult(VerifyOutcome.Error, "SNMP verification is unavailable.");
        if (string.IsNullOrWhiteSpace(host)) return new VerifyResult(VerifyOutcome.Error, "Enter a host to test against.");
        var community = set.Password is null ? "" : new NetworkCredential(string.Empty, set.Password).Password;
        if (community.Length == 0) return new VerifyResult(VerifyOutcome.Error, "A community string is required.");
        try
        {
            var r = await Marco.Core.Snmp.SnmpProbe.ProbeAsync(_snmp, host.Trim(), 161, community, set.SnmpVersion,
                new Marco.Core.Snmp.SnmpOptions(TimeoutMs: 2500, Retries: 1), ct).ConfigureAwait(false);
            if (r is null)
                return new VerifyResult(VerifyOutcome.BadCredentials,
                    $"No SNMP response from {host}.",
                    "SNMP drops requests with a wrong community string silently, so this is either a wrong community, SNMP v1/v2c disabled on the device, or a firewall. Check the device's web admin page under Network → SNMP.");
            var ver = r.Version == Marco.Core.Snmp.SnmpVersion.V1 ? "v1" : "v2c";
            var descr = r.Description;
            return new VerifyResult(VerifyOutcome.Success,
                descr is { Length: > 0 } ? $"Responded over SNMP {ver}: {(descr.Length > 90 ? descr[..90] + "…" : descr)}" : $"Responded over SNMP {ver}.");
        }
        catch (OperationCanceledException)
        {
            return new VerifyResult(VerifyOutcome.Error, "Verification cancelled or timed out.");
        }
        catch (Marco.Core.Snmp.SnmpException ex) when (ex.Kind == Marco.Core.Snmp.SnmpFailureKind.PortUnreachable)
        {
            return new VerifyResult(VerifyOutcome.Unreachable, $"{host} refuses UDP 161 — SNMP is switched off on the device.",
                "Enable SNMP v1/v2c (read-only) in the device's web admin page, typically under Network → SNMP.");
        }
        catch (Exception ex)
        {
            return new VerifyResult(VerifyOutcome.Error, $"SNMP: {ex.Message}");
        }
    }

    /// <summary>Verify a Linux credential by opening a real SSH session and running a trivial command. Uses the
    /// same SSH path inventory uses, so a green result means inventory will authenticate.</summary>
    public async Task<VerifyResult> VerifyLinuxHostAsync(CredentialSet set, string host, int port, CancellationToken ct)
    {
        if (_ssh is null) return new VerifyResult(VerifyOutcome.Error, "SSH verification is unavailable.");
        if (string.IsNullOrWhiteSpace(host)) return new VerifyResult(VerifyOutcome.Error, "Enter a host to test against.");
        if (string.IsNullOrWhiteSpace(set.Username)) return new VerifyResult(VerifyOutcome.Error, "A username is required.");

        var pw = set.Password is null ? "" : new NetworkCredential(string.Empty, set.Password).Password;
        try
        {
            using var session = await Task.Run(() =>
                _ssh.Connect(host.Trim(), port <= 0 ? 22 : port, set.Username!, pw, 15, ct), ct).ConfigureAwait(false);
            var res = session.Run("uname -sr", ct);
            var detail = res.StdOut.Trim();
            return new VerifyResult(VerifyOutcome.Success,
                detail.Length > 0 ? $"Authenticated to {host} over SSH ({detail})." : $"Authenticated to {host} over SSH.");
        }
        catch (OperationCanceledException)
        {
            return new VerifyResult(VerifyOutcome.Error, "Verification cancelled or timed out.");
        }
        catch (SshException ex)
        {
            var outcome = ex.Kind switch
            {
                SshFailureKind.AuthFailed => VerifyOutcome.BadCredentials,
                SshFailureKind.Unreachable => VerifyOutcome.Unreachable,
                SshFailureKind.Timeout => VerifyOutcome.Unreachable,
                _ => VerifyOutcome.Error,
            };
            return new VerifyResult(outcome, $"SSH: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new VerifyResult(VerifyOutcome.Error, ex.Message);
        }
    }

    public async Task<VerifyResult> VerifyAgainstHostAsync(CredentialSet set, string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new VerifyResult(VerifyOutcome.Error, "Enter a host to test against.");

        // Local WMI ignores alternate credentials (it always uses the current token), so verifying a supplied
        // credential against this machine would falsely succeed. Validate the account/password via LogonUser
        // instead and steer the operator to a remote host for a true inventory-access test.
        if (IsLocalHost(host) && !set.IsCurrentToken)
        {
            var logon = ValidateLogon(set);
            const string note = " Note: this is the local machine — WMI can't test alternate credentials here, so " +
                                "the account/password was validated instead. Test against a REMOTE host to confirm inventory access.";
            return logon.Success ? logon with { Message = logon.Message + note } : logon;
        }

        try
        {
            using var session = await _factory.ConnectAsync(host.Trim(), set.ToWmiCredential(), ct).ConfigureAwait(false);
            // Connect authenticated. Prove we can actually read by running one cheap query.
            await session.QueryAsync("root\\cimv2", "SELECT Name FROM Win32_ComputerSystem", ct).ConfigureAwait(false);
            return new VerifyResult(VerifyOutcome.Success, $"Authenticated to {host} and read WMI successfully.");
        }
        catch (OperationCanceledException)
        {
            return new VerifyResult(VerifyOutcome.Error, "Verification cancelled or timed out.");
        }
        catch (WmiException wex)
        {
            var outcome = wex.Kind switch
            {
                WmiFailureKind.AuthFailed => VerifyOutcome.BadCredentials,
                WmiFailureKind.AccessDenied => VerifyOutcome.AccessDenied,
                WmiFailureKind.Unreachable => VerifyOutcome.Unreachable,
                WmiFailureKind.Timeout => VerifyOutcome.Unreachable,
                WmiFailureKind.NotSupported => VerifyOutcome.Unsupported,
                _ => VerifyOutcome.Error,
            };
            return new VerifyResult(outcome, wex.Message, wex.Hint);
        }
        catch (Exception ex)
        {
            return new VerifyResult(VerifyOutcome.Error, ex.Message);
        }
    }

    /// <summary>Validate the account+password with LogonUser(LOGON32_LOGON_NETWORK). No target needed; runs
    /// unelevated. Best for domain accounts — a local account is only checked against this machine.</summary>
    public static VerifyResult ValidateLogon(CredentialSet set)
    {
        if (set.IsCurrentToken)
            return new VerifyResult(VerifyOutcome.Success, "Current session token — no password to validate.");
        if (string.IsNullOrWhiteSpace(set.Username))
            return new VerifyResult(VerifyOutcome.Error, "A username is required.");
        if (set.Password is null)
            return new VerifyResult(VerifyOutcome.BadCredentials, "No password entered.");

        IntPtr pwd = IntPtr.Zero, token = IntPtr.Zero;
        try
        {
            pwd = Marshal.SecureStringToGlobalAllocUnicode(set.Password);
            bool ok = LogonUser(set.Username, string.IsNullOrWhiteSpace(set.Domain) ? "." : set.Domain,
                pwd, LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, out token);
            if (ok)
                return new VerifyResult(VerifyOutcome.Success, "Account and password are valid.");

            int err = Marshal.GetLastWin32Error();
            return err switch
            {
                1326 => new VerifyResult(VerifyOutcome.BadCredentials, "Wrong username or password."),
                1327 => new VerifyResult(VerifyOutcome.BadCredentials, "Account restriction (e.g. blank password not allowed, or logon-hours)."),
                1330 => new VerifyResult(VerifyOutcome.BadCredentials, "The password has expired."),
                1331 => new VerifyResult(VerifyOutcome.BadCredentials, "The account is disabled."),
                1793 => new VerifyResult(VerifyOutcome.BadCredentials, "The account has expired."),
                1385 => new VerifyResult(VerifyOutcome.AccessDenied, "The account is not granted the requested logon type."),
                _ => new VerifyResult(VerifyOutcome.Error, $"Logon failed (Win32 error {err})."),
            };
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            if (pwd != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pwd);
        }
    }

    private static bool IsLocalHost(string host)
    {
        host = host.Trim();
        if (string.IsNullOrEmpty(host) || host is "." or "localhost" or "127.0.0.1" or "::1") return true;
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        try { return host.Equals(System.Net.Dns.GetHostName(), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string username, string? domain, IntPtr password,
        int logonType, int logonProvider, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
