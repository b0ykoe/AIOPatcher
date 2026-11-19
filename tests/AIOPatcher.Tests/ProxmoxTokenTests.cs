using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Services;

namespace AIOPatcher.Tests;

/// <summary>Proxmox-API-Token: häufigster Fehler ist nur das Secret (UUID) statt "benutzer@realm!token-id=secret".</summary>
public sealed class ProxmoxTokenTests
{
    public ProxmoxTokenTests() => L.Use("en");

    [Theory]
    [InlineData("root@pam!aiopatcher=1b2c3d4e-1111-2222-3333-444455556666")]
    [InlineData("monitor@pve!patch-token=abcdef")]
    public void ValidTokens(string token) => Assert.Null(IntegrationEndpoints.ProxmoxTokenProblem(token));

    [Fact]
    public void OnlyTheSecret_IsExplained()
    {
        var problem = IntegrationEndpoints.ProxmoxTokenProblem("1b2c3d4e-1111-2222-3333-444455556666");
        Assert.Contains("only the secret", problem);
        Assert.Contains("root@pam!aiopatcher=1b2c3d4e-1111-2222-3333-444455556666", problem);
    }

    [Theory]
    [InlineData("root@pam!aiopatcher")]
    [InlineData("root@pam=secret")]
    [InlineData("aiopatcher=secret")]
    [InlineData("root@pam!aiopatcher= secret")]
    public void WrongFormat(string token) => Assert.Contains("Format", IntegrationEndpoints.ProxmoxTokenProblem(token));
}
