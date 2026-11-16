using System.Security.Cryptography;
using System.Text;
using AIOPatcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Security;

/// <summary>
/// Solange kein Admin eingerichtet ist, erzeugt der Server bei jedem Start einen Setup-Token und schreibt ihn ins Log.
/// Nur wer Zugriff auf das Server-Log hat, kann die Ersteinrichtung durchführen.
/// </summary>
public sealed class SetupTokenService
{
    private readonly Lock _lock = new();
    private string? _token;

    public bool IsSetupRequired { get; private set; }

    public async Task InitializeAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var complete = await db.Users.AnyAsync(u => u.IsSetupComplete, ct);
        lock (_lock)
        {
            IsSetupRequired = !complete;
            if (!IsSetupRequired)
            {
                _token = null;
                return;
            }

            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        }

        logger.LogWarning("==============================================================");
        logger.LogWarning("Initial setup required. Setup token: {SetupToken}", _token);
        logger.LogWarning("==============================================================");
    }

    public bool Validate(string? candidate)
    {
        lock (_lock)
        {
            if (!IsSetupRequired || _token is null || string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            var normalized = candidate.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(normalized), Encoding.ASCII.GetBytes(_token));
        }
    }

    public void MarkCompleted()
    {
        lock (_lock)
        {
            IsSetupRequired = false;
            _token = null;
        }
    }
}
