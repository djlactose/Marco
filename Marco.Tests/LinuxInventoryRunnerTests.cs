using System.Security;
using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;
using Marco.Inventory.Linux;
using Marco.Core.Ssh;
using Xunit;

namespace Marco.Tests;

public class LinuxInventoryRunnerTests
{
    private static CredentialCandidate Cred(string user, string pw = "pw")
    {
        var s = new SecureString();
        foreach (var c in pw) s.AppendChar(c);
        return new CredentialCandidate(user, new WmiCredential { Username = user, Password = s });
    }

    private static FakeSshSession FullyPopulatedSession()
        => new FakeSshSession("10.0.0.50")
            .On("/etc/os-release", "PRETTY_NAME=\"Ubuntu 22.04.3 LTS\"\nVERSION_ID=\"22.04\"\nID=ubuntu\n")
            .On("uname -r", "5.15.0-91-generic\n")
            .On("uname -m", "x86_64\n")
            .On("uptime -s", "2024-01-10 08:30:00\n")
            .On("hostname", "web01.corp.local\n")
            .On("dmi/id", "sys_vendor=Dell Inc.\nproduct_name=PowerEdge R740\nproduct_serial=ABC123\nboard_vendor=Dell\n")
            .On("lscpu", "CPU(s):  4\nCore(s) per socket:  4\nSocket(s):  1\nModel name:  Xeon\nCPU max MHz:  3200\n")
            .On("/proc/meminfo", "MemTotal:       8000000 kB\n")
            .On("lsblk", "NAME=\"sda\" SIZE=\"1000000000000\" TYPE=\"disk\" MODEL=\"PERC H730\" SERIAL=\"XYZ\"\n")
            .On("df -B1", "Filesystem Type 1B-blocks Used Available Use% Mounted\n/dev/sda1 ext4 900000000000 100000000000 800000000000 12% /\n")
            .On("ip -o link", "2: eth0: <UP> link/ether aa:bb:cc:dd:ee:ff brd ff:ff:ff:ff:ff:ff\n")
            .On("ip -o -4 addr", "2: eth0    inet 10.0.0.50/24 brd 10.0.0.255 scope global eth0\n")
            .On("who", "root pts/0 2024-01-15 10:00\n")
            .On("last -n 1", "deploy pts/0 10.0.0.9 Mon Jan 15 09:00 still logged in\n")
            .On("echo dpkg", "dpkg\n")            // package-manager detection returns 'dpkg'
            .On("dpkg-query -W", "nginx\t1.18.0\tNginx Team\nopenssl\t3.0.2\tUbuntu\n");

    [Fact]
    public async Task Inventory_PopulatesLinuxMachine()
    {
        var session = FullyPopulatedSession();
        var factory = new FakeSshSessionFactory((h, u, p) => session);
        var runner = new LinuxInventoryRunner(factory);

        var m = new Machine("10.0.0.50") { DeviceType = DeviceType.UnixLinux };
        var outcome = await runner.InventoryAsync(m, new[] { Cred("root") }, null, null, default);

        Assert.True(outcome.Authenticated);
        Assert.Equal(MachineStatus.Done, m.Status);
        Assert.Equal("Ubuntu 22.04.3 LTS", m.Os.Caption);
        Assert.Equal("5.15.0-91-generic", m.Os.Build);
        Assert.Equal("x86_64", m.Os.Architecture);
        Assert.Equal("web01.corp.local", m.Fqdn);
        Assert.Equal("Dell Inc.", m.System.Manufacturer);
        Assert.Equal("PowerEdge R740", m.System.Model);
        Assert.Equal("ABC123", m.System.SerialNumber);
        Assert.Single(m.Cpus);
        Assert.Equal(4, m.Cpus[0].Cores);
        Assert.Equal(8_000_000L * 1024, m.TotalMemoryBytes);
        Assert.Single(m.Disks);
        Assert.Single(m.Volumes);
        Assert.Single(m.Adapters);
        Assert.Equal("AA:BB:CC:DD:EE:FF", m.Adapters[0].Mac);
        Assert.Equal("root", m.System.LoggedOnUser);
        Assert.Equal("deploy", m.System.LastLoggedOnUser);
        Assert.Equal(2, m.Software.Count);
        Assert.Contains(m.Software, s => s.DisplayName == "nginx" && s.Source == SoftwareSource.Package);
    }

    [Fact]
    public async Task Inventory_TriesCredentialsUntilOneAuthenticates()
    {
        var session = FullyPopulatedSession();
        var factory = new FakeSshSessionFactory((h, u, p) =>
            u == "root" ? session : throw new SshException(SshFailureKind.AuthFailed, "bad password"));
        var runner = new LinuxInventoryRunner(factory);

        var m = new Machine("10.0.0.50") { DeviceType = DeviceType.UnixLinux };
        var outcome = await runner.InventoryAsync(m, new[] { Cred("baduser"), Cred("root") }, null, null, default);

        Assert.True(outcome.Authenticated);
        Assert.Equal("root", outcome.CredentialLabel);
        Assert.Equal(new[] { "baduser", "root" }, factory.AttemptedUsers);
    }

    [Fact]
    public async Task Inventory_AllCredsRejected_IsAuthFailed()
    {
        var factory = new FakeSshSessionFactory((h, u, p) =>
            throw new SshException(SshFailureKind.AuthFailed, "denied"));
        var m = new Machine("10.0.0.50") { DeviceType = DeviceType.UnixLinux };
        var outcome = await new LinuxInventoryRunner(factory).InventoryAsync(m, new[] { Cred("a") }, null, null, default);

        Assert.False(outcome.Authenticated);
        Assert.Equal(MachineStatus.AuthFailed, m.Status);
    }

    [Fact]
    public async Task Inventory_Unreachable_ShortCircuits()
    {
        var factory = new FakeSshSessionFactory((h, u, p) =>
            throw new SshException(SshFailureKind.Unreachable, "connection refused"));
        var m = new Machine("10.0.0.50") { DeviceType = DeviceType.UnixLinux };
        var outcome = await new LinuxInventoryRunner(factory).InventoryAsync(m, new[] { Cred("a"), Cred("b") }, null, null, default);

        Assert.Equal(MachineStatus.Unreachable, m.Status);
        Assert.Single(factory.AttemptedUsers); // stopped after the first unreachable
    }

    [Fact]
    public async Task Inventory_CurrentTokenCandidate_IsSkipped()
    {
        var factory = new FakeSshSessionFactory((h, u, p) => new FakeSshSession());
        var m = new Machine("10.0.0.50") { DeviceType = DeviceType.UnixLinux };
        var outcome = await new LinuxInventoryRunner(factory).InventoryAsync(
            m, new[] { new CredentialCandidate("Current session", WmiCredential.CurrentToken) }, null, null, default);

        Assert.False(outcome.Authenticated);
        Assert.Equal(MachineStatus.AuthFailed, m.Status);
        Assert.Empty(factory.AttemptedUsers); // current-token can't be used for SSH
    }
}
