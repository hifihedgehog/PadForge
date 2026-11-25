using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PadForge.Engine.Privacy
{
    /// <summary>The kinds of private value a scrubber replaces. The order is
    /// the order a scrub check prints them in.</summary>
    public enum PrivateKind
    {
        UserName,
        PcName,
        Folder,
        DeviceInstance,
        SerialNumber,
        BluetoothAddress,
        LanAddress,
        PublicAddress,
        HostName,
        AccessCode,
        Key,
        Peer,
        PairingCode,
        Channel,
        Identifier,
        Email,
        DeviceName,
        ProfileName,
        MacroName,
        LayerName,
        NfcTagName,
        VoicePhrase,
        ChatWord,
        AudioDevice,
        Phone,
        Program,
        /// <summary>Words a person said, which a speech recognizer heard.</summary>
        Speech,
    }

    /// <summary>What this PC calls its user, itself and its folders. A
    /// scrubber takes one so a test can stand in for the real PC.</summary>
    public sealed class PrivacyEnvironment
    {
        public string UserName { get; init; }
        public string DomainName { get; init; }
        public string PcName { get; init; }
        /// <summary>The user's profile folder, such as C:\Users\name.</summary>
        public string ProfileFolder { get; init; }
        /// <summary>The folder PadForge runs from.</summary>
        public string AppFolder { get; init; }

        /// <summary>This PC's own values. A value Windows refuses to give is
        /// left empty.</summary>
        public static PrivacyEnvironment Current()
        {
            static string Read(Func<string> read)
            {
                try { return read() ?? string.Empty; }
                catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException
                    or System.Security.SecurityException or ArgumentException)
                {
                    return string.Empty;
                }
            }
            return new PrivacyEnvironment
            {
                UserName = Read(() => Environment.UserName),
                DomainName = Read(() => Environment.UserDomainName),
                PcName = Read(() => Environment.MachineName),
                ProfileFolder = Read(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                AppFolder = Read(() => AppContext.BaseDirectory),
            };
        }
    }

    /// <summary>One line of a scrub check: how many values of a kind were
    /// replaced, in how many places, and how many places the search of the
    /// finished text found. Never a value.</summary>
    public readonly record struct PrivacyCount(PrivateKind Kind, int Values, int Places, int FoundLate);

    /// <summary>
    /// Takes private values out of text that leaves PadForge: a problem
    /// recording, and what a program reads through Automation. It has no tie
    /// to either.
    ///
    /// <para>Two stages. A typed field (a device path, a serial number, a
    /// profile's name) goes through <see cref="Alias"/> and its relatives,
    /// which hand back a stand-in and never the value. Free text (SDL's
    /// lines, an exception's text, a diagnostics line) goes through
    /// <see cref="Text"/>, which replaces every value the scrubber already
    /// knows and then every run that has the shape of a private value. One
    /// scrubber serves one file, and in it a value always becomes the same
    /// stand-in.</para>
    ///
    /// <para>Safe to call from any thread. It is never called on the poll
    /// thread.</para>
    /// </summary>
    public sealed class PrivacyScrubber
    {
        private readonly object _sync = new();
        private readonly string _appFolder;

        private sealed class Known
        {
            public PrivateKind Kind;
            public string StandIn;
        }

        // The values the scrubber knows, by kind and folded value.
        private readonly Dictionary<(PrivateKind, string), Known> _known = new();
        // Every spelling of a known value the free-text search looks for,
        // folded. A Bluetooth address has three.
        private readonly Dictionary<string, Known> _forms = new(StringComparer.Ordinal);
        private Regex _knownRegex;
        private bool _knownDirty = true;

        private static readonly int KindCount = Enum.GetValues<PrivateKind>().Length;

        private readonly int[] _nextNumber = new int[KindCount];
        private readonly int[] _values = new int[KindCount];
        private readonly int[] _places = new int[KindCount];
        private readonly int[] _foundLate = new int[KindCount];
        private int _tooShort;
        private int _linesLeftOut;

        private readonly Dictionary<string, string> _deviceAliases = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A value of one character cannot be told from the text
        /// around it, so it is not searched for. <see cref="TooShort"/>
        /// counts them.</summary>
        public const int ShortestSearched = 2;

        /// <summary>A value this long or longer is replaced wherever it
        /// stands, inside a longer run of letters and digits too. A shorter
        /// one is replaced only as a word of its own, since it would
        /// otherwise change ordinary words.</summary>
        public const int GluedFrom = 5;

        /// <summary>The longest one search of one line may take.</summary>
        private static readonly TimeSpan SearchLimit = TimeSpan.FromMilliseconds(500);

        public PrivacyScrubber(PrivacyEnvironment environment = null)
        {
            var env = environment ?? PrivacyEnvironment.Current();
            _appFolder = TrimFolder(env.AppFolder);
            Know(PrivateKind.UserName, env.UserName);
            Know(PrivateKind.UserName, LeafOf(env.ProfileFolder));
            Know(PrivateKind.PcName, env.PcName);
            Know(PrivateKind.PcName, env.DomainName);
        }

        // ─────────────────────────────────────────────
        //  Stand-ins
        // ─────────────────────────────────────────────

        private static string WordOf(PrivateKind kind) => kind switch
        {
            PrivateKind.UserName => "user",
            PrivateKind.PcName => "pc",
            PrivateKind.Folder => "folder",
            PrivateKind.DeviceInstance => "i",
            PrivateKind.SerialNumber => "serial",
            PrivateKind.BluetoothAddress => "bt",
            PrivateKind.LanAddress => "lan-ip",
            PrivateKind.PublicAddress => "public-ip",
            PrivateKind.HostName => "host",
            PrivateKind.AccessCode => "access-code",
            PrivateKind.Key => "key",
            PrivateKind.Peer => "peer",
            PrivateKind.PairingCode => "pairing-code",
            PrivateKind.Channel => "channel",
            PrivateKind.Identifier => "id",
            PrivateKind.Email => "email",
            PrivateKind.DeviceName => "device-name",
            PrivateKind.ProfileName => "profile",
            PrivateKind.MacroName => "macro",
            PrivateKind.LayerName => "layer",
            PrivateKind.NfcTagName => "tag",
            PrivateKind.VoicePhrase => "phrase",
            PrivateKind.ChatWord => "word",
            PrivateKind.AudioDevice => "audio",
            PrivateKind.Phone => "phone",
            PrivateKind.Speech => "speech",
            _ => "program",
        };

        /// <summary>The kinds with one stand-in for every value: there is one
        /// user, one PC and one chat channel, and a folder is told from no
        /// other folder.</summary>
        private static bool IsSingle(PrivateKind kind)
            => kind is PrivateKind.UserName or PrivateKind.PcName or PrivateKind.Folder or PrivateKind.Channel;

        /// <summary>The stand-in for the folder PadForge runs from.</summary>
        public const string AppFolderStandIn = "<padforge>";

        /// <summary>The stand-in for any other folder.</summary>
        public const string FolderStandIn = "<folder>";

        /// <summary>The stand-in for a Run Program macro's arguments.</summary>
        public const string ArgumentsStandIn = "<args>";

        /// <summary>The stand-in for a device path with nothing in it that
        /// says what kind of device it is.</summary>
        public const string DeviceStandIn = "<device>";

        /// <summary>What a line becomes when a search of it ran out of time,
        /// which only a line built to stall a pattern does. Text that was not
        /// checked is never written.</summary>
        public const string LineLeftOut = "<line left out>";

        /// <summary>A text macro's content as its length, which is all a
        /// reader needs of it.</summary>
        public static string TextLength(string text)
            => "<text of " + (text?.Length ?? 0).ToString(CultureInfo.InvariantCulture) + " characters>";

        /// <summary>Words a person said as their length, as a text macro's
        /// content is written.</summary>
        public static string SpeechLength(string text)
            => "<speech of " + (text?.Length ?? 0).ToString(CultureInfo.InvariantCulture) + " characters>";

        // Every stand-in this class writes. The searches step over them, so
        // a value that happens to spell part of one cannot break it.
        private const string StandInShape =
            @"<(?:user|pc|folder|padforge|args|channel|device|line left out)>"
            + @"|<(?:i|serial|bt|lan-ip|public-ip|host|access-code|key|peer|pairing-code|id|email|device-name|profile|macro|layer|tag|phrase|word|audio|phone|program) [0-9]+>"
            + @"|<serial of D[0-9]+>|<(?:text|speech) of [0-9]+ characters>";

        private static readonly Regex StandIns = new(StandInShape,
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>True when <paramref name="text"/> is one stand-in and
        /// nothing else.</summary>
        public static bool IsStandIn(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var m = StandIns.Match(text);
            return m.Success && m.Index == 0 && m.Length == text.Length;
        }

        // ─────────────────────────────────────────────
        //  Stage 1: typed fields
        // ─────────────────────────────────────────────

        /// <summary>A device's name in this file: D1, D2 and so on, in the
        /// order the devices are first named. <paramref name="key"/> tells
        /// devices apart and is never written.</summary>
        public string DeviceAlias(string key)
        {
            lock (_sync)
            {
                key ??= string.Empty;
                if (!_deviceAliases.TryGetValue(key, out string alias))
                {
                    alias = "D" + (_deviceAliases.Count + 1).ToString(CultureInfo.InvariantCulture);
                    _deviceAliases[key] = alias;
                }
                return alias;
            }
        }

        /// <summary>A virtual controller's name in this file: C1 for the
        /// first slot to C16 for the last.</summary>
        public static string ControllerAlias(int slotIndex)
            => "C" + (slotIndex + 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>The stand-in for a private value of a known kind. The
        /// value is remembered, so free text that holds it later gets the
        /// same stand-in. Empty in, empty out.</summary>
        public string Alias(PrivateKind kind, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string trimmed = value.Trim();
            if (IsStandIn(trimmed)) return trimmed;
            lock (_sync)
            {
                var known = KnowLocked(kind, trimmed, null);
                _places[(int)known.Kind]++;
                return known.StandIn;
            }
        }

        /// <summary>A device's serial number, named after the device that
        /// has it. A Bluetooth device's serial number is its address, which
        /// is a Bluetooth address's stand-in, the same one the address gets
        /// wherever else it stands.</summary>
        public string Serial(string serial, string deviceAlias)
        {
            if (string.IsNullOrWhiteSpace(serial)) return string.Empty;
            if (BareBluetooth(serial.Trim()) != null) return Alias(PrivateKind.BluetoothAddress, serial);
            lock (_sync)
            {
                string standIn = string.IsNullOrEmpty(deviceAlias) ? null : "<serial of " + deviceAlias + ">";
                var known = KnowLocked(PrivateKind.SerialNumber, serial.Trim(), standIn);
                _places[(int)known.Kind]++;
                return known.StandIn;
            }
        }

        /// <summary>Remembers a private value without writing it, so free
        /// text that holds it is scrubbed. This PC's live values go in
        /// through here when a file starts.</summary>
        public void Know(PrivateKind kind, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string trimmed = value.Trim();
            if (IsStandIn(trimmed)) return;
            lock (_sync) KnowLocked(kind, trimmed, null);
        }

        private Known KnowLocked(PrivateKind kind, string value, string standIn)
        {
            if (kind == PrivateKind.BluetoothAddress && BareBluetooth(value) is { } bare) value = bare;
            string folded = Fold(value);
            var key = (kind, folded);
            if (_known.TryGetValue(key, out var known)) return known;

            // A value already known under another kind keeps its first
            // stand-in: one value, one stand-in in a file.
            if (_forms.TryGetValue(folded, out var other))
            {
                _known[key] = other;
                // This kind's other spellings lead to the same stand-in:
                // a serial number that is a Bluetooth address is searched
                // for with its separators too.
                foreach (string form in FormsOf(kind, value))
                    if (_forms.TryAdd(Fold(form), other)) _knownDirty = true;
                return other;
            }

            known = new Known { Kind = kind };
            known.StandIn = standIn ?? (IsSingle(kind)
                ? "<" + WordOf(kind) + ">"
                : "<" + WordOf(kind) + " " + (++_nextNumber[(int)kind]).ToString(CultureInfo.InvariantCulture) + ">");
            _known[key] = known;
            _values[(int)kind]++;

            if (value.Length < ShortestSearched)
            {
                _tooShort++;
                return known;
            }
            foreach (string form in FormsOf(kind, value))
                _forms.TryAdd(Fold(form), known);
            _knownDirty = true;
            return known;
        }

        /// <summary>The spellings a value is searched under. A Bluetooth
        /// address is written bare, with colons and with hyphens.</summary>
        private static IEnumerable<string> FormsOf(PrivateKind kind, string value)
        {
            yield return value;
            if (kind != PrivateKind.BluetoothAddress || value.Length != 12) yield break;
            foreach (char separator in new[] { ':', '-' })
            {
                var sb = new StringBuilder(17);
                for (int i = 0; i < 12; i += 2)
                {
                    if (i > 0) sb.Append(separator);
                    sb.Append(value, i, 2);
                }
                yield return sb.ToString();
            }
        }

        /// <summary>A Bluetooth address as its twelve hex digits, or null
        /// when the text is none.</summary>
        public static string BareBluetooth(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var sb = new StringBuilder(12);
            foreach (char c in text)
            {
                if (Uri.IsHexDigit(c)) sb.Append(char.ToUpperInvariant(c));
                else if (c is not (':' or '-')) return null;
            }
            return sb.Length == 12 ? sb.ToString() : null;
        }

        private static string Fold(string value) => value.ToUpperInvariant();

        private static readonly Regex VendorToken = new(@"VID[_&]([0-9A-F]{4,8})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex ProductToken = new(@"PID[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex InterfaceToken = new(@"MI_([0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex XInputToken = new(@"IG_([0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex CollectionToken = new(@"COL([0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex DriveStart = new(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex EnumeratorWord = new(@"^[A-Za-z][A-Za-z0-9_]{1,19}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly string[] DevicePathPrefixes = { @"\\?\", @"\\.\", @"\??\" };
        private static readonly char[] DevicePathSeparators = { '\\', '#' };

        /// <summary>A device path cut into what says which kind of device it
        /// is and what says which one: the enumerator with the vendor,
        /// product, interface and collection numbers, and the instance part,
        /// which is null when the path has none.</summary>
        private static (string Kind, string Instance) SplitDevicePath(string path)
        {
            string body = path;
            foreach (string prefix in DevicePathPrefixes)
                if (body.StartsWith(prefix, StringComparison.Ordinal)) { body = body.Substring(prefix.Length); break; }

            string[] parts = body.Split(DevicePathSeparators, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder(48);
            int next = 0;
            if (parts.Length > 0 && EnumeratorWord.IsMatch(parts[0]))
            {
                sb.Append(parts[0].ToUpperInvariant());
                next = 1;
            }

            // The hardware ID is the first part with a vendor or product
            // number. What follows it is the instance.
            int hardware = -1;
            for (int i = next; i < parts.Length; i++)
                if (VendorToken.IsMatch(parts[i]) || ProductToken.IsMatch(parts[i])) { hardware = i; break; }

            if (hardware >= 0)
            {
                string id = parts[hardware];
                var ids = new StringBuilder(32);
                void Add(string text) { if (ids.Length > 0) ids.Append('&'); ids.Append(text); }
                if (VendorToken.Match(id) is { Success: true } v)
                {
                    string digits = v.Groups[1].Value.ToUpperInvariant();
                    Add("VID_" + digits.Substring(digits.Length - 4));
                }
                if (ProductToken.Match(id) is { Success: true } p) Add("PID_" + p.Groups[1].Value.ToUpperInvariant());
                if (InterfaceToken.Match(id) is { Success: true } mi) Add("MI_" + mi.Groups[1].Value.ToUpperInvariant());
                if (XInputToken.Match(id) is { Success: true } ig) Add("IG_" + ig.Groups[1].Value.ToUpperInvariant());
                if (CollectionToken.Match(id) is { Success: true } col) Add("COL" + col.Groups[1].Value.ToUpperInvariant());
                if (sb.Length > 0) sb.Append('\\');
                sb.Append(ids);
                next = hardware + 1;
            }

            // The class GUID a device interface path ends with says what
            // kind of device it is, and is left out with the rest.
            return (sb.ToString(), next < parts.Length ? parts[next] : null);
        }

        /// <summary>The part of a device path or instance ID that tells one
        /// device from another of its model, where a serial number or a
        /// Bluetooth address sits. Null when the path has none.</summary>
        public static string InstancePartOf(string path)
            => string.IsNullOrWhiteSpace(path) ? null : SplitDevicePath(path.Trim()).Instance;

        /// <summary>A device path or instance ID with its instance part
        /// replaced. The enumerator and the vendor, product, interface and
        /// collection numbers stay, since they say what the device is and
        /// not which one.</summary>
        public string DevicePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string trimmed = path.Trim();
            lock (_sync)
                return LongFilePath(trimmed) is { } file ? FilePathLocked(file) : DevicePathLocked(trimmed);
        }

        /// <summary>A file path written the long way, \\?\C:\folder\file,
        /// opens like a device path and is a file's. Returns it without its
        /// opening, or null for anything else.</summary>
        private static string LongFilePath(string path)
        {
            foreach (string prefix in DevicePathPrefixes)
                if (path.StartsWith(prefix, StringComparison.Ordinal) && DriveStart.IsMatch(path.AsSpan(prefix.Length)))
                    return path.Substring(prefix.Length);
            return null;
        }

        private string DevicePathLocked(string path)
        {
            var (kind, instance) = SplitDevicePath(path);
            if (instance == null) return kind.Length == 0 ? DeviceStandIn : kind;
            if (IsStandIn(instance)) return kind.Length == 0 ? instance : kind + "\\" + instance;
            var known = KnowLocked(PrivateKind.DeviceInstance, instance, null);
            _places[(int)known.Kind]++;
            return kind.Length == 0 ? known.StandIn : kind + "\\" + known.StandIn;
        }

        /// <summary>A file path as its file name. A file in PadForge's own
        /// folder keeps a stand-in for that folder.</summary>
        public string FilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            lock (_sync) return FilePathLocked(path.Trim());
        }

        private string FilePathLocked(string path)
        {
            string normal = path.Replace('/', '\\');
            int cut = normal.LastIndexOf('\\');
            string name = cut < 0 ? normal : normal.Substring(cut + 1);
            name = ReplaceKnownLocked(name, Counting.Place);
            if (cut < 0) return name;

            string folder = TrimFolder(normal.Substring(0, cut));
            // A folder the known-value pass replaced was counted there.
            if (!folder.StartsWith(FolderStandIn, StringComparison.Ordinal)) _places[(int)PrivateKind.Folder]++;
            bool own = _appFolder.Length > 0 && string.Equals(folder, _appFolder, StringComparison.OrdinalIgnoreCase);
            return (own ? AppFolderStandIn : FolderStandIn) + "\\" + name;
        }

        private static string TrimFolder(string folder)
            => string.IsNullOrEmpty(folder) ? string.Empty : folder.Replace('/', '\\').TrimEnd('\\');

        private static string LeafOf(string folder)
        {
            string trimmed = TrimFolder(folder);
            int cut = trimmed.LastIndexOf('\\');
            return cut < 0 ? trimmed : trimmed.Substring(cut + 1);
        }

        /// <summary>An IPv4 or IPv6 address. A loopback address stays, since
        /// it names no PC. The rest become a stand-in that says whether the
        /// address is on the local network or a public one. Text that is no
        /// address comes back as it is.</summary>
        public string Address(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return string.Empty;
            string trimmed = address.Trim();
            lock (_sync) return AddressLocked(trimmed) ?? trimmed;
        }

        private string AddressLocked(string text)
        {
            string bare = text.Trim('[', ']');
            if (!IPAddress.TryParse(bare, out var ip)) return null;
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
                return text;
            var known = KnowLocked(IsLocal(ip) ? PrivateKind.LanAddress : PrivateKind.PublicAddress, ip.ToString(), null);
            _places[(int)known.Kind]++;
            return known.StandIn;
        }

        /// <summary>Whether an address is one a router hands out inside a
        /// home or office network, or one a PC gives itself.</summary>
        public static bool IsLocal(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            }
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        }

        // The hosts PadForge itself talks to or names in its own lines. Any
        // other host name becomes a stand-in.
        private static readonly HashSet<string> PublicHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "localhost",
            "github.com", "api.github.com", "raw.githubusercontent.com", "objects.githubusercontent.com",
            "release-assets.githubusercontent.com", "codeload.github.com",
            "padforge.org", "www.padforge.org", "docs.padforge.org", "hidmaestro.org",
            "steamcommunity.com", "api.steampowered.com", "store.steampowered.com",
            "twitch.tv", "www.twitch.tv", "irc-ws.chat.twitch.tv", "id.twitch.tv", "api.twitch.tv",
            "microsoft.com", "www.microsoft.com", "aka.ms", "libsdl.org", "www.libsdl.org",
        };

        /// <summary>A host name. One from PadForge's own list stays, an
        /// address goes the way <see cref="Address"/> sends it, and any other
        /// name becomes a stand-in.</summary>
        public string Host(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return string.Empty;
            lock (_sync) return HostLocked(host.Trim());
        }

        private string HostLocked(string host)
        {
            if (IsStandIn(host)) return host;
            bool addressShaped = host.Contains(':') || CountOf(host, '.') == 3;
            if (addressShaped && AddressLocked(host) is { } address) return address;
            if (PublicHosts.Contains(host)) return host;
            var known = KnowLocked(PrivateKind.HostName, host, null);
            _places[(int)known.Kind]++;
            return known.StandIn;
        }

        /// <summary>A GUID. One that names a kind of device stays
        /// (<see cref="IsDeviceTypeGuid"/>). Any other becomes a stand-in.
        /// Text that is no GUID comes back as it is.</summary>
        public string Identifier(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return string.Empty;
            lock (_sync) return IdentifierLocked(guid.Trim());
        }

        private string IdentifierLocked(string text)
        {
            if (!Guid.TryParse(text, out var guid)) return text;
            if (IsDeviceTypeGuid(guid)) return text;
            // A device of this file is named by its label, so a line that
            // names it by its GUID can be tied to its other lines (#600).
            if (_deviceAliases.TryGetValue(guid.ToString("D"), out string device)) return device;
            var known = KnowLocked(PrivateKind.Identifier, guid.ToString("D"), null);
            _places[(int)known.Kind]++;
            return known.StandIn;
        }

        private static readonly HashSet<Guid> DeviceClassGuids = new()
        {
            new Guid("4d1e55b2-f16f-11cf-88cb-001111000030"), // HID interface
            new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da"), // HID class
            new Guid("ec87f1e3-c13b-4100-b5f7-8b84d54260cb"), // XUSB interface
            new Guid("a5dcbf10-6530-11d2-901f-00c04fb951ed"), // USB device interface
            new Guid("36fc9e60-c465-11cf-8056-444553540000"), // USB class
            new Guid("88bae032-5a81-49f0-bc3d-a4ff138216d6"), // USB device class
            new Guid("dee824ef-729b-4a0e-9c14-b7117d33a817"), // WinUSB interface
            new Guid("884b96c3-56ef-11d1-bc8c-00a0c91405dd"), // keyboard interface
            new Guid("4d36e96b-e325-11ce-bfc1-08002be10318"), // keyboard class
            new Guid("378de44c-56ef-11d1-bc8c-00a0c91405dd"), // mouse interface
            new Guid("4d36e96f-e325-11ce-bfc1-08002be10318"), // mouse class
            new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974"), // Bluetooth class
            new Guid("0850302a-b344-4fda-9be9-90576b8d46f0"), // Bluetooth radio interface
            new Guid("781ef630-72b2-11d2-b852-00c04fad5101"), // Bluetooth LE interface
            new Guid("4d36e97d-e325-11ce-bfc1-08002be10318"), // system class
            new Guid("4d36e96c-e325-11ce-bfc1-08002be10318"), // media class
            new Guid("62f9c741-b25a-46ce-b54c-9bccce08b6f2"), // software device class
        };

        /// <summary>True for a GUID that names a kind of device and no one
        /// device, user or PC: a device class or interface class, a product
        /// GUID made of a product and a vendor number, a DirectInput object
        /// type, or a Bluetooth service.</summary>
        public static bool IsDeviceTypeGuid(Guid guid)
        {
            if (guid == Guid.Empty || DeviceClassGuids.Contains(guid)) return true;
            string text = guid.ToString("D");
            // A product GUID: the product and vendor numbers, then "PIDVID".
            if (text.EndsWith("-0000-0000-0000-504944564944", StringComparison.OrdinalIgnoreCase)) return true;
            // DirectInput's object types end in the letters "DEST".
            if (text.EndsWith("-444553540000", StringComparison.OrdinalIgnoreCase)) return true;
            // A Bluetooth service or profile, from the Bluetooth base UUID.
            return text.StartsWith("0000", StringComparison.Ordinal)
                && text.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase);
        }

        // ─────────────────────────────────────────────
        //  Stage 2: free text
        // ─────────────────────────────────────────────

        // Device paths and instance IDs, which carry a serial number or a
        // Bluetooth address in their instance part.
        private static readonly Regex DevicePathShape = new(
            @"(?:\\\\[?.]\\|\\\?\?\\)[^\s""'<>|,;)\]]+"
            + @"|(?<![A-Za-z0-9_])(?:HID|USB|USBSTOR|BTHENUM|BTHLE|BTHLEDEVICE|BTHHFENUM|BTH|SWD|ROOT|XUSB|HIDCLASS|ACPI|PCI|TS_INPT|VIGEM|HIDMAESTRO|BTHPS3BUS|DSHIDMINI|MIDI)\\[^\s""'<>|,;)\]]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, SearchLimit);

        // One character of a file or folder name, or one whole stand-in the
        // known-value pass wrote into a name.
        private const string NameAtom = "(?:" + StandInShape + @"|[^\\/:*?""|<>\r\n\s])";

        // A web address, then a path on a drive or a share, or under a folder
        // the known-value pass already replaced (the user's profile folder,
        // known whole), whose folders after it go too (#600). A folder's name
        // can hold one space between two runs, as Program Files does, and a
        // file's name ends at the first space.
        private static readonly Regex UrlOrPath = new(
            @"(?<url>(?<![A-Za-z0-9])[A-Za-z][A-Za-z0-9+.\-]{1,15}://(?:" + StandInShape + @"|[^\s""'|<>])+)"
            + @"|(?<path>(?:(?<![A-Za-z0-9])[A-Za-z]:|\\\\[A-Za-z0-9._$\-]+|" + FolderStandIn + @")[\\/](?:" + NameAtom + "+(?: " + NameAtom + @"+)*[\\/])*" + NameAtom + "*)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, SearchLimit);

        // Everything that is one run with a telling shape. A stand-in comes
        // first, so nothing inside one is read again.
        private static readonly Regex Tokens = new(
            "(?<skip>" + StandInShape + ")"
            + @"|(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})"
            + @"|(?<guid>\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?)"
            + @"|(?<ip4>(?<![0-9A-Za-z.])(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])(?:\.(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])){3}(?![0-9A-Za-z]|\.[0-9]))"
            + @"|(?<mac>(?<![0-9A-Za-z:\-])[0-9A-Fa-f]{2}(?<sep>[:\-])[0-9A-Fa-f]{2}(?:\k<sep>[0-9A-Fa-f]{2}){4}(?![0-9A-Za-z:\-]))"
            + @"|(?<ip6>(?<![0-9A-Za-z:.])(?:[0-9A-Fa-f]{1,4})?(?::[0-9A-Fa-f]{0,4}){2,7}(?![0-9A-Za-z:.]))"
            + @"|(?<code>(?<![0-9A-Za-z\-])[0-9A-HJKMNP-TV-Z]{5}(?:-[0-9A-HJKMNP-TV-Z]{5}){2,}(?:-[0-9A-HJKMNP-TV-Z]{1,4})?(?![0-9A-Za-z\-]))"
            + @"|(?<hex>(?<![0-9A-Za-z+/_=\-])[0-9A-Fa-f]{12,}(?![0-9A-Za-z+/_=\-]))"
            + @"|(?<b64>(?<![0-9A-Za-z+/_=\-])[0-9A-Za-z+/_\-]{20,}={0,2}(?![0-9A-Za-z+/_=\-]))",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, SearchLimit);

        // Lines whose source writes a name or words in a known place: the
        // speech recognizer's lines name the microphone in brackets and put
        // what it heard in quotes, its capture lines name the microphone
        // before the format, and a web controller's name ends with the name
        // its viewer typed on the phone.
        private static readonly Regex VoiceLine = new(@"^VOICE\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex VoiceEndpoint = new(@"^VOICE \[(?<audio>[^\]]+)\]", RegexOptions.CultureInvariant | RegexOptions.Compiled, SearchLimit);
        private static readonly Regex VoiceCapture = new(@"^VOICE capture (?:alive|stats): (?<audio>.+?) (?=fmt=|callbacks=)", RegexOptions.CultureInvariant | RegexOptions.Compiled, SearchLimit);
        private static readonly Regex Quoted = new(@"""(?<said>[^""]*)""", RegexOptions.CultureInvariant | RegexOptions.Compiled, SearchLimit);
        private static readonly Regex WebViewer = new(
            @"(?:Web Controller|Browser Gamepad): (?<phone>.+?)(?=\s+[A-Za-z]+=|["")\],]|$)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, SearchLimit);

        /// <summary>The name a web controller's viewer typed, from the name
        /// PadForge gives the device ("... Web Controller: name" or "Browser
        /// Gamepad: name"), or null when it carries none.</summary>
        public static string WebViewerOf(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return null;
            var m = WebViewer.Match(deviceName);
            return m.Success && m.Groups["phone"].Value.Trim().Length > 0 ? m.Groups["phone"].Value.Trim() : null;
        }

        private string ReplaceRulesLocked(string text, Counting counting)
        {
            void Counted(PrivateKind kind)
            {
                if (counting == Counting.Late) _foundLate[(int)kind]++;
                else if (counting == Counting.Place) _places[(int)kind]++;
            }
            string Named(PrivateKind kind, string value)
            {
                string trimmed = value.Trim();
                if (trimmed.Length == 0 || IsStandIn(trimmed)) return value;
                var known = KnowLocked(kind, trimmed, null);
                Counted(known.Kind);
                return known.StandIn;
            }

            if (VoiceLine.IsMatch(text))
            {
                text = VoiceEndpoint.Replace(text, m => "VOICE [" + Named(PrivateKind.AudioDevice, m.Groups["audio"].Value) + "]");
                text = VoiceCapture.Replace(text, m => m.Value.Substring(0, m.Groups["audio"].Index - m.Index)
                    + Named(PrivateKind.AudioDevice, m.Groups["audio"].Value) + " ");
                // What was heard. A registered phrase keeps its own stand-in,
                // so a reader sees which one fired. Anything else said is its
                // length.
                text = Quoted.Replace(text, m =>
                {
                    string said = m.Groups["said"].Value;
                    if (said.Trim().Length == 0 || IsStandIn(said)) return m.Value;
                    if (_forms.TryGetValue(Fold(said.Trim()), out var phrase))
                    {
                        Counted(phrase.Kind);
                        return "\"" + phrase.StandIn + "\"";
                    }
                    Counted(PrivateKind.Speech);
                    if (counting != Counting.None) _values[(int)PrivateKind.Speech]++;
                    return "\"" + SpeechLength(said) + "\"";
                });
            }
            return WebViewer.Replace(text, m => m.Value.Substring(0, m.Groups["phone"].Index - m.Index)
                + Named(PrivateKind.Phone, m.Groups["phone"].Value));
        }

        /// <summary>Free text with every private value taken out: the values
        /// this scrubber knows, and every run with the shape of one. Null and
        /// empty come back as they are.</summary>
        public string Text(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            lock (_sync)
            {
                return SearchLocked(() =>
                {
                    // Device paths go first and whole: their instance part
                    // gets one stand-in, where the known-value pass would
                    // cut a serial number out of its middle. A file path
                    // written the long way is left for the path pass, which
                    // reads a folder's name across a space.
                    string line = DevicePathShape.Replace(text, m => LongFilePath(m.Value) != null ? m.Value : DevicePathLocked(m.Value));
                    line = ReplaceRulesLocked(line, Counting.Place);
                    line = ReplaceKnownLocked(line, Counting.Place);
                    line = UrlOrPath.Replace(line, UrlOrPathEvaluator);
                    return Tokens.Replace(line, TokenEvaluator);
                });
            }
        }

        /// <summary>The most times one line is searched again after a search
        /// that ran out of time while it waited for the processor.</summary>
        private const int MostSearchesAgain = 2;

        /// <summary>
        /// Runs one line's search under <see cref="SearchLimit"/>, which
        /// counts time on the clock. A search that runs out of it while this
        /// thread had the processor for less than the limit was held back by
        /// a busy PC and not by the line (#600: a test run beside others left
        /// a plain line out), so it runs again from the counts it started
        /// with. A line that keeps the processor past the limit is left out.
        /// A value learned in a search that stopped stays known and counted
        /// once.
        /// </summary>
        private string SearchLocked(Func<string> search)
        {
            for (int again = 0; ; again++)
            {
                int speech = _values[(int)PrivateKind.Speech];
                int[] places = (int[])_places.Clone(), late = (int[])_foundLate.Clone();
                long cpu = ThreadProcessorTime();
                try
                {
                    return search();
                }
                catch (RegexMatchTimeoutException)
                {
                    long now = ThreadProcessorTime();
                    if (again >= MostSearchesAgain || cpu < 0 || now < 0 || now - cpu >= SearchLimit.Ticks)
                    {
                        _linesLeftOut++;
                        return LineLeftOut;
                    }
                    _values[(int)PrivateKind.Speech] = speech;
                    Array.Copy(places, _places, places.Length);
                    Array.Copy(late, _foundLate, late.Length);
                }
            }
        }

        /// <summary>The processor time this thread has used, in ticks of
        /// 100 ns, or -1 when Windows does not say.</summary>
        private static long ThreadProcessorTime()
            => GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? kernel + user : -1;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

        private static readonly char[] UrlRestStarts = { '/', '?', '#' };

        private string UrlOrPathEvaluator(Match m)
        {
            if (m.Groups["path"].Success) return FilePathLocked(m.Value);

            string url = m.Value;
            int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            string scheme = url.Substring(0, schemeEnd);
            string rest = url.Substring(schemeEnd + 3);
            int cut = rest.IndexOfAny(UrlRestStarts);
            string authority = cut < 0 ? rest : rest.Substring(0, cut);
            int at = authority.LastIndexOf('@');
            if (at >= 0) authority = authority.Substring(at + 1);

            string host = authority, port = string.Empty;
            if (authority.StartsWith('['))
            {
                int close = authority.IndexOf(']');
                if (close > 0) { host = authority.Substring(0, close + 1); port = authority.Substring(close + 1); }
            }
            else
            {
                int colon = authority.LastIndexOf(':');
                if (colon > 0) { host = authority.Substring(0, colon); port = authority.Substring(colon); }
            }
            // The path and the query can hold a key or a code, so they go.
            return scheme + "://" + (host.Length == 0 ? host : HostLocked(host)) + port + (cut < 0 ? string.Empty : "/...");
        }

        private string TokenEvaluator(Match m)
        {
            string value = m.Value;
            if (m.Groups["skip"].Success) return value;
            if (m.Groups["email"].Success) return Count(PrivateKind.Email, value);
            if (m.Groups["guid"].Success) return IdentifierLocked(value);
            if (m.Groups["ip4"].Success) return AddressLocked(value) ?? value;
            if (m.Groups["mac"].Success) return Count(PrivateKind.BluetoothAddress, value);
            if (m.Groups["ip6"].Success)
            {
                // A clock time and a hex dump have colons too. Only what
                // parses as an IPv6 address, written the short way or in
                // full, is one.
                bool shaped = value.Contains("::", StringComparison.Ordinal) || CountOf(value, ':') == 7;
                if (!shaped || !IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                    return value;
                return AddressLocked(value) ?? value;
            }
            if (m.Groups["code"].Success) return Count(PrivateKind.PairingCode, value);
            if (m.Groups["hex"].Success)
            {
                // Twelve hex digits with a letter among them: a Bluetooth
                // address written bare. Twelve digits are more often a
                // number, and this PC's own addresses are known values,
                // which the pass before this one replaced.
                if (value.Length == 12)
                    return HasHexLetter(value) ? Count(PrivateKind.BluetoothAddress, value) : value;
                if (value.Length < 20 || IsSdlGuid(value)) return value;
                return Count(PrivateKind.Key, value);
            }
            return LooksRandom(value) ? Count(PrivateKind.Key, value) : value;
        }

        private string Count(PrivateKind kind, string value)
        {
            var known = KnowLocked(kind, value, null);
            _places[(int)known.Kind]++;
            return known.StandIn;
        }

        private static int CountOf(string text, char c)
        {
            int n = 0;
            foreach (char x in text) if (x == c) n++;
            return n;
        }

        private static bool HasHexLetter(string text)
        {
            foreach (char c in text)
                if (c is (>= 'a' and <= 'f') or (>= 'A' and <= 'F')) return true;
            return false;
        }

        /// <summary>True for the 32 hex digits SDL names a model of joystick
        /// by: the bus, a checksum of the name, then the vendor, the product
        /// and the version, with a zero word after the vendor and after the
        /// product in the standard form. It names a model and no one device,
        /// so it stays.</summary>
        public static bool IsSdlGuid(string hex)
            => hex != null && hex.Length == 32
               && string.CompareOrdinal(hex, 12, "0000", 0, 4) == 0
               && string.CompareOrdinal(hex, 20, "0000", 0, 4) == 0;

        /// <summary>Whether a run of base64 letters reads as random data: two
        /// digits or more with both cases. A function's name or a file's
        /// seldom has all three, and a key in base64 nearly always does.</summary>
        private static bool LooksRandom(string run)
        {
            int digits = 0, upper = 0, lower = 0;
            foreach (char c in run)
            {
                if (c is >= '0' and <= '9') digits++;
                else if (c is >= 'A' and <= 'Z') upper++;
                else if (c is >= 'a' and <= 'z') lower++;
            }
            return digits >= 2 && upper >= 1 && lower >= 1;
        }

        // ─────────────────────────────────────────────
        //  Known values
        // ─────────────────────────────────────────────

        private void RebuildKnownLocked()
        {
            _knownDirty = false;
            if (_forms.Count == 0) { _knownRegex = null; return; }

            // Longest first, so a value that holds another is replaced whole.
            var forms = new List<string>(_forms.Keys);
            forms.Sort((a, b) => b.Length != a.Length ? b.Length.CompareTo(a.Length) : string.CompareOrdinal(a, b));

            var sb = new StringBuilder(256 + forms.Count * 24);
            sb.Append("(?<skip>").Append(StandInShape).Append(')');
            foreach (string form in forms)
            {
                var (before, after) = BoundsOf(form);
                sb.Append('|').Append(before).Append(Regex.Escape(form)).Append(after);
            }
            _knownRegex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, SearchLimit);
        }

        /// <summary>What may not stand right before and right after a known
        /// value for it to be one. A short value is a word of its own. A
        /// longer one is found inside a longer run too, except that an end
        /// that is a digit does not touch another digit: 192.168.1.5 is not
        /// the start of 192.168.1.50, and the code 48210 is not part of
        /// 1482105.</summary>
        private static (string Before, string After) BoundsOf(string form)
        {
            if (form.Length < GluedFrom) return ("(?<![A-Za-z0-9])", "(?![A-Za-z0-9])");
            return (char.IsAsciiDigit(form[0]) ? "(?<![0-9])" : string.Empty,
                    char.IsAsciiDigit(form[^1]) ? "(?![0-9])" : string.Empty);
        }

        private enum Counting { Place, Late, None }

        private string ReplaceKnownLocked(string text, Counting counting)
        {
            if (_knownDirty) RebuildKnownLocked();
            if (_knownRegex == null || string.IsNullOrEmpty(text)) return text;
            return _knownRegex.Replace(text, m =>
            {
                if (m.Groups["skip"].Success) return m.Value;
                if (!_forms.TryGetValue(Fold(m.Value), out var known)) return m.Value;
                if (counting == Counting.Late) _foundLate[(int)known.Kind]++;
                else if (counting == Counting.Place) _places[(int)known.Kind]++;
                return known.StandIn;
            });
        }

        /// <summary>The search of finished text: replaces every value the
        /// scrubber knows, and the names and words a line holds where its
        /// source puts them, and counts each as found late, which a scrub
        /// check prints apart. A value learned after a line was written, such
        /// as the serial number of a device that connected later, is caught
        /// here. With <paramref name="count"/> off the same search counts
        /// nothing, for a second pass over text already counted.</summary>
        public string SearchFinished(string text, bool count = true)
        {
            if (string.IsNullOrEmpty(text)) return text;
            lock (_sync)
            {
                var counting = count ? Counting.Late : Counting.None;
                return SearchLocked(() => ReplaceKnownLocked(ReplaceRulesLocked(text, counting), counting));
            }
        }

        /// <summary>How many values the scrubber knows and searches for.</summary>
        public int KnownValues { get { lock (_sync) return _known.Count - _tooShort; } }

        /// <summary>How many places the search of finished text found a value
        /// in, over every kind.</summary>
        public int FoundLate
        {
            get
            {
                lock (_sync)
                {
                    int total = 0;
                    foreach (int n in _foundLate) total += n;
                    return total;
                }
            }
        }

        /// <summary>How many known values are one character long and so are
        /// not searched for.</summary>
        public int TooShort { get { lock (_sync) return _tooShort; } }

        /// <summary>How many lines were left out because a search of them ran
        /// out of time.</summary>
        public int LinesLeftOut { get { lock (_sync) return _linesLeftOut; } }

        /// <summary>The kinds with a value replaced or found, in the order a
        /// scrub check prints them.</summary>
        public IReadOnlyList<PrivacyCount> Counts()
        {
            lock (_sync)
            {
                var list = new List<PrivacyCount>(KindCount);
                for (int i = 0; i < KindCount; i++)
                    if (_places[i] != 0 || _foundLate[i] != 0)
                        list.Add(new PrivacyCount((PrivateKind)i, _values[i], _places[i], _foundLate[i]));
                return list;
            }
        }

        /// <summary>A kind's name as a scrub check prints it.</summary>
        public static string KindLabel(PrivateKind kind) => kind switch
        {
            PrivateKind.UserName => "user name",
            PrivateKind.PcName => "PC name",
            PrivateKind.Folder => "folders",
            PrivateKind.DeviceInstance => "device instances",
            PrivateKind.SerialNumber => "serial numbers",
            PrivateKind.BluetoothAddress => "Bluetooth addresses",
            PrivateKind.LanAddress => "local network addresses",
            PrivateKind.PublicAddress => "public addresses",
            PrivateKind.HostName => "host names",
            PrivateKind.AccessCode => "access codes",
            PrivateKind.Key => "keys and long tokens",
            PrivateKind.Peer => "Remote Link peers",
            PrivateKind.PairingCode => "pairing codes",
            PrivateKind.Channel => "chat channels",
            PrivateKind.Identifier => "identifiers",
            PrivateKind.Email => "email addresses",
            PrivateKind.DeviceName => "device names the user typed",
            PrivateKind.ProfileName => "profile names",
            PrivateKind.MacroName => "macro names",
            PrivateKind.LayerName => "layer names",
            PrivateKind.NfcTagName => "NFC tag names",
            PrivateKind.VoicePhrase => "voice phrases",
            PrivateKind.ChatWord => "chat words",
            PrivateKind.AudioDevice => "audio devices",
            PrivateKind.Phone => "phones",
            PrivateKind.Speech => "spoken words",
            _ => "programs",
        };

        // ─────────────────────────────────────────────
        //  Self-test
        // ─────────────────────────────────────────────

        /// <summary>
        /// The check a file's start runs: each of this PC's live values is
        /// put through <paramref name="scrub"/> alone, inside a sentence, and
        /// glued between letters when it is long enough to be searched for
        /// there, and what comes back is searched for it. Returns the kinds
        /// with a value that came through, and an empty list when none did.
        /// Nothing it returns holds a value.
        /// </summary>
        public static IReadOnlyList<PrivateKind> Survivors(
            IEnumerable<(PrivateKind Kind, string Value)> liveValues, Func<string, string> scrub)
        {
            var survivors = new List<PrivateKind>();
            foreach (var (kind, raw) in liveValues)
            {
                string value = raw?.Trim();
                if (string.IsNullOrEmpty(value) || value.Length < ShortestSearched) continue;

                var canary = new List<string>
                {
                    value,
                    "read " + value + " from the device",
                    "name=" + value + ";",
                    "(" + value + ")",
                };
                if (value.Length >= GluedFrom) canary.Add("xq" + value + "qx");
                if (kind == PrivateKind.BluetoothAddress && BareBluetooth(value) is { } bare)
                    foreach (string form in FormsOf(PrivateKind.BluetoothAddress, bare))
                        canary.Add("addr " + form + " paired");

                foreach (string line in canary)
                {
                    if (!Holds(scrub(line) ?? string.Empty, value)) continue;
                    if (!survivors.Contains(kind)) survivors.Add(kind);
                    break;
                }
            }
            return survivors;
        }

        /// <summary>Lines in the shapes the free-text rules read, each with a
        /// planted name or sentence: a microphone named after a person, a
        /// phone named after a person, and a sentence said aloud.</summary>
        private static readonly (PrivateKind Kind, string Line, string Planted)[] ShapeCanaries =
        {
            (PrivateKind.AudioDevice, "VOICE [Headset Microphone (Quillon Ashby's AirPods Pro)] listening", "Quillon Ashby"),
            (PrivateKind.AudioDevice, "VOICE capture alive: Headset Microphone (Quillon Ashby's AirPods Pro) fmt=48000Hz/1ch/f32", "Quillon Ashby"),
            (PrivateKind.Phone, "WEBGP deadline DualShock 4 Web Controller: Ysolde Varga's Pixel idle=1200ms neutralized", "Ysolde Varga"),
            (PrivateKind.Phone, "DEV - Browser Gamepad: Ysolde Varga's Pixel", "Ysolde Varga"),
            (PrivateKind.Speech, "VOICE [Mic] heard \"meet me by the north gate at nine\" conf=0.91 gate=1 fires=0", "north gate"),
            (PrivateKind.Speech, "VOICE [Mic] alternates: \"meet me by the north gate\" 0.41 | \"meat me by the north gait\" 0.22", "north g"),
        };

        /// <summary>The check of the free-text rules a file's start runs
        /// beside <see cref="Survivors"/>: each line in a shape a source
        /// writes names or words in goes through <paramref name="scrub"/>,
        /// and the kinds whose planted value came through are returned.</summary>
        public static IReadOnlyList<PrivateKind> ShapeSurvivors(Func<string, string> scrub)
        {
            var survivors = new List<PrivateKind>();
            foreach (var (kind, line, planted) in ShapeCanaries)
                if ((scrub(line) ?? string.Empty).Contains(planted, StringComparison.OrdinalIgnoreCase) && !survivors.Contains(kind))
                    survivors.Add(kind);
            return survivors;
        }

        /// <summary>Whether text still holds a value, where the search
        /// replaces one: a short value as a word of its own, and an end that
        /// is a digit away from other digits (<see cref="BoundsOf"/>).</summary>
        private static bool Holds(string text, string value)
        {
            bool word = value.Length < GluedFrom;
            bool digitStart = char.IsAsciiDigit(value[0]), digitEnd = char.IsAsciiDigit(value[^1]);
            int from = 0;
            while (true)
            {
                int at = text.IndexOf(value, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return false;
                char before = at > 0 ? text[at - 1] : ' ';
                char after = at + value.Length < text.Length ? text[at + value.Length] : ' ';
                bool blocked = word
                    ? char.IsAsciiLetterOrDigit(before) || char.IsAsciiLetterOrDigit(after)
                    : (digitStart && char.IsAsciiDigit(before)) || (digitEnd && char.IsAsciiDigit(after));
                if (!blocked) return true;
                from = at + 1;
            }
        }
    }
}
