using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AIOPatcher.Server.Options;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Prüft regelmäßig, ob es eine neuere AIOPatcher-Version gibt (GitHub-Releases des konfigurierten Repositorys).
/// Standard alle 15 Minuten, einstellbar (<see cref="Timings.ReleaseCheck"/>, nie öfter: GitHub erlaubt ohne Anmeldung 60
/// Abfragen pro Stunde). Aktualisiert wird nie automatisch: Das Dashboard zeigt den Hinweis mit Befehl bzw. dem Knopf
/// "Jetzt aktualisieren".
/// </summary>
public sealed class ReleaseCheck(IHttpClientFactory httpFactory, AIOPatcherOptions options, ILogger<ReleaseCheck> logger, FeatureFlags features, ScanSchedule? schedule = null) : BackgroundService
{
    public sealed record LatestRelease(string Version, string Url, DateTime? PublishedAt);

    public LatestRelease? Latest { get; private set; }

    public bool UpdateAvailable => Latest is { } l && AgentDownloads.IsNewer(l.Version, AgentDownloads.AgentVersion);

    /// <summary>Zeitpunkt der letzten Abfrage (auch fehlgeschlagen) und ihr Fehler, für "Jetzt prüfen" in den Einstellungen.</summary>
    public DateTime? CheckedAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>"Jetzt prüfen" höchstens einmal pro Minute (GitHub ohne Anmeldung: 60 Abfragen pro Stunde).</summary>
    public static readonly TimeSpan ManualCooldown = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Sofort bei GitHub nachsehen (Knopf in den Einstellungen). false = nicht abgefragt: kein Repository konfiguriert,
    /// Schalter "Versionsprüfung" aus oder die letzte Abfrage ist jünger als <see cref="ManualCooldown"/>.
    /// </summary>
    public async Task<bool> CheckNowAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.UpdateCheckRepository) || !features.IsOn(Feature.ReleaseCheck)
            || CheckedAt is { } last && DateTime.UtcNow - last < ManualCooldown)
        {
            return false;
        }

        await CheckAsync(ct);
        return true;
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var http = httpFactory.CreateClient(Security.CveService.HttpClientName);
            var repository = options.UpdateCheckRepository?.Trim() ?? "";
            var json = await http.GetFromJsonAsync<JsonObject>($"https://api.github.com/repos/{repository}/releases/latest", ct);
            var tag = ((string?)json?["tag_name"])?.TrimStart('v');
            LastError = null;
            if (tag is not null)
            {
                Latest = new LatestRelease(tag, (string?)json?["html_url"] ?? "", (DateTime?)json?["published_at"]);
                if (UpdateAvailable)
                {
                    logger.LogInformation("AIOPatcher {Version} is available (running {Current})", tag, AgentDownloads.AgentVersion);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            LastError = ex.Message;
            logger.LogWarning("Release check failed: {Message}", ex.Message);
        }
        finally
        {
            CheckedAt = DateTime.UtcNow;
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.UpdateCheckRepository))
        {
            return;
        }

        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        using var timer = new PeriodicTimer(Timings.Default(Timings.ReleaseCheck));
        do
        {
            if (!features.IsOn(Feature.ReleaseCheck))
            {
                continue;
            }

            await CheckAsync(stoppingToken);
        }
        while (Timings.Retime(timer, schedule, Timings.ReleaseCheck) && await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>"dotnet AIOPatcher.Server.dll healthcheck": für den Docker-HEALTHCHECK, ohne curl im Image.</summary>
public static class HealthCheckCommand
{
    public static bool IsInvocation(string[] args) => args.Length > 0 && args[0] == "healthcheck";

    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("AIOPatcher__DashboardPort") is { Length: > 0 } p ? p : "9443";
        using var handler = new HttpClientHandler
        {
            // Nur localhost: das eigene, selbst ausgestellte Zertifikat ist hier irrelevant.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync($"https://127.0.0.1:{port}/healthz");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
