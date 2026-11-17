using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AIOPatcher.Server.Options;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Prüft einmal täglich, ob es eine neuere AIOPatcher-Version gibt (GitHub-Releases des konfigurierten Repositorys).
/// Aktualisiert wird nie automatisch: Das Dashboard zeigt nur den Hinweis mit den zwei Befehlen.
/// </summary>
public sealed class ReleaseCheck(IHttpClientFactory httpFactory, AIOPatcherOptions options, ILogger<ReleaseCheck> logger, FeatureFlags features) : BackgroundService
{
    public sealed record LatestRelease(string Version, string Url, DateTime? PublishedAt);

    public LatestRelease? Latest { get; private set; }

    public bool UpdateAvailable => Latest is { } l && AgentDownloads.IsNewer(l.Version, AgentDownloads.AgentVersion);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.UpdateCheckRepository))
        {
            return;
        }

        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            if (!features.IsOn(Feature.ReleaseCheck))
            {
                continue;
            }

            try
            {
                var http = httpFactory.CreateClient(Security.CveService.HttpClientName);
                var json = await http.GetFromJsonAsync<JsonObject>($"https://api.github.com/repos/{options.UpdateCheckRepository.Trim()}/releases/latest", stoppingToken);
                var tag = ((string?)json?["tag_name"])?.TrimStart('v');
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
                logger.LogWarning("Release check failed: {Message}", ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
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
