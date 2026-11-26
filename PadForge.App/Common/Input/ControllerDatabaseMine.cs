using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Serialization;
using PadForge.Engine.Data;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>A device's mapping rows on one controller of one profile
    /// before a row of the user's changed them: what Remove Mine puts back
    /// (#600 item 47). <see cref="Rows"/> holds each base and shift row that
    /// read the device, with only the device's own sources.
    /// <see cref="Profile"/> is the profile that was active, empty for the
    /// default.</summary>
    internal sealed record ControllerDatabaseBefore(Guid Instance, int Slot, PadSetting PadSetting, IReadOnlyList<MappingRow> Rows)
    {
        public string Profile { get; init; } = string.Empty;
    }

    /// <summary>
    /// One row of the user's own (#600 items 47 to 49). <see cref="Id"/> is
    /// the ID SDL is given it under: a device's own, checksum and version
    /// kept, so SDL reads that device through it before any row of its
    /// model (SDL_PrivateGetGamepadMappingForGUID tries the exact ID
    /// first), or the row's own ID for a row added for many devices.
    /// <see cref="Device"/> names a device SDL does not read, such as a
    /// plugin's, which PadForge maps through the row itself.
    /// <see cref="OldRow"/> is the mapping SDL held for the row's ID before,
    /// an empty text when SDL had none, or null while that is not known: a
    /// row added while the engine was stopped learns it as SDL starts
    /// (ControllerDatabaseEngine.LearnOldRows).
    /// </summary>
    internal sealed record ControllerDatabaseMineRow(string Id, SdlMappingRow Row, DateTime AddedUtc, string OldRow,
        Guid Device, IReadOnlyList<ControllerDatabaseBefore> Before)
    {
        /// <summary>The row as SDL is given it.</summary>
        public string Text => Row.TextUnder(Id);

        /// <summary>Whether this row is for a device SDL does not read.</summary>
        public bool ForPadForgeDevice => Device != Guid.Empty;

        /// <summary>The devices whose rows were mapped again through this
        /// row, each with the profile that was active (<see cref="Key"/>). A
        /// device of the row's ID that is not here for the active profile,
        /// because it was away, the engine stopped, or another profile was
        /// active when the row was added, has its rows mapped again the first
        /// time PadForge reads it through the row with that profile active
        /// (ControllerDatabaseEngine.UserRowOpened, and the profile switch),
        /// so its rows never meet a reading they were not made for.</summary>
        public IReadOnlyList<string> Mapped { get; init; } = Array.Empty<string>();

        /// <summary>A device and a profile as <see cref="Mapped"/> holds
        /// them: the instance ID, then "@" and the profile's ID for a profile
        /// other than the default.</summary>
        internal static string Key(Guid instance, string profile) =>
            string.IsNullOrEmpty(profile) ? instance.ToString() : instance.ToString() + "@" + profile;

        /// <summary>The profile active now, empty for the default.</summary>
        internal static string ActiveProfile => SettingsManager.ActiveProfileId ?? string.Empty;

        /// <summary>Whether a device's rows were mapped again through this
        /// row with the active profile.</summary>
        public bool HasMapped(Guid instance) => HasMapped(instance, ActiveProfile);

        public bool HasMapped(Guid instance, string profile)
        {
            string key = Key(instance, profile);
            foreach (string k in Mapped)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>This row with a device's rows mapped again with the
        /// active profile, and its rows from before for that profile kept
        /// when none are kept yet.</summary>
        public ControllerDatabaseMineRow WithMapped(Guid instance, IReadOnlyList<ControllerDatabaseBefore> snapshot)
        {
            string profile = ActiveProfile;
            var before = new List<ControllerDatabaseBefore>(Before ?? Array.Empty<ControllerDatabaseBefore>());
            bool kept = false;
            foreach (var b in before)
                if (b.Instance == instance && string.Equals(b.Profile, profile, StringComparison.OrdinalIgnoreCase)) kept = true;
            if (!kept && snapshot != null) before.AddRange(snapshot);
            var mapped = new List<string>(Mapped);
            if (!HasMapped(instance, profile)) mapped.Add(Key(instance, profile));
            return this with { Before = before, Mapped = mapped };
        }
    }

    /// <summary>
    /// The user's own rows (#600 items 47 to 49), kept in the controller
    /// database section of PadForge.xml apart from the downloaded rows. They
    /// win for their devices over every other source, survive the row's
    /// reset and a new release, and go with Remove Mine, Clear, or Reset to
    /// Defaults. Never changed once built: a change replaces the whole list,
    /// so the poll thread reads it without a lock.
    /// </summary>
    internal sealed class ControllerDatabaseMine
    {
        /// <summary>At most this many rows, one per device and model: above
        /// the community's whole Windows file (about 870 rows), so Add Rows of
        /// that file keeps every row. A row past it is not added, and a kept
        /// row is never dropped to make room (#600).</summary>
        internal const int MaxRows = 2048;

        private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        internal static readonly ControllerDatabaseMine Empty = new(Array.Empty<ControllerDatabaseMineRow>(), Array.Empty<ControllerDatabaseBefore>());

        private static ControllerDatabaseMine _current = Empty;

        /// <summary>The rows in force.</summary>
        internal static ControllerDatabaseMine Current
        {
            get => System.Threading.Volatile.Read(ref _current);
            set => System.Threading.Volatile.Write(ref _current, value ?? Empty);
        }

        /// <summary>Replaces the rows with what <paramref name="change"/>
        /// makes of them, and answers whether that was another list.</summary>
        internal static bool Change(Func<ControllerDatabaseMine, ControllerDatabaseMine> change)
        {
            while (true)
            {
                var before = Current;
                var after = change(before) ?? Empty;
                if (ReferenceEquals(before, after)) return false;
                if (ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref _current, after, before), before)) return true;
            }
        }

        public IReadOnlyList<ControllerDatabaseMineRow> Rows { get; }

        /// <summary>Rows from before a removed row of the user's, owed to a
        /// profile that was not active when it went: put back the next time
        /// that profile is active.</summary>
        public IReadOnlyList<ControllerDatabaseBefore> PutBacks { get; }

        public int Count => Rows.Count;

        private ControllerDatabaseMine(IReadOnlyList<ControllerDatabaseMineRow> rows, IReadOnlyList<ControllerDatabaseBefore> putBacks)
        {
            Rows = rows;
            PutBacks = putBacks;
        }

        /// <summary>These rows with rows from before owed to other
        /// profiles.</summary>
        internal ControllerDatabaseMine WithPutBacks(IEnumerable<ControllerDatabaseBefore> owed)
        {
            var list = new List<ControllerDatabaseBefore>(PutBacks);
            list.AddRange(owed);
            return list.Count == PutBacks.Count ? this : new ControllerDatabaseMine(Rows, list);
        }

        /// <summary>These rows without the rows from before owed to a
        /// profile, which <paramref name="taken"/> gives.</summary>
        internal ControllerDatabaseMine WithoutPutBacks(string profile, out List<ControllerDatabaseBefore> taken)
        {
            taken = new List<ControllerDatabaseBefore>();
            var kept = new List<ControllerDatabaseBefore>();
            foreach (var b in PutBacks)
                (string.Equals(b.Profile, profile ?? string.Empty, StringComparison.OrdinalIgnoreCase) ? taken : kept).Add(b);
            return taken.Count == 0 ? this : new ControllerDatabaseMine(Rows, kept);
        }

        /// <summary>The user's row SDL reads a device through, by the
        /// device's ID: its own row first, then the row at the nearest step
        /// of SDL's lookup (<see cref="SdlMappingRow.Reach"/>), the first of
        /// them at one step.</summary>
        internal ControllerDatabaseMineRow ForSdlDevice(string deviceGuid)
        {
            if (deviceGuid == null) return null;
            ControllerDatabaseMineRow nearest = null;
            int nearestStep = int.MaxValue;
            foreach (var row in Rows)
            {
                if (row.ForPadForgeDevice) continue;
                if (string.Equals(row.Id, deviceGuid, StringComparison.OrdinalIgnoreCase)) return row;
                int step = SdlMappingRow.Reach(row.Id, deviceGuid);
                if (step > 0 && step < nearestStep)
                {
                    nearest = row;
                    nearestStep = step;
                }
            }
            return nearest;
        }

        /// <summary>The user's row for a device SDL does not read.</summary>
        internal ControllerDatabaseMineRow ForPadForgeDevice(Guid instance)
        {
            if (instance == Guid.Empty) return null;
            foreach (var row in Rows)
                if (row.Device == instance) return row;
            return null;
        }

        /// <summary>The user's row for a device either way.</summary>
        internal ControllerDatabaseMineRow For(Guid instance, string sdlGuid) =>
            string.IsNullOrEmpty(sdlGuid) ? ForPadForgeDevice(instance) : ForSdlDevice(sdlGuid);

        /// <summary>Whether <paramref name="row"/> would be kept: it replaces
        /// a row under the same ID or for the same device, or the list holds
        /// fewer than <see cref="MaxRows"/>.</summary>
        internal bool HasRoomFor(ControllerDatabaseMineRow row)
        {
            if (Rows.Count < MaxRows) return true;
            foreach (var r in Rows)
                if (SameKey(r, row)) return true;
            return false;
        }

        /// <summary>How many of <paramref name="rows"/> Add Rows would keep,
        /// as it keeps them in order: a row under an ID kept already, or
        /// earlier in the text, replaces that row and takes no room, and a new
        /// ID takes room while the list has some.</summary>
        internal int AddCount(IReadOnlyList<SdlMappingRow> rows)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Rows)
                if (!r.ForPadForgeDevice) ids.Add(r.Id);
            int room = MaxRows - Rows.Count, added = 0;
            foreach (var row in rows ?? Array.Empty<SdlMappingRow>())
            {
                if (ids.Contains(row.Guid)) { added++; continue; }
                if (room <= 0) continue;
                ids.Add(row.Guid);
                room--;
                added++;
            }
            return added;
        }

        /// <summary>These rows with one added, replacing a row under the same
        /// ID or for the same device, or these rows themselves when the list
        /// is full and the row replaces none: no kept row is dropped to make
        /// room.</summary>
        internal ControllerDatabaseMine With(ControllerDatabaseMineRow row)
        {
            if (!HasRoomFor(row)) return this;
            var rows = new List<ControllerDatabaseMineRow>(Rows.Count + 1);
            foreach (var r in Rows)
                if (!SameKey(r, row)) rows.Add(r);
            rows.Add(row);
            return new ControllerDatabaseMine(rows, PutBacks);
        }

        /// <summary>These rows without one.</summary>
        internal ControllerDatabaseMine Without(ControllerDatabaseMineRow row)
        {
            var rows = new List<ControllerDatabaseMineRow>(Rows.Count);
            foreach (var r in Rows)
                if (!ReferenceEquals(r, row) && !SameKey(r, row)) rows.Add(r);
            return rows.Count == Rows.Count ? this
                : rows.Count == 0 && PutBacks.Count == 0 ? Empty
                : new ControllerDatabaseMine(rows, PutBacks);
        }

        private static bool SameKey(ControllerDatabaseMineRow a, ControllerDatabaseMineRow b) =>
            a.ForPadForgeDevice || b.ForPadForgeDevice
                ? a.Device == b.Device
                : string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

        /// <summary>The rows SDL is given as it starts, after PadForge's own,
        /// so each stands where they meet.</summary>
        internal IEnumerable<string> SdlRows()
        {
            foreach (var row in Rows)
                if (!row.ForPadForgeDevice) yield return row.Text;
        }

        /// <summary>How many devices the rows are for: each row is one.</summary>
        internal int Devices => Rows.Count;

        /// <summary>
        /// Where a device SDL does not read puts its inputs in SDL's layout,
        /// by the user's row for it, or null. The default mapping reads it
        /// as it reads a Bliss-Box port's (SettingsManager.CreateDefaultPadSetting).
        /// </summary>
        internal static PadForge.Engine.Common.GamepadPlacement PlacementFor(UserDevice ud)
        {
            if (ud == null || !string.IsNullOrEmpty(ud.SdlGuid)) return null;
            var row = Current.ForPadForgeDevice(ud.InstanceGuid);
            return row == null ? null : ControllerDatabaseLayout.PlacementOf(row.Row, out _);
        }

        // ─────────────────────────────────────────────
        //  The settings file
        // ─────────────────────────────────────────────

        private static readonly XmlSerializer RowSerializer = new(typeof(MappingRow));

        private static ControllerDatabaseBeforeData BeforeData(ControllerDatabaseBefore item) => new()
        {
            Instance = item.Instance.ToString(),
            Slot = item.Slot.ToString(CultureInfo.InvariantCulture),
            Profile = string.IsNullOrEmpty(item.Profile) ? null : item.Profile,
            PadSetting = item.PadSetting,
            Rows = item.Rows?.Count > 0 ? new List<MappingRow>(item.Rows).ToArray() : null,
        };

        /// <summary>A row from before as the settings file holds it, or null
        /// when the file's text cannot be read.</summary>
        private static ControllerDatabaseBefore BeforeOf(ControllerDatabaseBeforeData b)
        {
            if (b == null || !Guid.TryParse(b.Instance, out Guid instance)
                || !int.TryParse(b.Slot, NumberStyles.None, CultureInfo.InvariantCulture, out int slot)
                || slot < 0 || slot >= InputManager.MaxPads)
                return null;
            return new ControllerDatabaseBefore(instance, slot, b.PadSetting, b.Rows ?? Array.Empty<MappingRow>())
            {
                Profile = b.Profile ?? string.Empty,
            };
        }

        /// <summary>The rows from before owed to other profiles, as the
        /// settings file holds them, or null for none.</summary>
        internal ControllerDatabaseBeforeData[] PutBacksData()
        {
            if (PutBacks.Count == 0) return null;
            var data = new ControllerDatabaseBeforeData[PutBacks.Count];
            for (int i = 0; i < data.Length; i++) data[i] = BeforeData(PutBacks[i]);
            return data;
        }

        /// <summary>The rows as the settings file holds them, or null for
        /// none.</summary>
        internal ControllerDatabaseMineData[] ToData()
        {
            if (Rows.Count == 0) return null;
            var data = new ControllerDatabaseMineData[Rows.Count];
            for (int i = 0; i < data.Length; i++)
            {
                var row = Rows[i];
                var before = new ControllerDatabaseBeforeData[row.Before?.Count ?? 0];
                for (int b = 0; b < before.Length; b++) before[b] = BeforeData(row.Before[b]);
                data[i] = new ControllerDatabaseMineData
                {
                    Added = row.AddedUtc.ToString(TimeFormat, CultureInfo.InvariantCulture),
                    Device = row.ForPadForgeDevice ? row.Device.ToString() : null,
                    Old = row.OldRow,
                    Mapped = row.Mapped.Count > 0 ? string.Join(",", row.Mapped) : null,
                    Text = row.Text,
                    Before = before.Length > 0 ? before : null,
                };
            }
            return data;
        }

        /// <summary>
        /// The rows a settings file holds. A row is checked again by the same
        /// grammar a paste is, and one that fails is dropped:
        /// <paramref name="changed"/> then says the file is to be saved again.
        /// </summary>
        internal static ControllerDatabaseMine FromData(ControllerDatabaseMineData[] data, out bool changed) =>
            FromData(data, null, out changed);

        /// <summary>The rows a settings file holds, and the rows from before
        /// it owes to other profiles.</summary>
        internal static ControllerDatabaseMine FromData(ControllerDatabaseMineData[] data, ControllerDatabaseBeforeData[] putBacks,
            out bool changed)
        {
            changed = false;
            var mine = Empty;
            var owed = new List<ControllerDatabaseBefore>();
            foreach (var b in putBacks ?? Array.Empty<ControllerDatabaseBeforeData>())
            {
                var item = BeforeOf(b);
                if (item == null) changed = true;
                else owed.Add(item);
            }
            mine = mine.WithPutBacks(owed);
            if (data == null || data.Length == 0) return mine;
            foreach (var item in data)
            {
                if (item == null || !SdlMappingRow.TryParse(item.Text, out var row, out _))
                {
                    changed = true;
                    continue;
                }
                Guid device = Guid.Empty;
                if (item.Device != null && !Guid.TryParse(item.Device, out device)) { changed = true; continue; }
                if (!DateTime.TryParseExact(item.Added, TimeFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime added))
                {
                    added = DateTime.UnixEpoch;
                    changed = true;
                }
                string old = item.Old;
                if (old != null && (old.Length > SdlMappingRow.MaxLength * 2 || old.IndexOf('\n') >= 0)) { old = null; changed = true; }
                var before = new List<ControllerDatabaseBefore>();
                foreach (var b in item.Before ?? Array.Empty<ControllerDatabaseBeforeData>())
                {
                    var read = BeforeOf(b);
                    if (read == null) changed = true;
                    else before.Add(read);
                }
                var mapped = new List<string>();
                foreach (string part in (item.Mapped ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    int at = part.IndexOf('@');
                    if (Guid.TryParse(at < 0 ? part : part.Substring(0, at), out _)) mapped.Add(part);
                    else changed = true;
                }
                var built = new ControllerDatabaseMineRow(row.Guid, row, added, old, device, before) { Mapped = mapped };
                var next = mine.With(built);
                if (next.Count == mine.Count) changed = true;
                mine = next;
            }
            return mine;
        }

        /// <summary>A copy of a mapping row that shares nothing with the
        /// live one, with only the sources <paramref name="keep"/> takes.</summary>
        internal static MappingRow CopyOf(MappingRow row, Func<MappingSource, bool> keep)
        {
            MappingRow copy;
            using (var writer = new StringWriter(CultureInfo.InvariantCulture))
            {
                RowSerializer.Serialize(writer, row);
                using var reader = new StringReader(writer.ToString());
                copy = (MappingRow)RowSerializer.Deserialize(reader);
            }
            if (keep != null) copy.Sources = copy.Sources?.FindAll(s => s != null && keep(s)) ?? new List<MappingSource>();
            return copy;
        }
    }
}

namespace PadForge.Services
{
    /// <summary>One of the user's own controller database rows (#600), as
    /// PadForge.xml holds it: the row as SDL is given it, when it was added,
    /// the mapping SDL held before, the device it is for when SDL does not
    /// read that device, and the controller rows it changed. Every value is
    /// text, read again on load.</summary>
    public class ControllerDatabaseMineData
    {
        /// <summary>When, in UTC, as 2026-11-24T14:17:35Z.</summary>
        [XmlAttribute]
        public string Added { get; set; }

        /// <summary>The instance ID of a device SDL does not read.</summary>
        [XmlAttribute]
        public string Device { get; set; }

        /// <summary>The mapping SDL held for the device before, or none.</summary>
        [XmlAttribute]
        public string Old { get; set; }

        /// <summary>The devices whose rows were mapped again through the
        /// row, their instance IDs joined by commas.</summary>
        [XmlAttribute]
        public string Mapped { get; set; }

        /// <summary>The row in the database's form.</summary>
        [XmlElement("Text")]
        public string Text { get; set; }

        [XmlElement("Before")]
        public ControllerDatabaseBeforeData[] Before { get; set; }
    }

    /// <summary>A device's rows on one controller before a row of the
    /// user's mapped them again (#600).</summary>
    public class ControllerDatabaseBeforeData
    {
        [XmlAttribute]
        public string Instance { get; set; }

        [XmlAttribute]
        public string Slot { get; set; }

        /// <summary>The profile that was active, left out for the default.</summary>
        [XmlAttribute]
        public string Profile { get; set; }

        [XmlElement("PadSetting")]
        public PadSetting PadSetting { get; set; }

        [XmlArray("Rows")]
        [XmlArrayItem("Row")]
        public MappingRow[] Rows { get; set; }
    }
}
