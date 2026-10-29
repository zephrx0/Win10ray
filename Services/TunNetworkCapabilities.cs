using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace XrayMetro.Services;

public static class TunNetworkCapabilities
{
    public static bool RequiresIpv4Only()
    {
        try
        {
            bool ipv4 = false, ipv6 = false;
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.Name.StartsWith("Win10ray", StringComparison.OrdinalIgnoreCase)) continue;
                IPInterfaceProperties properties = adapter.GetIPProperties();
                bool Gateway(AddressFamily family) => properties.GatewayAddresses.Any(item => item.Address.AddressFamily == family && !item.Address.Equals(IPAddress.Any) && !item.Address.Equals(IPAddress.IPv6Any));
                ipv4 |= Gateway(AddressFamily.InterNetwork) && properties.UnicastAddresses.Any(item => item.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address) && !item.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
                ipv6 |= Gateway(AddressFamily.InterNetworkV6) && properties.UnicastAddresses.Any(item => IsUsableIpv6(item.Address));
            }
            // Do not force IPv4 on IPv6-only networks or when adapter information is unavailable.
            return ipv4 && !ipv6;
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException) { return false; }
    }

    public static bool IsUsableIpv6(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 &&
        !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast && !address.IsIPv4MappedToIPv6 &&
        !address.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(address);
}
