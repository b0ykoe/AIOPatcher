using AIOPatcher.Agent;
using AIOPatcher.Agent.Modules;
using AIOPatcher.Contracts;

namespace AIOPatcher.Tests;

/// <summary>"Jetzt aktualisieren": Agent auf dem Server-Host startet aiopatcher-update, nur mit lokaler Freigabe.</summary>
public sealed class ServerUpdateTests
{
    [Fact]
    public async Task Agent_RejectsWithoutLocalPermission()
    {
        var log = new List<string>();
        var outcome = await new ServerUpdateModule().ExecuteAsync(new AgentTask(Guid.NewGuid(), AgentModules.ServerUpdate, ModuleActions.Update),
            new TaskContext(new AgentPolicy(), log.Add), default);
        Assert.Equal(JobOutcome.Rejected, outcome.Outcome);
        Assert.Contains("allowServerUpdate", outcome.Summary);
    }

    [Fact]
    public void Policy_KeyCanBeSet()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiop-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(AgentPolicy.Load(dir).AllowServerUpdate);
            Assert.True(AgentPolicy.Set(dir, "allowServerUpdate", "true").AllowServerUpdate);
            Assert.True(AgentPolicy.Load(dir).AllowServerUpdate);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Log_IsFreeOfColorCodes() =>
        Assert.Equal("==> Done: AIOPatcher 0.12.0 → 0.12.1.", ServerUpdateModule.StripAnsi("\u001b[1;35m==>\u001b[0m Done: AIOPatcher 0.12.0 → 0.12.1."));

    [Fact]
    public void Module_IsOnlyAvailableWhereTheSetupInstalledTheCommand()
    {
        // Testrechner hat kein /usr/local/bin/aiopatcher-update
        Assert.False(new ServerUpdateModule().Available);
        Assert.Contains(AgentModules.ServerUpdate, AgentModules.All);
    }
}
