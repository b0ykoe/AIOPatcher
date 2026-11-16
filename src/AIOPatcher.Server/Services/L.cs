namespace AIOPatcher.Server.Services;

/// <summary>
/// Zweisprachige Texte ohne Ressourcen-Dateien: <c>L.T("Deutsch", "English")</c>.
/// Bewusst ohne CultureInfo: der Server läuft mit InvariantGlobalization (kein ICU im Container).
/// In Anfragen gilt die Sprache aus Accept-Language (siehe <see cref="UseRequestLanguage"/>),
/// sonst die Server-Sprache (AIOPatcher:Language).
/// </summary>
public static class L
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>Server-Sprache für Hintergrunddienste und Anfragen ohne Accept-Language.</summary>
    public static string Default { get; set; } = "en";

    public static string Language => Current.Value ?? Default;

    public static bool IsGerman => Language == "de";

    public static string T(string de, string en) => IsGerman ? de : en;

    public static string Normalize(string? language) => language?.Trim().ToLowerInvariant() is "de" ? "de" : "en";

    /// <summary>Setzt die Sprache für den aktuellen asynchronen Ablauf (z. B. Tests, CLI).</summary>
    public static void Use(string? language) => Current.Value = language is null ? null : Normalize(language);

    /// <summary>Erste unterstützte Sprache aus Accept-Language, z. B. "de-DE,de;q=0.9,en;q=0.8" → "de".</summary>
    public static string? FromAcceptLanguage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var best = header.Split(',')
            .Select(part =>
            {
                var pieces = part.Split(';');
                var tag = pieces[0].Trim().ToLowerInvariant();
                var q = 1.0;
                foreach (var p in pieces.Skip(1))
                {
                    var kv = p.Trim();
                    if (kv.StartsWith("q=", StringComparison.Ordinal) && double.TryParse(kv[2..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        q = parsed;
                    }
                }

                return (Lang: tag.Split('-')[0], Q: q);
            })
            .Where(x => x.Lang is "de" or "en" && x.Q > 0)
            .OrderByDescending(x => x.Q)
            .FirstOrDefault();

        return best.Lang;
    }

    public static IApplicationBuilder UseRequestLanguage(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            Current.Value = FromAcceptLanguage(ctx.Request.Headers.AcceptLanguage);
            await next();
        });
}
