using System.Net;

namespace AIOPatcher.Server.Services;

public static class AgentNetworkPolicy
{
    /// <summary>Prüft die optionale IP-Allowlist einer Site. Leere Liste = alles erlaubt.</summary>
    public static bool IsAllowed(string? allowedCidrs, IPAddress? remote)
    {
        if (string.IsNullOrWhiteSpace(allowedCidrs))
        {
            return true;
        }

        if (remote is null)
        {
            return false;
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        foreach (var cidr in allowedCidrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(cidr, out var network) && network.Contains(remote))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Adressen, die im IPAM nichts verloren haben (Loopback, Link-Local, unspezifiziert).</summary>
    public static bool IsReportable(IPAddress address) =>
        !IPAddress.IsLoopback(address)
        && !address.IsIPv6LinkLocal
        && !address.IsIPv6Multicast
        && !address.Equals(IPAddress.Any)
        && !address.Equals(IPAddress.IPv6Any)
        && !(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..]);
}
