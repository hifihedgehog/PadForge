using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PadForge.Services.Automation;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// One plugin's declared settings and their values (#600 item 17): what
    /// it declared in its present run, the values the user set for it and
    /// for each of its devices, and the status lines it keeps current. The
    /// values outlive a run and are kept in PadForge.xml, only those that
    /// differ from their default. The declarations last for the run: a
    /// plugin declares them again when it starts.
    ///
    /// <para>Limits: <see cref="PluginDeclarations.MostSettings"/>
    /// declarations, <see cref="PluginDeclarations.LongestValue"/> bytes a
    /// value, <see cref="PluginDeclarations.MostValueBytes"/> bytes of kept
    /// values in all.</para>
    ///
    /// <para>Safe from any thread. <see cref="Changed"/> is raised outside
    /// the lock, on the thread that made the change. A value the user
    /// changes reaches the plugin through <see cref="Send"/> at once, before
    /// anything PadForge sends it after.</para>
    /// </summary>
    internal sealed class PluginSettingSet
    {
        private readonly object _sync = new();
        private readonly List<PluginDeclaration> _declarations = new();
        private readonly Dictionary<(string Device, string Key), string> _values = new();
        private readonly Dictionary<(string Device, string Key), string> _status = new();
        private int _order;
        private string _refused;
        private volatile Action<string> _send;

        /// <summary>Raised when a declaration or a value changed.</summary>
        public event Action Changed;

        /// <summary>Raised when the user changed a value, with its device
        /// (or null) and key. The built-ins read their settings here.</summary>
        public event Action<string, string> ValueChanged;

        /// <summary>Raised when a value PadForge.xml keeps changed.</summary>
        public event Action KeptChanged;

        /// <summary>Where lines for the plugin go: its session's outbox, set
        /// when it declares. Null while no run listens.</summary>
        public Action<string> Send
        {
            get => _send;
            set => _send = value;
        }

        /// <summary>Whether a run declared anything.</summary>
        public bool HasDeclarations
        {
            get { lock (_sync) return _declarations.Count > 0; }
        }

        /// <summary>Why the last declaration was refused, in the protocol's
        /// words, or null.</summary>
        public string Refused
        {
            get { lock (_sync) return _refused; }
        }

        /// <summary>The declarations, in the order given.</summary>
        public List<PluginDeclaration> Declarations()
        {
            lock (_sync) return new List<PluginDeclaration>(_declarations);
        }

        /// <summary>A declaration by its device and key, or null.</summary>
        public PluginDeclaration Find(string device, string key)
        {
            lock (_sync) return FindLocked(device, key);
        }

        private PluginDeclaration FindLocked(string device, string key)
        {
            foreach (var d in _declarations)
                if (d.Key == key && d.Device == device) return d;
            return null;
        }

        /// <summary>
        /// Takes a declaration, in place of one with the same device and key.
        /// Gives the error code and words, or null with the value in force.
        /// </summary>
        public (string Code, string Words) Declare(PluginDeclaration declaration, out string value)
        {
            value = null;
            bool keptMoved = false;
            if (declaration == null) return ("bad-argument", "no declaration");
            lock (_sync)
            {
                var old = FindLocked(declaration.Device, declaration.Key);
                if (old == null && _declarations.Count >= PluginDeclarations.MostSettings)
                {
                    _refused = "a plugin declares up to 64 settings";
                    return ("too-many", _refused);
                }
                if (old != null)
                {
                    declaration.Order = old.Order;
                    _declarations[_declarations.IndexOf(old)] = declaration;
                }
                else
                {
                    declaration.Order = _order++;
                    _declarations.Add(declaration);
                }
                _refused = null;
                // A kept value the new declaration no longer takes, such as
                // a choice it dropped or a number out of its new range, gives
                // way to the default. One that is the new default is no
                // longer kept. Either way PadForge.xml loses it.
                var k = (declaration.Device, declaration.Key);
                if (_values.TryGetValue(k, out var kept))
                {
                    string fits = declaration.HoldsValue ? PluginValues.Normalize(declaration, kept, out _) : null;
                    if (fits == null || fits == declaration.Default)
                    {
                        _values.Remove(k);
                        keptMoved = true;
                    }
                    else
                    {
                        _values[k] = fits;
                        keptMoved = fits != kept;
                    }
                }
                value = ValueLocked(declaration);
            }
            Changed?.Invoke();
            if (keptMoved) KeptChanged?.Invoke();
            return (null, null);
        }

        /// <summary>A refused declaration, which the card names.</summary>
        public void Refuse(string words)
        {
            lock (_sync) _refused = words;
            Changed?.Invoke();
        }

        /// <summary>The plugin's run ended: its declarations and status lines
        /// go. The values stay.</summary>
        public void EndRun()
        {
            bool had;
            lock (_sync)
            {
                had = _declarations.Count > 0 || _status.Count > 0;
                _declarations.Clear();
                _status.Clear();
                _order = 0;
            }
            _send = null;
            if (had) Changed?.Invoke();
        }

        /// <summary>The value in force: the kept one, the status line, or
        /// the default.</summary>
        public string ValueOf(string device, string key)
        {
            lock (_sync)
            {
                var d = FindLocked(device, key);
                if (d != null) return ValueLocked(d);
                return _values.TryGetValue((device, key), out var v) ? v : null;
            }
        }

        private string ValueLocked(PluginDeclaration d)
        {
            if (d.Kind == PluginSettingKind.Status)
                return _status.TryGetValue((d.Device, d.Key), out var s) ? s : d.Default;
            if (!d.HoldsValue) return null;
            return _values.TryGetValue((d.Device, d.Key), out var v) ? v : d.Default;
        }

        /// <summary>
        /// The user's change, or an assistant's through the catalog: checked
        /// against the declaration, kept, and sent to the plugin as
        /// <c>e setting</c>. Gives the error words, or null.
        /// </summary>
        public string Set(string device, string key, string value) => Put(device, key, value, fromPlugin: false);

        /// <summary>The plugin's own <c>setting key=... value=...</c>: a
        /// status line, or a value it changed itself, which is kept and not
        /// sent back.</summary>
        public string PluginSet(string device, string key, string value) => Put(device, key, value, fromPlugin: true);

        private string Put(string device, string key, string value, bool fromPlugin)
        {
            string line = null;
            bool kept = false;
            lock (_sync)
            {
                var d = FindLocked(device, key);
                if (d == null) return "no setting " + key + (device != null ? " for device " + device : string.Empty) + " is declared";
                if (d.Kind == PluginSettingKind.Status)
                {
                    if (!fromPlugin) return d.Name + " is read only";
                    string status = ProgramDeviceDeclaration.Clean(value, PluginDeclarations.LongestValue);
                    if (_status.TryGetValue((device, key), out var was) && was == status) return null;
                    _status[(device, key)] = status;
                }
                else
                {
                    if (!d.HoldsValue) return d.Name + " holds no value";
                    string normalized = PluginValues.Normalize(d, value, out string error);
                    if (normalized == null) return error;
                    var k = (device, key);
                    string before = _values.TryGetValue(k, out var v) ? v : d.Default;
                    if (before == normalized) return null;
                    if (normalized == d.Default) _values.Remove(k);
                    else
                    {
                        int bytes = Encoding.UTF8.GetByteCount(normalized) - (v != null ? Encoding.UTF8.GetByteCount(v) : 0);
                        if (KeptBytesLocked() + bytes > PluginDeclarations.MostValueBytes) return "a plugin keeps up to 16 KB of values";
                        _values[k] = normalized;
                    }
                    if (!fromPlugin) line = SettingLine(device, key, normalized);
                    kept = true;
                }
            }
            if (line != null) Post(line);
            Changed?.Invoke();
            if (kept) KeptChanged?.Invoke();
            if (!fromPlugin) ValueChanged?.Invoke(device, key);
            return null;
        }

        private int KeptBytesLocked()
        {
            int n = 0;
            foreach (var v in _values.Values) n += Encoding.UTF8.GetByteCount(v);
            return n;
        }

        /// <summary>The plugin keeps a choice's list current.</summary>
        public string SetChoices(string device, string key, JsonElement list)
        {
            var choices = new List<PluginChoice>();
            if (!PluginDeclarations.ReadChoices(list, choices, out string error)) return error;
            lock (_sync)
            {
                var d = FindLocked(device, key);
                if (d == null || d.Kind != PluginSettingKind.Choice) return "no choice " + key + " is declared";
                d.Choices = choices;
            }
            Changed?.Invoke();
            return null;
        }

        /// <summary>A button pressed: <c>e press</c> to the plugin.</summary>
        public string Press(string device, string key)
        {
            lock (_sync)
            {
                var d = FindLocked(device, key);
                if (d == null || d.Kind != PluginSettingKind.Button) return "no button " + key + " is declared";
            }
            Post("e press key=" + AutomationLine.Word(key) + DeviceWord(device));
            ValueChanged?.Invoke(device, key);
            return null;
        }

        /// <summary>A choice's list opened: <c>e choices</c> asks the plugin
        /// for the list as it is now.</summary>
        public void AskChoices(string device, string key) => Post("e choices key=" + AutomationLine.Word(key) + DeviceWord(device));

        /// <summary>A setting back to its default.</summary>
        public void Reset(string device, string key)
        {
            string line = null;
            lock (_sync)
            {
                var d = FindLocked(device, key);
                if (!_values.Remove((device, key))) return;
                if (d != null) line = SettingLine(device, key, d.Default);
            }
            if (line != null) Post(line);
            Changed?.Invoke();
            KeptChanged?.Invoke();
            ValueChanged?.Invoke(device, key);
        }

        /// <summary>Every setting of the plugin itself (<paramref name="device"/>
        /// null), or of one of its devices, back to its default.</summary>
        public void ResetAll(string device)
        {
            var lines = new List<string>();
            var keys = new List<string>();
            lock (_sync)
            {
                foreach (var k in new List<(string Device, string Key)>(_values.Keys))
                {
                    if (k.Device != device) continue;
                    _values.Remove(k);
                    keys.Add(k.Key);
                    var d = FindLocked(k.Device, k.Key);
                    if (d != null) lines.Add(SettingLine(device, k.Key, d.Default));
                }
            }
            if (keys.Count == 0) return;
            foreach (var line in lines) Post(line);
            Changed?.Invoke();
            KeptChanged?.Invoke();
            foreach (var key in keys) ValueChanged?.Invoke(device, key);
        }

        private static string SettingLine(string device, string key, string value)
            => "e setting key=" + AutomationLine.Word(key) + " value=" + AutomationLine.Word(value ?? string.Empty) + DeviceWord(device);

        private static string DeviceWord(string device) => device != null ? " device=" + AutomationLine.Word(device) : string.Empty;

        private void Post(string line)
        {
            try { _send?.Invoke(line); }
            catch { /* the run went away */ }
        }

        // ── PadForge.xml ──

        /// <summary>Takes the values PadForge.xml holds, in place of those
        /// there were. Values over the limits are dropped.</summary>
        public void Load(IEnumerable<PluginValueData> saved)
        {
            lock (_sync)
            {
                _values.Clear();
                int bytes = 0;
                if (saved != null)
                    foreach (var v in saved)
                    {
                        if (v == null || !ProgramDeviceDeclaration.IsKey(v.Key)) continue;
                        string device = string.IsNullOrEmpty(v.Device) ? null : v.Device;
                        if (device != null && !ProgramDeviceDeclaration.IsKey(device)) continue;
                        string value = v.Value ?? string.Empty;
                        int size = Encoding.UTF8.GetByteCount(value);
                        if (size > PluginDeclarations.LongestValue || bytes + size > PluginDeclarations.MostValueBytes) continue;
                        if (_values.Count >= PluginDeclarations.MostSettings * 17) break;
                        bytes += size;
                        _values[(device, v.Key)] = value;
                    }
            }
            Changed?.Invoke();
        }

        /// <summary>The values as PadForge.xml keeps them, a device's until
        /// <see cref="DropDevice"/>. Null when there are none.</summary>
        public PluginValueData[] Save()
        {
            lock (_sync)
            {
                if (_values.Count == 0) return null;
                var list = new List<PluginValueData>(_values.Count);
                foreach (var ((device, key), value) in _values)
                {
                    list.Add(new PluginValueData { Device = device, Key = key, Value = value });
                }
                list.Sort((a, b) => string.CompareOrdinal((a.Device ?? string.Empty) + "\n" + a.Key, (b.Device ?? string.Empty) + "\n" + b.Key));
                return list.Count == 0 ? null : list.ToArray();
            }
        }

        /// <summary>The keys of the devices that have settings declared, and
        /// with <paramref name="withValues"/> those with values kept too.</summary>
        public HashSet<string> Devices(bool withValues = false)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            lock (_sync)
            {
                foreach (var d in _declarations) if (d.Device != null) set.Add(d.Device);
                if (withValues)
                    foreach (var (device, _) in _values.Keys) if (device != null) set.Add(device);
            }
            return set;
        }

        /// <summary>A device is gone for good: the user removed its row, or
        /// the plugin said <c>setting clear device=&lt;key&gt;</c>. Its
        /// declarations and its values go.</summary>
        public void DropDevice(string device)
        {
            if (device == null) return;
            bool had;
            lock (_sync)
            {
                int before = _values.Count + _declarations.Count;
                _declarations.RemoveAll(d => d.Device == device);
                foreach (var k in new List<(string Device, string Key)>(_values.Keys))
                    if (k.Device == device) _values.Remove(k);
                foreach (var k in new List<(string Device, string Key)>(_status.Keys))
                    if (k.Device == device) _status.Remove(k);
                had = _values.Count + _declarations.Count != before;
            }
            if (!had) return;
            Changed?.Invoke();
            KeptChanged?.Invoke();
        }
    }

    /// <summary>
    /// The plugins' verb on the Automation pipe (#600 item 17):
    /// <c>setting</c>. A plugin's grant is tied to its settings when its key
    /// is accepted, so the line finds them with no table of its own in the
    /// sessions.
    /// <list type="bullet">
    /// <item><c>setting key=port kind=number name="Port" min=1 max=65535 {...}</c> declares, and is answered with <c>ok key=port value=...</c>.</item>
    /// <item><c>setting key=rate value=998</c> sets a status line, or a value the plugin changed itself.</item>
    /// <item><c>setting key=port {"choices":[...]}</c> keeps a choice's list current.</item>
    /// <item><c>setting clear</c> takes back every declaration of the run.</item>
    /// </list>
    /// Each takes <c>device=&lt;key&gt;</c> for a device's own setting.
    /// </summary>
    internal static class PluginLines
    {
        private static readonly ConditionalWeakTable<AutomationPluginGrant, PluginSettingSet> Sets = new();

        /// <summary>Ties a plugin's grant to its settings.</summary>
        internal static void Bind(AutomationPluginGrant grant, PluginSettingSet set)
        {
            if (grant == null || set == null) return;
            Sets.AddOrUpdate(grant, set);
        }

        /// <summary>Runs a <c>setting</c> line of a plugin's session. False
        /// when the session holds no plugin's grant PadForge tied, which is
        /// answered as an unknown verb.</summary>
        internal static bool Run(AutomationSession session, AutomationRequest r)
        {
            var grant = session?.Grant;
            if (grant == null || !Sets.TryGetValue(grant, out var set)) return false;
            set.Send = line => session.Feedback(line);
            session.Answer(r.Tag, Answer(set, r));
            return true;
        }

        /// <summary>The answer to one line, run against
        /// <paramref name="set"/>.</summary>
        internal static string Answer(PluginSettingSet set, AutomationRequest r)
        {
            var bare = r.Bare();
            if (bare.Count == 1 && bare[0] == "clear")
            {
                // With device=, that device is gone for good. Alone, the run's
                // declarations go and the values stay.
                if (r.Get("device") is { } gone)
                {
                    if (!ProgramDeviceDeclaration.IsKey(gone)) return AutomationLine.Error("bad-argument", "device is a device's key");
                    set.DropDevice(gone);
                }
                else set.EndRun();
                return "ok";
            }
            string key = r.Get("key");
            string device = r.Get("device");
            if (r.Get("kind") != null)
            {
                var declaration = PluginDeclarations.Parse(r, out string error);
                if (declaration == null)
                {
                    set.Refuse(error);
                    return AutomationLine.Error("bad-argument", error);
                }
                var (code, words) = set.Declare(declaration, out string value);
                if (code != null) return AutomationLine.Error(code, words);
                return "ok key=" + AutomationLine.Word(declaration.Key)
                       + (value != null ? " value=" + AutomationLine.Word(value) : string.Empty)
                       + (declaration.Device != null ? " device=" + AutomationLine.Word(declaration.Device) : string.Empty);
            }
            if (!ProgramDeviceDeclaration.IsKey(key)) return AutomationLine.Error("bad-argument", "setting takes key= and kind=, value= or a choices object");
            string given = r.Get("value");
            if (given != null)
            {
                string error = set.PluginSet(device, key, given);
                return error == null ? "ok" : AutomationLine.Error(error.StartsWith("a plugin keeps", StringComparison.Ordinal) ? "too-long" : "bad-argument", error);
            }
            if (!string.IsNullOrWhiteSpace(r.Json))
            {
                try
                {
                    using var doc = JsonDocument.Parse(r.Json, new JsonDocumentOptions { MaxDepth = 4 });
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("choices", out var list))
                    {
                        string error = set.SetChoices(device, key, list);
                        return error == null ? "ok" : AutomationLine.Error("bad-argument", error);
                    }
                }
                catch (JsonException) { return AutomationLine.Error("bad-argument", "the object is not JSON"); }
            }
            return AutomationLine.Error("bad-argument", "setting takes key= and kind=, value= or a choices object");
        }
    }
}
