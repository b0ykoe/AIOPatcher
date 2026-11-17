using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Security;

/// <summary>
/// CVE nachschlagen (Seite "CVE-Suche"): Beschreibung und CVSS aus der lokalen CVE-Datenbank (<see cref="CveMirror"/>,
/// solange sie noch lädt: NVD live), Fix-Versionen je Distribution von OSV.dev (Debian/Ubuntu), KBs je Windows-Version
/// von Microsoft (Security Update Guide), dazu KEV/EPSS und die eigenen betroffenen Hosts. OSV und Microsoft werden nur
/// beim Aufruf gefragt, Ergebnisse einige Stunden zwischengespeichert.
/// </summary>
public sealed partial class CveLookupService(
    IHttpClientFactory httpFactory,
    AIOPatcherOptions options,
    CveService cves,
    ILogger<CveLookupService> logger,
    FeatureFlags? features = null,
    CveMirror? mirror = null)
{
    public sealed record CveScore(string Version, double BaseScore, string Severity, string? Vector, string? Source);

    /// <summary>Fix je System: Debian/Ubuntu (Paket, behoben in Version; null = noch offen) bzw. Windows (KB, Build).</summary>
    public sealed record SystemFix(string System, string Package, string? FixedIn, string Source);

    public sealed record AffectedHost(Guid HostId, string HostName, string? Fix, string? Package, bool Acknowledged = false);

    public sealed record CveDetails(
        string Id,
        bool Found,
        string? Description,
        DateTime? Published,
        DateTime? Modified,
        string? Status,
        IReadOnlyList<CveScore> Scores,
        IReadOnlyList<string> Weaknesses,
        IReadOnlyList<string> References,
        IReadOnlyList<string> Aliases,
        bool IsKev,
        string? KevName,
        DateTime? KevDateAdded,
        bool KevRansomware,
        double? Epss,
        double? EpssPercentile,
        IReadOnlyList<SystemFix> Fixes,
        IReadOnlyList<AffectedHost> Hosts,
        bool ExternalDisabled,
        IReadOnlyList<string> Unavailable,
        DateTime? MirrorSyncedAt);

    public sealed record SearchHit(string Id, string? Description, DateTime? Published, double? Score, string? Severity, bool IsKev, double? Epss, int Hosts);

    /// <summary>Nvd: Treffer aus der lokalen CVE-Datenbank (MirrorSyncedAt gesetzt) bzw. live von NVD.</summary>
    public sealed record SearchResult(
        IReadOnlyList<SearchHit> Local, IReadOnlyList<SearchHit> Nvd, int? NvdTotal, bool ExternalDisabled, IReadOnlyList<string> Unavailable,
        DateTime? MirrorSyncedAt);

    private static readonly TimeSpan DetailMaxAge = TimeSpan.FromHours(6);
    private static readonly TimeSpan SearchMaxAge = TimeSpan.FromHours(1);
    private const int MaxCacheEntries = 300;
    private const int NvdPageSize = 25;
    private const int MirrorPageSize = 50;

    private readonly ConcurrentDictionary<string, (DateTime At, object Value)> _cache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^CVE-\d{4}-\d{4,7}$", RegexOptions.IgnoreCase)]
    private static partial Regex CveIdPattern();

    [GeneratedRegex(@"^(?:CVE-)?(\d{4})-(\d{4,7})$", RegexOptions.IgnoreCase)]
    private static partial Regex LooseCvePattern();

    private bool ExternalOn => features?.IsOn(Feature.CveLookup) ?? options.CveLookupEnabled;

    private HttpClient Http => httpFactory.CreateClient(CveService.HttpClientName);

    /// <summary>"cve-2024-3094" oder "2024-3094" → "CVE-2024-3094"; sonst null.</summary>
    public static string? NormalizeId(string? input)
    {
        var m = LooseCvePattern().Match(input?.Trim() ?? "");
        return m.Success ? $"CVE-{m.Groups[1].Value}-{m.Groups[2].Value}" : null;
    }

    public static bool IsCveId(string? id) => id is not null && CveIdPattern().IsMatch(id);

    // --- Detail ---------------------------------------------------------------------------------------

    public async Task<CveDetails> LookupAsync(string id, AppDbContext db, CancellationToken ct)
    {
        id = id.ToUpperInvariant();
        var external = ExternalOn;
        var unavailable = new List<string>();

        // Beschreibung und Bewertung aus der lokalen Datenbank; NVD live nur, solange sie fehlt oder die CVE ganz neu ist
        var mirrorReady = mirror?.Ready == true;
        var nvd = mirrorReady ? mirror!.Get(id) : null;
        var fromMirror = nvd is not null;
        var fixes = new List<SystemFix>();
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? osvSummary = null;

        if (external)
        {
            var askNvd = nvd is null;
            var cacheKey = "detail|" + id + (askNvd ? "|nvd" : "");
            var cached = FromCache<ExternalData>(cacheKey, DetailMaxAge);
            if (cached is null)
            {
                cached = await FetchExternalAsync(id, askNvd, ct);
                if (cached.Unavailable.Count == 0)
                {
                    ToCache(cacheKey, cached);
                }
            }

            nvd ??= cached.Nvd;
            fixes.AddRange(cached.Fixes);
            aliases.UnionWith(cached.Aliases);
            osvSummary = cached.OsvSummary;
            unavailable.AddRange(cached.Unavailable);
        }

        aliases.Remove(id);
        var info = await db.CveInfos.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        (double Epss, double Percentile)? epss = cves.EpssOf(id);
        if (epss is null && info?.Epss is { } stored)
        {
            epss = (stored, info.EpssPercentile ?? 0);
        }

        if (epss is null && external)
        {
            epss = await FetchEpssAsync(id, ct);
        }

        var hosts = await AffectedHostsAsync(id, db, ct);
        var found = nvd is not null || fixes.Count > 0 || osvSummary is not null || info is not null || hosts.Count > 0;

        return new CveDetails(
            id,
            found,
            nvd?.Description ?? osvSummary,
            nvd?.Published,
            nvd?.Modified,
            nvd?.Status,
            nvd?.Scores ?? [],
            nvd?.Weaknesses ?? [],
            nvd?.References ?? [],
            aliases.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            info?.IsKev == true,
            info?.KevName,
            info?.KevDateAdded,
            info?.KevRansomware == true,
            epss?.Epss,
            epss?.Percentile,
            fixes,
            hosts,
            !external,
            unavailable,
            fromMirror ? mirror!.GetStatus().SyncedAt : null);
    }

    private sealed record ExternalData(NvdRecord? Nvd, List<SystemFix> Fixes, List<string> Aliases, string? OsvSummary, List<string> Unavailable);

    private async Task<ExternalData> FetchExternalAsync(string id, bool askNvd, CancellationToken ct)
    {
        var escaped = Uri.EscapeDataString(id);
        var nvdTask = askNvd
            ? GetJsonAsync("NVD", $"https://services.nvd.nist.gov/rest/json/cves/2.0?cveId={escaped}", nvdKey: true, ct)
            : Task.FromResult<(JsonNode? Node, string? Failed)>((null, null));
        var osvTask = GetJsonAsync("OSV", $"https://api.osv.dev/v1/vulns/{escaped}", nvdKey: false, ct);
        var debianTask = GetJsonAsync("OSV", $"https://api.osv.dev/v1/vulns/DEBIAN-{escaped}", nvdKey: false, ct);
        var ubuntuTask = GetJsonAsync("OSV", $"https://api.osv.dev/v1/vulns/UBUNTU-{escaped}", nvdKey: false, ct);
        var msrcFilter = Uri.EscapeDataString($"cveNumber eq '{id}'");
        var msrcTask = GetJsonAsync("MSRC", $"https://api.msrc.microsoft.com/sug/v2.0/en-US/affectedProduct?$filter={msrcFilter}", nvdKey: false, ct);
        await Task.WhenAll(nvdTask, osvTask, debianTask, ubuntuTask, msrcTask);

        var unavailable = new[] { nvdTask, osvTask, debianTask, ubuntuTask, msrcTask }
            .Where(t => t.Result.Failed is not null)
            .Select(t => t.Result.Failed!)
            .Distinct()
            .ToList();

        var nvd = (await nvdTask).Node is { } n ? ParseNvd(n).FirstOrDefault() : null;
        var fixes = new List<SystemFix>();
        var aliases = new List<string>();
        string? summary = null;
        foreach (var task in new[] { osvTask, debianTask, ubuntuTask })
        {
            if ((await task).Node is not { } node)
            {
                continue;
            }

            var osv = ParseOsv(node);
            fixes.AddRange(osv.Fixes);
            aliases.AddRange(osv.Aliases);
            summary ??= osv.Summary;
        }

        fixes = fixes.DistinctBy(f => (f.System, f.Package, f.FixedIn))
            .OrderBy(f => f.System, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Package, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if ((await msrcTask).Node is { } msrc)
        {
            fixes.AddRange(ParseMsrcAffected(msrc));
        }

        return new ExternalData(nvd, fixes, aliases, summary, unavailable);
    }

    private async Task<(double, double)?> FetchEpssAsync(string id, CancellationToken ct)
    {
        var (node, _) = await GetJsonAsync("EPSS", $"https://api.first.org/data/v1/epss?cve={Uri.EscapeDataString(id)}", nvdKey: false, ct);
        var row = (node?["data"] as JsonArray)?.FirstOrDefault();
        return double.TryParse((string?)row?["epss"], NumberStyles.Float, CultureInfo.InvariantCulture, out var e)
            && double.TryParse((string?)row?["percentile"], NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
            ? (e, p)
            : null;
    }

    /// <summary>Eigene Hosts mit dieser CVE: offenes Update, das sie schließt, oder Lücke ohne Update.</summary>
    public static async Task<List<AffectedHost>> AffectedHostsAsync(string id, AppDbContext db, CancellationToken ct)
    {
        var withUpdate = await db.PendingUpdates
            .Where(u => u.Cves != null && u.Cves.Contains(id) && !u.Host!.IsRevoked)
            .Select(u => new { u.HostId, HostName = u.Host!.Name, u.Name, u.Cves })
            .ToListAsync(ct);
        var withoutUpdate = await db.HostVulnerabilities
            .Where(v => v.CveId == id && !v.Host!.IsRevoked)
            .Select(v => new { v.HostId, HostName = v.Host!.Name, v.Package, v.InstalledVersion })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var acks = await db.VulnerabilityAcks.AsNoTracking().Where(a => a.CveId == id).ToListAsync(ct);
        return withUpdate
            .Where(u => CveMapping.Split(u.Cves).Contains(id, StringComparer.OrdinalIgnoreCase)) // "CVE-2024-1" steckt auch in "CVE-2024-12"
            .Select(u => new AffectedHost(u.HostId, u.HostName, u.Name, null))
            .Concat(withoutUpdate.Select(v => new AffectedHost(v.HostId, v.HostName, null, $"{v.Package} {v.InstalledVersion}".Trim())))
            .Select(h => h with { Acknowledged = acks.Any(a => a.Covers(h.HostId, now)) })
            .DistinctBy(h => (h.HostId, h.Fix, h.Package))
            .OrderBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // --- Suche ----------------------------------------------------------------------------------------

    public async Task<SearchResult> SearchAsync(string query, AppDbContext db, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length > 100)
        {
            query = query[..100];
        }

        var local = await SearchLocalAsync(query, db, ct);
        if (mirror?.Ready == true)
        {
            var (found, total) = query.Length >= 2 ? mirror.Search(query, MirrorPageSize) : ([], 0);
            var foundIds = found.Select(h => h.Id).ToList();
            var foundInfos = await db.CveInfos.AsNoTracking().Where(c => foundIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
            var hostCounts = local.ToDictionary(h => h.Id, h => h.Hosts);
            var mirrorHits = found.Select(h =>
            {
                var info = foundInfos.GetValueOrDefault(h.Id);
                return new SearchHit(h.Id, h.Description, h.Published, h.Score, h.Severity, info?.IsKev == true,
                    cves.EpssOf(h.Id)?.Epss ?? info?.Epss, hostCounts.GetValueOrDefault(h.Id));
            }).ToList();
            return new SearchResult(local, mirrorHits, total, !ExternalOn, [], mirror.GetStatus().SyncedAt);
        }

        if (!ExternalOn || query.Length < 3)
        {
            return new SearchResult(local, [], null, !ExternalOn, [], null);
        }

        var key = "search|" + query;
        if (FromCache<NvdSearch>(key, SearchMaxAge) is not { } nvd)
        {
            var fetched = await SearchNvdAsync(query, ct);
            if (fetched is null)
            {
                return new SearchResult(local, [], null, false, ["NVD"], null);
            }

            nvd = fetched;
            ToCache(key, nvd);
        }

        var ids = nvd.Hits.Select(h => h.Id).ToList();
        var infos = await db.CveInfos.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var localCounts = local.ToDictionary(h => h.Id, h => h.Hosts);
        var hits = nvd.Hits.Select(h =>
        {
            var score = h.Scores.FirstOrDefault();
            var info = infos.GetValueOrDefault(h.Id);
            var epss = cves.EpssOf(h.Id)?.Epss ?? info?.Epss;
            return new SearchHit(h.Id, h.Description, h.Published, score?.BaseScore, score?.Severity, info?.IsKev == true, epss, localCounts.GetValueOrDefault(h.Id));
        }).ToList();

        return new SearchResult(local, hits, nvd.Total, false, [], null);
    }

    /// <summary>Treffer in den eigenen Daten: CVEs der Hosts (nach CVE, Host, Paket/Update) und der KEV-Katalog (Hersteller/Produkt).</summary>
    private async Task<List<SearchHit>> SearchLocalAsync(string query, AppDbContext db, CancellationToken ct)
    {
        if (query.Length < 2)
        {
            return [];
        }

        var q = query.ToLowerInvariant();
        var hostsByCve = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        void Add(string cve, Guid host)
        {
            if (!hostsByCve.TryGetValue(cve, out var set))
            {
                hostsByCve[cve] = set = [];
            }

            set.Add(host);
        }

        var updates = await db.PendingUpdates
            .Where(u => u.Cves != null && u.Cves != "" && !u.Host!.IsRevoked)
            .Select(u => new { u.HostId, HostName = u.Host!.Name, u.Name, u.Cves })
            .ToListAsync(ct);
        foreach (var u in updates)
        {
            var rowMatches = u.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || u.HostName.Contains(q, StringComparison.OrdinalIgnoreCase);
            foreach (var cve in CveMapping.Split(u.Cves))
            {
                if (rowMatches || cve.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    Add(cve, u.HostId);
                }
            }
        }

        var unfixed = await db.HostVulnerabilities
            .Where(v => !v.Host!.IsRevoked)
            .Select(v => new { v.HostId, HostName = v.Host!.Name, v.CveId, v.Package })
            .ToListAsync(ct);
        foreach (var v in unfixed.Where(v => v.CveId.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || v.Package.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || v.HostName.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            Add(v.CveId, v.HostId);
        }

        // KEV-Katalog (liegt komplett lokal): Suche nach Hersteller/Produkt, auch ohne eigenen Treffer
        var kev = await db.CveInfos.AsNoTracking()
            .Where(c => c.IsKev && c.KevName != null && (EF.Functions.Like(c.KevName, $"%{EscapeLike(query)}%", "\\") || EF.Functions.Like(c.Id, $"%{EscapeLike(query)}%", "\\")))
            .OrderByDescending(c => c.KevDateAdded)
            .Take(50)
            .ToListAsync(ct);

        var ids = hostsByCve.Keys.Concat(kev.Select(k => k.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var infos = await db.CveInfos.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, StringComparer.OrdinalIgnoreCase, ct);
        var details = mirror?.Ready == true ? mirror.GetMany(ids) : [];

        return ids.Select(id =>
            {
                var info = infos.GetValueOrDefault(id);
                var detail = details.GetValueOrDefault(id);
                return new SearchHit(id, detail?.Description ?? info?.KevName, detail?.Published, detail?.Score, detail?.Severity, info?.IsKev == true,
                    cves.EpssOf(id)?.Epss ?? info?.Epss, hostsByCve.TryGetValue(id, out var h) ? h.Count : 0);
            })
            .OrderByDescending(h => h.Hosts > 0)
            .ThenByDescending(h => h.IsKev)
            .ThenByDescending(h => h.Epss ?? 0)
            .ThenByDescending(h => h.Id, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>NVD-Stichwortsuche; die API liefert älteste zuerst, daher die letzte Seite (= neueste Einträge).</summary>
    private sealed record NvdSearch(List<NvdRecord> Hits, int Total);

    private async Task<NvdSearch?> SearchNvdAsync(string query, CancellationToken ct)
    {
        var baseUrl = $"https://services.nvd.nist.gov/rest/json/cves/2.0?keywordSearch={Uri.EscapeDataString(query)}&noRejected";
        var (first, failed) = await GetJsonAsync("NVD", $"{baseUrl}&resultsPerPage={NvdPageSize}&startIndex=0", nvdKey: true, ct);
        if (failed is not null)
        {
            return null;
        }

        var total = (int?)first?["totalResults"] ?? 0;
        var node = first;
        if (total > NvdPageSize)
        {
            var (last, lastFailed) = await GetJsonAsync("NVD", $"{baseUrl}&resultsPerPage={NvdPageSize}&startIndex={total - NvdPageSize}", nvdKey: true, ct);
            if (lastFailed is null && last is not null)
            {
                node = last;
            }
        }

        var hits = node is null ? [] : ParseNvd(node).OrderByDescending(r => r.Published).ToList();
        return new NvdSearch(hits, total);
    }

    // --- HTTP und Cache -------------------------------------------------------------------------------

    /// <summary>JSON holen. 404 = gibt es nicht (kein Fehler); sonst Fehler mit Quellenname.</summary>
    private async Task<(JsonNode? Node, string? Failed)> GetJsonAsync(string source, string url, bool nvdKey, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            if (nvdKey && !string.IsNullOrWhiteSpace(options.NvdApiKey))
            {
                request.Headers.TryAddWithoutValidation("apiKey", options.NvdApiKey);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await Http.SendAsync(request, cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, null);
            }

            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonNode>(cts.Token), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("CVE lookup at {Source} failed: {Error}", source, ex.Message);
            return (null, source);
        }
    }

    private T? FromCache<T>(string key, TimeSpan maxAge) =>
        _cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.At < maxAge && entry.Value is T value ? value : default;

    private void ToCache(string key, object value)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        _cache[key] = (DateTime.UtcNow, value);
    }

    // --- Parser (öffentlich für Tests) ----------------------------------------------------------------

    public sealed record NvdRecord(
        string Id, string? Description, DateTime? Published, DateTime? Modified, string? Status,
        List<CveScore> Scores, List<string> Weaknesses, List<string> References);

    /// <summary>NVD API 2.0: vulnerabilities[].cve.</summary>
    public static List<NvdRecord> ParseNvd(JsonNode root) =>
        (root["vulnerabilities"] as JsonArray ?? []).Select(v => v?["cve"]).OfType<JsonObject>()
            .Select(ParseNvdCve)
            .OfType<NvdRecord>()
            .ToList();

    /// <summary>Ein Eintrag (vulnerabilities[].cve) aus API oder Datenfeed.</summary>
    public static NvdRecord? ParseNvdCve(JsonObject cve)
    {
        var id = (string?)cve["id"];
        if (id is null)
        {
            return null;
        }

        var description = (cve["descriptions"] as JsonArray ?? []).OfType<JsonObject>()
            .OrderByDescending(d => (string?)d["lang"] == "en")
            .Select(d => (string?)d["value"])
            .FirstOrDefault();

        var scores = new List<CveScore>();
        var metrics = cve["metrics"] as JsonObject;
        foreach (var (key, version) in new[] { ("cvssMetricV40", "4.0"), ("cvssMetricV31", "3.1"), ("cvssMetricV30", "3.0"), ("cvssMetricV2", "2.0") })
        {
            var metric = (metrics?[key] as JsonArray ?? []).OfType<JsonObject>()
                .OrderByDescending(m => (string?)m["type"] == "Primary")
                .FirstOrDefault();
            var data = metric?["cvssData"];
            if (data?["baseScore"] is not { } score)
            {
                continue;
            }

            var severity = (string?)data["baseSeverity"] ?? (string?)metric!["baseSeverity"] ?? "";
            scores.Add(new CveScore(version, (double)score, severity.ToUpperInvariant(), (string?)data["vectorString"], (string?)metric!["source"]));
        }

        var weaknesses = (cve["weaknesses"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(w => w["description"] as JsonArray ?? [])
            .Select(d => (string?)d?["value"])
            .Where(v => v is not null && v.StartsWith("CWE-", StringComparison.Ordinal))
            .Select(v => v!)
            .Distinct()
            .ToList();

        var references = (cve["references"] as JsonArray ?? []).Select(r => (string?)r?["url"])
            .Where(u => u is not null && u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Select(u => u!)
            .Distinct()
            .Take(30)
            .ToList();

        return new NvdRecord(id, description, ParseDate((string?)cve["published"]), ParseDate((string?)cve["lastModified"]),
            (string?)cve["vulnStatus"], scores, weaknesses, references);
    }

    /// <summary>Hersteller und Produkte aus den CPE-Angaben ("cpe:2.3:a:microsoft:exchange_server:…" → "microsoft exchange server").</summary>
    public static List<string> ParseNvdProducts(JsonObject cve) =>
        (cve["configurations"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(c => c["nodes"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .SelectMany(n => n["cpeMatch"] as JsonArray ?? [])
            .Select(m => (string?)m?["criteria"])
            .Select(c => c?.Split(':'))
            .Where(p => p is { Length: > 4 } && p[3] != "*" && p[4] != "*")
            .Select(p => $"{p![3]} {p[4]}".Replace('_', ' ').Replace("\\", ""))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

    public sealed record OsvRecord(string? Summary, List<string> Aliases, List<SystemFix> Fixes);

    /// <summary>OSV-Eintrag: Fix-Version je Ökosystem (z. B. "Debian:12"), Git-Bereiche werden übergangen.</summary>
    public static OsvRecord ParseOsv(JsonNode root)
    {
        var summary = (string?)root["summary"] ?? (string?)root["details"];
        if (summary is { Length: > 2000 })
        {
            summary = summary[..2000] + " …";
        }

        var aliases = (root["aliases"] as JsonArray ?? []).Concat(root["upstream"] as JsonArray ?? [])
            .Select(a => (string?)a)
            .Where(a => a is not null)
            .Select(a => a!)
            .Distinct()
            .ToList();

        var fixes = new List<SystemFix>();
        foreach (var affected in (root["affected"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var ecosystem = (string?)affected["package"]?["ecosystem"];
            var package = (string?)affected["package"]?["name"];
            if (string.IsNullOrEmpty(ecosystem) || string.IsNullOrEmpty(package))
            {
                continue;
            }

            var ranges = (affected["ranges"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(r => (string?)r["type"] is "ECOSYSTEM" or "SEMVER")
                .ToList();
            if (ranges.Count == 0 && affected["versions"] is null)
            {
                continue;
            }

            var fixedIn = ranges.SelectMany(r => r["events"] as JsonArray ?? [])
                .Select(e => (string?)e?["fixed"])
                .LastOrDefault(f => f is not null);
            fixes.Add(new SystemFix(ecosystem, package, fixedIn, "OSV"));
        }

        return new OsvRecord(summary, aliases, fixes);
    }

    /// <summary>Microsoft Security Update Guide (affectedProduct): KB und Build je Windows-Version.</summary>
    public static List<SystemFix> ParseMsrcAffected(JsonNode root) =>
        (root["value"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(p => (p["kbArticles"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(kb => (Product: (string?)p["product"], Kb: (string?)kb["articleName"], Build: (string?)kb["fixedBuildNumber"])))
            .Where(x => !string.IsNullOrEmpty(x.Product) && !string.IsNullOrEmpty(x.Kb))
            .Select(x => new SystemFix(x.Product!, x.Kb!.All(char.IsDigit) ? "KB" + x.Kb : x.Kb!, string.IsNullOrEmpty(x.Build) ? null : x.Build, "MSRC"))
            .DistinctBy(f => (f.System, f.Package))
            .OrderByDescending(f => f.System.StartsWith("Windows Server", StringComparison.OrdinalIgnoreCase)) // Server zuerst
            .ThenByDescending(f => f.System, StringComparer.OrdinalIgnoreCase) // neueste Version oben
            .Take(80)
            .ToList();

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
