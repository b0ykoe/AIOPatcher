using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization.Metadata;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent;

/// <summary>
/// HTTP-Client für die Agent-API über zwei Wege:
/// <list type="bullet">
/// <item><b>Direkt</b> (interne Adresse): gepinnte CA, Client-Zertifikat (mTLS).</item>
/// <item><b>Öffentlich</b> (Reverse Proxy, z. B. hinter Cloudflare): normales TLS des Proxys, jede Anfrage mit dem
/// Schlüssel des Client-Zertifikats signiert, jede Antwort gegen die gepinnte CA geprüft (<see cref="PublicChannel"/>).</item>
/// </list>
/// Erst direkt; ist die interne Adresse nicht erreichbar, automatisch öffentlich und dort bleiben, direkt alle
/// 15 Minuten wieder versuchen.
/// </summary>
public sealed class ServerClient : IDisposable
{
    public static readonly TimeSpan RetryDirectAfter = TimeSpan.FromMinutes(15);

    /// <summary>Prozessweit: bis wann der öffentliche Weg zuerst genommen wird (direkt war zuletzt nicht erreichbar).</summary>
    private static DateTime _preferPublicUntil = DateTime.MinValue;

    private readonly Uri _direct;
    private readonly X509Certificate2 _ca;
    private readonly X509Certificate2? _clientCertificate;
    private readonly HttpClient _directHttp;
    private HttpClient? _publicHttp;
    private Uri? _public;

    public ServerClient(Uri serverUrl, X509Certificate2 pinnedCa, X509Certificate2? clientCertificate, Uri? publicUrl = null)
    {
        _direct = serverUrl;
        _ca = pinnedCa;
        _clientCertificate = clientCertificate;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) => CertificateHelper.ValidateServer(pinnedCa, cert, errors);
        if (clientCertificate is not null)
        {
            handler.SslOptions.ClientCertificates = [clientCertificate];
        }

        _directHttp = NewClient(handler, serverUrl);
        UsePublicUrl(publicUrl);
    }

    public static ServerClient FromState(AgentState state) =>
        new(new Uri(state.Config.ServerUrl), state.CaCertificate, state.ClientCertificate,
            state.Config.PublicUrl is { Length: > 0 } p && Uri.TryCreate(p, UriKind.Absolute, out var u) ? u : null);

    /// <summary>Über welchen Weg ging die letzte Anfrage? (für Log und Selbsttest)</summary>
    public bool LastUsedPublic { get; private set; }

    public Uri? PublicUrl => _public;

    /// <summary>Öffentliche Adresse setzen oder wechseln (der Server meldet sie bei jeder Abfrage mit).</summary>
    public void UsePublicUrl(Uri? url)
    {
        if (url is not null && url.Scheme != Uri.UriSchemeHttps)
        {
            url = null;
        }

        if (url == _public)
        {
            return;
        }

        _publicHttp?.Dispose();
        _public = url;
        _publicHttp = url is null
            ? null
            : NewClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.All }, url);
    }

    private static HttpClient NewClient(SocketsHttpHandler handler, Uri baseAddress)
    {
        var http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"aiopatcher-agent/{AgentState.AgentVersion}");
        return http;
    }

    // --- Weg wählen -------------------------------------------------------------------------------

    /// <param name="bulk">Großer Download, Inhalt wird separat geprüft (Update-Signatur, SHA-256): Antwort nicht puffern.</param>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[]? body, IReadOnlyDictionary<string, string>? headers, bool bulk, CancellationToken ct)
    {
        var publicFirst = _public is not null && DateTime.UtcNow < _preferPublicUntil;
        if (!publicFirst)
        {
            try
            {
                var response = await SendDirectAsync(method, path, body, headers, bulk, ct);
                LastUsedPublic = false;
                return response;
            }
            catch (Exception ex) when (_public is not null && !ct.IsCancellationRequested && IsUnreachable(ex))
            {
                _preferPublicUntil = DateTime.UtcNow + RetryDirectAfter;
            }
        }

        try
        {
            var response = await SendPublicAsync(method, path, body, headers, bulk, ct);
            LastUsedPublic = true;
            return response;
        }
        catch (Exception ex) when (publicFirst && !ct.IsCancellationRequested && IsUnreachable(ex))
        {
            // öffentlich gerade weg: doch wieder direkt versuchen
            _preferPublicUntil = DateTime.MinValue;
            var response = await SendDirectAsync(method, path, body, headers, bulk, ct);
            LastUsedPublic = false;
            return response;
        }
    }

    /// <summary>Netz/Name/TLS/Zeitüberschreitung: Weg nicht erreichbar. HTTP-Fehler des Servers zählen nicht dazu.</summary>
    public static bool IsUnreachable(Exception ex) => ex is HttpRequestException { StatusCode: null } or TaskCanceledException or TimeoutException;

    private Task<HttpResponseMessage> SendDirectAsync(HttpMethod method, string path, byte[]? body, IReadOnlyDictionary<string, string>? headers, bool bulk, CancellationToken ct) =>
        _directHttp.SendAsync(Build(method, path, body, headers), bulk ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);

    private async Task<HttpResponseMessage> SendPublicAsync(HttpMethod method, string path, byte[]? body, IReadOnlyDictionary<string, string>? headers, bool bulk, CancellationToken ct)
    {
        var request = Build(method, path, body, headers);
        var nonce = PublicChannel.NewNonce();
        request.Headers.Add(PublicChannel.NonceHeader, nonce);
        if (_clientCertificate is not null)
        {
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var key = _clientCertificate.GetECDsaPrivateKey() ?? throw new CryptographicException("Client certificate has no ECDSA key.");
            request.Headers.Add(PublicChannel.CertHeader, Convert.ToBase64String(_clientCertificate.RawData));
            request.Headers.Add(PublicChannel.TimeHeader, time.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add(PublicChannel.SignatureHeader, PublicChannel.Sign(key, PublicChannel.RequestToSign(method.Method, path, time, nonce, body ?? [])));
        }

        var response = await _publicHttp!.SendAsync(request, bulk ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
        if (bulk)
        {
            return response;
        }

        var content = await response.Content.ReadAsByteArrayAsync(ct);
        if (!VerifyResponse(_ca, nonce, (int)response.StatusCode, content, Header(response, PublicChannel.ServerCertHeader), Header(response, PublicChannel.ResponseSignatureHeader)))
        {
            var status = response.StatusCode;
            response.Dispose();
            // Kein Netzfehler: Proxy liefert etwas, das nicht vom Server kommt (falsche Weiterleitung, Fehlerseite, Manipulation)
            throw new ServerRejectedException(HttpStatusCode.BadGateway,
                $"Response via {_public} is not signed by the AIOPatcher server (HTTP {(int)status}). Check the reverse proxy forwards to the proxy port.");
        }

        var replaced = new ByteArrayContent(content);
        foreach (var h in response.Content.Headers)
        {
            replaced.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        response.Content = replaced;
        return response;
    }

    /// <summary>Antwort-Signatur: Server-Zertifikat von der gepinnten CA (serverAuth), Signatur über Nonce, Status und Inhalt.</summary>
    public static bool VerifyResponse(X509Certificate2 pinnedCa, string nonce, int status, byte[] body, string? serverCertBase64, string? signature)
    {
        if (string.IsNullOrEmpty(serverCertBase64) || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        try
        {
            using var server = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(serverCertBase64));
            var isServerCert = server.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1"));
            if (!isServerCert || !CertificateHelper.ValidateServer(pinnedCa, server, System.Net.Security.SslPolicyErrors.None))
            {
                return false;
            }

            using var key = server.GetECDsaPublicKey();
            return key is not null && PublicChannel.Verify(key, PublicChannel.ResponseToSign(nonce, status, body), signature);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static HttpRequestMessage Build(HttpMethod method, string path, byte[]? body, IReadOnlyDictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.Add(name, value);
        }

        return request;
    }

    private Task<HttpResponseMessage> PostJsonAsync<T>(string path, T value, JsonTypeInfo<T> type, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, type), null, bulk: false, ct);

    // --- Aufrufe ---------------------------------------------------------------------------------

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct)
    {
        using var response = await PostJsonAsync(AgentProtocol.EnrollPath, request, AgentJsonContext.Default.EnrollRequest, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AgentJsonContext.Default.EnrollResponse, ct)
               ?? throw new InvalidDataException("Empty response from server.");
    }

    /// <summary>Lädt eine Datei vom Server (nur relative Pfade unter /agent/).</summary>
    public async Task DownloadAsync(string path, string target, CancellationToken ct)
    {
        if (!path.StartsWith("/agent/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unexpected download path.");
        }

        using var response = await SendAsync(HttpMethod.Get, path, null, null, bulk: true, ct);
        await EnsureSuccessAsync(response, ct);
        await using var file = File.Create(target);
        await response.Content.CopyToAsync(file, ct);
    }

    public async Task<HelloResponse> HelloAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, AgentProtocol.HelloPath, null, null, bulk: false, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AgentJsonContext.Default.HelloResponse, ct)
               ?? throw new InvalidDataException("Empty response from server.");
    }

    // --- Pull: abholen und antworten. Andere Aufrufe hat der Agent nicht (außer Registrierung und Downloads). ---

    /// <summary>Zufällig je Prozessstart; der Server erkennt daran einen Neustart (keine weiteren Daten).</summary>
    private static readonly string Instance = Guid.NewGuid().ToString("N");

    /// <summary>Auftrag abholen. running: Auftrag, den der Agent noch bearbeitet (z. B. über einen Neustart hinweg).</summary>
    public async Task<PollResponse> PollAsync(Guid? running, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { [PullProtocol.VersionHeader] = AgentState.AgentVersion, [PullProtocol.InstanceHeader] = Instance };
        using var response = await SendAsync(HttpMethod.Get, running is { } id ? $"{PullProtocol.PollPath}?running={id}" : PullProtocol.PollPath, null, headers, bulk: false, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(PullJsonContext.Default.PollResponse, ct)
               ?? throw new InvalidDataException("Empty response from server.");
    }

    /// <summary>Log-Zeilen als Teil der Antwort auf einen Auftrag.</summary>
    public async Task SendTaskLogAsync(Guid taskId, JobLogRequest lines, CancellationToken ct)
    {
        using var response = await PostJsonAsync(PullProtocol.TaskLogPath(taskId), lines, PullJsonContext.Default.JobLogRequest, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Antwort auf einen Auftrag.</summary>
    public async Task<TaskResultResponse> SendTaskResultAsync(Guid taskId, TaskResultRequest result, CancellationToken ct)
    {
        using var response = await PostJsonAsync(PullProtocol.TaskResultPath(taskId), result, PullJsonContext.Default.TaskResultRequest, ct);
        await EnsureSuccessAsync(response, ct);
        return response.Content.Headers.ContentLength is 0
            ? new TaskResultResponse()
            : await response.Content.ReadFromJsonAsync(PullJsonContext.Default.TaskResultResponse, ct) ?? new TaskResultResponse();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ServerRejectedException(response.StatusCode, body.Length > 500 ? body[..500] : body);
    }

    public void Dispose()
    {
        _directHttp.Dispose();
        _publicHttp?.Dispose();
    }
}

public sealed class ServerRejectedException(HttpStatusCode status, string body)
    : Exception($"Server responded with {(int)status} {status}: {body}")
{
    public HttpStatusCode Status { get; } = status;
}
