using System;
using System.Collections.Generic;
using PadForge.Common.Input;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>
    /// The Roccat Kone EMP (1E7D:2E24), which only libratbag lights
    /// (src/driver-roccat-kone-emp.c, #557).
    ///
    /// <para>The mouse has no way to take a color but its profile's settings, report 06.
    /// A write reads the active profile from report 05 (roccat_current_profile, lines
    /// 425-439), asks for that profile's settings by writing 04 with the profile and 80
    /// (roccat_set_config_profile, lines 484-505), waits for report 04 to say ready
    /// (roccat_is_ready, lines 378-396), and reads report 06. Each of the four LEDs takes
    /// color index 1E for a color of its own, the status FF, no color flow and the fixed
    /// effect (roccat_write_profile, lines 568-590), then the sum of the report's bytes as
    /// its last two (roccat_compute_crc, lines 334-347).</para>
    ///
    /// <para>Every picture is stored in the mouse, so the bounds are those of a stored write
    /// (<see cref="LenovoLegionLighting"/>), and the settings as they stood reach
    /// <see cref="KeyboardProfileBackups"/>, on disk, before the first write. An unplug, a failed
    /// write or a forced kill ends a session with no hand-back and leaves that owed: the next
    /// session takes the backup as the mouse's own, and with no claim the next run puts it back.
    /// The active profile can change during a session, and a picture lands on the one active then
    /// (<see cref="ReadActive"/>), so the backup holds one record per profile, in one entry. Going
    /// back to a profile takes no switch, unlike <see cref="LenovoLegionLighting"/>'s: libratbag
    /// reads any profile with report 04 (roccat_read_profile, lines 484-505) and writes any with
    /// the report that names it at byte 2 (roccat_write_profile, line 700), and only
    /// roccat_set_current_profile (lines 441-471) changes the active one.
    /// A write reads the settings first and moves only the lighting, the status, the flow, the
    /// effect, its speed and the four LEDs, so the DPI and the report rate the same report holds
    /// (struct roccat_settings_report, lines 112-131) stay as the user left them. A mouse that no
    /// longer shows a picture of PadForge's was set by Swarm or the user since, and is left as it
    /// is.</para>
    /// </summary>
    internal sealed class RoccatKoneEmpLighting : IHidLightingFamily
    {
        public static readonly RoccatKoneEmpLighting Instance = new();

        internal const ushort VendorId = 0x1E7D, ProductId = 0x2E24;

        internal const byte ReportConfigure = 0x04, ReportProfile = 0x05, ReportSettings = 0x06;

        /// <summary>ROCCAT_CONFIG_SETTINGS (line 75).</summary>
        internal const byte ConfigSettings = 0x80;

        /// <summary>sizeof(struct roccat_settings_report) (lines 112-131).</summary>
        internal const int SettingsLength = 41;

        internal const int Leds = 4;

        private const int StatusAt = 19, FlowAt = 20, EffectAt = 21, LedsAt = 23;
        private const byte UserColor = 0x1E, EffectFixed = 0x01;
        /// <summary>ROCCAT_MAX_RETRY_READY (line 57), and roccat_is_ready's and roccat_wait_ready's
        /// sleeps (lines 391, 404, 415).</summary>
        private const int ReadyPolls = 10, ReadyWaitMs = 10, BusyWaitMs = 100;

        /// <summary>The model's product key, the first part of the key each mouse's backup is kept
        /// under (<see cref="KeyboardProfileBackups.DeviceKey"/>).</summary>
        internal const string Product = "roccat:kone-emp";

        /// <summary>How many times in a row a run tries to put back what an earlier run left.</summary>
        internal const int RepairTries = LenovoLegionLighting.RepairTries;

        /// <summary>ROCCAT_PROFILE_MAX (line 52).</summary>
        internal const int LastProfile = 5;

        /// <summary>One profile's record in a backup: its number, then its settings.</summary>
        private const int RecordLength = 1 + SettingsLength;

        public string Name => "Roccat Kone EMP";

        private HidLightingModel _model;

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId || collection.ProductId != ProductId) return null;
            if (collection.FeatureReportLength < SettingsLength) return null;
            return _model ??= new HidLightingModel
            {
                Family = this,
                Name = Name,
                Map = new LightMap(new[]
                {
                    new LightInfo(0, LightKind.Edge, 0, LightKeySource.None),
                    new LightInfo(1, LightKind.Edge, 0, LightKeySource.None),
                    new LightInfo(2, LightKind.Edge, 0, LightKeySource.None),
                    new LightInfo(3, LightKind.Edge, 0, LightKeySource.None),
                }, "libratbag src/driver-roccat-kone-emp.c") { Product = Product },
                Programs = new[] { VendorPrograms.RoccatSwarm },
                HoldMs = LenovoLegionLighting.HoldMs,
                RepaintMs = LenovoLegionLighting.RepaintMs,
                Begin = Begin,
                Owed = path => KeyboardProfileBackups.Get(KeyboardProfileBackups.DeviceKey(Product, path)) != null
                               && KeyboardProfileBackups.Failures(KeyboardProfileBackups.DeviceKey(Product, path)) < RepairTries,
                Repair = (io, wait) => Repair(io, wait, KeyboardProfileBackups.DeviceKey(Product, io?.Path)),
            };
        }

        /// <summary>The sum of the report's bytes but the last two, low byte first.</summary>
        internal static void PutChecksum(byte[] report)
        {
            int sum = 0;
            for (int i = 0; i < SettingsLength - 2; i++) sum += report[i];
            report[SettingsLength - 2] = (byte)sum;
            report[SettingsLength - 1] = (byte)(sum >> 8);
        }

        /// <summary>The settings with the colors in them.</summary>
        internal static byte[] Written(byte[] settings, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var report = (byte[])settings.Clone();
            report[StatusAt] = 0xFF;
            report[FlowAt] = 0x00;
            report[EffectAt] = EffectFixed;
            int n = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < n; i++)
            {
                int at = lights[i].Address;
                if (at < 0 || at >= Leds) continue;
                report[LedsAt + at * 4] = UserColor;
                report[LedsAt + at * 4 + 1] = (byte)(colors[i] >> 16);
                report[LedsAt + at * 4 + 2] = (byte)(colors[i] >> 8);
                report[LedsAt + at * 4 + 3] = (byte)colors[i];
            }
            PutChecksum(report);
            return report;
        }

        /// <summary>Whether the settings show what <see cref="Written"/> leaves: the status FF, no
        /// flow, the fixed effect and every LED on its own color.</summary>
        internal static bool LooksOurs(byte[] settings)
        {
            if (settings[StatusAt] != 0xFF || settings[FlowAt] != 0x00 || settings[EffectAt] != EffectFixed) return false;
            for (int led = 0; led < Leds; led++)
                if (settings[LedsAt + led * 4] != UserColor) return false;
            return true;
        }

        /// <summary>The settings the mouse holds now with the lighting of
        /// <paramref name="original"/>, from the status to the last LED.</summary>
        internal static byte[] Restored(byte[] now, byte[] original)
        {
            var report = (byte[])now.Clone();
            Array.Copy(original, StatusAt, report, StatusAt, LedsAt + Leds * 4 - StatusAt);
            PutChecksum(report);
            return report;
        }

        /// <summary>roccat_is_ready and roccat_wait_ready (lines 378-421). Report 04's second
        /// byte: 01 ready, 02 an error, 03 not yet and 100 ms more, anything else not yet.</summary>
        internal static bool WaitReady(IHidReportChannel io, Action<int> wait)
        {
            var answer = new byte[Math.Max(3, io.FeatureReportLength)];
            wait?.Invoke(ReadyWaitMs);
            for (int i = 0; i < ReadyPolls; i++)
            {
                if (!io.GetFeature(ReportConfigure, answer)) return false;
                if (answer[1] == 0x03) wait?.Invoke(BusyWaitMs);
                if (answer[1] == 0x02) return false;
                if (answer[1] == 0x01) return true;
                wait?.Invoke(ReadyWaitMs);
            }
            return false;
        }

        /// <summary>The active profile, roccat_current_profile (lines 425-439), or null.</summary>
        internal static byte? ReadActive(IHidReportChannel io)
        {
            var profile = new byte[Math.Max(3, io.FeatureReportLength)];
            if (!io.GetFeature(ReportProfile, profile) || profile[2] > LastProfile) return null;
            return profile[2];
        }

        /// <summary>The active profile's settings, or null.</summary>
        internal static byte[] ReadSettings(IHidReportChannel io, Action<int> wait)
            => ReadActive(io) is byte active ? ReadSettings(io, wait, active) : null;

        /// <summary>A profile's settings, active or not, or null.</summary>
        internal static byte[] ReadSettings(IHidReportChannel io, Action<int> wait, byte profile)
        {
            if (!io.SetFeature(new[] { ReportConfigure, profile, ConfigSettings })) return null;
            if (!WaitReady(io, wait)) return null;
            var buffer = new byte[Math.Max(SettingsLength, io.FeatureReportLength)];
            if (!io.GetFeature(ReportSettings, buffer)) return null;
            var settings = new byte[SettingsLength];
            Array.Copy(buffer, settings, SettingsLength);
            return settings;
        }

        /// <summary>A backup's contents: each profile's number and settings.</summary>
        internal static byte[] BackupOf(IReadOnlyDictionary<byte, byte[]> originals)
        {
            var contents = new byte[originals.Count * RecordLength];
            int at = 0;
            foreach (var (profile, settings) in originals)
            {
                contents[at] = profile;
                settings.CopyTo(contents, at + 1);
                at += RecordLength;
            }
            return contents;
        }

        /// <summary>The records in a backup's contents, or null for contents that are none.</summary>
        private static SortedDictionary<byte, byte[]> RecordsOf(byte[] contents)
        {
            if (contents.Length == 0 || contents.Length % RecordLength != 0) return null;
            var records = new SortedDictionary<byte, byte[]>();
            for (int at = 0; at < contents.Length; at += RecordLength)
                records[contents[at]] = contents.AsSpan(at + 1, SettingsLength).ToArray();
            return records;
        }

        /// <summary>Puts a profile's lighting back over what it holds now. False when a read or a
        /// write failed.</summary>
        private static bool PutBack(IHidReportChannel io, Action<int> wait, byte profile, byte[] original)
        {
            var now = ReadSettings(io, wait, profile);
            return now != null && io.SetFeature(Restored(now, original)) && WaitReady(io, wait);
        }

        internal static HidLightingSession Begin(IHidReportChannel io, Action<int> wait)
        {
            if (io == null) return null;
            if (ReadActive(io) is not byte active) return null;
            var settings = ReadSettings(io, wait, active);
            if (settings == null) return null;
            string key = KeyboardProfileBackups.SessionKey(Product, io.Path);
            var owed = new SortedDictionary<byte, byte[]>();
            if (KeyboardProfileBackups.Get(key) is { } backup)
            {
                var records = RecordsOf(backup.Contents);
                if (records != null)
                {
                    foreach (var (profile, original) in records)
                    {
                        var now = profile == active ? settings : ReadSettings(io, wait, profile);
                        if (now == null) return null;
                        // An earlier run's picture, never handed back. A profile that holds
                        // anything else holds its own or another program's, and stays.
                        if (!now.AsSpan().SequenceEqual(original) && LooksOurs(now)) owed[profile] = original;
                    }
                }
                if (owed.Count == 0) KeyboardProfileBackups.Drop(key);
                else if (owed.Count != records.Count) KeyboardProfileBackups.Take(key, (byte)owed.Count, BackupOf(owed));
            }
            return new Session(key, owed, active, settings);
        }

        /// <summary>Settles a backup an earlier run left, with no claim on the mouse: each profile
        /// that still shows a picture of PadForge's gets its lighting back. True when nothing is owed
        /// any more.</summary>
        private static bool Repair(IHidReportChannel io, Action<int> wait, string key)
        {
            if (KeyboardProfileBackups.Get(key) is not { } backup) return true;
            var records = RecordsOf(backup.Contents);
            if (records != null)
            {
                foreach (var (profile, original) in records)
                {
                    var now = ReadSettings(io, wait, profile);
                    if (now != null && (now.AsSpan().SequenceEqual(original) || !LooksOurs(now)
                                        || (io.SetFeature(Restored(now, original)) && WaitReady(io, wait))))
                        continue;
                    KeyboardProfileBackups.NoteFailure(key);
                    return false;
                }
            }
            KeyboardProfileBackups.Drop(key);
            return true;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly string _key;

            /// <summary>What each profile the session has seen active held before PadForge wrote
            /// it, by profile.</summary>
            private readonly SortedDictionary<byte, byte[]> _originals;

            /// <summary>The profiles that may hold a picture of PadForge's.</summary>
            private readonly SortedSet<byte> _ours;

            private byte[][] _crash = Array.Empty<byte[]>();
            private bool _unsaved;

            public Session(string key, SortedDictionary<byte, byte[]> owed, byte active, byte[] settings)
            {
                _key = key;
                _originals = new SortedDictionary<byte, byte[]>(owed);
                _ours = new SortedSet<byte>(owed.Keys);
                _originals.TryAdd(active, settings);
                _unsaved = _originals.Count != owed.Count;
                Track();
            }

            public override bool Feature => true;

            /// <summary>Each profile's settings as they stood, once a picture may have gone out to
            /// it. The crash path cannot read first, so they go whole, each to the profile its byte 2
            /// names. The same array until a profile comes or goes.</summary>
            public override byte[][] CrashFrames => _crash;

            private void Track()
            {
                if (_crash.Length == _ours.Count) return;
                var frames = new byte[_ours.Count][];
                int at = 0;
                foreach (byte profile in _ours) frames[at++] = _originals[profile];
                _crash = frames;
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (ReadActive(io) is not byte active) return HidLightingPaint.Failed;
                if (!_originals.ContainsKey(active))
                {
                    var first = ReadSettings(io, wait, active);
                    if (first == null) return HidLightingPaint.Failed;
                    _originals[active] = first;
                    _unsaved = true;
                }
                // The backup reaches the settings file before the first write it guards.
                if (_unsaved || KeyboardProfileBackups.Get(_key) == null)
                {
                    KeyboardProfileBackups.Take(_key, (byte)_originals.Count, BackupOf(_originals));
                    _unsaved = false;
                }
                if (!KeyboardProfileBackups.IsPersisted(_key))
                {
                    LaterMs = LenovoLegionLighting.SavingMs;
                    return HidLightingPaint.Later;
                }
                var now = ReadSettings(io, wait, active);
                if (now == null) return HidLightingPaint.Failed;
                // Marked first: a write that reports a failure may still have landed.
                _ours.Add(active);
                Track();
                return io.SetFeature(Written(now, lights, colors)) && WaitReady(io, wait)
                    ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                foreach (byte profile in _ours)
                    if (!PutBack(io, wait, profile, _originals[profile])) return false;
                _ours.Clear();
                Track();
                KeyboardProfileBackups.Drop(_key);
                return true;
            }
        }
    }
}
