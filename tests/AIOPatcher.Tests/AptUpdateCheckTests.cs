using AIOPatcher.Agent.Platforms.Apt;
using AIOPatcher.Contracts;

namespace AIOPatcher.Tests;

public sealed class AptUpdateCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiop-apt-" + Guid.NewGuid().ToString("N"));
    private string Sources => Path.Combine(_dir, "sources.list.d");
    private string Backup => Path.Combine(_dir, "aiopatcher-disabled");

    public AptUpdateCheckTests() => Directory.CreateDirectory(Sources);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string[] Lines(string text) => text.Replace("\r", "").Split('\n');

    // echte Ausgabe: NodeSource hat node_18.x eingestellt, alle Ubuntu-Quellen sind in Ordnung
    private static readonly string[] NodeSourceGone = Lines("""
        Hit:1 http://de.archive.ubuntu.com/ubuntu jammy InRelease
        Hit:2 https://packages.couchbase.com/repos/debian jammy InRelease
        Hit:6 http://de.archive.ubuntu.com/ubuntu jammy-security InRelease
        Ign:7 https://deb.nodesource.com/node_18.x jammy InRelease
        Err:8 https://deb.nodesource.com/node_18.x jammy Release
          404  Not Found [IP: 172.66.150.169 443]
        Reading package lists...
        E: The repository 'https://deb.nodesource.com/node_18.x jammy Release' no longer has a Release file.
        """);

    [Fact]
    public void DeadThirdPartyRepo_ContinuesWithWarning_Permanent()
    {
        var result = AptUpdateCheck.Analyze(100, NodeSourceGone);

        Assert.True(result.CanContinue);
        var broken = Assert.Single(result.Broken);
        Assert.Equal("https://deb.nodesource.com/node_18.x jammy", broken.Source);
        Assert.True(broken.Permanent);
        Assert.Equal("404  Not Found; no longer has a Release file", broken.Reason);
    }

    [Fact]
    public void Lock_Aborts()
    {
        var output = Lines("""
            Hit:1 http://deb.debian.org/debian bookworm InRelease
            E: Could not get lock /var/lib/apt/lists/lock. It is held by process 1234 (apt-get)
            """);

        Assert.False(AptUpdateCheck.Analyze(100, output).CanContinue);
    }

    [Fact]
    public void NothingLoaded_Aborts_DnsIsNotPermanent()
    {
        var output = Lines("""
            Err:1 http://deb.debian.org/debian bookworm InRelease
              Temporary failure resolving 'deb.debian.org'
            W: Failed to fetch http://deb.debian.org/debian/dists/bookworm/InRelease  Temporary failure resolving 'deb.debian.org'
            """);

        var result = AptUpdateCheck.Analyze(100, output);

        Assert.False(result.CanContinue);
        var broken = Assert.Single(result.Broken);
        Assert.Equal("http://deb.debian.org/debian bookworm", broken.Source);
        Assert.False(broken.Permanent);
    }

    [Fact]
    public void GpgError_ReportedButNotPermanent()
    {
        var result = AptUpdateCheck.Analyze(0, Lines("""
            Hit:1 http://deb.debian.org/debian bookworm InRelease
            W: GPG error: https://repo.example.org/apt stable InRelease: The following signatures couldn't be verified because the public key is not available: NO_PUBKEY 1234
            """));

        var broken = Assert.Single(result.Broken);
        Assert.Equal("https://repo.example.org/apt stable", broken.Source);
        Assert.False(broken.Permanent);
    }

    [Fact]
    public void UnknownError_Aborts() =>
        Assert.False(AptUpdateCheck.Analyze(100, Lines("Hit:1 http://deb.debian.org/debian bookworm InRelease\nE: Something else broke")).CanContinue);

    [Fact]
    public void ParseList_IgnoresOptionsAndComments()
    {
        var entries = AptSources.ParseList("""
            # alt
            # deb https://old.example.org/ jammy main
            deb [arch=amd64 signed-by=/usr/share/keyrings/nodesource.gpg] https://deb.nodesource.com/node_18.x/ jammy main
            deb-src https://deb.nodesource.com/node_18.x jammy main
            """);

        Assert.Equal([new AptSources.Entry("https://deb.nodesource.com/node_18.x", "jammy")], entries);
    }

    [Fact]
    public void ParseSources_Deb822_WithEmbeddedKeyAndDisabledStanza()
    {
        var entries = AptSources.ParseSources("""
            Types: deb
            URIs: http://archive.ubuntu.com/ubuntu/
            Suites: noble noble-updates
            Components: main
            Signed-By:
             -----BEGIN PGP PUBLIC KEY BLOCK-----
             URIs: http://evil.example.org/
             -----END PGP PUBLIC KEY BLOCK-----

            Types: deb
            URIs: https://disabled.example.org
            Suites: stable
            Enabled: no
            """);

        Assert.Equal(
            [new AptSources.Entry("http://archive.ubuntu.com/ubuntu", "noble"), new AptSources.Entry("http://archive.ubuntu.com/ubuntu", "noble-updates")],
            entries);
    }

    [Fact]
    public void Classify_ThirdPartyFileFixable_DistroAndMixedFilesNot()
    {
        File.WriteAllText(Path.Combine(Sources, "nodesource.list"), "deb [signed-by=/k.gpg] https://deb.nodesource.com/node_18.x jammy main\n");
        File.WriteAllText(Path.Combine(Sources, "mixed.list"), "deb https://gone.example.org/apt stable main\ndeb https://fine.example.org/apt stable main\n");
        File.WriteAllText(Path.Combine(Sources, "ubuntu.sources"), "Types: deb\nURIs: http://de.archive.ubuntu.com/ubuntu\nSuites: jammy\nComponents: main\n");
        BrokenAptSource[] broken =
        [
            new("https://deb.nodesource.com/node_18.x jammy", "404", true),
            new("https://gone.example.org/apt stable", "404", true),
            new("http://de.archive.ubuntu.com/ubuntu jammy", "404", true),
            new("https://temp.example.org/apt stable", "Temporary failure resolving", false),
        ];

        var result = AptSources.Classify(broken, Sources).ToDictionary(b => b.Source);

        Assert.True(result["https://deb.nodesource.com/node_18.x jammy"].Fixable);
        Assert.EndsWith("nodesource.list", result["https://deb.nodesource.com/node_18.x jammy"].File);
        Assert.False(result["https://gone.example.org/apt stable"].Fixable); // Datei enthält auch eine funktionierende Quelle
        Assert.False(result["http://de.archive.ubuntu.com/ubuntu jammy"].Fixable); // Distribution: nie anfassen
        Assert.Null(result["https://temp.example.org/apt stable"].File);
    }

    [Fact]
    public void Disable_MovesFileToBackup_OnlyInsideSourcesDirectory()
    {
        var file = Path.Combine(Sources, "nodesource.list");
        File.WriteAllText(file, "deb https://deb.nodesource.com/node_18.x jammy main\n");
        var outside = Path.Combine(_dir, "sources.list");
        File.WriteAllText(outside, "deb http://deb.debian.org/debian bookworm main\n");

        var moved = AptSources.Disable(file, Sources, Backup);

        Assert.NotNull(moved);
        Assert.False(File.Exists(file));
        Assert.StartsWith(Path.Combine(Backup, "nodesource.list."), moved);
        Assert.Null(AptSources.Disable(outside, Sources, Backup));
        Assert.True(File.Exists(outside));
    }
}
