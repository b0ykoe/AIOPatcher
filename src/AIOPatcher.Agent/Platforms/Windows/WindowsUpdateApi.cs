using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Windows;

/// <summary>Ein von Windows Update gemeldetes, noch nicht installiertes Update.</summary>
public sealed record WindowsUpdateInfo(
    string UpdateId,
    string Title,
    string? KbArticle,
    bool IsSecurity,
    string Classification,
    string? Severity);

public sealed record WindowsInstallResult(bool Success, bool RebootRequired, string Summary);

/// <summary>
/// Windows-Update-Agent-API (wuapi, COM "Microsoft.Update.Session"). Funktioniert mit Windows Update direkt
/// und mit WSUS, je nachdem, was auf dem Host konfiguriert ist.
///
/// Zugriff über IDispatch (<c>dynamic</c>): Der Windows-Agent wird weder getrimmt noch AOT-kompiliert,
/// dafür sparen wir uns Hunderte Zeilen COM-Interface-Definitionen.
/// Alle Aufrufe laufen auf einem eigenen STA-Thread, wie es die API erwartet.
/// </summary>
[SupportedOSPlatform("windows")]
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Windows-Agent wird nicht getrimmt (siehe csproj).")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Windows-Agent wird nicht AOT-kompiliert (siehe csproj).")]
[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "COM-Typ über ProgID, Windows-Agent wird nicht getrimmt.")]
public static class WindowsUpdateApi
{
    // Klassifizierungen über feste IDs, die Namen sind je nach Systemsprache übersetzt.
    private const string SecurityUpdates = "0fa1201d-4330-4fa8-8ae9-b877473b6441";
    private const string DefinitionUpdates = "e0789628-ce08-4437-be74-2495b842f43b"; // Defender-Signaturen: macht Windows selbst
    private const string Upgrades = "3689bdc8-b205-4af4-8d4a-a63924c5e9d5";          // Feature-/OS-Upgrades: nie automatisch
    private const string Drivers = "ebfc1fc5-71a4-4f7b-9aca-3b9a503104a0";

    private const string SearchCriteria = "IsInstalled=0 and IsHidden=0 and Type='Software'";
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromHours(3);

    /// <param name="criteria">Nur für Diagnose überschreibbar (z. B. installierte Updates auflisten).</param>
    public static Task<IReadOnlyList<WindowsUpdateInfo>> SearchAsync(string criteria = SearchCriteria) =>
        RunOnStaThread(() =>
        {
            var session = CreateComObject("Microsoft.Update.Session");
            var result = Search(session, criteria);
            return Describe((object)result.Updates);
        }).WaitAsync(SearchTimeout);

    /// <summary>Gefilterte Beschreibung einer IUpdateCollection (öffentlich für Tests mit nachgebauten Objekten).</summary>
    public static IReadOnlyList<WindowsUpdateInfo> Describe(object updateCollection) =>
        Relevant(updateCollection).Select(x => x.Info).ToList();

    public static Task<WindowsInstallResult> InstallAsync(PatchScope scope, IReadOnlyList<string> holds, Action<string> log) =>
        RunOnStaThread(() => Install(scope, holds, log)).WaitAsync(InstallTimeout);

    private static WindowsInstallResult Install(PatchScope scope, IReadOnlyList<string> holds, Action<string> log)
    {
        var session = CreateComObject("Microsoft.Update.Session");
        session.ClientApplicationID = "AIOPatcher";

        log("[agent] Searching for updates via Windows Update / WSUS ...");
        var searchResult = Search(session);
        var candidates = Relevant((object)searchResult.Updates)
            .Where(x => scope == PatchScope.All || x.Info.IsSecurity)
            .ToList();
        var selected = new List<(dynamic Update, WindowsUpdateInfo Info)>();
        foreach (var candidate in candidates)
        {
            if (HoldPatterns.IsHeld(holds, candidate.Info.Title, candidate.Info.KbArticle))
            {
                log($"[agent] Held back: {candidate.Info.Title}");
            }
            else
            {
                selected.Add(candidate);
            }
        }

        if (selected.Count == 0)
        {
            var none = scope == PatchScope.Security ? "No pending security updates." : "No pending updates.";
            log($"[agent] {none}");
            return new WindowsInstallResult(true, false, none);
        }

        var collection = CreateComObject("Microsoft.Update.UpdateColl");
        foreach (var (update, info) in selected)
        {
            log($"[agent] Selected: {info.Title}{(info.IsSecurity ? " [Security]" : "")}");
            if (!(bool)update.EulaAccepted)
            {
                update.AcceptEula();
            }

            collection.Add(update);
        }

        log($"[agent] Downloading {selected.Count} update(s) ...");
        var downloader = session.CreateUpdateDownloader();
        downloader.Updates = collection;
        var downloadResult = downloader.Download();
        log($"[agent] Download: {ResultText((int)downloadResult.ResultCode)}");

        var toInstall = CreateComObject("Microsoft.Update.UpdateColl");
        for (var i = 0; i < (int)collection.Count; i++)
        {
            var update = collection[i];
            if ((bool)update.IsDownloaded)
            {
                toInstall.Add(update);
            }
            else
            {
                log($"[agent] Not downloaded, skipping: {update.Title}");
            }
        }

        if ((int)toInstall.Count == 0)
        {
            return new WindowsInstallResult(false, false, "No update could be downloaded.");
        }

        log($"[agent] Installing {(int)toInstall.Count} update(s) ...");
        var installer = session.CreateUpdateInstaller();
        installer.AllowSourcePrompts = false;
        installer.Updates = toInstall;
        var installResult = installer.Install();

        int succeeded = 0, failed = 0;
        for (var i = 0; i < (int)toInstall.Count; i++)
        {
            var code = (int)installResult.GetUpdateResult(i).ResultCode;
            var hresult = (int)installResult.GetUpdateResult(i).HResult;
            var title = (string)toInstall[i].Title;
            if (code is 2 or 3)
            {
                succeeded++;
                log($"[agent] OK: {title}");
            }
            else
            {
                failed++;
                log($"[agent] ERROR ({ResultText(code)}, 0x{hresult:X8}): {title}");
            }
        }

        var reboot = (bool)installResult.RebootRequired;
        var summary = $"{succeeded} of {(int)toInstall.Count} update(s) installed" + (failed > 0 ? $", {failed} failed" : "");
        log($"[agent] {summary}. Reboot required: {(reboot ? "yes" : "no")}.");
        return new WindowsInstallResult(failed == 0, reboot, summary);
    }

    private static dynamic Search(dynamic session, string criteria = SearchCriteria)
    {
        var searcher = session.CreateUpdateSearcher();
        searcher.Online = true;
        return searcher.Search(criteria);
    }

    /// <summary>Filtert Updates, die AIOPatcher verwaltet: keine Defender-Signaturen, keine OS-Upgrades, keine Treiber, keine optionalen Vorschauen.</summary>
    private static IEnumerable<(dynamic Update, WindowsUpdateInfo Info)> Relevant(object updateCollection)
    {
        dynamic updates = updateCollection; // statisch typisierter Parameter, damit LINQ auf dem Ergebnis funktioniert
        var count = (int)updates.Count;
        for (var i = 0; i < count; i++)
        {
            var update = updates[i];
            if ((bool)update.BrowseOnly)
            {
                continue; // optional (z. B. Vorschau-Updates)
            }

            var categoryIds = new List<string>();
            var categoryNames = new List<string>();
            var categories = update.Categories;
            for (var c = 0; c < (int)categories.Count; c++)
            {
                categoryIds.Add(((string)categories[c].CategoryID).ToLowerInvariant());
                categoryNames.Add((string)categories[c].Name);
            }

            if (categoryIds.Contains(DefinitionUpdates) || categoryIds.Contains(Upgrades) || categoryIds.Contains(Drivers))
            {
                continue;
            }

            var severity = update.MsrcSeverity as string;
            var kbs = update.KBArticleIDs;
            var kb = (int)kbs.Count > 0 ? "KB" + (string)kbs[0] : null;

            yield return (update, new WindowsUpdateInfo(
                UpdateId: (string)update.Identity.UpdateID,
                Title: (string)update.Title,
                KbArticle: kb,
                IsSecurity: categoryIds.Contains(SecurityUpdates) || !string.IsNullOrEmpty(severity),
                Classification: string.Join(", ", categoryNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(3)),
                Severity: string.IsNullOrEmpty(severity) ? null : severity));
        }
    }

    private static string ResultText(int code) => code switch
    {
        0 => "not started",
        1 => "in progress",
        2 => "succeeded",
        3 => "succeeded with errors",
        4 => "failed",
        5 => "aborted",
        _ => $"Code {code}",
    };

    private static dynamic CreateComObject(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (COMException ex)
            {
                tcs.SetException(new InvalidOperationException($"Windows Update reports 0x{ex.HResult:X8}: {Describe(ex.HResult)}", ex));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "WindowsUpdate",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Die häufigsten Fehlercodes in Klartext.</summary>
    public static string Describe(int hresult) => unchecked((uint)hresult) switch
    {
        0x8024402C or 0x8024401C or 0x80244022 => "Update server unreachable (check proxy/firewall/WSUS)",
        0x80240438 => "No connection to the update service",
        0x8024A000 or 0x80070422 => "Windows Update service is disabled or not running",
        0x80240022 => "All updates failed",
        0x80070005 => "Access denied (agent must run as SYSTEM/Administrator)",
        0x8024001E => "Operation aborted (service is shutting down)",
        0x80240016 => "Another installation is in progress",
        _ => "unknown error",
    };
}
