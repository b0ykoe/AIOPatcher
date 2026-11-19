using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services.Federation;

/// <summary>Instanz-ID und -Name (gecacht). Wird beim Start angelegt, der Name ist in den Einstellungen änderbar.</summary>
public sealed class ServerIdentity(AIOPatcherOptions options)
{
    private volatile ServerIdentityRecord _current = new() { Name = options.PublicHostname };

    public Guid InstanceId => _current.InstanceId;
    public string Name => _current.Name;

    public async Task LoadAsync(AppDbContext db, CancellationToken ct)
    {
        var record = await db.ServerIdentity.SingleOrDefaultAsync(r => r.Id == 1, ct);
        if (record is null)
        {
            record = new ServerIdentityRecord { Name = DefaultName(options.PublicHostname) };
            db.ServerIdentity.Add(record);
            await db.SaveChangesAsync(ct);
        }

        _current = record;
    }

    public void Use(ServerIdentityRecord record) => _current = record;

    public static string DefaultName(string hostname) => FederationProtocol.CleanName(hostname, 100);

    public static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || name.Any(char.IsControl)
            ? L.T("Name muss 1 bis 100 Zeichen lang sein.", "Name must be 1 to 100 characters long.")
            : null;
}
