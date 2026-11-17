using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AIOPatcher.Agent;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

/// <summary>Öffentlicher Weg über einen Reverse Proxy: signierte Anfragen, signierte Antworten, versiegelte Geheimnisse.</summary>
public sealed class PublicChannelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiopatcher-tests", Guid.NewGuid().ToString("N"));
    private readonly CertificateAuthority _ca;
    private readonly ECDsa _agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _agentCert;

    public PublicChannelTests()
    {
        _ca = CertificateAuthority.LoadOrCreate(_dir, "10.0.0.5", NullLogger.Instance);
        var csr = new CertificateRequest("CN=vps1", _agentKey, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        using var issued = _ca.IssueClientCertificate(csr, Guid.NewGuid(), Guid.NewGuid());
        _agentCert = X509CertificateLoader.LoadCertificate(issued.RawData).CopyWithPrivateKey(_agentKey);
    }

    public void Dispose()
    {
        _agentCert.Dispose();
        _agentKey.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Signature_CoversMethodPathTimeNonceAndBody()
    {
        var data = PublicChannel.RequestToSign("POST", "/agent/v2/tasks/1/result", 1000, "abc", "{\"a\":1}"u8);
        var signature = PublicChannel.Sign(_agentKey, data);
        Assert.True(PublicChannel.Verify(_agentKey, data, signature));
        Assert.False(PublicChannel.Verify(_agentKey, PublicChannel.RequestToSign("POST", "/agent/v2/tasks/1/result", 1000, "abc", "{\"a\":2}"u8), signature));
        Assert.False(PublicChannel.Verify(_agentKey, PublicChannel.RequestToSign("GET", "/agent/v2/tasks/1/result", 1000, "abc", "{\"a\":1}"u8), signature));
        Assert.False(PublicChannel.Verify(_agentKey, data, "kaputt"));
        Assert.True(PublicChannel.IsFresh(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, DateTimeOffset.UtcNow));
        Assert.False(PublicChannel.IsFresh(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Secret_SealedForAgentKeyOnly()
    {
        using var publicKey = _agentCert.GetECDsaPublicKey()!;
        var sealedText = PublicChannel.Seal("Geheim-Passwort ä!", publicKey);
        Assert.StartsWith(PublicChannel.SealedPrefix, sealedText);
        Assert.DoesNotContain("Geheim", sealedText);
        Assert.Equal("Geheim-Passwort ä!", PublicChannel.Open(sealedText, _agentKey));
        Assert.Equal("plain", PublicChannel.Open("plain", _agentKey)); // interner Weg: unverändert

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.ThrowsAny<CryptographicException>(() => PublicChannel.Open(sealedText, other));
    }

    [Fact]
    public void Task_SecretsSealed_IncludingBatchSteps()
    {
        var change = new SystemChange(SystemChangeKind.Batch, Steps: [new SystemChange(SystemChangeKind.UserSetPassword, UserName: "admin", Secret: "s3cret")]);
        var task = new AgentTask(Guid.NewGuid(), AgentModules.Users, ModuleActions.Change, PullJsonContext.ToElement(change, PullJsonContext.Default.SystemChange));

        var sealedTask = PullEndpoints.SealSecrets(task, _agentCert);
        Assert.DoesNotContain("s3cret", sealedTask.Payload!.Value.GetRawText());
        var step = PullJsonContext.FromElement(sealedTask.Payload, PullJsonContext.Default.SystemChange)!.Steps![0];
        Assert.Equal("s3cret", PublicChannel.Open(step.Secret, _agentKey));
    }

    private DefaultHttpContext Request(string method, string path, byte[] body, long? time = null, string? nonce = null, X509Certificate2? cert = null, ECDsa? key = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        var q = path.IndexOf('?');
        ctx.Request.Path = q < 0 ? path : path[..q];
        ctx.Request.QueryString = q < 0 ? QueryString.Empty : new QueryString(path[q..]);
        ctx.Request.Body = new MemoryStream(body);
        var t = time ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var n = nonce ?? PublicChannel.NewNonce();
        cert ??= _agentCert;
        ctx.Request.Headers[PublicChannel.CertHeader] = Convert.ToBase64String(cert.RawData);
        ctx.Request.Headers[PublicChannel.TimeHeader] = t.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ctx.Request.Headers[PublicChannel.NonceHeader] = n;
        ctx.Request.Headers[PublicChannel.SignatureHeader] = PublicChannel.Sign(key ?? _agentKey, PublicChannel.RequestToSign(method, path, t, n, body));
        return ctx;
    }

    [Fact]
    public async Task Server_AcceptsSignedRequest_RejectsReplayOldForeignAndTampered()
    {
        var access = new PublicAccess();
        var body = Encoding.UTF8.GetBytes("{\"outcome\":\"Succeeded\"}");

        var ok = Request("POST", "/agent/v2/poll?running=1", body, nonce: "0123456789abcdef0123");
        var (cert, error) = await PublicChannelGate.VerifyRequestAsync(ok, _ca, access, DateTimeOffset.UtcNow);
        Assert.NotNull(cert);
        Assert.Equal("", error);
        Assert.Equal(0, ok.Request.Body.Position); // Inhalt für den Endpunkt wieder lesbar

        var replay = Request("POST", "/agent/v2/poll?running=1", body, nonce: "0123456789abcdef0123");
        Assert.Contains("Replayed", (await PublicChannelGate.VerifyRequestAsync(replay, _ca, access, DateTimeOffset.UtcNow)).Error);

        var old = Request("GET", "/agent/v2/poll", [], time: DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeSeconds());
        Assert.Contains("too old", (await PublicChannelGate.VerifyRequestAsync(old, _ca, access, DateTimeOffset.UtcNow)).Error);

        using var foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var foreign = new CertificateRequest("CN=evil", foreignKey, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var stranger = Request("GET", "/agent/v2/poll", [], cert: foreign, key: foreignKey);
        Assert.Contains("not issued", (await PublicChannelGate.VerifyRequestAsync(stranger, _ca, access, DateTimeOffset.UtcNow)).Error);

        var tampered = Request("POST", "/agent/v2/tasks/1/result", body);
        tampered.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"outcome\":\"Failed\"}"));
        Assert.Contains("Invalid signature", (await PublicChannelGate.VerifyRequestAsync(tampered, _ca, access, DateTimeOffset.UtcNow)).Error);

        var missing = new DefaultHttpContext();
        Assert.Contains("Missing", (await PublicChannelGate.VerifyRequestAsync(missing, _ca, access, DateTimeOffset.UtcNow)).Error);
    }

    [Fact]
    public void Agent_AcceptsOnlyResponsesSignedByServerCertificate()
    {
        var body = "{\"pollIntervalSeconds\":60}"u8.ToArray();
        var server = _ca.GetServerCertificate();
        using var serverKey = server.GetECDsaPrivateKey()!;
        var signature = PublicChannel.Sign(serverKey, PublicChannel.ResponseToSign("n1", 200, body));
        var serverCert = Convert.ToBase64String(server.RawData);

        Assert.True(ServerClient.VerifyResponse(_ca.RootCertificate, "n1", 200, body, serverCert, signature));
        Assert.False(ServerClient.VerifyResponse(_ca.RootCertificate, "n2", 200, body, serverCert, signature)); // alte Antwort wiederholt
        Assert.False(ServerClient.VerifyResponse(_ca.RootCertificate, "n1", 200, "{}"u8.ToArray(), serverCert, signature)); // verändert
        Assert.False(ServerClient.VerifyResponse(_ca.RootCertificate, "n1", 200, body, null, null)); // Proxy-Fehlerseite

        // Ein Agent-Zertifikat (gleiche CA, aber clientAuth) darf keine Server-Antworten unterschreiben
        var forged = PublicChannel.Sign(_agentKey, PublicChannel.ResponseToSign("n1", 200, body));
        Assert.False(ServerClient.VerifyResponse(_ca.RootCertificate, "n1", 200, body, Convert.ToBase64String(_agentCert.RawData), forged));
    }

    [Theory]
    [InlineData("agents.example.com", "https://agents.example.com")]
    [InlineData("https://agents.example.com/", "https://agents.example.com")]
    [InlineData("https://agents.example.com:8443", "https://agents.example.com:8443")]
    [InlineData("http://agents.example.com", null)]
    [InlineData("https://agents.example.com/aio", null)]
    [InlineData("", null)]
    public void PublicUrl_Normalized(string input, string? expected) => Assert.Equal(expected, PublicAccess.NormalizeUrl(input));

    [Fact]
    public void Unreachable_OnlyNetworkErrors()
    {
        Assert.True(ServerClient.IsUnreachable(new HttpRequestException("refused")));
        Assert.True(ServerClient.IsUnreachable(new TaskCanceledException()));
        Assert.False(ServerClient.IsUnreachable(new ServerRejectedException(System.Net.HttpStatusCode.Unauthorized, "")));
        Assert.False(ServerClient.IsUnreachable(new HttpRequestException("x", null, System.Net.HttpStatusCode.BadGateway)));
    }
}
