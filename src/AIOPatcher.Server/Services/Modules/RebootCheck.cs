using System.Text.Json;
using System.Text.Json.Serialization;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services.Notifications;

namespace AIOPatcher.Server.Services.Modules;

/// <summary>Stand der laufenden Dienste/Container eines Hosts (letzte Meldung), Grundlage für den nächsten Neustart.</summary>
public sealed record ServiceSnapshot(
    DateTime TakenAt,
    DateTime BootedAt,
    List<string> Services,
    List<string>? Containers,
    List<string>? ContainersWithoutRestart);

/// <summary>Was nach einem Neustart fehlt. Kind: "service" oder "container"; Reason: "stopped" (systemd: failed, Windows: beendet), "noRestart" (Container ohne Restart-Policy) oder null.</summary>
public sealed record RebootCheckItem(string Kind, string Name, string? Reason);

/// <summary>Ergebnis des Vergleichs vor/nach einem Neustart. Missing schrumpft, wenn Dienste später doch noch starten.</summary>
public sealed record RebootCheckResult(
    DateTime BootedAt,
    DateTime BaselineAt,
    DateTime CheckedAt,
    int ServicesBefore,
    int ContainersBefore,
    List<RebootCheckItem> Missing,
    List<RebootCheckItem> Recovered);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServiceSnapshot))]
[JsonSerializable(typeof(RebootCheckResult))]
public sealed partial class RebootCheckJsonContext : JsonSerializerContext;

/// <summary>
/// Vergleicht laufende Dienste und Container vor und nach einem Neustart. Der Server holt den Stand regelmäßig und
/// direkt vor Patch- und Neustart-Jobs; nach einem Neustart wartet er <see cref="Settle"/>, damit langsame Dienste
/// hochkommen, und vergleicht dann.
/// </summary>
public static partial class RebootCheck
{
    /// <summary>So lange nach dem Systemstart warten, bevor verglichen wird.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(5);

    /// <summary>Regelmäßiger Stand (für Neustarts, die nicht AIOPatcher auslöst).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    /// <summary>Vor einem Patch-/Neustart-Job frisch holen, wenn der letzte Stand älter ist.</summary>
    public static readonly TimeSpan FreshBeforeJob = TimeSpan.FromMinutes(10);

    /// <summary>Startzeiten aus "jetzt − Uptime" schwanken um Sekunden; erst ab dieser Differenz ist es ein neuer Start.</summary>
    public static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    public static ServiceSnapshot? Snapshot(ManagedHost host) => Read(host.RunningSnapshot, RebootCheckJsonContext.Default.ServiceSnapshot);

    public static RebootCheckResult? Result(ManagedHost host) => Read(host.RebootCheck, RebootCheckJsonContext.Default.RebootCheckResult);

    /// <summary>Neuer Systemstart seit dem gespeicherten Stand?</summary>
    public static bool IsNewBoot(DateTime bootedAt, ServiceSnapshot? baseline) =>
        baseline is not null && bootedAt - baseline.BootedAt > BootTolerance;

    /// <summary>Nach einem Neustart: erst nach <see cref="Settle"/> und nach der neuen Systembeschreibung (sonst ist BootedAt noch alt).</summary>
    public static bool MustWait(ManagedHost host, ModuleContext context)
    {
        if (host.AgentRestartedAt is { } restarted && !(context.LastRequested(AgentModules.System, ModuleActions.Describe) >= restarted))
        {
            return true;
        }

        return host.BootedAt is { } booted && context.Now - booted < Settle;
    }

    /// <summary>
    /// Dienste, die nur bei Anmeldung oder Bedarf laufen und nach einem Neustart zu Recht fehlen: Login-Konsolen
    /// (getty@tty6 läuft erst, wenn jemand auf tty6 umschaltet), Benutzer-Sitzungen und unter Windows Dienste pro
    /// angemeldetem Benutzer (CDPUserSvc_1a2b3, die Endung wechselt bei jeder Anmeldung).
    /// </summary>
    public static bool IsSessionService(string name) =>
        SessionServicePattern().IsMatch(name);

    [System.Text.RegularExpressions.GeneratedRegex(@"^((serial-|container-)?getty@|autovt@|console-getty.|user@|user-runtime-dir@|session-[^.]+.scope$)|^[A-Za-z][A-Za-z0-9]+_[0-9a-f]{4,8}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SessionServicePattern();

    /// <summary>Was vorher lief und jetzt nicht (ohne Dienste, die nur bei Anmeldung laufen).</summary>
    public static List<RebootCheckItem> Compare(ServiceSnapshot before, RunningServices now)
    {
        var services = now.Services.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stopped = (now.StoppedServices ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = before.Services
            .Where(s => !services.Contains(s) && !IsSessionService(s))
            .Select(s => new RebootCheckItem("service", s, stopped.Contains(s) ? "stopped" : null))
            .ToList();

        // Docker beim zweiten Stand nicht erreichbar: keine Aussage über Container (sonst fehlen scheinbar alle)
        if (before.Containers is { } containersBefore && now.Containers is { } containersNow)
        {
            var running = containersNow.ToHashSet(StringComparer.Ordinal);
            var noRestart = (before.ContainersWithoutRestart ?? []).ToHashSet(StringComparer.Ordinal);
            missing.AddRange(containersBefore
                .Where(c => !running.Contains(c))
                .Select(c => new RebootCheckItem("container", c, noRestart.Contains(c) ? "noRestart" : null)));
        }

        return missing;
    }

    /// <summary>Neue Meldung auswerten: Vergleich nach einem Neustart, Nachzügler austragen, Stand speichern.</summary>
    public static void Apply(ManagedHost host, RunningServices report, DateTime now, Notifier? notifier)
    {
        if (report.Uptime < Settle)
        {
            return; // gerade erst gestartet: nicht als Vergleichs- oder Grundstand nehmen
        }

        var bootedAt = now - report.Uptime;
        var baseline = Snapshot(host);
        if (IsNewBoot(bootedAt, baseline))
        {
            var missing = Compare(baseline!, report);
            host.RebootCheck = Write(new RebootCheckResult(bootedAt, baseline!.TakenAt, now, baseline.Services.Count, baseline.Containers?.Count ?? 0, missing, []),
                RebootCheckJsonContext.Default.RebootCheckResult);
            if (missing.Count > 0)
            {
                notifier?.Publish(new Notification(NotificationEvents.RebootCheckFailed,
                    L.T($"{host.Name}: nach dem Neustart fehlen {missing.Count} Dienste/Container", $"{host.Name}: {missing.Count} services/containers missing after reboot"),
                    string.Join(", ", missing.Take(10).Select(m => m.Name)) + (missing.Count > 10 ? " …" : ""),
                    NotificationSeverity.Warning, host.SiteId, host.Id, $"/hosts/{host.Id}"));
            }
        }
        else if (Result(host) is { Missing.Count: > 0 } open && (bootedAt - open.BootedAt).Duration() <= BootTolerance)
        {
            // Gleicher Systemstart: was inzwischen läuft (langsamer Start, von Hand gestartet), ist nicht mehr offen
            var services = report.Services.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var containers = (report.Containers ?? []).ToHashSet(StringComparer.Ordinal);
            var recovered = open.Missing.Where(m => m.Kind == "service" ? services.Contains(m.Name) : containers.Contains(m.Name)).ToList();
            if (recovered.Count > 0)
            {
                host.RebootCheck = Write(open with
                {
                    CheckedAt = now,
                    Missing = open.Missing.Except(recovered).ToList(),
                    Recovered = [.. open.Recovered, .. recovered],
                }, RebootCheckJsonContext.Default.RebootCheckResult);
            }
        }

        host.RunningSnapshot = Write(new ServiceSnapshot(now, bootedAt, report.Services.Take(1000).ToList(), report.Containers?.Take(500).ToList(),
            report.ContainersWithoutRestart?.Take(500).ToList()), RebootCheckJsonContext.Default.ServiceSnapshot);
    }

    private static T? Read<T>(string? json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Write<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => JsonSerializer.Serialize(value, type);
}

/// <summary>services.list: regelmäßig, vor Patch-/Neustart-Jobs und einige Minuten nach jedem Neustart.</summary>
public sealed class ServicesServerModule : ServerModule
{
    public override string Id => AgentModules.Services;

    public override PlannedTask? Due(ManagedHost host, ModuleContext context, string? polledVersion)
    {
        if (!context.Features.IsOn(Feature.RebootCheck) || RebootCheck.MustWait(host, context))
        {
            return null;
        }

        // Neustart seit dem letzten Stand: sofort vergleichen (Wartezeit ist schon vorbei)
        var newBoot = host.BootedAt is { } booted && RebootCheck.IsNewBoot(booted, RebootCheck.Snapshot(host))
            && !(context.LastRequested(Id, ModuleActions.List) >= booted + RebootCheck.Settle);
        return newBoot || context.Older(Id, ModuleActions.List, RebootCheck.Interval) ? new PlannedTask(ModuleActions.List) : null;
    }

    public override Task<JsonElement?> HandleAsync(ModuleContext context, ManagedHost host, AgentTaskRecord task, TaskResultRequest result, CancellationToken ct)
    {
        if (context.Features.IsOn(Feature.RebootCheck) && PullJsonContext.FromElement(result.Payload, PullJsonContext.Default.RunningServices) is { } report)
        {
            RebootCheck.Apply(host, report, context.Now, context.Notifier);
        }

        return Task.FromResult<JsonElement?>(null);
    }
}
