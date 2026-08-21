using System.Net;
using System.Net.Sockets;
using Marco.Core.Snmp;

namespace Marco.Inventory.Snmp;

/// <summary>
/// SNMP v1/v2c over a connected UDP socket. One socket per session (a scan runs at most a few dozen hosts at
/// once, so demultiplexing a shared socket buys nothing). Connecting the socket filters cross-talk and, on
/// Windows, surfaces ICMP port-unreachable as a <see cref="SocketError.ConnectionReset"/> — which is how
/// "SNMP is switched off" is told apart from "nobody answered" (wrong community or a firewall).
/// </summary>
public sealed class UdpSnmpSession : ISnmpSession
{
    private const int MaxOidsPerGet = 15; // small embedded agents reject bigger PDUs with tooBig

    private readonly string _community;
    private readonly SnmpOptions _options;
    private readonly Lazy<Task<UdpClient>> _client;
    private int _nextRequestId = Random.Shared.Next(1, int.MaxValue / 2);
    private bool _everAnswered;

    public string Host { get; }
    public int Port { get; }
    public SnmpVersion Version { get; }

    public UdpSnmpSession(string host, int port, string community, SnmpVersion version, SnmpOptions options)
    {
        Host = host;
        Port = port;
        _community = community;
        Version = version;
        _options = options;
        _client = new Lazy<Task<UdpClient>>(OpenAsync);
    }

    private async Task<UdpClient> OpenAsync()
    {
        IPAddress? ip = IPAddress.TryParse(Host, out var parsed) ? parsed : null;
        if (ip is null)
        {
            var all = await Dns.GetHostAddressesAsync(Host).ConfigureAwait(false);
            ip = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault()
                 ?? throw new SnmpException(SnmpFailureKind.NoResponse, $"Could not resolve {Host}.");
        }
        var client = new UdpClient(ip.AddressFamily);
        client.Connect(new IPEndPoint(ip, Port));
        return client;
    }

    // ---------------------------------------------------------------- GET

    public async Task<IReadOnlyList<SnmpVarBind>> GetAsync(IReadOnlyList<SnmpOid> oids, CancellationToken ct)
    {
        var result = new List<SnmpVarBind>(oids.Count);
        for (int i = 0; i < oids.Count; i += MaxOidsPerGet)
        {
            var chunk = oids.Skip(i).Take(MaxOidsPerGet).ToList();
            result.AddRange(await GetChunkAsync(chunk, ct).ConfigureAwait(false));
        }
        return result;
    }

    private async Task<IReadOnlyList<SnmpVarBind>> GetChunkAsync(List<SnmpOid> oids, CancellationToken ct)
    {
        // v1 reports a missing instance as noSuchName for the whole PDU with error-index pointing at the culprit;
        // drop that OID (recording NoSuchInstance for it) and ask again for the rest. Bounded by the chunk size.
        var missing = new Dictionary<SnmpOid, SnmpValue>();
        var remaining = oids.ToList();
        for (int round = 0; round <= oids.Count && remaining.Count > 0; round++)
        {
            int id = NextId();
            var resp = await ExchangeAsync(SnmpMessage.BuildGet(Version, _community, id, remaining), id, ct).ConfigureAwait(false);
            switch (resp.ErrorStatus)
            {
                case SnmpErrorStatus.NoError:
                    return Merge(oids, resp.VarBinds, missing);
                case SnmpErrorStatus.NoSuchName when resp.ErrorIndex >= 1 && resp.ErrorIndex <= remaining.Count:
                    missing[remaining[resp.ErrorIndex - 1]] = SnmpValue.NoSuchInstance;
                    remaining.RemoveAt(resp.ErrorIndex - 1);
                    continue;
                case SnmpErrorStatus.NoSuchName:
                    foreach (var o in remaining) missing[o] = SnmpValue.NoSuchInstance; // agent didn't say which
                    remaining.Clear();
                    continue;
                case SnmpErrorStatus.TooBig when remaining.Count > 1:
                    {
                        int half = remaining.Count / 2;
                        var a = await GetChunkAsync(remaining.Take(half).ToList(), ct).ConfigureAwait(false);
                        var b = await GetChunkAsync(remaining.Skip(half).ToList(), ct).ConfigureAwait(false);
                        return Merge(oids, a.Concat(b).ToList(), missing);
                    }
                case SnmpErrorStatus.TooBig:
                    throw new SnmpException(SnmpFailureKind.TooBig, "The agent reports the response would be too big.");
                default:
                    throw new SnmpException(SnmpFailureKind.ProtocolError, $"SNMP error-status {resp.ErrorStatus} at index {resp.ErrorIndex}.");
            }
        }
        return Merge(oids, Array.Empty<SnmpVarBind>(), missing);
    }

    /// <summary>Re-order answers to match the request order, filling requested-but-unanswered OIDs with
    /// NoSuchInstance so callers can index by position.</summary>
    private static IReadOnlyList<SnmpVarBind> Merge(List<SnmpOid> requested, IReadOnlyList<SnmpVarBind> answered, Dictionary<SnmpOid, SnmpValue> missing)
    {
        var byOid = new Dictionary<SnmpOid, SnmpValue>();
        foreach (var vb in answered) byOid[vb.Oid] = vb.Value;
        var result = new List<SnmpVarBind>(requested.Count);
        foreach (var o in requested)
        {
            if (byOid.TryGetValue(o, out var v)) result.Add(new SnmpVarBind(o, v));
            else if (missing.TryGetValue(o, out var m)) result.Add(new SnmpVarBind(o, m));
            else result.Add(new SnmpVarBind(o, SnmpValue.NoSuchInstance));
        }
        return result;
    }

    // ---------------------------------------------------------------- WALK

    public async Task<IReadOnlyList<SnmpVarBind>> WalkAsync(SnmpOid prefix, int maxRows, CancellationToken ct)
    {
        var rows = new List<SnmpVarBind>();
        var last = prefix;
        bool bulk = Version == SnmpVersion.V2c;
        int maxRep = Math.Max(1, _options.MaxRepetitions);

        while (rows.Count < maxRows)
        {
            ct.ThrowIfCancellationRequested();
            int id = NextId();
            SnmpResponse resp;
            if (bulk)
            {
                try
                {
                    resp = await ExchangeAsync(SnmpMessage.BuildGetBulk(_community, id, new[] { last }, maxRep), id, ct).ConfigureAwait(false);
                }
                catch (SnmpException ex) when (ex.Kind == SnmpFailureKind.NoResponse && _everAnswered)
                {
                    bulk = false; // this agent answers GET but ignores GETBULK — finish the walk with GETNEXT
                    continue;
                }
                if (resp.ErrorStatus == SnmpErrorStatus.TooBig)
                {
                    if (maxRep == 1) { bulk = false; continue; }
                    maxRep = Math.Max(1, maxRep / 2);
                    continue;
                }
            }
            else
            {
                resp = await ExchangeAsync(SnmpMessage.BuildGetNext(Version, _community, id, new[] { last }), id, ct).ConfigureAwait(false);
                if (resp.ErrorStatus == SnmpErrorStatus.NoSuchName) break; // v1: end of MIB
            }

            if (resp.ErrorStatus != SnmpErrorStatus.NoError)
                throw new SnmpException(SnmpFailureKind.ProtocolError, $"SNMP error-status {resp.ErrorStatus} during walk.");
            if (resp.VarBinds.Count == 0) break;

            bool done = false;
            foreach (var vb in resp.VarBinds)
            {
                if (vb.Value.Kind == SnmpValueKind.EndOfMibView) { done = true; break; }
                if (!prefix.IsPrefixOf(vb.Oid)) { done = true; break; }
                if (vb.Oid.CompareTo(last) <= 0) { done = true; break; } // non-increasing: broken agent, stop
                rows.Add(vb);
                last = vb.Oid;
                if (rows.Count >= maxRows) { done = true; break; }
            }
            if (done) break;
        }
        return rows;
    }

    // ---------------------------------------------------------------- transport

    private int NextId()
    {
        int id = _nextRequestId++;
        if (_nextRequestId <= 0) _nextRequestId = 1;
        return id;
    }

    private async Task<SnmpResponse> ExchangeAsync(byte[] request, int requestId, CancellationToken ct)
    {
        var client = await _client.Value.ConfigureAwait(false);
        int attempts = Math.Max(1, _options.Retries + 1);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await client.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                throw new SnmpException(SnmpFailureKind.PortUnreachable, "The host reports no SNMP agent on that port (ICMP port unreachable).", ex);
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, _options.TimeoutMs));
            while (true)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) break;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(remaining);
                UdpReceiveResult received;
                try
                {
                    received = await client.ReceiveAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break; // timed out this attempt
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    throw new SnmpException(SnmpFailureKind.PortUnreachable, "The host reports no SNMP agent on that port (ICMP port unreachable).", ex);
                }

                // A late answer to an earlier request-id is routine after a retransmit: keep listening.
                if (SnmpMessage.PeekRequestId(received.Buffer) != requestId) continue;
                var resp = SnmpMessage.Parse(received.Buffer);
                _everAnswered = true;
                return resp;
            }
        }
        throw new SnmpException(SnmpFailureKind.NoResponse, "No SNMP response (agent off, firewalled, or wrong community string).");
    }

    public void Dispose()
    {
        if (_client.IsValueCreated && _client.Value.IsCompletedSuccessfully)
            _client.Value.Result.Dispose();
    }
}

public sealed class SnmpSessionFactory : ISnmpSessionFactory
{
    public ISnmpSession Create(string host, int port, string community, SnmpVersion version, SnmpOptions options)
        => new UdpSnmpSession(host, port, community, version, options);
}
