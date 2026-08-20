using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using Marco.Core.Inventory;
using Marco.Core.Wmi;

namespace Marco.Credentials;

/// <summary>
/// One named credential the operator supplies. The password is held as a <see cref="SecureString"/> and is never
/// written to disk in plaintext, logged, or exported. <see cref="IsCurrentToken"/> issues no credentials at all
/// (integrated auth / runas /netonly).
/// </summary>
public sealed class CredentialSet : IDisposable
{
    public string Label { get; set; }
    public string? Domain { get; set; }
    public string? Username { get; set; }
    public SecureString? Password { get; private set; }
    public bool IsCurrentToken { get; }

    /// <summary>Which host kind this credential targets (Windows/WMI, Linux/SSH, or Any).</summary>
    public CredentialKind Kind { get; set; } = CredentialKind.Any;

    /// <summary>SSH port for Linux credentials.</summary>
    public int SshPort { get; set; } = 22;

    /// <summary>Client profile this credential belongs to; null = shared (tried for every client). Scans scope
    /// candidates to the active client's sets plus the shared ones — another client's credentials are never
    /// sprayed at this client's network.</summary>
    public string? ClientId { get; set; }

    public CredentialSet(string label, string? domain, string? username, SecureString? password, bool isCurrentToken = false)
    {
        Label = label;
        Domain = domain;
        Username = username;
        Password = password;
        IsCurrentToken = isCurrentToken;
    }

    public static CredentialSet CurrentToken(string label = "Current session")
        => new(label, null, null, null, isCurrentToken: true) { Kind = CredentialKind.Windows };

    public WmiCredential ToWmiCredential() => IsCurrentToken
        ? WmiCredential.CurrentToken
        : new WmiCredential { Domain = Domain, Username = Username, Password = Password };

    public CredentialCandidate ToCandidate() => new(Label, ToWmiCredential(), Kind, SshPort);

    /// <summary>Set/replace the password from a transient plaintext string (e.g. unpacked from the Windows
    /// credential dialog), copying into a SecureString and leaving the caller to clear the plaintext.</summary>
    public void SetPassword(string plaintext)
    {
        var s = new SecureString();
        foreach (var c in plaintext) s.AppendChar(c);
        s.MakeReadOnly();
        Password?.Dispose();
        Password = s;
    }

    internal void SetPasswordSecure(SecureString s)
    {
        Password?.Dispose();
        Password = s;
    }

    public void Dispose() => Password?.Dispose();
}

internal static class SecureStringMarshal
{
    public static byte[] ToUtf8Bytes(SecureString s)
    {
        IntPtr bstr = IntPtr.Zero;
        try
        {
            bstr = Marshal.SecureStringToBSTR(s);
            int len = s.Length;
            var chars = new char[len];
            Marshal.Copy(bstr, chars, 0, len);
            try { return System.Text.Encoding.UTF8.GetBytes(chars); }
            finally { Array.Clear(chars, 0, chars.Length); }
        }
        finally
        {
            if (bstr != IntPtr.Zero) Marshal.ZeroFreeBSTR(bstr);
        }
    }

    public static SecureString FromUtf8Bytes(byte[] bytes)
    {
        var chars = System.Text.Encoding.UTF8.GetChars(bytes);
        try
        {
            var s = new SecureString();
            foreach (var c in chars) s.AppendChar(c);
            s.MakeReadOnly();
            return s;
        }
        finally { Array.Clear(chars, 0, chars.Length); }
    }
}
