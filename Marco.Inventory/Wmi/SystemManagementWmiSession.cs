using System.Management;
using System.Runtime.InteropServices;
using Marco.Core.Wmi;

namespace Marco.Inventory.Wmi;

/// <summary>
/// <see cref="IWmiSession"/> over <c>System.Management</c> (ManagementScope + ConnectionOptions) — the proven
/// path for remote DCOM WMI with alternate credentials on .NET 8. The synchronous searcher is marshaled onto the
/// thread pool. Two foot-guns are handled here: a connect timeout (so a firewalled-but-pingable host fails fast),
/// and the local-host rule (System.Management throws if credentials are supplied for the local machine).
/// </summary>
public sealed class SystemManagementWmiSession : IWmiSession
{
    private readonly WmiCredential? _credential;
    private readonly ConnectionOptions? _options;
    private readonly Dictionary<string, ManagementScope> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _timeoutSeconds;
    private bool _disposed;

    public string Host { get; }

    internal SystemManagementWmiSession(string host, WmiCredential? credential, int timeoutSeconds)
    {
        Host = host;
        _credential = credential;
        _timeoutSeconds = timeoutSeconds;
        _options = BuildOptions(host, credential, timeoutSeconds);
    }

    private static bool IsLocal(string host) =>
        string.IsNullOrEmpty(host) || host is "." or "localhost" or "127.0.0.1" or "::1"
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        || host.Equals(System.Net.Dns.GetHostName(), StringComparison.OrdinalIgnoreCase);

    private static ConnectionOptions? BuildOptions(string host, WmiCredential? cred, int timeoutSeconds)
    {
        // Local host: MUST pass null options — System.Management throws if credentials target the local machine.
        if (IsLocal(host) || cred is null || cred.UseCurrentToken)
            return new ConnectionOptions
            {
                Impersonation = ImpersonationLevel.Impersonate,
                Authentication = AuthenticationLevel.PacketPrivacy,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                EnablePrivileges = true,
            };

        var username = string.IsNullOrEmpty(cred.Domain) ? cred.Username : $"{cred.Domain}\\{cred.Username}";
        return new ConnectionOptions
        {
            Username = username,
            SecurePassword = cred.Password,
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            EnablePrivileges = true,
        };
    }

    /// <summary>Eagerly connect the default namespace so authentication failures surface at connect time.</summary>
    internal void Connect(string @namespace = "root\\cimv2") => GetScope(@namespace);

    private ManagementScope GetScope(string @namespace)
    {
        if (_scopes.TryGetValue(@namespace, out var cached)) return cached;

        var path = IsLocal(Host) ? @namespace : $"\\\\{Host}\\{@namespace}";
        var scope = new ManagementScope(path, _options);
        try
        {
            scope.Connect();
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
        _scopes[@namespace] = scope;
        return scope;
    }

    public Task<IReadOnlyList<WmiObject>> QueryAsync(string @namespace, string wql, CancellationToken ct)
        => Task.Run<IReadOnlyList<WmiObject>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var scope = GetScope(@namespace);
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
                using var results = searcher.Get();
                var list = new List<WmiObject>();
                foreach (ManagementBaseObject mo in results)
                {
                    ct.ThrowIfCancellationRequested();
                    using (mo)
                        list.Add(ToWmiObject(mo));
                }
                return list;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw Translate(ex); }
        }, ct);

    public Task<WmiObject?> InvokeAsync(
        string @namespace, string className, string methodName,
        IReadOnlyDictionary<string, object?> args, CancellationToken ct)
        => Task.Run<WmiObject?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var scope = GetScope(@namespace);
            try
            {
                using var cls = new ManagementClass(scope, new ManagementPath(className), null);
                using var inParams = cls.GetMethodParameters(methodName);
                foreach (var (k, v) in args)
                    inParams[k] = v;
                using var outParams = cls.InvokeMethod(methodName, inParams, null);
                return outParams is null ? null : ToWmiObject(outParams);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw Translate(ex); }
        }, ct);

    private static WmiObject ToWmiObject(ManagementBaseObject mo)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyData prop in mo.Properties)
            dict[prop.Name] = prop.Value;
        return new WmiObject(dict);
    }

    /// <summary>Map System.Management/COM failures onto the typed <see cref="WmiException"/> the runner reacts to,
    /// including the local-account UAC hint for a denied local credential.</summary>
    private WmiException Translate(Exception ex)
    {
        // Access denied — the common credential/UAC case.
        bool denied =
            (ex is ManagementException me && me.ErrorCode == ManagementStatus.AccessDenied)
            || (ex is UnauthorizedAccessException)
            || (ex is COMException ce && ((uint)ce.HResult is 0x80070005 or 0x80041003));

        if (denied)
        {
            string? hint = null;
            if (_credential is not null && _credential.IsLocalAccount(Host))
            {
                hint = "Access was denied for a local account. On the target, UAC remote token filtering blocks "
                     + "local admins over the network. Set HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\"
                     + "Policies\\System\\LocalAccountTokenFilterPolicy = 1 (DWORD), or use a domain account.";
                if (_credential.IsMicrosoftAccount)
                {
                    hint += " For a Microsoft account, use the username form MicrosoftAccount\\your-email@example.com "
                          + "with your account PASSWORD (a Windows Hello PIN will not work over the network), and note "
                          + "that the account must exist as an admin on the TARGET machine — Microsoft accounts do not "
                          + "roam between machines the way domain accounts do.";
                }
            }
            var kind = _credential is null || _credential.UseCurrentToken
                ? WmiFailureKind.AccessDenied
                : WmiFailureKind.AuthFailed;
            return new WmiException(kind, "Access denied connecting to WMI.", hint, ex);
        }

        if (ex is COMException rpc)
        {
            uint h = (uint)rpc.HResult;
            if (h == 0x800706BA) return new WmiException(WmiFailureKind.Unreachable, "RPC server unavailable (DCOM/firewall).", null, ex);
            if (h == 0x800705B4) return new WmiException(WmiFailureKind.Timeout, "WMI operation timed out.", null, ex);
        }

        if (ex is ManagementException m2)
        {
            if (m2.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.InvalidNamespace or ManagementStatus.NotFound)
                return new WmiException(WmiFailureKind.NotSupported, $"WMI class/namespace not present: {m2.Message}", null, ex);
        }

        return new WmiException(WmiFailureKind.Unknown, ex.Message, null, ex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scopes.Clear();
        // Note: the credential's SecureString is owned by the credential store (shared across hosts), not disposed here.
    }
}

/// <summary>Opens <see cref="SystemManagementWmiSession"/> instances, connecting eagerly so bad credentials fail here.</summary>
public sealed class SystemManagementWmiSessionFactory : IWmiSessionFactory
{
    private readonly int _timeoutSeconds;

    public SystemManagementWmiSessionFactory(int timeoutSeconds = 20) => _timeoutSeconds = timeoutSeconds;

    public Task<IWmiSession> ConnectAsync(string host, WmiCredential? credential, CancellationToken ct)
        => Task.Run<IWmiSession>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var session = new SystemManagementWmiSession(host, credential, _timeoutSeconds);
            session.Connect(); // validate auth/reachability up front
            return session;
        }, ct);
}
