using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Emerald.Core;

/// <summary>
/// The IPv4 address this computer is reached on, for defaults that point at a service on the
/// machine Emerald is installed on - so nothing site-specific is baked into the build.
/// </summary>
public static class LocalAddress
{
    private static readonly Lazy<string> Cached = new(Find);

    /// <summary>
    /// This machine's LAN address: the first working network adapter with an IPv4 address,
    /// preferring one with a default gateway (the real network rather than a virtual switch or
    /// a direct cable). "localhost" when there is none.
    /// </summary>
    public static string IPv4 => Cached.Value;

    private static string Find()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => (Props: n.GetIPProperties(), Nic: n))
                .Select(x => (
                    Address: x.Props.UnicastAddresses
                        .Select(u => u.Address)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                             !IPAddress.IsLoopback(a) &&
                                             !a.ToString().StartsWith("169.254.", StringComparison.Ordinal)),
                    HasGateway: x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                                  !g.Address.Equals(IPAddress.Any)),
                    Virtual: x.Nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                             x.Nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)))
                .Where(x => x.Address is not null)
                .ToList();

            var best = candidates
                .OrderByDescending(x => x.HasGateway)
                .ThenBy(x => x.Virtual)
                .FirstOrDefault();

            return best.Address?.ToString() ?? "localhost";
        }
        catch
        {
            // Never worth failing startup over: the operator can type the address.
            return "localhost";
        }
    }
}
