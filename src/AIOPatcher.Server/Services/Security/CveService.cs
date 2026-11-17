using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Security;

/// <summary>
/// Ordnet offenen Updates CVEs zu und bewertet sie:
/// Linux über OSV.dev (Quellpaket + installierte/neue Version), Windows über Microsoft MSRC (KB → CVE),
/// Bewertung über CISA KEV (aktiv ausgenutzt) und FIRST EPSS (Ausnutzungswahrscheinlichkeit).
/// Alle Ergebnisse werden zwischengespeichert; Dienste werden nur für Neues gefragt.
/// </summary>
public sealed class CveService(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpFactory,
    AIOPatcherOptions options,
    Notifier notifier,
    ILogger<CveService> logger,
    FeatureFlags? features = null) : BackgroundService
{
    public const string HttpClientName = "cve";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan KevMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan EpssMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan LookupMaxAge = TimeSpan.FromDays(3);
    private const int MaxAliasFetchesPerRun = 150;

    private DateTime _kevRefreshedAt;
    private DateTime _msrcRefreshedAt;
    private DateTime _epssLoadedAt;
    private DateTime _installedScannedAt;
    private Dictionary<long, (float Epss, float Percentile)> _epss = [];

    /// <summary>Ungepatchte Lücken nur speichern, wenn KEV oder EPSS mindestens so hoch (5 %).</summary>
    public const double UnfixedEpssThreshold = 0.05;

    /// <summary>EPSS aus der täglich geladenen Gesamttabelle (null = unbekannt).</summary>
    public (double Epss, double Percentile)? EpssOf(string cve) =>
        CveMapping.CveKey(cve) is { } key && _epss.TryGetValue(key, out var v) ? (v.Epss, v.Percentile) : null;
    private Dictionary<string, HashSet<string>> _msrc = [];

    private HttpClient Http => httpFactory.CreateClient(HttpClientName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = options;
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                if (features?.IsOn(Feature.CveLookup) == false)
                {
                    continue; // abgeschaltet: nächster Versuch beim nächsten Takt
                }

                using var scope = scopes.CreateScope();
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "CVE lookup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(AppDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - _kevRefreshedAt > KevMaxAge)
        {
            await RefreshKevAsync(db, ct);
            _kevRefreshedAt = now;
        }

        if (now - _epssLoadedAt > EpssMaxAge)
        {
            await LoadEpssTableAsync(ct);
            _epssLoadedAt = now;
        }

        await ResolvePendingAsync(db, now, ct);
        await RefreshEpssAsync(db, now, ct);

        if (now - _installedScannedAt > TimeSpan.FromHours(12))
        {
            await ScanInstalledAsync(db, now, ct);
            _installedScannedAt = now;
        }
        await ApplyScoresAsync(db, notifier, ct);
    }

    // --- 1) CISA KEV ---------------------------------------------------------------------------------

    private async Task RefreshKevAsync(AppDbContext db, CancellationToken ct)
    {
        var json = await Http.GetFromJsonAsync<JsonObject>("https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json", ct);
        var entries = (json?["vulnerabilities"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(v => (Id: ((string?)v["cveID"])?.ToUpperInvariant(), Node: v))
            .Where(v => v.Id is not null)
            .GroupBy(v => v.Id!)
            .ToDictionary(g => g.Key, g => g.First().Node);
        if (entries.Count == 0)
        {
            return; // lieber alten Stand behalten als alles auf "nicht KEV" setzen
        }

        var existing = await db.CveInfos.ToDictionaryAsync(c => c.Id, ct);
        foreach (var info in existing.Values.Where(c => c.IsKev && !entries.ContainsKey(c.Id)))
        {
            info.IsKev = false;
        }

        foreach (var (id, node) in entries)
        {
            if (!existing.TryGetValue(id, out var info))
            {
                info = new CveInfo { Id = id };
                db.CveInfos.Add(info);
            }

            info.IsKev = true;
            info.KevDateAdded = DateTime.TryParse((string?)node["dateAdded"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var added) ? added : null;
            var name = $"{node["vendorProject"]} {node["product"]}: {node["vulnerabilityName"]}";
            info.KevName = name.Length > 300 ? name[..300] : name;
            info.KevRansomware = string.Equals((string?)node["knownRansomwareCampaignUse"], "Known", StringComparison.OrdinalIgnoreCase);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("CISA KEV catalog: {Count} entries", entries.Count);
    }

    // --- 2) Zuordnung Update → CVEs --------------------------------------------------------------------

    private async Task ResolvePendingAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var pending = await db.PendingUpdates
            .Include(u => u.Host)
            .Where(u => u.CveCheckedAt == null)
            .Take(3000)
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            return;
        }

        var keyed = pending.Select(u => (Update: u, Key: CveMapping.LookupKey(u, u.Host!.OsFamily))).ToList();
        var keys = keyed.Select(k => k.Key).OfType<string>().Distinct().ToList();
        var cached = await db.VulnLookups.Where(l => keys.Contains(l.Key)).ToDictionaryAsync(l => l.Key, ct);
        var missing = keys.Where(k => !cached.TryGetValue(k, out var c) || now - c.FetchedAt > LookupMaxAge).ToList();

        var resolved = new Dictionary<string, string>();
        try
        {
            foreach (var (key, cves) in await ResolveOsvAsync(db, missing.Where(k => k.StartsWith("osv|", StringComparison.Ordinal)).ToList(), now, ct))
            {
                resolved[key] = cves;
            }

            foreach (var (key, cves) in await ResolveMsrcAsync(missing.Where(k => k.StartsWith("msrc|", StringComparison.Ordinal)).ToList(), now, ct))
            {
                resolved[key] = cves;
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("CVE sources unreachable: {Message}", ex.Message);
        }

        foreach (var (key, cves) in resolved)
        {
            if (cached.TryGetValue(key, out var lookup))
            {
                lookup.Cves = cves;
                lookup.FetchedAt = now;
            }
            else
            {
                db.VulnLookups.Add(cached[key] = new VulnLookup { Key = key, Cves = cves, FetchedAt = now });
            }
        }

        foreach (var (update, key) in keyed)
        {
            if (key is null)
            {
                update.Cves = "";
                update.CveCheckedAt = now;
            }
            else if (cached.TryGetValue(key, out var lookup))
            {
                update.Cves = lookup.Cves;
                update.CveCheckedAt = now;
            }
            // sonst: Quelle nicht erreichbar, beim nächsten Durchlauf erneut versuchen
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>CVEs, die das Update schließt = betreffen die installierte, aber nicht mehr die neue Version.</summary>
    private async Task<Dictionary<string, string>> ResolveOsvAsync(AppDbContext db, List<string> keys, DateTime now, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        if (keys.Count == 0)
        {
            return result;
        }

        var aliasBudget = MaxAliasFetchesPerRun;
        foreach (var chunk in keys.Chunk(400))
        {
            var queries = new JsonArray();
            foreach (var key in chunk)
            {
                var (ecosystem, package, current, available) = CveMapping.ParseOsvKey(key);
                queries.Add(new JsonObject { ["package"] = new JsonObject { ["name"] = package, ["ecosystem"] = ecosystem }, ["version"] = current });
                queries.Add(new JsonObject { ["package"] = new JsonObject { ["name"] = package, ["ecosystem"] = ecosystem }, ["version"] = available });
            }

            using var response = await Http.PostAsJsonAsync("https://api.osv.dev/v1/querybatch", new JsonObject { ["queries"] = queries }, ct);
            response.EnsureSuccessStatusCode();
            var results = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))?["results"] as JsonArray ?? [];

            for (var i = 0; i < chunk.Length; i++)
            {
                var before = Ids(results.ElementAtOrDefault(i * 2));
                var after = Ids(results.ElementAtOrDefault(i * 2 + 1));
                var fixedIds = before.Except(after).ToList();

                var cves = new HashSet<string>();
                foreach (var id in fixedIds)
                {
                    var direct = CveMapping.ExtractCves(id).ToList();
                    if (direct.Count > 0)
                    {
                        cves.UnionWith(direct);
                    }
                    else if (aliasBudget-- > 0)
                    {
                        cves.UnionWith(await OsvAliasesAsync(db, id, now, ct)); // USN-/DSA-/DLA-Meldungen → CVEs
                    }
                }

                result[chunk[i]] = CveMapping.Join(cves);
            }
        }

        return result;

        static IEnumerable<string> Ids(JsonNode? result) =>
            (result?["vulns"] as JsonArray ?? []).Select(v => (string?)v?["id"]).OfType<string>();
    }

    private async Task<IEnumerable<string>> OsvAliasesAsync(AppDbContext db, string id, DateTime now, CancellationToken ct)
    {
        var key = $"osvid|{id}";
        var cached = await db.VulnLookups.FindAsync([key], ct);
        if (cached is not null)
        {
            return CveMapping.Split(cached.Cves);
        }

        var vuln = await Http.GetFromJsonAsync<JsonObject>($"https://api.osv.dev/v1/vulns/{Uri.EscapeDataString(id)}", ct);
        var aliases = (vuln?["aliases"] as JsonArray ?? []).Select(a => (string?)a).OfType<string>()
            .Concat((vuln?["upstream"] as JsonArray ?? []).Select(a => (string?)a).OfType<string>())
            .SelectMany(CveMapping.ExtractCves)
            .ToList();
        db.VulnLookups.Add(new VulnLookup { Key = key, Cves = CveMapping.Join(aliases), FetchedAt = now });
        return aliases;
    }

    private async Task<Dictionary<string, string>> ResolveMsrcAsync(List<string> keys, DateTime now, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        if (keys.Count == 0)
        {
            return result;
        }

        if (now - _msrcRefreshedAt > TimeSpan.FromHours(24))
        {
            await RefreshMsrcAsync(ct);
            _msrcRefreshedAt = now;
        }

        foreach (var key in keys)
        {
            var kb = key["msrc|KB".Length..];
            result[key] = _msrc.TryGetValue(kb, out var cves) ? CveMapping.Join(cves) : "";
        }

        return result;
    }

    /// <summary>Lädt die CVRF-Dokumente der letzten Monate (je einige MB) und baut die KB-Zuordnung.</summary>
    private async Task RefreshMsrcAsync(CancellationToken ct)
    {
        var updates = await Http.GetFromJsonAsync<JsonObject>("https://api.msrc.microsoft.com/cvrf/v3.0/updates", ct);
        var ids = (updates?["value"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(u => (Id: (string?)u["ID"], Date: DateTime.TryParse((string?)u["InitialReleaseDate"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.MinValue))
            .Where(u => u.Id is not null)
            .OrderByDescending(u => u.Date)
            .Take(3) // je Monat ~20 MB; kumulative Updates decken die letzten Monate ab
            .Select(u => u.Id!)
            .ToList();

        var map = new Dictionary<string, HashSet<string>>();
        foreach (var id in ids)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.msrc.microsoft.com/cvrf/v3.0/cvrf/{Uri.EscapeDataString(id)}");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            foreach (var (kb, cves) in CveMapping.ParseMsrcDocument(await response.Content.ReadFromJsonAsync<JsonNode>(ct)))
            {
                if (!map.TryGetValue(kb, out var set))
                {
                    map[kb] = set = [];
                }

                set.UnionWith(cves);
            }
        }

        if (map.Count > 0)
        {
            _msrc = map;
            logger.LogInformation("MSRC: {Count} KB articles from {Documents} security update documents", map.Count, ids.Count);
        }
    }

    // --- 3) EPSS --------------------------------------------------------------------------------------

    private async Task RefreshEpssAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var all = (await db.PendingUpdates.Where(u => u.Cves != null && u.Cves != "").Select(u => u.Cves).ToListAsync(ct))
            .SelectMany(CveMapping.Split)
            .Distinct()
            .ToList();
        if (all.Count == 0)
        {
            return;
        }

        var known = await db.CveInfos.Where(c => all.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var stale = all.Where(id => !known.TryGetValue(id, out var c) || c.EpssUpdatedAt is null || now - c.EpssUpdatedAt > EpssMaxAge).ToList();

        // Gesamttabelle vorhanden: daraus übernehmen statt einzeln zu fragen.
        if (_epss.Count > 0)
        {
            foreach (var id in stale)
            {
                if (!known.TryGetValue(id, out var info))
                {
                    db.CveInfos.Add(known[id] = info = new CveInfo { Id = id });
                }

                info.EpssUpdatedAt = now;
                if (EpssOf(id) is { } score)
                {
                    info.Epss = score.Epss;
                    info.EpssPercentile = score.Percentile;
                }
            }

            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            foreach (var chunk in stale.Chunk(80))
            {
                var json = await Http.GetFromJsonAsync<JsonObject>($"https://api.first.org/data/v1/epss?cve={string.Join(',', chunk)}", ct);
                var scores = (json?["data"] as JsonArray ?? []).OfType<JsonObject>()
                    .ToDictionary(d => ((string?)d["cve"] ?? "").ToUpperInvariant(), d => d);
                foreach (var id in chunk)
                {
                    if (!known.TryGetValue(id, out var info))
                    {
                        db.CveInfos.Add(known[id] = info = new CveInfo { Id = id });
                    }

                    info.EpssUpdatedAt = now;
                    if (scores.TryGetValue(id, out var score))
                    {
                        info.Epss = ParseDouble(score["epss"]);
                        info.EpssPercentile = ParseDouble(score["percentile"]);
                    }
                }
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("EPSS unreachable: {Message}", ex.Message);
        }

        await db.SaveChangesAsync(ct);

        static double? ParseDouble(JsonNode? node) =>
            double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>EPSS-Gesamttabelle (~3 MB gzip, ~400 000 CVEs), einmal täglich.</summary>
    private async Task LoadEpssTableAsync(CancellationToken ct)
    {
        try
        {
            await using var stream = await Http.GetStreamAsync("https://epss.empiricalsecurity.com/epss_scores-current.csv.gz", ct);
            await using var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            var table = CveMapping.ParseEpssCsv(reader);
            if (table.Count > 0)
            {
                _epss = table;
                logger.LogInformation("EPSS table: {Count} CVEs", table.Count);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            logger.LogWarning("EPSS table download failed: {Message}", ex.Message);
        }
    }

    // --- 5) Lücken in installierten Paketen ohne verfügbares Update -----------------------------------

    /// <summary>
    /// Fragt OSV für alle installierten Debian/Ubuntu-Quellpakete ab. Gespeichert werden nur Lücken, die kein
    /// verfügbares Update schließt und die relevant sind (KEV oder EPSS ≥ 5 %).
    /// </summary>
    private async Task ScanInstalledAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var hosts = await db.Hosts.Where(h => !h.IsRevoked && h.OsFamily == OsFamily.Linux).ToListAsync(ct);
        var kev = (await db.CveInfos.Where(c => c.IsKev).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var aliasBudget = MaxAliasFetchesPerRun;

        foreach (var host in hosts)
        {
            if (CveMapping.EcosystemForHost(host.OsName, host.OsVersion) is not { } ecosystem)
            {
                continue;
            }

            var packages = await db.InstalledApps
                .Where(a => a.HostId == host.Id && a.Source == "dpkg" && a.PackageId != null && a.Version != null)
                .Select(a => new { Package = a.PackageId!, Version = a.Version! })
                .Distinct()
                .ToListAsync(ct);
            if (packages.Count == 0)
            {
                continue;
            }

            var keys = packages.ToDictionary(p => $"osvinst|{ecosystem}|{p.Package}|{p.Version}", p => p);
            var cached = await db.VulnLookups.Where(l => keys.Keys.Contains(l.Key)).ToDictionaryAsync(l => l.Key, ct);
            var missing = keys.Keys.Where(k => !cached.TryGetValue(k, out var c) || now - c.FetchedAt > TimeSpan.FromDays(1)).ToList();

            try
            {
                foreach (var chunk in missing.Chunk(500))
                {
                    var queries = new JsonArray();
                    foreach (var key in chunk)
                    {
                        queries.Add(new JsonObject { ["package"] = new JsonObject { ["name"] = keys[key].Package, ["ecosystem"] = ecosystem }, ["version"] = keys[key].Version });
                    }

                    using var response = await Http.PostAsJsonAsync("https://api.osv.dev/v1/querybatch", new JsonObject { ["queries"] = queries }, ct);
                    response.EnsureSuccessStatusCode();
                    var results = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))?["results"] as JsonArray ?? [];
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        var ids = (results.ElementAtOrDefault(i)?["vulns"] as JsonArray ?? []).Select(v => (string?)v?["id"]).OfType<string>();
                        var cves = new HashSet<string>();
                        foreach (var id in ids)
                        {
                            var direct = CveMapping.ExtractCves(id).ToList();
                            if (direct.Count > 0)
                            {
                                cves.UnionWith(direct);
                            }
                            else if (aliasBudget-- > 0)
                            {
                                cves.UnionWith(await OsvAliasesAsync(db, id, now, ct));
                            }
                        }

                        // Kernel & Co. haben tausende Einträge: nur die relevanten aufbewahren.
                        var relevant = cves.Where(c => kev.Contains(c) || EpssOf(c)?.Epss >= UnfixedEpssThreshold);
                        if (cached.TryGetValue(chunk[i], out var lookup))
                        {
                            lookup.Cves = CveMapping.Join(relevant);
                            lookup.FetchedAt = now;
                        }
                        else
                        {
                            db.VulnLookups.Add(cached[chunk[i]] = new VulnLookup { Key = chunk[i], Cves = CveMapping.Join(relevant), FetchedAt = now });
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("OSV unreachable for installed packages: {Message}", ex.Message);
                continue;
            }

            var fixable = (await db.PendingUpdates.Where(u => u.HostId == host.Id && u.Cves != null && u.Cves != "").Select(u => u.Cves).ToListAsync(ct))
                .SelectMany(CveMapping.Split)
                .ToHashSet();

            await db.HostVulnerabilities.Where(v => v.HostId == host.Id).ExecuteDeleteAsync(ct);
            foreach (var (key, package) in keys)
            {
                if (!cached.TryGetValue(key, out var lookup))
                {
                    continue;
                }

                foreach (var cve in CveMapping.Split(lookup.Cves).Where(c => !fixable.Contains(c)))
                {
                    db.HostVulnerabilities.Add(new HostVulnerability { HostId = host.Id, CveId = cve, Package = package.Package, InstalledVersion = package.Version, FoundAt = now });
                }
            }

            await db.SaveChangesAsync(ct);
        }
    }

    // --- 4) Bewertung übernehmen und neue KEV-Treffer melden ------------------------------------------

    public static async Task ApplyScoresAsync(AppDbContext db, Notifier notifier, CancellationToken ct)
    {
        var updates = await db.PendingUpdates.Where(u => u.Cves != null && u.Cves != "").ToListAsync(ct);
        var ids = updates.SelectMany(u => CveMapping.Split(u.Cves)).Distinct().ToList();
        var infos = await db.CveInfos.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        foreach (var update in updates)
        {
            var scored = CveMapping.Split(update.Cves).Select(id => infos.GetValueOrDefault(id)).OfType<CveInfo>().ToList();
            update.IsKev = scored.Any(c => c.IsKev);
            update.MaxEpss = scored.Max(c => c.Epss);
        }

        // Pro Host: KEV-CVEs, die noch nicht gemeldet wurden
        var kevByHost = updates.Where(u => u.IsKev)
            .GroupBy(u => u.HostId)
            .ToDictionary(g => g.Key, g => g.SelectMany(u => CveMapping.Split(u.Cves)).Where(id => infos.GetValueOrDefault(id)?.IsKev == true).Distinct().ToList());
        var kevHostIds = kevByHost.Keys.ToList();
        var hosts = await db.Hosts.Include(h => h.Site).Where(h => h.NotifiedKevCves != null || kevHostIds.Contains(h.Id)).ToListAsync(ct);
        foreach (var host in hosts)
        {
            var current = kevByHost.GetValueOrDefault(host.Id) ?? [];
            var alreadyNotified = CveMapping.Split(host.NotifiedKevCves).ToHashSet();
            var fresh = current.Where(id => !alreadyNotified.Contains(id)).ToList();
            if (fresh.Count > 0)
            {
                var details = fresh.Take(5).Select(id => $"{id} ({infos[id].KevName})");
                notifier.Publish(new Notification(NotificationEvents.KevDetected,
                    L.T($"{host.Name}: aktiv ausgenutzte Lücke offen", $"{host.Name}: actively exploited vulnerability open"),
                    L.T("Ein verfügbares Update schließt: ", "An available update fixes: ") + string.Join("; ", details) + (fresh.Count > 5 ? " …" : ""),
                    NotificationSeverity.Critical, host.SiteId, host.Id, $"/hosts/{host.Id}"));
            }

            host.NotifiedKevCves = current.Count == 0 ? null : CveMapping.Join(current);
        }

        await db.SaveChangesAsync(ct);
    }
}
