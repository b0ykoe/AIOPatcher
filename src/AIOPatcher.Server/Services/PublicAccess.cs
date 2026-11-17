using System.Collections.Concurrent;
using AIOPatcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

/// <summary>Was zuletzt über den Proxy-Port ankam (für den Wizard: "funktioniert es von außen?").</summary>
/// <param name="ClientIp">Laut Proxy (CF-Connecting-IP bzw. X-Forwarded-For), sonst die TCP-Gegenstelle.</param>
/// <param name="Cloudflare">Anfrage kam über Cloudflare (CF-Ray).</param>
public sealed record ProxyContact(DateTime At, string? ClientIp, string? ProxyIp, bool Cloudflare, string? ForwardedHost, string? Path);

/// <summary>
/// Einstellungen für den öffentlichen Zugang (gecacht, gelten ohne Neustart) und der zuletzt gesehene Verkehr über den
/// Proxy-Port. Der Proxy-Port selbst ist immer offen (siehe AgentProxyPort), beantwortet aber nur etwas, wenn eine
/// Agent-Adresse eingerichtet ist (außer der Prüfadresse).
/// </summary>
public sealed class PublicAccess
{
    private volatile PublicAccessSettingsRecord _current = new();
    private readonly ConcurrentDictionary<string, DateTime> _nonces = new();
    private DateTime _noncesPrunedAt = DateTime.UtcNow;

    public PublicAccessSettingsRecord Current => _current;

    /// <summary>Öffentliche Agent-Adresse, wenn eingerichtet.</summary>
    public string? AgentUrl => string.IsNullOrWhiteSpace(_current.AgentUrl) ? null : _current.AgentUrl;

    public ProxyContact? LastAgentContact { get; private set; }
    public ProxyContact? LastCheck { get; private set; }

    public async Task LoadAsync(AppDbContext db, CancellationToken ct) =>
        _current = await db.PublicAccessSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct) ?? new PublicAccessSettingsRecord();

    public void Use(PublicAccessSettingsRecord settings) => _current = settings;

    public void NoteAgentContact(ProxyContact contact) => LastAgentContact = contact;

    public void NoteCheck(ProxyContact contact) => LastCheck = contact;

    /// <summary>
    /// Jede Nonce nur einmal innerhalb der erlaubten Uhrenabweichung (danach lehnt schon der Zeitstempel ab).
    /// false = schon gesehen (wiederholte Anfrage).
    /// </summary>
    public bool TryUseNonce(string key, DateTime now)
    {
        if (now - _noncesPrunedAt > TimeSpan.FromMinutes(1))
        {
            _noncesPrunedAt = now;
            var limit = now - 2 * AIOPatcher.Contracts.PublicChannel.MaxClockSkew;
            foreach (var (k, at) in _nonces)
            {
                if (at < limit)
                {
                    _nonces.TryRemove(k, out _);
                }
            }
        }

        return _nonces.TryAdd(key, now);
    }

    /// <summary>Adresse prüfen und vereinheitlichen: https, Host, optional Port, ohne Pfad. null = ungültig.</summary>
    public static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0
               && uri.AbsolutePath is "/" or "" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }
}
