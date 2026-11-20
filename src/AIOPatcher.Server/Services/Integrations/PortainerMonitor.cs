using System.Text.Json;
using System.Threading.Channels;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Notifications;
using AIOPatcher.Server.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Integrations;

/// <summary>
/// Fragt Portainer alle 30 Minuten ab (Umgebungen, Stacks, Container) und lässt die Images der Stacks wie Host-Container bei der
/// Registry prüfen (gleicher Zwischenspeicher je Image). Neue veraltete Stacks werden gemeldet.
/// </summary>
public sealed class PortainerMonitor(IServiceScopeFactory scopes, PortainerClientFactory clients, Notifier notifier, FeatureFlags features,
    ILogger<PortainerMonitor> logger, ScanSchedule? schedule = null, ContainerUpdateService? imageChecks = null) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                foreach (var integration in await db.Integrations.Where(i => i.Type == IntegrationType.Portainer && i.Enabled).ToListAsync(stoppingToken))
                {
                    await RefreshAsync(db, integration, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Portainer monitor failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Zustand abfragen und speichern. Fehler landen in Integration.LastError (der letzte Zustand bleibt stehen).</summary>
    public async Task<PortainerStatus?> RefreshAsync(AppDbContext db, Integration integration, CancellationToken ct, bool forceImageCheck = false)
    {
        PortainerStatus? status = null;
        try
        {
            var previous = Parse(integration.StatusJson);
            using (var client = clients.For(integration))
            {
                status = await client.GetStatusAsync(DateTime.UtcNow, ct);
            }

            var references = status.Stacks.SelectMany(s => s.Containers).Select(c => c.Reference).OfType<string>().Distinct().ToList();
            if (imageChecks is not null && features.IsOn(Feature.ContainerChecks) && references.Count > 0)
            {
                var interval = forceImageCheck ? TimeSpan.FromMinutes(1) : schedule?.ImageCheckInterval(null) ?? TimeSpan.FromHours(ScanSchedule.DefaultHours);
                await imageChecks.CheckReferencesAsync(db, references, interval, DateTime.UtcNow, ct);
            }

            var checks = await ChecksAsync(db, references, ct);
            var outdated = status.Stacks.Where(s => s.Containers.Any(c => PortainerClient.IsOutdated(c, checks) == true)).ToList();
            status = status with { OutdatedStacks = outdated.Select(s => s.Id).ToList() };
            integration.StatusJson = JsonSerializer.Serialize(status, Json);
            integration.LastError = null;

            var known = previous?.OutdatedStacks ?? [];
            var fresh = outdated.Where(s => !known.Contains(s.Id)).ToList();
            if (fresh.Count > 0)
            {
                notifier.Publish(new Notification(NotificationEvents.ContainerUpdates,
                    L.T($"Portainer {integration.Name}: neue Images für {fresh.Count} Stack(s)", $"Portainer {integration.Name}: new images for {fresh.Count} stack(s)"),
                    string.Join(", ", fresh.Take(10).Select(s => s.Name)) + (fresh.Count > 10 ? ", …" : ""),
                    NotificationSeverity.Info, integration.SiteId, LinkPath: "/inventory?tab=stacks"));
            }
        }
        catch (IntegrationException ex)
        {
            integration.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
        }

        integration.LastCheckAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return status;
    }

    public static async Task<Dictionary<string, ImageCheck>> ChecksAsync(AppDbContext db, IReadOnlyCollection<string> references, CancellationToken ct) =>
        references.Count == 0 ? [] : await db.ImageChecks.AsNoTracking().Where(c => references.Contains(c.Reference)).ToDictionaryAsync(c => c.Reference, ct);

    public static PortainerStatus? Parse(string? statusJson)
    {
        if (string.IsNullOrEmpty(statusJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PortainerStatus>(statusJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Führt Stack-Updates nacheinander aus (Warteschlange im Speicher, Zustand in <see cref="StackUpdateJob"/>). Nach einem Neustart
/// des Servers laufen wartende Jobs weiter; unterbrochene gelten als fehlgeschlagen.
/// </summary>
public sealed class PortainerStackUpdates(IServiceScopeFactory scopes, PortainerClientFactory clients, PortainerMonitor monitor, Notifier notifier,
    ILogger<PortainerStackUpdates> logger) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();

    public void Enqueue(Guid jobId) => _queue.Writer.TryWrite(jobId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await RunAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), id, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Portainer stack update {JobId} failed", id);
            }
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var job in await db.StackUpdateJobs.Where(j => j.Status == JobStatus.Running).ToListAsync(ct))
        {
            job.Status = JobStatus.Failed;
            job.FinishedAt = DateTime.UtcNow;
            job.Summary = L.T("Unterbrochen (Server neu gestartet). Stand bitte in Portainer prüfen.", "Interrupted (server restarted). Please check in Portainer.");
        }

        await db.SaveChangesAsync(ct);
        foreach (var id in await db.StackUpdateJobs.Where(j => j.Status == JobStatus.Queued).OrderBy(j => j.CreatedAt).Select(j => j.Id).ToListAsync(ct))
        {
            Enqueue(id);
        }
    }

    /// <summary>Einen Job ausführen: Stack neu ausrollen, danach Portainer neu einlesen (neue Digests, Badge verschwindet).</summary>
    public async Task RunAsync(AppDbContext db, Guid jobId, CancellationToken ct)
    {
        var job = await db.StackUpdateJobs.Include(j => j.Integration).FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is not { Status: JobStatus.Queued } || job.Integration is not { } integration)
        {
            return;
        }

        var lines = new List<string>();
        void Log(string line)
        {
            lines.Add($"{DateTime.UtcNow:HH:mm:ss} {line}");
            var text = string.Join('\n', lines);
            job.Log = text.Length > StackUpdateJob.MaxLog ? text[^StackUpdateJob.MaxLog..] : text;
        }

        job.Status = JobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        Log(L.T($"Start: Stack {job.StackName} (ID {job.StackId}, Umgebung {job.EnvironmentId}) über {integration.Name}",
            $"Start: stack {job.StackName} (ID {job.StackId}, environment {job.EnvironmentId}) via {integration.Name}"));
        await db.SaveChangesAsync(ct);

        try
        {
            using var client = clients.For(integration);
            job.Summary = await client.RedeployAsync(job.StackId, job.EnvironmentId, Log, ct);
            job.Status = JobStatus.Succeeded;
        }
        catch (IntegrationException ex)
        {
            Log(ex.Message);
            job.Status = JobStatus.Failed;
            job.Summary = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
        }

        job.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        notifier.Publish(new Notification(job.Status == JobStatus.Succeeded ? NotificationEvents.JobSucceeded : NotificationEvents.JobFailed,
            job.Status == JobStatus.Succeeded
                ? L.T($"Stack {job.StackName} aktualisiert", $"Stack {job.StackName} updated")
                : L.T($"Stack {job.StackName}: Update fehlgeschlagen", $"Stack {job.StackName}: update failed"),
            $"Portainer {integration.Name}: {job.Summary}",
            job.Status == JobStatus.Succeeded ? NotificationSeverity.Info : NotificationSeverity.Warning, integration.SiteId, LinkPath: "/inventory?tab=stacks"));

        if (job.Status == JobStatus.Succeeded)
        {
            await monitor.RefreshAsync(db, integration, ct);
        }
    }
}
