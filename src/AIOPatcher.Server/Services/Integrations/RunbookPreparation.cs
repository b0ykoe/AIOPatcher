using AIOPatcher.Server.Data;

namespace AIOPatcher.Server.Services.Integrations;

/// <summary>Vorbereitung eines Hosts in einem Ablauf: Backup prüfen, Snapshot anlegen. Austauschbar für Tests.</summary>
public interface IRunbookPreparation
{
    Task<DateTime?> LastBackupAsync(ManagedHost host, CancellationToken ct);
    Task<string> StartSnapshotAsync(ManagedHost host, string name, CancellationToken ct);
    Task<ProviderTaskStatus> GetTaskAsync(ManagedHost host, string taskRef, CancellationToken ct);
    Task PruneSnapshotsAsync(ManagedHost host, CancellationToken ct);
}

public sealed class RunbookPreparation(VirtualizationProviderFactory providers, ILogger<RunbookPreparation> logger) : IRunbookPreparation
{
    private IVirtualizationProvider Provider(ManagedHost host)
    {
        if (host.Integration is not { Enabled: true } integration || string.IsNullOrEmpty(host.ExternalId))
        {
            throw new IntegrationException(L.T("Keine Proxmox-/Hetzner-Zuordnung für diesen Host.", "No Proxmox/Hetzner mapping for this host."));
        }

        return providers.For(integration)
               ?? throw new IntegrationException(L.T("Diese Integration kann keine Snapshots.", "This integration cannot take snapshots."));
    }

    public async Task<DateTime?> LastBackupAsync(ManagedHost host, CancellationToken ct) => (await Provider(host).LatestBackupAsync(host.ExternalId!, ct))?.CreatedAt;

    public Task<string> StartSnapshotAsync(ManagedHost host, string name, CancellationToken ct) =>
        Provider(host).StartSnapshotAsync(host.ExternalId!, name, $"AIOPatcher before patch ({host.Name})", ct);

    public Task<ProviderTaskStatus> GetTaskAsync(ManagedHost host, string taskRef, CancellationToken ct) => Provider(host).GetTaskAsync(host.ExternalId!, taskRef, ct);

    public async Task PruneSnapshotsAsync(ManagedHost host, CancellationToken ct)
    {
        try
        {
            var deleted = await Provider(host).PruneSnapshotsAsync(host.ExternalId!, host.Integration!.KeepSnapshots, ct);
            if (deleted > 0)
            {
                logger.LogInformation("Deleted {Count} old snapshot(s) of {Host}", deleted, host.Name);
            }
        }
        catch (IntegrationException ex)
        {
            // Aufräumen ist nett, aber kein Grund, den Patch scheitern zu lassen.
            logger.LogWarning("Pruning snapshots of {Host} failed: {Error}", host.Name, ex.Message);
        }
    }
}
