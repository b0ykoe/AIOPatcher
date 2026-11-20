namespace AIOPatcher.Server.Data;

/// <summary>
/// Update eines Portainer-Stacks ("Pull and redeploy" über die Portainer-API). Läuft auf dem Server, nicht über einen Agent,
/// deshalb eigene Tabelle statt PatchJob (der gehört immer zu einem Host).
/// </summary>
public sealed class StackUpdateJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IntegrationId { get; set; }
    public Integration? Integration { get; set; }

    /// <summary>Portainer-IDs von Stack und Umgebung (Endpoint).</summary>
    public int StackId { get; set; }
    public int EnvironmentId { get; set; }
    public required string StackName { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;
    public required string RequestedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Summary { get; set; }

    /// <summary>Protokoll (Zeilen mit Uhrzeit), gekürzt auf <see cref="MaxLog"/> Zeichen.</summary>
    public string? Log { get; set; }

    public const int MaxLog = 8000;
}
