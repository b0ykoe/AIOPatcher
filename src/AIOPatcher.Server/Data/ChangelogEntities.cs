using System.ComponentModel.DataAnnotations;

namespace AIOPatcher.Server.Data;

/// <summary>
/// Patchnotes (Debian-Changelog) eines Pakets in einer Version, wie der Agent sie per <c>apt-get changelog</c> geholt hat.
/// Je Paket und Version nur einmal, für alle Hosts. Bleibt nach der Installation erhalten (Historie am Patch-Job).
/// </summary>
public sealed class PackageChangelogRecord
{
    public long Id { get; set; }

    [MaxLength(100)]
    public required string Name { get; set; }

    [MaxLength(100)]
    public required string Version { get; set; }

    /// <summary>Anfang des Changelogs (neueste Einträge zuerst); null, wenn keiner zu haben war.</summary>
    public string? Text { get; set; }

    /// <summary>Warum es keinen gibt (z. B. Quelle bietet keine Changelogs an).</summary>
    [MaxLength(300)]
    public string? Error { get; set; }

    public DateTime FetchedAt { get; set; }

    /// <summary>KI-Zusammenfassung dessen, was diese Version gegenüber der vorigen ändert.</summary>
    public string? Summary { get; set; }

    /// <summary>Anbieter/Modell der Zusammenfassung, z. B. "anthropic/claude-haiku-5-5".</summary>
    [MaxLength(150)]
    public string? SummaryModel { get; set; }

    [MaxLength(5)]
    public string? SummaryLanguage { get; set; }

    /// <summary>Zusammengefasst wurde alles seit dieser (installierten) Version; null = nur der neueste Eintrag.</summary>
    [MaxLength(100)]
    public string? SummaryFrom { get; set; }

    public DateTime? SummaryAt { get; set; }

    [MaxLength(300)]
    public string? SummaryError { get; set; }
}
