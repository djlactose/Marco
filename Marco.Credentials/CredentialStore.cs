using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marco.Core.Inventory;

namespace Marco.Credentials;

/// <summary>Raised when a saved credential file cannot be decrypted — almost always because it was created by a
/// different Windows account or on a different machine (DPAPI CurrentUser binding). This is the correct security
/// property, not corruption, and the message says so.</summary>
public sealed class CredentialDecryptException : Exception
{
    public CredentialDecryptException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// In-memory ordered list of credential sets, with optional DPAPI-at-rest persistence. Passwords live only as
/// SecureStrings and, when saved, only as DPAPI-protected blobs scoped to the current user — never plaintext,
/// never in logs, never in exports. Profiles do not travel between machines/accounts by design.
/// </summary>
public sealed class CredentialStore : IDisposable
{
    private readonly List<CredentialSet> _sets = new();
    private readonly object _lock = new();

    // SHA-256 of the file as last read or written by this instance (null = no file). Another Marco window may
    // save in between; ReloadIfChanged compares against this before a mutation so we never clobber its edit.
    private string? _diskFingerprint;

    public IReadOnlyList<CredentialSet> Sets
    {
        get { lock (_lock) return _sets.ToList(); }
    }

    public void Add(CredentialSet set)
    {
        lock (_lock) _sets.Add(set);
    }

    public void Remove(CredentialSet set)
    {
        lock (_lock)
        {
            if (_sets.Remove(set)) set.Dispose();
        }
    }

    /// <summary>Swap an edited set in place, keeping its try-order position; the old set is disposed.</summary>
    public void Replace(CredentialSet existing, CredentialSet replacement)
    {
        lock (_lock)
        {
            var index = _sets.IndexOf(existing);
            if (index < 0) { _sets.Add(replacement); return; }
            _sets[index] = replacement;
            existing.Dispose();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var s in _sets) s.Dispose();
            _sets.Clear();
        }
    }

    /// <summary>Ordered candidates for the inventory runner's try-and-remember.</summary>
    public IReadOnlyList<CredentialCandidate> ToCandidates()
    {
        lock (_lock) return _sets.Select(s => s.ToCandidate()).ToList();
    }

    // --- DPAPI persistence ---

    private sealed record PersistedEntry(string Label, string? Domain, string? Username, bool IsCurrentToken,
        string? ProtectedPassword, Marco.Core.Inventory.CredentialKind Kind = Marco.Core.Inventory.CredentialKind.Any, int SshPort = 22);

    /// <summary>Persist to a DPAPI-protected file scoped to the current user. Passwords are individually
    /// encrypted; nothing plaintext is written.</summary>
    public void Save(string path)
    {
        List<PersistedEntry> entries;
        lock (_lock)
        {
            entries = _sets.Select(s =>
            {
                string? protectedPwd = null;
                if (!s.IsCurrentToken && s.Password is not null)
                {
                    var plain = SecureStringMarshal.ToUtf8Bytes(s.Password);
                    try
                    {
                        var enc = ProtectedData.Protect(plain, OptionalEntropy, DataProtectionScope.CurrentUser);
                        protectedPwd = Convert.ToBase64String(enc);
                    }
                    finally { Array.Clear(plain, 0, plain.Length); }
                }
                return new PersistedEntry(s.Label, s.Domain, s.Username, s.IsCurrentToken, protectedPwd, s.Kind, s.SshPort);
            }).ToList();
        }

        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(entries, JsonOpts));
        // Write-to-temp then rename: a concurrent reader (another Marco window) never sees a torn file.
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        _diskFingerprint = Fingerprint(bytes);
    }

    /// <summary>Load and replace the current set list. Throws <see cref="CredentialDecryptException"/> if the
    /// blobs were sealed by a different account/machine.</summary>
    public void Load(string path)
    {
        if (!File.Exists(path)) { _diskFingerprint = null; return; }
        var bytes = File.ReadAllBytes(path);
        var fingerprint = Fingerprint(bytes);
        var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(bytes, JsonOpts) ?? new();

        var loaded = new List<CredentialSet>();
        foreach (var e in entries)
        {
            if (e.IsCurrentToken)
            {
                loaded.Add(CredentialSet.CurrentToken(e.Label));
                continue;
            }
            var set = new CredentialSet(e.Label, e.Domain, e.Username, null) { Kind = e.Kind, SshPort = e.SshPort };
            if (e.ProtectedPassword is not null)
            {
                try
                {
                    var enc = Convert.FromBase64String(e.ProtectedPassword);
                    var plain = ProtectedData.Unprotect(enc, OptionalEntropy, DataProtectionScope.CurrentUser);
                    try { set.SetPasswordSecure(SecureStringMarshal.FromUtf8Bytes(plain)); }
                    finally { Array.Clear(plain, 0, plain.Length); }
                }
                catch (CryptographicException ex)
                {
                    foreach (var s in loaded) s.Dispose();
                    set.Dispose();
                    throw new CredentialDecryptException(
                        "Saved credential profiles could not be decrypted. They are bound to the Windows account " +
                        "that saved them (DPAPI CurrentUser), so they will not open on a different machine or user " +
                        "account. Re-enter the credentials on this machine.", ex);
                }
            }
            loaded.Add(set);
        }

        Clear();
        lock (_lock) _sets.AddRange(loaded);
        _diskFingerprint = fingerprint;
    }

    /// <summary>If the file on disk differs from what this instance last read or wrote (another Marco window
    /// saved), reload it and return true; otherwise leave the in-memory list untouched and return false. Call
    /// before a mutation that will be saved, so the save doesn't discard the other window's edit. May throw
    /// <see cref="CredentialDecryptException"/> like <see cref="Load"/>.</summary>
    public bool ReloadIfChanged(string path)
    {
        string? onDisk = null;
        if (File.Exists(path))
        {
            try { onDisk = Fingerprint(File.ReadAllBytes(path)); }
            catch (IOException) { return false; } // mid-rename by the other window; the next mutation will see it
        }
        if (onDisk == _diskFingerprint) return false;
        Load(path);
        return true;
    }

    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // Fixed application entropy — not a secret (defense-in-depth alongside the DPAPI user scope).
    private static readonly byte[] OptionalEntropy = Encoding.ASCII.GetBytes("Marco.Credentials.v1");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public void Dispose() => Clear();
}
