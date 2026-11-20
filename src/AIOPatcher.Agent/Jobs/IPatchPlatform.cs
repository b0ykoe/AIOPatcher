using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Jobs;

public sealed record PatchOutcome(bool Success, string Summary);

/// <summary>Plattformspezifisches Installieren von Updates und Auslösen eines Neustarts.</summary>
public interface IPatchPlatform
{
    /// <param name="holds">Muster zurückgehaltener Updates; diese werden nicht installiert.</param>
    /// <param name="fixBrokenSources">Dauerhaft kaputte Fremd-Paketquellen vorher deaktivieren (nur apt).</param>
    Task<PatchOutcome> PatchAsync(PatchScope scope, IReadOnlyList<string> holds, bool fixBrokenSources, Action<string> log, CancellationToken ct);

    /// <summary>Löst einen Neustart mit kurzer Verzögerung aus. true = Neustart ist eingeplant.</summary>
    Task<bool> RequestRebootAsync(Action<string> log, CancellationToken ct);

    /// <summary>Neustart sofort erzwingen, wenn der eingeplante nicht stattfand (abgebrochen oder blockiert).</summary>
    Task<bool> ForceRebootNowAsync(Action<string> log, CancellationToken ct) => RequestRebootAsync(log, ct);
}
