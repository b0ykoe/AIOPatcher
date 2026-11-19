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
            await ScanImagesAsync(db, onlyNew: false, now, ct);
            _installedScannedAt = now;
        }
        else
        {
            await ScanImagesAsync(db, onlyNew: true, now, ct); // gerade eingelesene Images gleich abgleichen
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
            try
            {
                aliasBudget = await ScanInstalledHostAsync(db, host, kev, aliasBudget, refresh: null, now, ct);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("OSV unreachable for installed packages: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    /// "Prüfen, ob behebbar": installierte Pakete eines Hosts sofort neu abgleichen; Abfragen, die diese CVE enthielten,
    /// werden frisch bei OSV gestellt (Debian kann inzwischen einen Fix eingetragen haben).
    /// </summary>
    public async Task RescanInstalledAsync(AppDbContext db, Guid hostId, string cve, CancellationToken ct)
    {
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == hostId && !h.IsRevoked && h.OsFamily == OsFamily.Linux, ct);
        if (host is null)
        {
            return;
        }

        var kev = (await db.CveInfos.Where(c => c.IsKev).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        await ScanInstalledHostAsync(db, host, kev, MaxAliasFetchesPerRun, lookup => CveMapping.Split(lookup.Cves).Contains(cve, StringComparer.OrdinalIgnoreCase), DateTime.UtcNow, ct);
    }

    /// <summary>
    /// Installierte dpkg-Pakete als (Quellpaket, Quellversion). OSV kennt für Debian/Ubuntu nur Quellpakete und deren Versionen;
    /// Bibliotheken wie libldb2 ("2:2.11.0+samba4.22.11…" aus samba 2:4.22.11) mit der eigenen Version abzufragen, ergibt
    /// lauter Fehlalarme. Ältere Agents melden keine Quellversion: dann die höchste Version unter den Paketen derselben Quelle
    /// (bzw. die des gleichnamigen Pakets), denn Bibliotheken tragen meist eine kleinere eigene Nummer.
    /// </summary>
    public static List<(string Package, string Version)> SourcePackages(IEnumerable<(string Name, string Package, string Version, string? SourceVersion)> installed) =>
        installed
            .GroupBy(p => p.Package)
            .SelectMany(g =>
            {
                var fallback = g.Where(p => p.Name == g.Key).Select(p => p.Version).FirstOrDefault() ?? DebianVersion.Max(g.Select(p => p.Version))!;
                return g.Select(p => (g.Key, string.IsNullOrWhiteSpace(p.SourceVersion) ? fallback : p.SourceVersion!));
            })
            .Distinct()
            .ToList();

    private async Task<int> ScanInstalledHostAsync(AppDbContext db, ManagedHost host, HashSet<string> kev, int aliasBudget, Func<VulnLookup, bool>? refresh, DateTime now, CancellationToken ct)
    {
        if (CveMapping.EcosystemForHost(host.OsName, host.OsVersion) is not { } ecosystem)
        {
            return aliasBudget;
        }

        var installed = await db.InstalledApps
            .Where(a => a.HostId == host.Id && a.Source == "dpkg" && a.PackageId != null && a.Version != null)
            .Select(a => new { a.Name, Package = a.PackageId!, Version = a.Version!, a.SourceVersion })
            .ToListAsync(ct);
        var packages = SourcePackages(installed.Select(a => (a.Name, a.Package, a.Version, a.SourceVersion)));
        if (packages.Count == 0)
        {
            return aliasBudget;
        }

        (var keys, var cached, aliasBudget) = await LookupInstalledAsync(db, ecosystem, packages, kev, aliasBudget, refresh, now, ct);

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
        return aliasBudget;
    }

    /// <summary>
    /// Installierte (Quell-)Pakete eines Ökosystems bei OSV nachschlagen; gecacht in VulnLookups (gemeinsam für Hosts und
    /// Images), aufbewahrt werden nur relevante CVEs (KEV oder EPSS ab 5 %).
    /// </summary>
    private async Task<(Dictionary<string, (string Package, string Version)> Keys, Dictionary<string, VulnLookup> Cached, int AliasBudget)> LookupInstalledAsync(
        AppDbContext db, string ecosystem, List<(string Package, string Version)> packages, HashSet<string> kev, int aliasBudget, Func<VulnLookup, bool>? refresh,
        DateTime now, CancellationToken ct)
    {
        var keys = packages.DistinctBy(p => $"{p.Package}|{p.Version}").ToDictionary(p => $"osvinst|{ecosystem}|{p.Package}|{p.Version}", p => p);
        var cached = await db.VulnLookups.Where(l => keys.Keys.Contains(l.Key)).ToDictionaryAsync(l => l.Key, ct);
        var missing = keys.Keys.Where(k => !cached.TryGetValue(k, out var c) || now - c.FetchedAt > TimeSpan.FromDays(1) || refresh?.Invoke(c) == true).ToList();

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

        return (keys, cached, aliasBudget);
    }

    // --- 6) Lücken in Container-Images ------------------------------------------------------------------

    /// <summary>
    /// Paketlisten von Container-Images (vom Agent per docker cp gelesen) wie installierte Pakete eines Hosts mit OSV
    /// abgleichen. onlyNew: nur noch nie abgeglichene (gleich nach dem Einlesen), sonst alle (alle 12 Stunden).
    /// </summary>
    public async Task ScanImagesAsync(AppDbContext db, bool onlyNew, DateTime now, CancellationToken ct)
    {
        var inUse = await db.Containers.Where(c => c.ImageId != null).Select(c => c.ImageId!).Distinct().ToListAsync(ct);
        var sets = await db.ImagePackageSets
            .Where(s => inUse.Contains(s.ImageId) && s.Ecosystem != null && s.PackageCount > 0 && (!onlyNew || s.ScannedAt == null))
            .ToListAsync(ct);
        if (sets.Count == 0)
        {
            return;
        }

        var kev = (await db.CveInfos.Where(c => c.IsKev).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var aliasBudget = MaxAliasFetchesPerRun;
        foreach (var set in sets)
        {
            try
            {
                aliasBudget = await ScanImageAsync(db, set, kev, aliasBudget, now, ct);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("OSV unreachable for container images: {Message}", ex.Message);
                return;
            }
        }

        await NotifyImageKevAsync(db, sets, ct);
    }

    private async Task<int> ScanImageAsync(AppDbContext db, ImagePackageSet set, HashSet<string> kev, int aliasBudget, DateTime now, CancellationToken ct)
    {
        var packages = ImageSourcePackages(set);
        (var keys, var cached, aliasBudget) = await LookupInstalledAsync(db, set.Ecosystem!, packages, kev, aliasBudget, null, now, ct);

        await db.ImageVulnerabilities.Where(v => v.ImageId == set.ImageId).ExecuteDeleteAsync(ct);
        foreach (var (key, package) in keys)
        {
            if (!cached.TryGetValue(key, out var lookup))
            {
                continue;
            }

            foreach (var cve in CveMapping.Split(lookup.Cves))
            {
                db.ImageVulnerabilities.Add(new ImageVulnerability
                {
                    ImageId = set.ImageId,
                    CveId = cve,
                    Package = package.Package.Length > 200 ? package.Package[..200] : package.Package,
                    InstalledVersion = package.Version.Length > 200 ? package.Version[..200] : package.Version,
                    FoundAt = now,
                });
            }
        }

        set.ScannedAt = now;
        await db.SaveChangesAsync(ct);
        return aliasBudget;
    }

    /// <summary>Pakete eines Images als (Quellpaket, Version), wie OSV sie kennt (dpkg: Quellpakete, apk: Ursprungspaket).</summary>
    public static List<(string Package, string Version)> ImageSourcePackages(ImagePackageSet set)
    {
        var packages = System.Text.Json.JsonSerializer.Deserialize(set.Packages, PullJsonContext.Default.IReadOnlyListImagePackage) ?? [];
        return set.Format == "dpkg"
            ? SourcePackages(packages.Select(p => (p.Name, p.Source ?? p.Name, p.Version, p.SourceVersion)))
            : packages.Select(p => (p.Source ?? p.Name, p.Version)).Distinct().ToList();
    }

    /// <summary>Neue KEV-Treffer in einem Image melden (einmal je CVE und Image), mit den Containern, die es nutzen.</summary>
    private async Task NotifyImageKevAsync(AppDbContext db, List<ImagePackageSet> sets, CancellationToken ct)
    {
        var imageIds = sets.Select(s => s.ImageId).ToList();
        var kevHits = await db.ImageVulnerabilities
            .Where(v => imageIds.Contains(v.ImageId))
            .Join(db.CveInfos.Where(c => c.IsKev), v => v.CveId, c => c.Id, (v, c) => new { v.ImageId, v.CveId, c.KevName })
            .ToListAsync(ct);
        foreach (var set in sets)
        {
            var current = kevHits.Where(h => h.ImageId == set.ImageId).DistinctBy(h => h.CveId).ToList();
            var notified = CveMapping.Split(set.NotifiedKevCves).ToHashSet();
            var fresh = current.Where(h => !notified.Contains(h.CveId)).ToList();
            if (fresh.Count > 0)
            {
                var containers = await db.Containers.Include(c => c.Host).Where(c => c.ImageId == set.ImageId).ToListAsync(ct);
                var details = string.Join("; ", fresh.Take(5).Select(h => $"{h.CveId} ({h.KevName})")) + (fresh.Count > 5 ? " …" : "");
                foreach (var site in containers.GroupBy(c => c.Host!.SiteId))
                {
                    var first = site.First();
                    var where = string.Join(", ", site.Select(c => $"{c.Host!.Name}/{c.Name}").Distinct().Take(10));
                    notifier.Publish(new Notification(NotificationEvents.KevDetected,
                        L.T($"{where}: aktiv ausgenutzte Lücke im Container-Image", $"{where}: actively exploited vulnerability in container image"),
                        L.T("Im Image steckt: ", "The image contains: ") + details,
                        NotificationSeverity.Critical, site.Key, site.Select(c => c.HostId).Distinct().Count() == 1 ? first.HostId : null,
                        site.Select(c => c.HostId).Distinct().Count() == 1 ? $"/hosts/{first.HostId}?tab=containers" : "/hosts"));
                }
            }

            set.NotifiedKevCves = current.Count == 0 ? null : CveMapping.Join(current.Select(h => h.CveId));
        }

        await db.SaveChangesAsync(ct);
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
