using Renci.SshNet;
using Renci.SshNet.Common;

namespace Marco.Inventory.Ssh;

/// <summary><see cref="ISshSession"/> over SSH.NET. Password auth only for now (matches the operator credential
/// model); key auth is a later addition. Only ever runs read-only commands — never SCP, which is where the
/// library's known download vulnerability lives.</summary>
public sealed class SshNetSession : ISshSession
{
    private readonly SshClient _client;
    private bool _disposed;

    public string Host { get; }

    internal SshNetSession(SshClient client, string host)
    {
        _client = client;
        Host = host;
    }

    public SshResult Run(string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cmd = _client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(30);
        try
        {
            var stdout = cmd.Execute();
            return new SshResult(cmd.ExitStatus ?? -1, stdout ?? "", cmd.Error ?? "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SshResult(-1, "", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_client.IsConnected) _client.Disconnect(); } catch { /* ignore */ }
        _client.Dispose();
    }
}

public sealed class SshNetSessionFactory : ISshSessionFactory
{
    public ISshSession Connect(string host, int port, string username, string? password, int timeoutSeconds, CancellationToken ct)
    {
        var auth = new PasswordAuthenticationMethod(username, password ?? "");
        var connInfo = new ConnectionInfo(host, port <= 0 ? 22 : port, username, auth)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        var client = new SshClient(connInfo);
        try
        {
            client.Connect();
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new SshException(SshFailureKind.AuthFailed, ex.Message, ex);
        }
        catch (SshOperationTimeoutException ex)
        {
            client.Dispose();
            throw new SshException(SshFailureKind.Timeout, ex.Message, ex);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new SshException(SshFailureKind.Unreachable, ex.Message, ex);
        }

        return new SshNetSession(client, host);
    }
}
