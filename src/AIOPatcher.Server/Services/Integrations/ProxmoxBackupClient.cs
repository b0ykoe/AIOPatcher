using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIOPatcher.Server.Data;

namespace AIOPatcher.Server.Services.Integrations;

/// <summary>Datastore laut /status/datastore-usage und /admin/datastore/{store}/gc.</summary>
public sealed record PbsDatastore(string Store, long? Total, long? Used, long? Avail, string? Error, DateTime? EstimatedFull,
    string? GcState, DateTime? GcLastRun, DateTime? GcNextRun);

/// <summary>Task aus /nodes/localhost/tasks. Status: "OK", "WARNINGS: n", "unknown" oder der Fehlertext; null = läuft.</summary>
public sealed record PbsTask(string Upid, string Type, string? WorkerId, DateTime Start, DateTime? End, string? Status, string? User);

/// <summary>Backup-Gruppe (VM, CT oder Host) mit jüngstem Snapshot und dessen Prüfstatus.</summary>
/// <param name="VerifyState">"ok", "failed" oder null (nie geprüft).</param>
public sealed record PbsGroup(string Store, string Namespace, string Type, string Id, DateTime? LastBackup, int Count, string? VerifyState, string? Owner, string? Comment);

/// <summary>Zustand eines Proxmox Backup Servers (gespeichert in Integration.StatusJson).</summary>
/// <param name="FailedTasks">Fehlgeschlagene Tasks der letzten 7 Tage, neueste zuerst (höchstens 50).</param>
/// <param name="Tasks">Anzahl beendeter Tasks in diesem Zeitraum.</param>
/// <param name="Warnings">Davon mit Warnungen.</param>
public sealed record PbsStatus(string? Version, IReadOnlyList<PbsDatastore> Datastores, IReadOnlyList<PbsTask> FailedTasks, int Tasks, int Warnings,
    IReadOnlyList<PbsGroup> Groups, DateTime CheckedAt);

/// <summary>
/// Proxmox-Backup-Server-API (Port 8007) mit API-Token "benutzer@realm!token:secret". Nur lesen. Rechte: Rolle "Audit" auf "/"
/// (Datastore.Audit für Datastores, Gruppen und Snapshots, Sys.Audit auf /system/tasks für alle Tasks).
/// </summary>
public sealed partial class ProxmoxBackupClient(Integration integration, IntegrationHttp http)
{
    public static readonly TimeSpan TaskWindow = TimeSpan.FromDays(7);
    private const int MaxNamespaces = 50;
    private const int MaxGroups = 2000;

    /// <summary>Token für den Header: "=" zwischen Token-ID und Secret (wie bei Proxmox VE) wird zu ":" (PBS).</summary>
    public static string NormalizeToken(string token)
    {
        token = token.Trim();
        var bang = token.IndexOf('!');
        if (bang < 0 || token.IndexOf(':', bang) >= 0)
        {
            return token;
        }

        var equals = token.IndexOf('=', bang);
        return equals < 0 ? token : token[..equals] + ":" + token[(equals + 1)..];
    }

    /// <summary>null = Format passt ("benutzer@realm!token-id:secret", "=" statt ":" wird akzeptiert).</summary>
    public static string? TokenProblem(string token)
    {
        if (TokenPattern().IsMatch(NormalizeToken(token)))
        {
            return null;
        }

        return Guid.TryParse(token.Trim(), out _)
            ? L.T("Das ist nur das Secret. PBS braucht Benutzer, Token-ID und Secret: root@pam!aiopatcher:" + token.Trim(),
                "That is only the secret. PBS needs user, token ID and secret: root@pam!aiopatcher:" + token.Trim())
            : L.T("Format: benutzer@realm!token-id:secret, z. B. root@pam!aiopatcher:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
                "Format: user@realm!token-id:secret, e.g. root@pam!aiopatcher:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx");
    }

    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        using var client = http.CreateClient(integration, new Uri((integration.BaseUrl ?? "").TrimEnd('/') + "/api2/json/"), out var fingerprint);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Authorization", $"PBSAPIToken={NormalizeToken(http.Unprotect(integration))}");
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(response.ReasonPhrase, null, response.StatusCode);
            }

            return (await response.Content.ReadFromJsonAsync<JsonNode>(ct))?["data"];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw IntegrationHttp.Wrap(ex, fingerprint, integration);
        }
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        var version = Version(await GetAsync("version", ct));
        var stores = ParseUsage(await GetAsync("status/datastore-usage", ct));
        return L.T($"Proxmox Backup Server {version}, {stores.Count} Datastore(s)", $"Proxmox Backup Server {version}, {stores.Count} datastore(s)");
    }

    public async Task<PbsStatus> GetStatusAsync(DateTime now, CancellationToken ct)
    {
        var version = Version(await GetAsync("version", ct));
        var stores = ParseUsage(await GetAsync("status/datastore-usage", ct)).ToList();

        // Garbage Collection je Datastore (Fehler einzelner Stores brechen nicht alles ab)
        for (var i = 0; i < stores.Count; i++)
        {
            try
            {
                stores[i] = WithGc(stores[i], await GetAsync($"admin/datastore/{Uri.EscapeDataString(stores[i].Store)}/gc", ct));
            }
            catch (IntegrationException)
            {
                // ohne GC-Angaben weiter
            }
        }

        var since = new DateTimeOffset(now - TaskWindow).ToUnixTimeSeconds();
        var tasks = ParseTasks(await GetAsync($"nodes/localhost/tasks?since={since}&limit=1000", ct));

        var groups = new List<PbsGroup>();
        foreach (var store in stores.Where(s => s.Error is null))
        {
            var escaped = Uri.EscapeDataString(store.Store);
            IReadOnlyList<string> namespaces;
            try
            {
                namespaces = ParseNamespaces(await GetAsync($"admin/datastore/{escaped}/namespace", ct));
            }
            catch (IntegrationException)
            {
                namespaces = [""];
            }

            foreach (var ns in namespaces.Take(MaxNamespaces))
            {
                var query = ns.Length == 0 ? "" : $"?ns={Uri.EscapeDataString(ns)}";
                try
                {
                    groups.AddRange(ParseGroups(store.Store, ns, await GetAsync($"admin/datastore/{escaped}/groups{query}", ct),
                        await GetAsync($"admin/datastore/{escaped}/snapshots{query}", ct)));
                }
                catch (IntegrationException)
                {
                    // Namespace ohne Rechte: überspringen
                }

                if (groups.Count >= MaxGroups)
                {
                    break;
                }
            }
        }

        var finished = tasks.Where(t => t.Status is not null).ToList();
        return new PbsStatus(version, stores, finished.Where(IsFailed).OrderByDescending(t => t.Start).Take(50).ToList(), finished.Count,
            finished.Count(t => t.Status!.StartsWith("WARNINGS", StringComparison.Ordinal)), groups.Take(MaxGroups).ToList(), now);
    }

    // --- Auswertung (statisch, testbar) ---------------------------------------------------------------

    /// <summary>Fehlgeschlagen: beendet und weder "OK" noch "WARNINGS: n" noch "unknown".</summary>
    public static bool IsFailed(PbsTask task) =>
        task.Status is { } s && s != "OK" && !s.StartsWith("WARNINGS", StringComparison.Ordinal) && s != "unknown";

    public static string? Version(JsonNode? data)
    {
        var version = Str(data, "version");
        var release = Str(data, "release");
        return version is null ? null : release is null ? version : $"{version}-{release}";
    }

    public static IReadOnlyList<PbsDatastore> ParseUsage(JsonNode? data) =>
        (data as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => Str(o, "store") is not null)
            .Select(o => new PbsDatastore(Str(o, "store")!, Long(o, "total"), Long(o, "used"), Long(o, "avail"), Str(o, "error"),
                Epoch(o, "estimated-full-date"), null, null, null))
            .OrderBy(d => d.Store, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static PbsDatastore WithGc(PbsDatastore store, JsonNode? gc) =>
        gc is null ? store : store with { GcState = Str(gc, "last-run-state"), GcLastRun = Epoch(gc, "last-run-endtime"), GcNextRun = Epoch(gc, "next-run") };

    public static IReadOnlyList<PbsTask> ParseTasks(JsonNode? data) =>
        (data as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => Str(o, "upid") is not null && Epoch(o, "starttime") is not null)
            .Select(o => new PbsTask(Str(o, "upid")!, Str(o, "worker_type") ?? "?", Str(o, "worker_id"), Epoch(o, "starttime")!.Value,
                Epoch(o, "endtime"), Str(o, "status"), Str(o, "user")))
            .ToList();

    public static IReadOnlyList<string> ParseNamespaces(JsonNode? data)
    {
        var list = (data as JsonArray ?? []).OfType<JsonObject>().Select(o => Str(o, "ns") ?? "").Distinct().ToList();
        if (!list.Contains(""))
        {
            list.Insert(0, "");
        }

        return list;
    }

    /// <summary>Gruppen mit jüngstem Snapshot und dessen Prüfstatus (aus der Snapshot-Liste).</summary>
    public static IReadOnlyList<PbsGroup> ParseGroups(string store, string ns, JsonNode? groups, JsonNode? snapshots)
    {
        var latest = (snapshots as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => (Key: $"{Str(o, "backup-type")}/{Str(o, "backup-id")}", Time: Epoch(o, "backup-time"), Verify: Str(o["verification"], "state")))
            .Where(s => s.Time is not null)
            .GroupBy(s => s.Key)
            .ToDictionary(g => g.Key, g => g.MaxBy(s => s.Time)!.Verify);

        return (groups as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => Str(o, "backup-type") is not null && Str(o, "backup-id") is not null)
            .Select(o =>
            {
                var type = Str(o, "backup-type")!;
                var id = Str(o, "backup-id")!;
                return new PbsGroup(store, ns, type, id, Epoch(o, "last-backup"), (int)(Long(o, "backup-count") ?? 0),
                    latest.GetValueOrDefault($"{type}/{id}"), Str(o, "owner"), Str(o, "comment"));
            })
            .OrderBy(g => g.Type).ThenBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? Str(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')) : null;

    private static long? Long(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v
            ? v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<double>(out var d) ? (long)d
            : long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null
            : null;

    private static DateTime? Epoch(JsonNode? node, string name) =>
        Long(node, name) is { } seconds and > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;

    [GeneratedRegex(@"^[^@\s!=:]+@[^!\s=:]+![^=:\s]+:\S+$")]
    private static partial Regex TokenPattern();
}

public sealed class ProxmoxBackupClientFactory(IntegrationHttp http)
{
    public ProxmoxBackupClient For(Integration integration) => new(integration, http);
}
