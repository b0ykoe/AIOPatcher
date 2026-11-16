using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

public sealed class NotificationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public NotificationTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    [Fact]
    public async Task HostMonitor_ReportsOfflineHostOnce()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var site = new Site { Name = "Homelab" };
        _db.Hosts.Add(new ManagedHost { Site = site, Name = "web01", Hostname = "web01", CertificateThumbprint = "a", LastSeenAt = now.AddMinutes(-10) });
        _db.Hosts.Add(new ManagedHost { Site = site, Name = "web02", Hostname = "web02", CertificateThumbprint = "b", LastSeenAt = now.AddSeconds(-30) });
        _db.Hosts.Add(new ManagedHost { Site = site, Name = "old", Hostname = "old", CertificateThumbprint = "c", LastSeenAt = now.AddDays(-3), IsRevoked = true });
        await _db.SaveChangesAsync();

        var notifier = new Notifier();
        await HostMonitor.CheckAsync(_db, TimeSpan.FromMinutes(3), now, notifier, CancellationToken.None);
        await HostMonitor.CheckAsync(_db, TimeSpan.FromMinutes(3), now.AddMinutes(1), notifier, CancellationToken.None);

        Assert.True(notifier.Reader.TryRead(out var n));
        Assert.Equal(NotificationEvents.HostOffline, n!.Event);
        Assert.Equal("web01 is offline", n.Title);
        Assert.False(notifier.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(ChannelType.Ntfy, "https://ntfy.sh/topic", null, null, true)]
    [InlineData(ChannelType.Ntfy, "ftp://x", null, null, false)]
    [InlineData(ChannelType.Email, null, "a@example.com", "b@example.com, c@example.com", true)]
    [InlineData(ChannelType.Email, null, "a@example.com", "kein-mail", false)]
    [InlineData(ChannelType.Telegram, null, null, null, false)]
    public void Validate_ChecksRequiredFields(ChannelType type, string? url, string? from, string? to, bool ok)
    {
        var config = new ChannelConfig { Url = url, SmtpHost = "smtp.example.com", From = from, To = to };
        Assert.Equal(ok, NotificationSenders.Validate(type, config) is null);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
