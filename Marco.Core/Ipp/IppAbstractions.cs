namespace Marco.Core.Ipp;

public enum IppFailureKind
{
    /// <summary>Nothing listening on the port, connection refused, or the request timed out.</summary>
    Unreachable,
    /// <summary>The HTTP layer answered with a non-success status on every known IPP path.</summary>
    HttpError,
    /// <summary>The body was not a decodable IPP message.</summary>
    BadResponse,
}

public sealed class IppException : Exception
{
    public IppFailureKind Kind { get; }
    public IppException(IppFailureKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

/// <summary>IPP value tags (RFC 8010 §3.5.2) — the ones a decoder must recognise by shape.</summary>
public static class IppTag
{
    public const byte OperationAttributes = 0x01, JobAttributes = 0x02, EndOfAttributes = 0x03, PrinterAttributes = 0x04, UnsupportedAttributes = 0x05;
    public const byte Unsupported = 0x10, Unknown = 0x12, NoValue = 0x13;
    public const byte Integer = 0x21, Boolean = 0x22, Enum = 0x23;
    public const byte OctetString = 0x30, DateTime = 0x31, Resolution = 0x32, RangeOfInteger = 0x33;
    public const byte BegCollection = 0x34, TextWithLanguage = 0x35, NameWithLanguage = 0x36, EndCollection = 0x37;
    public const byte TextWithoutLanguage = 0x41, NameWithoutLanguage = 0x42, Keyword = 0x44, Uri = 0x45, UriScheme = 0x46;
    public const byte Charset = 0x47, NaturalLanguage = 0x48, MimeMediaType = 0x49, MemberAttrName = 0x4A;
}

/// <summary>IPP operation ids and status codes used here.</summary>
public static class IppOperation
{
    public const ushort GetJobs = 0x000A, GetPrinterAttributes = 0x000B;
    public const ushort StatusOk = 0x0000, StatusOkIgnoredOrSubstituted = 0x0001, StatusOkConflicting = 0x0002;
    public const ushort ServerErrorVersionNotSupported = 0x0503;
    public static bool IsSuccess(ushort status) => status <= 0x00FF;
}

/// <summary>One decoded attribute value. Integers/enums/booleans land in <see cref="Int"/>, strings and the
/// textual renderings of dates/ranges/resolutions in <see cref="Text"/>; everything keeps its raw bytes.</summary>
public sealed class IppValue
{
    public byte Tag { get; }
    public long? Int { get; }
    public string? Text { get; }
    public byte[] Raw { get; }

    public IppValue(byte tag, byte[] raw, long? i = null, string? text = null)
    { Tag = tag; Raw = raw; Int = i; Text = text; }

    public override string ToString() => Text ?? Int?.ToString() ?? $"<{Raw.Length} bytes>";
}

public sealed record IppAttribute(string Name, byte Tag, IReadOnlyList<IppValue> Values)
{
    public string? FirstText => Values.Count > 0 ? Values[0].Text : null;
    public long? FirstInt => Values.Count > 0 ? Values[0].Int : null;
    public IEnumerable<string> Texts => Values.Select(v => v.Text).Where(t => t is not null)!;
    public IEnumerable<long> Ints => Values.Where(v => v.Int is not null).Select(v => v.Int!.Value);
}

public sealed record IppAttributeGroup(byte Tag, IReadOnlyList<IppAttribute> Attributes)
{
    public IppAttribute? Find(string name) => Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    public string? Text(string name) => Find(name)?.FirstText;
    public long? Int(string name) => Find(name)?.FirstInt;
    public IReadOnlyList<string> Texts(string name) => Find(name)?.Texts.ToList() ?? new List<string>();
    public IReadOnlyList<long> Ints(string name) => Find(name)?.Ints.ToList() ?? new List<long>();
}

public sealed record IppResponse(byte VersionMajor, byte VersionMinor, ushort Status, int RequestId, IReadOnlyList<IppAttributeGroup> Groups)
{
    public IppAttributeGroup? PrinterAttributes => Groups.FirstOrDefault(g => g.Tag == IppTag.PrinterAttributes);
    public IEnumerable<IppAttributeGroup> JobGroups => Groups.Where(g => g.Tag == IppTag.JobAttributes);
    public string? StatusMessage => Groups.FirstOrDefault(g => g.Tag == IppTag.OperationAttributes)?.Text("status-message");
}

/// <summary>Talks IPP to a printer — credential-free, read-only operations only. Abstracted so the runner is
/// unit-testable with canned responses.</summary>
public interface IIppClient
{
    Task<IppResponse> GetPrinterAttributesAsync(string host, IReadOnlyList<string> requestedAttributes, CancellationToken ct);
    Task<IppResponse> GetJobsAsync(string host, string whichJobs, int limit, IReadOnlyList<string> requestedAttributes, CancellationToken ct);
}
