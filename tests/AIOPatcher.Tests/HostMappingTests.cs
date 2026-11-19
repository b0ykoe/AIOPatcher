using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Tests;

/// <summary>Zuordnung Host ↔ VM: Auto-Map respektiert manuelle Zuordnungen, eine VM gehört höchstens einem Host.</summary>
public sealed class HostMappingTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Integration _pve;
    private readonly Site _site = new() { Name = "Homelab" };

    private static readonly MachineInfo[] Machines =
    [
        new("pve1/qemu/101", "web01", "running", []),
        new("pve1/qemu/102", "db01", "running", []),
        new("pve1/lxc/103", "dns", "stopped", []),
    ];

    public HostMappingTests()
    {
        L.Use("en");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _pve = new Integration { Name = "pve", Type = IntegrationType.Proxmox, ProtectedSecret = "x", BaseUrl = "https://pve:8006" };
        _db.Integrations.Add(_pve);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private ManagedHost Host(string name, string? hostname = null)
    {
        var host = new ManagedHost { Site = _site, Name = name, Hostname = hostname ?? name, CertificateThumbprint = Guid.NewGuid().ToString("N") };
        _db.Hosts.Add(host);
        _db.SaveChanges();
        return host;
    }

    [Fact]
    public void AutoMap_matches_by_name_and_short_hostname()
    {
        var web = Host("Web01");
        var db = Host("database", "db01.example.org");
        var other = Host("mail");

        var mapped = HostMapping.AutoMap(_pve.Id, [web, db, other], Machines, []);

        Assert.Equal([web, db], mapped);
        Assert.Equal("pve1/qemu/101", web.ExternalId);
        Assert.Equal("pve1/qemu/102", db.ExternalId);
        Assert.False(web.MappingManual);
        Assert.Null(other.IntegrationId);
    }

    [Fact]
    public void AutoMap_leaves_manual_and_manually_unlinked_hosts_alone()
    {
        var unlinked = Host("web01");
        HostMapping.Unassign(unlinked);
        var manual = Host("db01");
        manual.IntegrationId = _pve.Id;
        manual.ExternalId = "pve1/lxc/103";
        manual.MappingManual = true;

        var mapped = HostMapping.AutoMap(_pve.Id, [unlinked, manual], Machines, ["pve1/lxc/103"]);

        Assert.Empty(mapped);
        Assert.Null(unlinked.IntegrationId);
        Assert.Equal("pve1/lxc/103", manual.ExternalId);
    }

    [Fact]
    public void AutoMap_does_not_hand_out_a_machine_twice()
    {
        var first = Host("web01");
        var second = Host("web01.example.org", "web01.example.org");

        var mapped = HostMapping.AutoMap(_pve.Id, [first, second], Machines, ["pve1/qemu/102"]);

        Assert.Equal([first], mapped);
        Assert.Null(second.IntegrationId);

        var dbHost = Host("db01");
        Assert.Empty(HostMapping.AutoMap(_pve.Id, [dbHost], Machines, ["pve1/qemu/102"]));
    }

    [Fact]
    public void AutoMap_skips_ambiguous_names()
    {
        var host = Host("web01");
        MachineInfo[] machines = [new("pve1/qemu/101", "web01", null, []), new("pve2/qemu/201", "WEB01", null, [])];

        Assert.Empty(HostMapping.AutoMap(_pve.Id, [host], machines, []));
    }

    [Fact]
    public async Task Assign_marks_manual_and_unlinks_the_previous_host()
    {
        var old = Host("web01");
        HostMapping.AutoMap(_pve.Id, [old], Machines, []);
        _db.SaveChanges();
        var fresh = Host("web01-new");

        var previous = await HostMapping.AssignAsync(_db, fresh, _pve.Id, " pve1/qemu/101 ", CancellationToken.None);
        _db.SaveChanges();

        Assert.Same(old, previous);
        Assert.Equal("pve1/qemu/101", fresh.ExternalId);
        Assert.True(fresh.MappingManual);
        Assert.Null(old.IntegrationId);
        Assert.Null(old.ExternalId);
        Assert.False(old.MappingManual);
        Assert.Equal(1, await _db.Hosts.CountAsync(h => h.ExternalId == "pve1/qemu/101"));
    }

    [Fact]
    public async Task Different_names_need_the_manual_mapping_and_auto_map_keeps_it()
    {
        // Echter Fall: VM "win2025-warz" (VMID 131), der Windows-Agent heißt "WARZ-APP".
        var warz = Host("WARZ-APP", "WARZ-APP");
        MachineInfo[] machines = [new("pve/qemu/131", "win2025-warz", "running", [])];
        Assert.Empty(HostMapping.AutoMap(_pve.Id, [warz], machines, []));

        await HostMapping.AssignAsync(_db, warz, _pve.Id, "pve/qemu/131", CancellationToken.None);
        _db.SaveChanges();
        var other = Host("win2025-warz");

        Assert.Empty(HostMapping.AutoMap(_pve.Id, [warz, other], machines, ["pve/qemu/131"]));
        Assert.Equal("pve/qemu/131", warz.ExternalId);
        Assert.Null(other.IntegrationId);
    }

    [Fact]
    public async Task Host_list_shows_which_integration_the_mapping_points_to()
    {
        var warz = Host("WARZ-APP");
        await HostMapping.AssignAsync(_db, warz, _pve.Id, "pve/qemu/131", CancellationToken.None);
        var free = Host("mail");
        _db.SaveChanges();

        static HostEndpoints.HostSummaryDto Summary(ManagedHost h) => new(h.Id, h.SiteId, "Homelab", h.Name, h.Hostname, OsFamily.Windows, null, null, null,
            null, null, HostEndpoints.HostStatus.Online, false, false, 0, 0,
            new HostEndpoints.AutoPatchDto(false, PatchScope.Security, false, 0, new TimeOnly(3, 0), 0), null, null, 0, 0, h.IntegrationId != null);

        var list = await HostEndpoints.EnrichAsync(_db, [Summary(warz), Summary(free)], new ServerClock(new AIOPatcher.Server.Options.AIOPatcherOptions()), CancellationToken.None);

        var mapping = list.Single(s => s.Id == warz.Id).Mapping;
        Assert.NotNull(mapping);
        Assert.Equal(_pve.Id, mapping.IntegrationId);
        Assert.Equal("pve", mapping.IntegrationName);
        Assert.Equal(IntegrationType.Proxmox, mapping.Type);
        Assert.Equal("pve/qemu/131", mapping.ExternalId);
        Assert.True(mapping.Manual);
        Assert.Null(list.Single(s => s.Id == free.Id).Mapping);
    }

    [Fact]
    public void Unassign_is_remembered_as_manual()
    {
        var host = Host("web01");
        HostMapping.AutoMap(_pve.Id, [host], Machines, []);

        HostMapping.Unassign(host);
        _db.SaveChanges();

        var stored = _db.Hosts.AsNoTracking().Single(h => h.Id == host.Id);
        Assert.Null(stored.IntegrationId);
        Assert.True(stored.MappingManual);
    }

    [Fact]
    public void Overview_lists_every_machine_and_missing_mappings()
    {
        var web = Host("web01");
        HostMapping.AutoMap(_pve.Id, [web], Machines, []);
        var gone = Host("old");
        gone.IntegrationId = _pve.Id;
        gone.ExternalId = "pve1/qemu/999";
        gone.MappingManual = true;
        var free = Host("mail");

        var overview = IntegrationEndpoints.BuildOverview(_pve.Id, Machines, [web, gone, free]);

        Assert.Equal(["db01", "dns", "pve1/qemu/999", "web01"], overview.Machines.Select(m => m.Name));
        var webRow = overview.Machines.Single(m => m.ExternalId == "pve1/qemu/101");
        Assert.Equal(web.Id, webRow.HostId);
        Assert.False(webRow.Manual);
        var missing = overview.Machines.Single(m => m.Missing);
        Assert.Equal(gone.Id, missing.HostId);
        Assert.True(missing.Manual);
        Assert.Equal("web01", overview.Hosts.Single(h => h.Id == web.Id).MappedTo);
        Assert.Null(overview.Hosts.Single(h => h.Id == free.Id).MappedTo);
    }
}
