using System;
using System.Net;

namespace Y51_SE2Online.Core;

public static class ServerEndpoint
{
    public static IPEndPoint Parse(string text, bool listen = false)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Enter an IP address and port, such as 192.168.1.10:7742.");
        text = text.Trim();
        if (text.Length > 64 || text.Contains('/') || text.Contains('?') || text.Contains('@')) throw new FormatException("Enter an IP address, not a web URL.");
        IPEndPoint result;
        if (IPAddress.TryParse(text, out var address)) result = new IPEndPoint(address, MpOptions.DefaultIpPort);
        else if (!IPEndPoint.TryParse(text, out result)) throw new FormatException("Use IPv4:port or [IPv6]:port. The default port is 7742.");
        if (result.Port < 1 || result.Port > 65535) throw new FormatException("Port must be between 1 and 65535.");
        if (!listen && (result.Address.Equals(IPAddress.Any) || result.Address.Equals(IPAddress.IPv6Any))) throw new FormatException("Specify the host's IP address instead of an all-addresses listen address.");
        if (result.Address.Equals(IPAddress.Broadcast) || result.Address.IsIPv6Multicast || (result.Address.GetAddressBytes()[0] >= 224 && result.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) throw new FormatException("Use a single host address, not a multicast or broadcast address.");
        return result;
    }
}
