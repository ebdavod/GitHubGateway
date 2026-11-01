using System;
using System.Net;
using System.Net.Sockets;

namespace GitHubGateway.Infrastructure.Configuration
{
    /// <summary>
    /// Lightweight helper that matches a remote IP against a list of allowed entries.
    /// Each entry is either a single IP address or a CIDR notation (e.g. 192.168.1.0/24).
    /// Both IPv4 and IPv6 are supported.
    /// </summary>
    public static class IpWhitelistMatcher
    {
        /// <summary>
        /// Returns true if <paramref name="remote"/> matches any of the <paramref name="entries"/>.
        /// </summary>
        public static bool IsAllowed(IPAddress remote, string[] entries, bool allowLocalhost)
        {
            if (entries is null || entries.Length == 0) return allowLocalhost && IsLoopback(remote);
            if (remote is null) return false;

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                if (TryMatchCidr(remote, entry.Trim(), out var matched)) return matched;
                if (TryMatchSingle(remote, entry.Trim(), out var matched2)) return matched2;
            }
            return false;
        }

        public static bool IsLoopback(IPAddress ip)
        {
            if (ip is null) return false;
            return IPAddress.IsLoopback(ip);
        }

        /// <summary>
        /// Resolves the actual client IP from the request, taking X-Forwarded-For and
        /// X-Real-IP headers into account. The leftmost address in X-Forwarded-For is used
        /// because that is the original client.
        /// </summary>
        public static IPAddress ResolveClientIp(string? xForwardedFor, string? xRealIp, string? remoteIp)
        {
            if (!string.IsNullOrWhiteSpace(xForwardedFor))
            {
                var first = xForwardedFor.Split(',')[0].Trim();
                if (IPAddress.TryParse(first, out var ip)) return ip;
            }
            if (!string.IsNullOrWhiteSpace(xRealIp))
            {
                if (IPAddress.TryParse(xRealIp, out var ip)) return ip;
            }
            if (!string.IsNullOrWhiteSpace(remoteIp) && IPAddress.TryParse(remoteIp, out var r)) return r;
            return IPAddress.None;
        }

        private static bool TryMatchCidr(IPAddress remote, string entry, out bool matched)
        {
            matched = false;
            var slash = entry.IndexOf('/');
            if (slash <= 0 || slash == entry.Length - 1) return false;

            string ipPart = entry.Substring(0, slash);
            if (!int.TryParse(entry.AsSpan(slash + 1), out var prefixLength)) return false;
            if (!IPAddress.TryParse(ipPart, out var baseIp)) return false;

            if (baseIp.AddressFamily != remote.AddressFamily) return false;

            byte[] remoteBytes = remote.GetAddressBytes();
            byte[] baseBytes = baseIp.GetAddressBytes();
            int totalBits = remoteBytes.Length * 8;
            if (prefixLength < 0 || prefixLength > totalBits) return false;

            int fullBytes = prefixLength / 8;
            int remBits = prefixLength % 8;

            for (int i = 0; i < fullBytes; i++)
                if (remoteBytes[i] != baseBytes[i]) { matched = false; return true; }

            if (remBits == 0) { matched = true; return true; }

            int mask = (byte)~(0xFF >> remBits);
            matched = (remoteBytes[fullBytes] & mask) == (baseBytes[fullBytes] & mask);
            return true;
        }

        private static bool TryMatchSingle(IPAddress remote, string entry, out bool matched)
        {
            matched = false;
            if (!IPAddress.TryParse(entry, out var ip)) return false;
            if (ip.AddressFamily != remote.AddressFamily) return false;
            matched = ip.Equals(remote);
            return true;
        }
    }
}
