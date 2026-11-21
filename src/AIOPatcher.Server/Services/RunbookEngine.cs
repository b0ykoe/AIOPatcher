using AIOPatcher.Server.Services.Integrations;
using AIOPatcher.Server.Services.Notifications;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>
/// Führt Abläufe aus: Welle für Welle Patch-Jobs anlegen, Ergebnis prüfen (Job erfolgreich, Host meldet sich
/// danach wieder, Neustart erledigt), bei Fehlern je nach Strategie anhalten. Startet geplante Abläufe im Fenster.
/// Die Logik ist statisch und bekommt die Zeit übergeben, damit sie testbar ist.
/// </summary>
public sealed class RunbookEngine(IServiceScopeFactory scopes, ServerClock clock, Notifier notifier, ILogger<RunbookEngine> logger, FeatureFlags features) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>So lange darf ein Host nach erfolgreichem Patch-Job brauchen, um sich zurückzumelden.</summary>
    public static readonly TimeSpan VerifyTimeout = TimeSpan.FromMinutes(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            if (!features.IsOn(Feature.Automation))
            {
                continue; // Notaus: Abläufe stehen, bis wieder eingeschaltet wird
            }

            try
            {
                using var scope = scopes.CreateScope();
                await TickAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), clock.Zone, DateTime.UtcNow, stoppingToken, notifier,
                    scope.ServiceProvider.GetRequiredService<IRunbookPreparation>());
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Runbook engine failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>So lange darf ein Snapshot dauern (Hetzner-Snapshots großer Server brauchen etwas).</summary>
    public static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(60);

    public static async Task TickAsync(AppDbContext db, TimeZoneInfo zone, DateTime now, CancellationToken ct, Notifier? notifier = null, IRunbookPreparation? preparation = null)
    {
        var before = await db.RunbookRuns
            .Where(r => r.Status == RunStatus.Running || r.Status == RunStatus.WaitingApproval || r.Status == RunStatus.Paused)
            .Select(r => new { r.Id, r.Status })
            .ToDictionaryAsync(r => r.Id, r => r.Status, ct);

        await ScheduleAsync(db, zone, now, ct);

        var runs = await db.RunbookRuns
            .Include(r => r.Runbook)
            .Include(r => r.Waves)
            .Include(r => r.Items)
            .Where(r => r.Status == RunStatus.Running)
            .ToListAsync(ct);

        foreach (var run in runs)
        {
            await AdvanceAsync(db, run, now, ct, preparation);
        }

        // Nur Läufe, die in diesem Durchlauf entstanden sind oder ihren Status gewechselt haben.
        db.ChangeTracker.DetectChanges();
        var changedRuns = db.ChangeTracker.Entries<RunbookRun>()
            .Where(e => e.State == EntityState.Added || (e.Property(r => r.Status).IsModified && (!before.TryGetValue(e.Entity.Id, out var old) || old != e.Entity.Status)))
            .Select(e => e.Entity)
            .ToList();

        await db.SaveChangesAsync(ct);

        if (notifier is not null)
        {
            foreach (var run in changedRuns)
            {
                if (NotificationFor(run) is { } notification)
                {
                    notifier.Publish(notification);
                }
            }
        }
    }

    private static Notification? NotificationFor(RunbookRun run)
    {
        var name = run.Runbook?.Name ?? "?";
        var link = $"/runs/{run.Id}";
        return run.Status switch
        {
            RunStatus.Paused => new Notification(NotificationEvents.RunNeedsAttention,
                L.T($"Patch-Plan „{name}“ angehalten", $"Patch plan \"{name}\" paused"), run.Message ?? "", NotificationSeverity.Warning, LinkPath: link),
            RunStatus.WaitingApproval => new Notification(NotificationEvents.RunNeedsAttention,
                L.T($"Patch-Plan „{name}“ wartet auf Freigabe", $"Patch plan \"{name}\" waiting for approval"), run.Message ?? "", NotificationSeverity.Info, LinkPath: link),
            RunStatus.Succeeded => new Notification(NotificationEvents.RunFinished,
                L.T($"Patch-Plan „{name}“ abgeschlossen", $"Patch plan \"{name}\" finished"), run.Message ?? "", NotificationSeverity.Success, LinkPath: link),
            RunStatus.Failed => new Notification(NotificationEvents.RunFinished,
                L.T($"Patch-Plan „{name}“ mit Fehlern beendet", $"Patch plan \"{name}\" finished with errors"), run.Message ?? "", NotificationSeverity.Critical, LinkPath: link),
            _ => null,
        };
    }

    // --- Start ------------------------------------------------------------------------------------

    public static bool IsActive(RunStatus status) => status is RunStatus.Running or RunStatus.WaitingApproval or RunStatus.Paused;

    /// <summary>Friert Wellen und Hosts ein und legt den Lauf an. Speichert nicht selbst.</summary>
    public static async Task<RunbookRun> StartRunAsync(AppDbContext db, Guid runbookId, string requestedBy, DateTime now, DateTime? notAfter, CancellationToken ct)
    {
        var runbook = await db.Runbooks
                          .Include(r => r.Waves)
                          .SingleOrDefaultAsync(r => r.Id == runbookId, ct)
                      ?? throw new InvalidOperationException(L.T("Patch-Plan existiert nicht.", "Patch plan does not exist."));

        if (await db.RunbookRuns.AnyAsync(r => r.RunbookId == runbookId && (r.Status == RunStatus.Running || r.Status == RunStatus.WaitingApproval || r.Status == RunStatus.Paused), ct))
        {
            throw new InvalidOperationException(L.T("Für diesen Patch-Plan ist bereits ein Lauf aktiv.", "This patch plan already has an active run."));
        }

        // Mitglieder beim Start einfrieren: ausgewählte Hosts, Tag-/Site-Regeln, "alle übrigen"
        var membership = await PatchPlans.ResolveAsync(db, ct);
        var memberIds = membership.Where(m => m.Value.PlanId == runbook.Id).ToLookup(m => m.Value.GroupId, m => m.Key);
        var allIds = memberIds.SelectMany(g => g).ToList();
        var names = await db.Hosts.Where(h => allIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, ct);
        var waves = runbook.Waves.Where(w => memberIds[w.Id].Any()).OrderBy(w => w.Order).ToList();
        if (waves.Count == 0)
        {
            throw new InvalidOperationException(L.T("Der Patch-Plan enthält keine Hosts.", "The patch plan contains no hosts."));
        }

        var run = new RunbookRun
        {
            RunbookId = runbook.Id,
            Runbook = runbook,
            RequestedBy = requestedBy,
            CreatedAt = now,
            NotAfter = notAfter,
            FailurePolicy = runbook.FailurePolicy,
            CurrentWave = waves[0].Order,
        };

        foreach (var wave in waves)
        {
            run.Waves.Add(new RunbookRunWave
            {
                Order = wave.Order,
                Name = wave.Name,
                Scope = wave.Scope,
                AllowReboot = wave.AllowReboot,
                RequireApproval = wave.RequireApproval,
                SnapshotBeforePatch = wave.SnapshotBeforePatch,
                RequireBackupWithinHours = wave.RequireBackupWithinHours,
                PauseAfterMinutes = wave.PauseAfterMinutes,
            });
            foreach (var hostId in memberIds[wave.Id].OrderBy(id => names.GetValueOrDefault(id), StringComparer.OrdinalIgnoreCase))
            {
                run.Items.Add(new RunbookRunItem { WaveOrder = wave.Order, HostId = hostId, HostName = names.GetValueOrDefault(hostId) ?? "?" });
            }
        }

        if (waves[0].RequireApproval)
        {
            run.Status = RunStatus.WaitingApproval;
            run.Message = WaitingForApproval(waves[0].Name);
        }

        db.RunbookRuns.Add(run);
        return run;
    }

    // --- Fortschritt --------------------------------------------------------------------------------

    public static async Task AdvanceAsync(AppDbContext db, RunbookRun run, DateTime now, CancellationToken ct, IRunbookPreparation? preparation = null)
    {
        if (run.ResumeAt is { } resume)
        {
            if (now < resume)
            {
                return; // Pause nach der vorigen Gruppe
            }

            run.ResumeAt = null;
            run.Message = null;
        }

        var wave = run.Waves.Single(w => w.Order == run.CurrentWave);
        var items = run.Items.Where(i => i.WaveOrder == run.CurrentWave).ToList();

        var jobIds = items.Where(i => i.JobId != null).Select(i => i.JobId!.Value).ToList();
        var jobs = await db.Jobs.Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, ct);
        var hostIds = items.Where(i => i.HostId != null).Select(i => i.HostId!.Value).ToList();
        var hosts = await db.Hosts.Include(h => h.Integration).Where(h => hostIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);

        // 1) Laufende Snapshots, Jobs und Prüfungen auswerten
        foreach (var item in items)
        {
            if (item.Status == RunItemStatus.Preparing)
            {
                await PollPreparationAsync(item, item.HostId is { } hid ? hosts.GetValueOrDefault(hid) : null, preparation, now, ct);
            }

            if (item.Status == RunItemStatus.Running)
            {
                if (item.JobId is not { } jobId || !jobs.TryGetValue(jobId, out var job))
                {
                    Finish(item, RunItemStatus.Failed, L.T("Job nicht mehr vorhanden.", "Job no longer exists."), now);
                    continue;
                }

                switch (job.Status)
                {
                    case JobStatus.Succeeded:
                        item.Status = RunItemStatus.Verifying;
                        item.VerifyDeadline = now + VerifyTimeout;
                        item.Message = L.T("Prüfe, ob sich der Host zurückmeldet …", "Checking whether the host reports back …");
                        break;
                    case JobStatus.Failed or JobStatus.Rejected or JobStatus.Cancelled:
                        Finish(item, RunItemStatus.Failed, job.Summary ?? $"Job {job.Status}.", now);
                        break;
                }
            }

            if (item.Status == RunItemStatus.Verifying)
            {
                Verify(item, item.HostId is { } id ? hosts.GetValueOrDefault(id) : null, item.JobId is { } jid ? jobs.GetValueOrDefault(jid) : null, wave, now);
            }
        }

        // Fehler halten den Lauf an, außer sie sind bestätigt (Fortsetzen) oder der Plan macht ohnehin weiter.
        var acknowledged = run.AcknowledgedWave == run.CurrentWave || run.FailurePolicy == RunbookFailurePolicy.ContinueAndReport;
        var hasFailures = items.Any(i => i.Status == RunItemStatus.Failed) && !acknowledged;
        var stopNow = hasFailures && run.FailurePolicy == RunbookFailurePolicy.StopImmediately;
        var windowClosed = run.NotAfter is { } end && now >= end;

        // 2) Offene Hosts dieser Welle starten
        if (!stopNow && !windowClosed)
        {
            await StartPendingAsync(db, run, wave, items, hosts, now, ct, preparation);
        }

        var inFlight = items.Any(i => i.Status is RunItemStatus.Preparing or RunItemStatus.Running or RunItemStatus.Verifying);
        if (inFlight)
        {
            return;
        }

        // 3) Welle abgeschlossen oder angehalten?
        if (stopNow)
        {
            Pause(run, L.T($"Gruppe „{wave.Name}“: Fehler, weitere Hosts nicht gestartet. Fortsetzen oder abbrechen.",
                $"Group \"{wave.Name}\": error, remaining hosts not started. Continue or cancel."));
            return;
        }

        if (items.Any(i => i.Status == RunItemStatus.Pending))
        {
            if (windowClosed)
            {
                Pause(run, L.T("Wartungsfenster vorbei, offene Hosts nicht gestartet. Fortsetzen oder abbrechen.",
                    "Maintenance window over, pending hosts not started. Continue or cancel."));
            }

            return; // wartet (z. B. auf einen anderen Job des Hosts)
        }

        if (hasFailures)
        {
            var failed = items.Count(i => i.Status == RunItemStatus.Failed);
            Pause(run, L.T($"Gruppe „{wave.Name}“: {failed} Host(s) fehlgeschlagen. Fortsetzen (nächste Gruppe) oder abbrechen.",
                $"Group \"{wave.Name}\": {failed} host(s) failed. Continue (next group) or cancel."));
            return;
        }

        var next = run.Waves.Where(w => w.Order > run.CurrentWave).OrderBy(w => w.Order).FirstOrDefault();
        if (next is null)
        {
            var failedTotal = run.Items.Count(i => i.Status == RunItemStatus.Failed);
            var skippedTotal = run.Items.Count(i => i.Status == RunItemStatus.Skipped);
            run.Status = failedTotal > 0 ? RunStatus.Failed : RunStatus.Succeeded;
            run.FinishedAt = now;
            var succeededTotal = run.Items.Count(i => i.Status == RunItemStatus.Succeeded);
            run.Message = L.T(
                $"{succeededTotal} erfolgreich" +
                (failedTotal > 0 ? $", {failedTotal} fehlgeschlagen" : "") +
                (skippedTotal > 0 ? $", {skippedTotal} übersprungen" : "") + ".",
                $"{succeededTotal} succeeded" +
                (failedTotal > 0 ? $", {failedTotal} failed" : "") +
                (skippedTotal > 0 ? $", {skippedTotal} skipped" : "") + ".");
            return;
        }

        run.CurrentWave = next.Order;
        if (wave.PauseAfterMinutes is int pause and > 0)
        {
            run.ResumeAt = now.AddMinutes(pause);
            run.Message = L.T($"Pause nach „{wave.Name}“ bis {run.ResumeAt:HH:mm} UTC.", $"Pause after \"{wave.Name}\" until {run.ResumeAt:HH:mm} UTC.");
        }

        if (next.RequireApproval && !next.Approved)
        {
            run.Status = RunStatus.WaitingApproval;
            run.Message = WaitingForApproval(next.Name);
        }
        else if (windowClosed)
        {
            Pause(run, L.T($"Wartungsfenster vorbei, Gruppe „{next.Name}“ nicht gestartet. Fortsetzen oder abbrechen.",
                $"Maintenance window over, group \"{next.Name}\" not started. Continue or cancel."));
        }
        else if (run.ResumeAt is null)
        {
            run.Message = null;
        }
    }

    private static async Task StartPendingAsync(AppDbContext db, RunbookRun run, RunbookRunWave wave, List<RunbookRunItem> items, Dictionary<Guid, ManagedHost> hosts, DateTime now, CancellationToken ct, IRunbookPreparation? preparation)
    {
        var pending = new List<RunbookRunItem>();
        foreach (var item in items.Where(i => i.Status == RunItemStatus.Pending))
        {
            var host = item.HostId is { } id ? hosts.GetValueOrDefault(id) : null;
            if (host is null)
            {
                Finish(item, RunItemStatus.Skipped, L.T("Host existiert nicht mehr.", "Host no longer exists."), now);
            }
            else if (host.IsRevoked)
            {
                Finish(item, RunItemStatus.Skipped, L.T("Host ist gesperrt.", "Host is revoked."), now);
            }
            else
            {
                pending.Add(item);
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        // Pause gilt nur für geplante Läufe; manuell gestartete Läufe hat jemand bewusst angestoßen.
        if (run.RequestedBy == AutoPatchScheduler.Actor && run.NotAfter != null) // nach "Fortsetzen" ist NotAfter leer
        {
            var siteIds = pending.Select(i => hosts[i.HostId!.Value].SiteId).Distinct().ToList();
            if (await PatchRules.ActiveFreezeAsync(db, siteIds, now, ct) is { } freeze)
            {
                Pause(run, PatchRules.FreezeNote(freeze) + L.T(" Fortsetzen oder abbrechen.", " Continue or cancel."));
                return;
            }
        }

        if (run.RequestedBy == AutoPatchScheduler.Actor)
        {
            var todo = new List<RunbookRunItem>();
            foreach (var item in pending)
            {
                if (item.SnapshotTakenAt is null && await PatchPlans.NothingToDoAsync(db, hosts[item.HostId!.Value], wave.Scope, wave.AllowReboot, now, ct))
                {
                    Finish(item, RunItemStatus.Skipped, L.T("Nichts zu tun: keine offenen Updates laut letztem Scan.", "Nothing to do: no pending updates according to the last scan."), now);
                }
                else
                {
                    todo.Add(item);
                }
            }

            pending = todo;
            if (pending.Count == 0)
            {
                return;
            }
        }

        // Erst die VMs, dann der Hypervisor (sein Neustart reißt sonst laufende VM-Updates mit)
        pending = await GuestsBeforeHypervisorsAsync(db, items, pending, hosts, ct);
        if (pending.Count == 0)
        {
            return;
        }

        // Vorbedingungen und Snapshot: Hosts, die noch vorbereitet werden, bekommen in diesem Durchlauf keinen Job.
        if (wave.SnapshotBeforePatch || wave.RequireBackupWithinHours is not null)
        {
            var ready = new List<RunbookRunItem>();
            foreach (var item in pending)
            {
                if (await PrepareAsync(item, hosts[item.HostId!.Value], wave, preparation, now, ct))
                {
                    ready.Add(item);
                }
            }

            pending = ready;
            if (pending.Count == 0)
            {
                return;
            }
        }

        var plan = await JobPlanner.PlanAsync(db, pending.Select(i => i.HostId!.Value).ToList(), JobType.Patch, wave.Scope, wave.AllowReboot,
            $"runbook:{run.Runbook?.Name ?? "runbook"}", ct);

        foreach (var item in pending)
        {
            var job = plan.Created.FirstOrDefault(j => j.HostId == item.HostId);
            if (job is null)
            {
                item.Message = L.T("Wartet: Für den Host läuft gerade ein anderer Job.", "Waiting: another job is currently running for this host.");
                continue;
            }

            job.CreatedAt = now;
            job.NotAfter = run.NotAfter; // geplanter Lauf: nur innerhalb des Fensters starten
            item.Status = RunItemStatus.Running;
            item.JobId = job.Id;
            item.StartedAt = now;
            item.Message = null;
        }
    }

    /// <summary>Proxmox-Host (Hypervisor) laut Betriebssystem.</summary>
    public static bool IsHypervisor(ManagedHost host) => (host.OsName ?? "").Contains("proxmox", StringComparison.OrdinalIgnoreCase);

    /// <summary>Proxmox-Knoten einer zugeordneten VM bzw. eines Containers ("pve1/qemu/101" → "pve1"), sonst null.</summary>
    public static string? GuestNode(ManagedHost host) =>
        host.Integration?.Type == IntegrationType.Proxmox && host.ExternalId?.Split('/')[0] is { Length: > 0 } node ? node : null;

    /// <summary>Läuft die VM auf diesem Hypervisor? Proxmox nennt Knoten nach dem kurzen Hostnamen ("pve1.lan" → "pve1").</summary>
    public static bool RunsOn(ManagedHost guest, ManagedHost hypervisor) =>
        GuestNode(guest) is { } node && (Short(hypervisor.Name) == node.ToLowerInvariant() || Short(hypervisor.Hostname) == node.ToLowerInvariant());

    private static string Short(string? name) => (name ?? "").Split('.')[0].ToLowerInvariant();

    /// <summary>
    /// Reihenfolge VMs vor Hypervisor. Ein Hypervisor startet erst, wenn alle anderen Hosts seiner Gruppe fertig sind
    /// (ohne Zuordnung wissen wir nicht, welche VMs auf ihm laufen, also zuletzt) und keine seiner zugeordneten VMs gerade
    /// einen Job hat (auch aus einem anderen Plan). Eine zugeordnete VM wartet, solange ihr Hypervisor einen Job hat.
    /// </summary>
    public static async Task<List<RunbookRunItem>> GuestsBeforeHypervisorsAsync(AppDbContext db, List<RunbookRunItem> items, List<RunbookRunItem> pending,
        Dictionary<Guid, ManagedHost> hosts, CancellationToken ct)
    {
        var candidates = pending.Where(i => hosts[i.HostId!.Value] is var h && (IsHypervisor(h) || GuestNode(h) != null)).ToList();
        if (candidates.Count == 0)
        {
            return pending;
        }

        var busyIds = await db.Jobs.Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
            .Where(j => j.Type == JobType.Patch || j.Type == JobType.Reboot || j.Type == JobType.AppUpdate)
            .Select(j => j.HostId).Distinct().ToListAsync(ct);
        var busy = busyIds.Count == 0 ? [] : await db.Hosts.Include(h => h.Integration).Where(h => busyIds.Contains(h.Id)).ToListAsync(ct);

        var ready = new List<RunbookRunItem>();
        foreach (var item in pending)
        {
            var host = hosts[item.HostId!.Value];
            if (IsHypervisor(host))
            {
                var othersOpen = items.Count(i => i != item && i.HostId is { } id && hosts.TryGetValue(id, out var other) && !IsHypervisor(other)
                                                  && i.Status is RunItemStatus.Pending or RunItemStatus.Preparing or RunItemStatus.Running or RunItemStatus.Verifying);
                if (othersOpen > 0)
                {
                    item.Message = L.T($"Wartet: Erst die übrigen Hosts der Gruppe ({othersOpen}), der Hypervisor kommt zuletzt.",
                        $"Waiting: the other hosts of the group first ({othersOpen}), the hypervisor goes last.");
                    continue;
                }

                var guestsBusy = busy.Count(g => g.Id != host.Id && RunsOn(g, host));
                if (guestsBusy > 0)
                {
                    item.Message = L.T($"Wartet: {guestsBusy} VM(s) auf diesem Hypervisor werden gerade gepatcht.",
                        $"Waiting: {guestsBusy} VM(s) on this hypervisor are being patched.");
                    continue;
                }
            }
            else if (busy.Any(h => h.Id != host.Id && IsHypervisor(h) && RunsOn(host, h)))
            {
                item.Message = L.T("Wartet: Der Hypervisor dieser VM wird gerade gepatcht.", "Waiting: this VM's hypervisor is being patched.");
                continue;
            }

            ready.Add(item);
        }

        return ready;
    }

    /// <summary>
    /// Erfolg = der Host hat sich nach Job-Ende wieder gemeldet (bei Neustart: nach dem Boot) und,
    /// falls ein Neustart erlaubt war, steht keiner mehr aus.
    /// </summary>
    private static void Verify(RunbookRunItem item, ManagedHost? host, PatchJob? job, RunbookRunWave wave, DateTime now)
    {
        if (host is null || job is null)
        {
            Finish(item, RunItemStatus.Failed, L.T("Host oder Job nicht mehr vorhanden.", "Host or job no longer exists."), now);
            return;
        }

        var reportedBack = host.LastSeenAt is { } seen && job.FinishedAt is { } done && seen > done;
        if (!reportedBack)
        {
            if (now > item.VerifyDeadline)
            {
                Finish(item, RunItemStatus.Failed, L.T($"Host hat sich {VerifyTimeout.TotalMinutes:0} Minuten nach dem Patch nicht zurückgemeldet.",
                    $"Host did not report back within {VerifyTimeout.TotalMinutes:0} minutes after the patch."), now);
            }

            return;
        }

        if (host.RebootRequired && wave.AllowReboot)
        {
            Finish(item, RunItemStatus.Failed, L.T("Neustart steht nach dem Patch weiterhin aus.", "A reboot is still pending after the patch."), now);
            return;
        }

        var note = host.RebootRequired
            ? L.T("Gepatcht, Neustart steht aus (in dieser Gruppe nicht erlaubt).", "Patched, reboot pending (not allowed in this group).")
            : job.RebootPerformed
                ? L.T("Gepatcht und neu gestartet, Host ist wieder online.", "Patched and rebooted, host is back online.")
                : L.T("Gepatcht, Host ist online.", "Patched, host is online.");
        Finish(item, RunItemStatus.Succeeded, note, now);
    }

    /// <summary>Prüft das Backup und startet ggf. den Snapshot. true = Host ist bereit für den Patch-Job.</summary>
    private static async Task<bool> PrepareAsync(RunbookRunItem item, ManagedHost host, RunbookRunWave wave, IRunbookPreparation? preparation, DateTime now, CancellationToken ct)
    {
        if (item.SnapshotTakenAt is not null)
        {
            return true; // Snapshot fertig, Backup wurde davor geprüft
        }

        if (preparation is null)
        {
            Finish(item, RunItemStatus.Failed, L.T("Snapshots/Backup-Prüfung sind nicht verfügbar.", "Snapshots/backup check are not available."), now);
            return false;
        }

        try
        {
            if (wave.RequireBackupWithinHours is { } hours)
            {
                var last = await preparation.LastBackupAsync(host, ct);
                if (last is null || now - last > TimeSpan.FromHours(hours))
                {
                    Finish(item, RunItemStatus.Failed, last is null
                        ? L.T("Vorbedingung: kein Backup gefunden.", "Precondition: no backup found.")
                        : L.T($"Vorbedingung: letztes Backup {last:yyyy-MM-dd HH:mm} UTC ist älter als {hours} Std.",
                            $"Precondition: last backup {last:yyyy-MM-dd HH:mm} UTC is older than {hours} h."), now);
                    return false;
                }
            }

            if (!wave.SnapshotBeforePatch)
            {
                return true;
            }

            var name = SnapshotNames.For(now);
            item.PendingTask = await preparation.StartSnapshotAsync(host, name, ct);
            item.SnapshotName = name;
            item.Status = RunItemStatus.Preparing;
            item.StartedAt = now;
            item.PrepareDeadline = now + PrepareTimeout;
            item.Message = L.T($"Snapshot {name} läuft …", $"Snapshot {name} in progress …");
            return false;
        }
        catch (IntegrationException ex)
        {
            Finish(item, RunItemStatus.Failed, L.T($"Vorbereitung fehlgeschlagen: {ex.Message}", $"Preparation failed: {ex.Message}"), now);
            return false;
        }
    }

    private static async Task PollPreparationAsync(RunbookRunItem item, ManagedHost? host, IRunbookPreparation? preparation, DateTime now, CancellationToken ct)
    {
        if (host is null || preparation is null || item.PendingTask is null)
        {
            Finish(item, RunItemStatus.Failed, L.T("Snapshot-Status nicht abrufbar.", "Cannot query snapshot status."), now);
            return;
        }

        ProviderTaskStatus status;
        try
        {
            status = await preparation.GetTaskAsync(host, item.PendingTask, ct);
        }
        catch (IntegrationException ex)
        {
            status = new ProviderTaskStatus(ProviderTaskState.Running, ex.Message); // kurz nicht erreichbar: weiter warten bis zum Timeout
        }

        switch (status.State)
        {
            case ProviderTaskState.Succeeded:
                item.Status = RunItemStatus.Pending;
                item.PendingTask = null;
                item.SnapshotTakenAt = now;
                item.PrepareDeadline = null;
                item.Message = L.T($"Snapshot {item.SnapshotName} erstellt.", $"Snapshot {item.SnapshotName} created.");
                await preparation.PruneSnapshotsAsync(host, ct);
                break;
            case ProviderTaskState.Failed:
                Finish(item, RunItemStatus.Failed, L.T($"Snapshot fehlgeschlagen: {status.Error}", $"Snapshot failed: {status.Error}"), now);
                break;
            default:
                if (item.PrepareDeadline is { } deadline && now > deadline)
                {
                    Finish(item, RunItemStatus.Failed, L.T("Snapshot nicht rechtzeitig fertig geworden.", "Snapshot did not finish in time."), now);
                }

                break;
        }
    }

    private static void Finish(RunbookRunItem item, RunItemStatus status, string message, DateTime now)
    {
        item.Status = status;
        item.Message = message;
        item.FinishedAt = now;
        item.VerifyDeadline = null;
    }

    private static string WaitingForApproval(string wave) =>
        L.T($"Wartet auf Freigabe für Gruppe „{wave}“.", $"Waiting for approval of group \"{wave}\".");

    private static void Pause(RunbookRun run, string message)
    {
        run.Status = RunStatus.Paused;
        run.Message = message;
    }

    // --- Eingriffe aus dem Dashboard ----------------------------------------------------------------

    /// <summary>Angehaltenen Lauf fortsetzen: Fehler der aktuellen Welle gelten als bestätigt, Fenstergrenze entfällt.</summary>
    public static string? Continue(RunbookRun run)
    {
        if (run.Status != RunStatus.Paused)
        {
            return L.T("Nur angehaltene Läufe können fortgesetzt werden.", "Only paused runs can be continued.");
        }

        run.AcknowledgedWave = run.CurrentWave;
        run.NotAfter = null;
        run.Status = RunStatus.Running;
        run.Message = null;
        return null;
    }

    public static string? Approve(RunbookRun run)
    {
        if (run.Status != RunStatus.WaitingApproval)
        {
            return L.T("Der Lauf wartet nicht auf eine Freigabe.", "The run is not waiting for approval.");
        }

        run.Waves.Single(w => w.Order == run.CurrentWave).Approved = true;
        run.Status = RunStatus.Running;
        run.Message = null;
        return null;
    }

    /// <summary>Abbrechen: geplante Jobs des Laufs zurückziehen. Laufende Jobs laufen zu Ende (halbes apt ist schlimmer).</summary>
    public static async Task<string?> CancelAsync(AppDbContext db, RunbookRun run, DateTime now, CancellationToken ct)
    {
        if (!IsActive(run.Status))
        {
            return L.T("Der Lauf ist bereits beendet.", "The run has already finished.");
        }

        var jobIds = run.Items.Where(i => i.JobId != null).Select(i => i.JobId!.Value).ToList();
        var withdrawn = new HashSet<Guid>();
        foreach (var job in await db.Jobs.Where(j => j.Status == JobStatus.Queued && jobIds.Contains(j.Id)).ToListAsync(ct))
        {
            job.Status = JobStatus.Cancelled;
            job.FinishedAt = now;
            job.Summary = L.T("Patch-Plan abgebrochen.", "Patch plan cancelled.");
            withdrawn.Add(job.Id);
        }

        foreach (var item in run.Items)
        {
            switch (item.Status)
            {
                case RunItemStatus.Pending:
                case RunItemStatus.Running when item.JobId is { } id && withdrawn.Contains(id):
                    Finish(item, RunItemStatus.Skipped, L.T("Patch-Plan abgebrochen.", "Patch plan cancelled."), now);
                    break;
                case RunItemStatus.Preparing:
                    Finish(item, RunItemStatus.Skipped, L.T($"Patch-Plan abgebrochen; Snapshot {item.SnapshotName} bleibt erhalten.",
                        $"Patch plan cancelled; snapshot {item.SnapshotName} is kept."), now);
                    break;
                case RunItemStatus.Running or RunItemStatus.Verifying:
                    // Der Agent patcht bereits: das lässt sich nicht stoppen, Ergebnis steht im Job-Log.
                    Finish(item, RunItemStatus.Skipped, L.T("Patch-Plan abgebrochen; der laufende Patch wird zu Ende geführt (siehe Job-Log).",
                        "Patch plan cancelled; the running patch completes (see job log)."), now);
                    break;
            }
        }

        run.Status = RunStatus.Cancelled;
        run.FinishedAt = now;
        run.Message = L.T("Abgebrochen.", "Cancelled.");
        return null;
    }

    // --- Zeitplan -------------------------------------------------------------------------------------

    private static async Task ScheduleAsync(AppDbContext db, TimeZoneInfo zone, DateTime now, CancellationToken ct)
    {
        var runbooks = await db.Runbooks.Where(r => r.ScheduleEnabled && r.ScheduleDays != MaintenanceDays.None).ToListAsync(ct);
        foreach (var runbook in runbooks)
        {
            if (MaintenanceWindow.Current(runbook.ScheduleDays, runbook.ScheduleStart, runbook.ScheduleDurationMinutes, now, zone) is not { } window
                || runbook.LastWindowStart == window.StartUtc)
            {
                continue;
            }

            runbook.LastWindowStart = window.StartUtc;
            var members = (await PatchPlans.ResolveAsync(db, ct)).Where(m => m.Value.PlanId == runbook.Id).Select(m => m.Key).ToList();
            var siteIds = await db.Hosts.Where(h => members.Contains(h.Id)).Select(h => h.SiteId).Distinct().ToListAsync(ct);
            if (await PatchRules.ActiveFreezeAsync(db, siteIds, now, ct) is { } freeze)
            {
                runbook.LastWindowNote = L.T("Nicht gestartet: ", "Not started: ") + PatchRules.FreezeNote(freeze);
                continue;
            }

            try
            {
                await StartRunAsync(db, runbook.Id, AutoPatchScheduler.Actor, now, window.EndUtc, ct);
                runbook.LastWindowNote = L.T("Lauf gestartet.", "Run started.");
                db.AuditEntries.Add(new AuditEntry { Actor = AutoPatchScheduler.Actor, Action = "runbook.run-started", Details = runbook.Name });
            }
            catch (InvalidOperationException ex)
            {
                runbook.LastWindowNote = L.T($"Nicht gestartet: {ex.Message}", $"Not started: {ex.Message}");
            }
        }
    }
}
