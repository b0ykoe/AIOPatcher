using AIOPatcher.Server.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Data;

public static class DatabaseInitializer
{
    /// <summary>
    /// Aktiviert WAL, sichert die Datenbank vor ausstehenden Migrationen (VACUUM INTO) und spielt Migrationen ein.
    /// </summary>
    public static async Task InitializeAsync(AppDbContext db, AIOPatcherOptions options, ILogger logger, CancellationToken ct)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();

        if (pending.Count > 0 && applied.Count > 0 && File.Exists(options.DatabasePath))
        {
            Directory.CreateDirectory(options.BackupDirectory);
            var backupPath = Path.Combine(options.BackupDirectory, $"aiopatcher-{DateTime.UtcNow:yyyyMMdd-HHmmss}-pre-{pending[0]}.db");
            logger.LogInformation("Backing up database before {Count} migration(s) to {BackupPath}", pending.Count, backupPath);

            await using var connection = new SqliteConnection($"Data Source={options.DatabasePath}");
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", backupPath);
            await command.ExecuteNonQueryAsync(ct);
        }

        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

        // Früheres Auto-Patch pro Host wird einmalig zu Patch-Plänen
        if (await AIOPatcher.Server.Services.PatchPlans.MigrateHostAutoPatchAsync(db, ct) is > 0 and var plans)
        {
            logger.LogInformation("Converted per-host auto-patch into {Count} patch plan(s)", plans);
        }

        if (pending.Count > 0)
        {
            logger.LogInformation("Migrations applied: {Migrations}", string.Join(", ", pending));
        }
    }
}
