using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using AIOPatcher.Server.Data;
using Beta = Anthropic.Models.Beta.Messages;

namespace AIOPatcher.Server.Services.Ai;

/// <summary>Fehler der KI-Anbindung mit einer Meldung, die so im Dashboard stehen kann.</summary>
public sealed class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Eine Anfrage an die eingestellte KI: Claude über das offizielle Anthropic-SDK, ChatGPT und lokale Modelle über die
/// OpenAI-kompatible Schnittstelle (/chat/completions).
/// </summary>
public sealed class AiClient(IHttpClientFactory httpFactory)
{
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string Compatible = "compatible";

    public static readonly string[] Providers = [Anthropic, OpenAi, Compatible];

    /// <summary>Vorschlag, wenn kein Modell eingetragen ist (bei "compatible" muss eins eingetragen sein).</summary>
    public static string DefaultModel(string provider) => provider == Anthropic ? "claude-opus-5-5" : "";

    /// <summary>Kurzname für die Anzeige, z. B. "anthropic/claude-opus-5-5".</summary>
    public static string Label(AiSettingsRecord settings) => $"{settings.Provider}/{ModelOf(settings)}";

    public static string ModelOf(AiSettingsRecord settings) =>
        string.IsNullOrWhiteSpace(settings.Model) ? DefaultModel(settings.Provider) : settings.Model.Trim();

    public async Task<string> CompleteAsync(AiSettingsRecord settings, string? apiKey, string system, string user, int maxTokens, CancellationToken ct)
    {
        var model = ModelOf(settings);
        if (model.Length == 0)
        {
            throw new AiException(L.T("Kein Modell eingetragen.", "No model set."));
        }

        return settings.Provider switch
        {
            Anthropic => await ClaudeAsync(model, apiKey, system, user, maxTokens, ct),
            OpenAi => await ChatCompletionsAsync("https://api.openai.com/v1", model, apiKey, system, user, maxTokens, official: true, ct),
            Compatible when !string.IsNullOrWhiteSpace(settings.BaseUrl) =>
                await ChatCompletionsAsync(settings.BaseUrl.Trim().TrimEnd('/'), model, apiKey, system, user, maxTokens, official: false, ct),
            Compatible => throw new AiException(L.T("Keine Adresse für das lokale Modell eingetragen.", "No address set for the local model.")),
            _ => throw new AiException(L.T("Unbekannter Anbieter.", "Unknown provider.")),
        };
    }

    private static async Task<string> ClaudeAsync(string model, string? apiKey, string system, string user, int maxTokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiException(L.T("Kein API-Schlüssel für Claude eingetragen.", "No API key set for Claude."));
        }

        var client = new AnthropicClient { ApiKey = apiKey };

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
            if (response.StopReason == "refusal")
            {
                throw new AiException(L.T("Claude hat die Anfrage abgelehnt.", "Claude declined the request."));
            }

            var text = string.Join("\n", response.Content.Select(b => b.Value).OfType<Beta.BetaTextBlock>().Select(t => t.Text)).Trim();
            return text.Length > 0 ? text : throw new AiException(L.T("Leere Antwort von Claude.", "Empty answer from Claude."));
        }
        catch (AnthropicApiException ex)
        {
            throw new AiException($"Claude: {ex.Message}", ex);
        }
    }

    private async Task<string> ChatCompletionsAsync(string baseUrl, string model, string? apiKey, string system, string user, int maxTokens, bool official,
        CancellationToken ct)
    {
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

        try
        {
            using var response = await http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new AiException($"{(official ? "OpenAI" : baseUrl)}: HTTP {(int)response.StatusCode} {Shorten(raw)}");
            }

            var text = JsonNode.Parse(raw)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim();
            return string.IsNullOrEmpty(text) ? throw new AiException(L.T("Leere Antwort der KI.", "Empty answer from the AI.")) : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new AiException($"{(official ? "OpenAI" : baseUrl)}: {ex.Message}", ex);
        }
    }

    private static string Shorten(string text) => text.Length > 200 ? text[..200] : text;
}
