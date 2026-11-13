using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AIOPatcher.Server.Data;

/// <summary>Nur für `dotnet ef migrations`, damit dabei nicht der ganze Server (inkl. CA) startet.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
