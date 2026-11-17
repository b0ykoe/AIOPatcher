using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<ManagedHost> Hosts => Set<ManagedHost>();
    public DbSet<PendingUpdate> PendingUpdates => Set<PendingUpdate>();
    public DbSet<PatchJob> Jobs => Set<PatchJob>();
    public DbSet<JobLogEntry> JobLogEntries => Set<JobLogEntry>();
    public DbSet<Subnet> Subnets => Set<Subnet>();
    public DbSet<IpAddressEntry> IpAddresses => Set<IpAddressEntry>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Runbook> Runbooks => Set<Runbook>();
    public DbSet<RunbookWave> RunbookWaves => Set<RunbookWave>();
    public DbSet<RunbookWaveHost> RunbookWaveHosts => Set<RunbookWaveHost>();
    public DbSet<RunbookRun> RunbookRuns => Set<RunbookRun>();
    public DbSet<RunbookRunWave> RunbookRunWaves => Set<RunbookRunWave>();
    public DbSet<RunbookRunItem> RunbookRunItems => Set<RunbookRunItem>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<PatchFreeze> PatchFreezes => Set<PatchFreeze>();
    public DbSet<PackageHold> PackageHolds => Set<PackageHold>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<CveInfo> CveInfos => Set<CveInfo>();
    public DbSet<VulnLookup> VulnLookups => Set<VulnLookup>();
    public DbSet<ContainerInfo> Containers => Set<ContainerInfo>();
    public DbSet<ImageCheck> ImageChecks => Set<ImageCheck>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<InstalledApp> InstalledApps => Set<InstalledApp>();
    public DbSet<HostVulnerability> HostVulnerabilities => Set<HostVulnerability>();
    public DbSet<AIOPatcher.Server.Services.FeatureToggle> FeatureToggles => Set<AIOPatcher.Server.Services.FeatureToggle>();
    public DbSet<HostCompliance> HostCompliance => Set<HostCompliance>();
    public DbSet<AgentTaskRecord> AgentTasks => Set<AgentTaskRecord>();
    public DbSet<HostMetricSample> MetricSamples => Set<HostMetricSample>();
    public DbSet<SsoSettingsRecord> SsoSettings => Set<SsoSettingsRecord>();
    public DbSet<FirewallProfile> FirewallProfiles => Set<FirewallProfile>();
    public DbSet<FirewallProfileVersion> FirewallProfileVersions => Set<FirewallProfileVersion>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums lesbar in der Datenbank ablegen.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);

        // SQLite kennt keine Zeitzonen: beim Lesen als UTC markieren, damit die API "...Z" ausliefert.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Site>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<ManagedHost>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Hostname).HasMaxLength(255);
            e.Property(x => x.CertificateThumbprint).HasMaxLength(128);
            e.Property(x => x.PendingCertificateThumbprint).HasMaxLength(128);
            e.HasIndex(x => x.PendingCertificateThumbprint);
            e.Property(x => x.AgentUpdateOutcome).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AgentUpdateFromVersion).HasMaxLength(32);
            e.Property(x => x.AgentUpdateToVersion).HasMaxLength(32);
            e.Property(x => x.AgentUpdateBlockedVersion).HasMaxLength(32);
            e.Property(x => x.AgentUpdateMessage).HasMaxLength(1000);
            e.Property(x => x.ReportedModules).HasMaxLength(4000);
            e.Property(x => x.DisabledModules).HasMaxLength(500);
            e.Property(x => x.Architecture).HasMaxLength(16);
            e.HasIndex(x => x.CertificateThumbprint).IsUnique();
            e.HasIndex(x => new { x.SiteId, x.Name });
            e.HasOne(x => x.Site).WithMany(s => s.Hosts).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
            // MaintenanceDays ist ein Flags-Enum: als Zahl speichern, nicht als String.
            e.Property(x => x.MaintenanceDays).HasConversion<int>();
            e.Property(x => x.Tags).HasMaxLength(500);
            e.Property(x => x.NotifiedKevCves).HasMaxLength(4000);
            e.Property(x => x.ExternalId).HasMaxLength(100);
            e.HasOne(x => x.Integration).WithMany().HasForeignKey(x => x.IntegrationId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<PendingUpdate>(e =>
        {
            e.HasOne(x => x.Host).WithMany(h => h.PendingUpdates).HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.HostId);
            e.Property(x => x.SourcePackage).HasMaxLength(200);
            e.Property(x => x.Cves).HasMaxLength(4000);
            e.HasIndex(x => x.CveCheckedAt);
        });

        b.Entity<PatchJob>(e =>
        {
            e.HasOne(x => x.Host).WithMany(h => h.Jobs).HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HostId, x.Status });
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Packages).HasMaxLength(4000);
            e.Property(x => x.Payload).HasMaxLength(64000);
            e.Property(x => x.ProtectedSecret).HasMaxLength(2000);
        });

        b.Entity<JobLogEntry>(e =>
        {
            e.HasOne(x => x.Job).WithMany(j => j.LogEntries).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.JobId, x.Id });
        });

        b.Entity<Subnet>(e =>
        {
            e.HasOne(x => x.Site).WithMany(s => s.Subnets).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            // Eindeutig pro Site, Überschneidungen zwischen Sites sind erlaubt (Kundennetze).
            e.HasIndex(x => new { x.SiteId, x.Cidr }).IsUnique();
        });

        b.Entity<IpAddressEntry>(e =>
        {
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Subnet).WithMany().HasForeignKey(x => x.SubnetId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            // Bewusst nicht eindeutig: doppelte Adressen innerhalb einer Site werden als Konflikt angezeigt.
            e.HasIndex(x => new { x.SiteId, x.Address });
            e.Property(x => x.MacAddress).HasMaxLength(32);
            e.Property(x => x.Hostname).HasMaxLength(200);
        });

        b.Entity<EnrollmentToken>(e =>
        {
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<UserAccount>(e =>
        {
            e.ToTable("AdminUsers"); // Tabellenname aus v0.1 beibehalten (bestehende Daten)
            e.Property(x => x.Username).HasMaxLength(64);
            e.Property(x => x.SsoSubject).HasMaxLength(600);
            e.HasIndex(x => x.SsoSubject).IsUnique();
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Language).HasMaxLength(8);
            e.Property(x => x.SecurityStamp).HasMaxLength(64);
            e.HasIndex(x => x.InviteTokenHash);
        });

        b.Entity<NotificationChannel>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Events).HasConversion<int>();
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PatchFreeze>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.EndsAt);
        });

        b.Entity<PackageHold>(e =>
        {
            e.Property(x => x.Pattern).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Integration>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.BaseUrl).HasMaxLength(300);
            e.Property(x => x.TlsFingerprintSha256).HasMaxLength(64);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<CveInfo>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.KevName).HasMaxLength(300);
            e.HasIndex(x => x.IsKev);
        });

        b.Entity<VulnLookup>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(600);
        });

        b.Entity<ContainerInfo>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Image).HasMaxLength(300);
            e.Property(x => x.Reference).HasMaxLength(300);
            e.Property(x => x.ImageId).HasMaxLength(100);
            e.Property(x => x.LocalDigest).HasMaxLength(400);
            e.Property(x => x.State).HasMaxLength(32);
            e.HasOne(x => x.Host).WithMany(h => h.Containers).HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.HostId);
        });

        b.Entity<AIOPatcher.Server.Services.FeatureToggle>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.ChangedBy).HasMaxLength(100);
        });

        b.Entity<SsoSettingsRecord>(e =>
        {
            e.ToTable("SsoSettings");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.Authority).HasMaxLength(500);
            e.Property(x => x.ClientId).HasMaxLength(200);
            e.Property(x => x.ProtectedClientSecret).HasMaxLength(2000);
            e.Property(x => x.Scopes).HasMaxLength(500);
            e.Property(x => x.UsernameClaim).HasMaxLength(100);
            e.Property(x => x.GroupsClaim).HasMaxLength(100);
            e.Property(x => x.RoleMappingsJson).HasMaxLength(8000);
            e.Property(x => x.DefaultRole).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CertificateFingerprint).HasMaxLength(100);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
        });

        b.Entity<HostMetricSample>(e =>
        {
            e.ToTable("MetricSamples");
            e.HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HostId, x.At });
            e.HasIndex(x => x.At);
        });

        b.Entity<AgentTaskRecord>(e =>
        {
            e.ToTable("AgentTasks");
            e.HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HostId, x.Status });
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Module).HasMaxLength(32);
            e.Property(x => x.Action).HasMaxLength(32);
            e.Property(x => x.Summary).HasMaxLength(2000);
        });

        b.Entity<HostCompliance>(e =>
        {
            e.HasKey(x => x.HostId);
            e.HasOne(x => x.Host).WithOne().HasForeignKey<HostCompliance>(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.FirewallProduct).HasMaxLength(32);
            e.Property(x => x.AppliedFirewallHash).HasMaxLength(32);
        });

        b.Entity<FirewallProfile>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Description).HasMaxLength(500);
        });

        b.Entity<FirewallProfileVersion>(e =>
        {
            e.HasOne(x => x.Profile).WithMany(p => p.Versions).HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProfileId, x.Number }).IsUnique();
            e.HasIndex(x => x.Hash);
            e.Property(x => x.Hash).HasMaxLength(32);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
        });

        b.Entity<HostVulnerability>(e =>
        {
            e.Property(x => x.CveId).HasMaxLength(32);
            e.Property(x => x.Package).HasMaxLength(200);
            e.Property(x => x.InstalledVersion).HasMaxLength(100);
            e.HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.HostId);
            e.HasIndex(x => x.CveId);
        });

        b.Entity<InstalledApp>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Version).HasMaxLength(100);
            e.Property(x => x.Publisher).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.PackageId).HasMaxLength(200);
            e.Property(x => x.AvailableVersion).HasMaxLength(100);
            e.Property(x => x.Url).HasMaxLength(500);
            e.HasOne(x => x.Host).WithMany(h => h.Apps).HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.HostId);
            e.HasIndex(x => x.PackageId);
        });

        b.Entity<ImageCheck>(e =>
        {
            e.HasKey(x => x.Reference);
            e.Property(x => x.Reference).HasMaxLength(300);
            e.Property(x => x.RemoteDigest).HasMaxLength(100);
            e.Property(x => x.NotifiedDigest).HasMaxLength(100);
            e.Property(x => x.Error).HasMaxLength(300);
        });

        b.Entity<ApiToken>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Prefix).HasMaxLength(16);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<AuditEntry>(e =>
        {
            e.HasIndex(x => x.Timestamp);
        });

        b.Entity<Runbook>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.ScheduleDays).HasConversion<int>();
        });

        b.Entity<RunbookWave>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasOne(x => x.Runbook).WithMany(r => r.Waves).HasForeignKey(x => x.RunbookId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RunbookId, x.Order });
        });

        b.Entity<RunbookWaveHost>(e =>
        {
            e.HasKey(x => new { x.WaveId, x.HostId });
            e.HasOne(x => x.Wave).WithMany(w => w.Hosts).HasForeignKey(x => x.WaveId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Host).WithMany(h => h.RunbookMemberships).HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RunbookRun>(e =>
        {
            e.HasOne(x => x.Runbook).WithMany(r => r.Runs).HasForeignKey(x => x.RunbookId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RunbookId, x.Status });
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<RunbookRunWave>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasOne(x => x.Run).WithMany(r => r.Waves).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RunbookRunItem>(e =>
        {
            e.Property(x => x.HostName).HasMaxLength(200);
            e.Property(x => x.SnapshotName).HasMaxLength(64);
            e.Property(x => x.PendingTask).HasMaxLength(200);
            e.HasOne(x => x.Run).WithMany(r => r.Items).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            // Host gelöscht: Eintrag bleibt für die Historie, Verweis wird leer.
            e.HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.RunId, x.WaveOrder });
            e.HasIndex(x => x.JobId);
        });
    }
}
