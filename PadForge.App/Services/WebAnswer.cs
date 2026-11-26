using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;
using PadForge.Engine.Common;
using PadForge.Engine.Privacy;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;

namespace PadForge.Services
{
    /// <summary>One device the user paired to answer PadForge's questions
    /// (#600), in PadForge.xml. Its key is kept as a SHA-256 hash only.</summary>
    public sealed class AnswerDeviceData
    {
        [XmlAttribute] public string Id { get; set; }
        [XmlAttribute] public string Name { get; set; }

        /// <summary>SHA-256 of the device's key, lower hex. The key itself
        /// is never written.</summary>
        [XmlAttribute] public string KeyHash { get; set; }

        [XmlAttribute] public DateTime Paired { get; set; }
        [XmlAttribute] public DateTime LastAnswered { get; set; }
    }

    /// <summary>A paired device as the app holds it.</summary>
    internal sealed record AnswerDevice(string Id, string Name, string KeyHash, DateTimeOffset Paired, DateTimeOffset? LastAnswered);

    /// <summary>A pairing the user opened with Pair a Device…: the long
    /// secret the QR code carries, the six digits typed in its place, and
    /// when both stop working.</summary>
    internal sealed record AnswerPairing(string Secret, string Code, DateTimeOffset Until);

    /// <summary>How a pairing ended.</summary>
    internal enum AnswerPairingEnd { Paired, Canceled, TooManyTries, Expired }

    /// <summary>One request to the answer route, as the listener read it.</summary>
    internal sealed record AnswerRequest(string Method, string Path, string Host, string Origin, string Cookie, string Body, IPAddress Remote);

    /// <summary>What the answer route sends back. <see cref="Page"/> asks the
    /// listener for the page itself, which it serves as it serves every page
    /// of the web controller.</summary>
    internal sealed record AnswerReply(int Status, string Json, string SetCookie = null, bool Page = false);

    /// <summary>
    /// Answering from another device (#600): the page at <c>/answer</c> on the
    /// web controller's HTTPS address, which lists the questions waiting for
    /// the user and takes Allow or Don't Allow from a phone or another PC the
    /// user paired. PadForge serves it itself, with its rights, so no program
    /// running as the user can relay or forge an answer, as one could through
    /// the web helper, which runs without rights.
    ///
    /// <para>Pairing: Pair a Device… opens a pairing for five minutes. Its QR
    /// code carries the page's address with a long secret after the #, which
    /// the browser never sends, and the page hands it back in a POST. Six
    /// digits typed on the page are the other way in. Either works once, and
    /// five wrong tries close the pairing. A paired device keeps a key of 32
    /// random bytes in an HttpOnly, Secure, SameSite=Strict cookie, as the
    /// web controller's access code is kept (WebControllerAccess.SetCookieValue),
    /// and PadForge keeps its hash.</para>
    ///
    /// <para>Answering: each question names its request by ID and by the
    /// question's own nonce, and AutomationApprovals.AnswerNamed takes it
    /// once. A key an assistant holds, a web token or a network key, is no
    /// device key, and the route reads no Authorization header.</para>
    ///
    /// <para>The route admits peers on this PC's networks only (private,
    /// link-local and loopback addresses), a Host that names this PC by an
    /// address or its name, and a POST whose Origin is the page's own.</para>
    /// </summary>
    internal sealed class WebAnswer
    {
        internal const string RoutePath = "/answer";
        internal const string CookieName = "PadForgeAnswer";

        /// <summary>How long a pairing lasts.</summary>
        internal static readonly TimeSpan PairingLife = TimeSpan.FromMinutes(5);

        /// <summary>How many wrong codes or secrets close a pairing.</summary>
        internal const int MostWrongTries = 5;

        /// <summary>The most devices kept.</summary>
        internal const int MostDevices = 8;

        /// <summary>The longest device name kept.</summary>
        internal const int LongestName = 40;

        /// <summary>The most bytes of a body the route reads.</summary>
        internal const int LongestBody = 4096;

        /// <summary>The cookie lives 400 days, the most a browser keeps one
        /// (RFC 6265bis 5.6.2), so a paired phone stays paired until the
        /// user removes it.</summary>
        internal const int CookieSeconds = 400 * 24 * 60 * 60;

        internal static WebAnswer Shared { get; } = new WebAnswer();

        private readonly object _sync = new object();
        private readonly List<AnswerDevice> _devices = new();
        private readonly Func<DateTimeOffset> _now;
        private AnswerPairing _pairing;
        private int _wrong;
        private AutomationApprovals _approvals;
        private string _address, _addressProblem;

        internal WebAnswer(Func<DateTimeOffset> now = null)
        {
            _now = now ?? (() => DateTimeOffset.Now);
        }

        /// <summary>Raised when the devices change, which PadForge.xml
        /// keeps.</summary>
        public event EventHandler Changed;

        /// <summary>Raised when the pairing, the address or whether the route
        /// is wanted changes: the row, the pairing window and the listener
        /// follow it.</summary>
        public event EventHandler StateChanged;

        /// <summary>Raised when a pairing ends, with how.</summary>
        public event EventHandler<AnswerPairingEnd> PairingEnded;

        /// <summary>Whether the row shows: the rule <see cref="ShowsRow"/>
        /// over the web grants and the network keys. A test gives its
        /// own.</summary>
        internal Func<bool> RowShows { get; set; } = () => ShowsRow(WebAccessStore.Shared.Grants.Count, NetworkKeyExists());

        /// <summary>
        /// The row's rule: Answer From Another Device shows only while a
        /// caller from elsewhere can ask, a web assistant the user allowed or
        /// an assistant on this network with its key, since only such a
        /// caller leaves the user away from this PC with a question waiting.
        /// </summary>
        internal static bool ShowsRow(int webGrants, bool networkKey) => webGrants > 0 || networkKey;

        /// <summary>Whether a key for an assistant on this network exists.
        /// None does: the network door keeps no keys yet, and this is the
        /// one place the row asks.</summary>
        internal static bool NetworkKeyExists() => false;

        /// <summary>Hands the route the questions it lists and answers, once
        /// Automation's host is made.</summary>
        internal void Attach(AutomationApprovals approvals)
        {
            if (approvals == null) return;
            _approvals = approvals;
        }

        /// <summary>Whether the listener serves the route: the row shows, and
        /// a device is paired or a pairing is open.</summary>
        internal bool Wanted
        {
            get
            {
                if (!RowShows()) return false;
                lock (_sync) return _devices.Count > 0 || OpenPairing() != null;
            }
        }

        /// <summary>The page's address while the listener serves it over
        /// HTTPS, such as https://192.168.1.24:8080/answer, or null.</summary>
        internal string Address { get { lock (_sync) return _address; } }

        /// <summary>Why the page cannot open while the listener runs or
        /// failed to, or null.</summary>
        internal string AddressProblem { get { lock (_sync) return _addressProblem; } }

        /// <summary>Called by the listener's owner: the web controller's
        /// base address while it serves HTTPS, and why not otherwise.</summary>
        internal void SetAddress(string httpsBase, string problem)
        {
            string address = string.IsNullOrEmpty(httpsBase) ? null : httpsBase.TrimEnd('/') + RoutePath;
            lock (_sync)
            {
                if (_address == address && _addressProblem == problem) return;
                _address = address;
                _addressProblem = address == null ? problem : null;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─────────────────────────────────────────────
        //  Devices
        // ─────────────────────────────────────────────

        internal IReadOnlyList<AnswerDevice> Devices { get { lock (_sync) return _devices.ToList(); } }

        internal void Load(AnswerDeviceData[] data)
        {
            lock (_sync)
            {
                _devices.Clear();
                foreach (var d in data ?? Array.Empty<AnswerDeviceData>())
                {
                    if (string.IsNullOrEmpty(d?.Id) || Hex(d.KeyHash) == null || _devices.Count >= MostDevices) continue;
                    _devices.Add(new AnswerDevice(d.Id, CleanName(d.Name), d.KeyHash, Utc(d.Paired),
                        d.LastAnswered == default ? null : Utc(d.LastAnswered)));
                }
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>What goes into PadForge.xml, or null when no device is
        /// paired.</summary>
        internal AnswerDeviceData[] Save()
        {
            lock (_sync)
            {
                if (_devices.Count == 0) return null;
                return _devices.Select(d => new AnswerDeviceData
                {
                    Id = d.Id, Name = d.Name, KeyHash = d.KeyHash,
                    Paired = d.Paired.UtcDateTime, LastAnswered = d.LastAnswered?.UtcDateTime ?? default,
                }).ToArray();
            }
        }

        /// <summary>Removes a paired device. Its key is refused from its next
        /// request on.</summary>
        internal bool Remove(string id)
        {
            bool removed;
            lock (_sync) removed = _devices.RemoveAll(d => d.Id == id) > 0;
            if (removed) Raise();
            return removed;
        }

        /// <summary>The values a read and a recording must never carry: the
        /// open pairing's secret and code, written both ways, and each paired
        /// device's key hash and name.</summary>
        internal void PrivateValues(List<(PrivateKind Kind, string Value)> into)
        {
            lock (_sync)
            {
                if (_pairing != null)
                {
                    into.Add((PrivateKind.Key, _pairing.Secret));
                    into.Add((PrivateKind.PairingCode, _pairing.Code));
                    into.Add((PrivateKind.PairingCode, WebSecrets.Spaced(_pairing.Code)));
                }
                foreach (var d in _devices)
                {
                    into.Add((PrivateKind.Key, d.KeyHash));
                    into.Add((PrivateKind.DeviceName, d.Name));
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Pairing
        // ─────────────────────────────────────────────

        /// <summary>The pairing open now, or null. One past its time is
        /// closed here.</summary>
        internal AnswerPairing Pairing
        {
            get
            {
                bool expired;
                AnswerPairing open;
                lock (_sync)
                {
                    expired = _pairing != null && OpenPairing() == null;
                    if (expired) _pairing = null;
                    open = _pairing;
                }
                if (expired) End(AnswerPairingEnd.Expired);
                return open;
            }
        }

        private AnswerPairing OpenPairing() => _pairing != null && _now() < _pairing.Until ? _pairing : null;

        /// <summary>Opens a new pairing for <see cref="PairingLife"/>, in
        /// place of one that is open. A secret of 32 random bytes and six
        /// random digits, both from the system's random source.</summary>
        internal AnswerPairing StartPairing()
        {
            var pairing = new AnswerPairing(WebSecrets.New(), WebSecrets.SixDigits(), _now() + PairingLife);
            lock (_sync)
            {
                _pairing = pairing;
                _wrong = 0;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            return pairing;
        }

        /// <summary>Closes the open pairing: Cancel, or the window
        /// closed.</summary>
        internal void CancelPairing()
        {
            bool had;
            lock (_sync)
            {
                had = _pairing != null;
                _pairing = null;
            }
            if (had) End(AnswerPairingEnd.Canceled);
        }

        /// <summary>The address the QR code carries: the page's, with the
        /// pairing's secret after the #, or null without an address.</summary>
        internal static string PairingLink(string address, AnswerPairing pairing)
            => address == null || pairing == null ? null : address + "#pair=" + pairing.Secret;

        private void End(AnswerPairingEnd how)
        {
            PairingEnded?.Invoke(this, how);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Raise()
        {
            Changed?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─────────────────────────────────────────────
        //  The route
        // ─────────────────────────────────────────────

        /// <summary>Whether a path is the route's.</summary>
        internal static bool IsRoute(string path)
            => string.Equals(path, RoutePath, StringComparison.Ordinal) || (path != null && path.StartsWith(RoutePath + "/", StringComparison.Ordinal));

        /// <summary>Answers one request to the route.</summary>
        internal AnswerReply Handle(AnswerRequest request)
        {
            if (!IsNearby(request.Remote)) return Refuse(403, "This page works on PadForge’s network only.");
            if (!NamesThisPc(request.Host)) return Refuse(403, "This page opens only at PadForge’s own address.");
            if (!Wanted) return Refuse(404, "Not found.");
            string method = request.Method ?? "GET";
            switch (request.Path)
            {
                case RoutePath:
                    return method == "GET" ? new AnswerReply(200, null, Page: true) : Refuse(405, "Method Not Allowed.");
                case RoutePath + "/state":
                    return method == "GET" ? State(request) : Refuse(405, "Method Not Allowed.");
                case RoutePath + "/pair":
                    if (method != "POST") return Refuse(405, "Method Not Allowed.");
                    return OwnOrigin(request) ? Pair(request) : Refuse(403, "This Origin is not the page’s own.");
                case RoutePath + "/answer":
                    if (method != "POST") return Refuse(405, "Method Not Allowed.");
                    return OwnOrigin(request) ? Answer(request) : Refuse(403, "This Origin is not the page’s own.");
                default:
                    return Refuse(404, "Not found.");
            }
        }

        private AnswerReply State(AnswerRequest request)
        {
            if (DeviceOf(request.Cookie) == null)
                return Json(401, new { paired = false, pairing = Pairing != null });
            var now = _now();
            var questions = new List<object>();
            foreach (var p in _approvals?.Pending ?? Array.Empty<PendingApproval>())
            {
                if (!Listed(p)) continue;
                var r = p.Request;
                var who = r.Who ?? AutomationProgram.Unknown;
                bool named = r.From != null;
                questions.Add(new
                {
                    id = p.Id,
                    nonce = p.Nonce,
                    title = ShownText.Clean(r.Title ?? string.Empty),
                    program = named ? ShownText.Clean(r.From) : ShownText.Clean(!string.IsNullOrEmpty(who.Name) ? who.Name : who.ImageName),
                    path = named ? null : ShownText.Clean(who.ImagePath),
                    signer = named ? null : ShownText.Clean(who.Signer),
                    detail = (r.Detail ?? Array.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(ShownText.Clean).ToArray(),
                    waits = Math.Max(0, (int)Math.Ceiling((p.Expires - now).TotalSeconds)),
                });
            }
            return Json(200, new { paired = true, questions });
        }

        /// <summary>The questions a device lists and answers: a change, and a
        /// program's first connection. A web sign-in's code is for the card on
        /// this PC alone, and Show brings this PC's window forward.</summary>
        internal static bool Listed(PendingApproval pending)
            => pending.Request.Kind is ApprovalKind.Change or ApprovalKind.Consent;

        private AnswerReply Pair(AnswerRequest request)
        {
            var body = Read(request.Body);
            string secret = Text(body, "secret");
            string code = Text(body, "code")?.Replace(" ", string.Empty);
            string name = CleanName(Text(body, "name"));
            AnswerPairing pairing = Pairing;
            if (pairing == null) return Json(410, new { closed = true });
            bool right = secret != null ? Same(secret, pairing.Secret) : code != null && Same(code, pairing.Code);
            string key = null;
            AnswerPairingEnd? ended = null;
            int left = 0;
            lock (_sync)
            {
                // Judged again under the lock: another try may have closed it.
                if (!ReferenceEquals(_pairing, pairing)) return Json(410, new { closed = true });
                if (!right)
                {
                    left = MostWrongTries - ++_wrong;
                    if (left <= 0)
                    {
                        _pairing = null;
                        ended = AnswerPairingEnd.TooManyTries;
                    }
                }
                else if (_devices.Count >= MostDevices)
                {
                    return Json(409, new { full = true });
                }
                else
                {
                    key = WebSecrets.New();
                    _devices.Add(new AnswerDevice(WebSecrets.New(9), name, WebSecrets.Hash(key), _now(), null));
                    _pairing = null;
                    ended = AnswerPairingEnd.Paired;
                }
            }
            if (key != null) Changed?.Invoke(this, EventArgs.Empty);
            if (ended != null) End(ended.Value);
            if (key == null)
                return left > 0 ? Json(401, new { left }) : Json(410, new { closed = true });
            return Json(200, new { paired = true }, SetCookieValue(key));
        }

        private AnswerReply Answer(AnswerRequest request)
        {
            var device = DeviceOf(request.Cookie);
            if (device == null) return Json(401, new { paired = false });
            var body = Read(request.Body);
            if (body is not { ValueKind: JsonValueKind.Object }
                || !body.Value.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out int id)
                || !body.Value.TryGetProperty("allow", out var allowValue) || allowValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Json(400, new { taken = false });
            string nonce = Text(body, "nonce");
            var approvals = _approvals;
            if (approvals == null || !approvals.AnswerNamed(id, nonce, allowValue.GetBoolean(), Listed))
                return Json(409, new { taken = false });
            var when = _now();
            lock (_sync)
            {
                int i = _devices.FindIndex(d => d.Id == device.Id);
                if (i >= 0) _devices[i] = _devices[i] with { LastAnswered = when };
            }
            Raise();
            return Json(200, new { taken = true });
        }

        /// <summary>The paired device whose key the request's cookie
        /// carries, or null. Every kept hash is compared in fixed time.</summary>
        private AnswerDevice DeviceOf(string cookieHeader)
        {
            string key = ReadCookie(cookieHeader);
            if (string.IsNullOrEmpty(key)) return null;
            AnswerDevice found = null;
            lock (_sync)
                foreach (var d in _devices)
                    if (WebSecrets.Matches(key, d.KeyHash)) found = d;
            return found;
        }

        /// <summary>The Set-Cookie value that carries a device's key: on the
        /// route alone, HttpOnly, Secure and SameSite=Strict.</summary>
        internal static string SetCookieValue(string key)
            => $"{CookieName}={key}; Path={RoutePath}; Max-Age={CookieSeconds.ToString(CultureInfo.InvariantCulture)}; HttpOnly; Secure; SameSite=Strict";

        /// <summary>The device key in a raw Cookie header, or null.</summary>
        internal static string ReadCookie(string cookieHeader)
        {
            if (string.IsNullOrEmpty(cookieHeader)) return null;
            foreach (string part in cookieHeader.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (part.Substring(0, eq).Trim() == CookieName) return part.Substring(eq + 1).Trim();
            }
            return null;
        }

        /// <summary>Whether a peer is on one of this PC's networks: loopback,
        /// a private IPv4 address (RFC 1918) or a link-local one, or an IPv6
        /// link-local or unique local address.</summary>
        internal static bool IsNearby(IPAddress address)
        {
            if (address == null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily == AddressFamily.InterNetwork) return PadForge.Engine.RemoteLink.HolePuncher.IsPrivateAddress(address);
            return address.AddressFamily == AddressFamily.InterNetworkV6
                   && (address.IsIPv6LinkLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC);
        }

        /// <summary>Whether a Host header names this PC: an address on its
        /// networks, localhost, or the PC's own name, with or without .local,
        /// and any port. A page on another site's name that a rebound DNS
        /// answer points here names no such host.</summary>
        internal static bool NamesThisPc(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string name = host;
            if (name.StartsWith("[", StringComparison.Ordinal))
            {
                int close = name.IndexOf(']');
                if (close < 0) return false;
                name = name.Substring(1, close - 1);
            }
            else
            {
                int colon = name.LastIndexOf(':');
                if (colon >= 0) name = name.Substring(0, colon);
            }
            if (IPAddress.TryParse(name, out var address)) return IsNearby(address);
            string pc = Environment.MachineName;
            return string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, pc, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, pc + ".local", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether a POST came from the page itself: its Origin is
        /// https:// and the request's own Host. Browsers send Origin on every
        /// POST a page makes.</summary>
        private static bool OwnOrigin(AnswerRequest request)
            => request.Origin != null && request.Host != null
               && string.Equals(request.Origin.TrimEnd('/'), "https://" + request.Host, StringComparison.OrdinalIgnoreCase);

        private static bool Same(string offered, string expected)
            => offered != null && expected != null
               && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(offered), Encoding.UTF8.GetBytes(expected));

        private static JsonElement? Read(string body)
        {
            if (string.IsNullOrEmpty(body) || body.Length > LongestBody) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }
            catch (JsonException) { return null; }
        }

        private static string Text(JsonElement? body, string name)
            => body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>A name a device gave itself, cleaned and cut to
        /// <see cref="LongestName"/>, or the words for a device that gave
        /// none.</summary>
        internal static string CleanName(string name)
        {
            string clean = ShownText.Clean(name ?? string.Empty)?.Trim() ?? string.Empty;
            if (clean.Length > LongestName) clean = clean.Substring(0, LongestName).TrimEnd();
            return clean.Length > 0 ? clean : Strings.Instance.Answer_Device_Unnamed;
        }

        private static AnswerReply Refuse(int status, string error) => Json(status, new { error });

        private static AnswerReply Json(int status, object value, string setCookie = null)
            => new AnswerReply(status, JsonSerializer.Serialize(value), setCookie);

        private static DateTimeOffset Utc(DateTime when)
            => when == default ? DateTimeOffset.MinValue : new DateTimeOffset(DateTime.SpecifyKind(when, DateTimeKind.Utc));

        private static string Hex(string hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length != 64) return null;
            foreach (char c in hash)
                if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
            return hash;
        }
    }
}
