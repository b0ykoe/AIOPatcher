using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Security;

namespace AIOPatcher.Server.Services.Integrations;

/// <summary>Portainer-Umgebung (Endpoint). Type 1 Docker, 2 Agent, 4 Edge Agent, 5–7 Kubernetes. Status 1 = erreichbar.</summary>
/// <param name="Error">Docker-Abfrage dieser Umgebung fehlgeschlagen (Stacks ohne Container-Angaben).</param>
public sealed record PortainerEnvironment(int Id, string Name, int Type, int Status, string? Error);

/// <summary>Container eines Stacks. Reference/LocalDigest wie bei Host-Containern (null = lokal gebaut oder per Digest gepinnt).</summary>
public sealed record PortainerContainer(string Name, string? Service, string Image, string? Reference, string? LocalDigest, string? State);

/// <summary>Stack laut /api/stacks. Type 1 Swarm, 2 Compose, 3 Kubernetes. Status 1 aktiv, 2 gestoppt.</summary>
/// <param name="Git">Aus einem Git-Repository angelegt: Update per "git/redeploy", sonst mit der gespeicherten Stack-Datei.</param>
public sealed record PortainerStack(int Id, string Name, int Type, int EnvironmentId, int Status, bool Git, string? GitUrl, string? GitRef,
    DateTime? UpdatedAt, IReadOnlyList<PortainerContainer> Containers);

/// <summary>Zustand einer Portainer-Instanz (gespeichert in Integration.StatusJson).</summary>
/// <param name="OutdatedStacks">Stacks mit neuerem Image in der Registry beim letzten Abruf (für Benachrichtigungen nur Neue).</param>
public sealed record PortainerStatus(string? Version, IReadOnlyList<PortainerEnvironment> Environments, IReadOnlyList<PortainerStack> Stacks,
    DateTime CheckedAt, IReadOnlyList<int>? OutdatedStacks = null);

/// <summary>
/// Portainer-API mit Access Token (My account → Access tokens), gesendet als X-API-Key. Lesen: Umgebungen, Stacks, Container und
/// Images über den Docker-Proxy. Schreiben: Stack mit neuem Image neu ausrollen ("Pull and redeploy").
/// </summary>
public sealed class PortainerClient : IDisposable
{
    public const int SwarmStack = 1;
    public const int ComposeStack = 2;
    public const int KubernetesStack = 3;
    private const int StatusDeploying = 3;
    private const int StatusError = 4;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Ältere Portainer-Versionen antworten erst nach "pull" und "up"; das kann bei großen Images dauern.</summary>
    public static readonly TimeSpan DeployTimeout = TimeSpan.FromMinutes(15);

    private readonly Integration _integration;
    private readonly string _token;
    private readonly HttpClient _client;
    private readonly Func<string?> _fingerprint;

    /// <summary>Delay zwischen zwei Abfragen, solange Portainer "Deploying" meldet (Tests: 0).</summary>
    public TimeSpan PollDelay { get; init; } = TimeSpan.FromSeconds(5);

    public PortainerClient(Integration integration, string token, HttpMessageHandler handler, Func<string?>? fingerprint = null)
    {
        _integration = integration;
        _token = token.Trim();
        _fingerprint = fingerprint ?? (() => null);
        _client = new HttpClient(handler) { BaseAddress = new Uri((integration.BaseUrl ?? "").TrimEnd('/') + "/api/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _client.Dispose();

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, TimeSpan timeout, CancellationToken ct, bool allowNotFound = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation("X-API-Key", _token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await _client.SendAsync(request, cts.Token);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new HttpRequestException(response.ReasonPhrase, null, response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new IntegrationException($"Portainer: HTTP {(int)response.StatusCode}{ErrorDetail(await response.Content.ReadAsStringAsync(cts.Token))}");
            }

            var text = await response.Content.ReadAsStringAsync(cts.Token);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw IntegrationHttp.Wrap(ex is System.Text.Json.JsonException ? new IntegrationException(L.T("Portainer: unerwartete Antwort.", "Portainer: unexpected response.")) : ex,
                _fingerprint, _integration);
        }
    }

    private Task<JsonNode?> GetAsync(string path, CancellationToken ct, bool allowNotFound = false) => SendAsync(HttpMethod.Get, path, null, ReadTimeout, ct, allowNotFound);

    /// <summary>Fehlertext aus {"message": "...", "details": "..."}.</summary>
    public static string ErrorDetail(string? body)
    {
        try
        {
            var json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
            var detail = Str(json, "details") ?? Str(json, "message");
            return string.IsNullOrWhiteSpace(detail) ? "" : ": " + (detail.Length > 300 ? detail[..300] : detail);
        }
        catch (System.Text.Json.JsonException)
        {
            return "";
        }
    }

    /// <summary>Version aus /api/system/status (ab 2.20) bzw. /api/status (ältere Versionen).</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct) =>
        Str(await GetAsync("system/status", ct, allowNotFound: true) ?? await GetAsync("status", ct, allowNotFound: true), "Version");

    public async Task<string> TestAsync(CancellationToken ct)
    {
        var version = await GetVersionAsync(ct);
        var environments = ParseEnvironments(await GetAsync("endpoints", ct));
        var stacks = ParseStacks(await GetAsync("stacks", ct));
        return L.T($"Portainer {version ?? "?"}, {environments.Count} Umgebung(en), {stacks.Count} Stack(s)",
            $"Portainer {version ?? "?"}, {environments.Count} environment(s), {stacks.Count} stack(s)");
    }

    /// <summary>Umgebungen, Stacks und deren Container. Fällt eine Umgebung aus, bleiben ihre Stacks ohne Container stehen.</summary>
    public async Task<PortainerStatus> GetStatusAsync(DateTime now, CancellationToken ct)
    {
        var version = await GetVersionAsync(ct);
        var environments = ParseEnvironments(await GetAsync("endpoints", ct)).ToList();
        var stacks = ParseStacks(await GetAsync("stacks", ct));

        var containers = new Dictionary<int, IReadOnlyList<DockerContainer>>();
        for (var i = 0; i < environments.Count; i++)
        {
            var env = environments[i];
            if (!IsDocker(env.Type) || !stacks.Any(s => s.EnvironmentId == env.Id && s.Type != KubernetesStack))
            {
                continue;
            }

            try
            {
                var list = await GetAsync($"endpoints/{env.Id}/docker/containers/json?all=1", ct);
                var images = await GetAsync($"endpoints/{env.Id}/docker/images/json", ct);
                containers[env.Id] = ParseContainers(list, images);
            }
            catch (IntegrationException ex)
            {
                environments[i] = env with { Error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message };
            }
        }

        var withContainers = stacks
            .Select(s => s with { Containers = ContainersOf(s, containers.GetValueOrDefault(s.EnvironmentId) ?? []) })
            .ToList();
        return new PortainerStatus(version, environments, withContainers, now);
    }

    /// <summary>
    /// Stack mit neu gezogenen Images neu ausrollen. Git-Stacks: PUT stacks/{id}/git/redeploy (gleiche Referenz, gespeicherte
    /// Zugangsdaten). Sonst: PUT stacks/{id} mit der gespeicherten Stack-Datei und den bisherigen Umgebungsvariablen.
    /// </summary>
    /// <param name="log">Protokollzeilen für die Job-Ansicht.</param>
    public async Task<string> RedeployAsync(int stackId, int environmentId, Action<string> log, CancellationToken ct)
    {
        var stack = await GetAsync($"stacks/{stackId}", ct) as JsonObject ?? throw new IntegrationException(L.T("Stack nicht gefunden.", "Stack not found."));
        var parsed = ParseStack(stack) ?? throw new IntegrationException(L.T("Stack nicht lesbar.", "Stack not readable."));
        if (parsed.Type == KubernetesStack)
        {
            throw new IntegrationException(L.T("Kubernetes-Stacks werden nicht unterstützt.", "Kubernetes stacks are not supported."));
        }

        if ((int?)Long(stack, "Status") == StatusDeploying)
        {
            throw new IntegrationException(L.T("Portainer rollt den Stack gerade schon aus.", "Portainer is already deploying this stack."));
        }

        var env = stack["Env"] is JsonArray pairs ? pairs.DeepClone() : new JsonArray();
        var query = $"?endpointId={environmentId.ToString(CultureInfo.InvariantCulture)}";
        JsonNode? result;
        if (parsed.Git)
        {
            log(L.T($"Git-Stack: {parsed.GitUrl} ({parsed.GitRef ?? "Standard-Branch"}), Images neu ziehen und neu ausrollen.",
                $"Git stack: {parsed.GitUrl} ({parsed.GitRef ?? "default branch"}), pulling images and redeploying."));
            result = await SendAsync(HttpMethod.Put, $"stacks/{stackId}/git/redeploy{query}", GitRedeployBody(stack, env), DeployTimeout, ct);
        }
        else
        {
            var file = Str(await GetAsync($"stacks/{stackId}/file", ct), "StackFileContent");
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new IntegrationException(L.T("Portainer liefert keine Stack-Datei.", "Portainer returned no stack file."));
            }

            log(L.T($"Stack-Datei gelesen ({file.Length} Zeichen, {env.AsArray().Count} Umgebungsvariable(n)), Images neu ziehen und neu ausrollen.",
                $"Read stack file ({file.Length} characters, {env.AsArray().Count} environment variable(s)), pulling images and redeploying."));
            result = await SendAsync(HttpMethod.Put, $"stacks/{stackId}{query}", FileUpdateBody(file, env), DeployTimeout, ct);
        }

        // Neuere Versionen rollen im Hintergrund aus: warten, bis der Stack nicht mehr "Deploying" ist.
        var deadline = DateTime.UtcNow + DeployTimeout;
        while ((int?)Long(result, "Status") == StatusDeploying && DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollDelay, ct);
            result = await GetAsync($"stacks/{stackId}", ct);
        }

        if ((int?)Long(result, "Status") == StatusError)
        {
            throw new IntegrationException(L.T($"Portainer meldet einen Fehler beim Ausrollen{DeploymentMessage(result)}",
                $"Portainer reports a deployment error{DeploymentMessage(result)}"));
        }

        if ((int?)Long(result, "Status") == StatusDeploying)
        {
            throw new IntegrationException(L.T("Portainer rollt nach 15 Minuten immer noch aus; Stand bitte in Portainer prüfen.",
                "Portainer is still deploying after 15 minutes; please check in Portainer."));
        }

        log(L.T("Portainer hat den Stack neu ausgerollt.", "Portainer redeployed the stack."));
        return L.T($"Stack {parsed.Name} neu ausgerollt", $"Stack {parsed.Name} redeployed");
    }

    // --- Anfragen und Auswertung (statisch, testbar) --------------------------------------------------

    /// <summary>PUT stacks/{id}: "PullImage" bis 2.35, "RepullImageAndRedeploy" ab 2.36 (beide gesetzt, unbekannte Felder ignoriert Portainer).</summary>
    public static JsonObject FileUpdateBody(string file, JsonNode env) => new()
    {
        ["StackFileContent"] = file,
        ["Env"] = env,
        ["Prune"] = false,
        ["PullImage"] = true,
        ["RepullImageAndRedeploy"] = true,
    };

    /// <summary>
    /// PUT stacks/{id}/git/redeploy: gespeicherte Referenz und Prune-Einstellung, bisherige Variablen (sonst wären sie weg).
    /// Mit Anmeldung: Benutzername ohne Passwort, Portainer nimmt dann das gespeicherte.
    /// </summary>
    public static JsonObject GitRedeployBody(JsonObject stack, JsonNode env)
    {
        var git = stack["GitConfig"];
        var username = Str(git?["Authentication"], "Username");
        return new JsonObject
        {
            ["RepositoryReferenceName"] = Str(git, "ReferenceName") ?? "",
            ["RepositoryAuthentication"] = !string.IsNullOrEmpty(username),
            ["RepositoryUsername"] = username ?? "",
            ["RepositoryPassword"] = "",
            ["Env"] = env,
            ["Prune"] = stack["Option"]?["Prune"] is JsonValue prune && prune.TryGetValue<bool>(out var p) && p,
            ["PullImage"] = true,
            ["RepullImageAndRedeploy"] = true,
        };
    }

    private static string DeploymentMessage(JsonNode? stack)
    {
        var message = (stack?["DeploymentStatus"] as JsonArray)?.OfType<JsonObject>().Select(s => Str(s, "Message")).LastOrDefault(m => !string.IsNullOrWhiteSpace(m));
        return message is null ? "." : ": " + (message.Length > 300 ? message[..300] : message);
    }

    public static bool IsDocker(int environmentType) => environmentType is 1 or 2 or 4;

    public static IReadOnlyList<PortainerEnvironment> ParseEnvironments(JsonNode? data) =>
        (data as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => Long(o, "Id") is not null)
            .Select(o => new PortainerEnvironment((int)Long(o, "Id")!, Str(o, "Name") ?? "?", (int)(Long(o, "Type") ?? 0), (int)(Long(o, "Status") ?? 0), null))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<PortainerStack> ParseStacks(JsonNode? data) =>
        (data as JsonArray ?? []).OfType<JsonObject>()
            .Select(ParseStack)
            .OfType<PortainerStack>()
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static PortainerStack? ParseStack(JsonObject o)
    {
        if (Long(o, "Id") is not { } id || Str(o, "Name") is not { } name)
        {
            return null;
        }

        var git = o["GitConfig"] as JsonObject;
        var gitUrl = Str(git, "URL") ?? Str(o["CurrentDeploymentInfo"], "RepositoryURL");
        var gitRef = Str(git, "ReferenceName") ?? Str(o["CurrentDeploymentInfo"], "ReferenceName");
        var isGit = !string.IsNullOrEmpty(gitUrl) || Long(o, "WorkflowID") is > 0;
        var updated = Long(o, "UpdateDate") is > 0 ? Long(o, "UpdateDate") : Long(o, "CreationDate");
        return new PortainerStack((int)id, name, (int)(Long(o, "Type") ?? 0), (int)(Long(o, "EndpointId") ?? 0), (int)(Long(o, "Status") ?? 0),
            isGit, string.IsNullOrEmpty(gitUrl) ? null : gitUrl, string.IsNullOrEmpty(gitRef) ? null : gitRef,
            updated is > 0 ? DateTimeOffset.FromUnixTimeSeconds(updated.Value).UtcDateTime : null, []);
    }

    /// <summary>Container aus dem Docker-Proxy, Image-Digests aus der Image-Liste derselben Umgebung.</summary>
    public sealed record DockerContainer(string Name, string? Project, string? SwarmStack, string? Service, string Image, string? Reference, string? LocalDigest, string? State);

    public static IReadOnlyList<DockerContainer> ParseContainers(JsonNode? containers, JsonNode? images)
    {
        var byId = (images as JsonArray ?? []).OfType<JsonObject>()
            .Where(i => Str(i, "Id") is not null)
            .GroupBy(i => Str(i, "Id")!)
            .ToDictionary(g => g.Key, g => g.First());

        var result = new List<DockerContainer>();
        foreach (var c in (containers as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = ((c["Names"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? Str(c, "Id") ?? "?").TrimStart('/');
            var labels = c["Labels"] as JsonObject;
            var image = Str(c, "Image") ?? "";
            var imageInfo = Str(c, "ImageID") is { } imageId ? byId.GetValueOrDefault(imageId) : null;

            // Swarm: "nginx:1.27@sha256:…"; umbenanntes Tag: nur die Image-ID, dann den ersten Tag des Images nehmen
            image = image.Split('@')[0];
            if (image.StartsWith("sha256:", StringComparison.Ordinal))
            {
                image = Strings(imageInfo?["RepoTags"]).FirstOrDefault(t => !t.StartsWith("<none>", StringComparison.Ordinal)) ?? image;
            }

            var repoDigests = Strings(imageInfo?["RepoDigests"]);
            var reference = repoDigests.Count == 0 ? null : ImageReference.Parse(image);
            result.Add(new DockerContainer(name, Str(labels, "com.docker.compose.project"), Str(labels, "com.docker.stack.namespace"),
                Str(labels, "com.docker.compose.service") ?? Str(labels, "com.docker.swarm.service.name"), image, reference?.Normalized,
                reference?.LocalDigest(repoDigests), Str(c, "State")));
        }

        return result;
    }

    /// <summary>Compose: Label com.docker.compose.project = Stackname (Compose schreibt ihn klein). Swarm: com.docker.stack.namespace.</summary>
    public static IReadOnlyList<PortainerContainer> ContainersOf(PortainerStack stack, IReadOnlyList<DockerContainer> containers) =>
        containers
            .Where(c => stack.Type == SwarmStack
                ? string.Equals(c.SwarmStack, stack.Name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(c.Project, stack.Name, StringComparison.OrdinalIgnoreCase))
            .Select(c => new PortainerContainer(c.Name, c.Service, c.Image, c.Reference, c.LocalDigest, c.State))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();

    /// <summary>true = Registry hat einen neueren Digest, false = aktuell, null = unbekannt (lokal gebaut, noch nicht geprüft, Fehler).</summary>
    public static bool? IsOutdated(PortainerContainer container, IReadOnlyDictionary<string, ImageCheck> checks) =>
        container.Reference is null || container.LocalDigest is null || !checks.TryGetValue(container.Reference, out var check) || check.RemoteDigest is null
            ? null
            : !container.LocalDigest.Contains(check.RemoteDigest, StringComparison.Ordinal);

    private static List<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();

    private static string? Str(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')) : null;

    private static long? Long(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v
            ? v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<double>(out var d) ? (long)d
            : long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null
            : null;
}

/// <param name="handler">Nur für Tests: feste Antworten statt echter Verbindung.</param>
public sealed class PortainerClientFactory(IntegrationHttp http, Func<HttpMessageHandler>? handler = null)
{
    /// <summary>Neuer Client je Aufruf (TLS-Prüfung hängt am Fingerprint der Integration). Mit using verwenden.</summary>
    public PortainerClient For(Integration integration)
    {
        if (handler is not null)
        {
            return new PortainerClient(integration, http.Unprotect(integration), handler()) { PollDelay = TimeSpan.Zero };
        }

        return new PortainerClient(integration, http.Unprotect(integration), IntegrationHttp.CreateHandler(integration.TlsFingerprintSha256, out var fingerprint), fingerprint);
    }
}
