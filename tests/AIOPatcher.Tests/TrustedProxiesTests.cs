using System.Net;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOPatcher.Tests;

/// <summary>Reverse Proxy: X-Forwarded-For nur von eingetragenen Proxys und nur auf dem Dashboard-Port.</summary>
public sealed class TrustedProxiesTests
{
    private static readonly AIOPatcherOptions Options = new() { DashboardPort = 9443, AgentPort = 8443 };

    /// <summary>Baut die echte Pipeline (UseTrustedProxies) und liefert die IP, die der Rest der App sieht.</summary>
    private static async Task<string?> ClientIpAsync(string? trusted, string remote, string? forwardedFor, int port = 9443) =>
        (await RunAsync(trusted, remote, forwardedFor, port)).Ip;

    private static async Task<(string? Ip, bool Ok, string Message)> RunAsync(string? trusted, string remote, string? forwardedFor, int port = 9443)
    {
        var options = new AIOPatcherOptions { DashboardPort = Options.DashboardPort, AgentPort = Options.AgentPort, TrustedProxies = trusted };
        var services = new ServiceCollection().AddLogging().AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>)).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        string? seen = null;
        (bool Ok, string Message) described = (false, "");
        app.UseTrustedProxies(options);
        app.Run(ctx => { seen = ctx.Connection.RemoteIpAddress?.ToString(); described = TrustedProxies.Describe(ctx); return Task.CompletedTask; });

        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        ctx.Connection.LocalPort = port;
        if (forwardedFor is not null)
        {
            ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        await app.Build()(ctx);
        return (seen, described.Ok, described.Message);
    }

    [Fact]
    public async Task TrustedProxy_ForwardedIpIsUsed()
    {
        Assert.Equal("203.0.113.7", await ClientIpAsync("127.0.0.1", "127.0.0.1", "203.0.113.7"));
        Assert.Equal("203.0.113.7", await ClientIpAsync("172.30.0.0/24", "172.30.0.5", "203.0.113.7"));
    }

    [Fact]
    public async Task DualStackMappedAddress_IsRecognised()
    {
        Assert.Equal("203.0.113.7", await ClientIpAsync("172.30.0.0/24", "::ffff:172.30.0.5", "203.0.113.7"));
    }

    [Fact]
    public async Task UntrustedSender_CannotSpoofItsIp()
    {
        Assert.Equal("198.51.100.9", await ClientIpAsync("127.0.0.1", "198.51.100.9", "10.0.0.1"));
    }

    [Fact]
    public async Task NoTrustedProxies_HeaderIsIgnored_EvenFromLoopback()
    {
        Assert.Equal("127.0.0.1", await ClientIpAsync(null, "127.0.0.1", "203.0.113.7"));
    }

    [Fact]
    public async Task AgentPort_NeverUsesForwardedHeaders()
    {
        Assert.Equal("127.0.0.1", await ClientIpAsync("127.0.0.1", "127.0.0.1", "203.0.113.7", port: 8443));
    }

    [Fact]
    public async Task ProxyChain_SkipsTrustedHops_ButStopsAtFirstUntrusted()
    {
        // Client fälscht "1.1.1.1" vorne, echte Kette: Client 203.0.113.7 → Proxy 172.30.0.9 → Proxy 172.30.0.5 → AIOPatcher
        Assert.Equal("203.0.113.7", await ClientIpAsync("172.30.0.0/24", "172.30.0.5", "1.1.1.1, 203.0.113.7, 172.30.0.9"));
    }

    [Fact]
    public async Task Describe_ExplainsWhatTheServerSees()
    {
        L.Use("en");
        var direct = await RunAsync("127.0.0.1", "198.51.100.9", null);
        Assert.True(direct.Ok);
        Assert.Contains("directly, your IP: 198.51.100.9", direct.Message);

        var proxied = await RunAsync("127.0.0.1", "127.0.0.1", "203.0.113.7");
        Assert.True(proxied.Ok);
        Assert.Contains("your IP: 203.0.113.7", proxied.Message);

        var forgotten = await RunAsync(null, "::ffff:172.18.0.4", "203.0.113.7");
        Assert.False(forgotten.Ok);
        Assert.Contains("(172.18.0.4)", forgotten.Message);
        Assert.Contains("TrustedProxies", forgotten.Message);
    }

    [Theory]
    [InlineData("127.0.0.1", 1)]
    [InlineData("127.0.0.1, 172.30.0.0/24; ::1", 3)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Parse_AcceptsAddressesAndNetworks(string? value, int count)
    {
        Assert.Equal(count, TrustedProxies.Parse(value).Count);
    }

    [Theory]
    [InlineData("traefik")]
    [InlineData("172.30.0.0/99")]
    public void Parse_InvalidEntry_FailsAtStartup(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse(value));
        Assert.Contains("TrustedProxies", ex.Message);
    }
}
