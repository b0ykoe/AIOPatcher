using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Jobs;

public sealed record PatchOutcome(bool Success, string Summary);

/// <summary>Plattformspezifisches Installieren von Updates und Auslösen eines Neustarts.</summary>
public interface IPatchPlatform
{
    Task<PatchOutcome> PatchAsync(PatchScope scope, Action<string> log, CancellationToken ct);

    /// <summary>Löst einen Neustart mit kurzer Verzögerung aus. true = Neustart ist eingeplant.</summary>
    Task<bool> RequestRebootAsync(Action<string> log, CancellationToken ct);
}
