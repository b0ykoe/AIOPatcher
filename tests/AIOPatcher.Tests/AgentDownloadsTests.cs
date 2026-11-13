using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;

namespace AIOPatcher.Tests;

public sealed class AgentDownloadsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("../../appsettings.json")]
    [InlineData("linux-x86")]
    [InlineData("")]
    [InlineData("LINUX-X64")]
    public void Find_RejectsAnythingOutsideWhitelist(string rid)
    {
        Assert.Null(new AgentDownloads(new AIOPatcherOptions { DataDirectory = _dir }).Find(rid));
    }

    [Fact]
    public void Find_PrefersDataDirectory_AndHashesFile()
    {
        var target = Path.Combine(_dir, "agents", "linux-x64");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "aiopatcher-agent"), "abc");
        var downloads = new AgentDownloads(new AIOPatcherOptions { DataDirectory = _dir });

        var file = downloads.Find("linux-x64");

        Assert.NotNull(file);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", downloads.Sha256(file)); // SHA-256("abc")
        Assert.Null(downloads.Find("win-x64"));
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
