using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent;

/// <summary>HTTP-Client für die Agent-API: gepinnte CA, optional mit Client-Zertifikat (mTLS).</summary>
public sealed class ServerClient : IDisposable
{
    private readonly HttpClient _http;

    public ServerClient(Uri serverUrl, X509Certificate2 pinnedCa, X509Certificate2? clientCertificate)
    {
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

        _http = new HttpClient(handler) { BaseAddress = serverUrl, Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"aiopatcher-agent/{AgentState.AgentVersion}");
    }

    public static ServerClient FromState(AgentState state) =>
        new(new Uri(state.Config.ServerUrl), state.CaCertificate, state.ClientCertificate);

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(AgentProtocol.EnrollPath, request, AgentJsonContext.Default.EnrollRequest, ct);
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

        using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        await using var file = File.Create(target);
        await response.Content.CopyToAsync(file, ct);
    }

    public async Task<HelloResponse> HelloAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(AgentProtocol.HelloPath, ct);
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
        using var request = new HttpRequestMessage(HttpMethod.Get, running is { } id ? $"{PullProtocol.PollPath}?running={id}" : PullProtocol.PollPath);
        request.Headers.Add(PullProtocol.VersionHeader, AgentState.AgentVersion);
        request.Headers.Add(PullProtocol.InstanceHeader, Instance);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(PullJsonContext.Default.PollResponse, ct)
               ?? throw new InvalidDataException("Empty response from server.");
    }

    /// <summary>Log-Zeilen als Teil der Antwort auf einen Auftrag.</summary>
    public async Task SendTaskLogAsync(Guid taskId, JobLogRequest lines, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(PullProtocol.TaskLogPath(taskId), lines, PullJsonContext.Default.JobLogRequest, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Antwort auf einen Auftrag.</summary>
    public async Task<TaskResultResponse> SendTaskResultAsync(Guid taskId, TaskResultRequest result, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(PullProtocol.TaskResultPath(taskId), result, PullJsonContext.Default.TaskResultRequest, ct);
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

    public void Dispose() => _http.Dispose();
}

public sealed class ServerRejectedException(HttpStatusCode status, string body)
    : Exception($"Server responded with {(int)status} {status}: {body}")
{
    public HttpStatusCode Status { get; } = status;
}
