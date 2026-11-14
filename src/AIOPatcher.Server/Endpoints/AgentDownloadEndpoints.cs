using AIOPatcher.Server.Services;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// Anonyme Downloads auf dem Agent-Port (Hosts erreichen nur diesen Port). Nichts davon ist geheim:
/// Echtheit sichert das Installationsskript über die gepinnte CA und die SHA-256-Prüfsumme.
/// </summary>
public static class AgentDownloadEndpoints
{
    public const string DownloadPrefix = "/agent/download/";
    public const string InstallScriptPath = "/agent/install.sh";
    public const string WindowsInstallScriptPath = "/agent/install.ps1";

    public sealed record AgentDownloadDto(string RuntimeId, bool Available, long? SizeBytes, string? Sha256);

    public static void MapAgentDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(InstallScriptPath, () =>
            AgentDownloads.FindScript("install.sh") is { } file
                ? Results.File(file.FullName, "text/x-shellscript; charset=utf-8")
                : Results.NotFound());

        // Windows: Der Dashboard-Befehl prüft die SHA-256 dieser Datei, bevor er sie ausführt.
        app.MapGet(WindowsInstallScriptPath, () =>
            AgentDownloads.FindScript("install.ps1") is { } file
                ? Results.File(file.FullName, "text/plain; charset=utf-8")
                : Results.NotFound());

        app.MapGet(DownloadPrefix + "{rid}", (string rid, AgentDownloads downloads) =>
            downloads.Find(rid) is { } file
                ? Results.File(file.FullName, "application/octet-stream", AgentDownloads.FileName(rid))
                : Results.NotFound());

        app.MapGet(DownloadPrefix + "{rid}/sha256", (string rid, AgentDownloads downloads) =>
            downloads.Find(rid) is { } file
                ? Results.Text(downloads.Sha256(file) + "\n")
                : Results.NotFound());

        // Dashboard: welche Agent-Builds liegen vor?
        app.MapGet("/api/agent-downloads", (AgentDownloads downloads) =>
            AgentDownloads.RuntimeIds.Select(rid => downloads.Find(rid) is { } f
                ? new AgentDownloadDto(rid, true, f.Length, downloads.Sha256(f))
                : new AgentDownloadDto(rid, false, null, null)))
            .RequireAuthorization();
    }
}
