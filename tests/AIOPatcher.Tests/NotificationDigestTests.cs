using System.Text.Json.Nodes;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Sammelnachrichten: Wichtiges sofort, der Rest gebündelt nach Ruhe bzw. spätestens nach der Sammelzeit.</summary>
public sealed class NotificationDigestTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 20, 5, 40, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public NotificationDigestTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Collects_OnlyWhenEnabled_AndNotForImportantEvents()
    {
        var channel = new NotificationChannel { Name = "tg", ProtectedConfig = "", DigestMinutes = 60 };
        Assert.True(NotificationDigest.ShouldCollect(channel, NotificationEvents.NewSecurityUpdates));
        Assert.True(NotificationDigest.ShouldCollect(channel, NotificationEvents.ContainerUpdates));
        Assert.False(NotificationDigest.ShouldCollect(channel, NotificationEvents.KevDetected));
        Assert.False(NotificationDigest.ShouldCollect(channel, NotificationEvents.JobFailed));
        Assert.False(NotificationDigest.ShouldCollect(channel, NotificationEvents.HostOffline));
        Assert.False(NotificationDigest.ShouldCollect(channel, NotificationEvents.None)); // Testnachricht

        channel.DigestMinutes = 0;
        Assert.False(NotificationDigest.ShouldCollect(channel, NotificationEvents.NewSecurityUpdates));
    }

    [Fact]
    public void IsDue_AfterQuietPeriod_OrAtTheLatestAfterTheDigestTime()
    {
        // 10 Minuten Ruhe
        Assert.False(NotificationDigest.IsDue(Now.AddMinutes(-15), Now.AddMinutes(-9), 60, Now));
        Assert.True(NotificationDigest.IsDue(Now.AddMinutes(-15), Now.AddMinutes(-10), 60, Now));
        // es kommt ständig Neues: spätestens nach 60 Minuten
        Assert.False(NotificationDigest.IsDue(Now.AddMinutes(-59), Now.AddMinutes(-1), 60, Now));
        Assert.True(NotificationDigest.IsDue(Now.AddMinutes(-60), Now.AddMinutes(-1), 60, Now));
        // kurze Sammelzeit: kürzere Ruhe
        Assert.True(NotificationDigest.IsDue(Now.AddMinutes(-8), Now.AddMinutes(-7), 15, Now));
    }

    [Fact]
    public void Build_GroupsByEvent_AndMergesTheSameMessageAcrossHosts()
    {
        var (n, details) = NotificationDigest.Build(NotificationEndpoints.SampleDigest(Now), TimeZoneInfo.Utc);

        Assert.Equal("5 notifications since 05:26: 4× New security updates, 1× Container updates", n.Title);
        Assert.Contains("New security updates (4)\n• python3-jwt: vpn, pivpn, monitor\n• cloudpanel: 2 new security updates — openssl, libssl3", n.Message);
        Assert.Contains("Container updates (1)\n• docker: New image: mariadb:10.11 — Affects: docker/lc2018-db", n.Message);
        Assert.Equal(NotificationSeverity.Warning, n.Severity);
        Assert.Equal(5, n.Items!.Count);
        Assert.Null(n.HostId);
        Assert.Equal("/hosts", n.LinkPath);
        Assert.Equal("Homelab", details.Site);
        Assert.Equal("Digest", details.Event);
    }

    [Fact]
    public void Build_SingleItem_GoesOutUnchanged()
    {
        var item = NotificationEndpoints.SampleDigest(Now)[0];
        var (n, details) = NotificationDigest.Build([item], TimeZoneInfo.Utc);
        Assert.Equal("vpn: 1 new security updates", n.Title);
        Assert.Equal("python3-jwt", n.Message);
        Assert.Null(n.Items);
        Assert.Equal("vpn", details.Host);
        Assert.Equal("New security updates", details.Event);
    }

    [Fact]
    public async Task Flush_SendsDueDigests_AndKeepsTheRest()
    {
        var hourly = new NotificationChannel { Name = "tg", ProtectedConfig = "", DigestMinutes = 60 };
        var off = new NotificationChannel { Name = "off", ProtectedConfig = "", DigestMinutes = 60, Enabled = false };
        _db.NotificationChannels.AddRange(hourly, off);
        foreach (var item in NotificationEndpoints.SampleDigest(Now.AddMinutes(5))) // letzte Meldung vor 5 Minuten
        {
            item.ChannelId = hourly.Id;
            _db.PendingNotifications.Add(item);
        }

        var stale = NotificationEndpoints.SampleDigest(Now)[0];
        stale.ChannelId = off.Id;
        _db.PendingNotifications.Add(stale);
        await _db.SaveChangesAsync();

        // Letzte Meldung vor 5 Minuten: noch nicht fällig; der ausgeschaltete Kanal wird verworfen
        Assert.Equal(0, await NotifierFlush(Now));
        Assert.Equal(5, await _db.PendingNotifications.CountAsync());

        // Nach 10 Minuten Ruhe geht die Sammlung raus
        Assert.Equal(1, await NotifierFlush(Now.AddMinutes(6)));
        Assert.Equal(0, await _db.PendingNotifications.CountAsync());
    }

    private Task<int> NotifierFlush(DateTime now) => NotificationDispatcher.FlushDigestsAsync(_db, null, TimeZoneInfo.Utc, now, null, CancellationToken.None);

    [Fact]
    public void ExistingChannels_SendInstantly_WithTheImportantEventsPreselected()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name, dflt_value FROM pragma_table_info('NotificationChannels') WHERE name IN ('DigestMinutes', 'InstantEvents') ORDER BY name";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("0", reader.GetString(1));
        Assert.True(reader.Read());
        Assert.Equal(((int)NotificationChannel.ImportantEvents).ToString(), reader.GetString(1));
    }

    [Theory]
    [InlineData(ChannelType.Webhook, "items")]
    [InlineData(ChannelType.Telegram, "text")]
    public void Preview_ShowsADigest(ChannelType type, string key)
    {
        var options = new AIOPatcher.Server.Options.AIOPatcherOptions();
        var result = NotificationEndpoints.Preview(new(type, NotificationStyle.Standard, Digest: true), new ServerClock(options), options, null);
        var json = JsonNode.Parse(Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult>(result).ResponseContent!)!.AsObject();
        Assert.True(json.ContainsKey(key));
        Assert.Contains("python3-jwt: vpn, pivpn, monitor", json.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
