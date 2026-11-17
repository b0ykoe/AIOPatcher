using AIOPatcher.Contracts;

namespace AIOPatcher.Server.Data;

/// <summary>
/// Auftrag an einen Pull-Agent (Protokoll v2). Entweder aus einem Job (JobId gesetzt, z. B. Patch) oder vom Server
/// selbst geplant, um Daten anzufordern (Inventar, Scan, Compliance, Zertifikat). Antworten werden nur zu einem
/// offenen Auftrag angenommen.
/// </summary>
public sealed class AgentTaskRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HostId { get; set; }
    public ManagedHost? Host { get; set; }
    public required string Module { get; set; }
    public required string Action { get; set; }
    public Guid? JobId { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Running;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public string? Summary { get; set; }
}
