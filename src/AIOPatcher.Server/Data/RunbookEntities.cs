using AIOPatcher.Contracts;

namespace AIOPatcher.Server.Data;

public enum RunbookFailurePolicy
{
    /// <summary>Laufende Welle zu Ende führen, dann anhalten.</summary>
    FinishWaveThenPause,

    /// <summary>Sofort keine weiteren Hosts mehr starten, dann anhalten.</summary>
    StopImmediately,
}

/// <summary>Ablauf: benannter Plan aus Wellen, optional mit eigenem Wartungsfenster.</summary>
public sealed class Runbook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public RunbookFailurePolicy FailurePolicy { get; set; } = RunbookFailurePolicy.FinishWaveThenPause;

    // Zeitplan (wie Host-Auto-Patch). Hosts eines geplanten Ablaufs sind vom Host-Auto-Patch ausgenommen.
    public bool ScheduleEnabled { get; set; }
    public MaintenanceDays ScheduleDays { get; set; } = MaintenanceDays.None;
    public TimeOnly ScheduleStart { get; set; } = new(3, 0);
    public int ScheduleDurationMinutes { get; set; } = 240;
    public DateTime? LastWindowStart { get; set; }
    public string? LastWindowNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<RunbookWave> Waves { get; set; } = [];
    public List<RunbookRun> Runs { get; set; } = [];
}

public sealed class RunbookWave
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunbookId { get; set; }
    public Runbook? Runbook { get; set; }

    public int Order { get; set; }
    public required string Name { get; set; }
    public PatchScope Scope { get; set; } = PatchScope.Security;
    public bool AllowReboot { get; set; }

    /// <summary>Vor dieser Welle auf Freigabe im Dashboard warten (z. B. vor dem PVE-Host).</summary>
    public bool RequireApproval { get; set; }

    /// <summary>Vor dem Patch einen Snapshot über Proxmox bzw. Hetzner Cloud anlegen (Host braucht eine Zuordnung).</summary>
    public bool SnapshotBeforePatch { get; set; }

    /// <summary>Vorbedingung: letztes Backup (Proxmox/PBS bzw. Hetzner-Backup) höchstens so viele Stunden alt. null = keine Prüfung.</summary>
    public int? RequireBackupWithinHours { get; set; }

    public List<RunbookWaveHost> Hosts { get; set; } = [];
}

public sealed class RunbookWaveHost
{
    public Guid WaveId { get; set; }
    public RunbookWave? Wave { get; set; }
    public Guid HostId { get; set; }
    public ManagedHost? Host { get; set; }
}

public enum RunStatus
{
    Running,
    WaitingApproval,
    Paused,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Eine Ausführung eines Ablaufs. Wellen und Hosts werden beim Start eingefroren (Lauf-Einträge).</summary>
public sealed class RunbookRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunbookId { get; set; }
    public Runbook? Runbook { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;
    public required string RequestedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    /// <summary>Welle, die gerade läuft bzw. als Nächstes startet.</summary>
    public int CurrentWave { get; set; }

    /// <summary>Geplanter Lauf: Ende des Wartungsfensters. Danach startet keine neue Welle mehr.</summary>
    public DateTime? NotAfter { get; set; }

    public RunbookFailurePolicy FailurePolicy { get; set; }
    public string? Message { get; set; }

    /// <summary>Welle, deren Fehler per "Fortsetzen" bestätigt wurden (dort wird nicht erneut angehalten).</summary>
    public int? AcknowledgedWave { get; set; }

    public List<RunbookRunWave> Waves { get; set; } = [];
    public List<RunbookRunItem> Items { get; set; } = [];
}

/// <summary>Eingefrorene Wellen-Einstellungen eines Laufs (spätere Änderungen am Ablauf wirken nicht rückwirkend).</summary>
public sealed class RunbookRunWave
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public RunbookRun? Run { get; set; }

    public int Order { get; set; }
    public required string Name { get; set; }
    public PatchScope Scope { get; set; }
    public bool AllowReboot { get; set; }
    public bool RequireApproval { get; set; }
    public bool Approved { get; set; }
    public bool SnapshotBeforePatch { get; set; }
    public int? RequireBackupWithinHours { get; set; }
}

public enum RunItemStatus
{
    Pending,

    /// <summary>Vorbereitung vor dem Patch: Snapshot läuft.</summary>
    Preparing,
    Running,
    Verifying,
    Succeeded,
    Failed,
    Skipped,
}

public sealed class RunbookRunItem
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public RunbookRun? Run { get; set; }

    public int WaveOrder { get; set; }

    /// <summary>null, wenn der Host inzwischen gelöscht wurde.</summary>
    public Guid? HostId { get; set; }
    public ManagedHost? Host { get; set; }

    /// <summary>Name zum Zeitpunkt des Starts (bleibt lesbar, auch wenn der Host gelöscht wird).</summary>
    public required string HostName { get; set; }

    public RunItemStatus Status { get; set; } = RunItemStatus.Pending;
    public Guid? JobId { get; set; }
    public string? Message { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>Bis wann sich der Host nach dem Patch zurückmelden muss.</summary>
    public DateTime? VerifyDeadline { get; set; }

    /// <summary>Name des Snapshots vor dem Patch (z. B. "aiop_20261116_0300").</summary>
    public string? SnapshotName { get; set; }

    /// <summary>Laufende Aufgabe beim Hypervisor/Cloud-Anbieter (Proxmox-UPID bzw. Hetzner-Action-ID).</summary>
    public string? PendingTask { get; set; }

    /// <summary>Gesetzt, sobald der Snapshot fertig ist; danach startet der Patch-Job.</summary>
    public DateTime? SnapshotTakenAt { get; set; }
    public DateTime? PrepareDeadline { get; set; }
}
