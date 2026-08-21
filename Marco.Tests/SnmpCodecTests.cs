using Marco.Core.Snmp;
using Marco.Inventory.Snmp;
using Xunit;

namespace Marco.Tests;

public class SnmpOidTests
{
    [Fact]
    public void Prefix_IsArcWise_NotTextual()
    {
        var a = SnmpOid.Parse("1.3.6.1.2.1.43.1");
        Assert.False(a.IsPrefixOf(SnmpOid.Parse("1.3.6.1.2.1.43.10.2")));   // ".43.1" is NOT a prefix of ".43.10"
        Assert.True(a.IsPrefixOf(SnmpOid.Parse("1.3.6.1.2.1.43.1.5")));
        Assert.True(a.IsPrefixOf(a));
    }

    [Fact]
    public void Compare_IsNumeric_NotLexical()
    {
        Assert.True(SnmpOid.Parse("1.9.1.2").CompareTo(SnmpOid.Parse("1.9.1.10")) < 0); // 2 < 10
        Assert.True(SnmpOid.Parse("1.9.1").CompareTo(SnmpOid.Parse("1.9.1.0")) < 0);   // shorter first
        Assert.Equal(SnmpOid.Parse(".1.3.6"), SnmpOid.Parse("1.3.6"));
    }

    [Fact]
    public void IndexAfter_ReturnsRowIndexArcs()
    {
        var entry = SnmpOid.Parse("1.3.6.1.2.1.43.11.1.1");
        var inst = SnmpOid.Parse("1.3.6.1.2.1.43.11.1.1.9.1.4");
        Assert.Equal(new uint[] { 9, 1, 4 }, inst.IndexAfter(entry));
        Assert.Empty(SnmpOid.Parse("1.3.6.1.2.1.1.1.0").IndexAfter(entry));
    }
}

public class BerTests
{
    [Theory]
    [InlineData(0L, "020100")]
    [InlineData(1L, "020101")]
    [InlineData(127L, "02017F")]
    [InlineData(128L, "02020080")]      // needs a leading 0x00 or it would read as -128
    [InlineData(-1L, "0201FF")]
    [InlineData(-2L, "0201FE")]
    [InlineData(-3L, "0201FD")]         // the Printer-MIB "some remaining" sentinel
    [InlineData(-129L, "0202FF7F")]
    [InlineData(256L, "02020100")]
    [InlineData(2147483647L, "02047FFFFFFF")]
    public void Integer_RoundTrips_InShortestForm(long value, string hex)
    {
        var bytes = Ber.EncodeInteger(value);
        Assert.Equal(hex, Convert.ToHexString(bytes));
        var tlv = Ber.ReadTlv(bytes, 0);
        Assert.Equal(value, Ber.DecodeInteger(tlv.Contents(bytes), signed: true));
    }

    [Fact]
    public void Unsigned_FiveByteCounter_DecodesAsPositive()
    {
        // Counter32 4294967295 as real agents send it: 0x41 05 00 FF FF FF FF
        var bytes = Convert.FromHexString("410500FFFFFFFF");
        var tlv = Ber.ReadTlv(bytes, 0);
        Assert.Equal(Ber.TagCounter32, tlv.Tag);
        Assert.Equal(4294967295L, Ber.DecodeInteger(tlv.Contents(bytes), signed: false));
        Assert.Equal(4294967295L, Ber.DecodeValue(bytes, tlv).Int);
    }

    [Theory]
    [InlineData("1.3.6.1.2.1.1.1.0", "06082B060102010101 00")]
    [InlineData("1.3.6.1.4.1.2435.2.3.9.4.2.1.5.5.1.0", "06112B0601040193 030203090402010505 0100")] // 2435 → 0x93 0x03
    [InlineData("1.3.6.1.2.1.43.11.1.1.9.1.1", "060C2B06010201 2B0B0101090101")]                     // 43 fits one byte
    public void Oid_MatchesGoldenBytes_AndRoundTrips(string oid, string hex)
    {
        var parsed = SnmpOid.Parse(oid);
        var bytes = Ber.EncodeOid(parsed);
        Assert.Equal(hex.Replace(" ", ""), Convert.ToHexString(bytes));
        var tlv = Ber.ReadTlv(bytes, 0);
        Assert.Equal(parsed, Ber.DecodeOid(tlv.Contents(bytes)));
    }

    [Fact]
    public void Oid_LargeArc_UsesMultiByteSubIds()
    {
        var oid = SnmpOid.Parse("1.3.6.1.4.1.311.1.1.3.1.1.4294967295"); // 2^32-1 needs five 7-bit groups
        var bytes = Ber.EncodeOid(oid);
        var tlv = Ber.ReadTlv(bytes, 0);
        Assert.Equal(oid, Ber.DecodeOid(tlv.Contents(bytes)));
    }

    [Fact]
    public void Oid_FirstByteAtLeast80_DecodesAsArc2()
    {
        // joint-iso-itu-t(2).999 → first sub-id = 2*40 + 999 = 1079 = 0x88 0x37
        var oid = new SnmpOid(2, 999, 1);
        var bytes = Ber.EncodeOid(oid);
        var tlv = Ber.ReadTlv(bytes, 0);
        Assert.Equal(oid, Ber.DecodeOid(tlv.Contents(bytes)));
    }

    [Fact]
    public void LongFormLength_RoundTrips()
    {
        var payload = new byte[300];
        var sink = new List<byte>();
        Ber.WriteTlv(sink, Ber.TagOctetString, payload);
        var bytes = sink.ToArray();
        Assert.Equal(0x82, bytes[1]); // two length bytes follow
        Assert.Equal(0x01, bytes[2]);
        Assert.Equal(0x2C, bytes[3]);
        var tlv = Ber.ReadTlv(bytes, 0);
        Assert.Equal(300, tlv.ContentLength);
        Assert.Equal(304, tlv.TotalLength);
    }

    [Fact]
    public void UnknownTag_IsPreservedRaw_NotThrown()
    {
        var bytes = new byte[] { 0x47, 0x02, 0xAB, 0xCD };
        var v = Ber.DecodeValue(bytes, Ber.ReadTlv(bytes, 0));
        Assert.Equal(SnmpValueKind.Unknown, v.Kind);
        Assert.Equal(new byte[] { 0xAB, 0xCD }, v.Bytes);
    }

    [Fact]
    public void ExceptionTags_Decode()
    {
        Assert.Equal(SnmpValueKind.NoSuchObject, Ber.DecodeValue(new byte[] { 0x80, 0 }, Ber.ReadTlv(new byte[] { 0x80, 0 }, 0)).Kind);
        Assert.Equal(SnmpValueKind.NoSuchInstance, Ber.DecodeValue(new byte[] { 0x81, 0 }, Ber.ReadTlv(new byte[] { 0x81, 0 }, 0)).Kind);
        Assert.Equal(SnmpValueKind.EndOfMibView, Ber.DecodeValue(new byte[] { 0x82, 0 }, Ber.ReadTlv(new byte[] { 0x82, 0 }, 0)).Kind);
    }

    [Fact]
    public void Truncated_Throws_ProtocolError()
    {
        var ex = Assert.Throws<SnmpException>(() => Ber.ReadTlv(new byte[] { 0x30, 0x10, 0x02 }, 0));
        Assert.Equal(SnmpFailureKind.ProtocolError, ex.Kind);
    }
}

public class SnmpValueTests
{
    [Fact]
    public void AsText_StrictUtf8_ThenLatin1_StripsNulsAndControls()
    {
        Assert.Equal("Ready", SnmpValue.OctetString(new byte[] { (byte)'R', (byte)'e', (byte)'a', (byte)'d', (byte)'y', 0, 0 }).AsText());
        Assert.Equal("Tonér", SnmpValue.OctetString(new byte[] { (byte)'T', (byte)'o', (byte)'n', 0xE9, (byte)'r' }).AsText()); // Latin-1 é
        Assert.Equal("Tonér", SnmpValue.OctetString("Tonér").AsText());                                                   // UTF-8 é
        Assert.Null(SnmpValue.OctetString(Array.Empty<byte>()).AsText());
        Assert.Equal("-3", SnmpValue.Integer(-3).AsText());
    }

    [Fact]
    public void AsMac_AcceptsRawAndAscii_RejectsZero()
    {
        Assert.Equal("3C:D9:2B:11:22:33", SnmpValue.OctetString(Convert.FromHexString("3CD92B112233")).AsMac());
        Assert.Equal("00:1B:A9:AA:BB:CC", SnmpValue.OctetString("00:1b:a9:aa:bb:cc").AsMac());
        Assert.Equal("00:1B:A9:AA:BB:CC", SnmpValue.OctetString("00-1B-A9-AA-BB-CC").AsMac());
        Assert.Null(SnmpValue.OctetString(new byte[6]).AsMac());
        Assert.Null(SnmpValue.OctetString(Array.Empty<byte>()).AsMac());
    }
}

public class SnmpMessageTests
{
    [Fact]
    public void Get_SysDescr_Public_V2c_MatchesGoldenBytes()
    {
        // The canonical "snmpget -v2c -c public host sysDescr.0" request, request-id 1.
        var bytes = SnmpMessage.BuildGet(SnmpVersion.V2c, "public", 1, new[] { SnmpOid.Parse("1.3.6.1.2.1.1.1.0") });
        const string golden =
            "30 26" +                         // SEQUENCE, 38 bytes
            "02 01 01" +                      //   version v2c (1)
            "04 06 70 75 62 6C 69 63" +       //   community "public"
            "A0 19" +                         //   GetRequest PDU, 25 bytes
            "02 01 01" +                      //     request-id 1
            "02 01 00" +                      //     error-status 0
            "02 01 00" +                      //     error-index 0
            "30 0E" +                         //     varbinds
            "30 0C" +                         //       varbind
            "06 08 2B 06 01 02 01 01 01 00" + //         sysDescr.0
            "05 00";                          //         NULL
        Assert.Equal(golden.Replace(" ", ""), Convert.ToHexString(bytes));
    }

    [Fact]
    public void GetBulk_PutsRepetitionsInThirdField()
    {
        var bytes = SnmpMessage.BuildGetBulk("public", 7, new[] { SnmpOid.Parse("1.3.6.1.2.1.2.2.1.2") }, 20);
        var msg = Ber.ReadTlv(bytes, 0);
        var top = Ber.ReadChildren(bytes, msg);
        Assert.Equal(Ber.PduGetBulk, top[2].Tag);
        var fields = Ber.ReadChildren(bytes, top[2]);
        Assert.Equal(7, Ber.DecodeInteger(fields[0].Contents(bytes), true));
        Assert.Equal(0, Ber.DecodeInteger(fields[1].Contents(bytes), true));  // non-repeaters
        Assert.Equal(20, Ber.DecodeInteger(fields[2].Contents(bytes), true)); // max-repetitions
    }

    [Fact]
    public void V1_Uses_Version0()
    {
        var bytes = SnmpMessage.BuildGetNext(SnmpVersion.V1, "c", 3, new[] { SnmpOid.Parse("1.3.6") });
        Assert.Equal(0x00, bytes[4]); // 30 len 02 01 <version>
        Assert.Equal(Ber.PduGetNext, bytes[5 + 3]);
    }

    [Fact]
    public void Parse_Response_WithExceptionsAndErrorIndex()
    {
        var resp = FakeSnmpAgent.BuildResponse(SnmpVersion.V2c, "public", 42, SnmpErrorStatus.NoSuchName, 2, new List<(SnmpOid, SnmpValue)>
        {
            (SnmpOid.Parse("1.3.6.1.2.1.1.1.0"), SnmpValue.OctetString("Printer")),
            (SnmpOid.Parse("1.3.6.1.2.1.1.9.0"), SnmpValue.NoSuchInstance),
            (SnmpOid.Parse("1.3.6.1.2.1.99.0"), SnmpValue.EndOfMibView),
            (SnmpOid.Parse("1.3.6.1.2.1.1.3.0"), SnmpValue.TimeTicks(123456)),
        });
        var parsed = SnmpMessage.Parse(resp);
        Assert.Equal(SnmpVersion.V2c, parsed.Version);
        Assert.Equal("public", parsed.Community);
        Assert.Equal(42, parsed.RequestId);
        Assert.Equal(SnmpErrorStatus.NoSuchName, parsed.ErrorStatus);
        Assert.Equal(2, parsed.ErrorIndex);
        Assert.Equal(4, parsed.VarBinds.Count);
        Assert.Equal("Printer", parsed.VarBinds[0].Value.AsText());
        Assert.Equal(SnmpValueKind.NoSuchInstance, parsed.VarBinds[1].Value.Kind);
        Assert.Equal(SnmpValueKind.EndOfMibView, parsed.VarBinds[2].Value.Kind);
        Assert.Equal(123456, parsed.VarBinds[3].Value.Int);
        Assert.Equal(42, SnmpMessage.PeekRequestId(resp));
    }

    [Fact]
    public void Parse_RejectsNonResponsePdu()
    {
        var request = SnmpMessage.BuildGet(SnmpVersion.V2c, "public", 1, new[] { SnmpOid.Parse("1.3.6") });
        var ex = Assert.Throws<SnmpException>(() => SnmpMessage.Parse(request));
        Assert.Equal(SnmpFailureKind.ProtocolError, ex.Kind);
    }
}
