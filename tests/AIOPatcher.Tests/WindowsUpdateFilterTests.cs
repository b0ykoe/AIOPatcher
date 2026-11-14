using System.Runtime.Versioning;
using AIOPatcher.Agent.Platforms.Windows;

namespace AIOPatcher.Tests;

/// <summary>
/// Prüft die Auswertung der Windows-Update-Objekte mit nachgebauten Objekten in derselben Form wie die
/// COM-Schnittstellen (IUpdateCollection, IUpdate, ICategoryCollection, IStringCollection).
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsUpdateFilterTests
{
    // --- Nachbau der COM-Form: Count + Default-Indexer, Properties mit gleichen Namen -------------------
    public sealed class FakeCollection<T>(params T[] items)
    {
        public int Count => items.Length;
        public T this[int index] => items[index];
    }

    public sealed class FakeCategory(string id, string name)
    {
        public string CategoryID { get; } = id;
        public string Name { get; } = name;
    }

    public sealed class FakeIdentity(string id)
    {
        public string UpdateID { get; } = id;
    }

    public sealed class FakeUpdate
    {
        public string Title { get; init; } = "";
        public bool BrowseOnly { get; init; }
        public string? MsrcSeverity { get; init; }
        public FakeIdentity Identity { get; init; } = new(Guid.NewGuid().ToString());
        public FakeCollection<string> KBArticleIDs { get; init; } = new();
        public FakeCollection<FakeCategory> Categories { get; init; } = new();
    }

    private static readonly FakeCategory Security = new("0FA1201D-4330-4FA8-8AE9-B877473B6441", "Sicherheitsupdates");
    private static readonly FakeCategory Updates = new("cd5ffd1e-e932-4e3a-bf74-18bf0b1bbd83", "Updates");
    private static readonly FakeCategory Definition = new("e0789628-ce08-4437-be74-2495b842f43b", "Definitionsupdates");
    private static readonly FakeCategory Upgrade = new("3689bdc8-b205-4af4-8d4a-a63924c5e9d5", "Upgrades");
    private static readonly FakeCategory Driver = new("ebfc1fc5-71a4-4f7b-9aca-3b9a503104a0", "Treiber");

    [Fact]
    public void Describe_KeepsSecurityAndRegularUpdates_DropsDefenderUpgradesDriversAndOptional()
    {
        var collection = new FakeCollection<FakeUpdate>(
            new FakeUpdate { Title = "2026-11 Kumulatives Update", MsrcSeverity = "Critical", KBArticleIDs = new("5049981"), Categories = new(Security) },
            new FakeUpdate { Title = ".NET Update", KBArticleIDs = new("5050000"), Categories = new(Updates) },
            new FakeUpdate { Title = "Defender Signatur", Categories = new(Definition) },
            new FakeUpdate { Title = "Windows Server 2025 Upgrade", Categories = new(Upgrade) },
            new FakeUpdate { Title = "NVIDIA Treiber", Categories = new(Driver) },
            new FakeUpdate { Title = "Vorschau-Update", BrowseOnly = true, Categories = new(Updates) });

        var result = WindowsUpdateApi.Describe(collection);

        Assert.Equal(["2026-11 Kumulatives Update", ".NET Update"], result.Select(u => u.Title));

        var cu = result[0];
        Assert.True(cu.IsSecurity);                    // Kategorie-ID trotz Großschreibung erkannt
        Assert.Equal("KB5049981", cu.KbArticle);
        Assert.Equal("Critical", cu.Severity);
        Assert.Equal("Sicherheitsupdates", cu.Classification);

        Assert.False(result[1].IsSecurity);
        Assert.Null(result[1].Severity);
    }

    [Fact]
    public void Describe_TreatsMsrcSeverityAsSecurity_AndHandlesMissingKb()
    {
        var collection = new FakeCollection<FakeUpdate>(
            new FakeUpdate { Title = "Update mit Severity", MsrcSeverity = "Important", Categories = new(Updates) });

        var update = Assert.Single(WindowsUpdateApi.Describe(collection));

        Assert.True(update.IsSecurity);
        Assert.Null(update.KbArticle);
    }

    [Fact]
    public void Describe_EmptyCollection()
    {
        Assert.Empty(WindowsUpdateApi.Describe(new FakeCollection<FakeUpdate>()));
    }
}
