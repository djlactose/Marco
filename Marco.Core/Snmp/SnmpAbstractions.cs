using System.Text;

namespace Marco.Core.Snmp;

/// <summary>SNMP protocol version as carried on the wire (RFC 1157 v1 = 0, RFC 3416 v2c = 1).</summary>
public enum SnmpVersion { V1 = 0, V2c = 1 }

/// <summary>An object identifier as its arc list. Compared and prefix-tested arc-wise, never as text: the string
/// ".43.1" is a prefix of ".43.10" and ".9.1.10" sorts before ".9.1.2", both of which would break table walks.</summary>
public readonly struct SnmpOid : IEquatable<SnmpOid>, IComparable<SnmpOid>
{
    private readonly uint[] _arcs;

    public SnmpOid(params uint[] arcs) => _arcs = arcs ?? Array.Empty<uint>();

    public IReadOnlyList<uint> Arcs => _arcs ?? Array.Empty<uint>();
    public int Length => Arcs.Count;
    public uint this[int index] => Arcs[index];
    public bool IsEmpty => Length == 0;

    /// <summary>Parse dotted form ("1.3.6.1.2.1.1.1.0"); a leading dot is tolerated.</summary>
    public static SnmpOid Parse(string text)
    {
        if (!TryParse(text, out var oid)) throw new FormatException($"'{text}' is not a dotted OID.");
        return oid;
    }

    public static bool TryParse(string? text, out SnmpOid oid)
    {
        oid = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().TrimStart('.').Split('.');
        var arcs = new uint[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!uint.TryParse(parts[i], out arcs[i])) return false;
        oid = new SnmpOid(arcs);
        return true;
    }

    /// <summary>This OID followed by more arcs (e.g. a column OID + row index).</summary>
    public SnmpOid Append(params uint[] more)
    {
        var arcs = new uint[Length + more.Length];
        for (int i = 0; i < Length; i++) arcs[i] = Arcs[i];
        more.CopyTo(arcs, Length);
        return new SnmpOid(arcs);
    }

    /// <summary>True when every arc of this OID matches the start of <paramref name="other"/> (a table column
    /// OID is a prefix of each of its instances; an OID is a prefix of itself).</summary>
    public bool IsPrefixOf(SnmpOid other)
    {
        if (other.Length < Length) return false;
        for (int i = 0; i < Length; i++) if (Arcs[i] != other.Arcs[i]) return false;
        return true;
    }

    /// <summary>The arcs after <paramref name="prefix"/> — the row index of a table instance. Empty when this OID
    /// isn't under the prefix.</summary>
    public uint[] IndexAfter(SnmpOid prefix)
    {
        if (!prefix.IsPrefixOf(this)) return Array.Empty<uint>();
        var rest = new uint[Length - prefix.Length];
        for (int i = 0; i < rest.Length; i++) rest[i] = Arcs[prefix.Length + i];
        return rest;
    }

    public int CompareTo(SnmpOid other)
    {
        int n = Math.Min(Length, other.Length);
        for (int i = 0; i < n; i++)
        {
            int c = Arcs[i].CompareTo(other.Arcs[i]);
            if (c != 0) return c;
        }
        return Length.CompareTo(other.Length);
    }

    public bool Equals(SnmpOid other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SnmpOid o && Equals(o);
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var a in Arcs) h.Add(a);
        return h.ToHashCode();
    }
    public override string ToString() => string.Join(".", Arcs);
    public static bool operator ==(SnmpOid a, SnmpOid b) => a.Equals(b);
    public static bool operator !=(SnmpOid a, SnmpOid b) => !a.Equals(b);
}

/// <summary>The BER/SMI type of a decoded value. Exceptions (NoSuchObject/NoSuchInstance/EndOfMibView) are
/// v2c per-varbind markers; v1 agents signal the same thing through the PDU error-status instead.</summary>
public enum SnmpValueKind
{
    Null = 0, Integer, OctetString, Oid, IpAddress, Counter32, Gauge32, TimeTicks, Counter64, Opaque,
    NoSuchObject, NoSuchInstance, EndOfMibView, Unknown,
}

/// <summary>A decoded SNMP value. Integers (signed INTEGER and the unsigned application types) are normalised to
/// <see cref="Int"/>; OCTET STRING keeps its raw bytes so callers decide between text and bitmap readings.</summary>
public sealed class SnmpValue
{
    public SnmpValueKind Kind { get; }
    public long? Int { get; }
    public byte[]? Bytes { get; }
    public SnmpOid? Oid { get; }

    private SnmpValue(SnmpValueKind kind, long? i = null, byte[]? bytes = null, SnmpOid? oid = null)
    { Kind = kind; Int = i; Bytes = bytes; Oid = oid; }

    public static readonly SnmpValue Null = new(SnmpValueKind.Null);
    public static readonly SnmpValue NoSuchObject = new(SnmpValueKind.NoSuchObject);
    public static readonly SnmpValue NoSuchInstance = new(SnmpValueKind.NoSuchInstance);
    public static readonly SnmpValue EndOfMibView = new(SnmpValueKind.EndOfMibView);

    public static SnmpValue Integer(long v) => new(SnmpValueKind.Integer, v);
    public static SnmpValue Counter32(long v) => new(SnmpValueKind.Counter32, v);
    public static SnmpValue Gauge32(long v) => new(SnmpValueKind.Gauge32, v);
    public static SnmpValue TimeTicks(long v) => new(SnmpValueKind.TimeTicks, v);
    public static SnmpValue Counter64(long v) => new(SnmpValueKind.Counter64, v);
    public static SnmpValue OctetString(byte[] bytes) => new(SnmpValueKind.OctetString, bytes: bytes);
    public static SnmpValue OctetString(string text) => new(SnmpValueKind.OctetString, bytes: Encoding.UTF8.GetBytes(text));
    public static SnmpValue IpAddress(byte[] bytes) => new(SnmpValueKind.IpAddress, bytes: bytes);
    public static SnmpValue Opaque(byte[] bytes) => new(SnmpValueKind.Opaque, bytes: bytes);
    public static SnmpValue ObjectId(SnmpOid oid) => new(SnmpValueKind.Oid, oid: oid);
    public static SnmpValue UnknownTag(byte[] raw) => new(SnmpValueKind.Unknown, bytes: raw);

    /// <summary>True for the v2c exception markers (no data for this OID).</summary>
    public bool IsException => Kind is SnmpValueKind.NoSuchObject or SnmpValueKind.NoSuchInstance or SnmpValueKind.EndOfMibView;
    /// <summary>True when there is a real value (not null, not an exception).</summary>
    public bool HasValue => !IsException && Kind != SnmpValueKind.Null;

    /// <summary>OCTET STRING as text: strict UTF-8, falling back to Latin-1 for agents that send vendor code
    /// pages, with NUL padding and control characters removed and whitespace trimmed. Integers render as their
    /// decimal text. Null for other kinds or empty strings.</summary>
    public string? AsText()
    {
        if (Int is { } n && Kind != SnmpValueKind.OctetString) return n.ToString();
        if (Bytes is not { Length: > 0 } b) return null;
        string s;
        try { s = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b); }
        catch (DecoderFallbackException) { s = Encoding.Latin1.GetString(b); }
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (ch >= ' ' && ch != (char)0x7f) sb.Append(ch);
        var t = sb.ToString().Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>A MAC address in AA:BB:CC:DD:EE:FF form from a 6-byte OCTET STRING, or from an ASCII rendering
    /// some agents send instead. Null for anything else (including the all-zero address).</summary>
    public string? AsMac()
    {
        if (Bytes is not { } b) return null;
        if (b.Length == 6)
        {
            if (b.All(x => x == 0)) return null;
            return string.Join(":", b.Select(x => x.ToString("X2")));
        }
        var text = AsText();
        if (text is null) return null;
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12 || hex.All(c => c == '0')) return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
    }

    /// <summary>An IPv4 address in dotted form from a 4-byte IpAddress/OCTET STRING.</summary>
    public string? AsIpv4() => Bytes is { Length: 4 } b ? string.Join(".", b) : null;

    public override string ToString() => Kind switch
    {
        SnmpValueKind.OctetString => AsText() ?? "",
        SnmpValueKind.Oid => Oid?.ToString() ?? "",
        SnmpValueKind.IpAddress => AsIpv4() ?? "",
        _ when Int is { } n => n.ToString(),
        _ => Kind.ToString(),
    };
}

/// <summary>One (OID, value) pair.</summary>
public sealed record SnmpVarBind(SnmpOid Oid, SnmpValue Value);

public enum SnmpFailureKind
{
    /// <summary>No datagram came back within the timeout/retries: the agent is off, firewalled, or the
    /// community string is wrong (SNMP drops bad-community requests silently).</summary>
    NoResponse,
    /// <summary>The host answered with ICMP port-unreachable — the host is up but SNMP is not listening.</summary>
    PortUnreachable,
    /// <summary>The reply could not be decoded, or the agent returned a PDU-level error.</summary>
    ProtocolError,
    /// <summary>The agent said the response would not fit (error-status tooBig) even after shrinking.</summary>
    TooBig,
}

public sealed class SnmpException : Exception
{
    public SnmpFailureKind Kind { get; }
    public SnmpException(SnmpFailureKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

/// <summary>Transport tuning. Sleeping printers take 1–3 s to wake for their first reply, so the default
/// timeout is generous; retries re-send the same request-id.</summary>
public sealed record SnmpOptions(int TimeoutMs = 2000, int Retries = 1, int MaxRepetitions = 20)
{
    public static readonly SnmpOptions Default = new();
}

/// <summary>A read-only SNMP conversation with one agent, abstracted so the printer/network-device runner and
/// its MIB parsers are unit-testable against canned OID tables.</summary>
public interface ISnmpSession : IDisposable
{
    string Host { get; }
    SnmpVersion Version { get; }

    /// <summary>GET the given OIDs. Missing instances come back as NoSuchInstance values rather than throwing,
    /// in both v1 (via the noSuchName retry loop) and v2c.</summary>
    Task<IReadOnlyList<SnmpVarBind>> GetAsync(IReadOnlyList<SnmpOid> oids, CancellationToken ct);

    /// <summary>Walk every instance under <paramref name="prefix"/> (GETBULK on v2c, GETNEXT on v1), stopping at
    /// end-of-MIB, on leaving the prefix, on a non-increasing OID (broken agent), or at <paramref name="maxRows"/>.</summary>
    Task<IReadOnlyList<SnmpVarBind>> WalkAsync(SnmpOid prefix, int maxRows, CancellationToken ct);
}

/// <summary>Creates sessions. Nothing is sent until the first request; the first successful GET is what proves
/// the community string.</summary>
public interface ISnmpSessionFactory
{
    ISnmpSession Create(string host, int port, string community, SnmpVersion version, SnmpOptions options);
}

/// <summary>Convenience extensions used by the collectors.</summary>
public static class SnmpSessionExtensions
{
    public static async Task<SnmpValue?> GetOneAsync(this ISnmpSession s, SnmpOid oid, CancellationToken ct)
    {
        var r = await s.GetAsync(new[] { oid }, ct).ConfigureAwait(false);
        return r.Count > 0 && r[0].Value.HasValue ? r[0].Value : null;
    }

    /// <summary>Values of a GET keyed by OID, with exceptions/nulls dropped.</summary>
    public static async Task<Dictionary<SnmpOid, SnmpValue>> GetMapAsync(this ISnmpSession s, IReadOnlyList<SnmpOid> oids, CancellationToken ct)
    {
        var map = new Dictionary<SnmpOid, SnmpValue>();
        foreach (var vb in await s.GetAsync(oids, ct).ConfigureAwait(false))
            if (vb.Value.HasValue) map[vb.Oid] = vb.Value;
        return map;
    }
}
