using AIOPatcher.Agent.Platforms.Apt;

namespace AIOPatcher.Tests;

public class AptUpdateCheckTests
{
    private static string[] Lines(string text) => text.Replace("\r", "").Split('\n');

    [Fact]
    public void DeadThirdPartyRepo_ContinuesWithWarning()
    {
        // echte Ausgabe: NodeSource hat node_18.x eingestellt, alle Ubuntu-Quellen sind in Ordnung
        var output = Lines("""
            Hit:1 http://de.archive.ubuntu.com/ubuntu jammy InRelease
            Hit:2 https://packages.couchbase.com/repos/debian jammy InRelease
            Hit:6 http://de.archive.ubuntu.com/ubuntu jammy-security InRelease
            Ign:7 https://deb.nodesource.com/node_18.x jammy InRelease
            Err:8 https://deb.nodesource.com/node_18.x jammy Release
              404  Not Found [IP: 172.66.150.169 443]
            Reading package lists...
            E: The repository 'https://deb.nodesource.com/node_18.x jammy Release' no longer has a Release file.
            """);

        var result = AptUpdateCheck.Analyze(100, output);

        Assert.True(result.CanContinue);
        Assert.Equal(["https://deb.nodesource.com/node_18.x jammy"], result.BrokenSources);
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
    public void NothingLoaded_Aborts()
    {
        // z. B. kein DNS: keine Quelle erreichbar, dann lieber abbrechen als mit alten Listen patchen
        var output = Lines("""
            Err:1 http://deb.debian.org/debian bookworm InRelease
              Temporary failure resolving 'deb.debian.org'
            W: Failed to fetch http://deb.debian.org/debian/dists/bookworm/InRelease  Temporary failure resolving 'deb.debian.org'
            """);

        var result = AptUpdateCheck.Analyze(100, output);

        Assert.False(result.CanContinue);
        Assert.Equal(["http://deb.debian.org/debian bookworm"], result.BrokenSources);
    }

    [Fact]
    public void Success_NoWarning() =>
        Assert.Equal(new AptUpdateCheck.Result(true, []).CanContinue,
            AptUpdateCheck.Analyze(0, Lines("Hit:1 http://deb.debian.org/debian bookworm InRelease\nReading package lists...")).CanContinue);

    [Fact]
    public void UnknownError_Aborts() =>
        Assert.False(AptUpdateCheck.Analyze(100, Lines("Hit:1 http://deb.debian.org/debian bookworm InRelease\nE: Something else broke")).CanContinue);
}
