using Marco.Inventory.Ssh;

namespace Marco.Tests;

/// <summary>Fake SSH session that returns canned stdout when a registered substring appears in the command.</summary>
internal sealed class FakeSshSession : ISshSession
{
    private readonly List<(string Match, string Output)> _responses = new();
    public string Host { get; }
    public List<string> Commands { get; } = new();

    public FakeSshSession(string host = "linuxhost") => Host = host;

    public FakeSshSession On(string commandContains, string output)
    {
        _responses.Add((commandContains, output));
        return this;
    }

    public SshResult Run(string command, CancellationToken ct)
    {
        Commands.Add(command);
        // Longest match wins so more specific commands take precedence.
        var hit = _responses
            .Where(r => command.Contains(r.Match, StringComparison.Ordinal))
            .OrderByDescending(r => r.Match.Length)
            .Select(r => (string?)r.Output)
            .FirstOrDefault();
        return new SshResult(0, hit ?? "", "");
    }

    public void Dispose() { }
}

internal sealed class FakeSshSessionFactory : ISshSessionFactory
{
    private readonly Func<string, string, string?, ISshSession> _connect;
    public List<string> AttemptedUsers { get; } = new();

    public FakeSshSessionFactory(Func<string, string, string?, ISshSession> connect) => _connect = connect;

    public ISshSession Connect(string host, int port, string username, string? password, int timeoutSeconds, CancellationToken ct)
    {
        AttemptedUsers.Add(username);
        return _connect(host, username, password);
    }
}
