namespace Marco.Core.Ssh;

/// <summary>Result of running one remote command.</summary>
public readonly record struct SshResult(int ExitStatus, string StdOut, string StdError)
{
    public bool Ok => ExitStatus == 0;
    public static readonly SshResult NotRun = new(-1, "", "");
}

public enum SshFailureKind { AuthFailed, Unreachable, Timeout, Error }

public sealed class SshException : Exception
{
    public SshFailureKind Kind { get; }
    public SshException(SshFailureKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

/// <summary>An authenticated SSH session to one host. Runs commands and returns their output; abstracted so the
/// Linux inventory runner and its parsers are unit-testable against canned command output.</summary>
public interface ISshSession : IDisposable
{
    string Host { get; }
    SshResult Run(string command, CancellationToken ct);
}

/// <summary>Opens SSH sessions. Connecting is where authentication happens, so a bad credential fails here.</summary>
public interface ISshSessionFactory
{
    ISshSession Connect(string host, int port, string username, string? password, int timeoutSeconds, CancellationToken ct);
}
