using System.Text;
using Marco.Core.Snmp;

namespace Marco.Inventory.Snmp;

/// <summary>PDU error-status values (RFC 1157 / RFC 3416).</summary>
public static class SnmpErrorStatus
{
    public const int NoError = 0, TooBig = 1, NoSuchName = 2, BadValue = 3, ReadOnly = 4, GenErr = 5;
}

/// <summary>A parsed SNMP v1/v2c message. Only the response direction is ever parsed.</summary>
public sealed record SnmpResponse(
    SnmpVersion Version, string Community, int RequestId, int ErrorStatus, int ErrorIndex,
    IReadOnlyList<SnmpVarBind> VarBinds);

/// <summary>
/// Builds request messages and parses responses. The message is
/// <c>SEQUENCE { version INTEGER, community OCTET STRING, PDU }</c> where the PDU is a context-tagged
/// <c>SEQUENCE { request-id, error-status, error-index, varbinds SEQUENCE OF SEQUENCE { OID, value } }</c>;
/// GETBULK reuses the error fields as non-repeaters / max-repetitions. Pure, golden-byte tested.
/// </summary>
public static class SnmpMessage
{
    public static byte[] BuildGet(SnmpVersion v, string community, int requestId, IReadOnlyList<SnmpOid> oids)
        => Build(v, community, Ber.PduGet, requestId, 0, 0, oids);

    public static byte[] BuildGetNext(SnmpVersion v, string community, int requestId, IReadOnlyList<SnmpOid> oids)
        => Build(v, community, Ber.PduGetNext, requestId, 0, 0, oids);

    /// <summary>v2c only: non-repeaters is always 0 here (every OID in the request is walked).</summary>
    public static byte[] BuildGetBulk(string community, int requestId, IReadOnlyList<SnmpOid> oids, int maxRepetitions)
        => Build(SnmpVersion.V2c, community, Ber.PduGetBulk, requestId, 0, maxRepetitions, oids);

    private static byte[] Build(SnmpVersion v, string community, byte pduTag, int requestId, int f1, int f2, IReadOnlyList<SnmpOid> oids)
    {
        var binds = new byte[oids.Count][];
        for (int i = 0; i < oids.Count; i++)
            binds[i] = Ber.EncodeConstructed(Ber.TagSequence, Ber.EncodeOid(oids[i]), Ber.EncodeNull());
        var pdu = Ber.EncodeConstructed(pduTag,
            Ber.EncodeInteger(requestId), Ber.EncodeInteger(f1), Ber.EncodeInteger(f2),
            Ber.EncodeConstructed(Ber.TagSequence, binds));
        return Ber.EncodeConstructed(Ber.TagSequence,
            Ber.EncodeInteger((int)v), Ber.EncodeOctetString(Encoding.UTF8.GetBytes(community)), pdu);
    }

    /// <summary>Parse any SNMP message carrying a response PDU. Throws <see cref="SnmpException"/>
    /// (ProtocolError) on malformed input or a non-response PDU.</summary>
    public static SnmpResponse Parse(byte[] datagram)
    {
        var msg = Ber.ReadTlv(datagram, 0);
        if (msg.Tag != Ber.TagSequence) throw Bad("message is not a SEQUENCE");
        var top = Ber.ReadChildren(datagram, msg);
        if (top.Count < 3) throw Bad("message has fewer than three fields");
        if (top[0].Tag != Ber.TagInteger) throw Bad("version is not an INTEGER");
        var version = (SnmpVersion)Ber.DecodeInteger(top[0].Contents(datagram), signed: true);
        var community = Encoding.UTF8.GetString(top[1].Contents(datagram));
        var pdu = top[2];
        if (pdu.Tag != Ber.PduResponse) throw Bad($"unexpected PDU type 0x{pdu.Tag:X2}");
        var fields = Ber.ReadChildren(datagram, pdu);
        if (fields.Count < 4) throw Bad("PDU has fewer than four fields");
        int requestId = (int)Ber.DecodeInteger(fields[0].Contents(datagram), signed: true);
        int errorStatus = (int)Ber.DecodeInteger(fields[1].Contents(datagram), signed: true);
        int errorIndex = (int)Ber.DecodeInteger(fields[2].Contents(datagram), signed: true);
        var binds = new List<SnmpVarBind>();
        foreach (var vb in Ber.ReadChildren(datagram, fields[3]))
        {
            var parts = Ber.ReadChildren(datagram, vb);
            if (parts.Count < 2 || parts[0].Tag != Ber.TagOid) throw Bad("varbind is not (OID, value)");
            binds.Add(new SnmpVarBind(Ber.DecodeOid(parts[0].Contents(datagram)), Ber.DecodeValue(datagram, parts[1])));
        }
        return new SnmpResponse(version, community, requestId, errorStatus, errorIndex, binds);
    }

    /// <summary>Cheap peek at the request-id without fully decoding — used to match late replies.</summary>
    public static int? PeekRequestId(byte[] datagram)
    {
        try
        {
            var msg = Ber.ReadTlv(datagram, 0);
            var top = Ber.ReadChildren(datagram, msg);
            if (top.Count < 3) return null;
            var fields = Ber.ReadChildren(datagram, top[2]);
            return fields.Count == 0 ? null : (int)Ber.DecodeInteger(fields[0].Contents(datagram), signed: true);
        }
        catch (SnmpException) { return null; }
    }

    private static SnmpException Bad(string what) => new(SnmpFailureKind.ProtocolError, $"Malformed SNMP response: {what}.");
}
