using AIOPatcher.Agent.Platforms.Apt;

namespace AIOPatcher.Tests;

public class AptOutputParserTests
{
    private const string DebianProxmoxOutput = """
        NOTE: This is only a simulation!
              apt-get needs root privileges for real execution.
        Reading package lists...
        Building dependency tree...
        Calculating upgrade...
        The following NEW packages will be installed:
          proxmox-kernel-6.8.12-5-pve-signed
        The following packages will be upgraded:
          libssl3 openssl pve-manager
        3 upgraded, 1 newly installed, 0 to remove and 0 not upgraded.
        Inst libssl3 [3.0.13-1~deb12u1] (3.0.15-1~deb12u1 Debian-Security:12/stable-security [amd64])
        Inst openssl [3.0.13-1~deb12u1] (3.0.15-1~deb12u1 Debian-Security:12/stable-security [amd64])
        Inst proxmox-kernel-6.8.12-5-pve-signed (6.8.12-5 Proxmox:bookworm/pve-no-subscription [amd64])
        Inst pve-manager [8.2.7] (8.3.0 Proxmox:bookworm/pve-no-subscription [amd64])
        Conf libssl3 (3.0.15-1~deb12u1 Debian-Security:12/stable-security [amd64])
        Conf openssl (3.0.15-1~deb12u1 Debian-Security:12/stable-security [amd64])
        """;

    private const string UbuntuOutput = """
        Inst curl [8.5.0-2ubuntu10.4] (8.5.0-2ubuntu10.6 Ubuntu:24.04/noble-updates, Ubuntu:24.04/noble-security [amd64]) []
        Inst snapd [2.63+24.04] (2.66.1+24.04 Ubuntu:24.04/noble-updates [amd64])
        Inst tzdata [2024a-3ubuntu1.1] (2024b-0ubuntu0.24.04 Ubuntu:24.04/noble-updates [all])
        """;

    [Fact]
    public void Parse_DebianProxmox_FindsInstLinesOnly()
    {
        var updates = AptOutputParser.Parse(DebianProxmoxOutput);

        Assert.Equal(4, updates.Count);
        Assert.Equal(["libssl3", "openssl", "proxmox-kernel-6.8.12-5-pve-signed", "pve-manager"], updates.Select(u => u.Name));
    }

    [Fact]
    public void Parse_ExtractsVersionsAndSecurityFlag()
    {
        var updates = AptOutputParser.Parse(DebianProxmoxOutput).ToDictionary(u => u.Name);

        var ssl = updates["libssl3"];
        Assert.Equal("3.0.13-1~deb12u1", ssl.CurrentVersion);
        Assert.Equal("3.0.15-1~deb12u1", ssl.AvailableVersion);
        Assert.True(ssl.IsSecurity);
        Assert.Equal("Debian-Security:12/stable-security", ssl.Classification);

        var kernel = updates["proxmox-kernel-6.8.12-5-pve-signed"];
        Assert.Null(kernel.CurrentVersion); // neu installiert
        Assert.False(kernel.IsSecurity);

        Assert.False(updates["pve-manager"].IsSecurity);
    }

    [Fact]
    public void Parse_Ubuntu_DetectsSecurityAmongMultipleOrigins()
    {
        var updates = AptOutputParser.Parse(UbuntuOutput).ToDictionary(u => u.Name);

        Assert.Equal(3, updates.Count);
        Assert.True(updates["curl"].IsSecurity);
        Assert.Equal("8.5.0-2ubuntu10.6", updates["curl"].AvailableVersion);
        Assert.False(updates["snapd"].IsSecurity);
        Assert.Equal("2024b-0ubuntu0.24.04", updates["tzdata"].AvailableVersion);
    }

    [Fact]
    public void Parse_HandlesCrlfAndEmptyOutput()
    {
        Assert.Empty(AptOutputParser.Parse(""));
        Assert.Single(AptOutputParser.Parse("Inst a [1] (2 Debian:13/stable [amd64])\r\n"));
    }
}

public sealed class KernelAndRebootTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N"));

    public KernelAndRebootTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData("6.8.12-10-pve", "6.8.12-9-pve", 1)]
    [InlineData("6.8.0-47-generic", "6.8.0-47-generic", 0)]
    [InlineData("6.1.0-26-amd64", "6.11.0-1-amd64", -1)]
    public void KernelVersionComparer_ComparesNumerically(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(KernelVersionComparer.Instance.Compare(a, b)));
    }

    [Fact]
    public void RebootRequired_WhenNewerKernelInBoot()
    {
        File.WriteAllText(Path.Combine(_dir, "vmlinuz-6.8.12-4-pve"), "");
        File.WriteAllText(Path.Combine(_dir, "vmlinuz-6.8.12-5-pve"), "");

        Assert.True(RebootDetector.IsRebootRequired("6.8.12-4-pve", _dir, Path.Combine(_dir, "missing-flag")));
        Assert.False(RebootDetector.IsRebootRequired("6.8.12-5-pve", _dir, Path.Combine(_dir, "missing-flag")));
    }

    [Fact]
    public void RebootRequired_WhenFlagFileExists_OrFalseInContainerWithoutBoot()
    {
        var flag = Path.Combine(_dir, "reboot-required");
        File.WriteAllText(flag, "");

        Assert.True(RebootDetector.IsRebootRequired("6.8.12-5-pve", Path.Combine(_dir, "no-boot"), flag));
        Assert.False(RebootDetector.IsRebootRequired("6.8.12-5-pve", Path.Combine(_dir, "no-boot"), Path.Combine(_dir, "missing-flag")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
