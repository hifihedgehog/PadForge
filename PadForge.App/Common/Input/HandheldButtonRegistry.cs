using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Persisted registry of learned handheld hidden buttons (issue #343),
    /// the NFC tag registry's shape applied to a machine's own buttons.
    ///
    /// <para>One entry is one physical button the user pressed in the Learn
    /// dialog. It carries whichever delivery paths the press produced: a
    /// keyboard chord (the firmware typed keys), a vendor HID report field
    /// (a bit or a code in a vendor collection's input report), or both,
    /// since some firmwares do both for one button (the Legion Go's Desktop
    /// button sets a report bit AND types Win+D). Both paths assert the same
    /// STABLE raw button on the handheld device row, assigned once at
    /// registration and never renumbered, so saved bindings survive later
    /// additions and removals.</para>
    /// </summary>
    public static class HandheldButtonRegistry
    {
        public sealed class Entry
        {
            public string Name { get; set; }
            /// <summary>Stable raw-button index on the handheld device row,
            /// or -1 before registration assigns one.</summary>
            public int Button { get; set; } = -1;
            /// <summary>Chord codes (VK codes, or MouseCode + button id), or
            /// null when the button has no keyboard delivery.</summary>
            public int[] Keys { get; set; }
            /// <summary>Vendor collection key (VID:PID:PAGE:USAGE), or null
            /// when the button has no report delivery.</summary>
            public string Collection { get; set; }
            public byte ReportId { get; set; }
            public int ByteIndex { get; set; }
            public byte Mask { get; set; }
            public byte Value { get; set; }
            public VendorButtonKind ValueKind { get; set; }
            /// <summary>WMI event delivery (a firmware key the vendor's
            /// ACPI-WMI provider reports): the root\WMI event class, the
            /// data property, and its value as invariant text. Null class
            /// when the button has no WMI delivery.</summary>
            public string WmiClass { get; set; }
            public string WmiProperty { get; set; }
            public string WmiValue { get; set; }

            public bool HasChord => Keys != null && Keys.Length > 0;
            public bool HasReport => !string.IsNullOrEmpty(Collection);
            public bool HasWmi => !string.IsNullOrEmpty(WmiClass) && !string.IsNullOrEmpty(WmiProperty);
            public bool HasAnyPath => HasChord || HasReport || HasWmi;

            public Entry Clone() => new Entry
            {
                Name = Name, Button = Button, Keys = Keys == null ? null : (int[])Keys.Clone(),
                Collection = Collection, ReportId = ReportId, ByteIndex = ByteIndex,
                Mask = Mask, Value = Value, ValueKind = ValueKind,
                WmiClass = WmiClass, WmiProperty = WmiProperty, WmiValue = WmiValue,
            };

            public HandheldChordDefinition ToChord() => new HandheldChordDefinition
            {
                Name = Name, Keys = (int[])Keys.Clone(), Button = Button,
            };

            public VendorButtonDefinition ToReport() => new VendorButtonDefinition
            {
                Name = Name, Button = Button, ReportId = ReportId, ByteIndex = ByteIndex,
                Mask = Mask, Value = Value, Kind = ValueKind,
            };
        }

        // Buttons live in CustomInputState.Buttons[256], all of them usable
        // here since there is no "any" button on this row.
        private const int MaxButton = CustomInputState.MaxButtons - 1;

        private static readonly object _lock = new();
        private static readonly List<Entry> _entries = new();

        /// <summary>Raised when the registry changes (picker and preview refresh).</summary>
        public static event EventHandler RegistryChanged;

        /// <summary>Raised when <see cref="LearnCaptureActive"/> or
        /// <see cref="FeatureEnabled"/> flips, so the hook host re-evaluates
        /// whether the keyboard and mouse hooks must stay installed.</summary>
        public static event EventHandler ActivityChanged;

        private static volatile bool _learnCaptureActive;
        private static volatile bool _featureEnabled;

        /// <summary>True while a Learn dialog is open. The device opens
        /// EVERY vendor collection during a capture (the press could be in
        /// any of them), and only the collections a definition names
        /// otherwise, which is what keeps the feature free when unused.</summary>
        public static bool LearnCaptureActive
        {
            get => _learnCaptureActive;
            set
            {
                if (_learnCaptureActive == value) return;
                _learnCaptureActive = value;
                ActivityChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>The Settings toggle. Off by default: no device row, no
        /// hooks, no vendor handles, no sensor subscription on a machine
        /// whose owner never asked for any of it.</summary>
        public static bool FeatureEnabled
        {
            get => _featureEnabled;
            set
            {
                if (_featureEnabled == value) return;
                _featureEnabled = value;
                ActivityChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>Raises <see cref="ActivityChanged"/> for a change the
        /// flags do not carry: the device row opening (its chords reach the
        /// engine then, which is what makes the hooks necessary) or closing.</summary>
        internal static void NotifyActivity() => ActivityChanged?.Invoke(null, EventArgs.Empty);

        /// <summary>DMI key of the machine the stored definitions were
        /// learned on, for display and export. Definitions still apply on a
        /// different machine (two handhelds of one model share them).</summary>
        public static string MachineKey { get; private set; } = string.Empty;

        /// <summary>Snapshot of the entries ordered by their stable button.</summary>
        public static IReadOnlyList<Entry> Entries
        {
            get { lock (_lock) return _entries.OrderBy(e => e.Button).Select(e => e.Clone()).ToList(); }
        }

        public static int Count { get { lock (_lock) return _entries.Count; } }

        /// <summary>Highest button in use, or -1 when empty, so the device
        /// row can span every assigned button (gaps included).</summary>
        public static int MaxButtonInUse { get { lock (_lock) return _entries.Count == 0 ? -1 : _entries.Max(e => e.Button); } }

        /// <summary>True when any entry carries a chord, so the hooks are needed.</summary>
        public static bool HasChords { get { lock (_lock) return _entries.Any(e => e.HasChord); } }

        /// <summary>Chord definitions for the engine.</summary>
        public static List<HandheldChordDefinition> Chords
        {
            get { lock (_lock) return _entries.Where(e => e.HasChord).Select(e => e.ToChord()).ToList(); }
        }

        /// <summary>Vendor collections a definition names. The device keeps
        /// exactly these open outside a capture.</summary>
        public static HashSet<string> RequiredCollections
        {
            get
            {
                lock (_lock)
                    return new HashSet<string>(_entries.Where(e => e.HasReport).Select(e => e.Collection),
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>WMI event classes a definition names. Subscribed
        /// outside a capture; everything else only during one.</summary>
        public static HashSet<string> RequiredWmiClasses
        {
            get
            {
                lock (_lock)
                    return new HashSet<string>(_entries.Where(e => e.HasWmi).Select(e => e.WmiClass),
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        // Caller holds _lock.
        private static int LowestFreeButton()
        {
            for (int b = 0; b <= MaxButton; b++)
                if (!_entries.Any(e => e.Button == b))
                    return b;
            return -1;
        }

        // Caller holds _lock.
        private static string DedupeName(string name, Entry self)
        {
            string baseName = string.IsNullOrWhiteSpace(name) ? "Button" : name.Trim();
            string final = baseName;
            int k = 2;
            while (_entries.Any(e => !ReferenceEquals(e, self)
                    && string.Equals(e.Name, final, StringComparison.OrdinalIgnoreCase)))
                final = $"{baseName} ({k++})";
            return final;
        }

        /// <summary>Adds a learned button. A stored button index that is
        /// free is honored (import); otherwise the lowest free one is
        /// assigned. Returns the stored copy, or null when the entry carries
        /// no delivery path or the registry is full.</summary>
        public static Entry Register(Entry entry)
        {
            if (entry == null || !entry.HasAnyPath) return null;
            Entry stored;
            lock (_lock)
            {
                int button = entry.Button;
                if (button < 0 || button > MaxButton || _entries.Any(e => e.Button == button))
                    button = LowestFreeButton();
                if (button < 0) return null;
                stored = entry.Clone();
                stored.Button = button;
                stored.Name = DedupeName(entry.Name, null);
                _entries.Add(stored);
                stored = stored.Clone();
            }
            // Raise OUTSIDE the lock (NfcTagRegistry's rule).
            RegistryChanged?.Invoke(null, EventArgs.Empty);
            return stored;
        }

        public static void Rename(int button, string name)
        {
            bool changed = false;
            lock (_lock)
            {
                var e = _entries.FirstOrDefault(x => x.Button == button);
                if (e != null)
                {
                    string final = DedupeName(name, e);
                    changed = !string.Equals(e.Name, final, StringComparison.Ordinal);
                    e.Name = final;
                }
            }
            if (changed) RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void Remove(int button)
        {
            bool removed;
            lock (_lock) removed = _entries.RemoveAll(e => e.Button == button) > 0;
            if (removed) RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Replaces the registry from persisted settings. Stored
        /// buttons are honored so bindings stay valid across restarts; an
        /// absent, out-of-range, or colliding button (hand edits) gets the
        /// lowest free one.</summary>
        public static void LoadRegistry(IEnumerable<Entry> entries, string machineKey)
        {
            lock (_lock)
            {
                _entries.Clear();
                MachineKey = machineKey ?? string.Empty;
                if (entries != null)
                    foreach (var src in entries)
                    {
                        if (src == null || !src.HasAnyPath) continue;
                        var e = src.Clone();
                        if (e.Button < 0 || e.Button > MaxButton || _entries.Any(x => x.Button == e.Button))
                        {
                            e.Button = LowestFreeButton();
                            if (e.Button < 0) break;
                        }
                        e.Name = DedupeName(e.Name, null);
                        _entries.Add(e);
                    }
            }
            RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Serializable view for the settings round-trip.</summary>
        public static List<Entry> SaveRegistry()
        {
            lock (_lock) return _entries.OrderBy(e => e.Button).Select(e => e.Clone()).ToList();
        }

        /// <summary>Stamps the current machine as the definitions' origin
        /// when the first button is learned here.</summary>
        public static void StampMachine(string machineKey)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(MachineKey)) MachineKey = machineKey ?? string.Empty;
            }
        }
    }
}
