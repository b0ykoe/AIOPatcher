using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AIOPatcher.Server.Data;

namespace AIOPatcher.Server.Services.Integrations;

public sealed record MachineInfo(string ExternalId, string Name, string? Status, IReadOnlyList<string> Addresses);

public enum ProviderTaskState
{
    Running,
    Succeeded,
    Failed,
}

public sealed record ProviderTaskStatus(ProviderTaskState State, string? Error = null);

/// <summary>Jüngstes Backup. VerifyState: PBS-Prüfung ("ok", "failed") oder null, wenn nie geprüft bzw. unbekannt.</summary>
public sealed record BackupInfo(DateTime CreatedAt, string? Location, long? SizeBytes, string? VerifyState);

/// <summary>Was Abläufe von einem Hypervisor bzw. Cloud-Anbieter brauchen.</summary>
public interface IVirtualizationProvider
{
    /// <summary>Verbindung prüfen; liefert eine kurze Beschreibung (Version, Anzahl Maschinen).</summary>
    Task<string> TestAsync(CancellationToken ct);

    Task<IReadOnlyList<MachineInfo>> ListMachinesAsync(CancellationToken ct);

    /// <summary>Startet einen Snapshot und gibt eine Referenz auf die laufende Aufgabe zurück.</summary>
    Task<string> StartSnapshotAsync(string externalId, string name, string description, CancellationToken ct);

    Task<ProviderTaskStatus> GetTaskAsync(string externalId, string taskRef, CancellationToken ct);

    /// <summary>Jüngstes Backup (Proxmox: vzdump auf PBS/Storage; Hetzner: Server-Backup). null = keins gefunden.</summary>
    Task<BackupInfo?> LatestBackupAsync(string externalId, CancellationToken ct);

    /// <summary>Löscht einen bestimmten Snapshot. Gibt eine Aufgaben-Referenz zurück (oder null, wenn sofort erledigt).</summary>
    Task<string?> DeleteSnapshotAsync(string externalId, string name, CancellationToken ct);

    /// <summary>Löscht alte AIOPatcher-Snapshots über <paramref name="keep"/> hinaus. Gibt die Anzahl gelöschter zurück.</summary>
    Task<int> PruneSnapshotsAsync(string externalId, int keep, CancellationToken ct);
}

public static class SnapshotNames
{
    public const string Prefix = "aiop_";

    /// <summary>Proxmox erlaubt nur Buchstaben, Ziffern, _ und -, erstes Zeichen ein Buchstabe, max. 40 Zeichen.</summary>
    public static string For(DateTime utc) => $"{Prefix}{utc:yyyyMMdd_HHmmss}";
}

// --- Proxmox VE ------------------------------------------------------------------------------------

/// <summary>
/// Proxmox-VE-API mit API-Token. Benötigte Rechte (Rolle am Pfad /vms bzw. / ):
/// VM.Audit, VM.Snapshot, Datastore.Audit, Sys.Audit. Externe ID: "node/qemu/101" oder "node/lxc/105".
/// </summary>
public sealed class ProxmoxProvider(Integration integration, IntegrationHttp http) : IVirtualizationProvider
{
    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken ct)
    {
        var baseUrl = new Uri((integration.BaseUrl ?? "").TrimEnd('/') + "/api2/json/");
        using var client = http.CreateClient(integration, baseUrl, out var fingerprint);
        try
        {
            using var request = new HttpRequestMessage(method, path.TrimStart('/'));
            request.Headers.TryAddWithoutValidation("Authorization", $"PVEAPIToken={http.Unprotect(integration)}");
            if (form is not null)
            {
                request.Content = new FormUrlEncodedContent(form);
            }

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Proxmox liefert die Ursache im Status-Text, z. B. "VM is locked (snapshot)".
                throw new IntegrationException($"Proxmox: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            return json?["data"];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw IntegrationHttp.Wrap(ex, fingerprint, integration);
        }
    }

    private static (string Node, string Type, string VmId) Parse(string externalId)
    {
        var parts = externalId.Split('/');
        if (parts.Length != 3 || parts[1] is not ("qemu" or "lxc") || !int.TryParse(parts[2], out _))
        {
            throw new IntegrationException(L.T($"Ungültige Proxmox-Zuordnung „{externalId}“.", $"Invalid Proxmox mapping \"{externalId}\"."));
        }

        return (parts[0], parts[1], parts[2]);
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        var version = await SendAsync(HttpMethod.Get, "version", null, ct);
        var machines = await ListMachinesAsync(ct);
        return $"Proxmox VE {version?["version"]} · {machines.Count} VMs/CTs";
    }

    public async Task<IReadOnlyList<MachineInfo>> ListMachinesAsync(CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Get, "cluster/resources?type=vm", null, ct) as JsonArray ?? [];
        return data
            .OfType<JsonObject>()
            .Where(v => v["template"]?.GetValue<int>() != 1)
            .Select(v => new MachineInfo(
                $"{v["node"]}/{v["type"]}/{v["vmid"]}",
                (string?)v["name"] ?? $"{v["vmid"]}",
                (string?)v["status"],
                []))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> StartSnapshotAsync(string externalId, string name, string description, CancellationToken ct)
    {
        var (node, type, vmid) = Parse(externalId);
        var upid = await SendAsync(HttpMethod.Post, $"nodes/{node}/{type}/{vmid}/snapshot", [new("snapname", name), new("description", description)], ct);
        return (string?)upid ?? throw new IntegrationException(L.T("Proxmox hat keine Aufgaben-ID geliefert.", "Proxmox returned no task ID."));
    }

    public async Task<ProviderTaskStatus> GetTaskAsync(string externalId, string taskRef, CancellationToken ct)
    {
        var (node, _, _) = Parse(externalId);
        var status = await SendAsync(HttpMethod.Get, $"nodes/{node}/tasks/{Uri.EscapeDataString(taskRef)}/status", null, ct);
        if ((string?)status?["status"] == "running")
        {
            return new ProviderTaskStatus(ProviderTaskState.Running);
        }

        var exit = (string?)status?["exitstatus"];
        return exit == "OK" ? new ProviderTaskStatus(ProviderTaskState.Succeeded) : new ProviderTaskStatus(ProviderTaskState.Failed, $"Proxmox: {exit}");
    }

    public async Task<BackupInfo?> LatestBackupAsync(string externalId, CancellationToken ct)
    {
        var (node, _, vmid) = Parse(externalId);
        var storages = await SendAsync(HttpMethod.Get, $"nodes/{node}/storage?content=backup&enabled=1", null, ct) as JsonArray ?? [];
        BackupInfo? newest = null;
        foreach (var storage in storages.OfType<JsonObject>())
        {
            var name = (string?)storage["storage"];
            if (name is null || storage["active"]?.GetValue<int>() == 0)
            {
                continue;
            }

            try
            {
                var content = await SendAsync(HttpMethod.Get, $"nodes/{node}/storage/{Uri.EscapeDataString(name)}/content?content=backup&vmid={vmid}", null, ct) as JsonArray ?? [];
                foreach (var item in content.OfType<JsonObject>())
                {
                    if (item["ctime"]?.GetValue<long>() is { } ctime)
                    {
                        var at = DateTimeOffset.FromUnixTimeSeconds(ctime).UtcDateTime;
                        if (newest is null || at > newest.CreatedAt)
                        {
                            newest = new BackupInfo(at, (string?)item["volid"], item["size"]?.GetValue<long>(), (string?)item["verification"]?["state"]);
                        }
                    }
                }
            }
            catch (IntegrationException)
            {
                // Ein nicht erreichbarer Storage soll die anderen nicht verdecken.
            }
        }

        return newest;
    }

    public async Task<string?> DeleteSnapshotAsync(string externalId, string name, CancellationToken ct)
    {
        var (node, type, vmid) = Parse(externalId);
        return (string?)await SendAsync(HttpMethod.Delete, $"nodes/{node}/{type}/{vmid}/snapshot/{Uri.EscapeDataString(name)}", null, ct);
    }

    public async Task<int> PruneSnapshotsAsync(string externalId, int keep, CancellationToken ct)
    {
        var (node, type, vmid) = Parse(externalId);
        var snapshots = await SendAsync(HttpMethod.Get, $"nodes/{node}/{type}/{vmid}/snapshot", null, ct) as JsonArray ?? [];
        var old = snapshots.OfType<JsonObject>()
            .Select(s => (Name: (string?)s["name"], Time: s["snaptime"]?.GetValue<long>() ?? 0))
            .Where(s => s.Name?.StartsWith(SnapshotNames.Prefix, StringComparison.Ordinal) == true)
            .OrderByDescending(s => s.Time)
            .Skip(Math.Max(keep, 1))
            .ToList();

        // Proxmox sperrt die VM während des Löschens: pro Durchlauf nur den ältesten entfernen.
        var oldest = old.LastOrDefault();
        if (oldest.Name is null)
        {
            return 0;
        }

        await SendAsync(HttpMethod.Delete, $"nodes/{node}/{type}/{vmid}/snapshot/{Uri.EscapeDataString(oldest.Name)}", null, ct);
        return 1;
    }
}

// --- Hetzner Cloud ---------------------------------------------------------------------------------

/// <summary>Hetzner-Cloud-API (Projekt-Token mit Lese-/Schreibrecht). Externe ID: Server-ID.</summary>
public sealed class HetznerCloudProvider(Integration integration, IntegrationHttp http) : IVirtualizationProvider
{
    private static readonly Uri BaseUrl = new("https://api.hetzner.cloud/v1/");
    private const string Label = "aiopatcher";

    private async Task<JsonObject?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var client = http.CreateClient(integration, string.IsNullOrWhiteSpace(integration.BaseUrl) ? BaseUrl : new Uri(integration.BaseUrl.TrimEnd('/') + "/"), out var fingerprint);
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", http.Unprotect(integration));
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await client.SendAsync(request, ct);
            var json = response.Content.Headers.ContentLength == 0 ? null : await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            if (!response.IsSuccessStatusCode)
            {
                var message = (string?)json?["error"]?["message"] ?? response.ReasonPhrase;
                throw new IntegrationException($"Hetzner: {(int)response.StatusCode} {message}");
            }

            return json;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw IntegrationHttp.Wrap(ex, fingerprint, integration);
        }
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        var machines = await ListMachinesAsync(ct);
        return $"Hetzner Cloud · {machines.Count} {L.T("Server", "servers")}";
    }

    public async Task<IReadOnlyList<MachineInfo>> ListMachinesAsync(CancellationToken ct)
    {
        var result = new List<MachineInfo>();
        for (var page = 1; page <= 20; page++)
        {
            var json = await SendAsync(HttpMethod.Get, $"servers?page={page}&per_page=50", null, ct);
            foreach (var server in (json?["servers"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var addresses = new List<string>();
                if ((string?)server["public_net"]?["ipv4"]?["ip"] is { } v4)
                {
                    addresses.Add(v4);
                }

                foreach (var priv in (server["private_net"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if ((string?)priv["ip"] is { } ip)
                    {
                        addresses.Add(ip);
                    }
                }

                result.Add(new MachineInfo(server["id"]!.ToString(), (string?)server["name"] ?? "?", (string?)server["status"], addresses));
            }

            if (json?["meta"]?["pagination"]?["next_page"] is null)
            {
                break;
            }
        }

        return result.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<string> StartSnapshotAsync(string externalId, string name, string description, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, $"servers/{ServerId(externalId)}/actions/create_image", new JsonObject
        {
            ["type"] = "snapshot",
            ["description"] = $"{name} {description}",
            ["labels"] = new JsonObject { [Label] = "true" },
        }, ct);
        return json?["action"]?["id"]?.ToString() ?? throw new IntegrationException(L.T("Hetzner hat keine Aktions-ID geliefert.", "Hetzner returned no action ID."));
    }

    public async Task<ProviderTaskStatus> GetTaskAsync(string externalId, string taskRef, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"actions/{Uri.EscapeDataString(taskRef)}", null, ct);
        return (string?)json?["action"]?["status"] switch
        {
            "success" => new ProviderTaskStatus(ProviderTaskState.Succeeded),
            "error" => new ProviderTaskStatus(ProviderTaskState.Failed, $"Hetzner: {json?["action"]?["error"]?["message"]}"),
            _ => new ProviderTaskStatus(ProviderTaskState.Running),
        };
    }

    public async Task<BackupInfo?> LatestBackupAsync(string externalId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"images?type=backup&bound_to={ServerId(externalId)}&sort=created:desc&per_page=1", null, ct);
        var image = (json?["images"] as JsonArray)?.FirstOrDefault();
        var created = (string?)image?["created"];
        if (created is null)
        {
            return null;
        }

        var sizeGb = image?["image_size"]?.GetValue<double?>();
        return new BackupInfo(
            DateTime.Parse(created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            (string?)image?["description"],
            sizeGb is { } gb ? (long)(gb * 1024 * 1024 * 1024) : null,
            (string?)image?["status"] == "available" ? "ok" : (string?)image?["status"]);
    }

    public async Task<string?> DeleteSnapshotAsync(string externalId, string name, CancellationToken ct)
    {
        var serverId = ServerId(externalId);
        var json = await SendAsync(HttpMethod.Get, $"images?type=snapshot&label_selector={Label}%3Dtrue&per_page=50", null, ct);
        foreach (var image in (json?["images"] as JsonArray ?? []).OfType<JsonObject>()
                     .Where(i => i["created_from"]?["id"]?.ToString() == serverId && ((string?)i["description"])?.StartsWith(name, StringComparison.Ordinal) == true))
        {
            await SendAsync(HttpMethod.Delete, $"images/{image["id"]}", null, ct);
        }

        return null;
    }

    public async Task<int> PruneSnapshotsAsync(string externalId, int keep, CancellationToken ct)
    {
        var serverId = ServerId(externalId);
        var json = await SendAsync(HttpMethod.Get, $"images?type=snapshot&label_selector={Label}%3Dtrue&sort=created:desc&per_page=50", null, ct);
        var old = (json?["images"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(i => i["created_from"]?["id"]?.ToString() == serverId)
            .Skip(Math.Max(keep, 1))
            .ToList();

        foreach (var image in old)
        {
            await SendAsync(HttpMethod.Delete, $"images/{image["id"]}", null, ct);
        }

        return old.Count;
    }

    private static string ServerId(string externalId) =>
        long.TryParse(externalId, out _) ? externalId : throw new IntegrationException(L.T($"Ungültige Hetzner-Server-ID „{externalId}“.", $"Invalid Hetzner server ID \"{externalId}\"."));
}

public sealed class VirtualizationProviderFactory(IntegrationHttp http)
{
    public IVirtualizationProvider? For(Integration integration) => integration.Type switch
    {
        IntegrationType.Proxmox => new ProxmoxProvider(integration, http),
        IntegrationType.HetznerCloud => new HetznerCloudProvider(integration, http),
        _ => null,
    };
}
