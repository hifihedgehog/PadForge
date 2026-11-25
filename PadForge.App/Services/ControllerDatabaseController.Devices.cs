using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services.Catalog;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>How SDL reads a device, as the SDL Mapping card says it
    /// (#600 item 45).</summary>
    internal enum SdlReading
    {
        /// <summary>The engine is stopped, so SDL reads nothing.</summary>
        Stopped,
        /// <summary>SDL lists the device and reads it as a gamepad.</summary>
        Gamepad,
        /// <summary>SDL lists it and has no mapping for it.</summary>
        Joystick,
        /// <summary>SDL maps it, and PadForge reads it raw: Force Raw
        /// Joystick Mode, a Bliss-Box port, or a row of the user's removed
        /// this session.</summary>
        ReadRaw,
        /// <summary>A joystick SDL does not read, such as a plugin's. The
        /// user's row lays its inputs out in PadForge.</summary>
        PadForgeDevice,
        /// <summary>No joystick: a keyboard, a mouse, or a device with no
        /// inputs a row could name.</summary>
        None,
    }

    /// <summary>What the SDL Mapping card shows for a device (#600 item 45).</summary>
    internal sealed record SdlCardFacts(SdlReading Reading, string Name, string Id, SdlMappingSource Source, string SourceDate,
        SdlMappingRow Row, string RowText, ControllerDatabaseMineRow Mine, IReadOnlyList<(string Controller, IReadOnlyList<SdlPlaceLine> Lines)> Places,
        IReadOnlyList<string> Unused, string ControllerName)
    {
        internal static readonly SdlCardFacts Nothing = new(SdlReading.None, null, null, SdlMappingSource.None, null, null, null, null,
            Array.Empty<(string, IReadOnlyList<SdlPlaceLine>)>(), Array.Empty<string>(), null);
    }

    /// <summary>What applying a row came to (#600 item 47).</summary>
    internal enum SdlApplyResult
    {
        /// <summary>SDL reads the device through the row now.</summary>
        InUse,
        /// <summary>Kept, and SDL is given it when the engine starts.</summary>
        AtStart,
        /// <summary>SDL keeps the SDL_GAMECONTROLLERCONFIG variable's row for
        /// the device over the user's.</summary>
        VariableWins,
        /// <summary>SDL did not take the row.</summary>
        Refused,
        /// <summary>A device SDL does not read, mapped through the row in
        /// PadForge.</summary>
        InPadForge,
    }

    /// <summary>One row of Find a Mapping's list (#600 item 48).</summary>
    internal sealed record SdlFindRow(SdlMappingRow Row, SdlMappingSource Source, bool ThisDevice)
    {
        public string Name => Row.Name;
        public string Detail { get; init; }
        public string ReadName => Name + ", " + Detail;
    }

    internal sealed partial class ControllerDatabaseController
    {
        // The SDL Mapping card's work (#600 items 45 to 49). Every SDL call
        // goes through the poll thread (ControllerDatabaseEngine.Post), and
        // every change of a controller's rows runs the assistant's Map
        // Automatically sequence for one device (MainWindow.Assistant.AutoMap).

        /// <summary>The community database's page that says how to add a
        /// mapping.</summary>
        internal const string CommunityPage = "https://github.com/mdqinc/SDL_GameControllerDB#create-new-mappings";

        /// <summary>Reads the SDL_GAMECONTROLLERCONFIG variable, replaceable
        /// in tests.</summary>
        internal Func<string> Variable = () => Environment.GetEnvironmentVariable("SDL_GAMECONTROLLERCONFIG");

        private Func<IReadOnlyList<LiveItem>> _catalog;
        private System.Threading.SynchronizationContext _ui;

        /// <summary>Where the work after a profile switch runs
        /// (<see cref="ProfileSettledAsync"/>). Null runs it on the interface
        /// thread once the switch settles. A test sets it, and runs the work
        /// itself in its own order.</summary>
        internal Action<Action> SettleAfterSwitch;

        private void StartDevices()
        {
            _ui = System.Threading.SynchronizationContext.Current;
            ControllerDatabaseEngine.UserRowOpened += OnUserRowOpened;
            ControllerDatabaseEngine.OldRowsLearned += OnOldRowsLearned;
            SettingsManager.ActiveProfileChanged += OnActiveProfileChanged;
            _vm.Devices.SdlMapping.Host = this;
            _vm.Settings.SetControllerDatabaseMine(MineStatus, ClearMineAsync, AddRowsAsync);
            _catalog = CatalogItems;
            LiveCatalog.AddProvider(_catalog);
        }

        private void StopDevices()
        {
            ControllerDatabaseEngine.UserRowOpened -= OnUserRowOpened;
            ControllerDatabaseEngine.OldRowsLearned -= OnOldRowsLearned;
            SettingsManager.ActiveProfileChanged -= OnActiveProfileChanged;
            if (ReferenceEquals(_vm.Devices.SdlMapping.Host, this)) _vm.Devices.SdlMapping.Host = null;
            if (_catalog != null) LiveCatalog.RemoveProvider(_catalog);
        }

        private static UserDevice DeviceOf(Guid instance) => SettingsManager.FindDeviceByInstanceGuid(instance);

        /// <summary>Whether a device has inputs a row could name: a joystick
        /// of any kind, a plugin's or a script's device included.</summary>
        internal static bool IsJoystickClass(UserDevice ud) => ud != null && ud.CapType is InputDeviceType.Gamepad
            or InputDeviceType.Joystick or InputDeviceType.Driving or InputDeviceType.Flight or InputDeviceType.FirstPerson
            or InputDeviceType.Supplemental;

        // ─────────────────────────────────────────────
        //  The card's facts
        // ─────────────────────────────────────────────

        /// <summary>What the card shows for a device now.</summary>
        internal async Task<SdlCardFacts> ReadAsync(Guid instance)
        {
            var ud = DeviceOf(instance);
            if (!IsJoystickClass(ud)) return SdlCardFacts.Nothing;
            string controller = ControllerNames(instance).FirstOrDefault();
            var mine = ControllerDatabaseMine.Current;

            if (string.IsNullOrEmpty(ud.SdlGuid))
            {
                var own = mine.ForPadForgeDevice(instance);
                string id = instance.ToString("N");
                return Build(SdlReading.PadForgeDevice, ud.ProductName ?? ud.ResolvedName, id, own == null ? SdlMappingSource.None : SdlMappingSource.YoursPadForge,
                    own, own?.Row, own?.Row.TextUnder(id), instance, readAsGamepad: false, controller);
            }

            var facts = ud.IsOnline
                ? await ControllerDatabaseEngine.Post(e => e.ReadSdlFacts(instance), null)
                : null;
            if (facts == null)
            {
                // Offline, or the engine stopped: the user's row is still
                // known by the device's ID.
                var known = mine.ForSdlDevice(ud.SdlGuid);
                return Build(SdlReading.Stopped, null, ud.SdlGuid, known == null ? SdlMappingSource.None : SdlMappingSource.Yours,
                    known, known?.Row, known?.Text, instance, readAsGamepad: false, controller);
            }

            SdlMappingSource source;
            try
            {
                source = ControllerDatabaseSource.Of(facts.Guid, facts.Mapping, mine, SettingsManager.ControllerDatabase, OwnRows,
                    Baseline(), Variable());
            }
            catch (Exception)
            {
                source = facts.Mapping == null ? SdlMappingSource.None : SdlMappingSource.Sdl;
            }
            SdlMappingRow row = null;
            if (facts.Mapping != null)
                SdlMappingRow.TryParse(Sanitized(facts.Mapping), out row, out _);
            SdlReading reading = !facts.SdlMaps ? SdlReading.Joystick
                : facts.OpenedAsGamepad && !ud.ForceRawJoystickMode ? SdlReading.Gamepad
                : SdlReading.ReadRaw;
            var yours = source == SdlMappingSource.Yours ? mine.ForSdlDevice(facts.Guid) : mine.ForSdlDevice(facts.Guid);
            return Build(reading, row?.Name, facts.Guid, source, yours, row, row?.TextUnder(SdlMappingRow.CommunityId(facts.Guid)),
                instance, reading == SdlReading.Gamepad, controller);
        }

        private SdlCardFacts Build(SdlReading reading, string name, string id, SdlMappingSource source, ControllerDatabaseMineRow mine,
            SdlMappingRow row, string rowText, Guid instance, bool readAsGamepad, string controller)
        {
            string date = source switch
            {
                SdlMappingSource.Community => SafeBaseline()?.PinTimeUtc.ToString("D", CultureInfo.CurrentCulture),
                SdlMappingSource.Downloaded => SettingsManager.ControllerDatabase == null ? null
                    : TimeZoneInfo.ConvertTimeFromUtc(SettingsManager.ControllerDatabase.FetchedUtc, Zone).ToString("D", CultureInfo.CurrentCulture),
                SdlMappingSource.Yours or SdlMappingSource.YoursPadForge => mine == null ? null
                    : TimeZoneInfo.ConvertTimeFromUtc(mine.AddedUtc, Zone).ToString("D", CultureInfo.CurrentCulture),
                _ => null,
            };
            string device = instance.ToString();
            var places = new List<(string, IReadOnlyList<SdlPlaceLine>)>();
            var sets = new List<MappingSet>();
            foreach (var (slot, pad) in SlotsOf(instance))
            {
                var set = SettingsManager.SlotMappingSets is { } all && slot < all.Length ? all[slot] : null;
                if (set == null) continue;
                sets.Add(set);
                var ud = DeviceOf(instance);
                var lines = ControllerDatabaseLayout.Places(set, device, readAsGamepad, row,
                    d => (ud?.DeviceObjects is { Length: > 0 } objects ? MappingDisplayResolver.ResolveDescriptorText(d, objects) : null) ?? d);
                places.Add((PadName(pad), lines.Select(l => l with { Target = TargetLabel(pad, l.Target) }).ToList()));
            }
            var unused = readAsGamepad ? ControllerDatabaseLayout.FieldsNotUsed(row, sets, device) : new List<string>();
            return new SdlCardFacts(reading, name, id, source, date, row, rowText, mine, places, unused, controller);
        }

        /// <summary>SDL's mapping text in the form the user grammar reads:
        /// hints of other kinds and SDK bounds dropped, and an ID that SDL
        /// writes as a word replaced by a placeholder.</summary>
        internal static string Sanitized(string mapping)
        {
            if (string.IsNullOrEmpty(mapping)) return mapping;
            var parts = mapping.Split(',').ToList();
            parts.RemoveAll(p => p.StartsWith("sdk", StringComparison.Ordinal)
                || (p.StartsWith("hint:", StringComparison.Ordinal) && !p.Contains("_LABELS:=1", StringComparison.Ordinal)));
            if (parts.Count > 0 && parts[0].Length != 32) parts[0] = "00000000000000000000000000000001";
            if (parts.Count > 1) parts[1] = SdlMappingRow.CleanName(parts[1]);
            return string.Join(",", parts);
        }

        private ControllerDatabaseBaseline SafeBaseline()
        {
            try { return Baseline(); }
            catch (Exception) { return null; }
        }

        /// <summary>The controllers a device is on, each with its slot.</summary>
        internal IEnumerable<(int Slot, PadViewModel Pad)> SlotsOf(Guid instance)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) yield break;
            var slots = new SortedSet<int>();
            lock (settings.SyncRoot)
                foreach (var us in settings.Items)
                    if (us != null && us.InstanceGuid == instance && us.MapTo >= 0 && us.MapTo < InputManager.MaxPads) slots.Add(us.MapTo);
            foreach (int slot in slots)
                if (slot < _vm.Pads.Count) yield return (slot, _vm.Pads[slot]);
        }

        /// <summary>The controllers a device is on, as their pages name them.</summary>
        internal IEnumerable<string> ControllerNames(Guid instance) => SlotsOf(instance).Select(s => PadName(s.Pad));

        private static string PadName(PadViewModel pad) => pad.OutputTypeDisplayName + " #" + pad.TypeInstanceLabel;

        private static string TargetLabel(PadViewModel pad, string target)
        {
            foreach (var item in pad.Mappings)
                if (string.Equals(item.TargetSettingName, target, StringComparison.Ordinal)) return item.TargetLabel;
            return target;
        }

        // ─────────────────────────────────────────────
        //  Copy SDL Mapping (#600 item 46)
        // ─────────────────────────────────────────────

        /// <summary>
        /// The device's mapping as a community row: from SDL's mapping while
        /// SDL maps the device, else from the rows of the controller picked
        /// in the panel. The ID is the device's own with its checksum and
        /// version zeroed. Null when there is nothing to copy.
        /// </summary>
        internal async Task<(string Text, SdlLeftOut LeftOut)> CopyAsync(Guid instance, int pickedSlot)
        {
            var facts = await ReadAsync(instance);
            var ud = DeviceOf(instance);
            if (ud == null || facts.Reading == SdlReading.None) return (null, SdlLeftOut.None);
            string id = facts.Reading == SdlReading.PadForgeDevice ? instance.ToString("N") : SdlMappingRow.CommunityId(facts.Id ?? ud.SdlGuid);
            if (id == null) return (null, SdlLeftOut.None);

            var left = SdlLeftOut.None;
            var slots = SlotsOf(instance).ToList();
            int pick = slots.Any(x => x.Slot == pickedSlot) ? pickedSlot : slots.Count > 0 ? slots[0].Slot : -1;
            if (pick >= 0)
            {
                var pad = _vm.Pads[pick];
                if (pad.Macros?.Count > 0) left |= SdlLeftOut.Macros;
                var us = SettingFor(instance, pick);
                left |= ControllerDatabaseLayout.TuningLeftOut(us?.GetPadSetting());
            }

            if (facts.Row != null && facts.Reading is SdlReading.Gamepad or SdlReading.ReadRaw)
            {
                // What the device's rows do beyond SDL's mapping is left out.
                if (pick >= 0)
                {
                    ControllerDatabaseLayout.BindingsFromRows(SettingsManager.SlotMappingSets?[pick], instance.ToString(), out var beyond);
                    left |= beyond;
                }
                return (facts.Row.With(guid: id, name: SdlMappingRow.CleanName(string.IsNullOrEmpty(ud.ProductName) ? facts.Row.Name : ud.ProductName)).Text, left);
            }
            if (pick < 0) return (null, left);
            var bindings = ControllerDatabaseLayout.BindingsFromRows(SettingsManager.SlotMappingSets?[pick], instance.ToString(), out var rowsLeft);
            if (bindings.Count == 0) return (null, left | rowsLeft);
            var built = SdlMappingRow.Build(id, string.IsNullOrEmpty(ud.ProductName) ? ud.ResolvedName : ud.ProductName, bindings);
            return (built.Text, left | rowsLeft);
        }

        private static UserSetting SettingFor(Guid instance, int slot)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return null;
            lock (settings.SyncRoot)
                foreach (var us in settings.Items)
                    if (us != null && us.InstanceGuid == instance && us.MapTo == slot) return us;
            return null;
        }

        // ─────────────────────────────────────────────
        //  Guess a Layout (#600 item 48)
        // ─────────────────────────────────────────────

        /// <summary>A layout guessed from the device's raw inputs, as a row
        /// under the device's own ID, for Guess a Layout's dialog.</summary>
        internal string GuessRow(Guid instance)
        {
            var ud = DeviceOf(instance);
            if (ud == null) return string.Empty;
            int axes = ud.RawAxisCount > 0 ? ud.RawAxisCount : ud.CapAxeCount;
            int buttons = ud.RawButtonCount > 0 ? ud.RawButtonCount : ud.CapButtonCount;
            var bindings = ControllerDatabaseLayout.Guess(axes, buttons, ud.CapPovCount, ud.VendorId);
            if (bindings.Count == 0) return string.Empty;
            string id = string.IsNullOrEmpty(ud.SdlGuid) ? instance.ToString("N") : SdlMappingRow.CommunityId(ud.SdlGuid);
            return SdlMappingRow.Build(id, string.IsNullOrEmpty(ud.ProductName) ? ud.ResolvedName : ud.ProductName, bindings).Text;
        }

        // ─────────────────────────────────────────────
        //  The user's rows (#600 items 47 to 49)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Keeps a row as the user's own for a device and applies it at once:
        /// SDL is given it under the device's own ID, the device is read
        /// again, and its rows on every controller it is on are mapped again
        /// from the new layout. A device SDL does not read is laid out by the
        /// row in PadForge. The first row of the user's for a device keeps
        /// what was there before, for Remove Mine.
        /// </summary>
        internal async Task<SdlApplyResult> ApplyMineAsync(Guid instance, SdlMappingRow row)
        {
            var ud = DeviceOf(instance);
            if (ud == null || row == null) return SdlApplyResult.Refused;
            var mine = ControllerDatabaseMine.Current;

            if (string.IsNullOrEmpty(ud.SdlGuid))
            {
                var earlier = mine.ForPadForgeDevice(instance);
                string pid = instance.ToString("N");
                var kept = new ControllerDatabaseMineRow(pid, row.With(guid: pid), MineNow(), null, instance,
                    earlier?.Before ?? Snapshot(instance))
                {
                    Mapped = new[] { ControllerDatabaseMineRow.Key(instance, ControllerDatabaseMineRow.ActiveProfile) },
                };
                ControllerDatabaseMine.Change(m => m.With(kept));
                _markDirty();
                Remap(instance);
                return SdlApplyResult.InPadForge;
            }

            var facts = ud.IsOnline ? await ControllerDatabaseEngine.Post(e => e.ReadSdlFacts(instance), null) : null;
            string id = (facts?.Guid ?? ud.SdlGuid).ToLowerInvariant();
            // A row of the user's for the device already keeps what was there
            // before any, and which devices were mapped again through it.
            var earlierRow = mine.ForSdlDevice(id);
            bool sameId = earlierRow != null && string.Equals(earlierRow.Id, id, StringComparison.OrdinalIgnoreCase);
            // What SDL held for the ID: the device's mapping, none, or, for a
            // device away, SDL's answer for the ID. Unknown while the engine
            // is stopped, and learned as SDL starts.
            string old = sameId ? earlierRow.OldRow
                : facts != null ? facts.Mapping ?? string.Empty
                : await ControllerDatabaseEngine.Post(_ => ControllerDatabaseEngine.MappingForGuid(id), null);
            // The rows are mapped again only once the device is read through
            // the row (HandleUserRowOpened). Until then they keep the reading
            // they were made for.
            var keptRow = new ControllerDatabaseMineRow(id, row.With(guid: id), MineNow(), old, Guid.Empty,
                sameId ? earlierRow.Before : Snapshot(instance));
            ControllerDatabaseMine.Change(m => m.With(keptRow));
            _markDirty();
            ControllerDatabaseEngine.SetReadsRaw(id, false);
            var applied = await ControllerDatabaseEngine.Post(e => e.ApplySdlRows(new[] { keptRow.Text }, false), ControllerDatabaseApplied.NotRunning);
            if (!applied.Ran) return SdlApplyResult.AtStart;
            if (applied.Refused > 0) return SdlApplyResult.Refused;
            var after = await ControllerDatabaseEngine.Post(e => e.ReadSdlFacts(instance), null);
            if (after == null) return SdlApplyResult.AtStart;
            if (!string.Equals(ControllerDatabaseSource.LayoutOf(Sanitized(after.Mapping)), keptRow.Row.Layout, StringComparison.Ordinal))
                return SdlApplyResult.VariableWins;
            if (applied.Reopened.Contains(instance)) HandleUserRowOpened(instance);
            else if (after.OpenedAsGamepad) MarkMapped(instance);
            return SdlApplyResult.InUse;
        }

        /// <summary>
        /// A device opened through a row of the user's whose remap it owed:
        /// its rows from before are kept, if none are kept yet, and its rows
        /// on every controller it is on are mapped again from the new layout,
        /// once. Interface thread.
        /// </summary>
        internal void HandleUserRowOpened(Guid instance)
        {
            var ud = DeviceOf(instance);
            if (ud == null) return;
            var row = ControllerDatabaseMine.Current.For(instance, ud.SdlGuid);
            if (row == null || row.HasMapped(instance)) return;
            // Under Force Raw Joystick Mode the default authors nothing, so a
            // remap would only empty the device's rows: it waits until the
            // switch is off and the device is read through the row.
            if (ud.ForceRawJoystickMode) return;
            var updated = row.WithMapped(instance, Snapshot(instance));
            ControllerDatabaseMine.Change(m => m.With(updated));
            _markDirty();
            Remap(instance);
            _vm.Devices.SdlMapping.Refresh();
        }

        /// <summary>Records that a device reads through a row of the user's
        /// with rows already made for it: the gamepad it was, read again
        /// through the same layout.</summary>
        private void MarkMapped(Guid instance)
        {
            var ud = DeviceOf(instance);
            var row = ud == null ? null : ControllerDatabaseMine.Current.ForSdlDevice(ud.SdlGuid);
            if (row == null || row.HasMapped(instance)) return;
            var updated = row with
            {
                Mapped = new List<string>(row.Mapped) { ControllerDatabaseMineRow.Key(instance, ControllerDatabaseMineRow.ActiveProfile) },
            };
            ControllerDatabaseMine.Change(m => m.With(updated));
            _markDirty();
        }

        /// <summary>SDL told, as it started, what it held for rows added while
        /// the engine was stopped: the settings are saved with it.</summary>
        private void OnOldRowsLearned()
        {
            if (_disposed) return;
            if (_ui != null) _ui.Post(_ => _markDirty(), null);
            else _markDirty();
        }

        /// <summary>The poll thread's word that a device opened through a
        /// row of the user's it owes a remap for, carried to the interface
        /// thread.</summary>
        private void OnUserRowOpened(Guid instance)
        {
            if (_disposed) return;
            if (_ui != null) _ui.Post(_ => HandleUserRowOpened(instance), null);
            else HandleUserRowOpened(instance);
        }

        /// <summary>The rows from before of the devices whose rows were
        /// mapped again through a row, with the profile each was mapped in.
        /// A device that never was keeps the rows it has.</summary>
        private static List<ControllerDatabaseBefore> MappedBefore(ControllerDatabaseMineRow row)
        {
            var list = new List<ControllerDatabaseBefore>();
            foreach (var b in row.Before ?? Array.Empty<ControllerDatabaseBefore>())
                if (row.HasMapped(b.Instance, b.Profile)) list.Add(b);
            return list;
        }

        /// <summary>Puts back the rows from before of the active profile now,
        /// and owes the others to their profiles, put back the next time each
        /// is active (<see cref="OnActiveProfileChanged"/>).</summary>
        private void RestoreOrOwe(IReadOnlyList<ControllerDatabaseBefore> before)
        {
            string profile = ControllerDatabaseMineRow.ActiveProfile;
            var now = new List<ControllerDatabaseBefore>();
            var owed = new List<ControllerDatabaseBefore>();
            foreach (var b in before)
                (string.Equals(b.Profile, profile, StringComparison.OrdinalIgnoreCase) ? now : owed).Add(b);
            if (owed.Count > 0)
            {
                ControllerDatabaseMine.Change(m => m.WithPutBacks(owed));
                _markDirty();
            }
            Restore(now);
        }

        /// <summary>
        /// Another profile is active (#600): its rows of each device were
        /// made where that profile was saved. The rows from before owed to it
        /// are put back, and a device read through a row of the user's whose
        /// rows this profile has not mapped again yet has them mapped again,
        /// once. Run after the switch settles, on the interface thread.
        /// </summary>
        private void OnActiveProfileChanged()
        {
            if (_disposed) return;
            if (SettleAfterSwitch != null)
            {
                SettleAfterSwitch(() => _ = ProfileSettledAsync());
                return;
            }
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
                dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => _ = ProfileSettledAsync()));
            else if (_ui != null) _ui.Post(_ => _ = ProfileSettledAsync(), null);
            else _ = ProfileSettledAsync();
        }

        internal async Task ProfileSettledAsync()
        {
            if (_disposed) return;
            string profile = ControllerDatabaseMineRow.ActiveProfile;
            List<ControllerDatabaseBefore> owed = null;
            ControllerDatabaseMine.Change(m => m.WithoutPutBacks(profile, out owed));
            if (owed?.Count > 0)
            {
                Restore(owed);
                _markDirty();
            }
            var mine = ControllerDatabaseMine.Current;
            if (mine.Count == 0) return;
            var devices = SettingsManager.UserDevices;
            var present = new List<UserDevice>();
            if (devices != null)
                lock (devices.SyncRoot)
                    foreach (var ud in devices.Items)
                        if (ud != null) present.Add(ud);
            foreach (var ud in present)
            {
                var row = mine.For(ud.InstanceGuid, ud.SdlGuid);
                if (row == null || row.HasMapped(ud.InstanceGuid)) continue;
                if (row.ForPadForgeDevice)
                {
                    HandleUserRowOpened(ud.InstanceGuid);
                    continue;
                }
                // A device SDL reads through the row now: online, opened as a
                // gamepad, under the row's layout.
                if (!ud.IsOnline) continue;
                var facts = await ControllerDatabaseEngine.Post(e => e.ReadSdlFacts(ud.InstanceGuid), null);
                if (facts?.OpenedAsGamepad == true
                    && string.Equals(ControllerDatabaseSource.LayoutOf(Sanitized(facts.Mapping)), row.Row.Layout, StringComparison.Ordinal))
                    HandleUserRowOpened(ud.InstanceGuid);
            }
        }

        /// <summary>Whether a profile shortcut's trigger reads the device:
        /// its inputs stay as they were set when the reading changes, and the
        /// dialog says so.</summary>
        internal static bool NamedByShortcuts(Guid instance)
        {
            foreach (var shortcut in SettingsManager.GlobalMacros ?? Array.Empty<GlobalMacroData>())
            {
                if (shortcut == null) continue;
                if (shortcut.TriggerDeviceGuid == instance) return true;
                foreach (var entry in shortcut.TriggerEntries ?? Array.Empty<TriggerButtonEntry>())
                    if (entry != null && entry.DeviceInstanceGuid == instance) return true;
            }
            return false;
        }

        /// <summary>
        /// Remove Mine: the user's row for a device goes, SDL is given back
        /// the mapping it held before, or reads the device raw for the rest
        /// of the session when it had none, and the device's rows are put
        /// back as they were.
        /// </summary>
        internal async Task RemoveMineAsync(Guid instance)
        {
            var ud = DeviceOf(instance);
            var row = ControllerDatabaseMine.Current.For(instance, ud?.SdlGuid);
            if (row == null) return;
            ControllerDatabaseMine.Change(m => m.Without(row));
            _markDirty();
            await GiveBackAsync(row);
            var before = MappedBefore(row);
            RestoreOrOwe(before);
            // A device mapped again with no rows kept from before, put on a
            // controller since, takes the layout it is read through again.
            if (row.HasMapped(instance) && !before.Exists(b => b.Instance == instance)) Remap(instance);
        }

        /// <summary>Gives SDL back the reading a row of the user's
        /// replaced.</summary>
        private static Task GiveBackAsync(ControllerDatabaseMineRow row)
        {
            // A row SDL was never given, added while the engine was stopped
            // and removed before it ran, leaves SDL as it is.
            if (row.ForPadForgeDevice || row.OldRow == null) return Task.CompletedTask;
            if (row.OldRow.Length > 0)
                return ControllerDatabaseEngine.Post(e => e.ApplySdlRows(new[] { OldUnder(row) }, false), ControllerDatabaseApplied.NotRunning);
            ControllerDatabaseEngine.SetReadsRaw(row.Id, true);
            return ControllerDatabaseEngine.Post(e => e.ApplySdlRows(Array.Empty<string>(), false), ControllerDatabaseApplied.NotRunning);
        }

        /// <summary>The mapping SDL held before, under the ID the user's row
        /// took, so it lands on the same entry of SDL's list.</summary>
        internal static string OldUnder(ControllerDatabaseMineRow row)
        {
            string old = row.OldRow;
            int comma = old.IndexOf(',');
            return comma < 0 ? old : row.Id + old.Substring(comma);
        }

        /// <summary>Every row of the user's goes (Reset to Defaults), each
        /// device getting back the reading it had.</summary>
        internal static void ForgetAllMine()
        {
            var all = ControllerDatabaseMine.Current;
            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty;
            foreach (var row in all.Rows) _ = GiveBackAsync(row);
        }

        private async Task ClearMineAsync()
        {
            var all = ControllerDatabaseMine.Current;
            if (all.Count == 0) return;
            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty;
            _markDirty();
            foreach (var row in all.Rows)
            {
                await GiveBackAsync(row);
                RestoreOrOwe(MappedBefore(row));
            }
            _vm.Settings.RaiseControllerDatabaseMine();
            _vm.Devices.SdlMapping.Refresh();
        }

        /// <summary>
        /// Add Rows From a File or the Clipboard (#600 item 49): each Windows
        /// row is kept as the user's own under its own ID, for every device of
        /// that ID, and given to SDL at once. A device SDL reads through one
        /// now is read again. Returns how many rows were kept.
        /// </summary>
        internal async Task<int> AddRowsAsync(IReadOnlyList<SdlMappingRow> rows)
        {
            if (rows == null || rows.Count == 0) return 0;
            var texts = new List<string>(rows.Count);
            var matched = new List<Guid>();
            foreach (var row in rows)
            {
                var earlier = ControllerDatabaseMine.Current.ForSdlDevice(row.Guid);
                bool sameId = earlier != null && string.Equals(earlier.Id, row.Guid, StringComparison.OrdinalIgnoreCase);
                string old = sameId ? earlier.OldRow : null;
                var before = new List<ControllerDatabaseBefore>(sameId ? earlier.Before : Array.Empty<ControllerDatabaseBefore>());
                // Each connected device of the row's ID keeps what it had, for
                // Remove Mine: the mapping SDL held, and its rows.
                foreach (var ud in OnlineDevicesOf(row.Guid))
                {
                    matched.Add(ud.InstanceGuid);
                    if (!before.Exists(b => b.Instance == ud.InstanceGuid)) before.AddRange(Snapshot(ud.InstanceGuid));
                }
                // What SDL held for the row's ID. Unknown while the engine is
                // stopped, and learned as SDL starts.
                if (!sameId)
                {
                    string id = row.Guid;
                    old = await ControllerDatabaseEngine.Post(_ => ControllerDatabaseEngine.MappingForGuid(id), null);
                }
                var kept = new ControllerDatabaseMineRow(row.Guid, row, MineNow(), old, Guid.Empty, before)
                {
                    Mapped = sameId ? earlier.Mapped : Array.Empty<string>(),
                };
                ControllerDatabaseMine.Change(m => m.With(kept));
                ControllerDatabaseEngine.SetReadsRaw(row.Guid, false);
                texts.Add(kept.Text);
            }
            _markDirty();
            var applied = await ControllerDatabaseEngine.Post(e => e.ApplySdlRows(texts, false), ControllerDatabaseApplied.NotRunning);
            if (applied.Ran)
                foreach (Guid instance in matched)
                {
                    if (applied.Reopened.Contains(instance)) HandleUserRowOpened(instance);
                    else if (DeviceOf(instance)?.Device is PadForge.Engine.SdlDeviceWrapper { GameController: var pad } && pad != IntPtr.Zero)
                        MarkMapped(instance);
                }
            _vm.Settings.RaiseControllerDatabaseMine();
            _vm.Devices.SdlMapping.Refresh();
            return rows.Count;
        }

        /// <summary>The connected devices SDL reads that a row's ID maps.</summary>
        private static List<UserDevice> OnlineDevicesOf(string rowGuid)
        {
            var list = new List<UserDevice>();
            var devices = SettingsManager.UserDevices;
            if (devices == null) return list;
            lock (devices.SyncRoot)
                foreach (var ud in devices.Items)
                    if (ud != null && ud.IsOnline && SdlMappingRow.Maps(rowGuid, ud.SdlGuid)) list.Add(ud);
            return list;
        }

        /// <summary>The Your Rows line under the Controller Database row's
        /// More.</summary>
        private static string MineStatus()
        {
            int count = ControllerDatabaseMine.Current.Count;
            return count == 0
                ? Strings.Instance.ControllerDatabase_MineNone
                : string.Format(CultureInfo.CurrentCulture, Strings.CountFormat(nameof(Strings.ControllerDatabase_Mine_Format), count), count);
        }

        /// <summary>
        /// The downloaded rows in use at once (#600 item 49): a changed row
        /// replaces the community copy's in SDL, unless a row of the user's
        /// stands under the same ID, and the new rows are offered to every
        /// joystick SDL reads as a joystick, under the conditions a newly
        /// listed one meets. A device read again has its rows mapped again.
        /// </summary>
        private async Task ApplyDownloadAsync(ControllerDatabaseSection stored, ControllerDatabaseBaseline baseline)
        {
            var mine = ControllerDatabaseMine.Current;
            var changed = new List<string>();
            foreach (var row in stored.Rows)
            {
                if (row.Kind != ControllerDatabaseRowKind.Changed) continue;
                if (!baseline.TryFind(row.Row.Guid, out var entry) || entry.Verdict != ControllerDatabaseVerdict.Kept) continue;
                bool covered = false;
                foreach (var m in mine.Rows)
                    if (string.Equals(m.Id, row.Row.Guid, StringComparison.OrdinalIgnoreCase)) covered = true;
                if (!covered) changed.Add(row.Row.Text);
            }
            // No device's rows are made again: a changed row keeps a gamepad a
            // gamepad, its places where they were, and a new row reaches only
            // a joystick no controller uses (NewRowFor).
            var applied = await ControllerDatabaseEngine.Post(e => e.ApplySdlRows(changed, stored.NewRows > 0), ControllerDatabaseApplied.NotRunning);
            if (applied.Reopened.Count > 0) _vm.Devices.SdlMapping.Refresh();
        }

        /// <summary>Now, in whole seconds, as the settings file holds a time.</summary>
        private DateTime MineNow()
        {
            var now = UtcNow();
            return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }

        // ─────────────────────────────────────────────
        //  A device's rows: mapped again, kept, put back
        // ─────────────────────────────────────────────

        /// <summary>
        /// Map Again (#600 item 45): the device's rows on every controller it
        /// is on are made again from its layout, as assigning it makes them.
        /// The device's sources leave each controller's rows first
        /// (SettingsService.StripDeviceFromSlot, the unassign path), so the
        /// new layout replaces them rather than joining them, then the type
        /// change's default is authored (SettingsManager.ReAutoMapSlot's
        /// steps for one device) and the slot's rows follow it.
        /// </summary>
        internal void Remap(Guid instance)
        {
            var ud = DeviceOf(instance);
            if (ud == null) return;
            var touched = new List<PadViewModel>();
            foreach (var (slot, pad) in SlotsOf(instance).ToList())
            {
                var us = SettingFor(instance, slot);
                if (us == null) continue;
                SettingsService.StripDeviceFromSlot(instance, slot);
                if (pad.OutputType == VirtualControllerType.Nintendo) SettingsManager.StampNintendoWire(slot, pad.ProfileId);
                var ps = SettingsManager.CreateDefaultPadSetting(ud, pad.OutputType, pad.ProfileId, pad.ExtendedConfig);
                us.SetPadSetting(ps);
                us.PadSettingChecksum = ps.PadSettingChecksum;
                BlissBoxRuntime.NoteOwedDefault(us, ud, fresh: true);
                touched.Add(pad);
                Log($"AUTOMAP device={instance.ToString().Substring(0, 8)} slot={slot} (SDL Mapping)");
            }
            Refresh(touched);
        }

        /// <summary>The device's rows on every controller it is on, with
        /// only its own sources, for Remove Mine.</summary>
        internal static List<ControllerDatabaseBefore> Snapshot(Guid instance)
        {
            var list = new List<ControllerDatabaseBefore>();
            string device = instance.ToString();
            var settings = SettingsManager.UserSettings;
            if (settings == null) return list;
            var mine = new List<UserSetting>();
            lock (settings.SyncRoot)
                foreach (var us in settings.Items)
                    if (us != null && us.InstanceGuid == instance && us.MapTo >= 0) mine.Add(us);
            foreach (var us in mine)
            {
                var set = SettingsManager.SlotMappingSets is { } sets && us.MapTo < sets.Length ? sets[us.MapTo] : null;
                var rows = new List<MappingRow>();
                foreach (var r in set?.Rows ?? new List<MappingRow>())
                {
                    if (r?.Sources == null) continue;
                    bool reads = false;
                    foreach (var s in r.Sources)
                        if (s != null && string.Equals(s.DeviceGuid, device, StringComparison.OrdinalIgnoreCase)) { reads = true; break; }
                    if (reads) rows.Add(ControllerDatabaseMine.CopyOf(r, s => string.Equals(s.DeviceGuid, device, StringComparison.OrdinalIgnoreCase)));
                }
                list.Add(new ControllerDatabaseBefore(instance, us.MapTo, us.GetPadSetting()?.CloneDeep(), rows)
                {
                    Profile = ControllerDatabaseMineRow.ActiveProfile,
                });
            }
            return list;
        }

        /// <summary>Puts a device's rows back as a snapshot holds them, on
        /// the controllers it is still on.</summary>
        internal void Restore(IReadOnlyList<ControllerDatabaseBefore> before)
        {
            if (before == null || before.Count == 0) return;
            var touched = new List<PadViewModel>();
            foreach (var item in before)
            {
                var us = SettingFor(item.Instance, item.Slot);
                if (us == null || item.Slot >= _vm.Pads.Count) continue;
                SettingsService.StripDeviceFromSlot(item.Instance, item.Slot);
                if (item.PadSetting != null)
                {
                    var ps = item.PadSetting.CloneDeep();
                    us.SetPadSetting(ps);
                    us.PadSettingChecksum = ps.PadSettingChecksum;
                }
                var sets = SettingsManager.SlotMappingSets;
                if (sets != null && item.Slot < sets.Length)
                {
                    var set = sets[item.Slot] ??= new MappingSet();
                    set.Rows = PutBack(set.Rows, item.Rows);
                }
                touched.Add(_vm.Pads[item.Slot]);
            }
            Refresh(touched);
        }

        /// <summary>A slot's rows with a snapshot's sources put back: each
        /// joins the row of its target and layer, or comes back as its own
        /// row. A new list, assigned by reference, as the poll thread reads
        /// the rows unlocked.</summary>
        internal static List<MappingRow> PutBack(List<MappingRow> rows, IReadOnlyList<MappingRow> snapshot)
        {
            var result = new List<MappingRow>(rows ?? new List<MappingRow>());
            foreach (var stored in snapshot ?? Array.Empty<MappingRow>())
            {
                if (stored == null) continue;
                var copy = ControllerDatabaseMine.CopyOf(stored, null);
                int at = result.FindIndex(r => r != null && string.Equals(r.Target, copy.Target, StringComparison.Ordinal)
                    && string.Equals(r.LayerMask ?? "Base", copy.LayerMask ?? "Base", StringComparison.Ordinal));
                if (at < 0)
                {
                    result.Add(copy);
                    continue;
                }
                var sources = new List<MappingSource>(result[at].Sources ?? new List<MappingSource>());
                sources.AddRange(copy.Sources ?? new List<MappingSource>());
                result[at].Sources = sources;
            }
            return result;
        }

        /// <summary>The steps after a change of rows the assistant's Map
        /// Automatically runs: the slots' rows follow the device's settings,
        /// each page shows them again, the settings are saved and the
        /// device list is read again.</summary>
        private void Refresh(List<PadViewModel> pads)
        {
            if (pads.Count == 0) return;
            SettingsService.RefreshMappingSetsFromLegacy();
            foreach (var pad in pads)
            {
                pad.MappingsViewLoaded = false;
                InputService.RefreshMappingsToViewModel(pad);
            }
            _markDirty();
            _refreshDevices();
        }

        // ─────────────────────────────────────────────
        //  Find a Mapping (#600 item 48)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Every row PadForge carries: each mapping SDL holds (its own, the
        /// community copy, PadForge's own file, the changed and in-use rows,
        /// the user's), the downloaded new rows SDL has not been given, and
        /// the user's rows for devices SDL does not read. The device's own
        /// rows come first, then the rest by name.
        /// </summary>
        internal async Task<List<SdlFindRow>> AllRowsAsync(Guid instance)
        {
            var ud = DeviceOf(instance);
            string deviceGuid = ud?.SdlGuid;
            var held = await ControllerDatabaseEngine.Post(_ => ControllerDatabaseEngine.AllMappings(), null) ?? new List<string>();
            var rows = new List<SdlMappingRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string text)
            {
                if (!SdlMappingRow.TryParse(Sanitized(text), out var row, out _)) return;
                if (row.Guid == "00000000000000000000000000000001") return;
                if (seen.Add(row.Guid)) rows.Add(row);
            }
            foreach (string text in held) Add(text);
            if (SettingsManager.ControllerDatabase is { } section)
                foreach (var stored in section.Rows) Add(stored.Row.Text);
            foreach (string own in OwnRows) Add(own);
            foreach (var mine in ControllerDatabaseMine.Current.Rows) Add(mine.Text);
            return Describe(rows, deviceGuid);
        }

        /// <summary>Find's rows with their source line, the device's own
        /// first.</summary>
        internal List<SdlFindRow> Describe(IEnumerable<SdlMappingRow> rows, string deviceGuid)
        {
            var mine = ControllerDatabaseMine.Current;
            var section = SettingsManager.ControllerDatabase;
            var baseline = SafeBaseline();
            var list = new List<SdlFindRow>();
            foreach (var row in rows)
            {
                var source = ControllerDatabaseSource.OfRow(row.Guid, mine, section, OwnRows, baseline);
                bool own = deviceGuid != null && SdlMappingRow.Maps(row.Guid, deviceGuid);
                list.Add(new SdlFindRow(row, source, own) { Detail = SourceWords(source, SourceDateOf(source, row.Guid)) });
            }
            list.Sort((a, b) => a.ThisDevice != b.ThisDevice ? (a.ThisDevice ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        private string SourceDateOf(SdlMappingSource source, string guid) => source switch
        {
            SdlMappingSource.Community => SafeBaseline()?.PinTimeUtc.ToString("D", CultureInfo.CurrentCulture),
            SdlMappingSource.Downloaded => SettingsManager.ControllerDatabase is { } s
                ? TimeZoneInfo.ConvertTimeFromUtc(s.FetchedUtc, Zone).ToString("D", CultureInfo.CurrentCulture) : null,
            SdlMappingSource.Yours => ControllerDatabaseMine.Current.Rows.FirstOrDefault(r => string.Equals(r.Id, guid, StringComparison.OrdinalIgnoreCase)) is { } m
                ? TimeZoneInfo.ConvertTimeFromUtc(m.AddedUtc, Zone).ToString("D", CultureInfo.CurrentCulture) : null,
            _ => null,
        };

        /// <summary>A source in words, as the card's From line and Find's
        /// rows say it.</summary>
        internal static string SourceWords(SdlMappingSource source, string date)
        {
            var s = Strings.Instance;
            return source switch
            {
                SdlMappingSource.Sdl => s.SdlMapping_From_Sdl,
                SdlMappingSource.Community => string.Format(CultureInfo.CurrentCulture, s.SdlMapping_From_Community_Format, date ?? string.Empty),
                SdlMappingSource.Downloaded => string.Format(CultureInfo.CurrentCulture, s.SdlMapping_From_Downloaded_Format, date ?? string.Empty),
                SdlMappingSource.PadForge => s.SdlMapping_From_PadForge,
                SdlMappingSource.Yours or SdlMappingSource.YoursPadForge => string.Format(CultureInfo.CurrentCulture, s.SdlMapping_From_Yours_Format, date ?? string.Empty),
                SdlMappingSource.Variable => s.SdlMapping_From_Variable,
                _ => string.Empty,
            };
        }

        // ─────────────────────────────────────────────
        //  The catalog (#600 item 49)
        // ─────────────────────────────────────────────

        /// <summary>
        /// The user's rows as catalog entries, through wave 1's one seam for
        /// live rows: each is <c>db/&lt;ID&gt;</c>, its value the row, with
        /// Remove Mine in its menu. Free: a program at Change Settings may
        /// read and remove them, as a person does on the card.
        /// </summary>
        private IReadOnlyList<LiveItem> CatalogItems()
        {
            var s = Strings.Instance;
            var items = new List<LiveItem>();
            foreach (var row in ControllerDatabaseMine.Current.Rows)
            {
                var r = row;
                string id = "db/" + (r.ForPadForgeDevice ? r.Device.ToString("N") : r.Id);
                var remove = new LiveItem(id + "/remove", LiveKind.MenuItem, s.SdlMapping_RemoveMine, ReachClass.Free)
                {
                    Press = () => _ = RemoveRowAsync(r),
                    Where = s.Settings_ControllerDatabase,
                };
                items.Add(new LiveItem(id, LiveKind.Setting, r.Row.Name, ReachClass.Free)
                {
                    Read = () => r.Text,
                    Where = s.Settings_ControllerDatabase,
                    Menu = new[] { remove },
                });
            }
            return items;
        }

        /// <summary>Removes one of the user's rows, wherever its device is.</summary>
        internal async Task RemoveRowAsync(ControllerDatabaseMineRow row)
        {
            if (row == null) return;
            ControllerDatabaseMine.Change(m => m.Without(row));
            _markDirty();
            await GiveBackAsync(row);
            RestoreOrOwe(MappedBefore(row));
            _vm.Settings.RaiseControllerDatabaseMine();
            _vm.Devices.SdlMapping.Refresh();
        }
    }
}
