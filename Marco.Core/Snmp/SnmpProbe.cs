namespace Marco.Core.Snmp;

/// <summary>What a successful probe learned: which version answered and the system scalars it returned.</summary>
public sealed record SnmpProbeResult(SnmpVersion Version, IReadOnlyDictionary<SnmpOid, SnmpValue> System)
{
    public string? Description => Get(SnmpProbe.SysDescr)?.AsText();
    public SnmpOid? ObjectId => Get(SnmpProbe.SysObjectId)?.Oid;
    public long? UpTimeTicks => Get(SnmpProbe.SysUpTime)?.Int;
    private SnmpValue? Get(SnmpOid oid) => System.TryGetValue(oid, out var v) ? v : null;
}

/// <summary>
/// The "does this community string work?" exchange shared by the inventory runner and the credential
/// verifier: GET the three always-present system scalars. With no version pinned, v2c and v1 are sent
/// concurrently on separate sockets and the first reply wins — SNMP drops bad-community requests silently, so
/// trying versions in sequence would double the cost of the common "no answer" case.
/// </summary>
public static class SnmpProbe
{
    public static readonly SnmpOid SysDescr = SnmpOid.Parse("1.3.6.1.2.1.1.1.0");
    public static readonly SnmpOid SysObjectId = SnmpOid.Parse("1.3.6.1.2.1.1.2.0");
    public static readonly SnmpOid SysUpTime = SnmpOid.Parse("1.3.6.1.2.1.1.3.0");
    private static readonly SnmpOid[] ProbeOids = { SysDescr, SysObjectId, SysUpTime };
    /// <summary>How long to wait for a v2c reply after v1 already answered.</summary>
    public const int V2cGraceMs = 400;

    /// <summary>Null when nothing answered; throws <see cref="SnmpException"/> (PortUnreachable) when the host
    /// actively refused UDP 161, which means "SNMP is off" rather than "wrong community".</summary>
    public static async Task<SnmpProbeResult?> ProbeAsync(
        ISnmpSessionFactory factory, string host, int port, string community, SnmpVersion? pinned,
        SnmpOptions options, CancellationToken ct)
    {
        var versions = pinned is { } v ? new[] { v } : new[] { SnmpVersion.V2c, SnmpVersion.V1 };
        var sessions = versions.Select(ver => factory.Create(host, port, community, ver, options)).ToList();
        var tasks = sessions.Select(s => ProbeOneAsync(s, ct)).ToList();
        try
        {
            var pending = tasks.ToList();
            SnmpException? unreachable = null;
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                try
                {
                    var r = await done.ConfigureAwait(false);
                    if (r is null) continue;
                    // Both versions usually answer; prefer v2c (GETBULK walks) when it is only a moment behind.
                    if (r.Version == SnmpVersion.V1 && pending.Count > 0)
                    {
                        var v2 = pending[0];
                        await Task.WhenAny(v2, Task.Delay(V2cGraceMs, ct)).ConfigureAwait(false);
                        if (v2.IsCompletedSuccessfully && v2.Result is { } better) return better;
                    }
                    return r;
                }
                catch (SnmpException ex) when (ex.Kind == SnmpFailureKind.PortUnreachable) { unreachable = ex; }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* malformed reply, socket error: treat as no answer on this version */ }
            }
            if (unreachable is not null) throw unreachable;
            return null;
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
            // Disposing a socket faults the other version's still-pending receive; observe it so it never
            // surfaces as an unobserved-task exception.
            foreach (var t in tasks) _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static async Task<SnmpProbeResult?> ProbeOneAsync(ISnmpSession session, CancellationToken ct)
    {
        try
        {
            var binds = await session.GetAsync(ProbeOids, ct).ConfigureAwait(false);
            var map = new Dictionary<SnmpOid, SnmpValue>();
            foreach (var vb in binds) if (vb.Value.HasValue) map[vb.Oid] = vb.Value;
            return new SnmpProbeResult(session.Version, map);
        }
        catch (SnmpException ex) when (ex.Kind == SnmpFailureKind.NoResponse)
        {
            return null;
        }
    }
}
