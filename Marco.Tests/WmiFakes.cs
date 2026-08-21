using System.Text.RegularExpressions;
using Marco.Core.Inventory;
using Marco.Core.Wmi;

namespace Marco.Tests;

internal static class WmiFakeBuilders
{
    public static WmiObject Obj(params (string, object?)[] props)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in props) dict[k] = v;
        return new WmiObject(dict);
    }

    public static RegistryKeyData Key(string name, DateTime? lastWrite, params (string, object?)[] values)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values) dict[k] = v;
        return new RegistryKeyData(name, dict, lastWrite);
    }
}

/// <summary>Fake WMI session keyed by the class name in the WQL FROM clause. A class can be set to throw.</summary>
internal sealed class FakeWmiSession : IWmiSession
{
    public string Host { get; }
    public Dictionary<string, List<WmiObject>> ByClass { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WmiException> ThrowByClass { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeWmiSession(string host = "testhost") => Host = host;

    public FakeWmiSession With(string className, params WmiObject[] rows)
    {
        ByClass[className] = rows.ToList();
        return this;
    }

    public FakeWmiSession Throws(string className, WmiException ex)
    {
        ThrowByClass[className] = ex;
        return this;
    }

    /// <summary>Simulate an older host whose class lacks a property: any SELECT naming it is rejected
    /// (WBEM_E_INVALID_QUERY), while narrower queries against the same class still succeed.</summary>
    public FakeWmiSession ThrowsWhenWqlContains(string fragment, WmiException? ex = null)
    {
        _throwWhenWqlContains.Add((fragment, ex ?? new WmiException(WmiFailureKind.Unknown, $"Invalid query: {fragment}")));
        return this;
    }

    private readonly List<(string Fragment, WmiException Ex)> _throwWhenWqlContains = new();

    public List<string> Queries { get; } = new();

    public Task<IReadOnlyList<WmiObject>> QueryAsync(string ns, string wql, CancellationToken ct)
    {
        Queries.Add(wql);
        var cls = ExtractClass(wql);
        if (ThrowByClass.TryGetValue(cls, out var ex)) throw ex;
        foreach (var (fragment, fex) in _throwWhenWqlContains)
            if (wql.Contains(fragment, StringComparison.OrdinalIgnoreCase)) throw fex;
        IReadOnlyList<WmiObject> rows = ByClass.TryGetValue(cls, out var list) ? list : new List<WmiObject>();
        return Task.FromResult(rows);
    }

    public Task<WmiObject?> InvokeAsync(string ns, string cls, string method,
        IReadOnlyDictionary<string, object?> args, CancellationToken ct)
        => Task.FromResult<WmiObject?>(null);

    public void Dispose() { }

    private static string ExtractClass(string wql)
    {
        var m = Regex.Match(wql, @"FROM\s+(\w+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }
}

internal sealed class FakeWmiSessionFactory : IWmiSessionFactory
{
    private readonly Func<string, WmiCredential?, IWmiSession> _connect;
    public List<string?> AttemptedUsernames { get; } = new();

    public FakeWmiSessionFactory(Func<string, WmiCredential?, IWmiSession> connect) => _connect = connect;

    public Task<IWmiSession> ConnectAsync(string host, WmiCredential? credential, CancellationToken ct)
    {
        AttemptedUsernames.Add(credential?.UseCurrentToken == true ? "<token>" : credential?.Username);
        return Task.FromResult(_connect(host, credential));
    }
}

internal sealed class FakeRemoteRegistry : IRemoteRegistry
{
    public bool SupportsLastWriteTime { get; init; } = true;
    public Dictionary<string, List<RegistryKeyData>> Subkeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> SubkeyNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, object?>> KeyValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ThrowOnAccess { get; init; }

    private void Guard()
    {
        if (ThrowOnAccess) throw new Marco.Core.Wmi.WmiException(Marco.Core.Wmi.WmiFailureKind.AccessDenied, "no registry");
    }

    public IReadOnlyList<RegistryKeyData> EnumerateSubkeys(RegistryRoot root, string path, IReadOnlyCollection<string> valueNames)
    {
        Guard();
        return Subkeys.TryGetValue($"{root}:{path}", out var list) ? list : new List<RegistryKeyData>();
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string path)
    {
        Guard();
        return SubkeyNames.TryGetValue($"{root}:{path}", out var list) ? list : new List<string>();
    }

    public IReadOnlyDictionary<string, object?> GetValues(RegistryRoot root, string path, IReadOnlyCollection<string> valueNames)
    {
        Guard();
        return KeyValues.TryGetValue($"{root}:{path}", out var v) ? v : new Dictionary<string, object?>();
    }

    public void Dispose() { }
}

internal sealed class FakeRemoteRegistryFactory : IRemoteRegistryFactory
{
    private readonly IRemoteRegistry _registry;
    public FakeRemoteRegistryFactory(IRemoteRegistry registry) => _registry = registry;
    public IRemoteRegistry Create(string host, WmiCredential? credential, IWmiSession wmiSession) => _registry;
}

/// <summary>Connect blocks until the gate is released — proves a Stop mid-connect unblocks the awaiting
/// caller promptly (the abandoned connect drains via <see cref="Marco.Core.Threading.AbandonableTask"/>,
/// exactly like the real SystemManagementWmiSessionFactory).</summary>
internal sealed class GatedWmiSessionFactory : IWmiSessionFactory
{
    public TaskCompletionSource<IWmiSession> Gate { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IWmiSession> ConnectAsync(string host, WmiCredential? credential, CancellationToken ct)
        => Marco.Core.Threading.AbandonableTask.AwaitOrAbandonAsync(Gate.Task, ct, onAbandonedResult: s => s.Dispose());
}
