using System.Text;
using Marco.Core.Ipp;
using Marco.Inventory.Ipp;
using Xunit;

namespace Marco.Tests;

/// <summary>Builds IPP response bytes for tests — and doubles as the canned-response source for the fake client.</summary>
internal static class IppTestBytes
{
    public static byte[] Response(ushort status, int requestId, params (byte GroupTag, (byte Tag, string Name, object Value)[] Attrs)[] groups)
    {
        var sink = new List<byte> { 2, 0, (byte)(status >> 8), (byte)status };
        sink.AddRange(new[] { (byte)(requestId >> 24), (byte)(requestId >> 16), (byte)(requestId >> 8), (byte)requestId });
        foreach (var (groupTag, attrs) in groups)
        {
            sink.Add(groupTag);
            foreach (var (tag, name, value) in attrs)
            {
                var values = value is IEnumerable<object> many ? many.ToList() : new List<object> { value };
                for (int i = 0; i < values.Count; i++)
                {
                    sink.Add(tag);
                    var n = i == 0 ? Encoding.UTF8.GetBytes(name) : Array.Empty<byte>();
                    sink.Add((byte)(n.Length >> 8)); sink.Add((byte)n.Length); sink.AddRange(n);
                    byte[] v = values[i] switch
                    {
                        int num => new[] { (byte)(num >> 24), (byte)(num >> 16), (byte)(num >> 8), (byte)num },
                        bool b => new[] { (byte)(b ? 1 : 0) },
                        string s => Encoding.UTF8.GetBytes(s),
                        byte[] raw => raw,
                        _ => Array.Empty<byte>(),
                    };
                    sink.Add((byte)(v.Length >> 8)); sink.Add((byte)v.Length); sink.AddRange(v);
                }
            }
        }
        sink.Add(IppTag.EndOfAttributes);
        return sink.ToArray();
    }

    public static (byte, (byte, string, object)[]) Operation() => (IppTag.OperationAttributes, new (byte, string, object)[]
    {
        (IppTag.Charset, "attributes-charset", "utf-8"),
        (IppTag.NaturalLanguage, "attributes-natural-language", "en"),
    });
}

public class IppCodecTests
{
    [Fact]
    public void GetPrinterAttributes_Request_MatchesGoldenLayout()
    {
        var attrs = IppCodec.StandardHeader("ipp://10.0.0.9:631/ipp/print");
        attrs.Add(IppRequestAttribute.Strings(IppTag.Keyword, "requested-attributes", "printer-state", "marker-levels"));
        var bytes = IppCodec.BuildRequest(2, 0, IppOperation.GetPrinterAttributes, 1, attrs);

        var expected = new List<byte> { 0x02, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x01, 0x01 };
        void Attr(byte tag, string name, string value)
        {
            expected.Add(tag);
            var n = Encoding.UTF8.GetBytes(name); expected.Add(0); expected.Add((byte)n.Length); expected.AddRange(n);
            var v = Encoding.UTF8.GetBytes(value); expected.Add(0); expected.Add((byte)v.Length); expected.AddRange(v);
        }
        Attr(0x47, "attributes-charset", "utf-8");
        Attr(0x48, "attributes-natural-language", "en");
        Attr(0x45, "printer-uri", "ipp://10.0.0.9:631/ipp/print");
        Attr(0x44, "requested-attributes", "printer-state");
        Attr(0x44, "", "marker-levels"); // additional value: empty name
        expected.Add(0x03);
        Assert.Equal(expected.ToArray(), bytes);
    }

    [Fact]
    public void Parse_PrinterAttributes_MultiValues_AndSkipsCollections()
    {
        var bytes = IppTestBytes.Response(IppOperation.StatusOk, 77,
            IppTestBytes.Operation(),
            (IppTag.PrinterAttributes, new (byte, string, object)[]
            {
                (IppTag.Enum, "printer-state", 4),
                (IppTag.Keyword, "printer-state-reasons", new object[] { "marker-supply-low-warning", "media-empty-report" }),
                (IppTag.Integer, "queued-job-count", 3),
                (IppTag.TextWithoutLanguage, "printer-make-and-model", "HP Color LaserJet MFP M479fdw"),
                (IppTag.BegCollection, "media-col-default", Array.Empty<byte>()),
                (IppTag.MemberAttrName, "", "media-size"),
                (IppTag.BegCollection, "", Array.Empty<byte>()),
                (IppTag.MemberAttrName, "", "x-dimension"),
                (IppTag.Integer, "", 21590),
                (IppTag.EndCollection, "", Array.Empty<byte>()),
                (IppTag.EndCollection, "", Array.Empty<byte>()),
                (IppTag.Integer, "marker-levels", new object[] { 8, 80, -3 }),
                (IppTag.NameWithoutLanguage, "marker-names", new object[] { "Black", "Cyan", "Magenta" }),
                (IppTag.Boolean, "color-supported", true),
                (IppTag.DateTime, "printer-current-time", new byte[] { 0x07, 0xEA, 8, 21, 14, 30, 0, 0, (byte)'-', 5, 0 }),
            }));

        var r = IppCodec.Parse(bytes);
        Assert.Equal(IppOperation.StatusOk, r.Status);
        Assert.Equal(77, r.RequestId);
        var p = r.PrinterAttributes!;
        Assert.Equal(4, p.Int("printer-state"));
        Assert.Equal(new[] { "marker-supply-low-warning", "media-empty-report" }, p.Texts("printer-state-reasons"));
        Assert.Equal(3, p.Int("queued-job-count"));
        Assert.Equal("HP Color LaserJet MFP M479fdw", p.Text("printer-make-and-model"));
        Assert.Null(p.Find("media-size"));      // collection members were skipped, not promoted
        Assert.Null(p.Find("x-dimension"));
        Assert.Equal(new long[] { 8, 80, -3 }, p.Ints("marker-levels"));
        Assert.Equal(3, p.Texts("marker-names").Count);
        Assert.Equal("true", p.Text("color-supported"));
        Assert.Equal("2026-08-21T14:30:00-05:00", p.Text("printer-current-time"));
    }

    [Fact]
    public void Parse_JobGroups_OnePerJob()
    {
        var bytes = IppTestBytes.Response(IppOperation.StatusOk, 1,
            IppTestBytes.Operation(),
            (IppTag.JobAttributes, new (byte, string, object)[] { (IppTag.Integer, "job-id", 41), (IppTag.Enum, "job-state", 5), (IppTag.NameWithoutLanguage, "job-name", "Q3 report.pdf") }),
            (IppTag.JobAttributes, new (byte, string, object)[] { (IppTag.Integer, "job-id", 42), (IppTag.Enum, "job-state", 3) }));
        var r = IppCodec.Parse(bytes);
        var jobs = r.JobGroups.ToList();
        Assert.Equal(2, jobs.Count);
        Assert.Equal(41, jobs[0].Int("job-id"));
        Assert.Equal("Q3 report.pdf", jobs[0].Text("job-name"));
        Assert.Equal(3, jobs[1].Int("job-state"));
    }

    [Fact]
    public void Parse_TextWithLanguage()
    {
        var raw = new List<byte> { 0, 2, (byte)'e', (byte)'n', 0, 5 };
        raw.AddRange(Encoding.UTF8.GetBytes("Ready"));
        var v = IppCodec.DecodeValue(IppTag.TextWithLanguage, raw.ToArray());
        Assert.Equal("Ready", v.Text);
    }

    [Fact]
    public void Parse_Truncated_Throws()
    {
        var ex = Assert.Throws<IppException>(() => IppCodec.Parse(new byte[] { 2, 0, 0, 0, 0, 0, 0, 1, 0x04, 0x21, 0x00, 0x05, (byte)'a' }));
        Assert.Equal(IppFailureKind.BadResponse, ex.Kind);
    }
}
