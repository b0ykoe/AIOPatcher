using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Inventory;

public static class PlatformInfo
{
    public static OsFamily OsFamily => OperatingSystem.IsWindows() ? OsFamily.Windows : OsFamily.Linux;
}

/// <summary>Systemdaten ohne Update-Liste (die kommt aus dem Paketmanager-Scan).</summary>
public sealed record SystemInfo(
    string OsName,
    string OsVersion,
    string? KernelVersion,
    bool RebootRequired,
    bool NativeAutoUpdatesEnabled);

public interface IPlatformInventory
{
    Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct);

    /// <summary>null = Plattform kann (noch) nicht scannen.</summary>
    Task<IReadOnlyList<AvailableUpdate>?> ScanUpdatesAsync(CancellationToken ct);
}
