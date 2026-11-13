using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Inventory;

public static class NetworkInventory
{
    // Virtuelle Interfaces von Containern/Firewall-Bridges, die nur Rauschen im IPAM erzeugen.
    private static readonly string[] IgnoredPrefixes = ["lo", "veth", "docker", "br-", "fwbr", "fwpr", "fwln", "tap", "cni", "flannel", "isatap", "teredo"];

    public static IReadOnlyList<NetworkAddress> Collect()
    {
        var result = new List<NetworkAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || IgnoredPrefixes.Any(p => nic.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = unicast.Address;
                if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast
                    || (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes() is [169, 254, ..]))
                {
                    continue;
                }

                int prefix;
                try
                {
                    prefix = unicast.PrefixLength;
                }
                catch (PlatformNotSupportedException)
                {
                    prefix = -1;
                }

                result.Add(new NetworkAddress(nic.Name, ip.ToString(), prefix));
            }
        }

        return result;
    }
}
