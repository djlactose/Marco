using Marco.Core.Model;
using Marco.Core.Scanning;
using Marco.Discovery;
using Xunit;

namespace Marco.Tests;

public class DeviceClassifierTests
{
    private readonly DeviceClassifier _c = new();

    private DeviceType Classify(int[] ports, OuiCategory oui = OuiCategory.None, bool nbns = false, int? ttl = null)
        => _c.Classify(ports, oui, nbns, ttl).DeviceType;

    [Fact]
    public void PrinterPort_ClassifiesPrinter()
        => Assert.Equal(DeviceType.Printer, Classify(new[] { 9100 }));

    [Fact]
    public void PrinterOui_ClassifiesPrinter_EvenWithNoPorts()
        => Assert.Equal(DeviceType.Printer, Classify(Array.Empty<int>(), OuiCategory.Printer));

    [Fact]
    public void PrinterBeatsWindows_WhenBothSignalsPresent()
        => Assert.Equal(DeviceType.Printer, Classify(new[] { 445, 135, 9100 }));

    [Fact]
    public void NetworkOui_ClassifiesNetworkDevice()
        => Assert.Equal(DeviceType.NetworkDevice, Classify(Array.Empty<int>(), OuiCategory.Network));

    [Fact]
    public void TelnetWithoutSmb_ClassifiesNetworkDevice()
        => Assert.Equal(DeviceType.NetworkDevice, Classify(new[] { 23 }));

    [Fact]
    public void SnmpWithoutSmb_ClassifiesNetworkDevice()
        => Assert.Equal(DeviceType.NetworkDevice, Classify(new[] { 161 }));

    [Fact]
    public void SmbPlusRpc_ClassifiesWindows()
        => Assert.Equal(DeviceType.Windows, Classify(new[] { 445, 135 }));

    [Fact]
    public void NbnsResponse_ClassifiesWindows()
        => Assert.Equal(DeviceType.Windows, Classify(Array.Empty<int>(), nbns: true));

    [Fact]
    public void Rdp_ClassifiesWindows()
        => Assert.Equal(DeviceType.Windows, Classify(new[] { 3389 }));

    [Fact]
    public void SshWithoutSmb_ClassifiesUnix()
        => Assert.Equal(DeviceType.UnixLinux, Classify(new[] { 22 }));

    [Fact]
    public void SmbPresent_TelnetDoesNotWinNetwork()
        => Assert.Equal(DeviceType.Windows, Classify(new[] { 445, 135, 23 }));

    [Theory]
    [InlineData(64, DeviceType.UnixLinux)]
    [InlineData(58, DeviceType.UnixLinux)]
    [InlineData(128, DeviceType.Windows)]
    [InlineData(120, DeviceType.Windows)]
    [InlineData(255, DeviceType.Unknown)]   // high TTL is ambiguous (embedded IoT), not a network-gear verdict
    [InlineData(250, DeviceType.Unknown)]
    public void TtlHint_BreaksTies_WhenNoOtherSignal(int ttl, DeviceType expected)
        => Assert.Equal(expected, Classify(Array.Empty<int>(), ttl: ttl));

    [Fact]
    public void TtlHint_NeverOverridesPortSignal()
    {
        // TTL says Unix, but SSH is absent and SMB+RPC present => Windows wins.
        Assert.Equal(DeviceType.Windows, Classify(new[] { 445, 135 }, ttl: 64));
    }

    [Fact]
    public void NothingConclusive_IsUnknown()
        => Assert.Equal(DeviceType.Unknown, Classify(new[] { 80 }));

    [Fact]
    public void HypervisorOui_SetsIsVirtual_WithoutForcingType()
    {
        var result = _c.Classify(new[] { 445, 135 }, OuiCategory.Hypervisor, nbnsResponded: false, icmpTtl: null);
        Assert.True(result.IsVirtual);
        Assert.Equal(DeviceType.Windows, result.DeviceType);
    }

    [Fact]
    public void NonHypervisor_IsNotVirtual()
    {
        var result = _c.Classify(new[] { 445, 135 }, OuiCategory.Pc, nbnsResponded: false, icmpTtl: null);
        Assert.False(result.IsVirtual);
    }
}
