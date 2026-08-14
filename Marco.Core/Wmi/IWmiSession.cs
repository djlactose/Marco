using System.Security;

namespace Marco.Core.Wmi;

/// <summary>Minimal credential for a WMI/registry connection. The richer credential-set model (labels, ordering,
/// persistence) lives in Marco.Credentials and projects down to this when connecting.</summary>
public sealed class WmiCredential
{
    public string? Domain { get; init; }
    public string? Username { get; init; }
    public SecureString? Password { get; init; }

    /// <summary>Use the current process/session token (no explicit credentials) — required for runas /netonly.</summary>
    public bool UseCurrentToken { get; init; }

    public static WmiCredential CurrentToken => new() { UseCurrentToken = true };

    /// <summary>True when this credential is subject to remote UAC token filtering — a local account (no domain,
    /// or domain == host), OR a Microsoft-account / Azure-AD account, which are local principals for filtering
    /// purposes. Used to attach the LocalAccountTokenFilterPolicy hint on access-denied.</summary>
    public bool IsLocalAccount(string host)
    {
        if (UseCurrentToken) return false;
        if (string.IsNullOrEmpty(Domain)) return true;
        if (IsPseudoLocalDomain(Domain)) return true; // MicrosoftAccount\… / AzureAD\…
        return Domain.Equals(host, StringComparison.OrdinalIgnoreCase)
            || Domain == "." || Domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for the special "domains" of Microsoft-account and Azure-AD principals, which are not classic
    /// AD domains and are subject to local-account token filtering.</summary>
    public bool IsMicrosoftAccount =>
        !string.IsNullOrEmpty(Domain) && Domain.Equals("MicrosoftAccount", StringComparison.OrdinalIgnoreCase);

    public static bool IsPseudoLocalDomain(string? domain) =>
        domain is not null &&
        (domain.Equals("MicrosoftAccount", StringComparison.OrdinalIgnoreCase)
         || domain.Equals("AzureAD", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Distinguishes the failure modes a collector/orchestrator must react to differently.</summary>
public enum WmiFailureKind { Unknown, AuthFailed, AccessDenied, Unreachable, Timeout, NotSupported }

/// <summary>Typed WMI failure carrying a classified <see cref="Kind"/> and an operator-facing hint when relevant
/// (e.g. the LocalAccountTokenFilterPolicy guidance for a denied local account).</summary>
public sealed class WmiException : Exception
{
    public WmiFailureKind Kind { get; }
    public string? Hint { get; }

    public WmiException(WmiFailureKind kind, string message, string? hint = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Hint = hint;
    }
}

/// <summary>An authenticated WMI/CIM session to one host. Query is the workhorse; Invoke covers method calls
/// (e.g. StdRegProv). Implementations marshal the synchronous System.Management API onto the thread pool.</summary>
public interface IWmiSession : IDisposable
{
    string Host { get; }

    Task<IReadOnlyList<WmiObject>> QueryAsync(string @namespace, string wql, CancellationToken ct);

    Task<WmiObject?> InvokeAsync(
        string @namespace, string className, string methodName,
        IReadOnlyDictionary<string, object?> args, CancellationToken ct);
}

/// <summary>Opens sessions. Connecting is where authentication actually happens, so a bad credential set fails here.</summary>
public interface IWmiSessionFactory
{
    Task<IWmiSession> ConnectAsync(string host, WmiCredential? credential, CancellationToken ct);
}
