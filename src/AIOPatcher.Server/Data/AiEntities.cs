using System.ComponentModel.DataAnnotations;

namespace AIOPatcher.Server.Data;

/// <summary>
/// KI-Anbindung für Zusammenfassungen der Patchnotes (eine Zeile). Anbieter: "anthropic" (Claude), "openai" (ChatGPT)
/// oder "compatible" (OpenAI-kompatible Schnittstelle, z. B. Ollama, LM Studio, vLLM). An die KI gehen nur Paketnamen,
/// Versionen und Changelog-Texte, keine Host-Namen oder Adressen.
/// </summary>
public sealed class AiSettingsRecord
{
    public int Id { get; set; } = 1;

    public bool Enabled { get; set; }

    [MaxLength(20)]
    public string Provider { get; set; } = "anthropic";

    [MaxLength(100)]
    public string Model { get; set; } = "";

    /// <summary>Nur bei "compatible": Basisadresse der Schnittstelle, z. B. http://ollama:11434/v1.</summary>
    [MaxLength(300)]
    public string? BaseUrl { get; set; }

    /// <summary>API-Schlüssel, verschlüsselt per Data Protection; bei lokalen Modellen oft leer.</summary>
    public string? ProtectedApiKey { get; set; }

    /// <summary>Sprache der Zusammenfassungen: "de" oder "en".</summary>
    [MaxLength(5)]
    public string Language { get; set; } = "de";

    [MaxLength(200)]
    public string? UpdatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

/// <summary>KI-Zusammenfassung aller anstehenden Updates eines Hosts ("Was bringen diese Updates?").</summary>
public sealed class HostUpdateSummary
{
    public Guid HostId { get; set; }

    /// <summary>SHA-256 der sortierten Liste "paket=version": ändert sich die Liste, wird neu zusammengefasst.</summary>
    [MaxLength(64)]
    public required string Fingerprint { get; set; }

    public string? Text { get; set; }

    [MaxLength(300)]
    public string? Error { get; set; }

    [MaxLength(150)]
    public string? Model { get; set; }

    public int UpdateCount { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Verbrauch der KI je Tag (Anfragen und Tokens laut Anbieter), für den Hinweis unter Einstellungen → KI.</summary>
public sealed class AiUsageDay
{
    public DateOnly Day { get; set; }

    public int Requests { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }
}

/// <summary>Eine Anfrage an die KI: wofür, mit welchem Modell, wie viele Tokens (laut Anbieter), wie lange, mit welchem Ergebnis.</summary>
public sealed class AiRequestRecord
{
    public long Id { get; set; }

    public DateTime At { get; set; }

    /// <summary>"packages" (Bündel von Paket-Zusammenfassungen), "host" (Zusammenfassung eines Hosts) oder "test".</summary>
    [MaxLength(20)]
    public required string Kind { get; set; }

    [MaxLength(150)]
    public required string Model { get; set; }

    /// <summary>Host, für den die Anfrage lief (kein Fremdschlüssel: der Eintrag bleibt, wenn der Host gelöscht wird).</summary>
    public Guid? HostId { get; set; }

    /// <summary>Pakete in der Anfrage (bei "host": anstehende Updates).</summary>
    public int Items { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public int DurationMs { get; set; }

    /// <summary>"ok", "error" oder "rateLimited".</summary>
    [MaxLength(20)]
    public required string Outcome { get; set; }

    [MaxLength(300)]
    public string? Error { get; set; }
}
