using System.Diagnostics;
using Marco.Core.Snmp;
using Marco.Inventory.Snmp;
using Xunit;

namespace Marco.Tests;

/// <summary>Real-socket tests of the UDP transport against <see cref="FakeSnmpAgent"/> on loopback.</summary>
public class UdpSnmpSessionTests
{
    private static SnmpOidTable SmallTable() => new SnmpOidTable()
        .Str("1.3.6.1.2.1.1.1.0", "Fake printer")
        .Oid("1.3.6.1.2.1.1.2.0", "1.3.6.1.4.1.11.2.3.9.1")
        .Ticks("1.3.6.1.2.1.1.3.0", 4294967295)
        .Str("1.3.6.1.2.1.1.5.0", "fake01")
        .Int("1.3.6.1.2.1.43.11.1.1.9.1.1", -3)
        .Int("1.3.6.1.2.1.43.11.1.1.9.1.2", 80)
        .Int("1.3.6.1.2.1.43.11.1.1.9.1.10", 5)   // index 10 must sort after 2, not between 1 and 2
        .Int("1.3.6.1.2.1.43.12.1.1.4.1.1", 7);

    private static UdpSnmpSession Session(FakeSnmpAgent agent, SnmpVersion v = SnmpVersion.V2c, int timeoutMs = 1500, int retries = 1, int maxRep = 20)
        => new("127.0.0.1", agent.Port, "public", v, new SnmpOptions(timeoutMs, retries, maxRep));

    [Theory]
    [InlineData(SnmpVersion.V2c)]
    [InlineData(SnmpVersion.V1)]
    public async Task Get_ReturnsValues_AndMissingAsNoSuchInstance(SnmpVersion version)
    {
        using var agent = new FakeSnmpAgent(SmallTable());
        using var s = Session(agent, version);
        var r = await s.GetAsync(new[]
        {
            SnmpOid.Parse("1.3.6.1.2.1.1.1.0"),
            SnmpOid.Parse("1.3.6.1.2.1.1.99.0"),   // absent
            SnmpOid.Parse("1.3.6.1.2.1.1.3.0"),
            SnmpOid.Parse("1.3.6.1.2.1.43.11.1.1.9.1.1"),
        }, default);

        Assert.Equal(4, r.Count);
        Assert.Equal("Fake printer", r[0].Value.AsText());
        Assert.Equal(SnmpValueKind.NoSuchInstance, r[1].Value.Kind);   // v1: via the noSuchName retry loop
        Assert.Equal(4294967295L, r[2].Value.Int);
        Assert.Equal(-3, r[3].Value.Int);
    }

    [Theory]
    [InlineData(SnmpVersion.V2c)]
    [InlineData(SnmpVersion.V1)]
    public async Task Walk_StaysInsidePrefix_InNumericOrder(SnmpVersion version)
    {
        using var agent = new FakeSnmpAgent(SmallTable());
        using var s = Session(agent, version);
        var rows = await s.WalkAsync(SnmpOid.Parse("1.3.6.1.2.1.43.11.1.1.9"), 100, default);

        Assert.Equal(new long?[] { -3L, 80L, 5L }, rows.Select(r => r.Value.Int).ToArray());
        Assert.All(rows, r => Assert.True(SnmpOid.Parse("1.3.6.1.2.1.43.11.1.1.9").IsPrefixOf(r.Oid)));
        Assert.Equal(version == SnmpVersion.V2c ? Ber.PduGetBulk : Ber.PduGetNext, agent.PduTypesSeen.Last());
    }

    [Fact]
    public async Task Walk_AtEndOfMib_Stops()
    {
        using var agent = new FakeSnmpAgent(SmallTable());
        using var s = Session(agent);
        var rows = await s.WalkAsync(SnmpOid.Parse("1.3.6.1.2.1.43.12"), 100, default);
        Assert.Single(rows);
        var none = await s.WalkAsync(SnmpOid.Parse("1.3.6.1.2.1.99"), 100, default);
        Assert.Empty(none);
    }

    [Fact]
    public async Task Walk_RespectsMaxRows()
    {
        using var agent = new FakeSnmpAgent(SmallTable());
        using var s = Session(agent, maxRep: 1);
        var rows = await s.WalkAsync(SnmpOid.Parse("1.3.6.1.2.1"), 3, default);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Walk_TooBig_HalvesRepetitions()
    {
        using var agent = new FakeSnmpAgent(SmallTable()) { TooBigAboveRepetitions = 5 };
        using var s = Session(agent, maxRep: 20);
        var rows = await s.WalkAsync(SnmpOid.Parse("1.3.6.1.2.1.43.11.1.1.9"), 100, default);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Walk_AgentIgnoresGetBulk_FallsBackToGetNext()
    {
        using var agent = new FakeSnmpAgent(SmallTable()) { IgnoreGetBulk = true };
        using var s = Session(agent, timeoutMs: 300, retries: 0);
        _ = await s.GetAsync(new[] { SnmpOid.Parse("1.3.6.1.2.1.1.1.0") }, default); // proves the agent is alive
        var rows = await s.WalkAsync(SnmpOid.Parse("1.3.6.1.2.1.43.11.1.1.9"), 100, default);
        Assert.Equal(3, rows.Count);
        Assert.Contains(Ber.PduGetNext, agent.PduTypesSeen);
    }

    [Fact]
    public async Task DroppedDatagram_IsRetried()
    {
        using var agent = new FakeSnmpAgent(SmallTable()) { DropFirst = 1 };
        using var s = Session(agent, timeoutMs: 300, retries: 1);
        var r = await s.GetAsync(new[] { SnmpOid.Parse("1.3.6.1.2.1.1.1.0") }, default);
        Assert.Equal("Fake printer", r[0].Value.AsText());
        Assert.Equal(2, agent.RequestsSeen);
    }

    [Fact]
    public async Task WrongCommunity_IsSilence_NoResponse()
    {
        using var agent = new FakeSnmpAgent(SmallTable()) { Community = "secret" };
        using var s = Session(agent, timeoutMs: 200, retries: 1);
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<SnmpException>(() => s.GetAsync(new[] { SnmpOid.Parse("1.3.6.1.2.1.1.1.0") }, default));
        Assert.Equal(SnmpFailureKind.NoResponse, ex.Kind);
        Assert.True(sw.ElapsedMilliseconds >= 350, $"should have waited for both attempts, took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(2, agent.RequestsSeen);
    }

    [Fact]
    public async Task ClosedPort_IsPortUnreachable()
    {
        int port;
        using (var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
            port = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port; // released on dispose → nothing listens there
        using var s = new UdpSnmpSession("127.0.0.1", port, "public", SnmpVersion.V2c, new SnmpOptions(1000, 0));
        var ex = await Assert.ThrowsAsync<SnmpException>(() => s.GetAsync(new[] { SnmpOid.Parse("1.3.6.1.2.1.1.1.0") }, default));
        Assert.Equal(SnmpFailureKind.PortUnreachable, ex.Kind);
    }

    [Fact]
    public async Task Cancel_WhileWaiting_ReleasesPromptly()
    {
        using var agent = new FakeSnmpAgent(SmallTable()) { Silent = true };
        using var s = Session(agent, timeoutMs: 10_000, retries: 3);
        using var cts = new CancellationTokenSource(150);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.GetAsync(new[] { SnmpOid.Parse("1.3.6.1.2.1.1.1.0") }, cts.Token));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Probe_PicksTheVersionThatAnswers()
    {
        using var agent = new FakeSnmpAgent(SmallTable());
        agent.AnswerVersions.Remove(SnmpVersion.V2c); // v1-only agent
        var factory = new SnmpSessionFactory();
        var r = await SnmpProbe.ProbeAsync(factory, "127.0.0.1", agent.Port, "public", null, new SnmpOptions(500, 0), default);
        Assert.NotNull(r);
        Assert.Equal(SnmpVersion.V1, r!.Version);
        Assert.Equal("Fake printer", r.Description);
        Assert.Equal(4294967295L, r.UpTimeTicks);
    }

    [Fact]
    public async Task Probe_PrefersV2c_WhenBothVersionsAnswer()
    {
        using var agent = new FakeSnmpAgent(SmallTable());
        for (int i = 0; i < 5; i++) // the race is timing-dependent; the grace period must make v2c win every time
        {
            var r = await SnmpProbe.ProbeAsync(new SnmpSessionFactory(), "127.0.0.1", agent.Port, "public", null, new SnmpOptions(500, 0), default);
            Assert.Equal(SnmpVersion.V2c, r!.Version);
        }
    }

    [Fact]
    public async Task Probe_WrongCommunity_ReturnsNull()
    {
        using var agent = new FakeSnmpAgent(SmallTable()) { Community = "secret" };
        var r = await SnmpProbe.ProbeAsync(new SnmpSessionFactory(), "127.0.0.1", agent.Port, "public", null, new SnmpOptions(200, 0), default);
        Assert.Null(r);
    }
}
