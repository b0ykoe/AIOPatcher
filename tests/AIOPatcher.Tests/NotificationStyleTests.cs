using System.Text.Json.Nodes;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Stile je Kanal: Kurz, Standard, Ausführlich, Schick (eigene Formatierung des Dienstes).</summary>
public sealed class NotificationStyleTests
{
    private const string Link = "https://patch.example.com/jobs/1";

    private static readonly Notification Failed = new(NotificationEvents.JobFailed, "rigidtest: Patch <failed>", "1 of 2 updates installed", NotificationSeverity.Critical,
        LinkPath: "/jobs/1");

    private static readonly NotificationDetails Details = new("rigidtest", "Homelab", "Job failed", new DateTime(2026, 11, 19, 22, 46, 0));

    public NotificationStyleTests() => L.Use("en");

    [Fact]
    public void Telegram_ShortStandardDetailed()
    {
        Assert.Equal($"🚨 rigidtest: Patch <failed>\n{Link}", (string?)NotificationFormat.Telegram(NotificationStyle.Short, Failed, Details, "1", Link, true)["text"]);
        Assert.Equal($"🚨 rigidtest: Patch <failed>\n1 of 2 updates installed\n{Link}", (string?)NotificationFormat.Telegram(NotificationStyle.Standard, Failed, Details, "1", Link, true)["text"]);
        var detailed = (string?)NotificationFormat.Telegram(NotificationStyle.Detailed, Failed, Details, "1", Link, true)["text"];
        Assert.Contains("Host rigidtest · Site Homelab · Job failed · 2026-11-19 22:46", detailed);
    }

    [Fact]
    public void Telegram_Rich_HtmlWithButton_OrLinkWhenButtonsAreRefused()
    {
        var rich = NotificationFormat.Telegram(NotificationStyle.Rich, Failed, Details, "1", Link, withButton: true);
        Assert.Equal("HTML", (string?)rich["parse_mode"]);
        Assert.StartsWith("<b>🚨 rigidtest: Patch &lt;failed&gt;</b>", (string?)rich["text"]); // HTML maskiert
        Assert.Equal(Link, (string?)rich["reply_markup"]!["inline_keyboard"]![0]![0]!["url"]);

        var noButton = NotificationFormat.Telegram(NotificationStyle.Rich, Failed, Details, "1", Link, withButton: false);
        Assert.Null(noButton["reply_markup"]);
        Assert.Contains($"<a href=\"{Link}\">", (string?)noButton["text"]);
    }

    [Fact]
    public void Discord_Rich_IsAColoredEmbedWithFields()
    {
        var embed = NotificationFormat.Discord(NotificationStyle.Rich, Failed, Details, Link)["embeds"]![0]!;
        Assert.Equal(0xE03131, (int)embed["color"]!);
        Assert.Equal(Link, (string?)embed["url"]);
        Assert.Equal(["Host", "Site", "Event"], embed["fields"]!.AsArray().Select(f => (string?)f!["name"]));
        Assert.Null(NotificationFormat.Discord(NotificationStyle.Standard, Failed, Details, Link)["embeds"]);
    }

    [Fact]
    public void Slack_Rich_HasBlocksAndButton_PlusFallbackText()
    {
        var slack = NotificationFormat.Slack(NotificationStyle.Rich, Failed, Details, Link);
        Assert.Equal("🚨 rigidtest: Patch <failed>", (string?)slack["text"]);
        var blocks = slack["blocks"]!.AsArray();
        Assert.Equal(["header", "section", "context", "actions"], blocks.Select(b => (string?)b!["type"]));
        Assert.Equal(Link, (string?)blocks[3]!["elements"]![0]!["url"]);
    }

    [Fact]
    public void Mail_ShortText_AndRichHtml()
    {
        Assert.Equal($"rigidtest: Patch <failed>\n\n{Link}\n", NotificationFormat.MailText(NotificationStyle.Short, Failed, Details, Link));
        var html = NotificationFormat.MailHtml(Failed, Details, Link);
        Assert.Contains("rigidtest: Patch &lt;failed&gt;", html);
        Assert.Contains($"href=\"{Link}\"", html);
        Assert.Contains("#E03131", html);
    }

    [Fact]
    public void Ntfy_BodyPerStyle()
    {
        Assert.Equal("rigidtest: Patch <failed>", NotificationFormat.NtfyBody(NotificationStyle.Short, Failed, Details));
        Assert.Equal("1 of 2 updates installed", NotificationFormat.NtfyBody(NotificationStyle.Standard, Failed, Details));
        Assert.EndsWith("_Host rigidtest · Site Homelab · Job failed · 2026-11-19 22:46_", NotificationFormat.NtfyBody(NotificationStyle.Rich, Failed, Details));
    }

    [Fact]
    public void ExistingChannels_KeepTheStandardStyle()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dflt_value FROM pragma_table_info('NotificationChannels') WHERE name = 'Style'";
        Assert.Equal("'Standard'", command.ExecuteScalar() as string);
    }

    [Theory]
    [InlineData(ChannelType.Telegram, NotificationStyle.Rich, "reply_markup")]
    [InlineData(ChannelType.Discord, NotificationStyle.Rich, "embeds")]
    [InlineData(ChannelType.Slack, NotificationStyle.Rich, "blocks")]
    [InlineData(ChannelType.Email, NotificationStyle.Rich, "html")]
    [InlineData(ChannelType.Ntfy, NotificationStyle.Rich, "markdown")]
    [InlineData(ChannelType.Webhook, NotificationStyle.Short, "eventLabel")]
    public void Preview_UsesTheRealFormatter(ChannelType type, NotificationStyle style, string key)
    {
        var options = new AIOPatcher.Server.Options.AIOPatcherOptions();
        var result = AIOPatcher.Server.Endpoints.NotificationEndpoints.Preview(new(type, style), new ServerClock(options), options, null);
        var json = JsonNode.Parse(Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult>(result).ResponseContent!)!.AsObject();
        Assert.True(json.ContainsKey(key), key + " missing in " + json.ToJsonString());
    }
}
