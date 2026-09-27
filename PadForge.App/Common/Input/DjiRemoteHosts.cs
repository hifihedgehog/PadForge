using System.Collections.Generic;
using System.Globalization;

namespace PadForge.Common.Input
{
    /// <summary>
    /// DJI RC and DJI RC 2 remotes read over the network (hifihedgehog/SDL#33
    /// Part 6). These remotes serve their sticks on TCP port 40007 on firmware
    /// from before DJI closed that port, and the fork connects only to the
    /// addresses SDL_JOYSTICK_DJI_REMOTE_TCP_HOSTS names
    /// (docs/README-dji-remotes.md). The user adds each remote's address in
    /// the pairing dialog.
    /// </summary>
    public static class DjiRemoteHosts
    {
        /// <summary>The port the remotes serve on (SDL_DJI_TCP_PORT).</summary>
        public const int DefaultPort = 40007;

        /// <summary>The fork reads at most 8 hosts
        /// (SDL_DJI_TCP_MAX_HOSTS).</summary>
        public const int MaxHosts = 8;

        /// <summary>Reads an address the way the fork's parser does
        /// (SDL_dji_tcp_proto.c, DJITCP_ParseHost): four dotted decimal
        /// octets of one to three digits, no leading zero, at most 255, then
        /// an optional colon and a port of one to five digits, no leading
        /// zero, 1 to 65535. The result is the fork's own key form,
        /// "a.b.c.d:port", so an address named with and without the default
        /// port is one entry.</summary>
        public static bool TryNormalize(string text, out string key)
        {
            key = null;
            if (text == null) return false;
            string entry = text.Trim(' ');
            if (entry.Length == 0) return false;

            int port = DefaultPort;
            string address = entry;
            int colon = entry.IndexOf(':');
            if (colon >= 0)
            {
                address = entry.Substring(0, colon);
                if (!TryNumber(entry.Substring(colon + 1), 5, 65535, out port) || port == 0)
                    return false;
            }

            string[] octets = address.Split('.');
            if (octets.Length != 4) return false;
            var values = new int[4];
            for (int i = 0; i < 4; i++)
                if (!TryNumber(octets[i], 3, 255, out values[i]))
                    return false;

            key = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}:{4}",
                values[0], values[1], values[2], values[3], port);
            return true;
        }

        private static bool TryNumber(string text, int maxDigits, int max, out int value)
        {
            value = 0;
            if (text.Length == 0 || text.Length > maxDigits || (text.Length > 1 && text[0] == '0'))
                return false;
            foreach (char c in text)
            {
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return value <= max;
        }

        /// <summary>The hint's value: each host once, in list order,
        /// unreadable entries dropped, at most <see cref="MaxHosts"/>.</summary>
        public static string HintValue(IEnumerable<string> entries)
        {
            if (entries == null) return string.Empty;
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var parts = new List<string>();
            foreach (string entry in entries)
            {
                if (!TryNormalize(entry, out string key) || !seen.Add(key)) continue;
                parts.Add(key);
                if (parts.Count == MaxHosts) break;
            }
            return string.Join(",", parts);
        }
    }
}
