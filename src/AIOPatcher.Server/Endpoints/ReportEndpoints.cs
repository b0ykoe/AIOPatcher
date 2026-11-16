using System.Globalization;
using System.Text;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>Patch-Bericht pro Site und Zeitraum, z. B. für Kunden oder die eigene Dokumentation.</summary>
public static class ReportEndpoints
{
    /// <summary>Security-Updates gelten als überfällig, wenn sie länger offen sind (Standard 14 Tage).</summary>
    public const int DefaultSlaDays = 14;

    public sealed record ReportRow(
        Guid HostId, string Host, string Site, string Os, string Status, string? Tags,
        int PendingUpdates, int SecurityUpdates, int KevUpdates, int? OldestSecurityDays,
        DateTime? LastPatchedAt, int JobsSucceeded, int JobsFailed, bool AutoPatch, bool RebootRequired, bool Compliant);

    public sealed record ReportSummary(int Hosts, int Compliant, double CompliancePercent, int SecurityUpdates, int KevUpdates, int JobsSucceeded, int JobsFailed);

    public sealed record PatchReport(DateTime GeneratedAt, DateTime From, DateTime To, Guid? SiteId, string? SiteName, int SlaDays, ReportSummary Summary, IReadOnlyList<ReportRow> Hosts);

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").RequireAuthorization();
        group.MapGet("/patch", async (Guid? siteId, int? days, int? slaDays, AppDbContext db, AIOPatcherOptions options, CancellationToken ct) =>
            Results.Ok(await BuildAsync(db, options, siteId, days, slaDays, ct)));
        group.MapGet("/patch.csv", async (Guid? siteId, int? days, int? slaDays, AppDbContext db, AIOPatcherOptions options, CancellationToken ct) =>
        {
            var report = await BuildAsync(db, options, siteId, days, slaDays, ct);
            var name = $"aiopatcher-report-{report.SiteName ?? "all"}-{report.To:yyyy-MM-dd}.csv".Replace(' ', '-');
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ToCsv(report))).ToArray(), "text/csv; charset=utf-8", name);
        });
    }

    public static async Task<PatchReport> BuildAsync(AppDbContext db, AIOPatcherOptions options, Guid? siteId, int? days, int? slaDays, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var period = Math.Clamp(days ?? 30, 1, 366);
        var sla = Math.Clamp(slaDays ?? DefaultSlaDays, 1, 365);
        var from = now.AddDays(-period);
        var onlineSince = now - options.OfflineAfter;

        var hosts = await db.Hosts
            .Where(h => !h.IsRevoked && (siteId == null || h.SiteId == siteId))
            .Select(h => new
            {
                h.Id, h.Name, Site = h.Site!.Name, h.OsName, h.OsFamily, h.OsVersion, h.LastSeenAt, h.Tags, h.AutoPatchEnabled, h.RebootRequired,
                Pending = h.PendingUpdates.Count,
                Security = h.PendingUpdates.Count(u => u.IsSecurity),
                Kev = h.PendingUpdates.Count(u => u.IsKev),
                OldestSecurity = h.PendingUpdates.Where(u => u.IsSecurity).Min(u => (DateTime?)u.FirstSeenAt),
                LastPatched = h.Jobs.Where(j => j.Type == JobType.Patch && j.Status == JobStatus.Succeeded).Max(j => j.FinishedAt),
                Succeeded = h.Jobs.Count(j => j.Type == JobType.Patch && j.Status == JobStatus.Succeeded && j.FinishedAt >= from),
                Failed = h.Jobs.Count(j => j.Type == JobType.Patch && j.Status == JobStatus.Failed && j.FinishedAt >= from),
            })
            .ToListAsync(ct);

        var rows = hosts
            .Select(h =>
            {
                int? oldest = h.OldestSecurity is { } first ? (int)Math.Floor((now - first).TotalDays) : null;
                var compliant = h.Kev == 0 && (oldest is null || oldest < sla);
                var status = h.LastSeenAt is null ? "Pending" : h.LastSeenAt >= onlineSince ? "Online" : "Offline";
                return new ReportRow(h.Id, h.Name, h.Site, h.OsName ?? h.OsFamily.ToString(), status, h.Tags, h.Pending, h.Security, h.Kev, oldest,
                    h.LastPatched, h.Succeeded, h.Failed, h.AutoPatchEnabled, h.RebootRequired, compliant);
            })
            .OrderBy(r => r.Compliant).ThenBy(r => r.Site).ThenBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var compliantCount = rows.Count(r => r.Compliant);
        var summary = new ReportSummary(
            rows.Count,
            compliantCount,
            rows.Count == 0 ? 100 : Math.Round(100.0 * compliantCount / rows.Count, 1),
            rows.Sum(r => r.SecurityUpdates),
            rows.Sum(r => r.KevUpdates),
            rows.Sum(r => r.JobsSucceeded),
            rows.Sum(r => r.JobsFailed));

        var siteName = siteId is null ? null : await db.Sites.Where(s => s.Id == siteId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        return new PatchReport(now, from, now, siteId, siteName, sla, summary, rows);
    }

    public static string ToCsv(PatchReport report)
    {
        // Deutsches Excel erwartet Semikolon, englisches Komma.
        var sep = L.IsGerman ? ";" : ",";
        var sb = new StringBuilder();
        string[] header = L.IsGerman
            ? ["Host", "Site", "System", "Status", "Tags", "Offene Updates", "Security", "KEV", "Älteste Security (Tage)", "Zuletzt gepatcht (UTC)", "Patches ok", "Patches fehlgeschlagen", "Auto-Patch", "Neustart nötig", "Konform"]
            : ["Host", "Site", "System", "Status", "Tags", "Pending updates", "Security", "KEV", "Oldest security (days)", "Last patched (UTC)", "Patches ok", "Patches failed", "Auto-patch", "Reboot required", "Compliant"];
        sb.AppendLine(string.Join(sep, header.Select(Escape)));
        foreach (var r in report.Hosts)
        {
            string[] values =
            [
                r.Host, r.Site, r.Os, r.Status, r.Tags ?? "", Num(r.PendingUpdates), Num(r.SecurityUpdates), Num(r.KevUpdates),
                r.OldestSecurityDays?.ToString(CultureInfo.InvariantCulture) ?? "", r.LastPatchedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
                Num(r.JobsSucceeded), Num(r.JobsFailed), YesNo(r.AutoPatch), YesNo(r.RebootRequired), YesNo(r.Compliant),
            ];
            sb.AppendLine(string.Join(sep, values.Select(Escape)));
        }

        return sb.ToString();

        string Escape(string value) =>
            value.Contains(sep, StringComparison.Ordinal) || value.Contains('"') || value.Contains('\n') || value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@')
                ? "\"" + (value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@') ? "'" : "") + value.Replace("\"", "\"\"") + "\""
                : value;
        static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
        static string YesNo(bool b) => b ? L.T("ja", "yes") : L.T("nein", "no");
    }
}
