using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Security;

/// <summary>Image-Referenz in Registry, Repository und Tag zerlegt ("nginx" → docker.io/library/nginx:latest).</summary>
public sealed partial record ImageReference(string Registry, string Repository, string Tag)
{
    public string Normalized => $"{Registry}/{Repository}:{Tag}";

    /// <summary>API-Host der Registry (Docker Hub hat einen eigenen).</summary>
    public string ApiHost => Registry == "docker.io" ? "registry-1.docker.io" : Registry;

    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$")]
    private static partial Regex RepositoryPattern();

    /// <summary>null bei Digest-gepinnten Images ("app@sha256:…"): die haben kein "neuer" im Sinne eines Tags.</summary>
    public static ImageReference? Parse(string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Contains('@'))
        {
            return null;
        }

        var name = image.Trim();
        var registry = "docker.io";
        var slash = name.IndexOf('/');
        if (slash > 0 && (name[..slash].Contains('.') || name[..slash].Contains(':') || name[..slash] == "localhost"))
        {
            registry = name[..slash];
            name = name[(slash + 1)..];
        }

        var tag = "latest";
        var colon = name.LastIndexOf(':');
        if (colon > 0)
        {
            tag = name[(colon + 1)..];
            name = name[..colon];
        }

        if (registry == "docker.io" && !name.Contains('/'))
        {
            name = $"library/{name}";
        }

        return RepositoryPattern().IsMatch(name) && tag.Length is > 0 and <= 128 ? new ImageReference(registry.ToLowerInvariant(), name, tag) : null;
    }

    /// <summary>
    /// Lokale Digests aus RepoDigests ("nginx@sha256:…", "ghcr.io/org/app@sha256:…") passend zu diesem Repository. Alle, nicht
    /// nur der erste: Wurde dasselbe Image über mehrere Tags gezogen, hat es je Tag einen Digest; aktuell ist es, wenn einer
    /// davon dem Digest des Tags in der Registry entspricht. null = keiner (lokal gebaut).
    /// </summary>
    public string? LocalDigest(IReadOnlyList<string>? repoDigests)
    {
        var digests = new List<string>();
        foreach (var entry in repoDigests ?? [])
        {
            var at = entry.IndexOf('@');
            if (at <= 0)
            {
                continue;
            }

            var repo = Parse(entry[..at]);
            var digest = entry[(at + 1)..];
            if (repo is not null && repo.Registry == Registry && repo.Repository == Repository && digest.Length <= 80 && !digests.Contains(digest))
            {
                digests.Add(digest);
            }
        }

        return digests.Count == 0 ? null : string.Join(' ', digests.Take(5));
    }
}

/// <summary>
/// Prüft regelmäßig, ob es zu den Image-Tags laufender Container einen neueren Digest gibt (HEAD auf das Manifest,
/// anonymer Token wie bei "docker pull"). Ergebnis wird pro Image zwischengespeichert, nicht pro Host.
/// </summary>
public sealed class ContainerUpdateService(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpFactory,
    AIOPatcherOptions options,
    Notifier notifier,
    ILogger<ContainerUpdateService> logger,
    FeatureFlags features,
    ScanSchedule schedule) : BackgroundService
{
    /// <summary>So oft wird nachgesehen, welche Images fällig sind; wie oft je Image abgefragt wird, sagt <see cref="ScanSchedule"/>.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>Spielraum, damit ein Image mit 1-Stunden-Intervall nicht wegen Sekunden erst eine Runde später dran ist.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

    private static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = options;
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                if (!features.IsOn(Feature.ContainerChecks))
                {
                    continue;
                }

                using var scope = scopes.CreateScope();
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Container update check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>"Neu prüfen" für einen Host: dessen Images sofort bei der Registry abfragen (statt erst im Intervall).</summary>
    public async Task RecheckHostAsync(AppDbContext db, Guid hostId, DateTime now, CancellationToken ct)
    {
        var references = await db.Containers.Where(c => c.HostId == hostId && c.Reference != null).Select(c => c.Reference!).Distinct().ToListAsync(ct);
        await CheckAsync(db, references.ToDictionary(r => r, _ => TimeSpan.FromMinutes(1)), now, ct);
    }

    public async Task RunOnceAsync(AppDbContext db, DateTime now, CancellationToken ct) =>
        await CheckAsync(db, await DueIntervalsAsync(db, schedule, ct), now, ct);

    /// <summary>Je Image das kürzeste Intervall der Hosts, auf denen es läuft (Standard oder abweichender Wert des Hosts).</summary>
    public static async Task<Dictionary<string, TimeSpan>> DueIntervalsAsync(AppDbContext db, ScanSchedule schedule, CancellationToken ct)
    {
        var rows = await db.Containers.Where(c => c.Reference != null)
            .Select(c => new { Reference = c.Reference!, c.Host!.ImageCheckHours })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.Reference).ToDictionary(g => g.Key, g => g.Min(r => schedule.ImageCheckInterval(r.ImageCheckHours)));
    }

    private async Task CheckAsync(AppDbContext db, Dictionary<string, TimeSpan> recheckAfter, DateTime now, CancellationToken ct)
    {
        var references = recheckAfter.Keys.ToList();
        var checks = await db.ImageChecks.Where(i => references.Contains(i.Reference)).ToDictionaryAsync(i => i.Reference, ct);

        foreach (var reference in references)
        {
            if (checks.TryGetValue(reference, out var check) && now - check.CheckedAt < recheckAfter[reference] - Slack)
            {
                continue;
            }

            if (check is null)
            {
                db.ImageChecks.Add(checks[reference] = check = new ImageCheck { Reference = reference });
            }

            var parsed = ImageReference.Parse(reference)!;
            try
            {
                check.RemoteDigest = await RemoteDigestAsync(parsed, ct);
                check.Error = null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                check.Error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            }

            check.CheckedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, checks.Values, ct);
    }

    private async Task NotifyAsync(AppDbContext db, IEnumerable<ImageCheck> checks, CancellationToken ct)
    {
        foreach (var check in checks.Where(c => c.RemoteDigest is not null && c.RemoteDigest != c.NotifiedDigest))
        {
            var outdated = await db.Containers
                .Include(c => c.Host)
                .Where(c => c.Reference == check.Reference && c.LocalDigest != null && !c.LocalDigest.Contains(check.RemoteDigest!))
                .ToListAsync(ct);
            if (outdated.Count == 0)
            {
                continue;
            }

            foreach (var site in outdated.GroupBy(c => c.Host!.SiteId))
            {
                var where = string.Join(", ", site.Select(c => $"{c.Host!.Name}/{c.Name}").Take(10));
                notifier.Publish(new Notification(NotificationEvents.ContainerUpdates,
                    L.T($"Neues Image: {check.Reference}", $"New image: {check.Reference}"),
                    L.T($"Betrifft: {where}", $"Affects: {where}"),
                    NotificationSeverity.Info, site.Key, site.Count() == 1 ? site.First().HostId : null,
                    site.Count() == 1 ? $"/hosts/{site.First().HostId}" : "/hosts"));
            }

            check.NotifiedDigest = check.RemoteDigest;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> RemoteDigestAsync(ImageReference image, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(CveService.HttpClientName);
        var url = $"https://{image.ApiHost}/v2/{image.Repository}/manifests/{Uri.EscapeDataString(image.Tag)}";

        using var first = await SendHeadAsync(http, url, null, ct);
        if (first.StatusCode == HttpStatusCode.Unauthorized && first.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme == "Bearer") is { } challenge)
        {
            var token = await AnonymousTokenAsync(http, challenge, image, ct);
            using var second = await SendHeadAsync(http, url, token, ct);
            return Digest(second);
        }

        return Digest(first);

        static string? Digest(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException("Tag not found in registry");
            }

            response.EnsureSuccessStatusCode();
            return response.Headers.TryGetValues("Docker-Content-Digest", out var values) ? values.FirstOrDefault() : null;
        }
    }

    private static async Task<HttpResponseMessage> SendHeadAsync(HttpClient http, string url, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        foreach (var type in ManifestTypes)
        {
            request.Headers.Accept.ParseAdd(type);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await http.SendAsync(request, ct);
    }

    /// <summary>WWW-Authenticate: Bearer realm="https://auth.docker.io/token",service="registry.docker.io",scope="repository:library/nginx:pull"</summary>
    private static async Task<string> AnonymousTokenAsync(HttpClient http, AuthenticationHeaderValue challenge, ImageReference image, CancellationToken ct)
    {
        var parameters = (challenge.Parameter ?? "").Split(',')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim().Trim('"'), StringComparer.OrdinalIgnoreCase);

        if (!parameters.TryGetValue("realm", out var realm) || !Uri.TryCreate(realm, UriKind.Absolute, out var realmUri) || realmUri.Scheme != "https")
        {
            throw new InvalidOperationException("Registry requires unsupported authentication");
        }

        var scope = parameters.GetValueOrDefault("scope") ?? $"repository:{image.Repository}:pull";
        var tokenUrl = $"{realm}?service={Uri.EscapeDataString(parameters.GetValueOrDefault("service") ?? "")}&scope={Uri.EscapeDataString(scope)}";
        var json = await http.GetFromJsonAsync<JsonObject>(tokenUrl, ct);
        return (string?)json?["token"] ?? (string?)json?["access_token"] ?? throw new InvalidOperationException("Registry returned no token (private image?)");
    }

    /// <summary>
    /// Vom Agent: Container eines Hosts ersetzen. Ist ein vorher laufender Container jetzt gestoppt (oder sein Healthcheck
    /// "unhealthy"), gibt es eine Meldung. Selbst gebaute Compose-Dienste werden über ihr Basis-Image geprüft; Images
    /// ohne Registry-Digest gar nicht (es gibt nichts, womit man sie vergleichen könnte).
    /// </summary>
    public static async Task ReplaceContainersAsync(AppDbContext db, ManagedHost host, IReadOnlyList<ContainerReport> reports, DateTime now, CancellationToken ct,
        Notifier? notifier = null)
    {
        var previous = await db.Containers.Where(c => c.HostId == host.Id).ToListAsync(ct);
        var before = previous.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
        db.Containers.RemoveRange(previous);
        foreach (var report in reports.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Name)).Take(200))
        {
            var baseImage = report.Build?.BaseImage;
            var local = report.RepoDigests is { Count: 0 };
            var reference = baseImage is not null ? ImageReference.Parse(baseImage) : local ? null : ImageReference.Parse(report.Image);
            var digests = baseImage is not null ? report.Build!.BaseRepoDigests : report.RepoDigests;
            var container = new ContainerInfo
            {
                HostId = host.Id,
                Name = Cut(report.Name, 200),
                Image = Cut(report.Image, 300),
                Reference = reference?.Normalized is { } n ? Cut(n, 300) : null,
                ImageId = report.ImageId is null ? null : Cut(report.ImageId, 100),
                LocalDigest = reference?.LocalDigest(digests) is { } d ? Cut(d, 400) : null,
                State = report.State is null ? null : Cut(report.State, 32),
                SeenAt = now,
                ComposeProject = report.Compose?.Project is { } p ? Cut(p, 200) : null,
                ComposeService = report.Compose?.Service is { } sv ? Cut(sv, 200) : null,
                ComposeFiles = report.Compose is { ConfigFiles.Count: > 0 } c ? Cut(string.Join(',', c.ConfigFiles), 1000) : null,
                ComposeFilesFound = report.Compose?.FilesFound == true,
                ContainerId = report.Id is null ? null : Cut(report.Id, 100),
                ExitCode = report.ExitCode,
                StartedAt = report.StartedAt,
                FinishedAt = report.FinishedAt,
                Health = report.Health is null ? null : Cut(report.Health, 20),
                RestartPolicy = report.RestartPolicy is null ? null : Cut(report.RestartPolicy, 30),
                LocalImage = local,
                BaseImage = baseImage is null ? null : Cut(baseImage, 300),
            };
            db.Containers.Add(container);

            if (notifier is not null && before.GetValueOrDefault(container.Name) is { } old && StopReason(old, container) is { } reason)
            {
                notifier.Publish(new Notification(NotificationEvents.ContainerStopped,
                    L.T($"{host.Name}: Container {container.Name} {(container.Health == "unhealthy" && container.State == "running" ? "ungesund" : "gestoppt")}",
                        $"{host.Name}: container {container.Name} {(container.Health == "unhealthy" && container.State == "running" ? "unhealthy" : "stopped")}"),
                    reason, NotificationSeverity.Warning, host.SiteId, host.Id, $"/hosts/{host.Id}?tab=containers"));
            }
        }

        static string Cut(string value, int max) => value.Length > max ? value[..max] : value;
    }

    /// <summary>
    /// Warum ein Wechsel gemeldet wird: lief und ist jetzt beendet (exited/dead), oder Healthcheck neu "unhealthy".
    /// Nicht gemeldet: sauber beendet (Exit 0) ohne Neustart-Regel, die ihn am Laufen halten soll (z. B. Einmal-Jobs).
    /// </summary>
    public static string? StopReason(ContainerInfo before, ContainerInfo now)
    {
        if (before.State == "running" && now.State is "exited" or "dead")
        {
            var keepsRunning = now.RestartPolicy is "always" or "unless-stopped";
            if (now.ExitCode == 0 && !keepsRunning)
            {
                return null;
            }

            return L.T($"Status {now.State}, Exit-Code {now.ExitCode?.ToString() ?? "?"}", $"State {now.State}, exit code {now.ExitCode?.ToString() ?? "?"}")
                   + (now.RestartPolicy is { Length: > 0 } policy ? L.T($", Neustart-Regel {policy}", $", restart policy {policy}") : "")
                   + (now.FinishedAt is { } at ? L.T($", beendet {at:u}", $", finished {at:u}") : "") + ".";
        }

        return before.Health != "unhealthy" && now.Health == "unhealthy" && now.State == "running"
            ? L.T("Der Healthcheck meldet \"unhealthy\"; der Container läuft, antwortet aber nicht wie erwartet.",
                "The health check reports \"unhealthy\"; the container is running but not responding as expected.")
            : null;
    }
}
