using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Modules;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Stand des Agent-Self-Updates eines Hosts, für die Anzeige: aktuell, wartet auf das nächste Angebot, läuft gerade,
/// gescheitert oder gar nicht möglich (mit Grund). Spiegelt die Bedingungen von <see cref="AgentServerModule"/> und
/// <see cref="AgentDownloads.OfferFor"/>.
/// </summary>
public static class AgentUpdateStatus
{
    public const string Current = "current";
    public const string Waiting = "waiting";
    public const string Updating = "updating";
    public const string Failed = "failed";
    public const string Unavailable = "unavailable";

    /// <summary>Wie lange ein angebotenes Update als "läuft gerade" gilt (Download, Selbsttest, Neustart).</summary>
    public static readonly TimeSpan UpdatingWindow = TimeSpan.FromMinutes(15);

    /// <param name="Reason">Bei Unavailable: "unknown", "lite", "featureOff", "noBuild", "unsigned", "agentNoUpdate".</param>
    /// <param name="NextOfferAt">Bei Waiting: frühestens dann bietet der Server das Update wieder an (null = beim nächsten Abruf).</param>
    public sealed record Result(string State, string? Reason, DateTime? NextOfferAt);

    /// <param name="offerInterval">Abstand zwischen Angeboten (Einstellungen); null = fester Standard.</param>
    public static Result Evaluate(ManagedHost host, AgentDownloads downloads, FeatureFlags features, AgentTaskRecord? lastUpdateTask, DateTime now,
        TimeSpan? offerInterval = null)
    {
        var latest = AgentDownloads.AgentVersion;
        if (host.AgentVersion is null)
        {
            return new(Unavailable, "unknown", null);
        }

        if (host.AgentVersion.EndsWith("-lite", StringComparison.Ordinal))
        {
            return new(Unavailable, "lite", null);
        }

        if (!AgentDownloads.IsNewer(latest, host.AgentVersion))
        {
            return new(Current, null, null);
        }

        if (!features.IsOn(Feature.AgentSelfUpdate))
        {
            return new(Unavailable, "featureOff", null);
        }

        if (AgentDownloads.RuntimeId(host.OsFamily, host.Architecture) is not { } rid || downloads.Find(rid) is null)
        {
            return new(Unavailable, "noBuild", null);
        }

        if (downloads.SignerFor(rid) is null)
        {
            return new(Unavailable, "unsigned", null);
        }

        if (string.Equals(host.AgentUpdateBlockedVersion, latest, StringComparison.Ordinal))
        {
            return new(Failed, null, null);
        }

        // Pull-Agent ohne Update-Aktion (sehr alt); Agents vor dem Pull-Protokoll bekommen das Angebot beim Check-in
        if (host.ReportedModules is not null && !ModuleJson.Supports(host, AgentModules.Agent, ModuleActions.Update))
        {
            return new(Unavailable, "agentNoUpdate", null);
        }

        if (lastUpdateTask is { } task && now - task.CreatedAt < UpdatingWindow && task.Status is JobStatus.Queued or JobStatus.Running or JobStatus.Succeeded)
        {
            return new(Updating, null, null);
        }

        var asked = host.AgentUpdateRequestedAt is { } requested && (lastUpdateTask is null || requested > lastUpdateTask.CreatedAt);
        var next = lastUpdateTask is null || asked ? (DateTime?)null : lastUpdateTask.CreatedAt + (offerInterval ?? ServerModule.InventoryInterval);
        return new(Waiting, null, next > now ? next : null);
    }
}
