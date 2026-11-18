using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fido2NetLib;
using Microsoft.Extensions.Caching.Memory;
using static AIOPatcher.Server.Endpoints.PasskeyEndpoints;

namespace AIOPatcher.Tests;

/// <summary>Passkeys: Registrierung und Anmeldung mit einem Software-Authenticator (ES256), dazu die Ablehnungsfälle.</summary>
public sealed class PasskeyTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly UserAccount _user;
    private static readonly PasskeySite Site = new("https://patch.example.com", "patch.example.com");
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    public PasskeyTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _user = new UserAccount { Username = "alice", PasswordHash = "hash", ProtectedTotpSecret = "x", IsSetupComplete = true };
        _db.Users.Add(_user);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    // --- Software-Authenticator ---------------------------------------------------------------------

    private sealed class Authenticator
    {
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] UserHandle { get; set; } = [];
        public uint Counter { get; set; }
    }

    /// <summary>Abgelehnt aus dem erwarteten Grund (nicht wegen eines Fehlers im Test-Authenticator).</summary>
    private static void Rejected(Exception ex, string reason) => Assert.Contains(reason, ex.Message, StringComparison.OrdinalIgnoreCase);

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Challenge(string optionsJson) => JsonNode.Parse(optionsJson)!["challenge"]!.GetValue<string>();

    private static byte[] ClientData(string type, string challenge, string origin) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin, crossOrigin = false }));

    private static byte[] AuthData(string rpId, byte flags, uint counter, byte[]? attested = null)
    {
        var data = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(rpId))) { flags };
        data.AddRange([(byte)(counter >> 24), (byte)(counter >> 16), (byte)(counter >> 8), (byte)counter]);
        if (attested is not null)
        {
            data.AddRange(attested);
        }

        return [.. data];
    }

    private static byte[] CoseKey(ECDsa key)
    {
        var p = key.ExportParameters(false);
        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(5);
        w.WriteInt32(1); w.WriteInt32(2);     // kty: EC2
        w.WriteInt32(3); w.WriteInt32(-7);    // alg: ES256
        w.WriteInt32(-1); w.WriteInt32(1);    // crv: P-256
        w.WriteInt32(-2); w.WriteByteString(p.Q.X!);
        w.WriteInt32(-3); w.WriteByteString(p.Q.Y!);
        w.WriteEndMap();
        return w.Encode();
    }

    /// <summary>Antwort wie PublicKeyCredential.toJSON() nach navigator.credentials.create.</summary>
    private static JsonElement Attest(Authenticator a, string optionsJson, string origin, string rpId, byte flags = 0x45)
    {
        var attested = new List<byte>(new byte[16]); // AAGUID 0
        attested.AddRange([(byte)(a.CredentialId.Length >> 8), (byte)a.CredentialId.Length]);
        attested.AddRange(a.CredentialId);
        attested.AddRange(CoseKey(a.Key));

        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(3);
        w.WriteTextString("fmt"); w.WriteTextString("none");
        w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
        w.WriteTextString("authData"); w.WriteByteString(AuthData(rpId, flags, a.Counter, [.. attested]));
        w.WriteEndMap();

        var json = JsonSerializer.Serialize(new
        {
            id = B64(a.CredentialId),
            rawId = B64(a.CredentialId),
            type = "public-key",
            response = new
            {
                clientDataJSON = B64(ClientData("webauthn.create", Challenge(optionsJson), origin)),
                attestationObject = B64(w.Encode()),
                transports = new[] { "internal" },
            },
            clientExtensionResults = new { },
        });
        return JsonDocument.Parse(json).RootElement;
    }

    /// <summary>Antwort wie PublicKeyCredential.toJSON() nach navigator.credentials.get.</summary>
    private static JsonElement Assert_(Authenticator a, string optionsJson, string origin, string rpId, byte flags = 0x05)
    {
        a.Counter++;
        var authData = AuthData(rpId, flags, a.Counter);
        var clientData = ClientData("webauthn.get", Challenge(optionsJson), origin);
        var signature = a.Key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var json = JsonSerializer.Serialize(new
        {
            id = B64(a.CredentialId),
            rawId = B64(a.CredentialId),
            type = "public-key",
            response = new
            {
                clientDataJSON = B64(clientData),
                authenticatorData = B64(authData),
                signature = B64(signature),
                userHandle = B64(a.UserHandle),
            },
            clientExtensionResults = new { },
        });
        return JsonDocument.Parse(json).RootElement;
    }

    private async Task<Authenticator> RegisterAsync(PasskeySite site, string name = "Laptop")
    {
        var a = new Authenticator { UserHandle = _user.Id.ToByteArray() };
        var (requestId, options) = await BeginRegistrationAsync(_db, _cache, _user, site, CancellationToken.None);
        await CompleteRegistrationAsync(_db, _cache, _user, site, requestId, name, Attest(a, options, site.Origin, site.RpId), CancellationToken.None);
        await _db.SaveChangesAsync();
        return a;
    }

    private async Task<(UserAccount User, UserPasskey Passkey)> LoginAsync(Authenticator a, PasskeySite site, string? origin = null, string? rpId = null, byte flags = 0x05)
    {
        var (requestId, options) = BeginLogin(_cache, site);
        return await CompleteLoginAsync(_db, _cache, site, requestId, Assert_(a, options, origin ?? site.Origin, rpId ?? site.RpId, flags), Now, CancellationToken.None);
    }

    // --- Tests ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Register_ThenSignIn_UpdatesCounterAndLastUse()
    {
        var a = await RegisterAsync(Site);

        var stored = await _db.Passkeys.SingleAsync();
        Assert.Equal("patch.example.com", stored.RpId);
        Assert.Equal("Laptop", stored.Name);
        Assert.Equal("internal", stored.Transports);

        var (user, passkey) = await LoginAsync(a, Site);
        Assert.Equal(_user.Id, user.Id);
        Assert.Equal(1, passkey.SignCount);
        Assert.Equal(Now, passkey.LastUsedAt);
        Assert.Equal(Now, user.LastLoginAt);
    }

    [Fact]
    public async Task Passkey_OnlyWorksForTheNameItWasCreatedFor()
    {
        var a = await RegisterAsync(Site);
        var other = new PasskeySite("https://patch.lan:9443", "patch.lan");

        var ex = await Assert.ThrowsAsync<PasskeyException>(() => LoginAsync(a, other));
        Assert.Contains("not known", ex.Message);
    }

    [Fact]
    public async Task WrongOrigin_OrWrongRpIdHash_IsRejected()
    {
        var a = await RegisterAsync(Site);

        Rejected(await Assert.ThrowsAsync<Fido2VerificationException>(() => LoginAsync(a, Site, origin: "https://evil.example.com")), "origin");
        Rejected(await Assert.ThrowsAsync<Fido2VerificationException>(() => LoginAsync(a, Site, rpId: "evil.example.com")), "rp");
        Assert.Null((await _db.Passkeys.SingleAsync()).LastUsedAt);
    }

    [Fact]
    public async Task WithoutUserVerification_IsRejected()
    {
        var a = await RegisterAsync(Site);
        Rejected(await Assert.ThrowsAsync<Fido2VerificationException>(() => LoginAsync(a, Site, flags: 0x01)), "verif"); // nur "anwesend", keine PIN/Biometrie

        var b = new Authenticator { UserHandle = _user.Id.ToByteArray() };
        var (requestId, options) = await BeginRegistrationAsync(_db, _cache, _user, Site, CancellationToken.None);
        Rejected(await Assert.ThrowsAsync<Fido2VerificationException>(() =>
            CompleteRegistrationAsync(_db, _cache, _user, Site, requestId, "x", Attest(b, options, Site.Origin, Site.RpId, flags: 0x41), CancellationToken.None)), "verif");
    }

    [Fact]
    public async Task DisabledUser_CannotSignIn()
    {
        var a = await RegisterAsync(Site);
        _user.IsDisabled = true;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<PasskeyException>(() => LoginAsync(a, Site));
    }

    [Fact]
    public async Task SsoOnlyAccount_GetsNoPasskeys()
    {
        _user.PasswordHash = "";
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<PasskeyException>(() => BeginRegistrationAsync(_db, _cache, _user, Site, CancellationToken.None));
    }

    [Fact]
    public async Task Challenge_IsSingleUse_AndBoundToItsCeremony()
    {
        var a = await RegisterAsync(Site);
        var (requestId, options) = BeginLogin(_cache, Site);
        var response = Assert_(a, options, Site.Origin, Site.RpId);

        await CompleteLoginAsync(_db, _cache, Site, requestId, response, Now, CancellationToken.None);
        await Assert.ThrowsAsync<PasskeyException>(() => CompleteLoginAsync(_db, _cache, Site, requestId, response, Now, CancellationToken.None));

        // Antwort auf eine fremde Challenge (andere Zeremonie) wird abgelehnt
        var (_, otherOptions) = BeginLogin(_cache, Site);
        var (requestId2, _) = BeginLogin(_cache, Site);
        Rejected(await Assert.ThrowsAsync<Fido2VerificationException>(() =>
            CompleteLoginAsync(_db, _cache, Site, requestId2, Assert_(a, otherOptions, Site.Origin, Site.RpId), Now, CancellationToken.None)), "challenge");

        // Registrierungs-Zeremonie taugt nicht zum Anmelden
        var (regId, _) = await BeginRegistrationAsync(_db, _cache, _user, Site, CancellationToken.None);
        await Assert.ThrowsAsync<PasskeyException>(() => CompleteLoginAsync(_db, _cache, Site, regId, response, Now, CancellationToken.None));
    }

    [Fact]
    public async Task SameAuthenticator_CannotBeRegisteredTwice()
    {
        var a = await RegisterAsync(Site);
        var (requestId, options) = await BeginRegistrationAsync(_db, _cache, _user, Site, CancellationToken.None);
        Assert.Contains(B64(a.CredentialId), options); // excludeCredentials: Browser bietet ihn gar nicht erst an

        Rejected(await Assert.ThrowsAsync<Fido2VerificationException>(() =>
            CompleteRegistrationAsync(_db, _cache, _user, Site, requestId, "again", Attest(a, options, Site.Origin, Site.RpId), CancellationToken.None)), "credential");
    }

    [Theory]
    [InlineData("https://patch.example.com", "patch.example.com")]
    [InlineData("https://patch.lan:9443", "patch.lan")]
    [InlineData("http://localhost:5173", "localhost")]
    [InlineData("https://10.0.0.5:9443", null)]
    [InlineData("https://[fd00::5]:9443", null)]
    [InlineData("http://patch.lan:9443", null)]
    public void PasskeysNeedANameAndHttps(string url, string? rpId) => Assert.Equal(rpId, SiteOf(new Uri(url))?.RpId);
}
