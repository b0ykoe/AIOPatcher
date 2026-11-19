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

    /// <summary>
    /// Schutz nur vor Doppelklicks auf "Jetzt prüfen": die automatische Prüfung zählt nicht mit, damit der Knopf immer
    /// wirklich nachsieht (GitHub ohne Anmeldung erlaubt 60 Abfragen pro Stunde, das reicht für Klicks dicke).
    /// </summary>
    public static readonly TimeSpan ManualCooldown = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime? _lastManual;

    /// <summary>
    /// Sofort bei GitHub nachsehen (Knopf in den Einstellungen). false = nicht abgefragt: kein Repository konfiguriert,
    /// Schalter "Versionsprüfung" aus oder der letzte Klick ist jünger als <see cref="ManualCooldown"/>.
    /// </summary>
    public async Task<bool> CheckNowAsync(CancellationToken ct)
    {
        if (NotPossibleReason() is not null || _lastManual is { } last && DateTime.UtcNow - last < ManualCooldown)
        {
            return false;
        }

        _lastManual = DateTime.UtcNow;
        await CheckAsync(ct);
        return true;
    }

    /// <summary>Warum gar nicht geprüft wird (für die Meldung am Knopf); null = Prüfung möglich.</summary>
    public string? NotPossibleReason() =>
        string.IsNullOrWhiteSpace(options.UpdateCheckRepository)
            ? L.T("Die Versionsprüfung ist ausgeschaltet (AIOPatcher__UpdateCheckRepository ist leer).", "The version check is off (AIOPatcher__UpdateCheckRepository is empty).")
            : !features.IsOn(Feature.ReleaseCheck)
                ? L.T("Der Schalter „Versionsprüfung“ ist aus (Einstellungen → Funktionen & Sicherheit).", "The \"Version check\" switch is off (Settings → Features & security).")
                : null;

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
            LastError = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests }
                ? L.T("GitHub hat die Abfrage abgelehnt (Limit: 60 Abfragen pro Stunde und IP-Adresse). Später erneut versuchen.",
                    "GitHub refused the request (limit: 60 requests per hour and IP address). Try again later.")
                : ex.Message;
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
