using AIOPatcher.Agent.Platforms.Apt;
using AIOPatcher.Agent.Platforms.Windows;
using AIOPatcher.Server.Endpoints;

namespace AIOPatcher.Tests;

public sealed class SoftwareInventoryTests
{
    // Echte Ausgabe von "winget upgrade" (gekürzt), mit Fortschrittsanzeige davor und deutscher Fußzeile.
    private const string WingetOutput =
        "   -    \r   \\ \r   |    \r" +
        "Name                               ID                                  Version       Verfügbar      Quelle\n" +
        "-----------------------------------------------------------------------------------------------------------\n" +
        "7-Zip 23.01 (x64)                  7zip.7zip                           23.01         25.01          winget\n" +
        "Mozilla Firefox (x64 de)           Mozilla.Firefox.de                  132.0.1       133.0          winget\n" +
        "Battle.net                         Blizzard.BattleNet                  Unknown       2.40.0         winget\n" +
        "Sehr langer Programmname mit Ab…   Vendor.LongName                     1.0           1.1            winget\n" +
        "4 Aktualisierungen verfügbar.\n" +
        "2 Pakete haben Versionsnummern, die nicht ermittelt werden können. Verwenden Sie --include-unknown.\n";

    [Fact]
    public void Winget_ParsesTableByColumnPositions()
    {
        var upgrades = WingetClient.ParseTable(WingetOutput);

        Assert.Equal(4, upgrades.Count);
        Assert.Equal(new WingetUpgrade("7-Zip 23.01 (x64)", "7zip.7zip", "23.01", "25.01", "winget"), upgrades[0]);
        Assert.Equal("Mozilla.Firefox.de", upgrades[1].Id);
        Assert.Equal("Unknown", upgrades[2].Version);
        Assert.Equal("Sehr langer Programmname mit Ab", upgrades[3].Name);
    }

    [Fact]
    public void Winget_EmptyOrUnexpectedOutput_GivesNothing()
    {
        Assert.Empty(WingetClient.ParseTable(""));
        Assert.Empty(WingetClient.ParseTable("Keine installierten Pakete gefunden, die den Eingabekriterien entsprechen."));
    }

    [Fact]
    public void Dpkg_OnlyInstalledPackages_WithSourcePackage()
    {
        var output = "ii \tlibssl3\t3.0.13-0ubuntu3.4\topenssl\tUbuntu Developers <ubuntu-devel@lists.ubuntu.com>\n" +
                     "rc \toldpkg\t1.0\toldpkg\tX\n" +
                     "ii \tcurl\t8.5.0-2ubuntu10.6\t\tUbuntu Developers <x@y>\n";
        var installed = AptOutputParser.ParseInstalled(output);

        Assert.Equal(2, installed.Count);
        Assert.Equal("openssl", installed[0].PackageId);
        Assert.Equal("Ubuntu Developers", installed[0].Publisher);
        Assert.Equal("curl", installed[1].PackageId); // ohne Quellpaket = Paketname
    }

    [Fact]
    public void Snap_List()
    {
        var snaps = AptOutputParser.ParseSnapList("Name    Version   Rev    Tracking       Publisher   Notes\ncore22  20241001  1663   latest/stable  canonical✓  base\n").ToList();
        Assert.Equal("core22", Assert.Single(snaps).Name);
        Assert.Equal("canonical", snaps[0].Publisher);
    }

    [Theory]
    [InlineData("winget", "Mozilla.Firefox", null, "https://github.com/microsoft/winget-pkgs/tree/master/manifests/m/Mozilla/Firefox")]
    [InlineData("dpkg", "openssl", "Ubuntu 24.04 LTS", "https://launchpad.net/ubuntu/+source/openssl")]
    [InlineData("dpkg", "openssl", "Proxmox VE", "https://tracker.debian.org/pkg/openssl")]
    [InlineData("registry", null, null, null)]
    public void InfoUrl_PerSource(string source, string? packageId, string? os, string? expected) =>
        Assert.Equal(expected, SoftwareEndpoints.InfoUrl(source, packageId, os));
}
