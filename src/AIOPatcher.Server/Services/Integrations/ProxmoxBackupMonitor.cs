using System.Text.Json;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Integrations;

/// <summary>
/// Fragt Proxmox Backup Server alle 15 Minuten ab (Datastores, Tasks, Backup-Gruppen) und meldet Probleme:
/// fehlgeschlagene Tasks, Datastore ab 90 % voll, Backup-Gruppen ohne neues Backup seit 48 Stunden.
/// </summary>
public sealed class ProxmoxBackupMonitor(IServiceScopeFactory scopes, ProxmoxBackupClientFactory clients, Notifier notifier, ILogger<ProxmoxBackupMonitor> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);
    public const double FullPercent = 90;
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
                foreach (var integration in await db.Integrations.Where(i => i.Type == IntegrationType.ProxmoxBackup && i.Enabled).ToListAsync(stoppingToken))
                {
                    await RefreshAsync(db, integration, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Proxmox Backup Server monitor failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Zustand abfragen und speichern. Fehler landen in Integration.LastError (der letzte Zustand bleibt stehen).</summary>
    public async Task<PbsStatus?> RefreshAsync(AppDbContext db, Integration integration, CancellationToken ct)
    {
        PbsStatus? status = null;
        try
        {
            var previous = Parse(integration.StatusJson);
            status = await clients.For(integration).GetStatusAsync(DateTime.UtcNow, ct);
            integration.StatusJson = JsonSerializer.Serialize(status, Json);
            integration.LastError = null;
            foreach (var problem in Problems(previous, status))
            {
                notifier.Publish(new Notification(NotificationEvents.BackupProblem, $"PBS {integration.Name}: {problem.Title}", problem.Message,
                    NotificationSeverity.Warning, integration.SiteId, LinkPath: "/inventory?tab=backups"));
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

    public static PbsStatus? Parse(string? statusJson)
    {
        if (string.IsNullOrEmpty(statusJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PbsStatus>(statusJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static double? UsedPercent(PbsDatastore store) =>
        store.Total is > 0 && store.Used is { } used ? Math.Round(100.0 * used / store.Total.Value, 1) : null;

    public static bool IsStale(PbsGroup group, DateTime now) => group.LastBackup is { } last && now - last > StaleAfter;

    public sealed record Problem(string Title, string Message);

    /// <summary>
    /// Neue Probleme gegenüber der letzten Abfrage. Beim ersten Abruf (previous null) nur volle Datastores, damit die
    /// Historie der letzten Tage nicht auf einmal gemeldet wird.
    /// </summary>
    public static IReadOnlyList<Problem> Problems(PbsStatus? previous, PbsStatus current)
    {
        var problems = new List<Problem>();

        if (previous is not null)
        {
            var known = previous.FailedTasks.Select(t => t.Upid).ToHashSet();
            var fresh = current.FailedTasks.Where(t => !known.Contains(t.Upid) && t.Start >= previous.CheckedAt - TimeSpan.FromHours(1)).ToList();
            if (fresh.Count > 0)
            {
                problems.Add(new Problem(
                    L.T($"{fresh.Count} Task(s) fehlgeschlagen", $"{fresh.Count} task(s) failed"),
                    string.Join("\n", fresh.Take(5).Select(t => $"{t.Type} {t.WorkerId}: {t.Status}")) + (fresh.Count > 5 ? "\n…" : "")));
            }

            var wasStale = previous.Groups.Where(g => IsStale(g, previous.CheckedAt)).Select(Key).ToHashSet();
            var stale = current.Groups.Where(g => IsStale(g, current.CheckedAt) && !wasStale.Contains(Key(g))).ToList();
            if (stale.Count > 0)
            {
                problems.Add(new Problem(
                    L.T($"{stale.Count} Backup(s) älter als {StaleAfter.TotalHours:0} Stunden", $"{stale.Count} backup(s) older than {StaleAfter.TotalHours:0} hours"),
                    string.Join("\n", stale.Take(5).Select(g => $"{g.Type}/{g.Id} ({g.Store}{(g.Namespace.Length > 0 ? "/" + g.Namespace : "")})"))
                    + (stale.Count > 5 ? "\n…" : "")));
            }
        }

        foreach (var store in current.Datastores)
        {
            var before = previous?.Datastores.FirstOrDefault(d => d.Store == store.Store);
            if (UsedPercent(store) is >= FullPercent && (before is null || UsedPercent(before) is not >= FullPercent))
            {
                problems.Add(new Problem(L.T($"Datastore {store.Store} fast voll", $"Datastore {store.Store} almost full"),
                    L.T($"{UsedPercent(store):0.#} % belegt.", $"{UsedPercent(store):0.#} % used.")));
            }

            if (store.Error is not null && before?.Error is null)
            {
                problems.Add(new Problem(L.T($"Datastore {store.Store} nicht verfügbar", $"Datastore {store.Store} not available"), store.Error));
            }
        }

        return problems;

        static string Key(PbsGroup g) => $"{g.Store}|{g.Namespace}|{g.Type}|{g.Id}";
    }
}
