using System.Net;
using System.Net.Sockets;

namespace PixelPipe.Web.Services;

public static class ProxyTrust
{
    public static bool IsTrustedPeer(IPAddress? peer, bool trustAnyProxy)
    {
        if (trustAnyProxy || peer is null || IPAddress.IsLoopback(peer))
        {
            return true;
        }

        if (peer.IsIPv4MappedToIPv6)
        {
            peer = peer.MapToIPv4();
        }

        if (peer.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = peer.GetAddressBytes();
            if (bytes[0] == 10)
            {
                return true;
            }

            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }
        }

        if (peer.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = peer.GetAddressBytes();
            if ((bytes[0] & 0xfe) == 0xfc)
            {
                return true;
            }

            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            {
                return true;
            }
        }

        return false;
    }
}

public static class PasswordRules
{
    public static string? Check(string? password, string? confirm)
    {
        password ??= "";
        confirm ??= "";
        if (password.Length < 8)
        {
            return "Password must be at least 8 characters.";
        }

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            return "The passwords do not match.";
        }

        return null;
    }
}
