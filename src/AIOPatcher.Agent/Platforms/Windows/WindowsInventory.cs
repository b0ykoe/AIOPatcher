using System.Runtime.Versioning;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Contracts;
using Microsoft.Win32;

namespace AIOPatcher.Agent.Platforms.Windows;

/// <summary>Windows Server 2019/2022/2025 (und Windows 10/11). Updates über die Windows-Update-API.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInventory : IPlatformInventory
{
    public Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct)
    {
        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = cv?.GetValue("ProductName") as string ?? "Windows";
        var display = cv?.GetValue("DisplayVersion") as string;
        var build = cv?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
        var ubr = cv?.GetValue("UBR") is int u ? $".{u}" : "";
        var version = display is null ? $"Build {build}{ubr}" : $"{display} (Build {build}{ubr})";

        // Bekannter Registry-Fehler: Windows 11 trägt "Windows 10 ..." als ProductName (Server sind nicht betroffen).
        if (product.StartsWith("Windows 10", StringComparison.Ordinal) && int.TryParse(build, out var buildNumber) && buildNumber >= 22000)
        {
            product = "Windows 11" + product["Windows 10".Length..];
        }

        return Task.FromResult(new SystemInfo(product, version, KernelVersion: null, IsRebootPending(), AreNativeAutoUpdatesEnabled()));
    }

    public async Task<IReadOnlyList<AvailableUpdate>?> ScanUpdatesAsync(CancellationToken ct)
    {
        var updates = await WindowsUpdateApi.SearchAsync().WaitAsync(ct);
        return updates
            .Select(u => new AvailableUpdate(
                Name: u.Title,
                CurrentVersion: null,
                AvailableVersion: u.KbArticle,
                IsSecurity: u.IsSecurity,
                Classification: u.Severity is null ? u.Classification : $"{u.Classification} ({u.Severity})"))
            .ToList();
    }

    private static bool IsRebootPending()
    {
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
            || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
        {
            return true;
        }

        using var sessionManager = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        return sessionManager?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
    }

    /// <summary>
    /// true, wenn Windows Updates selbst installieren könnte. Nur explizit per Richtlinie abgeschaltet
    /// (NoAutoUpdate=1) oder auf "nur benachrichtigen/herunterladen" gesetzt gilt als aus.
    /// </summary>
    private static bool AreNativeAutoUpdatesEnabled()
    {
        using var au = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU");
        if (au is null)
        {
            return true; // keine Richtlinie: Windows-Standardverhalten
        }

        if (au.GetValue("NoAutoUpdate") is int noAuto && noAuto == 1)
        {
            return false;
        }

        // 2 = nur benachrichtigen, 3 = herunterladen + benachrichtigen, 7 = herunterladen, Installation manuell
        return au.GetValue("AUOptions") is not (2 or 3 or 7);
    }

    private static bool KeyExists(string path)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key is not null;
    }
}
