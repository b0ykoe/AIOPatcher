using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using AIOPatcher.Server.Data;
using Beta = Anthropic.Models.Beta.Messages;

namespace AIOPatcher.Server.Services.Ai;

/// <summary>Fehler der KI-Anbindung mit einer Meldung, die so im Dashboard stehen kann.</summary>
public class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Ratenlimit des Schlüssels erreicht (HTTP 429): ab <see cref="RetryAt"/> geht es weiter, kein echter Fehler.</summary>
public sealed class AiRateLimitException(string message, DateTime retryAt, Exception? inner = null) : AiException(message, inner)
{
    public DateTime RetryAt { get; } = retryAt;
}

/// <summary>Antwort der KI mit dem gemeldeten Verbrauch (0, wenn der Anbieter nichts meldet).</summary>
public sealed record AiAnswer(string Text, long InputTokens, long OutputTokens);

/// <summary>Ein Ratenlimit laut Antwort-Header: erlaubt, noch frei, wann wieder voll.</summary>
public sealed record AiLimit(long Limit, long Remaining, DateTime? ResetAt);

/// <summary>Ratenlimits des Schlüssels aus der letzten Antwort (Claude: anthropic-ratelimit-*, OpenAI: x-ratelimit-*).</summary>
public sealed record AiRateLimits(string Provider, DateTime ObservedAt, AiLimit? Requests, AiLimit? Tokens, AiLimit? InputTokens, AiLimit? OutputTokens);

/// <summary>
/// Eine Anfrage an die eingestellte KI: Claude über das offizielle Anthropic-SDK, ChatGPT und lokale Modelle über die
/// OpenAI-kompatible Schnittstelle (/chat/completions). Merkt sich die Ratenlimits aus den Antwort-Headern.
/// </summary>
public sealed partial class AiClient(IHttpClientFactory httpFactory)
{
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string Compatible = "compatible";

    public static readonly string[] Providers = [Anthropic, OpenAi, Compatible];

    /// <summary>Wartezeit bei HTTP 429 ohne Angabe des Anbieters.</summary>
    public static readonly TimeSpan DefaultRetry = TimeSpan.FromMinutes(1);

    private volatile AiRateLimits? _limits;

    /// <summary>Ratenlimits aus der letzten Antwort (null, solange keine kam oder der Anbieter keine meldet).</summary>
    public AiRateLimits? Limits => _limits;

    /// <summary>Vorschlag, wenn kein Modell eingetragen ist (bei "compatible" muss eins eingetragen sein).</summary>
    public static string DefaultModel(string provider) => provider == Anthropic ? "claude-opus-5-5" : "";

    /// <summary>Kurzname für die Anzeige, z. B. "anthropic/claude-opus-5-5".</summary>
    public static string Label(AiSettingsRecord settings) => $"{settings.Provider}/{ModelOf(settings)}";

    public static string ModelOf(AiSettingsRecord settings) =>
        string.IsNullOrWhiteSpace(settings.Model) ? DefaultModel(settings.Provider) : settings.Model.Trim();

    public async Task<AiAnswer> CompleteAsync(AiSettingsRecord settings, string? apiKey, string system, string user, int maxTokens, CancellationToken ct)
    {
        var model = ModelOf(settings);
        if (model.Length == 0)
        {
            throw new AiException(L.T("Kein Modell eingetragen.", "No model set."));
        }

        return settings.Provider switch
        {
            Anthropic => await ClaudeAsync(model, apiKey, system, user, maxTokens, ct),
            OpenAi => await ChatCompletionsAsync(OpenAi, "https://api.openai.com/v1", model, apiKey, system, user, maxTokens, ct),
            Compatible when !string.IsNullOrWhiteSpace(settings.BaseUrl) =>
                await ChatCompletionsAsync(Compatible, settings.BaseUrl.Trim().TrimEnd('/'), model, apiKey, system, user, maxTokens, ct),
            Compatible => throw new AiException(L.T("Keine Adresse für das lokale Modell eingetragen.", "No address set for the local model.")),
            _ => throw new AiException(L.T("Unbekannter Anbieter.", "Unknown provider.")),
        };
    }

    private async Task<AiAnswer> ClaudeAsync(string model, string? apiKey, string system, string user, int maxTokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiException(L.T("Kein API-Schlüssel für Claude eingetragen.", "No API key set for Claude."));
        }

        // letzte Antwort mitlesen (Ratenlimit-Header), offizieller Haken des SDK
        HttpResponseMessage? last = null;
        var client = new AnthropicClient
        {
            ApiKey = apiKey,
            HttpClient = httpFactory.CreateClient("ai"),
            Handlers =
            [
                Handler.Create(async (request, next, token) =>
                {
                    var response = await next(request, token);
                    last = response;
                    return response;
                }),
            ],
        };

        // Ablehnungen durch Sicherheitsfilter: auf diesen Modellen übernimmt serverseitig ein anderes Modell (fallbacks).
        var fallback = model.StartsWith("claude-opus-5-5", StringComparison.Ordinal) || model.StartsWith("claude-sonnet-5-5", StringComparison.Ordinal)
                       || model.StartsWith("claude-fable-5", StringComparison.Ordinal);
        var parameters = fallback
            ? new Beta.MessageCreateParams
            {
                Model = model,
                MaxTokens = maxTokens,
                System = system,
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Beta.Default(),
                Messages = [new Beta.BetaUserMessageParam { Content = user }],
            }
            : new Beta.MessageCreateParams
            {
                Model = model,
                MaxTokens = maxTokens,
                System = system,
                Messages = [new Beta.BetaUserMessageParam { Content = user }],
            };

        try
        {
            var response = await client.Beta.Messages.Create(parameters, ct);
            Observe(Anthropic, last);
            if (response.StopReason == "refusal")
            {
                throw new AiException(L.T("Claude hat die Anfrage abgelehnt.", "Claude declined the request."));
            }

            var text = string.Join("\n", response.Content.Select(b => b.Value).OfType<Beta.BetaTextBlock>().Select(t => t.Text)).Trim();
            return text.Length > 0
                ? new AiAnswer(text, response.Usage.InputTokens + (response.Usage.CacheCreationInputTokens ?? 0) + (response.Usage.CacheReadInputTokens ?? 0),
                    response.Usage.OutputTokens)
                : throw new AiException(L.T("Leere Antwort von Claude.", "Empty answer from Claude."));
        }
        catch (AnthropicRateLimitException ex)
        {
            // Ausgabenlimit des Kontos: Warten hilft nicht, das ist ein echter Fehler
            var limits = Observe(Anthropic, last);
            if (ex.Message.Contains("spend_limit", StringComparison.OrdinalIgnoreCase))
            {
                throw new AiException($"Claude: {ex.Message}", ex);
            }

            throw RateLimited("Claude", RetryAt(name => Header(last, name), limits, DateTime.UtcNow), ex);
        }
        catch (AnthropicApiException ex)
        {
            Observe(Anthropic, last);
            throw new AiException($"Claude: {ex.Message}", ex);
        }
    }

    private async Task<AiAnswer> ChatCompletionsAsync(string provider, string baseUrl, string model, string? apiKey, string system, string user, int maxTokens,
        CancellationToken ct)
    {
        var official = provider == OpenAi;
        if (official && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiException(L.T("Kein API-Schlüssel für OpenAI eingetragen.", "No API key set for OpenAI."));
        }

        var http = httpFactory.CreateClient("ai");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        body[official ? "max_completion_tokens" : "max_tokens"] = maxTokens;
        request.Content = JsonContent.Create(body);

        var source = official ? "OpenAI" : baseUrl;
        try
        {
            using var response = await http.SendAsync(request, ct);
            var limits = Observe(provider, response);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode == 429 && !raw.Contains("insufficient_quota", StringComparison.Ordinal))
            {
                throw RateLimited(source, RetryAt(name => Header(response, name), limits, DateTime.UtcNow), null);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AiException($"{source}: HTTP {(int)response.StatusCode} {Shorten(raw)}");
            }

            var json = JsonNode.Parse(raw);
            var text = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim();
            return string.IsNullOrEmpty(text)
                ? throw new AiException(L.T("Leere Antwort der KI.", "Empty answer from the AI."))
                : new AiAnswer(text, Tokens(json?["usage"]?["prompt_tokens"]), Tokens(json?["usage"]?["completion_tokens"]));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new AiException($"{source}: {ex.Message}", ex);
        }
    }

    /// <summary>Ratenlimits einer Antwort merken (nur wenn der Anbieter welche meldet).</summary>
    private AiRateLimits? Observe(string provider, HttpResponseMessage? response)
    {
        if (response is null)
        {
            return null;
        }

        var limits = ParseLimits(provider, name => Header(response, name), DateTime.UtcNow);
        if (limits is not null)
        {
            _limits = limits;
        }

        return limits;
    }

    private static AiRateLimitException RateLimited(string source, DateTime? retryAt, Exception? inner)
    {
        var at = retryAt ?? DateTime.UtcNow + DefaultRetry;
        return new AiRateLimitException(
            L.T($"{source}: Ratenlimit des Schlüssels erreicht, weiter ab {at.ToLocalTime():HH:mm:ss}.",
                $"{source}: rate limit of the key reached, continuing from {at.ToLocalTime():HH:mm:ss}."), at, inner);
    }

    private static string? Header(HttpResponseMessage? response, string name) =>
        response is not null && (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
            ? values.FirstOrDefault()
            : null;

    /// <summary>
    /// Ratenlimits aus den Antwort-Headern. Claude: anthropic-ratelimit-{requests,tokens,input-tokens,output-tokens}-{limit,remaining,reset}
    /// mit Zeitpunkt (RFC 3339); OpenAI und kompatible: x-ratelimit-{limit,remaining,reset}-{requests,tokens} mit Dauer ("6m0s").
    /// </summary>
    public static AiRateLimits? ParseLimits(string provider, Func<string, string?> header, DateTime now)
    {
        AiLimit? Read(string limit, string remaining, string reset) =>
            long.TryParse(header(limit), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)
            && long.TryParse(header(remaining), NumberStyles.Integer, CultureInfo.InvariantCulture, out var left)
                ? new AiLimit(max, left, ResetTime(header(reset), now))
                : null;

        AiRateLimits limits = provider == Anthropic
            ? new(provider, now,
                Read("anthropic-ratelimit-requests-limit", "anthropic-ratelimit-requests-remaining", "anthropic-ratelimit-requests-reset"),
                Read("anthropic-ratelimit-tokens-limit", "anthropic-ratelimit-tokens-remaining", "anthropic-ratelimit-tokens-reset"),
                Read("anthropic-ratelimit-input-tokens-limit", "anthropic-ratelimit-input-tokens-remaining", "anthropic-ratelimit-input-tokens-reset"),
                Read("anthropic-ratelimit-output-tokens-limit", "anthropic-ratelimit-output-tokens-remaining", "anthropic-ratelimit-output-tokens-reset"))
            : new(provider, now,
                Read("x-ratelimit-limit-requests", "x-ratelimit-remaining-requests", "x-ratelimit-reset-requests"),
                Read("x-ratelimit-limit-tokens", "x-ratelimit-remaining-tokens", "x-ratelimit-reset-tokens"),
                null, null);
        return limits is { Requests: null, Tokens: null, InputTokens: null, OutputTokens: null } ? null : limits;
    }

    /// <summary>Weiter ab: retry-after (Sekunden oder Datum), sonst der späteste Reset eines ausgeschöpften Limits.</summary>
    public static DateTime? RetryAt(Func<string, string?> header, AiRateLimits? limits, DateTime now)
    {
        if (header("retry-after") is { } retryAfter)
        {
            if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return now.AddSeconds(Math.Max(1, seconds));
            }

            if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                return at.UtcDateTime;
            }
        }

        var resets = new[] { limits?.Requests, limits?.Tokens, limits?.InputTokens, limits?.OutputTokens }
            .Where(l => l is { Remaining: <= 0, ResetAt: not null })
            .Select(l => l!.ResetAt!.Value)
            .ToList();
        return resets.Count > 0 ? resets.Max() : null;
    }

    // "1s", "6m0s", "20ms", "1h2m3.5s"
    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ms|h|m|s)", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPart();

    private static DateTime? ResetTime(string? value, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && value.Contains('-'))
        {
            return at.UtcDateTime;
        }

        var parts = DurationPart().Matches(value);
        if (parts.Count == 0)
        {
            return null;
        }

        var total = parts.Sum(p =>
        {
            var n = double.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture);
            return p.Groups[2].Value switch { "h" => n * 3600, "m" => n * 60, "ms" => n / 1000, _ => n };
        });
        return now.AddSeconds(total);
    }

    private static long Tokens(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;

    private static string Shorten(string text) => text.Length > 200 ? text[..200] : text;
}
