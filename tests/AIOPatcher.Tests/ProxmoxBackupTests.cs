using System.Text.Json.Nodes;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;

namespace AIOPatcher.Tests;

/// <summary>Proxmox Backup Server: API-Antworten auswerten, Token-Format, Benachrichtigungen bei neuen Problemen.</summary>
public sealed class ProxmoxBackupTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
    private static long Epoch(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

    public ProxmoxBackupTests() => L.Use("en");

    [Fact]
    public void Token_ColonOrEquals()
    {
        Assert.Equal("root@pam!aiopatcher:abc-123", ProxmoxBackupClient.NormalizeToken(" root@pam!aiopatcher=abc-123 "));
        Assert.Equal("root@pam!aiopatcher:abc=123", ProxmoxBackupClient.NormalizeToken("root@pam!aiopatcher:abc=123"));
        Assert.Null(ProxmoxBackupClient.TokenProblem("root@pam!aiopatcher:1b2c3d4e-1111-2222-3333-444455556666"));
        Assert.Null(ProxmoxBackupClient.TokenProblem("monitor@pbs!dash=secret"));
        Assert.Contains("only the secret", ProxmoxBackupClient.TokenProblem("1b2c3d4e-1111-2222-3333-444455556666"));
        Assert.Contains("Format", ProxmoxBackupClient.TokenProblem("root@pam!aiopatcher"));
    }

    [Fact]
    public void Parse_UsageTasksGroups()
    {
        Assert.Equal("3.2-7", ProxmoxBackupClient.Version(JsonNode.Parse("""{"version":"3.2","release":"7","repoid":"abc"}""")));

        var usage = ProxmoxBackupClient.ParseUsage(JsonNode.Parse("""
            [{"store":"main","total":1000,"used":950,"avail":50,"estimated-full-date":1795000000,"history":[0.5,null]},
             {"store":"usb","error":"datastore is not mounted","mount-status":"notmounted"}]
            """));
        Assert.Equal(2, usage.Count);
        Assert.Equal(95.0, ProxmoxBackupMonitor.UsedPercent(usage[0]));
        Assert.Equal("datastore is not mounted", usage[1].Error);
        var gc = ProxmoxBackupClient.WithGc(usage[0], JsonNode.Parse("""{"store":"main","last-run-state":"ok","last-run-endtime":1795000000,"next-run":1795086400}"""));
        Assert.Equal("ok", gc.GcState);
        Assert.NotNull(gc.GcNextRun);

        var tasks = ProxmoxBackupClient.ParseTasks(JsonNode.Parse($$"""
            [{"upid":"U1","worker_type":"backup","worker_id":"main:vm/100","starttime":{{Epoch(Now.AddHours(-2))}},"endtime":{{Epoch(Now.AddHours(-2))}},"status":"OK","user":"root@pam"},
             {"upid":"U2","worker_type":"verificationjob","worker_id":"main:v-1","starttime":{{Epoch(Now.AddHours(-1))}},"endtime":{{Epoch(Now)}},"status":"verification failed - please check the log","user":"root@pam"},
             {"upid":"U3","worker_type":"garbage_collection","worker_id":"main","starttime":{{Epoch(Now)}},"status":"WARNINGS: 2"},
             {"upid":"U4","worker_type":"backup","worker_id":"main:ct/200","starttime":{{Epoch(Now)}} }]
            """));
        Assert.Equal(4, tasks.Count);
        Assert.Equal(["U2"], tasks.Where(ProxmoxBackupClient.IsFailed).Select(t => t.Upid));
        Assert.Null(tasks[3].Status); // läuft noch

        Assert.Equal(["", "team-a"], ProxmoxBackupClient.ParseNamespaces(JsonNode.Parse("""[{"ns":"team-a"}]""")));
        var groups = ProxmoxBackupClient.ParseGroups("main", "", JsonNode.Parse($$"""
            [{"backup-type":"vm","backup-id":"100","last-backup":{{Epoch(Now.AddHours(-3))}},"backup-count":14,"owner":"root@pam"},
             {"backup-type":"host","backup-id":"nas","last-backup":{{Epoch(Now.AddDays(-5))}},"backup-count":2}]
            """), JsonNode.Parse($$"""
            [{"backup-type":"vm","backup-id":"100","backup-time":{{Epoch(Now.AddDays(-1))}},"verification":{"state":"failed","upid":"x"} },
             {"backup-type":"vm","backup-id":"100","backup-time":{{Epoch(Now.AddHours(-3))}},"verification":{"state":"ok","upid":"y"} },
             {"backup-type":"host","backup-id":"nas","backup-time":{{Epoch(Now.AddDays(-5))}} }]
            """));
        Assert.Equal(2, groups.Count);
        var nas = groups.Single(g => g.Id == "nas");
        var vm = groups.Single(g => g.Id == "100");
        Assert.Equal(("ok", 14), (vm.VerifyState, vm.Count)); // jüngster Snapshot zählt
        Assert.Null(nas.VerifyState);
        Assert.True(ProxmoxBackupMonitor.IsStale(nas, Now));
        Assert.False(ProxmoxBackupMonitor.IsStale(vm, Now));
    }

    [Fact]
    public void ProxmoxVe_BackupJobs_GuestsAndFailedRuns()
    {
        var jobs = ProxmoxBackupJobsMonitor.ParseJobs(JsonNode.Parse("""
            [{"id":"backup-1a2b","schedule":"21:00","storage":"pbs","all":1,"exclude":"105,106","mode":"snapshot","enabled":1,"next-run":1795100000,"comment":"Nightly"},
             {"id":"backup-3c4d","schedule":"sun 01:00","storage":"usb","vmid":"100,101","enabled":0},
             {"id":"backup-old","starttime":"02:30","dow":"mon,wed","storage":"local","pool":"prod"}]
            """));
        Assert.Equal(3, jobs.Count);
        var nightly = jobs.Single(j => j.Id == "backup-1a2b");
        Assert.True(nightly.All && nightly.Enabled);
        Assert.Equal(["105", "106"], nightly.Exclude);
        Assert.NotNull(nightly.NextRun);
        Assert.False(jobs.Single(j => j.Id == "backup-3c4d").Enabled);
        Assert.Equal(["100", "101"], jobs.Single(j => j.Id == "backup-3c4d").VmIds);
        var legacy = jobs.Single(j => j.Id == "backup-old");
        Assert.True(legacy.Enabled); // "enabled" fehlt = an
        Assert.Equal(("mon,wed 02:30", "prod"), (legacy.Schedule, legacy.Pool));

        var guests = ProxmoxBackupJobsMonitor.ParseGuests(JsonNode.Parse("""[{"vmid":120,"name":"test-vm","type":"qemu"},{"vmid":105,"name":"tmp","type":"lxc"}]"""));
        Assert.Equal([105, 120], guests.Select(g => g.VmId));

        var tasks = ProxmoxBackupJobsMonitor.ParseFailedTasks(JsonNode.Parse($$"""
            [{"upid":"A","type":"vzdump","node":"pve1","id":"","starttime":{{Epoch(Now.AddHours(-3))}},"endtime":{{Epoch(Now.AddHours(-2))}},"status":"job errors"},
             {"upid":"B","type":"vzdump","node":"pve1","id":"101","starttime":{{Epoch(Now.AddHours(-1))}},"status":"OK"},
             {"upid":"C","type":"qmstart","node":"pve1","id":"101","starttime":{{Epoch(Now)}},"status":"failed"},
             {"upid":"D","type":"vzdump","node":"pve2","id":"130","starttime":{{Epoch(Now.AddDays(-9))}},"status":"interrupted"}]
            """), Now.AddDays(-7));
        Assert.Equal("A", Assert.Single(tasks).Upid);
        Assert.Null(tasks[0].Guest);

        // Erstes Prüfen: alle Gäste ohne Job einmal zusammen, aber keine alten Fehlläufe
        var first = new PveBackupStatus(jobs, guests, tasks, Now);
        var problems = ProxmoxBackupJobsMonitor.Problems(null, first);
        Assert.Contains("2 guest(s) without a backup job", Assert.Single(problems).Title);

        // Danach nur Neues
        var next = first with { NotBackedUp = [.. guests, new PveGuest(140, "new-vm", "qemu")], FailedTasks = [new PveBackupTask("E", "pve1", "140", Now, Now, "storage full"), .. tasks] };
        problems = ProxmoxBackupJobsMonitor.Problems(first, next);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Title.Contains("1 guest(s)") && p.Message.Contains("140 new-vm"));
        Assert.Contains(problems, p => p.Title.Contains("1 backup run(s) failed") && p.Message.Contains("pve1 140: storage full"));

        // Schalter aus: nichts auszuwerten
        Assert.Empty(ProxmoxBackupJobsMonitor.Problems(next, next with { NotBackedUp = null, FailedTasks = null }));
    }

    private static PbsStatus Status(DateTime at, double usedPercent, IReadOnlyList<PbsTask> failed, params PbsGroup[] groups) =>
        new("3.2-7", [new PbsDatastore("main", 1000, (long)(usedPercent * 10), 0, null, null, null, null, null)], failed, 10, 0, groups, at);

    [Fact]
    public void Problems_OnlyNewOnes()
    {
        var old = new PbsTask("OLD", "backup", "main:vm/100", Now.AddDays(-2), Now.AddDays(-2), "connection error", null);
        var fresh = new PbsTask("NEW", "prunejob", "main", Now.AddMinutes(-5), Now, "prune failed", null);
        var stale = new PbsGroup("main", "", "host", "nas", Now.AddHours(-49), 3, null, null, null);
        var staleBefore = stale with { LastBackup = Now.AddHours(-49) };

        // Erster Abruf: alte Fehler nicht melden, nur volle Datastores
        Assert.Empty(ProxmoxBackupMonitor.Problems(null, Status(Now, 50, [old], stale)));
        Assert.Single(ProxmoxBackupMonitor.Problems(null, Status(Now, 95, [old])));

        var previous = Status(Now.AddMinutes(-15), 85, [old], stale with { LastBackup = Now.AddHours(-47) });
        var problems = ProxmoxBackupMonitor.Problems(previous, Status(Now, 91, [fresh, old], staleBefore));
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Title.Contains("1 task(s) failed") && p.Message.Contains("prunejob main: prune failed"));
        Assert.Contains(problems, p => p.Title.Contains("older than 48 hours") && p.Message.Contains("host/nas"));
        Assert.Contains(problems, p => p.Title.Contains("almost full"));

        // Danach bleibt alles gleich: nichts erneut melden
        Assert.Empty(ProxmoxBackupMonitor.Problems(Status(Now, 91, [fresh, old], staleBefore), Status(Now.AddMinutes(15), 92, [fresh, old], staleBefore)));
    }
}
