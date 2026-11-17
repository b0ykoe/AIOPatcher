using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIOPatcher.Server.Options;
using Microsoft.Data.Sqlite;

namespace AIOPatcher.Server.Services.Security;

/// <summary>
/// Lokale CVE-Datenbank aus den NVD-Datenfeeds (https://nvd.nist.gov/vuln/data-feeds): beim ersten Mal alle Jahre
/// (ca. 250 MB), danach alle 2 Stunden der Feed "modified" (Änderungen der letzten 8 Tage). Liegt der letzte Abgleich
/// länger als 7 Tage zurück, werden die Jahres-Feeds erneut verglichen (nur geänderte Dateien werden geladen).
/// Eigene SQLite-Datei unter cache/ mit Volltextsuche (FTS5), getrennt von der AIOPatcher-Datenbank und den Sicherungen,
/// weil sie sich jederzeit neu laden lässt.
/// </summary>
public sealed partial class CveMirror(
    IHttpClientFactory httpFactory,
    AIOPatcherOptions options,
    ILogger<CveMirror> logger,
    FeatureFlags? features = null) : BackgroundService
{
    public const string FeedBaseUrl = "https://nvd.nist.gov/feeds/json/cve/2.0/";
    public const int FirstFeedYear = 2002; // enthält auch 1999–2001

    private const int SchemaVersion = 2;
    private const int CommitEvery = 5000;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(2);
    private static readonly TimeSpan ModifiedFeedCovers = TimeSpan.FromDays(7); // Feed enthält 8 Tage, 1 Tag Puffer

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile Progress? _progress;
    private string? _lastError;
    private DateTime? _lastErrorAt;
    private readonly Lock _schemaLock = new();
    private bool _schemaReady;

    /// <summary>Uhr (Tests); bestimmt auch das letzte Jahr, für das ein Jahres-Feed geladen wird.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public sealed record Progress(string Feed, int Done, int Total, int Imported);

    public sealed record Status(
        bool Enabled, bool Ready, bool Syncing, int Count, DateTime? SyncedAt, DateTime? FullSyncedAt,
        Progress? Current, string? LastError, DateTime? LastErrorAt, long SizeBytes);

    public sealed record Hit(string Id, string? Description, DateTime? Published, double? Score, string? Severity);

    public string DatabasePath => Path.Combine(options.CacheDirectory, "cve-mirror.db");

    public bool Enabled => options.CveMirrorEnabled && (features?.IsOn(Feature.CveLookup) ?? options.CveLookupEnabled);

    /// <summary>
    /// Erster vollständiger Import abgeschlossen: Suche und Details kommen von hier. Gilt auch bei abgeschaltetem
    /// Abgleich, denn das Lesen braucht keine Verbindung nach außen (Stand dann eben älter).
    /// </summary>
    public bool Ready => File.Exists(DatabasePath) && GetMeta("full_at") is not null;

    // --- Hintergrund ----------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (Enabled)
            {
                await SyncAsync(stoppingToken);
            }

            await _wake.WaitAsync(Interval, stoppingToken); // Takt oder "Jetzt abgleichen"
        }
    }

    /// <summary>Abgleich sofort anstoßen (läuft im Hintergrund). false, wenn abgeschaltet.</summary>
    public bool RequestSync()
    {
        if (!Enabled)
        {
            return false;
        }

        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // schon angestoßen
            }
        }

        return true;
    }

    /// <summary>Ein Abgleich. Läuft schon einer, passiert nichts.</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(options.CacheDirectory);
            EnsureSchema();
            var now = UtcNow();
            var lastModified = ParseMeta(GetMeta("modified_at"));
            // Jahres-Feeds vergleichen: beim ersten Mal, nach einer Lücke im Modified-Feed oder wenn zuletzt einer nicht passte
            var needFull = GetMeta("full_at") is null || GetMeta("recheck") == "1" || lastModified is null || now - lastModified > ModifiedFeedCovers;
            var feeds = needFull ? Feeds(now) : [];
            var allComplete = true;
            for (var i = 0; i < feeds.Count; i++)
            {
                allComplete &= await SyncFeedAsync(feeds[i], i, feeds.Count + 1, ct);
            }

            await SyncFeedAsync("modified", feeds.Count, feeds.Count + 1, ct);
            if (needFull)
            {
                SetMeta("full_at", FormatMeta(now)); // auch mit einzelnen Fehlschlägen nutzbar; die werden beim nächsten Lauf nachgeholt
                SetMeta("recheck", allComplete ? "0" : "1");
                Optimize();
            }

            SetMeta("modified_at", FormatMeta(now));
            SetMeta("count", CountRows().ToString(CultureInfo.InvariantCulture));
            _lastError = null;
            _lastErrorAt = null;
            logger.LogInformation("CVE database synced: {Count} CVEs", GetMeta("count"));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _lastError = ex.Message;
            _lastErrorAt = UtcNow();
            logger.LogWarning("CVE database sync failed: {Error}", ex.Message);
        }
        finally
        {
            _progress = null;
            _gate.Release();
        }
    }

    private static List<string> Feeds(DateTime now) =>
        Enumerable.Range(FirstFeedYear, now.Year - FirstFeedYear + 1).Select(y => y.ToString(CultureInfo.InvariantCulture)).ToList();

    private HttpClient Http => httpFactory.CreateClient(CveService.HttpClientName);

    /// <summary>Einen Feed laden, falls sich seine Prüfsumme geändert hat. false = Prüfsumme passte nicht (nächstes Mal erneut).</summary>
    private async Task<bool> SyncFeedAsync(string feed, int index, int total, CancellationToken ct)
    {
        _progress = new Progress(feed, index, total, 0);
        var meta = await GetMetaFileAsync(feed, ct);
        if (meta is null)
        {
            return true; // z. B. Jahres-Feed am 1. Januar noch nicht vorhanden
        }

        if (string.Equals(GetMeta("sha_" + feed), meta.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var temp = Path.Combine(options.CacheDirectory, $"nvdcve-{feed}.json.gz.part");
        try
        {
            using (var response = await Http.GetAsync($"{FeedBaseUrl}nvdcve-2.0-{feed}.json.gz", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(temp);
                await (await response.Content.ReadAsStreamAsync(ct)).CopyToAsync(file, ct);
            }

            var sha = await Task.Run(() => Import(temp, n => _progress = new Progress(feed, index, total, n), ct), ct);
            if (string.Equals(sha, meta.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                SetMeta("sha_" + feed, meta.Sha256);
                return true;
            }

            // z. B. Datei zwischen .meta und Download neu erzeugt
            logger.LogWarning("NVD feed {Feed}: checksum mismatch, will retry next time", feed);
            return false;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public sealed record FeedMeta(string Sha256, DateTime? LastModified);

    private async Task<FeedMeta?> GetMetaFileAsync(string feed, CancellationToken ct)
    {
        using var response = await Http.GetAsync($"{FeedBaseUrl}nvdcve-2.0-{feed}.meta", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return ParseFeedMeta(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>.meta-Datei: "lastModifiedDate:…", "sha256:…" (Prüfsumme der entpackten JSON-Datei).</summary>
    public static FeedMeta? ParseFeedMeta(string text)
    {
        string? sha = null;
        DateTime? modified = null;
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon];
            var value = line[(colon + 1)..].Trim();
            if (key == "sha256" && value.Length == 64)
            {
                sha = value.ToUpperInvariant();
            }
            else if (key == "lastModifiedDate" && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                modified = d.UtcDateTime;
            }
        }

        return sha is null ? null : new FeedMeta(sha, modified);
    }

    /// <summary>Feed (gz) einlesen; Rückgabe: SHA-256 des entpackten Inhalts.</summary>
    public string Import(string gzPath, Action<int>? progress = null, CancellationToken ct = default)
    {
        EnsureSchema();
        using var file = File.OpenRead(gzPath);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var sha = SHA256.Create();
        using var hashing = new CryptoStream(gz, sha, CryptoStreamMode.Read);

        using var connection = Open();
        var transaction = connection.BeginTransaction();
        // unverändert (gleiches lastModified) = keine Änderung, auch nicht am Suchindex
        using var upsert = connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO cve (rowid, id, published, modified, status, score, severity, data)
            VALUES ($key, $id, $published, $modified, $status, $score, $severity, $data)
            ON CONFLICT(rowid) DO UPDATE SET
              id = excluded.id, published = excluded.published, modified = excluded.modified, status = excluded.status,
              score = excluded.score, severity = excluded.severity, data = excluded.data
            WHERE excluded.modified IS NOT cve.modified
            """;
        var parameters = new[] { "$key", "$id", "$published", "$modified", "$status", "$score", "$severity", "$data" }
            .ToDictionary(n => n, n => upsert.Parameters.Add(new SqliteParameter { ParameterName = n }));
        using var ftsDelete = connection.CreateCommand();
        ftsDelete.CommandText = "DELETE FROM cve_fts WHERE rowid = $key";
        var ftsDeleteKey = ftsDelete.Parameters.Add(new SqliteParameter { ParameterName = "$key" });
        using var ftsInsert = connection.CreateCommand();
        ftsInsert.CommandText = "INSERT INTO cve_fts (rowid, description, products) VALUES ($key, $description, $products)";
        var ftsKey = ftsInsert.Parameters.Add(new SqliteParameter { ParameterName = "$key" });
        var ftsDescription = ftsInsert.Parameters.Add(new SqliteParameter { ParameterName = "$description" });
        var ftsProducts = ftsInsert.Parameters.Add(new SqliteParameter { ParameterName = "$products" });
        void Bind(SqliteTransaction t) => upsert.Transaction = ftsDelete.Transaction = ftsInsert.Transaction = t;
        Bind(transaction);

        var imported = 0;
        try
        {
            NvdFeedReader.Read(hashing, cve =>
            {
                ct.ThrowIfCancellationRequested();
                if (CveLookupService.ParseNvdCve(cve) is not { } record || CveMapping.CveKey(record.Id) is not { } key)
                {
                    return;
                }

                var best = record.Scores.FirstOrDefault();
                parameters["$key"].Value = key;
                parameters["$id"].Value = record.Id;
                parameters["$published"].Value = (object?)(string?)cve["published"] ?? DBNull.Value;
                parameters["$modified"].Value = (object?)(string?)cve["lastModified"] ?? DBNull.Value;
                parameters["$status"].Value = (object?)record.Status ?? DBNull.Value;
                parameters["$score"].Value = (object?)best?.BaseScore ?? DBNull.Value;
                parameters["$severity"].Value = (object?)best?.Severity ?? DBNull.Value;
                parameters["$data"].Value = Pack(new Packed(record.Description, record.Scores, record.Weaknesses, record.References));
                if (upsert.ExecuteNonQuery() > 0)
                {
                    ftsDeleteKey.Value = ftsKey.Value = key;
                    ftsDelete.ExecuteNonQuery();
                    ftsDescription.Value = (object?)record.Description ?? DBNull.Value;
                    ftsProducts.Value = string.Join(" | ", CveLookupService.ParseNvdProducts(cve));
                    ftsInsert.ExecuteNonQuery();
                }

                if (++imported % CommitEvery == 0)
                {
                    transaction.Commit();
                    transaction.Dispose();
                    transaction = connection.BeginTransaction();
                    Bind(transaction);
                    progress?.Invoke(imported);
                }
            });

            transaction.Commit();
        }
        finally
        {
            transaction.Dispose();
        }

        progress?.Invoke(imported);
        return Convert.ToHexString(sha.Hash ?? []);
    }

    // --- Abfragen -------------------------------------------------------------------------------------

    /// <summary>Eine CVE aus der lokalen Datenbank (null = nicht vorhanden oder Datenbank nicht bereit).</summary>
    public CveLookupService.NvdRecord? Get(string id)
    {
        if (!Ready || CveMapping.CveKey(id) is not { } key)
        {
            return null;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, published, modified, status, data FROM cve WHERE rowid = $key";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var packed = Unpack((byte[])reader["data"]);
        return new CveLookupService.NvdRecord(
            reader.GetString(0), packed.Description, ParseFeedDate(reader, 1), ParseFeedDate(reader, 2),
            reader.IsDBNull(3) ? null : reader.GetString(3), packed.Scores, packed.Weaknesses, packed.References);
    }

    /// <summary>Kurzinfos zu mehreren CVEs (Beschreibung, Bewertung), z. B. für Treffer aus den eigenen Hosts.</summary>
    public Dictionary<string, Hit> GetMany(IEnumerable<string> ids)
    {
        var keys = ids.Select(CveMapping.CveKey).OfType<long>().Distinct().Take(500).ToList();
        if (keys.Count == 0 || !Ready)
        {
            return [];
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, data, published, score, severity FROM cve WHERE rowid IN ({string.Join(',', keys)})";
        return ReadHits(command).ToDictionary(h => h.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Volltextsuche in Beschreibung und betroffenen Produkten; neueste zuerst, ohne zurückgezogene CVEs.
    /// Mehrere Wörter: zuerst als zusammenhängender Ausdruck ("exchange server" → Microsoft Exchange Server),
    /// nur ohne Treffer dann alle Wörter irgendwo.
    /// </summary>
    public (List<Hit> Hits, int Total) Search(string query, int limit)
    {
        if (!Ready || BuildMatch(query) is not { } match)
        {
            return ([], 0);
        }

        if (BuildMatch(query, phrase: true) is { } phrase && phrase != match && SearchMatch(phrase, limit) is { Total: > 0 } exact)
        {
            return exact;
        }

        return SearchMatch(match, limit);
    }

    private (List<Hit> Hits, int Total) SearchMatch(string match, int limit)
    {
        using var connection = Open();
        using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT count(*) FROM cve_fts JOIN cve ON cve.rowid = cve_fts.rowid
            WHERE cve_fts MATCH $match AND (cve.status IS NULL OR cve.status <> 'Rejected')
            """;
        count.Parameters.AddWithValue("$match", match);
        var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (total == 0)
        {
            return ([], 0);
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cve.id, cve.data, cve.published, cve.score, cve.severity
            FROM cve_fts JOIN cve ON cve.rowid = cve_fts.rowid
            WHERE cve_fts MATCH $match AND (cve.status IS NULL OR cve.status <> 'Rejected')
            ORDER BY cve.published DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", limit);
        return (ReadHits(command).ToList(), total);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Suchbegriffe als FTS5-Ausdruck, ohne FTS-Syntax vom Benutzer: jedes Wort in Anführungszeichen, alle müssen
    /// vorkommen; mit phrase = true alle Wörter als ein zusammenhängender Ausdruck.
    /// </summary>
    public static string? BuildMatch(string query, bool phrase = false)
    {
        var terms = Whitespace().Split(query.Trim())
            .Select(t => t.Replace("\"", "", StringComparison.Ordinal))
            .Where(t => t.Any(char.IsLetterOrDigit))
            .Take(8)
            .ToList();
        return terms.Count == 0 ? null
            : phrase ? $"\"{string.Join(' ', terms)}\""
            : string.Join(' ', terms.Select(t => $"\"{t}\""));
    }

    private static IEnumerable<Hit> ReadHits(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new Hit(reader.GetString(0), Unpack((byte[])reader[1]).Description, ParseFeedDate(reader, 2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.IsDBNull(4) ? null : reader.GetString(4));
        }
    }

    public Status GetStatus()
    {
        var enabled = Enabled;
        var exists = File.Exists(DatabasePath);
        var size = exists
            ? new[] { DatabasePath, DatabasePath + "-wal" }.Where(File.Exists).Sum(f => new FileInfo(f).Length)
            : 0;
        return new Status(
            enabled,
            exists && GetMeta("full_at") is not null,
            _progress is not null,
            exists && int.TryParse(GetMeta("count"), CultureInfo.InvariantCulture, out var count) ? count : 0,
            exists ? ParseMeta(GetMeta("modified_at")) : null,
            exists ? ParseMeta(GetMeta("full_at")) : null,
            _progress,
            _lastError,
            _lastErrorAt,
            size);
    }

    private int CountRows()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM cve";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // --- Speicher -------------------------------------------------------------------------------------

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = true }.ToString());
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        lock (_schemaLock)
        {
            if (!_schemaReady)
            {
                CreateSchema();
                _schemaReady = true;
            }
        }
    }

    private void CreateSchema()
    {
        Directory.CreateDirectory(options.CacheDirectory);
        using var connection = Open();
        Exec(connection, "PRAGMA journal_mode = WAL; CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT)");
        if (ReadMeta(connection, "schema") != SchemaVersion.ToString(CultureInfo.InvariantCulture))
        {
            // neu aufbauen; die Daten lassen sich jederzeit wieder laden
            Exec(connection, """
                DROP TABLE IF EXISTS cve_fts; DROP TABLE IF EXISTS cve; DELETE FROM meta;
                CREATE TABLE cve (
                  rowid INTEGER PRIMARY KEY, id TEXT NOT NULL, published TEXT, modified TEXT, status TEXT,
                  score REAL, severity TEXT, data BLOB);
                CREATE VIRTUAL TABLE cve_fts USING fts5(description, products, content = '', contentless_delete = 1);
                """);
            WriteMeta(connection, "schema", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Nach dem großen Import: Volltextindex zusammenfassen, Datei verdichten (halb leere Seiten), WAL leeren.</summary>
    private void Optimize()
    {
        using var connection = Open();
        Exec(connection, "INSERT INTO cve_fts (cve_fts) VALUES ('optimize')");
        Exec(connection, "VACUUM");
        Exec(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
    }

    private string? GetMeta(string key)
    {
        if (!File.Exists(DatabasePath))
        {
            return null;
        }

        EnsureSchema();
        using var connection = Open();
        return ReadMeta(connection, key);
    }

    private void SetMeta(string key, string value)
    {
        using var connection = Open();
        WriteMeta(connection, key, value);
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string FormatMeta(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime? ParseMeta(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static DateTime? ParseFeedDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ParseMeta(reader.GetString(ordinal));

    // Beschreibung, Bewertungen, CWE und Verweise komprimiert in einer Spalte (spart gut die Hälfte); der Suchindex
    // braucht den Klartext nicht (contentless), gelesen wird nur für Treffer
    private sealed record Packed(string? Description, List<CveLookupService.CveScore> Scores, List<string> Weaknesses, List<string> References);

    private static byte[] Pack(Packed value)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal))
        {
            JsonSerializer.Serialize(brotli, value);
        }

        return output.ToArray();
    }

    private static Packed Unpack(byte[] data)
    {
        using var brotli = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Packed>(brotli) ?? new Packed(null, [], [], []);
    }
}
