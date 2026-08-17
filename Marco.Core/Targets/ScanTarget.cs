namespace Marco.Core.Targets;

/// <summary>A single address to probe. <see cref="IsHostname"/> is true when the token could not be
/// parsed as an IP literal and must be resolved before scanning. <see cref="Block"/> is the operator's token this
/// address expanded from (a CIDR or range), or <see cref="TargetParser.IndividualHostsBlock"/> for single IPs and
/// hostnames — the results grid groups rows by it.</summary>
public sealed record ScanTarget(string Address, bool IsHostname, string? Block = null)
{
    public override string ToString() => Address;
}

/// <summary>Options governing how ranges expand into individual targets.</summary>
public sealed class TargetExpansionOptions
{
    /// <summary>Include the network and broadcast addresses for prefixes /30 and wider. Default false.</summary>
    public bool IncludeNetworkAndBroadcast { get; init; }

    /// <summary>Bypass the large-expansion guard. Set only after the operator confirms.</summary>
    public bool AllowLargeExpansion { get; init; }

    /// <summary>Total addresses above which expansion is rejected without <see cref="AllowLargeExpansion"/>. A /16.</summary>
    public int LargeExpansionThreshold { get; init; } = 65536;

    public static readonly TargetExpansionOptions Default = new();
}

/// <summary>A malformed target token.</summary>
public sealed class TargetParseException : Exception
{
    public string Token { get; }
    public TargetParseException(string token, string message) : base(message) => Token = token;
}

/// <summary>The requested expansion exceeds the guard threshold; the UI must confirm before retrying with
/// <see cref="TargetExpansionOptions.AllowLargeExpansion"/>.</summary>
public sealed class TargetTooLargeException : Exception
{
    public long RequestedCount { get; }
    public int Threshold { get; }

    public TargetTooLargeException(long requested, int threshold)
        : base($"Target expansion of {requested:N0} addresses exceeds the {threshold:N0} guard. Confirm to proceed.")
    {
        RequestedCount = requested;
        Threshold = threshold;
    }
}
