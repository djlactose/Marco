using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Marco.Core.Ipp;

namespace Marco.Inventory.Ipp;

/// <summary>
/// IPP over plain HTTP on port 631, the form every AirPrint/Mopria-capable printer exposes. The printer's IPP
/// endpoint path is not standardised, so the well-known candidates are tried in order and the one that answers
/// is remembered per host. Requests are IPP/2.0 with a 1.1 retry for older firmware. No credentials, no writes.
/// </summary>
public sealed class HttpIppClient : IIppClient
{
    public const int DefaultPort = 631;
    private static readonly string[] CandidatePaths = { "/ipp/print", "/ipp", "/ipp/printer", "/" };

    private static readonly HttpClient Http = CreateHttp();
    private readonly ConcurrentDictionary<string, string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _port;
    private readonly int _timeoutMs;
    private int _requestId = Random.Shared.Next(1, 1 << 20);

    public HttpIppClient(int port = DefaultPort, int timeoutMs = 6000)
    {
        _port = port;
        _timeoutMs = timeoutMs;
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = false,
            UseProxy = false,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.ExpectContinue = false;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ipp"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Marco/1.0");
        return client;
    }

    public Task<IppResponse> GetPrinterAttributesAsync(string host, IReadOnlyList<string> requestedAttributes, CancellationToken ct)
        => SendAsync(host, IppOperation.GetPrinterAttributes, uri => new List<IppRequestAttribute>(IppCodec.StandardHeader(uri))
        {
            IppRequestAttribute.Strings(IppTag.Keyword, "requested-attributes", requestedAttributes.ToArray()),
        }, ct);

    public Task<IppResponse> GetJobsAsync(string host, string whichJobs, int limit, IReadOnlyList<string> requestedAttributes, CancellationToken ct)
        => SendAsync(host, IppOperation.GetJobs, uri => new List<IppRequestAttribute>(IppCodec.StandardHeader(uri))
        {
            IppRequestAttribute.Strings(IppTag.Keyword, "which-jobs", whichJobs),
            IppRequestAttribute.Integer("limit", limit),
            IppRequestAttribute.Strings(IppTag.Keyword, "requested-attributes", requestedAttributes.ToArray()),
        }, ct);

    private async Task<IppResponse> SendAsync(string host, ushort operation, Func<string, List<IppRequestAttribute>> attributes, CancellationToken ct)
    {
        var paths = _knownPaths.TryGetValue(host, out var known)
            ? new[] { known }.Concat(CandidatePaths.Where(p => p != known)).ToArray()
            : CandidatePaths;

        IppException? last = null;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            string printerUri = $"ipp://{host}:{_port}{path}";
            string httpUrl = $"http://{host}:{_port}{path}";
            try
            {
                var resp = await PostAsync(httpUrl, 2, 0, operation, attributes(printerUri), ct).ConfigureAwait(false);
                if (resp.Status == IppOperation.ServerErrorVersionNotSupported)
                    resp = await PostAsync(httpUrl, 1, 1, operation, attributes(printerUri), ct).ConfigureAwait(false);
                _knownPaths[host] = path;
                return resp;
            }
            catch (IppException ex) when (ex.Kind == IppFailureKind.Unreachable)
            {
                throw; // nothing on the port at all — no point trying other paths
            }
            catch (IppException ex)
            {
                last = ex; // 404 / non-IPP body on this path: try the next
            }
        }
        throw last ?? new IppException(IppFailureKind.HttpError, "No IPP endpoint answered.");
    }

    private async Task<IppResponse> PostAsync(string url, byte major, byte minor, ushort operation, List<IppRequestAttribute> attributes, CancellationToken ct)
    {
        var body = IppCodec.BuildRequest(major, minor, operation, Interlocked.Increment(ref _requestId), attributes);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_timeoutMs);
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
            using var response = await Http.PostAsync(url, content, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new IppException(IppFailureKind.HttpError, $"HTTP {(int)response.StatusCode} from {url}.");
            var bytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            return IppCodec.Parse(bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IppException(IppFailureKind.Unreachable, $"IPP request to {url} timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new IppException(IppFailureKind.Unreachable, $"IPP: {ex.Message}", ex);
        }
    }
}
