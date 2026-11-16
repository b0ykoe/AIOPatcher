using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Jobs;

public sealed record PatchOutcome(bool Success, string Summary);

/// <summary>Plattformspezifisches Installieren von Updates und Auslösen eines Neustarts.</summary>
public interface IPatchPlatform
{
    /// <param name="holds">Muster zurückgehaltener Updates; diese werden nicht installiert.</param>
    Task<PatchOutcome> PatchAsync(PatchScope scope, IReadOnlyList<string> holds, Action<string> log, CancellationToken ct);

    /// <summary>Löst einen Neustart mit kurzer Verzögerung aus. true = Neustart ist eingeplant.</summary>
    Task<bool> RequestRebootAsync(Action<string> log, CancellationToken ct);
}
