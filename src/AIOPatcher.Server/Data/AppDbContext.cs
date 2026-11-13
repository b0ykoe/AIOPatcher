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
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

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
            e.HasIndex(x => x.CertificateThumbprint).IsUnique();
            e.HasIndex(x => new { x.SiteId, x.Name });
            e.HasOne(x => x.Site).WithMany(s => s.Hosts).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
            // MaintenanceDays ist ein Flags-Enum: als Zahl speichern, nicht als String.
            e.Property(x => x.MaintenanceDays).HasConversion<int>();
        });

        b.Entity<PendingUpdate>(e =>
        {
            e.HasOne(x => x.Host).WithMany(h => h.PendingUpdates).HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.HostId);
        });

        b.Entity<PatchJob>(e =>
        {
            e.HasOne(x => x.Host).WithMany(h => h.Jobs).HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HostId, x.Status });
            e.HasIndex(x => x.CreatedAt);
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
        });

        b.Entity<EnrollmentToken>(e =>
        {
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<AdminUser>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(64);
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<AuditEntry>(e =>
        {
            e.HasIndex(x => x.Timestamp);
        });
    }
}
