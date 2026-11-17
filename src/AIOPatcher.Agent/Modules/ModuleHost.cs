using System.Text.Json;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Modules;

/// <summary>Was ein Modul zu einem Auftrag zurückgibt.</summary>
/// <param name="Payload">Daten der Antwort (siehe <see cref="PullProtocol"/>).</param>
/// <param name="RebootRequested">Nach der Antwort neu starten (Ergebnis wird erst nach dem Neustart gemeldet).</param>
/// <param name="AfterAnswer">Läuft, nachdem der Server die Antwort angenommen hat (z. B. Neustart nach Self-Update).</param>
public sealed record TaskOutcome(
    JobOutcome Outcome,
    string Summary,
    JsonElement? Payload = null,
    bool RebootRequested = false,
    Func<TaskResultResponse, Task>? AfterAnswer = null)
{
    public static TaskOutcome Rejected(string summary) => new(JobOutcome.Rejected, summary);
}

public sealed record TaskContext(AgentPolicy Policy, Action<string> Log);

/// <summary>
/// Ein Modul des Agents: feste Aktionen, sonst nichts. Der Agent führt nur Aufträge aus, die zu einem Modul und einer
/// seiner Aktionen passen. Module lassen sich lokal (policy.json disabledModules) und im Dashboard pro Host sperren.
/// </summary>
public interface IAgentModule
{
    string Id { get; }

    /// <summary>Gibt es das auf diesem Host (z. B. Docker installiert, winget vorhanden)?</summary>
    bool Available { get; }

    IReadOnlyList<string> Actions { get; }

    Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct);
}

/// <summary>Verteilt Aufträge an die Module und setzt die lokale Policy durch.</summary>
public sealed class ModuleHost(IEnumerable<IAgentModule> modules)
{
    private readonly IReadOnlyList<IAgentModule> _modules = modules.ToList();

    public IReadOnlyList<ModuleInfo> Describe(AgentPolicy policy) =>
        _modules.Select(m => new ModuleInfo(m.Id, m.Available, policy.IsModuleDisabled(m.Id), m.Actions)).ToList();

    public async Task<TaskOutcome> ExecuteAsync(AgentTask task, TaskContext context, CancellationToken ct)
    {
        var module = _modules.FirstOrDefault(m => m.Id == task.Module);
        if (module is null || !module.Actions.Contains(task.Action))
        {
            return TaskOutcome.Rejected($"Unknown task {task.Module}.{task.Action} (agent {AgentState.AgentVersion}).");
        }

        if (context.Policy.IsModuleDisabled(module.Id))
        {
            return TaskOutcome.Rejected($"Module '{module.Id}' is disabled on this host (policy.json disabledModules).");
        }

        if (!module.Available)
        {
            return TaskOutcome.Rejected($"Module '{module.Id}' is not available on this host.");
        }

        return await module.ExecuteAsync(task, context, ct);
    }
}
