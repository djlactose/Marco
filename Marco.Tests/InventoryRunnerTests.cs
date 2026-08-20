using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;
using Xunit;

namespace Marco.Tests;

public class InventoryRunnerTests
{
    private sealed class TestCollector : IInventoryCollector
    {
        private readonly Action<Machine>? _onCollect;
        private readonly Exception? _throw;
        public string Name { get; }

        public TestCollector(string name, Action<Machine>? onCollect = null, Exception? toThrow = null)
        {
            Name = name; _onCollect = onCollect; _throw = toThrow;
        }

        public Task CollectAsync(InventoryContext ctx, Machine machine, CancellationToken ct)
        {
            if (_throw is not null) throw _throw;
            _onCollect?.Invoke(machine);
            return Task.CompletedTask;
        }
    }

    private static InventoryRunner Runner(IWmiSessionFactory factory, params IInventoryCollector[] collectors)
        => new(factory, new FakeRemoteRegistryFactory(new FakeRemoteRegistry()), collectors);

    private static FakeWmiSessionFactory AcceptAll()
        => new((h, _) => new FakeWmiSession(h));

    private static List<CredentialCandidate> Creds(params string[] users)
        => users.Select(u => new CredentialCandidate(u, new WmiCredential { Username = u })).ToList();

    [Fact]
    public async Task AllCollectorsSucceed_StatusDone()
    {
        var m = new Machine("10.0.0.1");
        var runner = Runner(AcceptAll(),
            new TestCollector("A", mm => mm.System.Manufacturer = "x"),
            new TestCollector("B"));

        var outcome = await runner.InventoryAsync(m, Creds("token"), null, null, default);

        Assert.True(outcome.Authenticated);
        Assert.Equal(MachineStatus.Done, m.Status);
        Assert.All(m.Collectors, c => Assert.Equal(CollectorStatus.Ok, c.Status));
    }

    [Fact]
    public async Task OneCollectorThrows_IsIsolated_StatusPartial()
    {
        var m = new Machine("10.0.0.2");
        var runner = Runner(AcceptAll(),
            new TestCollector("A", mm => mm.System.Model = "ok"),
            new TestCollector("B", toThrow: new InvalidOperationException("kaboom")),
            new TestCollector("C", mm => mm.Os.Caption = "ok"));

        await runner.InventoryAsync(m, Creds("token"), null, null, default);

        Assert.Equal(MachineStatus.Partial, m.Status);
        Assert.Equal(CollectorStatus.Ok, m.Collectors.Single(c => c.Name == "A").Status);
        Assert.Equal(CollectorStatus.Failed, m.Collectors.Single(c => c.Name == "B").Status);
        Assert.Equal(CollectorStatus.Ok, m.Collectors.Single(c => c.Name == "C").Status);
        Assert.Equal("ok", m.Os.Caption); // C still ran after B threw
    }

    [Fact]
    public async Task NotSupportedCollector_DoesNotDowngradeToPartial()
    {
        var m = new Machine("10.0.0.3");
        var runner = Runner(AcceptAll(),
            new TestCollector("A"),
            new TestCollector("B", toThrow: new WmiException(WmiFailureKind.NotSupported, "no class")));

        await runner.InventoryAsync(m, Creds("token"), null, null, default);

        Assert.Equal(MachineStatus.Done, m.Status); // NotSupported is expected, not a failure
        Assert.Equal(CollectorStatus.NotSupported, m.Collectors.Single(c => c.Name == "B").Status);
    }

    [Fact]
    public async Task TriesCredentialsInOrder_UntilOneAuthenticates()
    {
        var factory = new FakeWmiSessionFactory((h, cred) =>
            cred?.Username == "good" ? new FakeWmiSession(h)
            : throw new WmiException(WmiFailureKind.AuthFailed, "bad creds"));

        var m = new Machine("10.0.0.4");
        var outcome = await Runner(factory, new TestCollector("A"))
            .InventoryAsync(m, Creds("bad1", "bad2", "good"), null, null, default);

        Assert.True(outcome.Authenticated);
        Assert.Equal("good", outcome.CredentialLabel);
        Assert.Equal(new[] { "bad1", "bad2", "good" }, factory.AttemptedUsernames);
    }

    [Fact]
    public async Task RemembersWinningCredential_AndTriesItFirstNextTime()
    {
        var factory = new FakeWmiSessionFactory((h, cred) =>
            cred?.Username == "good" ? new FakeWmiSession(h)
            : throw new WmiException(WmiFailureKind.AuthFailed, "bad"));
        var runner = Runner(factory, new TestCollector("A"));
        var creds = Creds("bad", "good");

        await runner.InventoryAsync(new Machine("10.0.0.5"), creds, null, null, default);
        factory.AttemptedUsernames.Clear();
        await runner.InventoryAsync(new Machine("10.0.0.5"), creds, null, null, default);

        // Second pass on the same host tries the remembered 'good' first.
        Assert.Equal("good", factory.AttemptedUsernames[0]);
    }

    [Fact]
    public async Task AllCredentialsRejected_StatusAuthFailed_WithLastHint()
    {
        var factory = new FakeWmiSessionFactory((h, cred) =>
            throw new WmiException(WmiFailureKind.AccessDenied, "denied", hint: "Set LocalAccountTokenFilterPolicy=1"));

        var m = new Machine("10.0.0.6");
        var outcome = await Runner(factory, new TestCollector("A"))
            .InventoryAsync(m, Creds("a", "b"), null, null, default);

        Assert.False(outcome.Authenticated);
        Assert.Equal(MachineStatus.AuthFailed, m.Status);
        Assert.Contains("LocalAccountTokenFilterPolicy", m.StatusDetail);
    }

    [Fact]
    public async Task UnreachableHost_ShortCircuits_WithoutTryingRemainingCredentials()
    {
        var factory = new FakeWmiSessionFactory((h, cred) =>
            throw new WmiException(WmiFailureKind.Unreachable, "RPC unavailable"));

        var m = new Machine("10.0.0.7");
        var outcome = await Runner(factory, new TestCollector("A"))
            .InventoryAsync(m, Creds("a", "b", "c"), null, null, default);

        Assert.False(outcome.Authenticated);
        Assert.Equal(MachineStatus.Unreachable, m.Status);
        Assert.Single(factory.AttemptedUsernames); // stopped after the first unreachable result
    }

    [Fact]
    public async Task PerHostOverride_TakesPrecedence()
    {
        var factory = new FakeWmiSessionFactory((h, cred) =>
            cred?.Username == "override" ? new FakeWmiSession(h)
            : throw new WmiException(WmiFailureKind.AuthFailed, "bad"));

        var overrides = new Dictionary<string, CredentialCandidate>
        {
            ["10.0.0.8"] = new("override", new WmiCredential { Username = "override" }),
        };

        var m = new Machine("10.0.0.8");
        var outcome = await Runner(factory, new TestCollector("A"))
            .InventoryAsync(m, Creds("listed"), overrides, null, default);

        Assert.True(outcome.Authenticated);
        Assert.Equal(new[] { "override" }, factory.AttemptedUsernames);
    }

    [Fact]
    public async Task DisabledCollectors_AreSkipped()
    {
        var m = new Machine("10.0.0.9");
        bool bRan = false;
        var runner = Runner(AcceptAll(),
            new TestCollector("A"),
            new TestCollector("B", _ => bRan = true));

        await runner.InventoryAsync(m, Creds("token"), null, new HashSet<string> { "A" }, default);

        Assert.False(bRan);
        Assert.DoesNotContain(m.Collectors, c => c.Name == "B");
    }

    // --- CurrentActivity (live "what is inventory doing") ---

    [Fact]
    public async Task CurrentActivity_ShowsCollectorName_WhileCollecting_AndClearsAfter()
    {
        var m = new Machine("10.0.0.20");
        string? seenDuringCollect = null;
        var runner = Runner(AcceptAll(),
            new TestCollector("Software", mm => seenDuringCollect = mm.CurrentActivity));

        await runner.InventoryAsync(m, Creds("admin"), null, null, default);

        Assert.Equal("Collecting Software…", seenDuringCollect);
        Assert.Null(m.CurrentActivity);
    }

    [Fact]
    public async Task CurrentActivity_ClearedAfterAuthFailure()
    {
        var m = new Machine("10.0.0.21");
        var rejectAll = new FakeWmiSessionFactory((h, _) =>
            throw new WmiException(WmiFailureKind.AuthFailed, "bad password"));
        var runner = Runner(rejectAll, new TestCollector("A"));

        var outcome = await runner.InventoryAsync(m, Creds("a", "b"), null, null, default);

        Assert.False(outcome.Authenticated);
        Assert.Null(m.CurrentActivity);
    }

    [Fact]
    public async Task CurrentActivity_ClearedWhenCancelledMidRun()
    {
        var m = new Machine("10.0.0.22");
        using var cts = new CancellationTokenSource();
        var runner = Runner(AcceptAll(),
            new TestCollector("A", _ => cts.Cancel()),   // cancel lands between collectors
            new TestCollector("B"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.InventoryAsync(m, Creds("admin"), null, null, cts.Token));

        Assert.Null(m.CurrentActivity);
        Assert.DoesNotContain(m.Collectors, c => c.Name == "B"); // cancelled before B was even recorded
    }

    // --- Stop promptness: a host stuck in connect must release the awaiting caller immediately ---

    [Fact]
    public async Task Cancel_WhileConnectIsStuck_ReleasesPromptly()
    {
        var m = new Machine("10.0.0.23");
        var gated = new GatedWmiSessionFactory();
        var runner = Runner(gated, new TestCollector("A"));
        using var cts = new CancellationTokenSource();

        var run = runner.InventoryAsync(m, Creds("admin"), null, null, cts.Token);
        cts.CancelAfter(50);

        // The gate never opens; only the abandonment path can end the wait.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(m.CurrentActivity);
        gated.Gate.TrySetResult(new FakeWmiSession()); // released late → disposed by the abandonment callback
    }

    // --- Assign-on-completion contract (list refresh fix) ---

    [Fact]
    public async Task CollectorAssignedList_ReplacesTheReference()
    {
        var m = new Machine("10.0.0.24");
        var before = m.Software;
        var assigned = new List<SoftwareEntry> { new() { DisplayName = "App" } };
        var runner = Runner(AcceptAll(), new TestCollector("InstalledSoftware", mm => mm.Software = assigned));

        await runner.InventoryAsync(m, Creds("admin"), null, null, default);

        Assert.NotSame(before, m.Software); // the reference change is what makes bound ItemsControls re-render
        Assert.Same(assigned, m.Software);
        m.Software = null!;
        Assert.NotNull(m.Software); // setter null-coalesces defensively
    }
}
