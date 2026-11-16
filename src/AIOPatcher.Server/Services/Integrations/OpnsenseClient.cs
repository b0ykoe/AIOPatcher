using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AIOPatcher.Server.Data;

namespace AIOPatcher.Server.Services.Integrations;

public sealed record OpnsenseFirmware(string? ProductVersion, string? LatestVersion, bool UpdatesAvailable, int PackageUpdates, bool RebootRequired, string? StatusMessage);

public sealed record ArpEntry(string Ip, string? Mac, string? Interface, string? Hostname);

/// <summary>
/// OPNsense-API mit Key/Secret (System → Zugang → Benutzer → API-Schlüssel). Benötigte Rechte:
/// "System: Firmware" (Status/Update) und "Diagnostics: ARP Table" (für IPAM).
/// </summary>
public sealed class OpnsenseClient(Integration integration, IntegrationHttp http)
{
    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, CancellationToken ct, JsonObject? body = null)
    {
        using var client = http.CreateClient(integration, new Uri((integration.BaseUrl ?? "").TrimEnd('/') + "/api/"), out var fingerprint);
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(http.Unprotect(integration))));
            if (body is not null || method == HttpMethod.Post)
            {
                request.Content = JsonContent.Create(body ?? new JsonObject());
            }

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new IntegrationException($"OPNsense: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            return await response.Content.ReadFromJsonAsync<JsonNode>(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw IntegrationHttp.Wrap(ex, fingerprint, integration);
        }
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        var firmware = await GetFirmwareAsync(check: false, ct);
        return $"OPNsense {firmware.ProductVersion}";
    }

    /// <param name="check">true: OPNsense sucht zuerst nach neuen Updates (dauert einige Sekunden).</param>
    public async Task<OpnsenseFirmware> GetFirmwareAsync(bool check, CancellationToken ct)
    {
        if (check)
        {
            await SendAsync(HttpMethod.Post, "core/firmware/check", ct);
            // Der Check läuft im Hintergrund; kurz warten, bis der Status aktualisiert ist.
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                var progress = await SendAsync(HttpMethod.Get, "core/firmware/upgradestatus", ct);
                if ((string?)progress?["status"] is "done" or "error")
                {
                    break;
                }
            }
        }

        var status = await SendAsync(HttpMethod.Get, "core/firmware/status", ct);
        var product = status?["product"];
        var updates = (status?["all_packages"] as JsonObject)?.Count ?? (status?["upgrade_packages"] as JsonArray)?.Count ?? 0;
        var latest = (string?)product?["product_latest"];
        var current = (string?)product?["product_version"];
        return new OpnsenseFirmware(
            current,
            latest,
            UpdatesAvailable: (string?)status?["status"] is "update" or "upgrade" || (latest is not null && current is not null && latest != current),
            PackageUpdates: updates,
            RebootRequired: (string?)status?["upgrade_needs_reboot"] == "1",
            StatusMessage: (string?)status?["status_msg"]);
    }

    /// <summary>Startet das Firmware-Update. OPNsense startet danach ggf. selbst neu.</summary>
    public async Task StartUpdateAsync(CancellationToken ct) => await SendAsync(HttpMethod.Post, "core/firmware/update", ct);

    public async Task<IReadOnlyList<ArpEntry>> GetArpAsync(CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Get, "diagnostics/interface/get_arp", ct);
        var rows = result as JsonArray ?? result?["rows"] as JsonArray ?? [];
        return rows.OfType<JsonObject>()
            .Where(r => (string?)r["ip"] is not null)
            .Select(r => new ArpEntry((string)r["ip"]!, (string?)r["mac"], (string?)r["intf_description"] ?? (string?)r["intf"], NullIfEmpty((string?)r["hostname"])))
            .ToList();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

public sealed class OpnsenseClientFactory(IntegrationHttp http)
{
    public OpnsenseClient For(Integration integration) => new(integration, http);
}
