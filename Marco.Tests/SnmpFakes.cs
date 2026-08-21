using System.Net;
using System.Net.Sockets;
using System.Text;
using Marco.Core.Snmp;
using Marco.Inventory.Snmp;

namespace Marco.Tests;

/// <summary>An ordered OID → value table, loadable from the "oid = type:value" fixture format.</summary>
internal sealed class SnmpOidTable
{
    public SortedDictionary<SnmpOid, SnmpValue> Entries { get; } = new();

    public SnmpOidTable Set(string oid, SnmpValue value) { Entries[SnmpOid.Parse(oid)] = value; return this; }
    public SnmpOidTable Str(string oid, string s) => Set(oid, SnmpValue.OctetString(s));
    public SnmpOidTable Int(string oid, long n) => Set(oid, SnmpValue.Integer(n));
    public SnmpOidTable Oid(string oid, string value) => Set(oid, SnmpValue.ObjectId(SnmpOid.Parse(value)));
    public SnmpOidTable Hex(string oid, string hex) => Set(oid, SnmpValue.OctetString(Convert.FromHexString(hex.Replace(" ", ""))));
    public SnmpOidTable Ticks(string oid, long n) => Set(oid, SnmpValue.TimeTicks(n));
    public SnmpOidTable Counter(string oid, long n) => Set(oid, SnmpValue.Counter32(n));
    public SnmpOidTable Gauge(string oid, long n) => Set(oid, SnmpValue.Gauge32(n));

    public SnmpValue? Get(SnmpOid oid) => Entries.TryGetValue(oid, out var v) ? v : null;

    /// <summary>The first entry whose OID sorts after <paramref name="oid"/> (GETNEXT semantics).</summary>
    public KeyValuePair<SnmpOid, SnmpValue>? Next(SnmpOid oid)
    {
        foreach (var kv in Entries) if (kv.Key.CompareTo(oid) > 0) return kv;
        return null;
    }

    /// <summary>Parse fixture text: one "oid = type:value" per line; '#' comments; types str/int/oid/hex/ticks/counter/gauge/ip.</summary>
    public static SnmpOidTable Parse(string text)
    {
        var t = new SnmpOidTable();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var oid = line[..eq].Trim();
            var rest = line[(eq + 1)..].Trim();
            int colon = rest.IndexOf(':');
            var type = colon < 0 ? "str" : rest[..colon].Trim();
            var value = colon < 0 ? rest : rest[(colon + 1)..];
            switch (type)
            {
                case "str": t.Str(oid, value); break;
                case "int": t.Int(oid, long.Parse(value)); break;
                case "oid": t.Oid(oid, value.Trim()); break;
                case "hex": t.Hex(oid, value.Trim()); break;
                case "ticks": t.Ticks(oid, long.Parse(value)); break;
                case "counter": t.Counter(oid, long.Parse(value)); break;
                case "gauge": t.Gauge(oid, long.Parse(value)); break;
                case "ip": t.Set(oid, SnmpValue.IpAddress(IPAddress.Parse(value.Trim()).GetAddressBytes())); break;
                default: throw new FormatException($"Unknown fixture type '{type}' on line: {line}");
            }
        }
        return t;
    }

    public static SnmpOidTable FromFixture(string name)
        => Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
}

/// <summary>In-memory <see cref="ISnmpSession"/> over an OID table — the runner/parser test double.</summary>
internal sealed class FakeSnmpSession : ISnmpSession
{
    private readonly SnmpOidTable _table;
    public string Host { get; }
    public SnmpVersion Version { get; }
    public List<string> Requests { get; } = new();
    public bool Disposed { get; private set; }

    public FakeSnmpSession(SnmpOidTable table, string host = "10.0.0.9", SnmpVersion version = SnmpVersion.V2c)
    { _table = table; Host = host; Version = version; }

    public Task<IReadOnlyList<SnmpVarBind>> GetAsync(IReadOnlyList<SnmpOid> oids, CancellationToken ct)
    {
        Requests.Add("GET " + string.Join(",", oids));
        IReadOnlyList<SnmpVarBind> r = oids.Select(o => new SnmpVarBind(o, _table.Get(o) ?? SnmpValue.NoSuchInstance)).ToList();
        return Task.FromResult(r);
    }

    public Task<IReadOnlyList<SnmpVarBind>> WalkAsync(SnmpOid prefix, int maxRows, CancellationToken ct)
    {
        Requests.Add("WALK " + prefix);
        IReadOnlyList<SnmpVarBind> r = _table.Entries.Where(kv => prefix.IsPrefixOf(kv.Key)).Take(maxRows)
            .Select(kv => new SnmpVarBind(kv.Key, kv.Value)).ToList();
        return Task.FromResult(r);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Creates <see cref="FakeSnmpSession"/>s gated on the community string; wrong communities get a session
/// that never answers (NoResponse), like the real protocol. Optionally refuses the port outright.</summary>
internal sealed class FakeSnmpSessionFactory : ISnmpSessionFactory
{
    private readonly SnmpOidTable _table;
    public string Community { get; set; } = "public";
    public SnmpVersion? OnlyVersion { get; set; }
    public bool PortUnreachable { get; set; }
    public List<(string Community, SnmpVersion Version)> Attempts { get; } = new();
    public List<FakeSnmpSession> Sessions { get; } = new();

    public FakeSnmpSessionFactory(SnmpOidTable table) => _table = table;

    public ISnmpSession Create(string host, int port, string community, SnmpVersion version, SnmpOptions options)
    {
        Attempts.Add((community, version));
        if (PortUnreachable) return new ThrowingSession(host, version, new SnmpException(SnmpFailureKind.PortUnreachable, "port unreachable"));
        if (community != Community || (OnlyVersion is { } only && version != only))
            return new ThrowingSession(host, version, new SnmpException(SnmpFailureKind.NoResponse, "silence"));
        var s = new FakeSnmpSession(_table, host, version);
        Sessions.Add(s);
        return s;
    }

    private sealed class ThrowingSession : ISnmpSession
    {
        private readonly SnmpException _ex;
        public string Host { get; }
        public SnmpVersion Version { get; }
        public ThrowingSession(string host, SnmpVersion v, SnmpException ex) { Host = host; Version = v; _ex = ex; }
        public Task<IReadOnlyList<SnmpVarBind>> GetAsync(IReadOnlyList<SnmpOid> oids, CancellationToken ct) => Task.FromException<IReadOnlyList<SnmpVarBind>>(_ex);
        public Task<IReadOnlyList<SnmpVarBind>> WalkAsync(SnmpOid prefix, int maxRows, CancellationToken ct) => Task.FromException<IReadOnlyList<SnmpVarBind>>(_ex);
        public void Dispose() { }
    }
}

/// <summary>
/// A real UDP SNMP agent on 127.0.0.1 for exercising <see cref="UdpSnmpSession"/> end to end: GET / GETNEXT /
/// GETBULK over an OID table, with knobs for the failure modes the transport must handle (silence, dropped
/// datagrams, tooBig, v1-only, ignoring GETBULK).
/// </summary>
internal sealed class FakeSnmpAgent : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    public SnmpOidTable Table { get; }
    public string Community { get; set; } = "public";
    public HashSet<SnmpVersion> AnswerVersions { get; } = new() { SnmpVersion.V1, SnmpVersion.V2c };
    public int DropFirst { get; set; }
    public bool Silent { get; set; }
    public bool IgnoreGetBulk { get; set; }
    public int? TooBigAboveRepetitions { get; set; }
    public int RequestsSeen => _requests;
    private int _requests;
    public List<byte> PduTypesSeen { get; } = new();

    public int Port { get; }

    public FakeSnmpAgent(SnmpOidTable table, int port = 0)
    {
        Table = table;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            Interlocked.Increment(ref _requests);
            byte[]? reply;
            try { reply = Handle(r.Buffer); }
            catch { reply = null; }
            if (reply is not null)
            {
                try { await _udp.SendAsync(reply, reply.Length, r.RemoteEndPoint); } catch { }
            }
        }
    }

    private byte[]? Handle(byte[] datagram)
    {
        var msg = Ber.ReadTlv(datagram, 0);
        var top = Ber.ReadChildren(datagram, msg);
        var version = (SnmpVersion)Ber.DecodeInteger(top[0].Contents(datagram), signed: true);
        var community = Encoding.UTF8.GetString(top[1].Contents(datagram));
        var pdu = top[2];
        var fields = Ber.ReadChildren(datagram, pdu);
        int requestId = (int)Ber.DecodeInteger(fields[0].Contents(datagram), signed: true);
        int f2 = (int)Ber.DecodeInteger(fields[2].Contents(datagram), signed: true);
        var oids = Ber.ReadChildren(datagram, fields[3])
            .Select(vb => Ber.DecodeOid(Ber.ReadChildren(datagram, vb)[0].Contents(datagram))).ToList();
        lock (PduTypesSeen) PduTypesSeen.Add(pdu.Tag);

        if (Silent || community != Community || !AnswerVersions.Contains(version)) return null;
        if (DropFirst > 0) { DropFirst--; return null; }
        if (pdu.Tag == Ber.PduGetBulk && IgnoreGetBulk) return null;

        var binds = new List<(SnmpOid, SnmpValue)>();
        int errorStatus = 0, errorIndex = 0;
        switch (pdu.Tag)
        {
            case Ber.PduGet:
                for (int i = 0; i < oids.Count; i++)
                {
                    var v = Table.Get(oids[i]);
                    if (v is null)
                    {
                        if (version == SnmpVersion.V1) { errorStatus = SnmpErrorStatus.NoSuchName; errorIndex = i + 1; binds.Clear(); goto done; }
                        binds.Add((oids[i], SnmpValue.NoSuchInstance));
                    }
                    else binds.Add((oids[i], v));
                }
                break;
            case Ber.PduGetNext:
                for (int i = 0; i < oids.Count; i++)
                {
                    var next = Table.Next(oids[i]);
                    if (next is null)
                    {
                        if (version == SnmpVersion.V1) { errorStatus = SnmpErrorStatus.NoSuchName; errorIndex = i + 1; binds.Clear(); goto done; }
                        binds.Add((oids[i], SnmpValue.EndOfMibView));
                    }
                    else binds.Add((next.Value.Key, next.Value.Value));
                }
                break;
            case Ber.PduGetBulk:
                if (TooBigAboveRepetitions is { } cap && f2 > cap) { errorStatus = SnmpErrorStatus.TooBig; goto done; }
                foreach (var o in oids)
                {
                    var cur = o;
                    for (int rep = 0; rep < f2; rep++)
                    {
                        var next = Table.Next(cur);
                        if (next is null) { binds.Add((cur, SnmpValue.EndOfMibView)); break; }
                        binds.Add((next.Value.Key, next.Value.Value));
                        cur = next.Value.Key;
                    }
                }
                break;
            default:
                return null;
        }
        done:
        if (errorStatus != 0 && binds.Count == 0) binds.AddRange(oids.Select(o => (o, SnmpValue.Null)));
        return BuildResponse(version, community, requestId, errorStatus, errorIndex, binds);
    }

    public static byte[] BuildResponse(SnmpVersion version, string community, int requestId, int errorStatus, int errorIndex, List<(SnmpOid Oid, SnmpValue Value)> binds)
    {
        var vbs = binds.Select(b => Ber.EncodeConstructed(Ber.TagSequence, Ber.EncodeOid(b.Oid), EncodeValue(b.Value))).ToArray();
        var pdu = Ber.EncodeConstructed(Ber.PduResponse,
            Ber.EncodeInteger(requestId), Ber.EncodeInteger(errorStatus), Ber.EncodeInteger(errorIndex),
            Ber.EncodeConstructed(Ber.TagSequence, vbs));
        return Ber.EncodeConstructed(Ber.TagSequence,
            Ber.EncodeInteger((int)version), Ber.EncodeOctetString(Encoding.UTF8.GetBytes(community)), pdu);
    }

    public static byte[] EncodeValue(SnmpValue v)
    {
        var sink = new List<byte>();
        switch (v.Kind)
        {
            case SnmpValueKind.Integer: return Ber.EncodeInteger(v.Int ?? 0);
            case SnmpValueKind.OctetString: return Ber.EncodeOctetString(v.Bytes ?? Array.Empty<byte>());
            case SnmpValueKind.Oid: return Ber.EncodeOid(v.Oid ?? default);
            case SnmpValueKind.IpAddress: Ber.WriteTlv(sink, Ber.TagIpAddress, v.Bytes ?? new byte[4]); return sink.ToArray();
            case SnmpValueKind.Counter32: Ber.WriteTlv(sink, Ber.TagCounter32, Unsigned(v.Int ?? 0)); return sink.ToArray();
            case SnmpValueKind.Gauge32: Ber.WriteTlv(sink, Ber.TagGauge32, Unsigned(v.Int ?? 0)); return sink.ToArray();
            case SnmpValueKind.TimeTicks: Ber.WriteTlv(sink, Ber.TagTimeTicks, Unsigned(v.Int ?? 0)); return sink.ToArray();
            case SnmpValueKind.Counter64: Ber.WriteTlv(sink, Ber.TagCounter64, Unsigned(v.Int ?? 0)); return sink.ToArray();
            case SnmpValueKind.NoSuchObject: return new byte[] { Ber.TagNoSuchObject, 0 };
            case SnmpValueKind.NoSuchInstance: return new byte[] { Ber.TagNoSuchInstance, 0 };
            case SnmpValueKind.EndOfMibView: return new byte[] { Ber.TagEndOfMibView, 0 };
            default: return Ber.EncodeNull();
        }
    }

    /// <summary>Unsigned big-endian with a leading 0x00 when the top bit is set — exactly how real agents encode
    /// Counter32/Gauge32/TimeTicks (the 5-byte shape the decoder must accept).</summary>
    private static byte[] Unsigned(long value)
    {
        var bytes = new List<byte>();
        ulong u = (ulong)value;
        do { bytes.Insert(0, (byte)(u & 0xFF)); u >>= 8; } while (u > 0);
        if ((bytes[0] & 0x80) != 0) bytes.Insert(0, 0);
        return bytes.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        try { _loop.Wait(1000); } catch { }
    }
}
