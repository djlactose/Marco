using System.Globalization;
using System.Net;

namespace Marco.Core.Targets;

/// <summary>
/// Parses operator-supplied target tokens (CIDR, dashed ranges, single IPs, hostnames, host-file lines)
/// into a de-duplicated, lazily-enumerated sequence of <see cref="ScanTarget"/>. Range expansion is IPv4
/// only; a bare hostname is passed through for later DNS resolution (it may resolve to v6). The total
/// expansion is sized up front and rejected past the guard threshold before any address is yielded, so a
/// /16 confirmation never materializes 65k objects first.
/// </summary>
public static class TargetParser
{
    /// <summary>The block name given to targets that came from a single IP or hostname token, so a list of
    /// individual hosts groups together instead of producing one group per host.</summary>
    public const string IndividualHostsBlock = "Individual hosts";

    /// <summary>Split raw input (which may be multi-line, from a text box or host file) into tokens,
    /// dropping blank lines and <c>#</c> comments.</summary>
    public static IEnumerable<string> Tokenize(IEnumerable<string> inputs)
    {
        foreach (var raw in inputs)
        {
            if (raw is null) continue;
            foreach (var line in raw.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith('#')) continue;

                // Allow a trailing inline comment: "10.0.0.1  # gateway"
                var hash = trimmed.IndexOf('#');
                if (hash > 0) trimmed = trimmed[..hash].Trim();

                // A single line may carry several comma/space/tab/semicolon-separated tokens.
                foreach (var tok in trimmed.Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    yield return tok;
            }
        }
    }

    /// <summary>The exact count of addresses that <see cref="Parse"/> would yield ignoring de-duplication
    /// — a safe upper bound used to drive the confirmation dialog.</summary>
    public static long EstimateCount(IEnumerable<string> inputs, TargetExpansionOptions? options = null)
    {
        options ??= TargetExpansionOptions.Default;
        long total = 0;
        foreach (var token in Tokenize(inputs))
            total += SizeOf(token, options);
        return total;
    }

    /// <summary>Parse and expand all tokens into a de-duplicated lazy sequence. Throws
    /// <see cref="TargetTooLargeException"/> eagerly (before yielding) when the guard trips, and
    /// <see cref="TargetParseException"/> for a malformed token.</summary>
    public static IEnumerable<ScanTarget> Parse(IEnumerable<string> inputs, TargetExpansionOptions? options = null)
    {
        options ??= TargetExpansionOptions.Default;
        var tokens = Tokenize(inputs).ToList();

        long total = 0;
        foreach (var token in tokens)
            total += SizeOf(token, options); // also validates each token eagerly

        if (!options.AllowLargeExpansion && total > options.LargeExpansionThreshold)
            throw new TargetTooLargeException(total, options.LargeExpansionThreshold);

        return ExpandDeduped(tokens, options);
    }

    /// <summary>The block (see <see cref="ScanTarget.Block"/>) that <paramref name="address"/> belongs to among
    /// <paramref name="tokens"/>: the first CIDR/range containing it, <see cref="IndividualHostsBlock"/> when it
    /// matches a single IP or hostname token, or null when no token covers it. Used to re-group a scan reloaded
    /// from a file that predates per-row block information. Malformed tokens are skipped.</summary>
    public static string? FindBlock(IEnumerable<string> tokens, string address)
    {
        bool isIp = TryParseIpv4(address, out uint addr);
        foreach (var token in tokens)
        {
            try
            {
                switch (Classify(token))
                {
                    case Kind.Cidr:
                        if (!isIp) break;
                        var (network, prefix) = ParseCidr(token);
                        uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
                        if ((addr & mask) == network) return token;
                        break;
                    case Kind.Range:
                        if (!isIp) break;
                        var (start, end) = ParseRange(token);
                        if (addr >= start && addr <= end) return token;
                        break;
                    default:
                        if (string.Equals(token, address, StringComparison.OrdinalIgnoreCase)) return IndividualHostsBlock;
                        if (IPAddress.TryParse(token, out var ip) && ip.ToString() == address) return IndividualHostsBlock;
                        break;
                }
            }
            catch (TargetParseException) { /* skip malformed token */ }
        }
        return null;
    }

    private static IEnumerable<ScanTarget> ExpandDeduped(List<string> tokens, TargetExpansionOptions options)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            foreach (var target in ExpandToken(token, options))
            {
                if (seen.Add(target.Address))
                    yield return target;
            }
        }
    }

    // --- token classification -------------------------------------------------

    private enum Kind { Cidr, Range, Single }

    private static Kind Classify(string token)
    {
        // '/' only ever appears in CIDR notation — never in a hostname or IP literal — so route it to the
        // CIDR parser unconditionally; an invalid address part then throws rather than silently becoming a host.
        if (token.IndexOf('/') > 0)
            return Kind.Cidr;

        int dash = token.IndexOf('-');
        if (dash > 0 && IsIpv4(token[..dash]))
            return Kind.Range;

        return Kind.Single; // single IP literal, or a hostname (which may itself contain '-')
    }

    private static long SizeOf(string token, TargetExpansionOptions options) => Classify(token) switch
    {
        Kind.Cidr => CidrCount(token, options),
        Kind.Range => RangeCount(token),
        _ => 1,
    };

    private static IEnumerable<ScanTarget> ExpandToken(string token, TargetExpansionOptions options) => Classify(token) switch
    {
        Kind.Cidr => ExpandCidr(token, options),
        Kind.Range => ExpandRange(token),
        _ => ExpandSingle(token),
    };

    // --- CIDR -----------------------------------------------------------------

    private static (uint baseAddr, int prefix) ParseCidr(string token)
    {
        int slash = token.IndexOf('/');
        var ipPart = token[..slash];
        var prefixPart = token[(slash + 1)..];

        if (!TryParseIpv4(ipPart, out uint ip))
            throw new TargetParseException(token, $"'{ipPart}' is not a valid IPv4 address.");
        if (!int.TryParse(prefixPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int prefix) || prefix is < 0 or > 32)
            throw new TargetParseException(token, $"'/{prefixPart}' is not a valid IPv4 prefix (expected 0-32).");

        uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        uint network = ip & mask;
        return (network, prefix);
    }

    private static (uint first, uint last) CidrUsableRange(string token, TargetExpansionOptions options)
    {
        var (network, prefix) = ParseCidr(token);
        uint size = prefix == 0 ? uint.MaxValue : (1u << (32 - prefix));
        uint broadcast = prefix == 32 ? network : network + size - 1;

        // /31 (RFC 3021) and /32 have no separate network/broadcast to strip.
        bool strip = prefix <= 30 && !options.IncludeNetworkAndBroadcast;
        uint first = strip ? network + 1 : network;
        uint last = strip ? broadcast - 1 : broadcast;
        return (first, last);
    }

    private static long CidrCount(string token, TargetExpansionOptions options)
    {
        var (first, last) = CidrUsableRange(token, options);
        return (long)last - first + 1;
    }

    private static IEnumerable<ScanTarget> ExpandCidr(string token, TargetExpansionOptions options)
    {
        var (first, last) = CidrUsableRange(token, options);
        for (uint a = first; ; a++)
        {
            yield return new ScanTarget(Ipv4ToString(a), false, token);
            if (a == last) break; // guard against uint overflow at 255.255.255.255
        }
    }

    // --- dashed range ---------------------------------------------------------

    private static (uint start, uint end) ParseRange(string token)
    {
        int dash = token.IndexOf('-');
        var startPart = token[..dash];
        var endPart = token[(dash + 1)..];

        if (!TryParseIpv4(startPart, out uint start))
            throw new TargetParseException(token, $"'{startPart}' is not a valid IPv4 address.");

        uint end;
        if (endPart.Contains('.'))
        {
            if (!TryParseIpv4(endPart, out end))
                throw new TargetParseException(token, $"'{endPart}' is not a valid IPv4 address.");
        }
        else
        {
            // Shorthand: last octet only, e.g. 10.20.30.1-254
            if (!byte.TryParse(endPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte lastOctet))
                throw new TargetParseException(token, $"'{endPart}' is not a valid range end (expected an IP or 0-255).");
            end = (start & 0xFFFFFF00u) | lastOctet;
        }

        if (end < start)
            throw new TargetParseException(token, $"Range end {Ipv4ToString(end)} precedes start {Ipv4ToString(start)}.");
        return (start, end);
    }

    private static long RangeCount(string token)
    {
        var (start, end) = ParseRange(token);
        return (long)end - start + 1;
    }

    private static IEnumerable<ScanTarget> ExpandRange(string token)
    {
        var (start, end) = ParseRange(token);
        for (uint a = start; ; a++)
        {
            yield return new ScanTarget(Ipv4ToString(a), false, token);
            if (a == end) break;
        }
    }

    // --- single IP / hostname -------------------------------------------------

    private static IEnumerable<ScanTarget> ExpandSingle(string token)
    {
        if (IPAddress.TryParse(token, out var ip))
            yield return new ScanTarget(ip.ToString(), false, IndividualHostsBlock);
        else
            yield return new ScanTarget(token, true, IndividualHostsBlock);
    }

    // --- IPv4 helpers ---------------------------------------------------------

    private static bool IsIpv4(string s) => TryParseIpv4(s, out _);

    /// <summary>Strict dotted-quad parse to a big-endian uint. Rejects IPv6 and the shorthand forms
    /// <see cref="IPAddress.TryParse"/> accepts (e.g. "10" or "10.1"), which would corrupt range math.</summary>
    private static bool TryParseIpv4(string s, out uint value)
    {
        value = 0;
        var parts = s.Split('.');
        if (parts.Length != 4) return false;
        uint acc = 0;
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 3) return false;
            if (!byte.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b)) return false;
            acc = (acc << 8) | b;
        }
        value = acc;
        return true;
    }

    private static string Ipv4ToString(uint value)
        => $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
}
