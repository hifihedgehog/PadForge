using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PadForge.Services.Automation.Web
{
    /// <summary>
    /// A key an assistant on another PC calls PadForge with (#600 item 53),
    /// made on the Automation page. It is named by the name the user gave
    /// it, works at the level the user picked for it, and is kept as the
    /// SHA-256 of its text alone, as a web assistant's tokens are.
    /// </summary>
    internal sealed record NetworkKey
    {
        /// <summary>PadForge's own ID for it, random, for the pipe and the
        /// page.</summary>
        public string Id { get; init; }

        /// <summary>The name the user gave it, such as the other PC's.</summary>
        public string Name { get; init; }

        public AutomationLevel Level { get; init; }

        /// <summary>SHA-256 of the key's text, lower hex. The text itself is
        /// shown once and never written.</summary>
        public string Hash { get; init; }

        public DateTimeOffset Made { get; init; }
        public DateTimeOffset? LastUsed { get; init; }

        /// <summary>What it may drive and change (#600 DESIGN-2 N3), null
        /// for all of PadForge.</summary>
        public ProgramReach Reach { get; init; }
    }

    /// <summary>
    /// Reach PadForge From This Network (#600 item 53): what PadForge sets up
    /// with its rights so the web helper, which runs without them, may
    /// listen on this PC's network addresses, and the names a request must
    /// call this PC by.
    ///
    /// <para>The helper listens on <c>http://+:port/</c>. Windows lets a
    /// process without rights take that prefix only with a URL reservation
    /// for its user: PadForge makes one for the signed-in user's SID alone,
    /// with the right to listen and not to delegate
    /// (<c>sddl=D:(A;;GX;;;SID)</c>, the form <c>netsh http show urlacl</c>
    /// prints for "Listen: Yes, Delegate: No"), never for Everyone. The
    /// firewall rule lets the port in on the Private profile alone, through
    /// the web controller's own rule code
    /// (WebControllerServer.EnsureInboundFirewallRule). Both go when the row
    /// is turned off or its port changes.</para>
    ///
    /// <para>The port is picked once, below 49152, where Windows' dynamic
    /// range starts and Hyper-V and WSL reserve blocks at boot, outside every
    /// range <c>netsh interface ipv4 show excludedportrange
    /// protocol=tcp</c> lists, and not one a program listens on. It is kept
    /// in PadForge.xml and never moved by a fallback, since the reservation,
    /// the rule and each assistant's saved line name it.</para>
    /// </summary>
    internal sealed class NetworkDoor
    {
        internal const int LowestPort = 1024, HighestPort = 49151;

        /// <summary>The firewall rule's name.</summary>
        internal const string RuleName = "PadForge Assistants on This Network";

        /// <summary>What every key starts with, so a person and a scrubber
        /// can tell one.</summary>
        internal const string KeyPrefix = "pfn_";

        /// <summary>The most keys kept.</summary>
        internal const int MostKeys = 8;

        internal const string ExcludedRangesCommand = "interface ipv4 show excludedportrange protocol=tcp";

        private readonly Func<string, string> _netsh;

        /// <param name="netsh">Runs one netsh command with PadForge's rights
        /// and gives its output. The web controller's runner when null,
        /// which the test host's fence refuses.</param>
        internal NetworkDoor(Func<string, string> netsh = null) => _netsh = netsh ?? WebControllerServer.RunNetsh;

        /// <summary>A new key's text: the prefix and 24 random bytes,
        /// base64url.</summary>
        internal static string NewKey() => KeyPrefix + WebSecrets.New(24);

        internal static bool IsPort(int port) => port is >= LowestPort and <= HighestPort;

        /// <summary>The prefix the helper listens on.</summary>
        internal static string Prefix(int port) => "http://+:" + port.ToString(CultureInfo.InvariantCulture) + "/";

        /// <summary>The reservation for one user's SID: listen, no
        /// delegate.</summary>
        internal static string ReservationAdd(int port, string sid) => "http add urlacl url=" + Prefix(port) + " sddl=D:(A;;GX;;;" + sid + ")";

        internal static string ReservationDelete(int port) => "http delete urlacl url=" + Prefix(port);

        internal static string RuleDelete => "advfirewall firewall delete rule name=\"" + RuleName + "\"";

        /// <summary>Whether a SID is one netsh may be handed: S-1- and
        /// digits and dashes alone.</summary>
        internal static bool IsSid(string sid) => sid != null && Regex.IsMatch(sid, @"\AS-1-[0-9]+(-[0-9]+)+\z");

        /// <summary>Makes the reservation and the rule for a port, after
        /// taking away the ones of <paramref name="before"/> when it differs.
        /// Each is deleted before it is added, so a run again changes
        /// nothing and a reservation of another user is replaced. Best
        /// effort and blocking, as netsh is: callers hop to the thread
        /// pool.</summary>
        internal void Apply(int port, string sid, int before = 0)
        {
            if (!IsPort(port) || !IsSid(sid)) return;
            if (before != 0 && before != port) Remove(before);
            try
            {
                _netsh(ReservationDelete(port));
                _netsh(ReservationAdd(port, sid));
            }
            catch { /* best effort, as the web controller's rule is */ }
            WebControllerServer.EnsureInboundFirewallRule(RuleName, "TCP", port, "private", _netsh);
        }

        /// <summary>Takes away the reservation and the rule of a port.</summary>
        internal void Remove(int port)
        {
            if (!IsPort(port)) return;
            try { _netsh(ReservationDelete(port)); } catch { }
            try { _netsh(RuleDelete); } catch { }
        }

        /// <summary>Picks the port the first time the row is turned on: one
        /// in 1024 to 49151 outside the ranges Windows excludes and not one a
        /// program listens on. Zero when none is found.</summary>
        internal int PickPort(Random random = null, ISet<int> listening = null)
        {
            string excluded;
            try { excluded = _netsh(ExcludedRangesCommand); }
            catch { excluded = string.Empty; }
            return PickPort(ExcludedRanges(excluded), listening ?? Listening(), random ?? Random.Shared);
        }

        /// <summary>The pick itself, from ranges and ports given.</summary>
        internal static int PickPort(IReadOnlyList<(int Start, int End)> excluded, ISet<int> listening, Random random)
        {
            for (int attempt = 0; attempt < 1000; attempt++)
            {
                int port = random.Next(LowestPort, HighestPort + 1);
                if (excluded.Any(r => port >= r.Start && port <= r.End)) continue;
                if (listening != null && listening.Contains(port)) continue;
                return port;
            }
            return 0;
        }

        /// <summary>The ranges netsh's table lists: each line that holds two
        /// numbers, its start and its end. Its words are the system's
        /// language, so only the numbers are read.</summary>
        internal static IReadOnlyList<(int Start, int End)> ExcludedRanges(string text)
        {
            var ranges = new List<(int, int)>();
            foreach (string line in (text ?? string.Empty).Split('\n'))
            {
                var m = Regex.Match(line, @"^\s*(\d{1,5})\s+(\d{1,5})(\s|$)");
                if (!m.Success) continue;
                int start = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), end = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (start <= end && end <= 65535) ranges.Add((start, end));
            }
            return ranges;
        }

        /// <summary>The TCP ports a program listens on now, as Windows lists
        /// them. Read alone: nothing is bound.</summary>
        private static ISet<int> Listening()
        {
            try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet(); }
            catch (NetworkInformationException) { return new HashSet<int>(); }
        }

        // ─────────────────────────────────────────────
        //  This PC's names on the network
        // ─────────────────────────────────────────────

        /// <summary>Stands in for the PC's addresses in a test.</summary>
        internal static Func<IReadOnlyList<string>> AddressesSeam;

        /// <summary>This PC's private IPv4 addresses (10/8, 172.16/12,
        /// 192.168/16) on adapters that are up, in the order Windows lists
        /// them.</summary>
        internal static IReadOnlyList<string> PrivateAddresses()
        {
            if (AddressesSeam != null) return AddressesSeam();
            var found = new List<string>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                        if (IsPrivate(unicast.Address) && !found.Contains(unicast.Address.ToString())) found.Add(unicast.Address.ToString());
                }
            }
            catch (NetworkInformationException) { }
            return found;
        }

        internal static bool IsPrivate(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        /// <summary>The names a request may call this PC by: each private
        /// address, the computer's name and its .local name, compared
        /// without regard to case.</summary>
        internal static IReadOnlyCollection<string> ThisPcNames()
        {
            var names = new HashSet<string>(PrivateAddresses(), StringComparer.OrdinalIgnoreCase);
            string machine = Environment.MachineName;
            if (!string.IsNullOrEmpty(machine))
            {
                names.Add(machine);
                names.Add(machine + ".local");
            }
            return names;
        }

        /// <summary>The address an assistant is given for one of this PC's
        /// addresses.</summary>
        internal static string Address(string host, int port) => "http://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + WebAddress.McpPath;

        /// <summary>
        /// The assistant programs the key window gives a line of their own,
        /// each one seen connecting on this PC to a plain HTTP address that is
        /// not localhost with a Bearer header (2026-11-25: Claude Code
        /// 2.1.332, its line pasted into PowerShell and into cmd, then
        /// <c>claude mcp list</c> said Connected, and a wrong key was not).
        /// Every other host takes the address and the header.
        /// </summary>
        internal static readonly IReadOnlyList<(string Key, string Name)> PasteHosts = new[] { ("claude-code", "Claude Code") };

        /// <summary>What the key window gives to paste: a host's own line,
        /// or, for any other host (<paramref name="host"/> null), the address
        /// and the header under the words given.</summary>
        internal static string PasteLine(string host, string address, string key, string addressWord, string headerWord) => host switch
        {
            "claude-code" => "claude mcp add --transport http padforge " + address + " --header \"Authorization: Bearer " + key + "\"",
            _ => addressWord + ": " + address + Environment.NewLine + headerWord + ": Authorization: Bearer " + key,
        };
    }
}
