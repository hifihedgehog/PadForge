using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The engine as the Automation pipe reaches it (#600), present while the
    /// engine runs. The input service gives one at the engine's start and
    /// takes it back at its stop.
    /// </summary>
    internal interface IAutomationEngine
    {
        /// <summary>Puts a program's device on the device list, or reloads
        /// it there (InputManager.RegisterProgramDevice).</summary>
        void Register(ProgramDevice device);

        /// <summary>Takes it offline with its mapped outputs at rest.</summary>
        void Unregister(ProgramDevice device);

        /// <summary>What virtual controller <paramref name="number"/>, as
        /// the interface numbers them, sends to the game now, as the rest
        /// of an <c>ok</c> line. Null when no created controller has that
        /// number.</summary>
        string Outputs(int number);
    }

    /// <summary>
    /// What a program's <c>declare</c> line describes, read from its JSON.
    /// </summary>
    internal static class ProgramDeviceDeclaration
    {
        internal const int LongestName = 64;
        internal const int LongestKey = 64;
        internal const int MostTouchpads = 4;
        internal const int MostFingers = 10;

        /// <summary>
        /// Reads a declaration. The object may hold <c>buttons</c>,
        /// <c>axes</c> and <c>sliders</c> (names, or objects with
        /// <c>index</c>, <c>name</c> and <c>rest</c>), <c>hats</c> (names),
        /// <c>gyro</c>, <c>accel</c> and <c>battery</c> (true or false),
        /// <c>touchpads</c> (finger counts) and <c>feedback</c> (any of
        /// <c>rumble</c>, <c>trigrumble</c>, <c>trigfx</c>, <c>light</c>,
        /// <c>player</c>).
        /// An axis rests at 32768 and a slider at 0 unless it says otherwise.
        /// Trigger rumble brings rumble with it, as every pad with trigger
        /// motors has. Null and the reason in words when it cannot be read.
        /// </summary>
        internal static ProgramDeviceShape Parse(string json, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json)) return new ProgramDeviceShape();
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "the description is not an object"; return null; }

                var buttons = new List<(int, string)>();
                var axes = new List<ProgramAxis>();
                var hats = new List<string>();
                var fingers = new List<int>();
                bool gyro = false, accel = false, battery = false;
                bool rumble = false, trig = false, light = false, player = false, fx = false;

                foreach (var property in root.EnumerateObject())
                {
                    switch (property.Name.ToLowerInvariant())
                    {
                        case "buttons":
                            if (!ReadControls(property.Value, CustomInputState.MaxButtons, "Button ", 0, 0, false,
                                (i, n, _) => buttons.Add((i, n)), out error)) return null;
                            break;
                        case "axes":
                            if (!ReadControls(property.Value, CustomInputState.MaxAxis, "Axis ", 0, 32768, true,
                                (i, n, r) => axes.Add(new ProgramAxis(i, n, r)), out error)) return null;
                            break;
                        case "sliders":
                            if (!ReadControls(property.Value, CustomInputState.MaxSliders, "Slider ", ProgramAxis.FirstSlider, 0, true,
                                (i, n, r) => axes.Add(new ProgramAxis(i, n, r)), out error)) return null;
                            break;
                        case "hats":
                            if (!ReadControls(property.Value, CustomInputState.MaxPovs, "Hat ", 0, 0, false,
                                (i, n, _) => { while (hats.Count <= i) hats.Add("Hat " + hats.Count); hats[i] = n; }, out error)) return null;
                            break;
                        case "gyro": if (!ReadBool(property.Value, out gyro)) { error = "gyro is true or false"; return null; } break;
                        case "accel": if (!ReadBool(property.Value, out accel)) { error = "accel is true or false"; return null; } break;
                        case "battery": if (!ReadBool(property.Value, out battery)) { error = "battery is true or false"; return null; } break;
                        case "touchpads":
                            if (property.Value.ValueKind != JsonValueKind.Array) { error = "touchpads is a list"; return null; }
                            foreach (var pad in property.Value.EnumerateArray())
                            {
                                int count = 2;
                                // TryGetInt32 throws on an element that is
                                // not a number, so each kind is read first.
                                if (pad.ValueKind == JsonValueKind.Number)
                                {
                                    if (!pad.TryGetInt32(out count)) { error = "a touchpad is its finger count"; return null; }
                                }
                                else if (pad.ValueKind == JsonValueKind.Object)
                                {
                                    if (pad.TryGetProperty("fingers", out var f)
                                        && (f.ValueKind != JsonValueKind.Number || !f.TryGetInt32(out count)))
                                    {
                                        error = "fingers is a whole number";
                                        return null;
                                    }
                                }
                                else { error = "a touchpad is its finger count"; return null; }
                                if (count < 1 || count > MostFingers) { error = "a touchpad has 1 to 10 fingers"; return null; }
                                fingers.Add(count);
                            }
                            if (fingers.Count > MostTouchpads) { error = "a device has up to 4 touchpads"; return null; }
                            break;
                        case "feedback":
                            if (property.Value.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var word in property.Value.EnumerateArray())
                                {
                                    switch (word.ValueKind == JsonValueKind.String ? word.GetString()?.ToLowerInvariant() : null)
                                    {
                                        case "rumble": rumble = true; break;
                                        case "trigrumble": trig = true; break;
                                        case "light": light = true; break;
                                        case "player": player = true; break;
                                        case "trigfx": fx = true; break;
                                        default: error = "feedback is rumble, trigrumble, trigfx, light or player"; return null;
                                    }
                                }
                            }
                            else if (property.Value.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var kind in property.Value.EnumerateObject())
                                {
                                    if (!ReadBool(kind.Value, out bool on)) { error = "feedback " + kind.Name + " is true or false"; return null; }
                                    switch (kind.Name.ToLowerInvariant())
                                    {
                                        case "rumble": rumble = on; break;
                                        case "trigrumble": trig = on; break;
                                        case "light": light = on; break;
                                        case "player": player = on; break;
                                        case "trigfx": fx = on; break;
                                        default: error = "feedback is rumble, trigrumble, trigfx, light or player"; return null;
                                    }
                                }
                            }
                            else { error = "feedback is a list"; return null; }
                            break;
                        default:
                            error = "the description has no part " + Clean(property.Name, 32);
                            return null;
                    }
                }

                axes.Sort((a, b) => a.Index.CompareTo(b.Index));
                buttons.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                return new ProgramDeviceShape
                {
                    Buttons = buttons,
                    Axes = axes,
                    Hats = hats,
                    Gyro = gyro,
                    Accel = accel,
                    TouchpadFingers = fingers,
                    Battery = battery,
                    // A pad with trigger motors or adaptive triggers has
                    // rumble motors too, as every such pad does.
                    Rumble = rumble || trig || fx,
                    TriggerRumble = trig,
                    TriggerEffects = fx,
                    Light = light,
                    Player = player,
                };
            }
            catch (JsonException)
            {
                error = "the description is not JSON";
                return null;
            }
        }

        private static bool ReadBool(JsonElement e, out bool value)
        {
            value = e.ValueKind == JsonValueKind.True;
            return e.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }

        private static bool ReadControls(JsonElement list, int most, string defaultName, int indexBase, int defaultRest,
            bool hasRest, Action<int, string, int> add, out string error)
        {
            error = null;
            if (list.ValueKind != JsonValueKind.Array) { error = defaultName.Trim().ToLowerInvariant() + "s is a list"; return false; }
            var seen = new HashSet<int>();
            int position = 0;
            foreach (var item in list.EnumerateArray())
            {
                int index = position;
                string name = null;
                int rest = defaultRest;
                if (item.ValueKind == JsonValueKind.String) name = item.GetString();
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("index", out var i))
                    {
                        if (i.ValueKind != JsonValueKind.Number || !i.TryGetInt32(out index)) { error = "an index is a whole number"; return false; }
                    }
                    if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
                    if (item.TryGetProperty("rest", out var r))
                    {
                        // Raw, or a share of the travel as a percent (#600):
                        // "50%" makes a stick, "0%" a trigger.
                        bool read = hasRest && (r.ValueKind == JsonValueKind.String
                            ? PlainUnits.TryParsePercent(r.GetString(), out double share) && share >= 0 && share <= 100
                              && (rest = PlainUnits.AxisToRaw(share, 0)) >= 0
                            : r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out rest) && rest >= 0 && rest <= 65535);
                        if (!read) { error = "a rest is 0 to 65535, or a percent of the travel such as 50%"; return false; }
                    }
                }
                else { error = "a control is a name or an object"; return false; }
                if (index < 0 || index >= most) { error = defaultName.Trim().ToLowerInvariant() + " index is 0 to " + (most - 1); return false; }
                if (!seen.Add(index)) { error = defaultName.Trim().ToLowerInvariant() + " " + index + " is named twice"; return false; }
                name = Clean(name, LongestName);
                if (name.Length == 0) name = defaultName + index;
                add(indexBase + index, name, rest);
                position++;
            }
            return true;
        }

        /// <summary>A name from a program, made safe for the settings file
        /// and the interface: control and format characters out
        /// (<see cref="ShownText"/>), trimmed, and cut at
        /// <paramref name="longest"/>. A cut never splits a surrogate
        /// pair.</summary>
        internal static string Clean(string text, int longest)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = ShownText.Clean(PadForge.Engine.Common.XmlSafeText.Clean(text));
            if (text.Length > longest)
                text = text.Substring(0, char.IsHighSurrogate(text[longest - 1]) ? longest - 1 : longest);
            return text.Trim();
        }

        /// <summary>Whether a key can be used: 1 to 64 letters, digits,
        /// <c>_</c>, <c>.</c> or <c>-</c>.</summary>
        internal static bool IsKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > LongestKey) return false;
            foreach (char c in key)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')) return false;
            return true;
        }

        /// <summary>The device's instance GUID: from the program's kind, who
        /// it is (<see cref="ProgramDevices.IdentityOf"/>, or a built-in
        /// plugin's name) and the key, so a program that restarts finds its
        /// device and its mappings again.</summary>
        internal static Guid InstanceGuid(string kind, string name, string key)
            => Hash("pfprogram:" + kind + "\n" + name + "\n" + key);

        /// <summary>A product GUID of the device's own, so one program
        /// device's first connect never adopts another's offline row by
        /// product (FindOrCreateUserDevice).</summary>
        internal static Guid ProductGuid(string kind, string name, string key)
            => Hash("pfprogram-product:" + kind + "\n" + name + "\n" + key);

        private static Guid Hash(string text) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// The devices programs add (#600), for as long as PadForge runs. Each is
    /// owned by the session that declared it. A session that ends rests its
    /// devices at once and takes them offline after
    /// <see cref="OfflineAfterMs"/>, unless a program declares the same key
    /// again before then: a program that connects and drops in a loop keeps
    /// one device, and the device list and HidHide are not churned. A device
    /// the user assigned to a virtual controller keeps its row on the Devices
    /// page like any disconnected device's. One nobody assigned goes once it
    /// is offline, row and all, so a program that declares and removes
    /// devices in a loop leaves nothing behind in PadForge.xml.
    ///
    /// <para>Limits: <see cref="MostPerSession"/> devices a session
    /// (<see cref="MostPerPlugin"/> for a plugin) and
    /// <see cref="MostInAll"/> online at once, and in one run of PadForge
    /// <see cref="MostMadePerProgram"/> devices for one program
    /// (<see cref="IdentityOf"/>) and <see cref="MostMadeInAll"/> in all. A
    /// device declared again by the same program under the same key is the
    /// same device and counts once.</para>
    /// </summary>
    internal sealed class ProgramDevices
    {
        /// <summary>
        /// Who a session's devices belong to, which their identity is made
        /// from: a script's and an assistant's consent key, which
        /// AutomationConsent.Identify reads from what Windows says of the
        /// process, never the name the hello claims, as <see cref="IsOwn"/>
        /// judges ownership. A plugin goes by the name it was approved under,
        /// a web assistant by its grant's address and a network caller by
        /// its key's name, each PadForge's own record. Null when the program
        /// is known only by its process number (AutomationConsent.KeyOf with
        /// no path or image), which its next start does not keep.
        /// </summary>
        internal static string IdentityOf(AutomationSession session)
        {
            if (session.Kind is not (AutomationClientKind.Script or AutomationClientKind.Assistant)) return session.IdentityName;
            string key = session.Who?.Key;
            return key == null || AutomationConsent.NamesOnlyAProcess(key) ? null : key;
        }

        internal const int MostPerSession = 8;

        /// <summary>The devices one plugin may add (#600 item 18).</summary>
        internal const int MostPerPlugin = 16;

        private static int MostFor(AutomationSession session) => session.IsPlugin ? MostPerPlugin : MostPerSession;
        internal const int MostInAll = 32;
        internal const int MostMadePerProgram = 64;
        internal const int MostMadeInAll = 256;

        /// <summary>How long a device of a session that ended stays online
        /// at rest, waiting for its program to come back.</summary>
        internal int OfflineAfterMs { get; set; } = 1500;

        internal sealed class Entry
        {
            public Guid Guid;
            public string Id;
            public string Kind;
            public string Program;
            public string Key;
            public ProgramDevice Device;
            public volatile AutomationSession Owner;
            public bool Registered;
            public long OfflineAtMs;

            /// <summary>A built-in plugin's device: it belongs to no
            /// session and stays on the list while its switch is on.</summary>
            public bool BuiltIn;

            /// <summary>Its script was silent long enough for it to go
            /// offline at rest (#600 item 24). It is still the script's, and
            /// comes back when the script speaks.</summary>
            public bool Asleep;

            /// <summary>Its session ended because PadForge ended it, so the
            /// wait that runs out takes it offline as PadForge's own
            /// drop.</summary>
            public bool OwnDrop;
        }

        /// <summary>Whether a device is one the session's program added (#600
        /// DESIGN-2 N3): one the session owns, or one another open session of
        /// the same program owns, by the program's consent key, never by the
        /// name it claims. A device no session owns now is no one's own until
        /// the program declares it again.</summary>
        internal bool IsOwn(Guid device, AutomationSession session)
        {
            if (session == null) return false;
            lock (_sync)
            {
                if (!_entries.TryGetValue(device, out var e) || e.BuiltIn) return false;
                var owner = e.Owner;
                if (owner == null) return false;
                if (owner == session) return true;
                string key = session.Who?.Key;
                return key != null && string.Equals(owner.Who?.Key, key, StringComparison.Ordinal);
            }
        }

        private readonly object _sync = new object();
        private readonly Dictionary<Guid, Entry> _entries = new();
        private readonly Func<long> _nowMs;
        private IAutomationEngine _engine;

        // Every device made this run, and how many of them each program
        // (IdentityOf) made, for the run's limits.
        private readonly HashSet<Guid> _made = new();
        private readonly Dictionary<(string Kind, string Identity), int> _madeBy = new();

        // How many devices this registry told the input gate are online, and
        // whether its host went.
        private int _livePublished;
        private bool _disposed;

        public ProgramDevices(Func<long> nowMs) => _nowMs = nowMs;

        /// <summary>Whether the user assigned a device to a virtual
        /// controller: a row of the device's mappings stands. A test gives
        /// its own.</summary>
        internal Func<Guid, bool> IsAssigned { get; set; } = AssignedToAController;

        /// <summary>Takes away the row of a device nobody assigned, once it
        /// went offline, where the Devices page and PadForge.xml read it. A
        /// test gives its own.</summary>
        internal Action<Guid> ForgetRow { get; set; } = guid => PadForge.Common.Input.SettingsManager.RemoveDevice(guid);

        private static bool AssignedToAController(Guid guid)
        {
            var settings = PadForge.Common.Input.SettingsManager.UserSettings;
            if (settings == null) return false;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us != null && us.InstanceGuid == guid) return true;
            }
            return false;
        }

        /// <summary>Under the lock: tells the input gate how many devices
        /// are online now (ProgramInputGate).</summary>
        private void PublishLiveLocked()
        {
            // A registry whose host went counts for nothing, whatever its
            // sessions' ends still do to it.
            if (_disposed) return;
            int live = CountLive();
            PadForge.Common.Input.ProgramInputGate.ProgramDevicesChanged(live - _livePublished);
            _livePublished = live;
        }

        /// <summary>How many devices this run made, for tests.</summary>
        internal int Made
        {
            get { lock (_sync) return _made.Count; }
        }

        /// <summary>How many entries the registry holds, online or not, for
        /// tests.</summary>
        internal int Held
        {
            get { lock (_sync) return _entries.Count; }
        }

        /// <summary>How many devices sessions own or wait for now.</summary>
        internal int Live
        {
            get
            {
                lock (_sync)
                {
                    int n = 0;
                    foreach (var e in _entries.Values) if (e.Owner != null || e.OfflineAtMs != 0) n++;
                    return n;
                }
            }
        }

        internal Entry Find(string id)
        {
            lock (_sync)
            {
                foreach (var e in _entries.Values) if (e.Id == id) return e;
                return null;
            }
        }

        /// <summary>The devices an open session of the program whose consent
        /// key is <paramref name="key"/> owns now: the ones its reach always
        /// takes (<see cref="IsOwn"/>).</summary>
        internal HashSet<Guid> OwnedByKey(string key)
        {
            var owned = new HashSet<Guid>();
            if (string.IsNullOrEmpty(key)) return owned;
            lock (_sync)
                foreach (var e in _entries.Values)
                    if (!e.BuiltIn && e.Owner is { } owner && string.Equals(owner.Who?.Key, key, StringComparison.Ordinal)) owned.Add(e.Guid);
            return owned;
        }

        internal List<Entry> OwnedBy(AutomationSession session)
        {
            lock (_sync)
            {
                var list = new List<Entry>();
                foreach (var e in _entries.Values) if (e.Owner == session) list.Add(e);
                return list;
            }
        }

        /// <summary>
        /// Adds a device or, for a key the program declared before, gives it
        /// the new shape and keeps the device and its mappings. Returns the
        /// error code, or null with <paramref name="entry"/> set. A program
        /// <see cref="IdentityOf"/> cannot name gets no device, as
        /// "unnamed-program": its session stays, and only the device is not
        /// made.
        /// </summary>
        internal string Declare(AutomationSession session, string key, string name, ProgramDeviceShape shape,
            out Entry entry, out bool known)
        {
            entry = null;
            known = false;
            string kind = session.KindWord;
            string identity = IdentityOf(session);
            if (identity == null) return "unnamed-program";
            string program = session.IdentityName;
            Guid guid = ProgramDeviceDeclaration.InstanceGuid(kind, identity, key);
            IAutomationEngine engine;
            bool register;
            lock (_sync)
            {
                if (_entries.TryGetValue(guid, out var e))
                {
                    var owner = e.Owner;
                    if (owner != null && owner != session) return "busy";
                    if (owner == null)
                    {
                        if (CountOwned(session) >= MostFor(session)) return "too-many";
                        if (e.OfflineAtMs == 0 && CountLive() >= MostInAll) return "too-many";
                    }
                    known = true;
                    e.OfflineAtMs = 0;
                    e.Asleep = false;
                    e.Owner = session;
                    e.Device.OwnerSession = session.Number;
                    entry = e;
                }
                else
                {
                    if (CountOwned(session) >= MostFor(session) || CountLive() >= MostInAll) return "too-many";
                    if (!_made.Contains(guid))
                    {
                        _madeBy.TryGetValue((kind, identity), out int byProgram);
                        if (byProgram >= MostMadePerProgram || _made.Count >= MostMadeInAll) return "too-many";
                        _made.Add(guid);
                        _madeBy[(kind, identity)] = byProgram + 1;
                    }
                    string id = AutomationDevices.IdOf(guid);
                    var device = new ProgramDevice(guid, ProgramDeviceDeclaration.ProductGuid(kind, identity, key),
                        "program://" + id, name, shape);
                    device.SetAttached(true);
                    device.OwnerSession = session.Number;
                    e = new Entry { Guid = guid, Id = id, Kind = kind, Program = program, Key = key, Device = device, Owner = session };
                    Wire(e);
                    _entries[guid] = e;
                    entry = e;
                }
                engine = _engine;
                register = engine != null;
                if (register) entry.Registered = true;
                PublishLiveLocked();
            }
            // Outside the lock: a new shape reloads the device's row, which
            // takes the device list's lock.
            if (known)
            {
                entry.Device.Reshape(name, shape);
                entry.Device.SetAttached(true);
            }
            if (register) engine.Register(entry.Device);
            return null;
        }

        private int CountOwned(AutomationSession session)
        {
            int n = 0;
            foreach (var e in _entries.Values) if (e.Owner == session) n++;
            return n;
        }

        private int CountLive()
        {
            int n = 0;
            foreach (var e in _entries.Values) if (e.Owner != null || e.OfflineAtMs != 0) n++;
            return n;
        }

        /// <summary>The device's feedback goes to whichever session owns it
        /// now. Raised on the poll thread or the interface's, and queued
        /// there without a wait.</summary>
        private static void Wire(Entry e)
        {
            string id = e.Id;
            e.Device.RumbleRequested += (low, high) =>
                e.Owner?.Feedback("e rumble " + id + " " + low + " " + high);
            e.Device.TriggerRumbleRequested += (left, right) =>
                e.Owner?.Feedback("e trigrumble " + id + " " + left + " " + right);
            e.Device.LedChanged += (r, g, b) =>
                e.Owner?.Feedback("e led " + id + " " + Hex(r, g, b));
            e.Device.PlayerChanged += n =>
                e.Owner?.Feedback("e player " + id + " " + n);
            e.Device.TriggerEffectsRequested += (left, right) =>
                e.Owner?.Feedback(TriggerEffectsLine(id, left, right));
        }

        /// <summary><c>e trigfx &lt;id&gt; &lt;left&gt; &lt;right&gt;</c>: each
        /// trigger's 11-byte effect program as 22 hex digits, its mode first
        /// (#600 item 18).</summary>
        internal static string TriggerEffectsLine(string id, byte[] left, byte[] right)
            => "e trigfx " + id + " " + Convert.ToHexString(left).ToLowerInvariant() + " " + Convert.ToHexString(right).ToLowerInvariant();

        internal static string Hex(byte r, byte g, byte b)
            => r.ToString("x2", CultureInfo.InvariantCulture) + g.ToString("x2", CultureInfo.InvariantCulture)
               + b.ToString("x2", CultureInfo.InvariantCulture);

        /// <summary>The feedback a device last took, as the event lines a
        /// program that declares it again hears first.</summary>
        internal static List<string> Replay(Entry e)
        {
            var lines = new List<string>();
            var d = e.Device;
            if (d.LastRumble is { } rumble) lines.Add("e rumble " + e.Id + " " + rumble.Low + " " + rumble.High);
            if (d.LastTriggerRumble is { } trig) lines.Add("e trigrumble " + e.Id + " " + trig.Left + " " + trig.Right);
            if (d.LastLed is { } led) lines.Add("e led " + e.Id + " " + Hex(led.R, led.G, led.B));
            if (d.LastPlayer is { } player) lines.Add("e player " + e.Id + " " + player);
            if (d.LastTriggerEffects is { } fx) lines.Add(TriggerEffectsLine(e.Id, fx.Left, fx.Right));
            return lines;
        }

        /// <summary>The session's own device by ID, or null.</summary>
        internal Entry Owned(AutomationSession session, string id, out string error)
        {
            var e = Find(id);
            if (e == null) { error = "unknown-device"; return null; }
            if (e.Owner != session) { error = "not-owner"; return null; }
            error = null;
            return e;
        }

        /// <summary>Removes a device at its program's word: at rest and
        /// offline at once.</summary>
        internal string Gone(AutomationSession session, string id)
        {
            var e = Owned(session, id, out string error);
            if (e == null) return error;
            TakeOffline(new List<Entry> { e });
            return null;
        }

        /// <summary>A session ended. Its devices rest now and go offline
        /// after the wait, or at once when <paramref name="now"/> is set, as
        /// for a level that no longer allows them. <paramref name="own"/>
        /// says PadForge ended it (a plugin it stopped or restarted,
        /// Automation turned off or a level lowered), so the connection notes
        /// say nothing of the drop.</summary>
        internal void SessionEnded(AutomationSession session, bool now, bool own = false)
        {
            List<Entry> offline = null;
            lock (_sync)
            {
                foreach (var e in _entries.Values)
                {
                    if (e.Owner != session) continue;
                    e.Device.Rest();
                    e.Owner = null;
                    e.OwnDrop = own;
                    if (now) (offline ??= new List<Entry>()).Add(e);
                    else e.OfflineAtMs = _nowMs() + OfflineAfterMs;
                }
                PublishLiveLocked();
            }
            if (offline != null) TakeOffline(offline);
        }

        /// <summary>A session stopped answering: its devices rest, and stay
        /// online for it to drive again.</summary>
        internal void Rest(AutomationSession session)
        {
            foreach (var e in OwnedBy(session)) e.Device.Rest();
        }

        /// <summary>A script or a plugin was silent too long (#600 items 18
        /// and 24): its devices go offline at rest, through the path that
        /// rests their mapped outputs, and stay its own.</summary>
        internal void Sleep(AutomationSession session)
        {
            IAutomationEngine engine;
            var unregister = new List<ProgramDevice>();
            lock (_sync)
            {
                engine = _engine;
                foreach (var e in _entries.Values)
                {
                    if (e.Owner != session || e.Asleep) continue;
                    e.Asleep = true;
                    e.Device.Rest();
                    e.Device.SetAttached(false);
                    if (e.Registered)
                    {
                        e.Registered = false;
                        unregister.Add(e.Device);
                    }
                }
            }
            if (engine != null) foreach (var d in unregister) engine.Unregister(d);
        }

        /// <summary>The script or the plugin spoke again: its devices are
        /// back online.</summary>
        internal void Wake(AutomationSession session)
        {
            IAutomationEngine engine;
            var register = new List<ProgramDevice>();
            lock (_sync)
            {
                engine = _engine;
                foreach (var e in _entries.Values)
                {
                    if (e.Owner != session || !e.Asleep) continue;
                    e.Asleep = false;
                    e.Device.SetAttached(true);
                    if (engine != null)
                    {
                        e.Registered = true;
                        register.Add(e.Device);
                    }
                }
            }
            foreach (var d in register) engine.Register(d);
        }

        /// <summary>Takes devices offline. A device PadForge takes away
        /// itself (<paramref name="own"/>, or its session ended by PadForge)
        /// is stamped first, so its connection note says nothing
        /// (OwnDrops).</summary>
        private void TakeOffline(List<Entry> entries, bool own = false)
        {
            IAutomationEngine engine;
            var unregister = new List<ProgramDevice>();
            lock (_sync)
            {
                engine = _engine;
                foreach (var e in entries)
                {
                    if (own || (e.OwnDrop && e.Owner == null)) PadForge.Engine.OwnDrops.Row(e.Guid);
                    e.OwnDrop = false;
                    e.Device.Rest();
                    e.Device.SetAttached(false);
                    e.Owner = null;
                    e.OfflineAtMs = 0;
                    e.Asleep = false;
                    if (e.Registered)
                    {
                        e.Registered = false;
                        unregister.Add(e.Device);
                    }
                }
                PublishLiveLocked();
            }
            if (engine != null) foreach (var d in unregister) engine.Unregister(d);

            // A device nobody assigned goes, row and all, unless its program
            // declared it again in the meantime.
            List<Guid> forget = null;
            foreach (var e in entries)
            {
                bool assigned;
                try { assigned = IsAssigned?.Invoke(e.Guid) ?? true; }
                catch { assigned = true; }
                if (assigned) continue;
                lock (_sync)
                {
                    if (e.Owner != null || e.OfflineAtMs != 0 || e.Registered) continue;
                    if (!_entries.TryGetValue(e.Guid, out var held) || !ReferenceEquals(held, e)) continue;
                    _entries.Remove(e.Guid);
                }
                (forget ??= new List<Guid>()).Add(e.Guid);
            }
            if (forget == null) return;
            foreach (var guid in forget)
            {
                try { ForgetRow?.Invoke(guid); }
                catch { /* the entry is gone either way, and the row goes at the next start */ }
            }
            Forgotten?.Invoke();
        }

        /// <summary>Raised after rows of devices nobody assigned were taken
        /// away, on the thread that took them, so the Devices page reads its
        /// list again.</summary>
        internal event Action Forgotten;

        /// <summary>The registry goes with its host: the input gate no
        /// longer counts its devices.</summary>
        internal void Dispose()
        {
            lock (_sync)
            {
                PadForge.Common.Input.ProgramInputGate.ProgramDevicesChanged(-_livePublished);
                _livePublished = 0;
                _disposed = true;
            }
        }

        /// <summary>Takes offline the devices whose wait ran out. Called by
        /// the sessions' clock.</summary>
        internal void Tick()
        {
            List<Entry> due = null;
            long now = _nowMs();
            lock (_sync)
            {
                foreach (var e in _entries.Values)
                {
                    if (e.Owner == null && e.OfflineAtMs != 0 && now >= e.OfflineAtMs) (due ??= new List<Entry>()).Add(e);
                    // A timed step of a device nothing reads lands on the
                    // clock (#600 item 22).
                    e.Device.LandDue();
                }
            }
            if (due != null) TakeOffline(due);
        }

        /// <summary>The kind a built-in plugin's device's identity is made
        /// with.</summary>
        internal const string BuiltInKind = "builtin";

        /// <summary>
        /// Puts a built-in plugin's device on the list, or gives the one it
        /// had the new name and shape: the Test Controller and each device
        /// Replay plays. It belongs to no session, counts toward no
        /// program's limit, and stays until <see cref="RemoveBuiltIn"/>.
        /// Its identity is made from <paramref name="program"/> and
        /// <paramref name="key"/>, so its mappings survive a restart.
        /// </summary>
        /// <param name="productName">The name the row keeps as its product's,
        /// in no language (ProgramDevice.ProductName). Null keeps
        /// <paramref name="name"/>.</param>
        /// <param name="pathKind">A word the device's path carries after the
        /// scheme, <c>program://kind/id</c>, so its row is known for what it
        /// is in a later run. Null for <c>program://id</c>.</param>
        /// <param name="live">The state worked out on the poll thread
        /// (ProgramDevice.Live), or null.</param>
        /// <param name="livePeek">The same for a reader off the poll thread
        /// (ProgramDevice.LivePeek), or null. Both are set before the
        /// engine lists the device.</param>
        internal Entry DeclareBuiltIn(string program, string key, string name, ProgramDeviceShape shape,
            string productName = null, string pathKind = null,
            Func<CustomInputState> live = null, Func<CustomInputState> livePeek = null)
        {
            const string kind = BuiltInKind;
            Guid guid = ProgramDeviceDeclaration.InstanceGuid(kind, program, key);
            IAutomationEngine engine;
            Entry entry;
            bool known;
            lock (_sync)
            {
                known = _entries.TryGetValue(guid, out entry);
                if (!known)
                {
                    string id = AutomationDevices.IdOf(guid);
                    var device = new ProgramDevice(guid, ProgramDeviceDeclaration.ProductGuid(kind, program, key),
                        "program://" + (pathKind == null ? id : pathKind + "/" + id), name, shape);
                    entry = new Entry { Guid = guid, Id = id, Kind = kind, Program = program, Key = key, Device = device };
                    Wire(entry);
                    _entries[guid] = entry;
                }
                entry.Device.ProductName = productName;
                entry.Device.PadForgeOwn = true;
                entry.Device.Live = live;
                entry.Device.LivePeek = livePeek;
                entry.BuiltIn = true;
                entry.OfflineAtMs = 0;
                engine = _engine;
                if (engine != null) entry.Registered = true;
            }
            // Outside the lock, as Declare's reshape is.
            if (known) entry.Device.Reshape(name, shape);
            entry.Device.SetAttached(true);
            if (engine != null) engine.Register(entry.Device);
            return entry;
        }

        /// <summary>Takes a built-in plugin's device offline at once, its
        /// mapped outputs at rest. Its row stays on the Devices page when the
        /// device is assigned to a virtual controller, as any disconnected
        /// device's does, and goes, row and all, when it is not.</summary>
        internal void RemoveBuiltIn(Entry entry)
        {
            if (entry == null) return;
            lock (_sync)
            {
                if (!entry.BuiltIn) return;
                entry.BuiltIn = false;
            }
            // Only PadForge turns a built-in plugin's device off: its switch,
            // its count of controllers, or Plugins turned off.
            TakeOffline(new List<Entry> { entry }, own: true);
        }

        /// <summary>The engine started: every device a program drives, or
        /// waits to come back to, and every built-in plugin's device that is
        /// on goes on the list.</summary>
        internal void Attach(IAutomationEngine engine)
        {
            var register = new List<ProgramDevice>();
            lock (_sync)
            {
                _engine = engine;
                foreach (var e in _entries.Values)
                {
                    if (e.Owner == null && e.OfflineAtMs == 0 && !e.BuiltIn) continue;
                    if (e.Asleep) continue;
                    e.Registered = true;
                    // The engine that stopped disposed the device with its
                    // list. It is the program's still.
                    e.Device.SetAttached(true);
                    register.Add(e.Device);
                }
            }
            foreach (var d in register) engine.Register(d);
        }

        /// <summary>The engine stopped, and its device list with it.</summary>
        internal void Detach()
        {
            lock (_sync)
            {
                _engine = null;
                foreach (var e in _entries.Values) e.Registered = false;
            }
        }

        internal IAutomationEngine Engine
        {
            get { lock (_sync) return _engine; }
        }

        /// <summary>Every entry, for the device list while the engine is
        /// stopped.</summary>
        internal List<Entry> All()
        {
            lock (_sync) return new List<Entry>(_entries.Values);
        }
    }
}
