using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

public sealed class CveMirrorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiop-cvemirror-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFeeds _feeds = new();
    private readonly CveMirror _mirror;

    public CveMirrorTests()
    {
        Directory.CreateDirectory(_dir);
        _mirror = new CveMirror(new FakeFactory(_feeds), new AIOPatcherOptions { DataDirectory = _dir }, NullLogger<CveMirror>.Instance)
        {
            UtcNow = () => new DateTime(2003, 6, 1, 12, 0, 0, DateTimeKind.Utc),
        };
    }

    public void Dispose()
    {
        _mirror.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    // Vorlagen mit ' statt " (lesbarer), Platzhalter in Großbuchstaben
    private static string Cve(string id, string description, string modified = "2024-01-01T00:00:00.000", string status = "Analyzed", string? cpe = null, double score = 7.5) =>
        ("{'cve':{'id':'ID','published':'2003-01-02T10:00:00.000','lastModified':'MOD','vulnStatus':'STATUS',"
         + "'descriptions':[{'lang':'en','value':'DESC'}],"
         + "'metrics':{'cvssMetricV31':[{'source':'nvd@nist.gov','type':'Primary','cvssData':{'baseScore':SCORE,'baseSeverity':'HIGH','vectorString':'CVSS:3.1/AV:N'}}]},"
         + "'weaknesses':[{'description':[{'lang':'en','value':'CWE-79'}]}],"
         + "'configurations':[{'nodes':[{'cpeMatch':[{'criteria':'CPE'}]}]}],"
         + "'references':[{'url':'https://example.org/ID'}]}}")
        .Replace('\'', '"')
        .Replace("ID", id).Replace("MOD", modified).Replace("STATUS", status)
        .Replace("SCORE", score.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("CPE", cpe ?? "cpe:2.3:a:apache:http_server:2.0:*:*:*:*:*:*:*")
        .Replace("DESC", description);

    private static string Feed(params string[] entries) =>
        "{\"resultsPerPage\":1,\"format\":\"NVD_CVE\",\"version\":\"2.0\",\"vulnerabilities\":["
        + string.Join(",", entries)
        + "],\"timestamp\":\"2026-11-17T00:00:00\"}";

    [Fact]
    public void FeedReader_ReadsEntriesAcrossTinyBuffers()
    {
        var json = Feed(Cve("CVE-2002-0001", "first {with} \\\"braces\\\""), Cve("CVE-2002-0002", "second [array]"), Cve("CVE-2002-0003", "third"));
        var ids = new List<string>();

        var count = NvdFeedReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)), cve => ids.Add((string)cve["id"]!), bufferSize: 16);

        Assert.Equal(3, count);
        Assert.Equal(["CVE-2002-0001", "CVE-2002-0002", "CVE-2002-0003"], ids);
    }

    [Fact]
    public void FeedReader_TruncatedFeed_Throws()
    {
        var json = Feed(Cve("CVE-2002-0001", "a"), Cve("CVE-2002-0002", "b"));
        var bytes = Encoding.UTF8.GetBytes(json)[..(json.Length - 80)];

        Assert.ThrowsAny<Exception>(() => NvdFeedReader.Read(new MemoryStream(bytes), _ => { }, bufferSize: 64));
    }

    [Fact]
    public void ParseFeedMeta_ReadsChecksum()
    {
        var meta = CveMirror.ParseFeedMeta("lastModifiedDate:2026-11-17T06:00:16-05:00\r\nsize:72950849\r\ngzSize:6095156\r\nsha256:818dc2b32e8362b6177b677b17e0555b4a2d719a5d91c3acdf1c2b0a1a68af23\r\n");

        Assert.NotNull(meta);
        Assert.Equal("818DC2B32E8362B6177B677B17E0555B4A2D719A5D91C3ACDF1C2B0A1A68AF23", meta.Sha256);
        Assert.Equal(new DateTime(2026, 11, 17, 11, 0, 16, DateTimeKind.Utc), meta.LastModified);
        Assert.Null(CveMirror.ParseFeedMeta("<html>error</html>"));
    }

    [Fact]
    public void ParseNvdProducts_VendorAndProductFromCpe()
    {
        var cve = (JsonObject)JsonNode.Parse(Cve("CVE-2021-26855", "x", cpe: "cpe:2.3:a:microsoft:exchange_server:2016:cumulative_update_18:*:*:*:*:*:*"))!["cve"]!;

        Assert.Equal(["microsoft exchange server"], CveLookupService.ParseNvdProducts(cve));
    }

    [Theory]
    [InlineData("openssl", "\"openssl\"")]
    [InlineData("  apache   http ", "\"apache\" \"http\"")]
    [InlineData("x\" OR \"y NEAR(", "\"x\" \"OR\" \"y\" \"NEAR(\"")]
    [InlineData("\" * -", null)]
    public void BuildMatch_QuotesEveryTerm(string query, string? expected) => Assert.Equal(expected, CveMirror.BuildMatch(query));

    [Fact]
    public async Task Sync_ImportsYears_ThenSearchAndGetWork()
    {
        _feeds.Set("2002", Feed(Cve("CVE-2002-0001", "Buffer overflow in Apache HTTP Server"), Cve("CVE-2002-0002", "Withdrawn entry", status: "Rejected")));
        _feeds.Set("2003", Feed(Cve("CVE-2003-1234", "XSS in OpenSSL web console", cpe: "cpe:2.3:a:openssl:openssl:0.9:*:*:*:*:*:*:*", score: 9.8)));
        _feeds.Set("modified", Feed());
        Assert.False(_mirror.Ready);

        await _mirror.SyncAsync(CancellationToken.None);

        var status = _mirror.GetStatus();
        Assert.True(status.Ready, status.LastError);
        Assert.Equal(3, status.Count);

        var record = _mirror.Get("CVE-2003-1234");
        Assert.NotNull(record);
        Assert.Equal("XSS in OpenSSL web console", record.Description);
        Assert.Equal(9.8, Assert.Single(record.Scores).BaseScore);
        Assert.Equal(["CWE-79"], record.Weaknesses);
        Assert.Equal(["https://example.org/CVE-2003-1234"], record.References);

        var (hits, total) = _mirror.Search("apache", 10); // Treffer über Produkt (CPE) und Beschreibung
        Assert.Equal(1, total); // zurückgezogene CVE (ebenfalls Apache-CPE) zählt nicht
        Assert.Equal("CVE-2002-0001", Assert.Single(hits).Id);
        Assert.Equal("CVE-2003-1234", Assert.Single(_mirror.Search("openssl", 10).Hits).Id);
        Assert.Empty(_mirror.Search("openssl apache", 10).Hits); // alle Wörter müssen vorkommen
        Assert.Equal("\"apache http\"", CveMirror.BuildMatch("apache http", phrase: true));
        Assert.Equal(2, _mirror.GetMany(["CVE-2002-0001", "CVE-2003-1234", "CVE-2099-0001"]).Count);
    }

    [Fact]
    public async Task Search_PrefersWordsAsPhrase_FallsBackToAllWords()
    {
        _feeds.Set("2002", Feed(
            Cve("CVE-2002-0001", "Flaw in the key exchange of an SSH server", cpe: "cpe:2.3:a:openbsd:openssh:1.0:*:*:*:*:*:*:*"),
            Cve("CVE-2002-0002", "Remote code execution in Exchange", cpe: "cpe:2.3:a:microsoft:exchange_server:2000:*:*:*:*:*:*:*")));
        _feeds.Set("2003", Feed());
        _feeds.Set("modified", Feed());
        await _mirror.SyncAsync(CancellationToken.None);

        Assert.Equal(["CVE-2002-0002"], _mirror.Search("exchange server", 10).Hits.Select(h => h.Id)); // nur der Ausdruck
        Assert.Equal(["CVE-2002-0001"], _mirror.Search("ssh exchange", 10).Hits.Select(h => h.Id)); // kein Ausdruck → alle Wörter
    }

    [Fact]
    public async Task Sync_SkipsUnchangedFeeds_AppliesModifiedFeed()
    {
        _feeds.Set("2002", Feed(Cve("CVE-2002-0001", "Old text about sendmail")));
        _feeds.Set("2003", Feed());
        _feeds.Set("modified", Feed());
        await _mirror.SyncAsync(CancellationToken.None);
        _feeds.Downloads.Clear();

        // Änderung kommt über den Modified-Feed; Jahres-Feeds werden nicht erneut geladen
        _feeds.Set("modified", Feed(Cve("CVE-2002-0001", "New text about postfix", modified: "2026-11-17T00:00:00.000")));
        await _mirror.SyncAsync(CancellationToken.None);

        Assert.Equal(["modified"], _feeds.Downloads);
        Assert.Equal("New text about postfix", _mirror.Get("CVE-2002-0001")!.Description);
        Assert.Empty(_mirror.Search("sendmail", 10).Hits); // Volltextindex ohne alten Text
        Assert.Single(_mirror.Search("postfix", 10).Hits);
        Assert.Equal(1, _mirror.GetStatus().Count);

        // unveränderter Modified-Feed: nichts laden
        _feeds.Downloads.Clear();
        await _mirror.SyncAsync(CancellationToken.None);
        Assert.Empty(_feeds.Downloads);
    }

    [Fact]
    public async Task Sync_ChecksumMismatch_RetriesNextTime()
    {
        _feeds.Set("2002", Feed(Cve("CVE-2002-0001", "a")));
        _feeds.Set("2003", Feed());
        _feeds.Set("modified", Feed());
        _feeds.BreakChecksum("2002");

        await _mirror.SyncAsync(CancellationToken.None);
        _feeds.Downloads.Clear();
        await _mirror.SyncAsync(CancellationToken.None);

        Assert.Contains("2002", _feeds.Downloads); // Prüfsumme nicht gemerkt, also erneut geladen
    }

    [Fact]
    public async Task Sync_FeedUnreachable_ReportsErrorAndStaysNotReady()
    {
        _feeds.Set("modified", Feed());

        await _mirror.SyncAsync(CancellationToken.None); // 2002/2003 fehlen → 404 → übersprungen, kein Fehler

        Assert.True(_mirror.Ready);
        _feeds.Fail = true;
        await _mirror.SyncAsync(CancellationToken.None);
        Assert.NotNull(_mirror.GetStatus().LastError);
    }

    // --- NVD-Feeds im Speicher --------------------------------------------------------------------------

    private sealed class FakeFeeds : HttpMessageHandler
    {
        private readonly Dictionary<string, (byte[] Gz, string Sha)> _feeds = [];
        public List<string> Downloads { get; } = [];
        public bool Fail { get; set; }

        public void Set(string feed, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            using var output = new MemoryStream();
            using (var gz = new GZipStream(output, CompressionLevel.Fastest))
            {
                gz.Write(bytes);
            }

            _feeds[feed] = (output.ToArray(), Convert.ToHexString(SHA256.HashData(bytes)));
        }

        public void BreakChecksum(string feed) => _feeds[feed] = (_feeds[feed].Gz, new string('0', 64));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var name = request.RequestUri!.Segments[^1]; // nvdcve-2.0-2002.meta bzw. .json.gz
            var feed = name.Replace("nvdcve-2.0-", "").Replace(".json.gz", "").Replace(".meta", "");
            if (!_feeds.TryGetValue(feed, out var data))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (name.EndsWith(".meta", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"lastModifiedDate:2026-11-17T06:00:16-05:00\r\nsha256:{data.Sha}\r\n"),
                });
            }

            Downloads.Add(feed);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data.Gz) });
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
