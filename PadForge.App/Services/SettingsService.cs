using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Service responsible for loading and saving PadForge settings to XML files.
    /// Handles the bidirectional sync between the SettingsManager's data collections
    /// and the WPF ViewModels.
    /// 
    /// Settings file search order:
    ///   1. PadForge.xml (preferred for new installs)
    ///   2. Settings.xml (generic fallback)
    /// 
    /// The settings file lives next to the executable.
    /// </summary>
    public class SettingsService
    {
        // ─────────────────────────────────────────────
        //  Constants
        // ─────────────────────────────────────────────

        /// <summary>Primary settings file name.</summary>
        public const string PrimaryFileName = "PadForge.xml";

        /// <summary>Fallback settings file name.</summary>
        public const string FallbackFileName = "Settings.xml";

        // ─────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────

        private readonly MainViewModel _mainVm;
        private string _settingsFilePath;
        private DispatcherTimer _autoSaveTimer;
        private readonly List<UserProfileData> _userProfiles = new();

        /// <summary>
        /// Full path to the active settings file.
        /// </summary>
        public string SettingsFilePath => _settingsFilePath;

        /// <summary>
        /// Whether settings have been modified since last save.
        /// </summary>
        public bool IsDirty { get; private set; }

        /// <summary>
        /// Raised after autosave completes so callers can perform post-save actions
        /// (e.g. refreshing the default profile snapshot).
        /// </summary>
        public event EventHandler AutoSaved;

        // ─────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────

        public SettingsService(MainViewModel mainVm)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
        }

        // ─────────────────────────────────────────────
        //  Initialize
        // ─────────────────────────────────────────────

        /// <summary>
        /// Initializes the settings service: ensures SettingsManager collections
        /// exist, finds the settings file, and loads it.
        /// </summary>
        public void Initialize()
        {
            // Ensure SettingsManager collections are initialized.
            if (SettingsManager.UserDevices == null)
                SettingsManager.UserDevices = new DeviceCollection();
            if (SettingsManager.UserSettings == null)
                SettingsManager.UserSettings = new SettingsCollection();

            // Find or create the settings file.
            _settingsFilePath = FindSettingsFile();

            // Load settings from disk.
            if (File.Exists(_settingsFilePath))
            {
                LoadFromFile(_settingsFilePath);
            }
            else
            {
                // No settings file — initialize profiles with the Default entry.
                LoadProfiles(null, null);
            }

            // Push file path to ViewModel.
            _mainVm.Settings.SettingsFilePath = _settingsFilePath;
            _mainVm.Settings.HasUnsavedChanges = false;
            IsDirty = false;
        }

        // ─────────────────────────────────────────────
        //  File discovery
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds the settings file. Checks for the primary file first,
        /// then fallback, then creates the primary file path for new installs.
        /// </summary>
        private static string FindSettingsFile()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;

            // Check primary file.
            string primaryPath = Path.Combine(appDir, PrimaryFileName);
            if (File.Exists(primaryPath))
                return primaryPath;

            // Check fallback file.
            string fallbackPath = Path.Combine(appDir, FallbackFileName);
            if (File.Exists(fallbackPath))
                return fallbackPath;

            // Neither exists — use primary path for new file.
            return primaryPath;
        }

        // ─────────────────────────────────────────────
        //  Load
        // ─────────────────────────────────────────────

        /// <summary>
        /// Loads settings from an XML file into the SettingsManager collections.
        /// </summary>
        /// <param name="filePath">Path to the settings XML file.</param>
        public void LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                return;

            try
            {
                SettingsFileData data;
                var serializer = new XmlSerializer(typeof(SettingsFileData));

                using (var stream = File.OpenRead(filePath))
                {
                    data = (SettingsFileData)serializer.Deserialize(stream);
                }

                if (data == null)
                    return;

                // Pre-deserialization migrations applied to every PadSetting
                // before they're handed out to UserSettings or the VM.
                //
                // TouchpadClick: prior versions auto-mapped this to "Button 11"
                // because SDL3's HIDAPI PS5/PS4 drivers report touchpad press
                // at joystick button index 11. v3.0.x makes "Touchpad 0 Click"
                // the canonical descriptor (consistent with the Touchpad 0
                // Finger N X/Y/Down family) and stops mirroring the bool into
                // state.Buttons[11], so legacy "Button 11" stops resolving.
                // Translate it on load so DualSense / DS4 users don't have to
                // re-map.
                if (data.PadSettings != null)
                {
                    foreach (var ps in data.PadSettings)
                    {
                        if (ps?.TouchpadClick == "Button 11")
                            ps.TouchpadClick = "Touchpad 0 Click";
                    }
                }

                // Populate SettingsManager collections.
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    SettingsManager.UserDevices.Items.Clear();
                    if (data.Devices != null)
                    {
                        foreach (var ud in data.Devices)
                            SettingsManager.UserDevices.Items.Add(ud);
                    }
                }

                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    SettingsManager.UserSettings.Items.Clear();
                    if (data.Settings != null)
                    {
                        foreach (var us in data.Settings)
                        {
                            // Link PadSetting — clone the template so each device
                            // has its own independent PadSetting instance. Without
                            // cloning, devices that share a checksum would share the
                            // same object, so modifying one device's settings would
                            // silently corrupt the other's.
                            if (data.PadSettings != null && us.PadSettingChecksum != null)
                            {
                                var template = data.PadSettings.FirstOrDefault(
                                    p => p.PadSettingChecksum == us.PadSettingChecksum);
                                if (template != null)
                                {
                                    // CloneDeep copies all properties + mapping arrays
                                    var ps = template.CloneDeep();
                                    us.SetPadSetting(ps);
                                }
                            }

                            SettingsManager.UserSettings.Items.Add(us);
                        }
                    }
                }

                // Purge orphaned UserSettings (MapTo == -1) left by older versions.
                // Lock guards the polling thread's FindByInstanceGuid /
                // FindByPadIndex iteration: Reload() can fire while the engine
                // is running, and List<T>.RemoveAll mutating concurrently with
                // a foreach is undefined behavior.
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    SettingsManager.UserSettings.Items.RemoveAll(us => us.MapTo < 0);
                }

                // Phase 2A — load persisted SlotMappingSets when present;
                // fall back to one-time legacy migration only for slots
                // where the XML had no MappingSet (or an empty one). Once
                // a slot's MappingSet is authoritative, UI multi-source
                // edits survive the save round-trip.
                LoadOrMigrateSlotMappingSets(data.SlotMappingSets);

                // Load app settings into ViewModel.
                if (data.AppSettings != null)
                    LoadAppSettings(data.AppSettings);

                // Publish user-imported HIDMaestro profiles to the catalog
                // so they appear in the Extended dropdown alongside the
                // built-in entries. _userProfiles is the in-memory mirror
                // of AppSettings.UserProfiles, mutated live by
                // AddUserProfile() and serialized back out on save.
                _userProfiles.Clear();
                if (data.AppSettings?.UserProfiles != null)
                {
                    foreach (var p in data.AppSettings.UserProfiles)
                    {
                        if (p != null && !string.IsNullOrWhiteSpace(p.Json))
                            _userProfiles.Add(p);
                    }
                }
                Common.Input.HMaestroProfileCatalog.UserProfilesProvider =
                    () => _userProfiles
                        .Where(p => !string.IsNullOrWhiteSpace(p?.Json))
                        .Select(p => p.Json)
                        .ToList();
                Common.Input.HMaestroProfileCatalog.Reload();

                // Load pad-specific settings.
                if (data.PadSettings != null)
                    LoadPadSettings(data.Settings, data.PadSettings);

                // Load macros into pad ViewModels.
                if (data.Macros != null)
                    LoadMacros(data.Macros);

                // Load profiles.
                LoadProfiles(data.Profiles, data.AppSettings);
            }
            catch (Exception ex)
            {
                _mainVm.StatusText = string.Format(Strings.Instance.Status_ErrorLoadingSettings_Format, ex.Message);
            }
        }

        /// <summary>
        /// Phase 2A: load <see cref="MappingSet"/>s persisted in the XML
        /// when present; one-time-migrate from legacy <see cref="PadSetting"/>
        /// fields for slots whose XML had no MappingSet (or an empty one).
        /// Once a slot's MappingSet has been authored / loaded, it's the
        /// authoritative source for descriptors and the legacy fields stop
        /// being consulted on subsequent loads.
        /// </summary>
        private static void LoadOrMigrateSlotMappingSets(MappingSet[] persisted)
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || sets.Length == 0) return;

            for (int slot = 0; slot < sets.Length; slot++)
            {
                MappingSet fromXml = (persisted != null && slot < persisted.Length)
                    ? persisted[slot]
                    : null;

                bool xmlHasContent = fromXml != null && fromXml.Rows != null && fromXml.Rows.Count > 0;
                if (xmlHasContent)
                {
                    SanitizeMappingSet(fromXml, slot);
                    sets[slot] = fromXml;
                    continue;
                }

                // Initial migration from legacy fields.
                sets[slot] = BuildOneSlotFromLegacy(slot);
            }
        }

        /// <summary>
        /// In-place cleanup of a loaded MappingSet:
        /// 1. Deduplicates row Sources by (DeviceGuid, Descriptor) — heals
        ///    the per-save accumulation bug from earlier multi-source builds.
        /// 2. Drops sources whose owning device is not gamepad-class for
        ///    gamepad-class targets — heals the "joystick stuck left"
        ///    symptom caused by stale auto-mapped gamepad descriptors on
        ///    keyboard/mouse/touchpad PadSettings polluting the row.
        /// </summary>
        private static void SanitizeMappingSet(MappingSet ms, int slot)
        {
            if (ms?.Rows == null) return;

            UserDevice[] devSnapshot;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                devSnapshot = SettingsManager.UserDevices.Items.ToArray();
            }
            bool IsGamepadDevice(string deviceGuid)
            {
                if (string.IsNullOrEmpty(deviceGuid)) return true; // "any device"
                if (!Guid.TryParse(deviceGuid, out Guid g)) return true;
                foreach (var ud in devSnapshot)
                {
                    if (ud != null && ud.InstanceGuid == g)
                        return ud.CapType == InputDeviceType.Gamepad;
                }
                return false; // unknown device — assume non-gamepad
            }

            foreach (var row in ms.Rows)
            {
                if (row?.Sources == null) continue;
                bool isGamepadTarget = IsGamepadOnlyTarget(row.Target);
                var seen = new HashSet<(string, string)>();
                int writeIdx = 0;
                for (int i = 0; i < row.Sources.Count; i++)
                {
                    var s = row.Sources[i];
                    if (s == null) continue;
                    if (isGamepadTarget && !IsGamepadDevice(s.DeviceGuid)) continue;
                    var key = ((s.DeviceGuid ?? "").ToLowerInvariant(), s.Descriptor ?? "");
                    if (!seen.Add(key)) continue;
                    row.Sources[writeIdx++] = s;
                }
                if (writeIdx < row.Sources.Count)
                    row.Sources.RemoveRange(writeIdx, row.Sources.Count - writeIdx);
            }

            // Drop rows whose sources were all filtered out — leaves the
            // engine and UI to fall back to legacy PadSetting fields for
            // those targets instead of seeing an empty MappingSet row.
            ms.Rows.RemoveAll(r => r?.Sources == null || r.Sources.Count == 0);
        }

        private static bool IsGamepadOnlyTarget(string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            if (target.StartsWith("Kbm", StringComparison.Ordinal)) return false;
            if (target.StartsWith("Midi", StringComparison.Ordinal)) return false;
            if (target.StartsWith("Extended", StringComparison.Ordinal)) return false;
            if (target.StartsWith("Touchpad", StringComparison.Ordinal)) return false;
            return true; // ButtonA/B/X/Y, LeftShoulder, ..., LeftThumbAxisX, LeftTrigger, DPad*, etc.
        }

        private static MappingSet BuildOneSlotFromLegacy(int slot)
        {
            UserSetting[] snapshot;
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                snapshot = SettingsManager.UserSettings.Items.ToArray();
            }

            // Look up each device's CapType so the migrator can skip
            // emitting gamepad-target sources for non-gamepad devices
            // (keyboards, mice, touchpads). Stops keyboards with stale
            // auto-mapped gamepad descriptors from polluting the per-VC
            // MappingSet — without this, an axis source pointing at
            // state.Axis[0] reads uninitialized 0 → bipolar -1 →
            // joystick stuck pointing left during evaluation.
            UserDevice[] devSnapshot;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                devSnapshot = SettingsManager.UserDevices.Items.ToArray();
            }

            bool IsGamepad(Guid g)
            {
                foreach (var ud in devSnapshot)
                {
                    if (ud != null && ud.InstanceGuid == g)
                        return ud.CapType == InputDeviceType.Gamepad;
                }
                // Unknown device — be conservative and assume non-gamepad
                // so we don't pollute. The user can always re-record on
                // an actual gamepad.
                return false;
            }

            var devicesForSlot = new List<(string DeviceGuid, PadSetting PadSetting, bool IsGamepadEligible)>();
            foreach (var us in snapshot)
            {
                if (us == null || us.MapTo != slot) continue;
                var ps = us.GetPadSetting();
                if (ps == null) continue;
                devicesForSlot.Add((
                    us.InstanceGuid.ToString(),
                    ps,
                    IsGamepad(us.InstanceGuid)));
            }

            return MappingSetMigrator.BuildFromLegacy(slot, devicesForSlot);
        }

        /// <summary>
        /// Phase 2C — push the per-VC PadViewModel's MappingItem
        /// <c>ExtraSources</c> + <c>CombineMode</c> + <c>CombineExpression</c>
        /// into the in-memory <see cref="SettingsManager.SlotMappingSets"/>
        /// so they survive the legacy-merge step and the XML round-trip.
        /// Runs in <see cref="SaveToFile"/> right after the UI → PadSetting
        /// push.
        /// </summary>
        private void PushUiExtraSourcesIntoSlotMappingSets()
        {
            var pads = _mainVm?.Pads;
            if (pads == null) return;
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null) return;

            for (int slot = 0; slot < pads.Count && slot < sets.Length; slot++)
            {
                var padVm = pads[slot];
                if (padVm == null) continue;

                // Ensure the slot has a MappingSet to mutate.
                var ms = sets[slot] ?? (sets[slot] = new MappingSet());

                foreach (var mapping in padVm.Mappings)
                {
                    if (mapping == null || string.IsNullOrEmpty(mapping.TargetSettingName)) continue;

                    // Find or create the row for this Target on the Base layer.
                    MappingRow row = null;
                    foreach (var r in ms.Rows)
                    {
                        if (r != null
                            && string.Equals(r.LayerMask ?? "Base", "Base", StringComparison.Ordinal)
                            && string.Equals(r.Target, mapping.TargetSettingName, StringComparison.Ordinal))
                        {
                            row = r;
                            break;
                        }
                    }
                    if (row == null)
                    {
                        row = new MappingRow { Target = mapping.TargetSettingName, LayerMask = "Base" };
                        ms.Rows.Add(row);
                    }

                    row.CombineMode = mapping.CombineMode ?? "";
                    row.CombineExpression = mapping.CombineExpression ?? "";

                    // Phase 2C — clear and rebuild Sources from the UI
                    // state so MappingSet is fully authoritative for
                    // this row. This stops keyboard primaries (which
                    // the legacy gamepad-only migrator filters out of
                    // rebuilt rows) from disappearing across save
                    // round-trips when a non-gamepad device authored
                    // the primary descriptor.
                    row.Sources.Clear();

                    // Defensive filter: never write a non-gamepad
                    // device's source into a gamepad-class target row.
                    // Catches earlier-bled rows where a keyboard's
                    // GUID got attached to "Axis 0" / "Button N", which
                    // would otherwise stick the joystick at -1 every
                    // poll because the keyboard has no axes.
                    bool gamepadOnly = IsGamepadOnlyTarget(mapping.TargetSettingName);
                    bool primaryDeviceIsGamepadEligible = true;
                    if (gamepadOnly && !string.IsNullOrEmpty(mapping.PrimarySourceDeviceGuid)
                        && Guid.TryParse(mapping.PrimarySourceDeviceGuid, out var pg))
                    {
                        UserDevice ud;
                        lock (SettingsManager.UserDevices.SyncRoot)
                        {
                            ud = SettingsManager.UserDevices.Items.FirstOrDefault(
                                d => d.InstanceGuid == pg);
                        }
                        primaryDeviceIsGamepadEligible = (ud != null
                            && ud.CapType == InputDeviceType.Gamepad);
                    }

                    // Push the primary as Sources[0] when present and
                    // device-eligible.
                    string primaryDesc = mapping.SourceDescriptor ?? "";
                    if (!string.IsNullOrEmpty(primaryDesc) && primaryDeviceIsGamepadEligible)
                    {
                        // Strip any I/H prefix off the descriptor so
                        // the new schema's per-source bool flags are
                        // the source of truth, matching how the
                        // migrator emits sources.
                        bool inv = false, half = false;
                        string clean = primaryDesc;
                        if (clean.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                        { inv = true; half = true; clean = clean.Substring(2); }
                        else if (clean.StartsWith("I", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
                        { inv = true; clean = clean.Substring(1); }
                        else if (clean.StartsWith("H", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
                        { half = true; clean = clean.Substring(1); }

                        row.Sources.Add(new MappingSource
                        {
                            Kind = "Direct",
                            DeviceGuid = mapping.PrimarySourceDeviceGuid ?? "",
                            Descriptor = clean,
                            Invert = inv,
                            HalfAxis = half,
                            DeadZone = mapping.MappingDeadZone,
                        });

                        // For bipolar axis rows, also encode the Neg
                        // descriptor as a paired source with Invert
                        // flipped (the load path detects this pair).
                        if (!string.IsNullOrEmpty(mapping.NegSettingName)
                            && !string.IsNullOrEmpty(mapping.NegSourceDescriptor))
                        {
                            string negRaw = mapping.NegSourceDescriptor;
                            bool ninv = false, nhalf = false;
                            string ncl = negRaw;
                            if (ncl.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                            { ninv = true; nhalf = true; ncl = ncl.Substring(2); }
                            else if (ncl.StartsWith("I", StringComparison.OrdinalIgnoreCase) && ncl.Length > 1 && !char.IsDigit(ncl[1]))
                            { ninv = true; ncl = ncl.Substring(1); }
                            else if (ncl.StartsWith("H", StringComparison.OrdinalIgnoreCase) && ncl.Length > 1 && !char.IsDigit(ncl[1]))
                            { nhalf = true; ncl = ncl.Substring(1); }
                            // Negative source: flip Invert relative to
                            // primary's encoded inversion.
                            row.Sources.Add(new MappingSource
                            {
                                Kind = "Direct",
                                DeviceGuid = mapping.PrimarySourceDeviceGuid ?? "",
                                Descriptor = ncl,
                                Invert = !ninv,
                                HalfAxis = nhalf,
                                DeadZone = mapping.MappingDeadZone,
                            });
                        }
                    }

                    foreach (var extra in mapping.ExtraSources)
                    {
                        if (extra != null) row.Sources.Add(extra.ToDomain());
                    }
                }
            }
        }

        /// <summary>
        /// Phase 2A merge: additively reconcile each slot's MappingSet
        /// with the per-device PadSetting fields. Adding a new device
        /// to a slot that already has authored mappings (e.g. keyboard
        /// custom mappings, then the user assigns a DualSense whose
        /// auto-mapper populates ButtonA="Button 0" / etc) used to
        /// CLOBBER the existing rows — the rebuilt rows came from
        /// PadSetting and the existing rows' primary sources got
        /// dropped because the gamepad-only migrator filter skipped
        /// non-gamepad devices.
        ///
        /// New behavior: existing rows are preserved as the source of
        /// truth (they already include the user's primary + extras
        /// after <see cref="PushUiExtraSourcesIntoSlotMappingSets"/>);
        /// rebuilt rows only contribute auto-mapped sources for
        /// newly-added devices that aren't already represented by a
        /// matching (DeviceGuid, Descriptor) entry. Sources whose
        /// owning device left the slot get dropped.
        /// </summary>
        /// <summary>Public hook so callers outside the save pipeline
        /// (e.g. device-assignment changes) can trigger the merge so
        /// newly-assigned devices' auto-mapped sources appear in the
        /// per-VC view without waiting for the next save / reload.</summary>
        public static void RefreshMappingSetsFromLegacy() => MergeMappingSetsFromLegacy();

        private static void MergeMappingSetsFromLegacy()
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || sets.Length == 0) return;

            UserSetting[] usSnapshot;
            lock (SettingsManager.UserSettings.SyncRoot)
                usSnapshot = SettingsManager.UserSettings.Items.ToArray();

            for (int slot = 0; slot < sets.Length; slot++)
            {
                var rebuilt = BuildOneSlotFromLegacy(slot);
                var current = sets[slot];

                // Devices currently assigned to this slot (lowercase
                // GUID strings) — used to drop sources for devices
                // that are no longer mapped here.
                var devGuidsInSlot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var us in usSnapshot)
                {
                    if (us != null && us.MapTo == slot)
                        devGuidsInSlot.Add(us.InstanceGuid.ToString().ToLowerInvariant());
                }

                if (current?.Rows == null)
                {
                    sets[slot] = rebuilt;
                    continue;
                }

                // Index rebuilt rows by (Target, LayerMask) so we can
                // quickly look up auto-mapped sources to add to each
                // existing row.
                var rebuiltByKey = new Dictionary<(string, string), Engine.Data.MappingRow>();
                foreach (var rr in rebuilt.Rows)
                {
                    if (rr == null) continue;
                    rebuiltByKey[(rr.Target ?? "", rr.LayerMask ?? "Base")] = rr;
                }

                var merged = new Engine.Data.MappingSet();
                var consumedRebuilt = new HashSet<(string, string)>();

                foreach (var er in current.Rows)
                {
                    if (er == null) continue;

                    // Drop sources for devices that left the slot.
                    if (er.Sources != null)
                    {
                        er.Sources.RemoveAll(s =>
                            !string.IsNullOrEmpty(s?.DeviceGuid)
                            && !devGuidsInSlot.Contains(s.DeviceGuid.ToLowerInvariant()));
                    }

                    var key = (er.Target ?? "", er.LayerMask ?? "Base");

                    // Only Base-layer rows merge with rebuilt; non-Base
                    // (Shift) rows carry forward intact.
                    if (string.Equals(er.LayerMask ?? "Base", "Base", StringComparison.Ordinal)
                        && rebuiltByKey.TryGetValue(key, out var rrow))
                    {
                        // Append any rebuilt sources not already
                        // present (dedup by DeviceGuid + Descriptor).
                        var seen = new HashSet<(string, string)>();
                        foreach (var s in er.Sources)
                        {
                            if (s == null) continue;
                            seen.Add(((s.DeviceGuid ?? "").ToLowerInvariant(), s.Descriptor ?? ""));
                        }
                        if (rrow.Sources != null)
                        {
                            foreach (var s in rrow.Sources)
                            {
                                if (s == null) continue;
                                var k = ((s.DeviceGuid ?? "").ToLowerInvariant(), s.Descriptor ?? "");
                                if (seen.Add(k)) er.Sources.Add(s);
                            }
                        }
                        consumedRebuilt.Add(key);
                    }

                    // Drop empty rows so the engine and UI fall back to
                    // the legacy PadSetting fields cleanly.
                    if (er.Sources == null || er.Sources.Count == 0) continue;

                    merged.Rows.Add(er);
                }

                // Add rebuilt rows that didn't match any existing row
                // (newly-added device whose auto-mapping introduces a
                // target the user hasn't authored yet).
                foreach (var rr in rebuilt.Rows)
                {
                    if (rr == null) continue;
                    var key = (rr.Target ?? "", rr.LayerMask ?? "Base");
                    if (consumedRebuilt.Contains(key)) continue;
                    if (rr.Sources == null || rr.Sources.Count == 0) continue;
                    merged.Rows.Add(rr);
                }

                if (current.ShiftButton != null)
                    merged.ShiftButton = current.ShiftButton;

                sets[slot] = merged;
            }
        }

        /// <summary>
        /// Phase 1b legacy entry point: rebuilds every slot's MappingSet
        /// from the per-(VC × Device) PadSetting mapping fields. Now used
        /// only as a "reset to legacy" path; ordinary loads route through
        /// <see cref="LoadOrMigrateSlotMappingSets"/>.
        /// </summary>
        private static void BuildSlotMappingSetsFromLegacy()
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || sets.Length == 0) return;

            // Snapshot the user-settings list under the lock so we can iterate
            // safely while the engine polls.
            UserSetting[] snapshot;
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                snapshot = SettingsManager.UserSettings.Items.ToArray();
            }

            for (int slot = 0; slot < sets.Length; slot++)
            {
                var devicesForSlot = new List<(string DeviceGuid, PadSetting PadSetting)>();
                foreach (var us in snapshot)
                {
                    if (us == null || us.MapTo != slot) continue;
                    var ps = us.GetPadSetting();
                    if (ps == null) continue;
                    devicesForSlot.Add((
                        us.InstanceGuid.ToString(),
                        ps));
                }

                sets[slot] = MappingSetMigrator.BuildFromLegacy(slot, devicesForSlot);
            }
        }

        /// <summary>
        /// Pushes application-level settings to the SettingsViewModel.
        /// </summary>
        private void LoadAppSettings(AppSettingsData appSettings)
        {
            var vm = _mainVm.Settings;
            vm.AutoStartEngine = appSettings.AutoStartEngine;
            vm.MinimizeToTray = appSettings.MinimizeToTray;
            vm.StartMinimized = appSettings.StartMinimized;
            vm.StartAtLogin = appSettings.StartAtLogin;
            vm.EnablePollingOnFocusLoss = appSettings.EnablePollingOnFocusLoss;
            vm.PollingRateMs = appSettings.PollingRateMs;
            vm.HmInactivityDestroyTimeoutSeconds = appSettings.HmInactivityDestroyTimeoutSeconds;
            vm.SelectedThemeIndex = appSettings.ThemeIndex;
            vm.EnableInputHiding = appSettings.EnableInputHiding;
            vm.KeepHidHideCloaksBetweenLaunches = appSettings.KeepHidHideCloaksBetweenLaunches;
            vm.HidHideWhitelistPaths.Clear();
            if (appSettings.HidHideWhitelistPaths != null)
            {
                foreach (var p in appSettings.HidHideWhitelistPaths)
                    if (!string.IsNullOrWhiteSpace(p))
                        vm.HidHideWhitelistPaths.Add(p);
            }
            vm.SetLanguageFromCode(appSettings.Language);
            vm.EnableAutoProfileSwitching = appSettings.EnableAutoProfileSwitching;
            SettingsManager.EnableAutoProfileSwitching = appSettings.EnableAutoProfileSwitching;
            SettingsManager.ActiveProfileId = appSettings.ActiveProfileId;
            // Migrate legacy global macros and store.
            if (appSettings.GlobalMacros != null)
                foreach (var gm in appSettings.GlobalMacros)
                    gm.MigrateLegacyTrigger();
            SettingsManager.GlobalMacros = appSettings.GlobalMacros;

            // Load per-slot created/enabled state BEFORE OutputType,
            // because setting OutputType fires PropertyChanged → RefreshNavControllerItems()
            // which reads SlotCreated[]. If SlotCreated isn't loaded yet, the sidebar
            // gets built with the wrong slot set and triggers a double-rebuild crash.
            if (appSettings.SlotCreated != null && appSettings.SlotCreated.Length >= 1)
            {
                int count = Math.Min(appSettings.SlotCreated.Length, SettingsManager.SlotCreated.Length);
                Array.Copy(appSettings.SlotCreated, SettingsManager.SlotCreated, count);
            }
            else
            {
                // Backward compat: auto-create slots for existing device assignments.
                AutoCreateSlotsFromExistingAssignments();
            }

            if (appSettings.SlotEnabled != null && appSettings.SlotEnabled.Length >= 1)
            {
                int count = Math.Min(appSettings.SlotEnabled.Length, SettingsManager.SlotEnabled.Length);
                Array.Copy(appSettings.SlotEnabled, SettingsManager.SlotEnabled, count);
            }
            // else: defaults are all true, which is correct for migration.

            // Load per-slot virtual controller types (after SlotCreated/SlotEnabled).
            if (appSettings.SlotControllerTypes != null)
            {
                for (int i = 0; i < _mainVm.Pads.Count && i < appSettings.SlotControllerTypes.Length; i++)
                {
                    // Only load types for created slots. Uncreated slots keep the
                    // default (Xbox) to prevent stale values from previous sessions
                    // leaking into the engine's SlotControllerTypes array.
                    if (SettingsManager.SlotCreated[i] &&
                        Enum.IsDefined(typeof(Engine.VirtualControllerType), appSettings.SlotControllerTypes[i]))
                        _mainVm.Pads[i].OutputType = (Engine.VirtualControllerType)appSettings.SlotControllerTypes[i];
                }
            }

            // Load per-slot HIDMaestro profile slugs (after OutputType so the
            // OutputType setter doesn't clear them via its category-change reset).
            if (appSettings.SlotProfileIds != null)
            {
                for (int i = 0; i < _mainVm.Pads.Count && i < appSettings.SlotProfileIds.Length; i++)
                {
                    if (SettingsManager.SlotCreated[i])
                        _mainVm.Pads[i].ProfileId = appSettings.SlotProfileIds[i];
                }
            }

            // Reconcile per-group order lists with the loaded topology. Pads
            // that the persisted lists reference but that are no longer
            // created (or that have changed types) are dropped; pads that
            // are created in a group but missing from the persisted list
            // (legacy XML, externally edited, etc.) are appended in
            // ascending pad-index order.
            SettingsManager.SlotOrders.RebuildFromCurrentTopology(
                pi => _mainVm.Pads[pi].OutputType,
                appSettings.XboxSlotOrder,
                appSettings.PlayStationSlotOrder,
                appSettings.ExtendedSlotOrder,
                appSettings.KeyboardMouseSlotOrder,
                appSettings.MidiSlotOrder);

            ApplyExtendedConfigs(appSettings.ExtendedConfigs);
            ApplyPlayStationConfigs(appSettings.PlayStationConfigs);
            ApplyMidiConfigs(appSettings.MidiConfigs);

            // Load DSU motion server settings (now on Dashboard VM).
            _mainVm.Dashboard.EnableDsuMotionServer = appSettings.EnableDsuMotionServer;
            _mainVm.Dashboard.DsuMotionServerPort = appSettings.DsuMotionServerPort > 0
                ? appSettings.DsuMotionServerPort : 26760;

            // Load web controller server settings.
            _mainVm.Dashboard.EnableWebController = appSettings.EnableWebController;
            _mainVm.Dashboard.WebControllerPort = appSettings.WebControllerPort > 0
                ? appSettings.WebControllerPort : 8080;

            // Load touchpad overlay settings.
            _mainVm.Dashboard.EnableTouchpadOverlay = appSettings.EnableTouchpadOverlay;
            _mainVm.Dashboard.TouchpadOverlayOpacity = appSettings.TouchpadOverlayOpacity;
            _mainVm.Dashboard.TouchpadOverlayMonitor = appSettings.TouchpadOverlayMonitor;
            _mainVm.Dashboard.TouchpadOverlayLeft = appSettings.TouchpadOverlayLeft;
            _mainVm.Dashboard.TouchpadOverlayTop = appSettings.TouchpadOverlayTop;
            _mainVm.Dashboard.TouchpadOverlayWidth = appSettings.TouchpadOverlayWidth > 0
                ? appSettings.TouchpadOverlayWidth : 500;
            _mainVm.Dashboard.TouchpadOverlayHeight = appSettings.TouchpadOverlayHeight > 0
                ? appSettings.TouchpadOverlayHeight : 250;

            vm.Use2DControllerView = appSettings.Use2DControllerView;
            vm.LegacyDriverCleanupOffered = appSettings.LegacyDriverCleanupOffered;

            // Restore main window position/size (profile-independent).
            vm.MainWindowLeft = appSettings.MainWindowLeft;
            vm.MainWindowTop = appSettings.MainWindowTop;
            vm.MainWindowWidth = appSettings.MainWindowWidth > 0 ? appSettings.MainWindowWidth : 1100;
            vm.MainWindowHeight = appSettings.MainWindowHeight > 0 ? appSettings.MainWindowHeight : 720;
            vm.MainWindowState = appSettings.MainWindowState;
            vm.MainWindowFullScreen = appSettings.MainWindowFullScreen;
        }

        /// <summary>
        /// Applies per-slot Extended configurations.
        /// Only restores configs for slots that are currently created as Extended.
        /// </summary>
        private void ApplyExtendedConfigs(ViewModels.ExtendedSlotConfigData[] configs)
        {
            if (configs == null) return;
            foreach (var cfgData in configs)
            {
                int idx = cfgData.SlotIndex;
                if (idx >= 0 && idx < _mainVm.Pads.Count &&
                    SettingsManager.SlotCreated[idx] &&
                    _mainVm.Pads[idx].OutputType == Engine.VirtualControllerType.Extended)
                {
                    var cfg = _mainVm.Pads[idx].ExtendedConfig;
                    cfg.ThumbstickCount = cfgData.ThumbstickCount;
                    cfg.TriggerCount = cfgData.TriggerCount;
                    cfg.PovCount = cfgData.PovCount;
                    cfg.ButtonCount = cfgData.ButtonCount;
                    cfg.OemNameOverride = cfgData.OemNameOverride;
                    cfg.ProductString = cfgData.ProductString ?? string.Empty;
                    cfg.Customize = cfgData.Customize;
                    cfg.ForceFeedbackEnabled = cfgData.ForceFeedbackEnabled;
                }
            }
        }

        /// <summary>Applies per-slot PlayStation configurations (Adaptive
        /// Triggers + Lighting). Only restores configs for slots that
        /// are currently created as PlayStation.</summary>
        private void ApplyPlayStationConfigs(ViewModels.PlayStationSlotConfigData[] configs)
        {
            if (configs == null) return;

            // Pre-scan: which slots already carry per-device entries in
            // the saved config. v3.1+ saves write one entry per (slot,
            // device GUID) pair plus a slot-level "anchor" entry (with
            // DeviceGuid = Empty) for legacy display fallback. The
            // anchor mirrors whatever device was selected at save time,
            // so fanning it out across every per-device config (the
            // pre-v3.1 model) corrupts other devices' settings — most
            // visibly, it spreads InputReactiveMode = Random from the
            // selected DualSense to every other Sony pad on the slot
            // even though Lighting is per-device. Only fan out when
            // there are NO per-device entries (genuine pre-v3.1 save).
            var slotsWithPerDeviceEntries = new System.Collections.Generic.HashSet<int>();
            foreach (var cfgData in configs)
            {
                if (cfgData.DeviceGuid != Guid.Empty)
                    slotsWithPerDeviceEntries.Add(cfgData.SlotIndex);
            }

            foreach (var cfgData in configs)
            {
                int idx = cfgData.SlotIndex;
                if (idx < 0 || idx >= _mainVm.Pads.Count) continue;
                // Skip entries for uncreated slots. Older saves (or
                // pre-2026-05-04 builds before DeleteSlot cleared the
                // per-device dictionary) can carry stale entries for
                // slots that have since been deleted; loading them
                // would resurrect the prior lightbar mode / colors /
                // palette as soon as the user remaps the same physical
                // device to a freshly-created slot at the same index.
                if (!SettingsManager.SlotCreated[idx]) continue;
                var padVm = _mainVm.Pads[idx];

                // Per-device entry — apply to that device's per-device
                // PlayStationSlotConfig only. The Lighting tab is
                // per-device, so two pads on one slot legitimately
                // carry different lightbar / overlay state.
                if (cfgData.DeviceGuid != Guid.Empty)
                {
                    var devCfg = padVm.GetOrCreatePlayStationConfig(cfgData.DeviceGuid);
                    if (devCfg != null)
                        ApplyPlayStationConfigData(devCfg, cfgData);
                    continue;
                }

                // Slot-level entry — always apply to the anchor so the
                // Lighting tab shows something reasonable before any
                // device is selected. Fan out to per-device configs
                // ONLY when this slot has zero per-device entries (a
                // pre-v3.1 save where lighting was slot-wide); v3.1+
                // saves are authoritative per-device.
                if (padVm.PlayStationConfig != null)
                    ApplyPlayStationConfigData(padVm.PlayStationConfig, cfgData);
                if (!slotsWithPerDeviceEntries.Contains(idx))
                {
                    foreach (var devCfg in padVm.PerDevicePlayStationConfigs.Values)
                    {
                        if (devCfg != null && !ReferenceEquals(devCfg, padVm.PlayStationConfig))
                            ApplyPlayStationConfigData(devCfg, cfgData);
                    }
                }
            }
        }

        /// <summary>Writes the saved DTO fields into a single
        /// PlayStationSlotConfig instance. Extracted so the loader can
        /// call it once per per-device entry, or once per slot when
        /// fanning out a legacy slot-level entry to every device.</summary>
        private static void ApplyPlayStationConfigData(ViewModels.PlayStationSlotConfig cfg, ViewModels.PlayStationSlotConfigData cfgData)
        {
            if (cfg == null) return;
                    cfg.LeftTriggerMode = cfgData.LeftTriggerMode;
                    cfg.RightTriggerMode = cfgData.RightTriggerMode;
                    cfg.LeftStartPosition = cfgData.LeftStartPosition;
                    cfg.LeftEndPosition = cfgData.LeftEndPosition;
                    cfg.LeftStrength = cfgData.LeftStrength;
                    cfg.LeftFrequency = cfgData.LeftFrequency;
                    cfg.RightStartPosition = cfgData.RightStartPosition;
                    cfg.RightEndPosition = cfgData.RightEndPosition;
                    cfg.RightStrength = cfgData.RightStrength;
                    cfg.RightFrequency = cfgData.RightFrequency;
                    cfg.LightbarRed = cfgData.LightbarRed;
                    cfg.LightbarGreen = cfgData.LightbarGreen;
                    cfg.LightbarBlue = cfgData.LightbarBlue;
                    cfg.LightbarEnabled = cfgData.LightbarEnabled;
                    // Migrate legacy MicLightOn to the new MicLedMode if
                    // the new field hasn't been set explicitly.
                    if (cfgData.MicLedMode != ViewModels.MicLedMode.Off)
                        cfg.MicLedMode = cfgData.MicLedMode;
                    else
                        cfg.MicLightOn = cfgData.MicLightOn;
                    cfg.MicLedFollowDeviceId = cfgData.MicLedFollowDeviceId ?? string.Empty;
                    cfg.PlayerLedMode = cfgData.PlayerLedMode;
                    cfg.PlayerLedBrightness = cfgData.PlayerLedBrightness;
                    cfg.AudioLightbarEnabled = cfgData.AudioLightbarEnabled;
                    cfg.AudioLightbarSensitivity = cfgData.AudioLightbarSensitivity;
                    cfg.AudioLightbarMode = cfgData.AudioLightbarMode;
                    cfg.AudioLowR = cfgData.AudioLowR;
                    cfg.AudioLowG = cfgData.AudioLowG;
                    cfg.AudioLowB = cfgData.AudioLowB;
                    cfg.AudioMidR = cfgData.AudioMidR;
                    cfg.AudioMidG = cfgData.AudioMidG;
                    cfg.AudioMidB = cfgData.AudioMidB;
                    cfg.AudioHighR = cfgData.AudioHighR;
                    cfg.AudioHighG = cfgData.AudioHighG;
                    cfg.AudioHighB = cfgData.AudioHighB;
                    cfg.AudioLowToMidPercent = cfgData.AudioLowToMidPercent;
                    cfg.AudioMidToHighPercent = cfgData.AudioMidToHighPercent;
                    cfg.AudioCrossFadePercent = cfgData.AudioCrossFadePercent;

                    // Unified lightbar mode (v3.1.0+). Migrate from the
                    // legacy bools when the saved value is at the default.
                    cfg.LightbarMode = cfgData.LightbarMode != ViewModels.LightbarMode.Off
                        ? cfgData.LightbarMode
                        : cfgData.AudioLightbarEnabled
                            ? cfgData.AudioLightbarMode switch
                            {
                                ViewModels.AudioLightbarMode.Pulse      => ViewModels.LightbarMode.AudioPulse,
                                ViewModels.AudioLightbarMode.Thresholds => ViewModels.LightbarMode.AudioThresholds,
                                ViewModels.AudioLightbarMode.Gradient   => ViewModels.LightbarMode.AudioGradient,
                                ViewModels.AudioLightbarMode.CrossFade  => ViewModels.LightbarMode.AudioCrossFade,
                                _                                       => ViewModels.LightbarMode.AudioPulse,
                            }
                            : cfgData.LightbarEnabled
                                ? ViewModels.LightbarMode.Static
                                : ViewModels.LightbarMode.Off;
                    cfg.LightbarPeriodMs = cfgData.LightbarPeriodMs;
                    cfg.LightbarColorCycleSmooth = cfgData.LightbarColorCycleSmooth;
                    cfg.LightbarRainbowBrightness = cfgData.LightbarRainbowBrightness;
                    cfg.LightbarBatteryLowR  = cfgData.LightbarBatteryLowR;
                    cfg.LightbarBatteryLowG  = cfgData.LightbarBatteryLowG;
                    cfg.LightbarBatteryLowB  = cfgData.LightbarBatteryLowB;
                    cfg.LightbarBatteryHighR = cfgData.LightbarBatteryHighR;
                    cfg.LightbarBatteryHighG = cfgData.LightbarBatteryHighG;
                    cfg.LightbarBatteryHighB = cfgData.LightbarBatteryHighB;
                    if (cfgData.LightbarPalette != null && cfgData.LightbarPalette.Length > 0)
                    {
                        cfg.ReplaceLightbarPalette(cfgData.LightbarPalette
                            .Select(e => new ViewModels.LightbarPaletteEntry(e.R, e.G, e.B)));
                    }
                    cfg.LightbarInputHoldMs = cfgData.LightbarInputHoldMs;
                    cfg.LightbarInputDecayMs = cfgData.LightbarInputDecayMs;
                    cfg.InputReactiveR = cfgData.InputReactiveR;
                    cfg.InputReactiveG = cfgData.InputReactiveG;
                    cfg.InputReactiveB = cfgData.InputReactiveB;

                    // v3.1.0 migration: the LightbarInputRandomize bool used
                    // to gate cycle-vs-random in the InputReactive mode; now
                    // each is its own LightbarMode entry. Old saves with the
                    // bool at false flip to InputReactiveCycle.
                    if (cfg.LightbarMode == ViewModels.LightbarMode.InputReactive
                        && !cfgData.LightbarInputRandomize)
                    {
                        cfg.LightbarMode = ViewModels.LightbarMode.InputReactiveCycle;
                    }

                    // v3.2 migration: the InputReactive* values used to be
                    // base-mode entries; they are now an OVERLAY layered on
                    // top of the base. If the saved file already carries a
                    // non-Off InputReactiveMode, honor it and leave the base
                    // alone. Otherwise, translate any legacy InputReactive*
                    // base-mode value into (LightbarMode = Off, overlay =
                    // corresponding) so the visual result matches what users
                    // had before — a black base with a reactive flash. Users
                    // can re-pick a richer base mode under the overlay later.
                    if (cfgData.InputReactiveMode != ViewModels.InputReactiveMode.Off)
                    {
                        cfg.InputReactiveMode = cfgData.InputReactiveMode;
                    }
                    else
                    {
                        switch (cfg.LightbarMode)
                        {
                            case ViewModels.LightbarMode.InputReactive:
                                cfg.InputReactiveMode = ViewModels.InputReactiveMode.Random;
                                cfg.LightbarMode = ViewModels.LightbarMode.Off;
                                break;
                            case ViewModels.LightbarMode.InputReactiveCycle:
                                cfg.InputReactiveMode = ViewModels.InputReactiveMode.Cycle;
                                cfg.LightbarMode = ViewModels.LightbarMode.Off;
                                break;
                            case ViewModels.LightbarMode.InputReactiveFixed:
                                cfg.InputReactiveMode = ViewModels.InputReactiveMode.Fixed;
                                // Legacy InputReactiveFixed flashed
                                // LightbarRed/G/B; preserve that color
                                // intent on the new dedicated overlay
                                // RGB so the per-press flash keeps its
                                // configured color across migration.
                                cfg.InputReactiveR = cfgData.LightbarRed;
                                cfg.InputReactiveG = cfgData.LightbarGreen;
                                cfg.InputReactiveB = cfgData.LightbarBlue;
                                cfg.LightbarMode = ViewModels.LightbarMode.Off;
                                break;
                        }
                    }
        }

        /// <summary>
        /// Applies per-slot MIDI configurations.
        /// Only restores configs for slots that are currently created as MIDI.
        /// </summary>
        private void ApplyMidiConfigs(ViewModels.MidiSlotConfigData[] configs)
        {
            if (configs == null) return;
            foreach (var cfgData in configs)
            {
                int idx = cfgData.SlotIndex;
                if (idx >= 0 && idx < _mainVm.Pads.Count &&
                    SettingsManager.SlotCreated[idx] &&
                    _mainVm.Pads[idx].OutputType == Engine.VirtualControllerType.Midi)
                {
                    var cfg = _mainVm.Pads[idx].MidiConfig;
                    cfg.Channel = cfgData.Channel;
                    cfg.Velocity = cfgData.Velocity;
                    cfg.StartCc = cfgData.StartCc;
                    cfg.CcCount = cfgData.CcCount;
                    cfg.StartNote = cfgData.StartNote;
                    cfg.NoteCount = cfgData.NoteCount;
                    _mainVm.Pads[idx].RebuildMappings();

                    lock (SettingsManager.UserSettings.SyncRoot)
                    {
                        foreach (var us in SettingsManager.UserSettings.Items)
                        {
                            if (us.MapTo != idx) continue;
                            var ps = us.GetPadSetting();
                            if (ps == null) continue;
                            foreach (var mapping in _mainVm.Pads[idx].Mappings)
                            {
                                string target = mapping.TargetSettingName;
                                string value = target.StartsWith("Midi", StringComparison.Ordinal)
                                    ? ps.GetMidiMapping(target) : string.Empty;
                                if (!string.IsNullOrEmpty(value))
                                    mapping.LoadDescriptor(value);
                                if (mapping.NegSettingName != null)
                                {
                                    string negValue = mapping.NegSettingName.StartsWith("Midi", StringComparison.Ordinal)
                                        ? ps.GetMidiMapping(mapping.NegSettingName) : string.Empty;
                                    if (!string.IsNullOrEmpty(negValue))
                                        mapping.LoadNegDescriptor(negValue);
                                }
                            }
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// For old settings files without SlotCreated: creates slots for any
        /// indices that have device assignments.
        /// </summary>
        private static void AutoCreateSlotsFromExistingAssignments()
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    int idx = us.MapTo;
                    if (idx >= 0 && idx < InputManager.MaxPads)
                    {
                        SettingsManager.SlotCreated[idx] = true;
                        SettingsManager.SlotEnabled[idx] = true;
                    }
                }
            }
        }

        /// <summary>
        /// Pushes per-pad settings to PadViewModels.
        /// Only loads the first device encountered per slot — the user can switch
        /// to other devices via the dropdown, which triggers a live swap.
        /// </summary>
        private void LoadPadSettings(UserSetting[] settings, PadSetting[] padSettings)
        {
            if (settings == null || padSettings == null)
                return;

            var loadedSlots = new System.Collections.Generic.HashSet<int>();

            foreach (var us in settings)
            {
                int padIndex = us.MapTo;
                if (padIndex < 0 || padIndex >= _mainVm.Pads.Count)
                    continue;

                // Only load the first device's PadSetting into the ViewModel per slot.
                if (!loadedSlots.Add(padIndex))
                    continue;

                var padVm = _mainVm.Pads[padIndex];
                var ps = us.GetPadSetting();
                if (ps == null)
                    continue;

                // Load force feedback settings.
                padVm.ForceOverallGain = TryParseInt(ps.ForceOverall, 100);
                padVm.LeftMotorStrength = TryParseInt(ps.LeftMotorStrength, 100);
                padVm.RightMotorStrength = TryParseInt(ps.RightMotorStrength, 100);
                padVm.SwapMotors = ps.ForceSwapMotor == "1" ||
                    (ps.ForceSwapMotor ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);

                // Load audio bass rumble settings.
                padVm.AudioRumbleEnabled = ps.AudioRumbleEnabled == "1";
                padVm.AudioRumbleSensitivity = TryParseDouble(ps.AudioRumbleSensitivity, 4.0);
                padVm.AudioRumbleCutoffHz = TryParseDouble(ps.AudioRumbleCutoffHz, 80.0);
                padVm.AudioRumbleLeftMotor = TryParseInt(ps.AudioRumbleLeftMotor, 100);
                padVm.AudioRumbleRightMotor = TryParseInt(ps.AudioRumbleRightMotor, 100);

                // Load constant force settings.
                padVm.ConstantForceEnabled = ps.ConstantForceEnabled == "1";
                padVm.ConstantForceX = TryParseDouble(ps.ConstantForceX, 0.0);
                padVm.ConstantForceY = TryParseDouble(ps.ConstantForceY, 0.0);

                // Load deadzone settings (independent X/Y).
                padVm.LeftDeadZoneShape = (int)InputManager.ParseDeadZoneShape(ps.LeftThumbDeadZoneShape);
                padVm.LeftDeadZoneX = TryParseDouble(ps.LeftThumbDeadZoneX, 0);
                padVm.LeftDeadZoneY = TryParseDouble(ps.LeftThumbDeadZoneY, 0);
                padVm.RightDeadZoneShape = (int)InputManager.ParseDeadZoneShape(ps.RightThumbDeadZoneShape);
                padVm.RightDeadZoneX = TryParseDouble(ps.RightThumbDeadZoneX, 0);
                padVm.RightDeadZoneY = TryParseDouble(ps.RightThumbDeadZoneY, 0);
                ps.MigrateAntiDeadZones();
                padVm.LeftAntiDeadZoneX = TryParseDouble(ps.LeftThumbAntiDeadZoneX, 0);
                padVm.LeftAntiDeadZoneY = TryParseDouble(ps.LeftThumbAntiDeadZoneY, 0);
                padVm.RightAntiDeadZoneX = TryParseDouble(ps.RightThumbAntiDeadZoneX, 0);
                padVm.RightAntiDeadZoneY = TryParseDouble(ps.RightThumbAntiDeadZoneY, 0);
                padVm.LeftLinear = TryParseDouble(ps.LeftThumbLinear, 0);
                padVm.RightLinear = TryParseDouble(ps.RightThumbLinear, 0);
                padVm.LeftSensitivityCurveX = ps.LeftThumbSensitivityCurveX ?? "0,0;1,1";
                padVm.LeftSensitivityCurveY = ps.LeftThumbSensitivityCurveY ?? "0,0;1,1";
                padVm.RightSensitivityCurveX = ps.RightThumbSensitivityCurveX ?? "0,0;1,1";
                padVm.RightSensitivityCurveY = ps.RightThumbSensitivityCurveY ?? "0,0;1,1";
                padVm.LeftTriggerSensitivityCurve = ps.LeftTriggerSensitivityCurve ?? "0,0;1,1";
                padVm.RightTriggerSensitivityCurve = ps.RightTriggerSensitivityCurve ?? "0,0;1,1";
                padVm.LeftMaxRangeX = TryParseDouble(ps.LeftThumbMaxRangeX, 100);
                padVm.LeftMaxRangeY = TryParseDouble(ps.LeftThumbMaxRangeY, 100);
                padVm.RightMaxRangeX = TryParseDouble(ps.RightThumbMaxRangeX, 100);
                padVm.RightMaxRangeY = TryParseDouble(ps.RightThumbMaxRangeY, 100);
                ps.MigrateMaxRangeDirections();
                padVm.LeftMaxRangeXNeg = TryParseDouble(ps.LeftThumbMaxRangeXNeg, 100);
                padVm.LeftMaxRangeYNeg = TryParseDouble(ps.LeftThumbMaxRangeYNeg, 100);
                padVm.RightMaxRangeXNeg = TryParseDouble(ps.RightThumbMaxRangeXNeg, 100);
                padVm.RightMaxRangeYNeg = TryParseDouble(ps.RightThumbMaxRangeYNeg, 100);
                padVm.LeftCenterOffsetX = TryParseDouble(ps.LeftThumbCenterOffsetX, 0);
                padVm.LeftCenterOffsetY = TryParseDouble(ps.LeftThumbCenterOffsetY, 0);
                padVm.RightCenterOffsetX = TryParseDouble(ps.RightThumbCenterOffsetX, 0);
                padVm.RightCenterOffsetY = TryParseDouble(ps.RightThumbCenterOffsetY, 0);

                // Load trigger deadzone settings.
                padVm.LeftTriggerDeadZone = TryParseDouble(ps.LeftTriggerDeadZone, 0);
                padVm.RightTriggerDeadZone = TryParseDouble(ps.RightTriggerDeadZone, 0);
                padVm.LeftTriggerAntiDeadZone = TryParseDouble(ps.LeftTriggerAntiDeadZone, 0);
                padVm.RightTriggerAntiDeadZone = TryParseDouble(ps.RightTriggerAntiDeadZone, 0);
                padVm.LeftTriggerMaxRange = TryParseDouble(ps.LeftTriggerMaxRange, 100);
                padVm.RightTriggerMaxRange = TryParseDouble(ps.RightTriggerMaxRange, 100);

                // Sync dynamic stick/trigger config items from the loaded VM properties.
                padVm.SyncAllConfigItemsFromVm();

                // Load Extended custom stick/trigger settings for indices 2+ from dictionary.
                foreach (var stick in padVm.StickConfigs)
                {
                    if (stick.Index < 2) continue;
                    int g = stick.Index;
                    stick.DeadZoneShape = InputManager.ParseDeadZoneShape(ps.GetExtendedMapping($"ExtendedStick{g}DzShape"));
                    stick.DeadZoneX = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}DzX"), 0);
                    stick.DeadZoneY = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}DzY"), 0);
                    stick.AntiDeadZoneX = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}AdzX"), 0);
                    stick.AntiDeadZoneY = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}AdzY"), 0);
                    stick.Linear = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}Linear"), 0);
                    stick.SensitivityCurveX = ps.GetExtendedMapping($"ExtendedStick{g}CurveX") ?? "0,0;1,1";
                    stick.SensitivityCurveY = ps.GetExtendedMapping($"ExtendedStick{g}CurveY") ?? "0,0;1,1";
                    stick.CenterOffsetX = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}CofX"), 0);
                    stick.CenterOffsetY = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}CofY"), 0);
                    stick.MaxRangeX = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}MrX"), 100);
                    stick.MaxRangeY = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}MrY"), 100);
                    stick.MaxRangeXNeg = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}MrXN"), stick.MaxRangeX);
                    stick.MaxRangeYNeg = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}MrYN"), stick.MaxRangeY);
                }
                foreach (var trig in padVm.TriggerConfigs)
                {
                    if (trig.Index < 2) continue;
                    int g = trig.Index;
                    trig.DeadZone = TryParseDouble(ps.GetExtendedMapping($"ExtendedTrigger{g}Dz"), 0);
                    trig.AntiDeadZone = TryParseDouble(ps.GetExtendedMapping($"ExtendedTrigger{g}Adz"), 0);
                    trig.MaxRange = TryParseDouble(ps.GetExtendedMapping($"ExtendedTrigger{g}Mr"), 100);
                    trig.SensitivityCurve = ps.GetExtendedMapping($"ExtendedTrigger{g}Curve") ?? "0,0;1,1";
                }

                // Load mapping descriptors into mapping rows.
                LoadMappingDescriptors(padVm, ps);
            }
        }

        /// <summary>
        /// Populates PadViewModel mapping rows from a PadSetting's descriptor strings.
        /// </summary>
        private static void LoadMappingDescriptors(PadViewModel padVm, PadSetting ps)
        {
            foreach (var mapping in padVm.Mappings)
            {
                string value = GetPadSettingProperty(ps, mapping.TargetSettingName);
                mapping.SourceDescriptor = value ?? string.Empty;

                if (mapping.NegSettingName != null)
                {
                    string negValue = GetPadSettingProperty(ps, mapping.NegSettingName);
                    mapping.NegSourceDescriptor = negValue ?? string.Empty;
                }

                // Load per-mapping deadzone.
                string dzStr = ps.GetMappingDeadZone(mapping.TargetSettingName);
                mapping.MappingDeadZone = int.TryParse(dzStr, out int dz) && dz > 0 ? dz : 50;
            }
        }

        /// <summary>
        /// Populates pad ViewModels with macros from serialized data.
        /// </summary>
        private void LoadMacros(MacroData[] macros)
        {
            // Clear existing macros on all pads.
            foreach (var pad in _mainVm.Pads)
                pad.Macros.Clear();

            foreach (var md in macros)
            {
                if (md.PadIndex < 0 || md.PadIndex >= _mainVm.Pads.Count)
                    continue;

                var padVm = _mainVm.Pads[md.PadIndex];
                var macro = new MacroItem
                {
                    PadIndex = md.PadIndex,
                    Name = md.Name ?? "Macro",
                    IsEnabled = md.IsEnabled,
                    TriggerButtons = md.TriggerButtons,
                    TriggerCustomButtons = md.TriggerCustomButtons,
                    TriggerDeviceGuid = Guid.TryParse(md.TriggerDeviceGuid, out var parsedGuid)
                        ? parsedGuid : Guid.Empty,
                    TriggerRawButtons = ParseRawButtonIndices(md.TriggerRawButtons),
                    TriggerSource = md.TriggerSource,
                    TriggerMode = md.TriggerMode,
                    ConsumeTriggerButtons = md.ConsumeTriggerButtons,
                    RepeatMode = md.RepeatMode,
                    RepeatCount = md.RepeatCount,
                    RepeatDelayMs = md.RepeatDelayMs,
                    TriggerAxisTargetList = md.TriggerAxisTargets,
                    TriggerAxisThreshold = md.TriggerAxisThreshold > 0 ? md.TriggerAxisThreshold : 50,
                    TriggerPovs = md.TriggerPovs ?? Array.Empty<string>()
                };

                if (md.Actions != null)
                {
                    foreach (var ad in md.Actions)
                    {
                        macro.Actions.Add(new MacroAction
                        {
                            Type = ad.Type,
                            ButtonFlags = ad.ButtonFlags,
                            CustomButtons = ad.CustomButtons,
                            KeyCode = ad.KeyCode,
                            KeyString = !string.IsNullOrEmpty(ad.KeyString)
                                ? ad.KeyString
                                : (ad.KeyCode != 0 ? $"{{{(VirtualKey)ad.KeyCode}}}" : ""),
                            DurationMs = ad.DurationMs,
                            AxisValue = ad.AxisValue,
                            AxisTarget = ad.AxisTarget,
                            AxisSource = ad.AxisSource,
                            SourceDeviceGuid = Guid.TryParse(ad.SourceDeviceGuid, out var devGuid)
                                ? devGuid : Guid.Empty,
                            SourceDeviceAxisIndex = ad.SourceDeviceAxisIndex,
                            ProcessName = ad.ProcessName ?? "",
                            VolumeLimit = ad.VolumeLimit > 0 ? ad.VolumeLimit : 100,
                            MouseSensitivity = ad.MouseSensitivity > 0 ? ad.MouseSensitivity : 10f,
                            MouseButton = ad.MouseButton,
                            InvertAxis = ad.InvertAxis,
                            ShowVolumeOsd = ad.ShowVolumeOsd,
                            LightbarR = ad.LightbarR,
                            LightbarG = ad.LightbarG,
                            LightbarB = ad.LightbarB,
                            LightbarHoldMode = ad.LightbarHoldMode,
                            LightbarColorSource = ad.LightbarColorSource,
                            LightbarHoldMs = Math.Clamp(ad.LightbarHoldMs, 0, 5000),
                            LightbarFadeMs = Math.Clamp(ad.LightbarFadeMs, 0, 5000),
                            LightbarPaletteCsv = ad.LightbarPaletteCsv ?? string.Empty,
                            LightbarTargetMode = ad.LightbarTargetMode,
                            LightbarCycleModesCsv = ad.LightbarCycleModesCsv
                        });
                    }
                }

                // Set after actions are populated so propagation reaches all of them.
                var style = MacroButtonNames.DeriveStyle(padVm.OutputType);
                int btnCount = (padVm.OutputType == VirtualControllerType.Extended ? padVm.ExtendedConfig?.ButtonCount : null) ?? 11;
                macro.CustomButtonCount = btnCount;
                macro.ButtonStyle = style;
                foreach (var action in macro.Actions)
                    action.CustomButtonCount = btnCount;

                padVm.Macros.Add(macro);
            }
        }

        /// <summary>
        /// Parses a comma-separated string of button indices (e.g. "13,14") into an int array.
        /// Returns empty array for null/empty input.
        /// </summary>
        private static int[] ParseRawButtonIndices(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<int>();
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = new System.Collections.Generic.List<int>(parts.Length);
            foreach (var part in parts)
            {
                if (int.TryParse(part, out int idx))
                    result.Add(idx);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Loads profiles from serialized data into SettingsManager and the ViewModel.
        /// </summary>
        private void LoadProfiles(ProfileData[] profiles, AppSettingsData appSettings)
        {
            SettingsManager.Profiles.Clear();
            _mainVm.Settings.ProfileItems.Clear();

            // Always include the built-in Default profile at the top.
            var defaultItem = new ViewModels.ProfileListItem
            {
                Id = ViewModels.ProfileListItem.DefaultProfileId,
                Name = Strings.Instance.Profile_Default,
            };
            var slotTypes = Enumerable.Range(0, SettingsManager.SlotCreated.Length)
                .Select(i => i < _mainVm.Pads.Count ? (int)_mainVm.Pads[i].OutputType : 0).ToArray();
            UpdateTopologyCounts(defaultItem, SettingsManager.SlotCreated, slotTypes);
            _mainVm.Settings.ProfileItems.Add(defaultItem);

            if (profiles != null)
            {
                foreach (var p in profiles)
                {
                    SettingsManager.Profiles.Add(p);
                    var item = new ViewModels.ProfileListItem
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Executables = InputService.FormatExePaths(p.ExecutableNames),
                    };
                    UpdateTopologyCounts(item, p.SlotCreated, p.SlotControllerTypes);
                    _mainVm.Settings.ProfileItems.Add(item);
                }
            }

            // Update active profile display.
            string activeId = appSettings?.ActiveProfileId;
            var active = SettingsManager.Profiles.Find(p => p.Id == activeId);
            _mainVm.Settings.ActiveProfileInfo = active?.Name ?? Strings.Instance.Profile_Default;

            // If a named profile was active at shutdown, snapshot the default
            // profile's state (loaded by LoadAppSettings) before overwriting with
            // the active profile's topology. InputService.Start uses this snapshot
            // so switching back to Default restores the correct state.
            if (active != null)
            {
                // Restore the default profile snapshot from the XML. This was
                // persisted by BuildAppSettings when a named profile was active,
                // and contains the default's full state (slots, device assignments,
                // configs). The runtime state at this point has the named profile's
                // device assignments (loaded by LoadPadSettings), so we can't build
                // the default snapshot from runtime — it must come from the XML.
                SettingsManager.PendingDefaultSnapshot = appSettings?.DefaultProfileSnapshot;

                if (active.SlotCreated != null)
                {
                    int count = Math.Min(active.SlotCreated.Length, SettingsManager.SlotCreated.Length);
                    Array.Copy(active.SlotCreated, SettingsManager.SlotCreated, count);
                }

                if (active.SlotEnabled != null)
                {
                    int count = Math.Min(active.SlotEnabled.Length, SettingsManager.SlotEnabled.Length);
                    Array.Copy(active.SlotEnabled, SettingsManager.SlotEnabled, count);
                }

                if (active.SlotControllerTypes != null)
                {
                    for (int i = 0; i < _mainVm.Pads.Count && i < active.SlotControllerTypes.Length; i++)
                    {
                        if (SettingsManager.SlotCreated[i] &&
                            Enum.IsDefined(typeof(Engine.VirtualControllerType), active.SlotControllerTypes[i]))
                            _mainVm.Pads[i].OutputType = (Engine.VirtualControllerType)active.SlotControllerTypes[i];
                    }
                }

                if (active.SlotProfileIds != null)
                {
                    for (int i = 0; i < _mainVm.Pads.Count && i < active.SlotProfileIds.Length; i++)
                    {
                        if (SettingsManager.SlotCreated[i])
                            _mainVm.Pads[i].ProfileId = active.SlotProfileIds[i];
                    }
                }

                // Reconcile per-group order lists from the profile's saved
                // arrays against the just-applied topology. Same shape as the
                // app-load reconcile above.
                SettingsManager.SlotOrders.RebuildFromCurrentTopology(
                    pi => _mainVm.Pads[pi].OutputType,
                    active.XboxSlotOrder,
                    active.PlayStationSlotOrder,
                    active.ExtendedSlotOrder,
                    active.KeyboardMouseSlotOrder,
                    active.MidiSlotOrder);

                // Now that SlotCreated and OutputType are restored, apply Extended/MIDI/PlayStation
                // configs from the profile's own snapshot.
                ApplyExtendedConfigs(active.ExtendedConfigs);
                ApplyPlayStationConfigs(active.PlayStationConfigs);
                ApplyMidiConfigs(active.MidiConfigs);

                // Apply DSU/Web/overlay settings from the active profile.
                _mainVm.Dashboard.EnableDsuMotionServer = active.EnableDsuMotionServer;
                if (active.DsuMotionServerPort >= 1024 && active.DsuMotionServerPort <= 65535)
                    _mainVm.Dashboard.DsuMotionServerPort = active.DsuMotionServerPort;
                _mainVm.Dashboard.EnableWebController = active.EnableWebController;
                if (active.WebControllerPort >= 1024 && active.WebControllerPort <= 65535)
                    _mainVm.Dashboard.WebControllerPort = active.WebControllerPort;
                _mainVm.Dashboard.EnableTouchpadOverlay = active.EnableTouchpadOverlay;
                _mainVm.Dashboard.TouchpadOverlayOpacity = active.TouchpadOverlayOpacity;
                _mainVm.Dashboard.TouchpadOverlayMonitor = active.TouchpadOverlayMonitor;
                _mainVm.Dashboard.TouchpadOverlayLeft = active.TouchpadOverlayLeft;
                _mainVm.Dashboard.TouchpadOverlayTop = active.TouchpadOverlayTop;
                _mainVm.Dashboard.TouchpadOverlayWidth = active.TouchpadOverlayWidth > 0
                    ? active.TouchpadOverlayWidth : 500;
                _mainVm.Dashboard.TouchpadOverlayHeight = active.TouchpadOverlayHeight > 0
                    ? active.TouchpadOverlayHeight : 250;
            }
        }

        /// <summary>
        /// If a profile is currently active, updates its stored snapshot from
        /// the current runtime state so that edits made while the profile was
        /// active are persisted back to it. Called during Save after checksums
        /// have been recomputed.
        /// </summary>
        private void UpdateActiveProfileSnapshot()
        {
            string activeId = SettingsManager.ActiveProfileId;
            if (string.IsNullOrEmpty(activeId))
                return;

            var profile = SettingsManager.Profiles.Find(p => p.Id == activeId);
            if (profile == null)
                return;

            var entries = new System.Collections.Generic.List<ProfileEntry>();
            var padSettings = new System.Collections.Generic.List<PadSetting>();
            var seen = new System.Collections.Generic.HashSet<string>();

            lock (SettingsManager.UserSettings.SyncRoot)
            {
                foreach (var us in SettingsManager.UserSettings.Items)
                {
                    var ps = us.GetPadSetting();
                    if (ps == null) continue;

                    entries.Add(new ProfileEntry
                    {
                        InstanceGuid = us.InstanceGuid,
                        ProductGuid = us.ProductGuid,
                        MapTo = us.MapTo,
                        PadSettingChecksum = ps.PadSettingChecksum
                    });

                    if (seen.Add(ps.PadSettingChecksum))
                        padSettings.Add(ps.CloneDeep());
                }
            }

            profile.Entries = entries.ToArray();
            profile.PadSettings = padSettings.ToArray();
            profile.SlotCreated = (bool[])SettingsManager.SlotCreated.Clone();
            profile.SlotEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            profile.SlotControllerTypes = Enumerable.Range(0, _mainVm.Pads.Count)
                .Select(i => (int)_mainVm.Pads[i].OutputType).ToArray();
            profile.SlotProfileIds = Enumerable.Range(0, _mainVm.Pads.Count)
                .Select(i => _mainVm.Pads[i].ProfileId).ToArray();
            profile.ExtendedConfigs = BuildExtendedConfigSnapshot();
            profile.PlayStationConfigs = BuildPlayStationConfigSnapshot();
            profile.MidiConfigs = BuildMidiConfigSnapshot();
            profile.XboxSlotOrder          = SettingsManager.XboxSlotOrder.ToArray();
            profile.PlayStationSlotOrder   = SettingsManager.PlayStationSlotOrder.ToArray();
            profile.ExtendedSlotOrder      = SettingsManager.ExtendedSlotOrder.ToArray();
            profile.KeyboardMouseSlotOrder = SettingsManager.KeyboardMouseSlotOrder.ToArray();
            profile.MidiSlotOrder          = SettingsManager.MidiSlotOrder.ToArray();
            profile.EnableDsuMotionServer = _mainVm.Dashboard.EnableDsuMotionServer;
            profile.DsuMotionServerPort = _mainVm.Dashboard.DsuMotionServerPort;
            profile.EnableWebController = _mainVm.Dashboard.EnableWebController;
            profile.WebControllerPort = _mainVm.Dashboard.WebControllerPort;
            profile.EnableTouchpadOverlay = _mainVm.Dashboard.EnableTouchpadOverlay;
            profile.TouchpadOverlayOpacity = _mainVm.Dashboard.TouchpadOverlayOpacity;
            profile.TouchpadOverlayMonitor = _mainVm.Dashboard.TouchpadOverlayMonitor;
            profile.TouchpadOverlayLeft = _mainVm.Dashboard.TouchpadOverlayLeft;
            profile.TouchpadOverlayTop = _mainVm.Dashboard.TouchpadOverlayTop;
            profile.TouchpadOverlayWidth = _mainVm.Dashboard.TouchpadOverlayWidth;
            profile.TouchpadOverlayHeight = _mainVm.Dashboard.TouchpadOverlayHeight;
        }

        /// <summary>
        /// Formats a profile's topology into a compact label like "2x Xbox, 1x PlayStation".
        /// Returns empty string for old profiles without topology data.
        /// </summary>
        internal static string FormatTopologyLabel(bool[] slotCreated, int[] slotControllerTypes)
        {
            CountTopology(slotCreated, slotControllerTypes, out int xbox, out int playstation, out int extendedCount, out int midi, out int kbm);
            var parts = new System.Collections.Generic.List<string>();
            if (xbox > 0) parts.Add($"{xbox}x Xbox");
            if (playstation > 0) parts.Add($"{playstation}x PlayStation");
            if (extendedCount > 0) parts.Add($"{extendedCount}x Extended");
            if (midi > 0) parts.Add($"{midi}x MIDI");
            if (kbm > 0) parts.Add($"{kbm}x KB+M");
            return parts.Count > 0 ? string.Join(", ", parts) : Strings.Instance.Profiles_NoSlots;
        }

        internal static void UpdateTopologyCounts(ViewModels.ProfileListItem item,
            bool[] slotCreated, int[] slotControllerTypes)
        {
            CountTopology(slotCreated, slotControllerTypes, out int xbox, out int playstation, out int extendedCount, out int midi, out int kbm);
            item.XboxCount = xbox;
            item.PlayStationCount = playstation;
            item.ExtendedCount = extendedCount;
            item.MidiCount = midi;
            item.KbmCount = kbm;
            item.TopologyLabel = FormatTopologyLabel(slotCreated, slotControllerTypes);
        }

        private static void CountTopology(bool[] slotCreated, int[] slotControllerTypes,
            out int xbox, out int playstation, out int extendedCount, out int midi, out int kbm)
        {
            xbox = 0; playstation = 0; extendedCount = 0; midi = 0; kbm = 0;
            if (slotCreated == null) return;
            for (int i = 0; i < slotCreated.Length; i++)
            {
                if (!slotCreated[i]) continue;
                int type = (slotControllerTypes != null && i < slotControllerTypes.Length)
                    ? slotControllerTypes[i] : 0;
                switch (type)
                {
                    case 1: playstation++; break;
                    case 2: extendedCount++; break;
                    case 3: midi++; break;
                    case 4: kbm++; break;
                    default: xbox++; break;
                }
            }
        }

        /// <summary>

        // ─────────────────────────────────────────────
        //  Save
        // ─────────────────────────────────────────────

        /// <summary>
        /// Saves current settings to the active settings file.
        /// </summary>
        public void Save()
        {
            SaveToFile(_settingsFilePath);
        }

        /// <summary>
        /// Saves all settings to the specified XML file.
        /// </summary>
        /// <param name="filePath">Output file path.</param>
        public void SaveToFile(string filePath)
        {
            try
            {
                var data = new SettingsFileData();

                // Push ViewModel values to PadSetting objects FIRST,
                // before collecting data for serialization.
                UpdatePadSettingsFromViewModels();

                // Flush Extended mappings from in-memory dictionaries to serializable arrays,
                // then recompute checksums for ALL PadSettings and sync to UserSettings.
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    foreach (var us in SettingsManager.UserSettings.Items)
                    {
                        var ps = us.GetPadSetting();
                        if (ps != null)
                        {
                            ps.FlushExtendedMappings();
                            ps.FlushMidiMappings();
                            ps.FlushKbmMappings();
                            ps.FlushMappingDeadZones();
                            ps.UpdateChecksum();
                            us.PadSettingChecksum = ps.PadSettingChecksum;
                        }
                    }
                }

                // If a profile is currently active, update its snapshot so
                // any edits made while the profile was active are persisted.
                UpdateActiveProfileSnapshot();

                // Collect devices.
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    data.Devices = SettingsManager.UserDevices.Items.ToArray();
                }

                // Collect user settings and unique pad settings (deduplicated by checksum).
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    data.Settings = SettingsManager.UserSettings.Items.ToArray();

                    var seen = new System.Collections.Generic.HashSet<string>();
                    var uniquePadSettings = new System.Collections.Generic.List<PadSetting>();
                    foreach (var us in SettingsManager.UserSettings.Items)
                    {
                        var ps = us.GetPadSetting();
                        if (ps != null && seen.Add(ps.PadSettingChecksum))
                            uniquePadSettings.Add(ps);
                    }
                    data.PadSettings = uniquePadSettings.ToArray();
                }

                // Phase 2C — push per-row ExtraSources / CombineMode /
                // CombineExpression from the live PadViewModels into the
                // in-memory SlotMappingSets so the merge below has the
                // user's multi-source edits to preserve.
                PushUiExtraSourcesIntoSlotMappingSets();

                // Issue #61 Phase 6 ShiftActivator UI push reverted —
                // multi-source UI must complete first per recipe order.

                // Phase 2A: rebuild MappingSet from the just-updated
                // PadSetting fields BEFORE serializing — but only replace
                // rows that don't have user-added multi-source extras.
                // Multi-source rows authored via the Phase 2C UI are
                // preserved across save round-trips; single-source rows
                // pick up the latest PadSetting edits.
                MergeMappingSetsFromLegacy();
                data.SlotMappingSets = SettingsManager.SlotMappingSets;

                // Collect app settings from ViewModel.
                data.AppSettings = BuildAppSettings();

                // Collect macros from all pad ViewModels.
                data.Macros = BuildMacroData();

                // Collect profiles.
                if (SettingsManager.Profiles.Count > 0)
                    data.Profiles = SettingsManager.Profiles.ToArray();

                // Serialize to an in-memory buffer first so a serializer
                // crash mid-write never truncates the on-disk file. The old
                // File.Create path truncated to 0 bytes before serialize
                // ran, which turned any XmlSerializer exception (e.g. an
                // embedded-null device name sneaking past the sanitizer)
                // into catastrophic settings loss at whatever byte was last
                // flushed (issue #53).
                var serializer = new XmlSerializer(typeof(SettingsFileData));
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                byte[] serializedBytes;
                using (var ms = new System.IO.MemoryStream())
                {
                    serializer.Serialize(ms, data);
                    serializedBytes = ms.ToArray();
                }
                File.WriteAllBytes(filePath, serializedBytes);

                IsDirty = false;
                _mainVm.Settings.HasUnsavedChanges = false;
                _mainVm.StatusText = string.Format(Strings.Instance.Status_SettingsSaved_Format, Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                _mainVm.StatusText = string.Format(Strings.Instance.Status_ErrorSavingSettings_Format, ex.Message);
            }
        }

        /// <summary>
        /// Builds an AppSettingsData from the current SettingsViewModel state.
        /// </summary>
        private AppSettingsData BuildAppSettings()
        {
            var vm = _mainVm.Settings;
            // Sync the ViewModel toggle to the static state.
            SettingsManager.EnableAutoProfileSwitching = vm.EnableAutoProfileSwitching;

            // Collect per-slot controller types and HIDMaestro profile slugs.
            var slotTypes = new int[_mainVm.Pads.Count];
            var slotProfileIds = new string[_mainVm.Pads.Count];
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                slotTypes[i] = (int)_mainVm.Pads[i].OutputType;
                slotProfileIds[i] = _mainVm.Pads[i].ProfileId;
            }

            // Collect per-slot Extended configurations. Only persist for
            // slots that are actually Extended — Xbox / PlayStation /
            // KbM / MIDI slots don't read this and shouldn't carry stale
            // ExtendedConfig state in the XML.
            var extendedConfigs = new System.Collections.Generic.List<ViewModels.ExtendedSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i] ||
                    _mainVm.Pads[i].OutputType != Engine.VirtualControllerType.Extended)
                    continue;
                var cfg = _mainVm.Pads[i].ExtendedConfig;
                extendedConfigs.Add(new ViewModels.ExtendedSlotConfigData
                {
                    SlotIndex = i,
                    ThumbstickCount = cfg.ThumbstickCount,
                    TriggerCount = cfg.TriggerCount,
                    PovCount = cfg.PovCount,
                    ButtonCount = cfg.ButtonCount,
                    OemNameOverride = cfg.OemNameOverride,
                    ProductString = cfg.ProductString,
                    Customize = cfg.Customize,
                    ForceFeedbackEnabled = cfg.ForceFeedbackEnabled
                });
            }

            // Collect per-(slot, device) PlayStation configurations.
            // Lighting tab is per-device — different physical devices
            // mapped to the same slot can have different mode / colors
            // / palette. We write:
            //   1. ONE slot-level entry (DeviceGuid = Empty) holding
            //      the slot's anchor PlayStationConfig — this is a
            //      pre-v3.1 compat row so older PadForge installs
            //      reading the new XML still get a usable default.
            //   2. ONE per-device entry per dict entry, INCLUDING the
            //      one whose instance the anchor currently references.
            //
            // Do NOT dedup the active-device's per-device entry against
            // the anchor by reference equality — they're the same
            // instance in memory but the saved per-device entry is
            // what gets reapplied to dict[active device's GUID] on
            // reload. Without it, the active device's settings live
            // ONLY in the slot-level entry, and ApplyPlayStationConfigs's
            // fan-out skip (which prevents the slot-level entry from
            // bleeding into other devices' dict entries) leaves the
            // active device's dict entry at defaults. Result: user's
            // Lighting tab edits don't survive an app restart.
            var playStationConfigs = new System.Collections.Generic.List<ViewModels.PlayStationSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                if (padVm.PlayStationConfig != null)
                    playStationConfigs.Add(BuildPlayStationConfigData(padVm.PlayStationConfig, i, Guid.Empty));
                foreach (var kvp in padVm.PerDevicePlayStationConfigs)
                {
                    if (kvp.Key == Guid.Empty || kvp.Value == null) continue;
                    playStationConfigs.Add(BuildPlayStationConfigData(kvp.Value, i, kvp.Key));
                }
            }

            // AppSettings always stores the DEFAULT profile's per-slot state.
            // When a named profile is active, use the saved default snapshot
            // so the named profile's state doesn't contaminate the default.
            var defaultSnap = SettingsManager.PendingDefaultSnapshot;
            bool isDefault = string.IsNullOrEmpty(SettingsManager.ActiveProfileId)
                          || defaultSnap == null;

            return new AppSettingsData
            {
                AutoStartEngine = vm.AutoStartEngine,
                MinimizeToTray = vm.MinimizeToTray,
                StartMinimized = vm.StartMinimized,
                StartAtLogin = vm.StartAtLogin,
                EnablePollingOnFocusLoss = vm.EnablePollingOnFocusLoss,
                PollingRateMs = vm.PollingRateMs,
                HmInactivityDestroyTimeoutSeconds = vm.HmInactivityDestroyTimeoutSeconds,
                ThemeIndex = vm.SelectedThemeIndex,
                Language = vm.LanguageCode,
                EnableAutoProfileSwitching = vm.EnableAutoProfileSwitching,
                ActiveProfileId = SettingsManager.ActiveProfileId,
                GlobalMacros = SettingsManager.GlobalMacros,
                SlotControllerTypes = isDefault ? slotTypes : defaultSnap.SlotControllerTypes,
                SlotProfileIds = isDefault ? slotProfileIds : defaultSnap.SlotProfileIds,
                SlotCreated = isDefault
                    ? (bool[])SettingsManager.SlotCreated.Clone()
                    : defaultSnap.SlotCreated,
                SlotEnabled = isDefault
                    ? (bool[])SettingsManager.SlotEnabled.Clone()
                    : defaultSnap.SlotEnabled,
                EnableDsuMotionServer = _mainVm.Dashboard.EnableDsuMotionServer,
                DsuMotionServerPort = _mainVm.Dashboard.DsuMotionServerPort,
                EnableWebController = _mainVm.Dashboard.EnableWebController,
                WebControllerPort = _mainVm.Dashboard.WebControllerPort,
                EnableTouchpadOverlay = _mainVm.Dashboard.EnableTouchpadOverlay,
                TouchpadOverlayOpacity = _mainVm.Dashboard.TouchpadOverlayOpacity,
                TouchpadOverlayMonitor = _mainVm.Dashboard.TouchpadOverlayMonitor,
                TouchpadOverlayLeft = _mainVm.Dashboard.TouchpadOverlayLeft,
                TouchpadOverlayTop = _mainVm.Dashboard.TouchpadOverlayTop,
                TouchpadOverlayWidth = _mainVm.Dashboard.TouchpadOverlayWidth,
                TouchpadOverlayHeight = _mainVm.Dashboard.TouchpadOverlayHeight,
                MainWindowLeft = vm.MainWindowLeft,
                MainWindowTop = vm.MainWindowTop,
                MainWindowWidth = vm.MainWindowWidth,
                MainWindowHeight = vm.MainWindowHeight,
                MainWindowState = vm.MainWindowState,
                MainWindowFullScreen = vm.MainWindowFullScreen,
                Use2DControllerView = vm.Use2DControllerView,
                LegacyDriverCleanupOffered = vm.LegacyDriverCleanupOffered,
                EnableInputHiding = vm.EnableInputHiding,
                KeepHidHideCloaksBetweenLaunches = vm.KeepHidHideCloaksBetweenLaunches,
                HidHideWhitelistPaths = vm.HidHideWhitelistPaths.Count > 0
                    ? vm.HidHideWhitelistPaths.ToArray()
                    : null,
                ExtendedConfigs = isDefault ? extendedConfigs.ToArray() : defaultSnap.ExtendedConfigs,
                PlayStationConfigs = isDefault ? playStationConfigs.ToArray() : defaultSnap.PlayStationConfigs,
                UserProfiles = _userProfiles.Count > 0 ? _userProfiles.ToArray() : null,
                MidiConfigs = isDefault ? BuildMidiConfigs() : defaultSnap.MidiConfigs,
                XboxSlotOrder          = isDefault ? SettingsManager.XboxSlotOrder.ToArray()          : defaultSnap.XboxSlotOrder,
                PlayStationSlotOrder   = isDefault ? SettingsManager.PlayStationSlotOrder.ToArray()   : defaultSnap.PlayStationSlotOrder,
                ExtendedSlotOrder      = isDefault ? SettingsManager.ExtendedSlotOrder.ToArray()      : defaultSnap.ExtendedSlotOrder,
                KeyboardMouseSlotOrder = isDefault ? SettingsManager.KeyboardMouseSlotOrder.ToArray() : defaultSnap.KeyboardMouseSlotOrder,
                MidiSlotOrder          = isDefault ? SettingsManager.MidiSlotOrder.ToArray()          : defaultSnap.MidiSlotOrder,
                DefaultProfileSnapshot = isDefault ? null : defaultSnap
            };
        }

        /// <summary>
        /// Snapshots Extended configs for only created Extended slots (for profile storage).
        /// </summary>
        private ViewModels.ExtendedSlotConfigData[] BuildExtendedConfigSnapshot()
        {
            var list = new System.Collections.Generic.List<ViewModels.ExtendedSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i] ||
                    _mainVm.Pads[i].OutputType != Engine.VirtualControllerType.Extended)
                    continue;
                var cfg = _mainVm.Pads[i].ExtendedConfig;
                list.Add(new ViewModels.ExtendedSlotConfigData
                {
                    SlotIndex = i,
                    ThumbstickCount = cfg.ThumbstickCount,
                    TriggerCount = cfg.TriggerCount,
                    PovCount = cfg.PovCount,
                    ButtonCount = cfg.ButtonCount,
                    OemNameOverride = cfg.OemNameOverride,
                    ProductString = cfg.ProductString,
                    Customize = cfg.Customize,
                    ForceFeedbackEnabled = cfg.ForceFeedbackEnabled
                });
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>
        /// Snapshots PlayStation configs for every slot for profile
        /// storage. One DTO per slot's anchor (DeviceGuid empty) plus
        /// one per (slot, device) entry — mirrors the load path's
        /// per-device handling.
        /// </summary>
        private ViewModels.PlayStationSlotConfigData[] BuildPlayStationConfigSnapshot()
        {
            var list = new System.Collections.Generic.List<ViewModels.PlayStationSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                if (padVm.PlayStationConfig != null)
                    list.Add(BuildPlayStationConfigData(padVm.PlayStationConfig, i, Guid.Empty));
                // Always emit every per-device entry. See the comment
                // in BuildAppSettingsForActiveProfile's main collector
                // for why we don't dedup against the anchor — the
                // active device's per-device entry is what reloads
                // back into its dict slot on next launch.
                foreach (var kvp in padVm.PerDevicePlayStationConfigs)
                {
                    if (kvp.Key == Guid.Empty || kvp.Value == null) continue;
                    list.Add(BuildPlayStationConfigData(kvp.Value, i, kvp.Key));
                }
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>Encodes a single <see cref="ViewModels.PlayStationSlotConfig"/>
        /// into a <see cref="ViewModels.PlayStationSlotConfigData"/> tagged with
        /// the (slot index, device GUID) pair. Empty <paramref name="deviceGuid"/>
        /// produces a legacy slot-level entry.</summary>
        private static ViewModels.PlayStationSlotConfigData BuildPlayStationConfigData(
            ViewModels.PlayStationSlotConfig cfg, int slotIndex, Guid deviceGuid)
        {
            return new ViewModels.PlayStationSlotConfigData
            {
                SlotIndex = slotIndex,
                DeviceGuid = deviceGuid,
                LeftTriggerMode = cfg.LeftTriggerMode,
                RightTriggerMode = cfg.RightTriggerMode,
                LeftStartPosition = cfg.LeftStartPosition,
                LeftEndPosition = cfg.LeftEndPosition,
                LeftStrength = cfg.LeftStrength,
                LeftFrequency = cfg.LeftFrequency,
                RightStartPosition = cfg.RightStartPosition,
                RightEndPosition = cfg.RightEndPosition,
                RightStrength = cfg.RightStrength,
                RightFrequency = cfg.RightFrequency,
                LightbarRed = cfg.LightbarRed,
                LightbarGreen = cfg.LightbarGreen,
                LightbarBlue = cfg.LightbarBlue,
                LightbarEnabled = cfg.LightbarEnabled,
                MicLedMode = cfg.MicLedMode,
                MicLedFollowDeviceId = cfg.MicLedFollowDeviceId ?? string.Empty,
                MicLightOn = cfg.MicLightOn,
                PlayerLedMode = cfg.PlayerLedMode,
                PlayerLedBrightness = cfg.PlayerLedBrightness,
                AudioLightbarEnabled = cfg.AudioLightbarEnabled,
                AudioLightbarSensitivity = cfg.AudioLightbarSensitivity,
                AudioLightbarMode = cfg.AudioLightbarMode,
                AudioLowR = cfg.AudioLowR,
                AudioLowG = cfg.AudioLowG,
                AudioLowB = cfg.AudioLowB,
                AudioMidR = cfg.AudioMidR,
                AudioMidG = cfg.AudioMidG,
                AudioMidB = cfg.AudioMidB,
                AudioHighR = cfg.AudioHighR,
                AudioHighG = cfg.AudioHighG,
                AudioHighB = cfg.AudioHighB,
                AudioLowToMidPercent = cfg.AudioLowToMidPercent,
                AudioMidToHighPercent = cfg.AudioMidToHighPercent,
                AudioCrossFadePercent = cfg.AudioCrossFadePercent,
                LightbarMode = cfg.LightbarMode,
                LightbarPeriodMs = cfg.LightbarPeriodMs,
                LightbarColorCycleSmooth = cfg.LightbarColorCycleSmooth,
                LightbarRainbowBrightness = cfg.LightbarRainbowBrightness,
                LightbarBatteryLowR  = cfg.LightbarBatteryLowR,
                LightbarBatteryLowG  = cfg.LightbarBatteryLowG,
                LightbarBatteryLowB  = cfg.LightbarBatteryLowB,
                LightbarBatteryHighR = cfg.LightbarBatteryHighR,
                LightbarBatteryHighG = cfg.LightbarBatteryHighG,
                LightbarBatteryHighB = cfg.LightbarBatteryHighB,
                LightbarPalette = cfg.LightbarPalette
                    .Select(e => new ViewModels.LightbarPaletteEntryData { R = e.R, G = e.G, B = e.B })
                    .ToArray(),
                LightbarInputHoldMs = cfg.LightbarInputHoldMs,
                LightbarInputDecayMs = cfg.LightbarInputDecayMs,
                InputReactiveMode = cfg.InputReactiveMode,
                InputReactiveR = cfg.InputReactiveR,
                InputReactiveG = cfg.InputReactiveG,
                InputReactiveB = cfg.InputReactiveB,
            };
        }

        /// <summary>
        /// Snapshots MIDI configs for only created MIDI slots (for profile storage).
        /// </summary>
        private ViewModels.MidiSlotConfigData[] BuildMidiConfigSnapshot()
        {
            var list = new System.Collections.Generic.List<ViewModels.MidiSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i] ||
                    _mainVm.Pads[i].OutputType != Engine.VirtualControllerType.Midi)
                    continue;
                var cfg = _mainVm.Pads[i].MidiConfig;
                list.Add(new ViewModels.MidiSlotConfigData
                {
                    SlotIndex = i,
                    Channel = cfg.Channel,
                    Velocity = cfg.Velocity,
                    CcCount = cfg.CcCount,
                    StartCc = cfg.StartCc,
                    NoteCount = cfg.NoteCount,
                    StartNote = cfg.StartNote
                });
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private ViewModels.MidiSlotConfigData[] BuildMidiConfigs()
        {
            var list = new System.Collections.Generic.List<ViewModels.MidiSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var cfg = _mainVm.Pads[i].MidiConfig;
                list.Add(new ViewModels.MidiSlotConfigData
                {
                    SlotIndex = i,
                    Channel = cfg.Channel,
                    Velocity = cfg.Velocity,
                    CcCount = cfg.CcCount,
                    StartCc = cfg.StartCc,
                    NoteCount = cfg.NoteCount,
                    StartNote = cfg.StartNote
                });
            }
            return list.ToArray();
        }

        /// <summary>
        /// Collects macro data from all pad ViewModels for serialization.
        /// </summary>
        private MacroData[] BuildMacroData()
        {
            var list = new System.Collections.Generic.List<MacroData>();

            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                foreach (var macro in padVm.Macros)
                {
                    list.Add(new MacroData
                    {
                        PadIndex = i,
                        Name = macro.Name,
                        IsEnabled = macro.IsEnabled,
                        TriggerButtons = macro.TriggerButtons,
                        TriggerDeviceGuid = macro.TriggerDeviceGuid != Guid.Empty
                            ? macro.TriggerDeviceGuid.ToString("N") : null,
                        TriggerRawButtons = macro.TriggerRawButtons.Length > 0
                            ? string.Join(",", macro.TriggerRawButtons) : null,
                        TriggerSource = macro.TriggerSource,
                        TriggerMode = macro.TriggerMode,
                        ConsumeTriggerButtons = macro.ConsumeTriggerButtons,
                        RepeatMode = macro.RepeatMode,
                        RepeatCount = macro.RepeatCount,
                        RepeatDelayMs = macro.RepeatDelayMs,
                        TriggerCustomButtons = macro.TriggerCustomButtons,
                        TriggerAxisTargets = macro.TriggerAxisTargetList,
                        TriggerAxisThreshold = macro.TriggerAxisThreshold,
                        TriggerPovs = macro.TriggerPovs?.Length > 0 ? macro.TriggerPovs : null,
                        Actions = macro.Actions.Select(a => new ActionData
                        {
                            Type = a.Type,
                            ButtonFlags = a.ButtonFlags,
                            CustomButtons = a.CustomButtons,
                            KeyCode = a.ParsedKeyCodes.Length > 0 ? a.ParsedKeyCodes[0] : a.KeyCode,
                            KeyString = a.KeyString,
                            DurationMs = a.DurationMs,
                            AxisValue = a.AxisValue,
                            AxisTarget = a.AxisTarget,
                            AxisSource = a.AxisSource,
                            SourceDeviceGuid = a.SourceDeviceGuid != Guid.Empty
                                ? a.SourceDeviceGuid.ToString("N") : null,
                            SourceDeviceAxisIndex = a.SourceDeviceAxisIndex,
                            ProcessName = a.ProcessName,
                            VolumeLimit = a.VolumeLimit,
                            MouseSensitivity = a.MouseSensitivity,
                            MouseButton = a.MouseButton,
                            InvertAxis = a.InvertAxis,
                            ShowVolumeOsd = a.ShowVolumeOsd,
                            LightbarR = a.LightbarR,
                            LightbarG = a.LightbarG,
                            LightbarB = a.LightbarB,
                            LightbarHoldMode = a.LightbarHoldMode,
                            LightbarColorSource = a.LightbarColorSource,
                            LightbarHoldMs = a.LightbarHoldMs,
                            LightbarFadeMs = a.LightbarFadeMs,
                            LightbarPaletteCsv = a.LightbarPaletteCsv,
                            LightbarTargetMode = a.LightbarTargetMode,
                            LightbarCycleModesCsv = a.LightbarCycleModesCsv
                        }).ToArray()
                    });
                }
            }

            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>
        /// Pushes ViewModel values back into the currently selected device's
        /// PadSetting per slot. Non-selected devices retain their own settings.
        /// </summary>
        private void UpdatePadSettingsFromViewModels()
        {
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                for (int i = 0; i < _mainVm.Pads.Count; i++)
                {
                    var padVm = _mainVm.Pads[i];
                    var selected = padVm.SelectedMappedDevice;
                    if (selected == null || selected.InstanceGuid == Guid.Empty)
                        continue;

                    var us = SettingsManager.FindSettingByInstanceGuidAndSlot(selected.InstanceGuid, i);
                    if (us == null) continue;

                    var ps = us.GetPadSetting();
                    if (ps == null) continue;

                    // Write force feedback settings.
                    ps.ForceOverall = padVm.ForceOverallGain.ToString();
                    ps.LeftMotorStrength = padVm.LeftMotorStrength.ToString();
                    ps.RightMotorStrength = padVm.RightMotorStrength.ToString();
                    ps.ForceSwapMotor = padVm.SwapMotors ? "1" : "0";

                    // Issue #50: all double→string conversions MUST use InvariantCulture.
                    // WARNING: if you add a new double property below, use .ToString(ic)
                    // — NOT bare .ToString(). See InputService.SaveViewModelToPadSetting
                    // for the full explanation of the locale data-loss bug.
                    var ic = System.Globalization.CultureInfo.InvariantCulture;

                    // Write audio bass rumble settings.
                    ps.AudioRumbleEnabled = padVm.AudioRumbleEnabled ? "1" : "0";
                    ps.AudioRumbleSensitivity = padVm.AudioRumbleSensitivity.ToString("F1", ic);
                    ps.AudioRumbleCutoffHz = padVm.AudioRumbleCutoffHz.ToString("F0", ic);
                    ps.AudioRumbleLeftMotor = padVm.AudioRumbleLeftMotor.ToString();
                    ps.AudioRumbleRightMotor = padVm.AudioRumbleRightMotor.ToString();

                    // Write constant force settings.
                    ps.ConstantForceEnabled = padVm.ConstantForceEnabled ? "1" : "0";
                    ps.ConstantForceX = padVm.ConstantForceX.ToString("F4", ic);
                    ps.ConstantForceY = padVm.ConstantForceY.ToString("F4", ic);

                    // Write deadzone settings (independent X/Y).
                    ps.LeftThumbDeadZoneShape = padVm.LeftDeadZoneShape.ToString();
                    ps.LeftThumbDeadZoneX = padVm.LeftDeadZoneX.ToString(ic);
                    ps.LeftThumbDeadZoneY = padVm.LeftDeadZoneY.ToString(ic);
                    ps.RightThumbDeadZoneShape = padVm.RightDeadZoneShape.ToString();
                    ps.RightThumbDeadZoneX = padVm.RightDeadZoneX.ToString(ic);
                    ps.RightThumbDeadZoneY = padVm.RightDeadZoneY.ToString(ic);
                    ps.LeftThumbAntiDeadZoneX = padVm.LeftAntiDeadZoneX.ToString(ic);
                    ps.LeftThumbAntiDeadZoneY = padVm.LeftAntiDeadZoneY.ToString(ic);
                    ps.RightThumbAntiDeadZoneX = padVm.RightAntiDeadZoneX.ToString(ic);
                    ps.RightThumbAntiDeadZoneY = padVm.RightAntiDeadZoneY.ToString(ic);
                    ps.LeftThumbLinear = padVm.LeftLinear.ToString(ic);
                    ps.RightThumbLinear = padVm.RightLinear.ToString(ic);
                    ps.LeftThumbSensitivityCurveX = padVm.LeftSensitivityCurveX;
                    ps.LeftThumbSensitivityCurveY = padVm.LeftSensitivityCurveY;
                    ps.RightThumbSensitivityCurveX = padVm.RightSensitivityCurveX;
                    ps.RightThumbSensitivityCurveY = padVm.RightSensitivityCurveY;
                    ps.LeftTriggerSensitivityCurve = padVm.LeftTriggerSensitivityCurve;
                    ps.RightTriggerSensitivityCurve = padVm.RightTriggerSensitivityCurve;
                    ps.LeftThumbMaxRangeX = padVm.LeftMaxRangeX.ToString(ic);
                    ps.LeftThumbMaxRangeY = padVm.LeftMaxRangeY.ToString(ic);
                    ps.RightThumbMaxRangeX = padVm.RightMaxRangeX.ToString(ic);
                    ps.RightThumbMaxRangeY = padVm.RightMaxRangeY.ToString(ic);
                    ps.LeftThumbMaxRangeXNeg = padVm.LeftMaxRangeXNeg.ToString(ic);
                    ps.LeftThumbMaxRangeYNeg = padVm.LeftMaxRangeYNeg.ToString(ic);
                    ps.RightThumbMaxRangeXNeg = padVm.RightMaxRangeXNeg.ToString(ic);
                    ps.RightThumbMaxRangeYNeg = padVm.RightMaxRangeYNeg.ToString(ic);
                    ps.LeftThumbCenterOffsetX = padVm.LeftCenterOffsetX.ToString(ic);
                    ps.LeftThumbCenterOffsetY = padVm.LeftCenterOffsetY.ToString(ic);
                    ps.RightThumbCenterOffsetX = padVm.RightCenterOffsetX.ToString(ic);
                    ps.RightThumbCenterOffsetY = padVm.RightCenterOffsetY.ToString(ic);

                    // Write trigger deadzone settings.
                    ps.LeftTriggerDeadZone = padVm.LeftTriggerDeadZone.ToString(ic);
                    ps.RightTriggerDeadZone = padVm.RightTriggerDeadZone.ToString(ic);
                    ps.LeftTriggerAntiDeadZone = padVm.LeftTriggerAntiDeadZone.ToString(ic);
                    ps.RightTriggerAntiDeadZone = padVm.RightTriggerAntiDeadZone.ToString(ic);
                    ps.LeftTriggerMaxRange = padVm.LeftTriggerMaxRange.ToString(ic);
                    ps.RightTriggerMaxRange = padVm.RightTriggerMaxRange.ToString(ic);

                    // Write Extended custom stick/trigger settings for indices 2+ to dictionary.
                    foreach (var stick in padVm.StickConfigs)
                    {
                        if (stick.Index < 2) continue;
                        int g = stick.Index;
                        ps.SetExtendedMapping($"ExtendedStick{g}DzShape", ((int)stick.DeadZoneShape).ToString());
                        ps.SetExtendedMapping($"ExtendedStick{g}DzX", stick.DeadZoneX.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedStick{g}DzY", stick.DeadZoneY.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedStick{g}AdzX", stick.AntiDeadZoneX.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedStick{g}AdzY", stick.AntiDeadZoneY.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedStick{g}Linear", stick.Linear.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedStick{g}CurveX", stick.SensitivityCurveX);
                        ps.SetExtendedMapping($"ExtendedStick{g}CurveY", stick.SensitivityCurveY);
                        ps.SetExtendedMapping($"ExtendedStick{g}CofX", stick.CenterOffsetX.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedStick{g}CofY", stick.CenterOffsetY.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedStick{g}MrX", stick.MaxRangeX.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedStick{g}MrY", stick.MaxRangeY.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedStick{g}MrXN", stick.MaxRangeXNeg.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedStick{g}MrYN", stick.MaxRangeYNeg.ToString(ic));
                    }
                    foreach (var trig in padVm.TriggerConfigs)
                    {
                        if (trig.Index < 2) continue;
                        int g = trig.Index;
                        ps.SetExtendedMapping($"ExtendedTrigger{g}Dz", trig.DeadZone.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedTrigger{g}Adz", trig.AntiDeadZone.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedTrigger{g}Mr", trig.MaxRange.ToString(ic));
                        ps.SetExtendedMapping($"ExtendedTrigger{g}Curve", trig.SensitivityCurve);
                    }

                    // Write mapping descriptors and per-mapping deadzones.
                    foreach (var mapping in padVm.Mappings)
                    {
                        SetPadSettingProperty(ps, mapping.TargetSettingName, mapping.SourceDescriptor);
                        if (mapping.NegSettingName != null)
                            SetPadSettingProperty(ps, mapping.NegSettingName, mapping.NegSourceDescriptor);

                        if (mapping.MappingDeadZone > 0)
                            ps.SetMappingDeadZone(mapping.TargetSettingName, mapping.MappingDeadZone.ToString());
                        else
                            ps.SetMappingDeadZone(mapping.TargetSettingName, "");
                    }
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Reset
        // ─────────────────────────────────────────────

        /// <summary>
        /// Resets all settings to defaults. Clears all mappings and device records.
        /// </summary>
        public void ResetToDefaults()
        {
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                SettingsManager.UserDevices.Items.Clear();
            }

            lock (SettingsManager.UserSettings.SyncRoot)
            {
                SettingsManager.UserSettings.Items.Clear();
            }

            // Reset ViewModels.
            foreach (var padVm in _mainVm.Pads)
            {
                foreach (var mapping in padVm.Mappings)
                    mapping.SourceDescriptor = string.Empty;

                padVm.ForceOverallGain = 100;
                padVm.LeftMotorStrength = 100;
                padVm.RightMotorStrength = 100;
                padVm.SwapMotors = false;
                padVm.AudioRumbleEnabled = false;
                padVm.AudioRumbleSensitivity = 4.0;
                padVm.AudioRumbleCutoffHz = 80.0;
                padVm.AudioRumbleLeftMotor = 100;
                padVm.AudioRumbleRightMotor = 100;
                padVm.ConstantForceEnabled = false;
                padVm.ConstantForceX = 0;
                padVm.ConstantForceY = 0;
                padVm.LeftDeadZoneX = 0;
                padVm.LeftDeadZoneY = 0;
                padVm.RightDeadZoneX = 0;
                padVm.RightDeadZoneY = 0;
                padVm.LeftAntiDeadZoneX = 0;
                padVm.LeftAntiDeadZoneY = 0;
                padVm.RightAntiDeadZoneX = 0;
                padVm.RightAntiDeadZoneY = 0;
                padVm.LeftLinear = 0;
                padVm.RightLinear = 0;
                padVm.LeftMaxRangeX = 100;
                padVm.LeftMaxRangeY = 100;
                padVm.RightMaxRangeX = 100;
                padVm.RightMaxRangeY = 100;
                padVm.LeftMaxRangeXNeg = 100;
                padVm.LeftMaxRangeYNeg = 100;
                padVm.RightMaxRangeXNeg = 100;
                padVm.RightMaxRangeYNeg = 100;
                padVm.LeftCenterOffsetX = 0;
                padVm.LeftCenterOffsetY = 0;
                padVm.RightCenterOffsetX = 0;
                padVm.RightCenterOffsetY = 0;
                padVm.LeftTriggerDeadZone = 0;
                padVm.RightTriggerDeadZone = 0;
                padVm.LeftTriggerAntiDeadZone = 0;
                padVm.RightTriggerAntiDeadZone = 0;
                padVm.LeftTriggerMaxRange = 100;
                padVm.RightTriggerMaxRange = 100;

                padVm.SyncAllConfigItemsFromVm();
            }

            var settingsVm = _mainVm.Settings;
            settingsVm.AutoStartEngine = true;
            settingsVm.MinimizeToTray = false;
            settingsVm.StartMinimized = false;
            settingsVm.StartAtLogin = false;
            settingsVm.EnablePollingOnFocusLoss = true;
            settingsVm.PollingRateMs = 1;
            settingsVm.HmInactivityDestroyTimeoutSeconds = 60;
            settingsVm.SelectedThemeIndex = 0;
            settingsVm.EnableInputHiding = true;
            settingsVm.EnableAutoProfileSwitching = false;
            _mainVm.Dashboard.EnableDsuMotionServer = false;
            _mainVm.Dashboard.DsuMotionServerPort = 26760;
            _mainVm.Dashboard.EnableWebController = false;
            _mainVm.Dashboard.WebControllerPort = 8080;
            SettingsManager.EnableAutoProfileSwitching = false;
            SettingsManager.ActiveProfileId = null;
            SettingsManager.Profiles.Clear();
            settingsVm.ProfileItems.Clear();
            settingsVm.ProfileItems.Add(new ViewModels.ProfileListItem
            {
                Id = ViewModels.ProfileListItem.DefaultProfileId,
                Name = Strings.Instance.Profile_Default,
            });
            settingsVm.ActiveProfileInfo = Strings.Instance.Profile_Default;

            IsDirty = true;
            settingsVm.HasUnsavedChanges = true;
            _mainVm.StatusText = Strings.Instance.Status_SettingsResetDefaults;
        }

        // ─────────────────────────────────────────────
        //  Reload
        // ─────────────────────────────────────────────

        /// <summary>
        /// Reloads settings from disk, discarding any unsaved changes.
        /// </summary>
        public void Reload()
        {
            if (File.Exists(_settingsFilePath))
            {
                LoadFromFile(_settingsFilePath);
                _mainVm.StatusText = Strings.Instance.Status_SettingsReloaded;
            }
            else
            {
                _mainVm.StatusText = Strings.Instance.Status_NoSettingsFile;
            }

            IsDirty = false;
            _mainVm.Settings.HasUnsavedChanges = false;
        }

        /// <summary>
        /// Snapshot of the current user-profile store for UI presentation
        /// in the Manage Profiles dialog. Returns id + display name pairs
        /// (the name is pulled from each profile's JSON "name" field,
        /// which already carries the "(User Generated)" suffix applied on
        /// import).
        /// </summary>
        public IReadOnlyList<Views.ManageProfilesDialog.ImportedProfileRow> GetUserProfileRows()
        {
            var list = new List<Views.ManageProfilesDialog.ImportedProfileRow>();
            foreach (var p in _userProfiles)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Json)) continue;
                string name = p.Id;
                try
                {
                    var root = System.Text.Json.Nodes.JsonNode.Parse(p.Json)?.AsObject();
                    var n = root?["name"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(n)) name = n;
                }
                catch { }
                list.Add(new Views.ManageProfilesDialog.ImportedProfileRow
                {
                    Id = p.Id,
                    Name = name,
                });
            }
            return list;
        }

        /// <summary>
        /// Remove a user-imported profile from the store by id. Any slot
        /// whose ProfileId matched the deleted entry gets reset to the
        /// synthetic padforge-custom entry so no slot points at a missing
        /// profile after the delete lands.
        ///
        /// UI-thread only. Callers are ManageProfilesDialog button
        /// handlers, which own modal focus for the duration of the call,
        /// so the slot-reset loop cannot race with a user-driven slot edit
        /// or a polling-thread read of ProfileId.
        /// </summary>
        public void RemoveUserProfile(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            int removed = _userProfiles.RemoveAll(p =>
                string.Equals(p?.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return;

            // Reset any slot that was using the deleted profile so the
            // dropdown doesn't land on a now-missing id. padforge-custom
            // is the Extended category's default per
            // InputManager.GetDefaultProfileId; Xbox and PlayStation
            // slots have fixed catalog defaults and don't accept user
            // imports in the first place, so this reset only ever fires
            // for Extended slots.
            foreach (var padVm in _mainVm.Pads)
            {
                if (string.Equals(padVm?.ProfileId, id, StringComparison.OrdinalIgnoreCase))
                    padVm.ProfileId = Common.Input.HMaestroProfileCatalog.CustomProfileId;
            }

            Common.Input.HMaestroProfileCatalog.Reload();
            MarkDirty();
        }

        /// <summary>
        /// Write a user-imported profile's JSON to the given file path.
        /// Caller is responsible for prompting the user for the file
        /// location. Throws on failure so the dialog can surface the
        /// error.
        /// </summary>
        public void ExportUserProfile(string id, string filePath)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(filePath))
                return;
            var entry = _userProfiles.FirstOrDefault(p =>
                string.Equals(p?.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry == null || string.IsNullOrWhiteSpace(entry.Json))
                throw new InvalidOperationException($"User profile '{id}' not found.");

            // Pretty-print the JSON on export — the stored form is compact
            // to keep PadForge.xml tight, but files written for contribution
            // / inspection are more useful with indentation.
            string pretty = entry.Json;
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(entry.Json);
                if (root != null)
                    pretty = root.ToJsonString(
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            catch { }
            System.IO.File.WriteAllText(filePath, pretty);
        }

        /// <summary>
        /// Persist a newly-extracted HIDMaestro profile JSON into the user
        /// profile store. Applies the "-user" id suffix and the
        /// " (User Generated)" name suffix (forcing uniqueness against the
        /// built-in catalog), deduplicates by Id so re-importing the same
        /// device overwrites the prior capture, triggers a catalog reload
        /// so the Extended dropdown picks up the new entry, and marks
        /// settings dirty. Returns the suffixed id so the caller can set
        /// the current slot's ProfileId directly.
        /// </summary>
        public string AddUserProfile(string extractedJson)
        {
            if (string.IsNullOrWhiteSpace(extractedJson)) return null;

            string suffixed;
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(extractedJson)?.AsObject();
                if (root == null) return null;

                string baseId = root["id"]?.GetValue<string>();
                string baseName = root["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(baseId)) return null;

                if (!baseId.EndsWith("-user", StringComparison.OrdinalIgnoreCase))
                    baseId += "-user";
                if (!string.IsNullOrEmpty(baseName) && !baseName.Contains("(User Generated)"))
                    baseName = $"{baseName} (User Generated)";

                root["id"] = baseId;
                if (baseName != null) root["name"] = baseName;
                suffixed = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            }
            catch
            {
                return null;
            }

            string id;
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(suffixed)?.AsObject();
                id = root?["id"]?.GetValue<string>();
            }
            catch { id = null; }
            if (string.IsNullOrWhiteSpace(id)) return null;

            _userProfiles.RemoveAll(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            _userProfiles.Add(new UserProfileData { Id = id, Json = suffixed });

            Common.Input.HMaestroProfileCatalog.Reload();
            MarkDirty();
            return id;
        }

        /// <summary>
        /// Marks settings as dirty (unsaved changes) and schedules an autosave
        /// after a 2-second debounce period.
        /// </summary>
        public void MarkDirty()
        {
            IsDirty = true;
            _mainVm.Settings.HasUnsavedChanges = true;

            // Start or restart the autosave debounce timer.
            if (_autoSaveTimer == null)
            {
                _autoSaveTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _autoSaveTimer.Tick += (s, e) =>
                {
                    _autoSaveTimer.Stop();
                    if (IsDirty)
                    {
                        Save();
                        AutoSaved?.Invoke(this, EventArgs.Empty);
                    }
                };
            }

            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }

        // ─────────────────────────────────────────────
        //  PadSetting reflection helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Gets a string property value from a PadSetting by property name.
        /// For keys starting with "Extended", uses the dictionary-based Extended mapping system.
        /// </summary>
        private static string GetPadSettingProperty(PadSetting ps, string propertyName)
        {
            if (ps == null || string.IsNullOrEmpty(propertyName))
                return string.Empty;

            if (propertyName.StartsWith("Extended", StringComparison.Ordinal))
                return ps.GetExtendedMapping(propertyName);
            if (propertyName.StartsWith("Midi", StringComparison.Ordinal))
                return ps.GetMidiMapping(propertyName);
            if (propertyName.StartsWith("Kbm", StringComparison.Ordinal))
                return ps.GetKbmMapping(propertyName);

            var prop = typeof(PadSetting).GetProperty(propertyName);
            if (prop == null || prop.PropertyType != typeof(string))
                return string.Empty;

            return prop.GetValue(ps) as string ?? string.Empty;
        }

        /// <summary>
        /// Sets a string property value on a PadSetting by property name.
        /// For keys starting with "Extended", uses the dictionary-based Extended mapping system.
        /// </summary>
        private static void SetPadSettingProperty(PadSetting ps, string propertyName, string value)
        {
            if (ps == null || string.IsNullOrEmpty(propertyName))
                return;

            // Extended custom mappings use dictionary-based storage
            if (propertyName.StartsWith("Extended", StringComparison.Ordinal))
            {
                ps.SetExtendedMapping(propertyName, value ?? string.Empty);
                return;
            }

            // MIDI mappings use dictionary-based storage
            if (propertyName.StartsWith("Midi", StringComparison.Ordinal))
            {
                ps.SetMidiMapping(propertyName, value ?? string.Empty);
                return;
            }

            // KBM mappings use dictionary-based storage
            if (propertyName.StartsWith("Kbm", StringComparison.Ordinal))
            {
                ps.SetKbmMapping(propertyName, value ?? string.Empty);
                return;
            }

            var prop = typeof(PadSetting).GetProperty(propertyName);
            if (prop == null || prop.PropertyType != typeof(string) || !prop.CanWrite)
                return;

            prop.SetValue(ps, value ?? string.Empty);
        }

        // ─────────────────────────────────────────────
        //  Parse helper
        // ─────────────────────────────────────────────

        private static int TryParseInt(string value, int defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        private static double TryParseDouble(string value, double defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            return double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double result) ? result : defaultValue;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Serialization data classes
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Root element for the PadForge settings XML file.
    /// </summary>
    [XmlRoot("PadForgeSettings")]
    public class SettingsFileData
    {
        [XmlArray("Devices")]
        [XmlArrayItem("Device")]
        public UserDevice[] Devices { get; set; }

        [XmlArray("UserSettings")]
        [XmlArrayItem("Setting")]
        public UserSetting[] Settings { get; set; }

        [XmlArray("PadSettings")]
        [XmlArrayItem("PadSetting")]
        public PadSetting[] PadSettings { get; set; }

        /// <summary>Per-slot mapping tables (Issue #61 multi-source / shift
        /// layer). One entry per VC slot. Phase 1a (this commit) only
        /// round-trips the data; the legacy <see cref="PadSetting"/>
        /// per-field mapping descriptors stay authoritative until Commit
        /// 1c flips Step 3 over to read from here.</summary>
        [XmlArray("SlotMappingSets")]
        [XmlArrayItem("MappingSet")]
        public MappingSet[] SlotMappingSets { get; set; }

        /// <summary>Per-device tuning data (deadzones, sensitivity curves,
        /// FFB gains, audio rumble). Phase 1a placeholder; Commit 1b
        /// migrates the relevant fields off <see cref="PadSetting"/>.</summary>
        [XmlArray("DeviceTunings")]
        [XmlArrayItem("DeviceTuning")]
        public DeviceTuning[] DeviceTunings { get; set; }

        [XmlElement("AppSettings")]
        public AppSettingsData AppSettings { get; set; }

        [XmlArray("Macros")]
        [XmlArrayItem("Macro")]
        public MacroData[] Macros { get; set; }

        [XmlArray("Profiles")]
        [XmlArrayItem("Profile")]
        public ProfileData[] Profiles { get; set; }
    }

    /// <summary>
    /// Application-level settings stored in the XML file.
    /// </summary>
    public class AppSettingsData
    {
        [XmlElement]
        public bool AutoStartEngine { get; set; } = true;

        [XmlElement]
        public bool MinimizeToTray { get; set; }

        [XmlElement]
        public bool StartMinimized { get; set; }

        [XmlElement]
        public bool StartAtLogin { get; set; }

        [XmlElement]
        public bool EnablePollingOnFocusLoss { get; set; } = true;

        [XmlElement]
        public int PollingRateMs { get; set; } = 1;

        /// <summary>
        /// Seconds an HM virtual controller waits for any mapped device to
        /// come back online before destroying itself. 0 disables (HM VCs
        /// survive arbitrary offline windows, legacy behavior). Default 60.
        /// When triggered, the slot is removed entirely (same shape as
        /// user-driven delete) and surviving Xbox HM VCs in the same
        /// group bubble down via InputService.OnSlotDeleted so xinputhid
        /// kernel slots stay contiguous. Slots in other groups are not
        /// touched.
        /// </summary>
        [XmlElement]
        public int HmInactivityDestroyTimeoutSeconds { get; set; } = 60;

        [XmlElement]
        public int ThemeIndex { get; set; }

        [XmlElement]
        public string Language { get; set; } = "";

        [XmlElement]
        public bool EnableAutoProfileSwitching { get; set; }

        [XmlElement]
        public string ActiveProfileId { get; set; }

        /// <summary>
        /// Per-slot virtual controller output types.
        /// Array of ints matching VirtualControllerType enum values.
        /// </summary>
        [XmlArray("SlotControllerTypes")]
        [XmlArrayItem("Type")]
        public int[] SlotControllerTypes { get; set; }

        /// <summary>
        /// Per-slot HIDMaestro profile slug (e.g. "xbox-360-wired",
        /// "dualsense", "logitech-g920"). Empty string falls back to a
        /// category-appropriate default in the engine. Added in v3.0.0.
        /// </summary>
        [XmlArray("SlotProfileIds")]
        [XmlArrayItem("Id")]
        public string[] SlotProfileIds { get; set; }

        /// <summary>
        /// Which virtual controller slots have been explicitly created.
        /// Null on old settings files — auto-populated from existing assignments.
        /// </summary>
        [XmlArray("SlotCreated")]
        [XmlArrayItem("Created")]
        public bool[] SlotCreated { get; set; }

        /// <summary>
        /// Which virtual controller slots are enabled for output.
        /// Null on old settings files, defaults to all true.
        /// </summary>
        [XmlArray("SlotEnabled")]
        [XmlArrayItem("Enabled")]
        public bool[] SlotEnabled { get; set; }

        /// <summary>
        /// Per-group ordered list of pad indices in user-facing visual order.
        /// Null on settings predating the per-group ordering refactor; load
        /// reconstructs ascending pad-index defaults via
        /// <c>SettingsManager.SlotOrders.RebuildFromCurrentTopology</c>.
        /// </summary>
        // XmlArray name kept as "MicrosoftSlotOrder" for v2/early-v3
        // PadForge.xml back-compat. The C# property name follows the
        // user-facing "Xbox" family naming.
        [XmlArray("MicrosoftSlotOrder")]
        [XmlArrayItem("PadIndex")]
        public int[] XboxSlotOrder { get; set; }

        [XmlArray("PlayStationSlotOrder")]
        [XmlArrayItem("PadIndex")]
        public int[] PlayStationSlotOrder { get; set; }

        [XmlArray("ExtendedSlotOrder")]
        [XmlArrayItem("PadIndex")]
        public int[] ExtendedSlotOrder { get; set; }

        [XmlArray("KeyboardMouseSlotOrder")]
        [XmlArrayItem("PadIndex")]
        public int[] KeyboardMouseSlotOrder { get; set; }

        [XmlArray("MidiSlotOrder")]
        [XmlArrayItem("PadIndex")]
        public int[] MidiSlotOrder { get; set; }

        [XmlElement]
        public bool EnableDsuMotionServer { get; set; }

        [XmlElement]
        public int DsuMotionServerPort { get; set; } = 26760;

        [XmlElement]
        public bool EnableWebController { get; set; }

        [XmlElement]
        public int WebControllerPort { get; set; } = 8080;

        [XmlElement]
        public bool EnableTouchpadOverlay { get; set; }

        [XmlElement]
        public double TouchpadOverlayOpacity { get; set; } = 0.25;

        [XmlElement]
        public int TouchpadOverlayMonitor { get; set; }

        [XmlElement]
        public double TouchpadOverlayLeft { get; set; } = -1;

        [XmlElement]
        public double TouchpadOverlayTop { get; set; } = -1;

        [XmlElement]
        public double TouchpadOverlayWidth { get; set; } = 500;

        [XmlElement]
        public double TouchpadOverlayHeight { get; set; } = 250;

        [XmlElement]
        public double MainWindowLeft { get; set; } = -1;

        [XmlElement]
        public double MainWindowTop { get; set; } = -1;

        [XmlElement]
        public double MainWindowWidth { get; set; } = 1100;

        [XmlElement]
        public double MainWindowHeight { get; set; } = 720;

        [XmlElement]
        public int MainWindowState { get; set; } // 0=Normal, 2=Maximized

        [XmlElement]
        public bool MainWindowFullScreen { get; set; }

        [XmlElement]
        public bool Use2DControllerView { get; set; }

        /// <summary>
        /// True after the v3 first-run cleanup wizard has been shown to the
        /// user, regardless of whether they accepted or declined. Prevents
        /// the dialog from re-prompting on every launch. Defaults to false
        /// on v2.x settings files (so v2 → v3 upgraders see the prompt
        /// once); v3 fresh-install settings files start true since there
        /// are no legacy drivers to clean up.
        /// </summary>
        [XmlElement]
        public bool LegacyDriverCleanupOffered { get; set; }

        /// <summary>
        /// Global master switch for device hiding (HidHide + input hooks).
        /// When false, no HidHide blacklisting or hook suppression occurs
        /// regardless of per-device toggles.
        /// </summary>
        [XmlElement]
        public bool EnableInputHiding { get; set; } = true;

        /// <summary>
        /// When true, PadForge keeps its HidHide-managed cloaks asserted
        /// across shutdowns instead of clearing them on Stop. Lets
        /// non-PadForge sessions still see the physicals cloaked (e.g.
        /// Steam scanning controllers after PadForge exits). Default
        /// false matches the previous shutdown-decloak behavior.
        /// </summary>
        [XmlElement]
        public bool KeepHidHideCloaksBetweenLaunches { get; set; } = false;

        /// <summary>
        /// User-specified application paths to whitelist in HidHide.
        /// These are regular Windows paths (e.g. C:\Games\emulator.exe),
        /// converted to DOS device paths at runtime.
        /// </summary>
        [XmlArray("HidHideWhitelistPaths")]
        [XmlArrayItem("Path")]
        public string[] HidHideWhitelistPaths { get; set; }

        /// <summary>
        /// Per-slot Extended configuration (preset, axis/button counts).
        /// Null on old settings files — uses Xbox360 preset defaults.
        /// </summary>
        [XmlArray("ExtendedConfigs")]
        [XmlArrayItem("Config")]
        public ViewModels.ExtendedSlotConfigData[] ExtendedConfigs { get; set; }

        /// <summary>
        /// Per-slot PlayStation configuration (Adaptive Triggers + Lighting).
        /// Null on settings files older than v3.1.0 — slots fall back to
        /// out-of-the-box defaults (all triggers Off, lightbar disabled,
        /// audio neutral) so the schema add is a clean no-op for legacy files.
        /// </summary>
        [XmlArray("PlayStationConfigs")]
        [XmlArrayItem("Config")]
        public ViewModels.PlayStationSlotConfigData[] PlayStationConfigs { get; set; }

        /// <summary>
        /// User-imported HIDMaestro profile JSONs, captured via
        /// HMDeviceExtractor from controllers the user owns. Each entry is
        /// the full profile JSON (as emitted by HMDeviceExtractor.ToJson)
        /// stored verbatim so no in-app serializer has to know the
        /// schema — HIDMaestro parses them through its own loader on
        /// startup. Appear in the Extended dropdown alongside the built-in
        /// catalog after the next catalog reload.
        /// </summary>
        [XmlArray("UserProfiles")]
        [XmlArrayItem("Profile")]
        public UserProfileData[] UserProfiles { get; set; }

        /// <summary>
        /// Per-slot MIDI configuration (port, channel, CC/note mappings).
        /// Null on old settings files — uses defaults.
        /// </summary>
        [XmlArray("MidiConfigs")]
        [XmlArrayItem("Config")]
        public ViewModels.MidiSlotConfigData[] MidiConfigs { get; set; }

        /// <summary>
        /// Full snapshot of the default profile's state, saved when a named
        /// profile is active so the default can be restored on restart.
        /// Null when the default profile is active (its state is in the
        /// global UserSettings/SlotCreated/etc. fields).
        /// </summary>
        [XmlElement("DefaultProfileSnapshot")]
        public ProfileData DefaultProfileSnapshot { get; set; }

        /// <summary>
        /// Global macros for profile shortcuts and other app-wide actions.
        /// Null on old settings files — no shortcuts configured.
        /// </summary>
        [XmlArray("GlobalMacros")]
        [XmlArrayItem("GlobalMacro")]
        public GlobalMacroData[] GlobalMacros { get; set; }

    }

    /// <summary>
    /// Serializable DTO for a macro. Stored per pad slot.
    /// </summary>
    public class MacroData
    {
        [XmlAttribute]
        public int PadIndex { get; set; }

        [XmlElement]
        public string Name { get; set; } = "New Macro";

        [XmlElement]
        public bool IsEnabled { get; set; } = true;

        [XmlElement]
        public ushort TriggerButtons { get; set; }

        /// <summary>
        /// GUID of the device whose raw buttons are the trigger source (string form).
        /// Null/empty = use legacy Xbox bitmask path.
        /// </summary>
        [XmlElement]
        public string TriggerDeviceGuid { get; set; }

        /// <summary>
        /// Comma-separated raw button indices, e.g. "13,14".
        /// Null/empty = not using raw trigger path.
        /// </summary>
        [XmlElement]
        public string TriggerRawButtons { get; set; }

        [XmlElement]
        public MacroTriggerSource TriggerSource { get; set; }

        [XmlElement]
        public MacroTriggerMode TriggerMode { get; set; }

        [XmlElement]
        public bool ConsumeTriggerButtons { get; set; } = true;

        [XmlElement]
        public MacroRepeatMode RepeatMode { get; set; }

        [XmlElement]
        public int RepeatCount { get; set; } = 1;

        [XmlElement]
        public int RepeatDelayMs { get; set; } = 100;

        /// <summary>Hex-encoded custom Extended trigger button words (e.g. "00000003,00000000,00000000,00000000").</summary>
        [XmlElement]
        public string TriggerCustomButtons { get; set; }

        /// <summary>Comma-separated axis targets (e.g. "LeftStickX,LeftTrigger").</summary>
        [XmlElement]
        public string TriggerAxisTargets { get; set; }

        /// <summary>Axis trigger threshold percentage (1-100).</summary>
        [XmlElement]
        public int TriggerAxisThreshold { get; set; } = 50;

        /// <summary>POV triggers stored as "povIndex:centidegrees" entries.</summary>
        [XmlArray("TriggerPovs")]
        [XmlArrayItem("Pov")]
        public string[] TriggerPovs { get; set; }

        [XmlArray("Actions")]
        [XmlArrayItem("Action")]
        public ActionData[] Actions { get; set; }
    }

    /// <summary>
    /// Serializable DTO for a single macro action.
    /// </summary>
    public class ActionData
    {
        [XmlElement]
        public MacroActionType Type { get; set; }

        [XmlElement]
        public ushort ButtonFlags { get; set; }

        /// <summary>Hex-encoded custom Extended button words for this action.</summary>
        [XmlElement]
        public string CustomButtons { get; set; }

        [XmlElement]
        public int KeyCode { get; set; }

        /// <summary>
        /// Multi-key combo in "{Key1}{Key2}..." format. Takes precedence over KeyCode.
        /// </summary>
        [XmlElement]
        public string KeyString { get; set; }

        [XmlElement]
        public int DurationMs { get; set; } = 50;

        [XmlElement]
        public short AxisValue { get; set; }

        [XmlElement]
        public MacroAxisTarget AxisTarget { get; set; }

        [XmlElement]
        public MacroAxisSource AxisSource { get; set; }

        /// <summary>GUID of the source device for InputDevice axis source (string form).</summary>
        [XmlElement]
        public string SourceDeviceGuid { get; set; }

        [XmlElement]
        public int SourceDeviceAxisIndex { get; set; }

        /// <summary>Process name for AppVolume action (e.g., "firefox", "spotify").</summary>
        [XmlElement]
        public string ProcessName { get; set; }

        /// <summary>Maximum volume percentage (1-100) for SystemVolume/AppVolume. Default 100 (no limit).</summary>
        [XmlElement]
        public int VolumeLimit { get; set; } = 100;

        /// <summary>Pixels/scroll units per frame at full deflection for MouseMove/MouseScroll.</summary>
        [XmlElement]
        public float MouseSensitivity { get; set; } = 10f;

        /// <summary>Which mouse button for MouseButtonPress/MouseButtonRelease.</summary>
        [XmlElement]
        public MacroMouseButton MouseButton { get; set; }

        /// <summary>When true, invert the axis value (0→1 becomes 1→0).</summary>
        [XmlElement]
        public bool InvertAxis { get; set; }

        /// <summary>When true, show the Windows volume flyout OSD on volume changes.</summary>
        [XmlElement]
        public bool ShowVolumeOsd { get; set; } = true;

        /// <summary>Lightbar override RGB for
        /// MacroActionType.LightbarColor with ColorSource = Fixed (or
        /// any Sticky hold). Default white so a freshly-added action
        /// produces a visible flash on first test fire.</summary>
        [XmlElement] public byte LightbarR { get; set; } = 0xFF;
        [XmlElement] public byte LightbarG { get; set; } = 0xFF;
        [XmlElement] public byte LightbarB { get; set; } = 0xFF;
        /// <summary>Reactive (decay-fade) or Sticky hold for
        /// LightbarColor.</summary>
        [XmlElement] public ViewModels.MacroLightbarHoldMode LightbarHoldMode { get; set; } = ViewModels.MacroLightbarHoldMode.Reactive;
        /// <summary>Color source for Reactive holds (Fixed / RandomHue /
        /// PaletteStep). Sticky always uses Fixed.</summary>
        [XmlElement] public ViewModels.MacroLightbarColorSource LightbarColorSource { get; set; } = ViewModels.MacroLightbarColorSource.Fixed;
        /// <summary>Hold window for Reactive holds, ms (full intensity
        /// for this duration before fade begins).</summary>
        [XmlElement] public int LightbarHoldMs { get; set; } = 0;
        /// <summary>Fade window for Reactive holds, ms (linear fade
        /// from full to off after the hold elapses).</summary>
        [XmlElement] public int LightbarFadeMs { get; set; } = 600;
        /// <summary>CSV of "RRGGBB" hex triplets for the per-macro
        /// palette used by PaletteStep. Empty falls back to the slot's
        /// own LightbarPalette.</summary>
        [XmlElement] public string LightbarPaletteCsv { get; set; } = string.Empty;
        /// <summary>Target mode for LightbarModeSet.</summary>
        [XmlElement] public ViewModels.LightbarMode LightbarTargetMode { get; set; } = ViewModels.LightbarMode.Static;
        /// <summary>CSV of LightbarMode int values for LightbarModeCycle.</summary>
        [XmlElement] public string LightbarCycleModesCsv { get; set; } = "1,2,3,4,11,12,13";
    }

    /// <summary>
    /// A single user-imported HIDMaestro profile stored in PadForge.xml.
    /// Id is the stable profile slug (dedup key across imports); Json is
    /// the full HMProfile JSON as emitted by HMDeviceExtractor.ToJson. The
    /// JSON is stored verbatim as element text so HIDMaestro's own loader
    /// parses it, letting us avoid a parallel schema in PadForge.
    /// </summary>
    public class UserProfileData
    {
        [XmlAttribute] public string Id { get; set; } = string.Empty;
        [XmlText] public string Json { get; set; } = string.Empty;
    }

    /// <summary>
    /// A named profile that stores per-device PadSettings and macros.
    /// When auto-switching is enabled, profiles activate when a matching
    /// executable's window comes to the foreground.
    /// </summary>
    public class ProfileData
    {
        [XmlAttribute]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [XmlElement]
        public string Name { get; set; } = "New Profile";

        /// <summary>
        /// Pipe-separated full executable paths (e.g. "C:\Games\game.exe|D:\Other\game2.exe").
        /// Case-insensitive matching against the foreground window's process path.
        /// </summary>
        [XmlElement]
        public string ExecutableNames { get; set; } = string.Empty;

        [XmlArray("Entries")]
        [XmlArrayItem("Entry")]
        public ProfileEntry[] Entries { get; set; }

        [XmlArray("ProfilePadSettings")]
        [XmlArrayItem("PadSetting")]
        public PadSetting[] PadSettings { get; set; }

        /// <summary>
        /// Per-VC mapping tables captured with this profile (Issue #61).
        /// One <see cref="MappingSet"/> per slot. Null on profiles
        /// captured before multi-source landed — ApplyProfile falls back
        /// to per-device PadSetting in that case via the legacy migrator.
        /// </summary>
        [XmlArray("ProfileSlotMappingSets")]
        [XmlArrayItem("MappingSet")]
        public MappingSet[] SlotMappingSets { get; set; }

        [XmlArray("ProfileMacros")]
        [XmlArrayItem("Macro")]
        public MacroData[] Macros { get; set; }

        /// <summary>
        /// Which virtual controller slots were created when this profile was saved.
        /// Null on old profiles — topology application is skipped.
        /// </summary>
        [XmlArray("ProfileSlotCreated")]
        [XmlArrayItem("Created")]
        public bool[] SlotCreated { get; set; }

        /// <summary>
        /// Which virtual controller slots were enabled when this profile was saved.
        /// Null on old profiles — topology application is skipped.
        /// </summary>
        [XmlArray("ProfileSlotEnabled")]
        [XmlArrayItem("Enabled")]
        public bool[] SlotEnabled { get; set; }

        /// <summary>
        /// Per-slot virtual controller output types (VirtualControllerType enum cast to int).
        /// Null on old profiles — topology application is skipped.
        /// </summary>
        [XmlArray("ProfileSlotControllerTypes")]
        [XmlArrayItem("Type")]
        public int[] SlotControllerTypes { get; set; }

        /// <summary>
        /// Per-slot HIDMaestro profile slug saved with this profile. Added
        /// in v3.0.0; null on profiles saved by v2.x.
        /// </summary>
        [XmlArray("ProfileSlotProfileIds")]
        [XmlArrayItem("Id")]
        public string[] SlotProfileIds { get; set; }

        /// <summary>Per-slot Extended configurations saved with this profile.</summary>
        [XmlArray("ProfileExtendedConfigs")]
        [XmlArrayItem("ExtendedConfig")]
        public ViewModels.ExtendedSlotConfigData[] ExtendedConfigs { get; set; }

        /// <summary>Per-slot PlayStation configurations (Adaptive Triggers
        /// + Lighting) saved with this profile. Null on profiles predating
        /// v3.1.0; defaults applied on load.</summary>
        [XmlArray("ProfilePlayStationConfigs")]
        [XmlArrayItem("PlayStationConfig")]
        public ViewModels.PlayStationSlotConfigData[] PlayStationConfigs { get; set; }

        /// <summary>
        /// Per-group ordered list of pad indices in user-facing visual order
        /// at profile-save time. Null on profiles predating the per-group
        /// ordering refactor; activation reconstructs ascending pad-index
        /// defaults via <c>SettingsManager.SlotOrders.RebuildFromCurrentTopology</c>.
        /// </summary>
        // XmlArray name kept as "ProfileMicrosoftSlotOrder" for v2/early-v3
        // PadForge.xml back-compat. The C# property name follows the
        // user-facing "Xbox" family naming.
        [XmlArray("ProfileMicrosoftSlotOrder")]
        [XmlArrayItem("PadIndex")]
        public int[] XboxSlotOrder { get; set; }

        [XmlArray("ProfilePlayStationSlotOrder")]
        [XmlArrayItem("PadIndex")]
        public int[] PlayStationSlotOrder { get; set; }

        [XmlArray("ProfileExtendedSlotOrder")]
        [XmlArrayItem("PadIndex")]
        public int[] ExtendedSlotOrder { get; set; }

        [XmlArray("ProfileKeyboardMouseSlotOrder")]
        [XmlArrayItem("PadIndex")]
        public int[] KeyboardMouseSlotOrder { get; set; }

        [XmlArray("ProfileMidiSlotOrder")]
        [XmlArrayItem("PadIndex")]
        public int[] MidiSlotOrder { get; set; }

        /// <summary>Per-slot MIDI configurations saved with this profile.</summary>
        [XmlArray("ProfileMidiConfigs")]
        [XmlArrayItem("MidiConfig")]
        public ViewModels.MidiSlotConfigData[] MidiConfigs { get; set; }

        /// <summary>Whether the DSU motion server was enabled in this profile.</summary>
        [XmlElement]
        public bool EnableDsuMotionServer { get; set; }

        /// <summary>DSU motion server port for this profile.</summary>
        [XmlElement]
        public int DsuMotionServerPort { get; set; } = 26760;

        /// <summary>Whether the web controller server was enabled in this profile.</summary>
        [XmlElement]
        public bool EnableWebController { get; set; }

        /// <summary>Web controller server port for this profile.</summary>
        [XmlElement]
        public int WebControllerPort { get; set; } = 8080;

        [XmlElement]
        public bool EnableTouchpadOverlay { get; set; }

        [XmlElement]
        public double TouchpadOverlayOpacity { get; set; } = 0.25;

        [XmlElement]
        public int TouchpadOverlayMonitor { get; set; }

        [XmlElement]
        public double TouchpadOverlayLeft { get; set; } = -1;

        [XmlElement]
        public double TouchpadOverlayTop { get; set; } = -1;

        [XmlElement]
        public double TouchpadOverlayWidth { get; set; } = 500;

        [XmlElement]
        public double TouchpadOverlayHeight { get; set; } = 250;
    }

    /// <summary>
    /// Links a device (by instance GUID) to a slot and PadSetting within a profile.
    /// ProductGuid enables fallback matching when InstanceGuid changes (BT reconnect).
    /// </summary>
    public class ProfileEntry
    {
        [XmlElement]
        public Guid InstanceGuid { get; set; }

        [XmlElement]
        public Guid ProductGuid { get; set; }

        [XmlElement]
        public int MapTo { get; set; }

        [XmlElement]
        public string PadSettingChecksum { get; set; }
    }

    /// <summary>
    /// A global macro that runs regardless of which profile is active.
    /// Currently used for profile shortcuts; future uses include overlay
    /// toggles, global volume, etc.
    /// </summary>
    public class GlobalMacroData
    {
        [XmlAttribute]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [XmlElement]
        public SwitchProfileMode SwitchMode { get; set; }

        /// <summary>For Specific mode: target profile ID. Null for Next/Previous.</summary>
        [XmlElement]
        public string TargetProfileId { get; set; }

        /// <summary>
        /// Per-button trigger entries. Each entry has a button index, the device instance
        /// GUID it was recorded from, and the product GUID for same-type matching.
        /// Supports cross-device combos (e.g., Shift on keyboard + Start on gamepad).
        /// </summary>
        [XmlArray("TriggerEntries")]
        [XmlArrayItem("Entry")]
        public TriggerButtonEntry[] TriggerEntries { get; set; }

        /// <summary>Legacy: flat button index array from old XML. Migrated on load.</summary>
        [XmlArray("TriggerButtons")]
        [XmlArrayItem("Index")]
        public int[] LegacyTriggerRawButtons { get; set; }

        /// <summary>Legacy: single device GUID from old XML.</summary>
        [XmlElement]
        public Guid TriggerDeviceGuid { get; set; }

        /// <summary>Runtime-only: previous frame trigger state for edge detection.</summary>
        [XmlIgnore]
        public bool WasTriggerActive { get; set; }

        /// <summary>True if this macro has any trigger buttons configured.</summary>
        [XmlIgnore]
        public bool HasTrigger => TriggerEntries != null && TriggerEntries.Length > 0;

        /// <summary>
        /// Migrates legacy flat button array to per-button entries.
        /// Called once during settings load.
        /// </summary>
        public void MigrateLegacyTrigger()
        {
            if (TriggerEntries != null || LegacyTriggerRawButtons == null)
                return;

            TriggerEntries = new TriggerButtonEntry[LegacyTriggerRawButtons.Length];
            for (int i = 0; i < LegacyTriggerRawButtons.Length; i++)
            {
                TriggerEntries[i] = new TriggerButtonEntry
                {
                    ButtonIndex = LegacyTriggerRawButtons[i],
                    DeviceInstanceGuid = TriggerDeviceGuid,
                    DeviceProductGuid = System.Guid.Empty
                };
            }
            LegacyTriggerRawButtons = null; // Clear so next save uses new format.
        }
    }

    /// <summary>
    /// A single button in a global macro trigger combo. Each button tracks
    /// which device it was recorded from, enabling cross-device combos.
    /// </summary>
    public class TriggerButtonEntry
    {
        /// <summary>Raw button index on the source device (when IsAxis=false).</summary>
        [XmlElement]
        public int ButtonIndex { get; set; }

        /// <summary>True if this entry represents an axis threshold, not a button.</summary>
        [XmlElement]
        public bool IsAxis { get; set; }

        /// <summary>Raw axis index on the source device (when IsAxis=true).</summary>
        [XmlElement]
        public int AxisIndex { get; set; }

        /// <summary>
        /// Axis threshold as normalized value (0.0–1.0). The axis must exceed this
        /// value to be considered active. Default 0.5 (50%).
        /// </summary>
        [XmlElement]
        public float AxisThreshold { get; set; } = 0.5f;

        /// <summary>
        /// Axis direction: Positive (axis > threshold) or Negative (axis &lt; 1-threshold).
        /// </summary>
        [XmlElement]
        public AxisTriggerDirection AxisDirection { get; set; }

        /// <summary>Instance GUID of the device this entry was recorded from.</summary>
        [XmlElement]
        public Guid DeviceInstanceGuid { get; set; }

        /// <summary>Product GUID for same-type device matching in "Any Device" mode.</summary>
        [XmlElement]
        public Guid DeviceProductGuid { get; set; }
    }

    public enum AxisTriggerDirection
    {
        Positive, // Axis value above threshold (e.g., stick right, trigger pulled)
        Negative  // Axis value below 1-threshold (e.g., stick left)
    }

    public enum SwitchProfileMode
    {
        Specific,
        Next,
        Previous,
        ToggleWindow
    }
}
