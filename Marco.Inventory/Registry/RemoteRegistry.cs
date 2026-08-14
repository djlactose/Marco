using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Marco.Core.Inventory;
using Marco.Core.Wmi;

namespace Marco.Inventory.Registry;

/// <summary>
/// Remote registry reader over the preferred path: a WNetAddConnection2 SMB session to \\host\IPC$ with the
/// operator's credentials, then RegistryKey.OpenRemoteBaseKey (which rides that session — it can't take
/// alternate credentials itself, which is exactly why the WNet step is mandatory). Bulk reads, last-write times.
/// The local host is special-cased to skip WNet entirely. Requires the Remote Registry service on the target.
/// </summary>
public sealed partial class RemoteRegistry : IRemoteRegistry
{
    private readonly string _host;
    private readonly WmiCredential? _credential;
    private readonly bool _isLocal;
    private bool _connected;
    private bool _disposed;

    public bool SupportsLastWriteTime => true;

    public RemoteRegistry(string host, WmiCredential? credential)
    {
        _host = host;
        _credential = credential;
        _isLocal = IsLocal(host);
    }

    private static bool IsLocal(string host) =>
        string.IsNullOrEmpty(host) || host is "." or "localhost" or "127.0.0.1" or "::1"
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    private void EnsureConnected()
    {
        if (_connected || _isLocal) { _connected = true; return; }

        // Current-token mode uses the caller's identity; explicit creds establish an IPC$ session.
        string? user = null, password = null;
        IntPtr pwdPtr = IntPtr.Zero;
        try
        {
            if (_credential is { UseCurrentToken: false })
            {
                user = string.IsNullOrEmpty(_credential.Domain)
                    ? _credential.Username
                    : $"{_credential.Domain}\\{_credential.Username}";
                if (_credential.Password is not null)
                {
                    pwdPtr = Marshal.SecureStringToGlobalAllocUnicode(_credential.Password);
                    password = Marshal.PtrToStringUni(pwdPtr);
                }
            }

            var nr = new NETRESOURCE
            {
                dwType = RESOURCETYPE_ANY,
                lpRemoteName = $"\\\\{_host}\\IPC$",
            };
            int result = WNetAddConnection2(ref nr, password, user, 0);
            if (result != NO_ERROR && result != ERROR_SESSION_CREDENTIAL_CONFLICT)
                throw new WmiException(MapWNetError(result), $"Could not connect to \\\\{_host}\\IPC$ (error {result}).");
            _connected = true;
        }
        finally
        {
            if (pwdPtr != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pwdPtr);
        }
    }

    private RegistryKey OpenBase(RegistryRoot root)
    {
        var hive = root == RegistryRoot.Users ? RegistryHive.Users : RegistryHive.LocalMachine;
        try
        {
            return _isLocal
                ? RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)
                : RegistryKey.OpenRemoteBaseKey(hive, _host, RegistryView.Registry64);
        }
        catch (Exception ex)
        {
            throw new WmiException(WmiFailureKind.AccessDenied,
                $"Could not open the remote registry on {_host}. Is the Remote Registry service running?", null, ex);
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string path)
    {
        EnsureConnected();
        using var baseKey = OpenBase(root);
        using var key = baseKey.OpenSubKey(path);
        return key is null ? Array.Empty<string>() : key.GetSubKeyNames();
    }

    public IReadOnlyList<RegistryKeyData> EnumerateSubkeys(
        RegistryRoot root, string path, IReadOnlyCollection<string> valueNames)
    {
        EnsureConnected();
        using var baseKey = OpenBase(root);
        using var parent = baseKey.OpenSubKey(path);
        if (parent is null) return Array.Empty<RegistryKeyData>();

        var result = new List<RegistryKeyData>();
        foreach (var subName in parent.GetSubKeyNames())
        {
            RegistryKey? sub = null;
            try { sub = parent.OpenSubKey(subName); }
            catch { /* skip unreadable subkey */ }
            if (sub is null) continue;
            using (sub)
            {
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var vn in valueNames)
                {
                    var val = sub.GetValue(vn);
                    if (val is not null) values[vn] = val;
                }
                result.Add(new RegistryKeyData(subName, values, GetLastWriteTimeUtc(sub)));
            }
        }
        return result;
    }

    private static DateTime? GetLastWriteTimeUtc(RegistryKey key)
    {
        try
        {
            SafeRegistryHandle handle = key.Handle;
            long ft = 0;
            int rc = RegQueryInfoKey(handle, null, IntPtr.Zero, IntPtr.Zero,
                out _, IntPtr.Zero, IntPtr.Zero, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref ft);
            if (rc == 0 && ft > 0) return DateTime.FromFileTimeUtc(ft);
        }
        catch { /* fall through */ }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_connected && !_isLocal)
        {
            try { WNetCancelConnection2($"\\\\{_host}\\IPC$", 0, true); } catch { /* best effort */ }
        }
    }

    // --- P/Invoke ---

    private const int NO_ERROR = 0;
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_BAD_NETPATH = 53;
    private const int RESOURCETYPE_ANY = 0;

    private static WmiFailureKind MapWNetError(int code) => code switch
    {
        ERROR_ACCESS_DENIED => WmiFailureKind.AccessDenied,
        ERROR_LOGON_FAILURE => WmiFailureKind.AuthFailed,
        ERROR_BAD_NETPATH => WmiFailureKind.Unreachable,
        _ => WmiFailureKind.Unknown,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    [DllImport("advapi32.dll")]
    private static extern int RegQueryInfoKey(
        SafeRegistryHandle hKey, System.Text.StringBuilder? lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        out int lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, out int lpcValues,
        IntPtr lpcbMaxValueNameLen, IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, ref long lpftLastWriteTime);
}
