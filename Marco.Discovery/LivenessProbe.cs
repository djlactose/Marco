using System.Net.NetworkInformation;
using System.Net.Sockets;
using Marco.Core.Model;
using Marco.Core.Scanning;

namespace Marco.Discovery;

/// <summary>
/// ICMP-echo liveness with optional TCP-connect fallback, plus classification-port probing. A host is alive if
/// ICMP answers OR any probed port is open — so a printer that answers only on 9100 and filters ICMP is still
/// found. TCP probes carry a hard per-connect timeout and are always disposed, so a filtered address can never
/// pin a socket for ~20s and exhaust the ephemeral port range on a wide sweep.
/// </summary>
public sealed class LivenessProbe : ILivenessProbe
{
    public async Task<LivenessResult> ProbeAsync(string address, ScanSettings settings, CancellationToken ct)
    {
        bool alive = false;
        var method = DiscoveryMethod.None;
        int? ttl = null;

        if (settings.IcmpEnabled)
        {
            var (ok, replyTtl) = await TryIcmpAsync(address, settings.IcmpTimeoutMs, ct).ConfigureAwait(false);
            if (ok)
            {
                alive = true;
                method = DiscoveryMethod.Icmp;
                ttl = replyTtl;
            }
        }

        ct.ThrowIfCancellationRequested();

        // Decide which ports to open. Classification needs the full set (liveness ports feed the Windows rule);
        // otherwise we only need the liveness set, and only if ICMP hasn't already proven the host up.
        bool needTcpForLiveness = !alive && (settings.TcpFallbackEnabled || !settings.IcmpEnabled);
        IReadOnlyCollection<int> portsToProbe =
            settings.ClassificationEnabled ? settings.AllProbePorts().ToArray()
            : needTcpForLiveness ? settings.LivenessPorts.ToArray()
            : Array.Empty<int>();

        var openPorts = new List<int>();
        if (portsToProbe.Count > 0)
        {
            var tasks = portsToProbe.Select(async port =>
            {
                bool open = await TryConnectAsync(address, port, settings.TcpTimeoutMs, ct).ConfigureAwait(false);
                return (port, open);
            });
            foreach (var (port, open) in await Task.WhenAll(tasks).ConfigureAwait(false))
                if (open) openPorts.Add(port);
        }

        if (!alive && openPorts.Count > 0)
        {
            alive = true;
            method = DiscoveryMethod.Tcp;
        }

        if (!alive)
            return LivenessResult.Dead;

        openPorts.Sort();
        return new LivenessResult(true, method, ttl, openPorts);
    }

    /// <summary>ICMP echo that honours cancellation, so a Cancel doesn't wait out every in-flight ping's timeout.
    /// A token cancel surfaces from Ping as an unwrapped OperationCanceledException; a plain timeout is
    /// IPStatus.TimedOut (or a PingException on some stacks) and must still read as "dead", not "cancelled".</summary>
    private static async Task<(bool ok, int? ttl)> TryIcmpAsync(string address, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), null, null, ct)
                .ConfigureAwait(false);
            if (reply.Status == IPStatus.Success)
                return (true, reply.Options?.Ttl);
            return (false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (false, null); // internal timeout token on some platforms — not our cancellation
        }
        catch (PingException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (PingException)
        {
            return (false, null);
        }
        catch (ArgumentException)
        {
            return (false, null); // unresolvable host name
        }
    }

    private static async Task<bool> TryConnectAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real cancellation, not our timeout
        }
        catch
        {
            return false; // refused, timed out, unreachable, or unresolvable
        }
    }
}
