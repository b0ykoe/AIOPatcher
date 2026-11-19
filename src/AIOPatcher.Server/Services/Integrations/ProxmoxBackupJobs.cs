using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Integrations;

/// <summary>Backup-Job aus Datacenter → Backup (/cluster/backup).</summary>
/// <param name="All">Alle Gäste (bzw. alle eines Knotens, wenn Node gesetzt).</param>
/// <param name="VmIds">Ausgewählte Gäste (leer bei All oder Pool).</param>
/// <param name="Exclude">Bei All: ausgenommene Gäste.</param>
public sealed record PveBackupJob(string Id, bool Enabled, string? Schedule, string? Storage, bool All, IReadOnlyList<string> VmIds, string? Pool,
    IReadOnlyList<string> Exclude, string? Mode, string? Comment, DateTime? NextRun, string? Node);

/// <summary>Gast ohne Backup-Job (/cluster/backup-info/not-backed-up).</summary>
public sealed record PveGuest(int VmId, string? Name, string? Type);

/// <summary>Fehlgeschlagener vzdump-Lauf (/cluster/tasks).</summary>
public sealed record PveBackupTask(string Upid, string? Node, string? Guest, DateTime Start, DateTime? End, string Status);

/// <summary>
/// Backup-Zustand eines Proxmox-VE-Clusters (in Integration.StatusJson). NotBackedUp und FailedTasks nur mit dem
/// Schalter "Backup-Jobs prüfen", sonst null (dann zeigt AIOPatcher nur, welche Jobs es gibt).
/// </summary>
public sealed record PveBackupStatus(IReadOnlyList<PveBackupJob> Jobs, IReadOnlyList<PveGuest>? NotBackedUp, IReadOnlyList<PveBackupTask>? FailedTasks,
    DateTime CheckedAt, string? Error = null);

/// <summary>
/// Liest alle 15 Minuten die Backup-Jobs der Proxmox-VE-Integrationen. Mit dem Schalter "Backup-Jobs prüfen" zusätzlich
/// Gäste ohne Backup-Job und fehlgeschlagene vzdump-Läufe (letzte 7 Tage), und meldet Neues als "Backup-Problem".
/// </summary>
public sealed class ProxmoxBackupJobsMonitor(IServiceScopeFactory scopes, VirtualizationProviderFactory providers, FeatureFlags features, Notifier notifier,
    ILogger<ProxmoxBackupJobsMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TaskWindow = TimeSpan.FromDays(7);
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
                foreach (var integration in await db.Integrations.Where(i => i.Type == IntegrationType.Proxmox && i.Enabled).ToListAsync(stoppingToken))
                {
                    await RefreshAsync(db, integration, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Proxmox backup job monitor failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Jobs (und mit Schalter: Lücken, Fehler) lesen und speichern. Ein Lesefehler steht im Zustand, nicht in Integration.LastError.</summary>
    public async Task<PveBackupStatus> RefreshAsync(AppDbContext db, Integration integration, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var previous = Parse(integration.StatusJson);
        PveBackupStatus status;
        try
        {
            var provider = (ProxmoxProvider)providers.For(integration)!;
            var jobs = ParseJobs(await provider.GetAsync("cluster/backup", ct));
            if (features.IsOn(Feature.BackupJobChecks))
            {
                var notBackedUp = ParseGuests(await provider.GetAsync("cluster/backup-info/not-backed-up", ct));
                var failed = ParseFailedTasks(await provider.GetAsync("cluster/tasks", ct), now - TaskWindow);
                status = new PveBackupStatus(jobs, notBackedUp, failed, now);
                foreach (var problem in Problems(previous, status))
                {
                    notifier.Publish(new Notification(NotificationEvents.BackupProblem, $"Proxmox {integration.Name}: {problem.Title}", problem.Message,
                        NotificationSeverity.Warning, integration.SiteId, LinkPath: "/inventory?tab=backups"));
                }
            }
            else
            {
                status = new PveBackupStatus(jobs, null, null, now);
            }
        }
        catch (IntegrationException ex)
        {
            status = (previous ?? new PveBackupStatus([], null, null, now)) with { CheckedAt = now, Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message };
        }

        integration.StatusJson = JsonSerializer.Serialize(status, Json);
        await db.SaveChangesAsync(ct);
        return status;
    }

    public static PveBackupStatus? Parse(string? statusJson)
    {
        if (string.IsNullOrEmpty(statusJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PveBackupStatus>(statusJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Neue Probleme: Gäste, die neu ohne Backup-Job sind (beim ersten Prüfen einmal alle zusammen), und neue
    /// fehlgeschlagene vzdump-Läufe (beim ersten Prüfen keine alten nachmelden).
    /// </summary>
    public static IReadOnlyList<ProxmoxBackupMonitor.Problem> Problems(PveBackupStatus? previous, PveBackupStatus current)
    {
        var problems = new List<ProxmoxBackupMonitor.Problem>();
        if (current.NotBackedUp is { Count: > 0 } missing)
        {
            var known = previous?.NotBackedUp?.Select(g => g.VmId).ToHashSet();
            var fresh = known is null ? missing : missing.Where(g => !known.Contains(g.VmId)).ToList();
            if (fresh.Count > 0)
            {
                problems.Add(new ProxmoxBackupMonitor.Problem(
                    L.T($"{fresh.Count} Gast/Gäste ohne Backup-Job", $"{fresh.Count} guest(s) without a backup job"),
                    string.Join("\n", fresh.Take(10).Select(g => $"{g.VmId} {g.Name}".Trim())) + (fresh.Count > 10 ? "\n…" : "")));
            }
        }

        if (current.FailedTasks is { Count: > 0 } failed && previous?.FailedTasks is { } before)
        {
            var known = before.Select(t => t.Upid).ToHashSet();
            var fresh = failed.Where(t => !known.Contains(t.Upid)).ToList();
            if (fresh.Count > 0)
            {
                problems.Add(new ProxmoxBackupMonitor.Problem(
                    L.T($"{fresh.Count} Backup-Lauf/-Läufe fehlgeschlagen", $"{fresh.Count} backup run(s) failed"),
                    string.Join("\n", fresh.Take(5).Select(t => $"{t.Node} {t.Guest}: {t.Status}".Trim())) + (fresh.Count > 5 ? "\n…" : "")));
            }
        }

        return problems;
    }

    // --- Auswertung (statisch, testbar) ---------------------------------------------------------------

    public static IReadOnlyList<PveBackupJob> ParseJobs(JsonNode? data) =>
        (data as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => Str(o, "id") is not null)
            .Select(o => new PveBackupJob(
                Str(o, "id")!,
                Num(o, "enabled") is not 0, // fehlt = an
                Str(o, "schedule") ?? Legacy(o),
                Str(o, "storage"),
                Num(o, "all") == 1,
                List(Str(o, "vmid")),
                Str(o, "pool"),
                List(Str(o, "exclude")),
                Str(o, "mode"),
                Str(o, "comment"),
                Num(o, "next-run") is { } next and > 0 ? DateTimeOffset.FromUnixTimeSeconds(next).UtcDateTime : null,
                Str(o, "node")))
            .OrderBy(j => j.Comment ?? j.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<PveGuest> ParseGuests(JsonNode? data) =>
        (data as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => Num(o, "vmid") is not null)
            .Select(o => new PveGuest((int)Num(o, "vmid")!.Value, Str(o, "name"), Str(o, "type")))
            .OrderBy(g => g.VmId)
            .ToList();

    /// <summary>vzdump-Läufe seit "since", die weder "OK" noch mit Warnungen beendet wurden.</summary>
    public static IReadOnlyList<PveBackupTask> ParseFailedTasks(JsonNode? data, DateTime since) =>
        (data as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => Str(o, "type") == "vzdump" && Str(o, "upid") is not null && Num(o, "starttime") is { } start
                        && DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime >= since)
            .Where(o => Str(o, "status") is { } s && s != "OK" && !s.StartsWith("WARNINGS", StringComparison.Ordinal))
            .Select(o => new PveBackupTask(Str(o, "upid")!, Str(o, "node"), Str(o, "id") is { Length: > 0 } id ? id : null,
                DateTimeOffset.FromUnixTimeSeconds(Num(o, "starttime")!.Value).UtcDateTime,
                Num(o, "endtime") is { } end and > 0 ? DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime : null, Str(o, "status")!))
            .OrderByDescending(t => t.Start)
            .Take(50)
            .ToList();

    /// <summary>Alte Jobs ohne "schedule": starttime + dow (z. B. "02:00", "mon,tue").</summary>
    private static string? Legacy(JsonObject o) =>
        Str(o, "starttime") is { } time ? (Str(o, "dow") is { } dow ? $"{dow} {time}" : time) : null;

    private static IReadOnlyList<string> List(string? value) =>
        (value ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Str(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')) : null;

    private static long? Num(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v
            ? v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<double>(out var d) ? (long)d
            : long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null
            : null;
}
