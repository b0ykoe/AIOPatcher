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
               ?? throw new InvalidDataException("Leere Antwort vom Server.");
    }

    public async Task<CheckinResponse> CheckinAsync(CheckinRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(AgentProtocol.CheckinPath, request, AgentJsonContext.Default.CheckinRequest, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AgentJsonContext.Default.CheckinResponse, ct)
               ?? throw new InvalidDataException("Leere Antwort vom Server.");
    }

    public async Task AppendJobLogAsync(Guid jobId, JobLogRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(AgentProtocol.JobLogPath(jobId), request, AgentJsonContext.Default.JobLogRequest, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task SendJobResultAsync(Guid jobId, JobResultRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(AgentProtocol.JobResultPath(jobId), request, AgentJsonContext.Default.JobResultRequest, ct);
        await EnsureSuccessAsync(response, ct);
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
    : Exception($"Server antwortet mit {(int)status} {status}: {body}")
{
    public HttpStatusCode Status { get; } = status;
}
