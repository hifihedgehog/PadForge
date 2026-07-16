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

        /// <summary>App-layer hook for fetching the active profile's
        /// custom touchpad gestures at save time. SettingsService
        /// doesn't own the live list (that's InputService._activeTouchpadGestures);
        /// this provider lets the save paths pull from it without a
        /// reverse reference. Returns null = no gestures to persist.</summary>
        public System.Func<PadForge.Engine.Touchpad.TouchpadCustomGesture[]> TouchpadGesturesProvider { get; set; }

        private System.Action<PadForge.Engine.Touchpad.TouchpadCustomGesture[]> _touchpadGesturesApplier;
        private PadForge.Engine.Touchpad.TouchpadCustomGesture[] _pendingTouchpadGesturesToApply;

        /// <summary>App-layer hook for restoring touchpad gestures
        /// after a settings load — InputService re-seeds its working
        /// list from whatever was persisted (default-profile gestures
        /// from AppSettings.TouchpadGestures, named-profile gestures
        /// from each ProfileData.TouchpadGestures, etc.).
        ///
        /// <para>The setter auto-flushes any gestures stashed by an
        /// earlier load that ran before the applier was wired —
        /// SettingsService.LoadFromFile runs at startup BEFORE
        /// InputService.StartEngine attaches the applier, so the
        /// load path stashes the loaded gestures in a pending slot
        /// and this property's setter invokes them on first
        /// assignment.</para></summary>
        public System.Action<PadForge.Engine.Touchpad.TouchpadCustomGesture[]> TouchpadGesturesApplier
        {
            get => _touchpadGesturesApplier;
            set
            {
                _touchpadGesturesApplier = value;
                if (value != null && _pendingTouchpadGesturesToApply != null)
                {
                    var pending = _pendingTouchpadGesturesToApply;
                    _pendingTouchpadGesturesToApply = null;
                    try { value(pending); } catch { /* applier is best-effort */ }
                }
            }
        }

        /// <summary>Installs a custom-gesture catalog through the applier when
        /// it is wired, or stages it for the applier's setter to flush when it
        /// is not. Both load-path callers (the default catalog in
        /// LoadAppSettings, the active named profile's own catalog in
        /// LoadProfiles) share this so the LAST caller wins, which is what
        /// makes the named profile's catalog override the default's on a cold
        /// start. Kept as one helper because the two call sites diverging is
        /// exactly how the named profile's gestures got lost.</summary>
        private void ApplyOrStageTouchpadGestures(PadForge.Engine.Touchpad.TouchpadCustomGesture[] gestures)
        {
            if (_touchpadGesturesApplier != null)
                _touchpadGesturesApplier(gestures);
            else
                _pendingTouchpadGesturesToApply = gestures;
        }

        /// <summary>
        /// Full path to the active settings file.
        /// </summary>
        public string SettingsFilePath => _settingsFilePath;

        /// <summary>
        /// Runtime holder for the Remote Link (issue #138) identity + trust list,
        /// loaded from AppSettings and written back by BuildAppSettings. The static
        /// identity is minted lazily on first Remote Link use, not on load, so
        /// loading touches no behavior. Not serialized directly — it mirrors the
        /// AppSettingsData.RemoteLink* fields.
        /// </summary>
        public sealed class RemoteLinkRuntime
        {
            public string ProtectedPrivateBase64 { get; set; } = "";
            public string PublicBase64 { get; set; } = "";
            /// <summary>How the private key above is wrapped at rest. Default machine-bound
            /// (Secure); the user can switch to a portable mode for thumb-drive use.</summary>
            public PadForge.Engine.RemoteLink.IdentityProtectionMode IdentityProtection { get; set; }
                = PadForge.Engine.RemoteLink.IdentityProtectionMode.Secure;
            public PadForge.Engine.RemoteLink.PeerTrustStore Trust { get; set; }
                = new PadForge.Engine.RemoteLink.PeerTrustStore();
        }

        /// <summary>The loaded Remote Link identity + trust list (see <see cref="RemoteLinkRuntime"/>).</summary>
        public RemoteLinkRuntime RemoteLink { get; private set; } = new RemoteLinkRuntime();

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

            // Wire the post-MergeMappingSetsFromLegacy hook so device-
            // assignment changes (DeviceService) trigger the motion-rows
            // backfill without each caller needing an instance reference.
            // See AfterMappingSetsRefreshed for context.
            AfterMappingSetsRefreshed = EnsureMotionRowsForAllSlots;
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

                // Schema migration: files saved before the v4.x rename spell
                // the per-(slot, device) config arrays PlayStationConfigs /
                // ProfilePlayStationConfigs. Move them to the new names
                // before anything consumes the data.
                data.MigrateLegacySchema();

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
                // Gated on the paired device actually having a touchpad:
                // "Button 11" is ALSO a legitimate descriptor for a generic
                // device's twelfth button deliberately mapped to the
                // touchpad-click target (arcade sticks, button boxes), and
                // the unconditional rewrite silently retargeted it to a
                // Buttons[16] slot such a device never populates. Only the
                // legacy Sony auto-map ever wrote "Button 11" here, and
                // Sony pads are exactly the devices that report a touchpad.
                if (data.PadSettings != null)
                {
                    foreach (var ps in data.PadSettings)
                    {
                        if (ps?.TouchpadClick != "Button 11") continue;
                        bool pairedTouchpadDevice = false;
                        if (data.Settings != null && data.Devices != null)
                        {
                            foreach (var us in data.Settings)
                            {
                                if (us == null || us.PadSettingChecksum == null
                                    || us.PadSettingChecksum != ps.PadSettingChecksum) continue;
                                foreach (var ud in data.Devices)
                                {
                                    if (ud != null && ud.InstanceGuid == us.InstanceGuid
                                        && ud.CapTouchpadCount > 0)
                                    {
                                        pairedTouchpadDevice = true;
                                        break;
                                    }
                                }
                                if (pairedTouchpadDevice) break;
                            }
                        }
                        if (pairedTouchpadDevice)
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

                // Backfill motion-passthrough rows for every Sony-class
                // slot. Runs after LoadAppSettings has populated slot
                // types so we know which slots are eligible. Idempotent
                // — a device already represented in the slot's motion
                // row is not re-added.
                EnsureMotionRowsForAllSlots();
            }
            catch (Exception ex)
            {
                // The file exists but couldn't be read. A later save would
                // overwrite it with defaults, so preserve the original first.
                try
                {
                    string bad = filePath + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    if (!File.Exists(bad))
                        File.Copy(filePath, bad);
                }
                catch { /* preservation is best-effort */ }

                // The app continues with defaults; seed the Profiles list
                // (built-in Default entry) the same way a fresh install does.
                if (_mainVm.Settings.ProfileItems.Count == 0)
                    LoadProfiles(null, null);

                _mainVm.SetStatus(string.Format(Strings.Instance.Status_ErrorLoadingSettings_Format, ex.Message), persist: true);
            }
        }

        /// <summary>
        /// Post-load backfill: for every slot, ensure the slot's
        /// <see cref="MappingSet"/> has motion-passthrough rows for every
        /// gyro / accel-capable assigned device. Sony-class slots only.
        /// Idempotent — safe to call multiple times.
        /// </summary>
        private void EnsureMotionRowsForAllSlots()
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || _mainVm?.Pads == null) return;

            UserDevice[] devSnapshot;
            UserSetting[] userSettingsSnapshot;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                devSnapshot = SettingsManager.UserDevices.Items.ToArray();
            }
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                userSettingsSnapshot = SettingsManager.UserSettings.Items.ToArray();
            }

            // Per-guid capability lookup. Devices not currently known
            // (offline / never enumerated) report no caps and get no row;
            // they'll be backfilled on the next load after they appear.
            (bool HasGyro, bool HasAccel, bool HasAccelAux) Caps(Guid guid)
            {
                foreach (var ud in devSnapshot)
                {
                    if (ud != null && ud.InstanceGuid == guid)
                        return (ud.HasGyro, ud.HasAccel, ud.HasAccelAux);
                }
                return (false, false, false);
            }

            for (int slot = 0; slot < sets.Length && slot < _mainVm.Pads.Count; slot++)
            {
                if (!SettingsManager.SlotCreated[slot]) continue;
                var slotType = _mainVm.Pads[slot].OutputType;

                var devicesForSlot = new List<(string DeviceGuid, bool HasGyro, bool HasAccel)>();
                foreach (var us in userSettingsSnapshot)
                {
                    if (us == null || us.MapTo != slot) continue;
                    var caps = Caps(us.InstanceGuid);
                    devicesForSlot.Add((us.InstanceGuid.ToString(), caps.HasGyro, caps.HasAccel));
                }
                if (devicesForSlot.Count == 0) continue;

                var ms = sets[slot] ?? (sets[slot] = new MappingSet());
                MappingSetMigrator.EnsureMotionRows(ms, (int)slotType, devicesForSlot);

                // Mirror the row presence into each device's PadSetting
                // MotionGyro / MotionAccel descriptor fields so the
                // mapping-table MappingItem (which reads via PadSetting
                // reflection) shows the right source name for the
                // row. Sony-class only — non-Sony slots have no
                // MotionRow and clear the fields.
                bool isSony = slotType == Engine.VirtualControllerType.PlayStation;
                foreach (var us in userSettingsSnapshot)
                {
                    if (us == null || us.MapTo != slot) continue;
                    var ps = us.GetPadSetting();
                    if (ps == null) continue;
                    var caps = Caps(us.InstanceGuid);
                    string newGyro  = (isSony && caps.HasGyro)  ? MappingSetMigrator.MotionGyroSourceDescriptor  : "";
                    string newAccel = (isSony && caps.HasAccel) ? MappingSetMigrator.MotionAccelSourceDescriptor : "";
                    // A user-picked aux source ("Motion Accel L", the Nunchuk /
                    // left Joy-Con, #199 follow-up) survives the recompute: the
                    // mirror only snaps between default-present and absent, and
                    // the authoritative MappingSet row already carries the pick.
                    if (isSony && caps.HasAccelAux
                        && MappingSetMigrator.IsMotionAccelAuxDescriptor(ps.MotionAccel))
                        newAccel = MappingSetMigrator.MotionAccelAuxSourceDescriptor;
                    if (ps.MotionGyro != newGyro || ps.MotionAccel != newAccel)
                    {
                        ps.MotionGyro = newGyro;
                        ps.MotionAccel = newAccel;
                        ps.UpdateChecksum();
                    }
                }
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

                // Content = rows OR menus OR shift activators. Gating on
                // rows alone discarded a rows-empty set on restart, deleting
                // any menus / activators it carried (a menus-only slot lost
                // its menu on every launch, Codex audit 2026-07-16).
                bool xmlHasContent = fromXml != null
                    && ((fromXml.Rows != null && fromXml.Rows.Count > 0)
                        || (fromXml.Menus != null && fromXml.Menus.Count > 0)
                        || (fromXml.ShiftActivators != null && fromXml.ShiftActivators.Count > 0));
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

            // Cross-device mappings are intentional: a keyboard key
            // mapped to ButtonA legitimately stores as
            // {DeviceGuid=keyboard, Descriptor="Button N"}. The
            // earlier non-gamepad-on-gamepad-target filter dropped
            // those legitimate rows and clobbered the user's mappings.
            // Sanitize now only dedups by (DeviceGuid, Descriptor) and
            // strips empty rows.
            foreach (var row in ms.Rows)
            {
                if (row?.Sources == null) continue;
                var seen = new HashSet<(string, string)>();
                int writeIdx = 0;
                for (int i = 0; i < row.Sources.Count; i++)
                {
                    var s = row.Sources[i];
                    if (s == null) continue;
                    var key = ((s.DeviceGuid ?? "").ToLowerInvariant(), s.Descriptor ?? "");
                    if (!seen.Add(key)) continue;
                    row.Sources[writeIdx++] = s;
                }
                if (writeIdx < row.Sources.Count)
                    row.Sources.RemoveRange(writeIdx, row.Sources.Count - writeIdx);
            }

            ms.Rows.RemoveAll(r => r?.Sources == null || r.Sources.Count == 0);
        }

        private static MappingSet BuildOneSlotFromLegacy(int slot)
        {
            UserSetting[] snapshot;
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                snapshot = SettingsManager.UserSettings.Items.ToArray();
            }

            // Look up each device's CapType so the migrator can skip
            // emitting gamepad-target sources for keyboards / mice /
            // touchpads (whose auto-mapped stale gamepad descriptors
            // would pollute the per-VC MappingSet — an axis source
            // pointing at state.Axis[0] reads uninitialized 0 → bipolar
            // -1 → joystick stuck pointing left during evaluation).
            //
            // Joystick-class devices (DirectInput joysticks, arcade
            // sticks, throttles, racing wheels, flight sticks) ARE
            // legitimate gamepad-target contributors — they expose real
            // axes / buttons / POVs and the user explicitly authored
            // mappings on them. Excluding them was dropping arcade-stick
            // and joystick mappings during legacy migration. The
            // controller-class set mirrors DeviceService.AutoEnableHidingDefaults.
            UserDevice[] devSnapshot;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                devSnapshot = SettingsManager.UserDevices.Items.ToArray();
            }

            bool IsControllerClass(Guid g)
            {
                foreach (var ud in devSnapshot)
                {
                    if (ud != null && ud.InstanceGuid == g)
                    {
                        return ud.CapType == InputDeviceType.Gamepad
                            || ud.CapType == InputDeviceType.Joystick
                            || ud.CapType == InputDeviceType.Driving
                            || ud.CapType == InputDeviceType.Flight
                            || ud.CapType == InputDeviceType.FirstPerson
                            || ud.CapType == InputDeviceType.Supplemental;
                    }
                }
                // Unknown device — be conservative and assume non-controller
                // so we don't pollute. The user can always re-record on
                // an actual controller.
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
                    IsControllerClass(us.InstanceGuid)));
            }

            return MappingSetMigrator.BuildFromLegacy(slot, devicesForSlot);
        }

        /// <summary>
        /// Phase 2C — push the per-VC PadViewModel's MappingItem
        /// <c>ExtraSources</c> + <c>CombineMode</c> + <c>CombineExpression</c>
        /// into the in-memory <see cref="SettingsManager.SlotMappingSets"/>
        /// so they survive the legacy-merge step and the XML round-trip.
        /// Runs in <see cref="SaveToFile"/> right after the UI → PadSetting
        /// push. Also exposed to the recording pipeline so a freshly-recorded
        /// mapping lands in the in-memory MappingSet immediately, keeping the
        /// per-VC Mappings tab consistent without waiting for the next
        /// debounced save / device-dropdown toggle.
        /// </summary>
        internal void PushUiExtraSourcesIntoSlotMappingSets()
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

                // Save into whichever layer the user is currently authoring.
                // Defaults to "Base"; switching the nested Mappings tab to a
                // shift layer routes edits into that layer's rows instead.
                // Rows for OTHER layers stay untouched — this slot can
                // accumulate multi-layer authoring without bleed.
                string activeMask = string.IsNullOrEmpty(padVm.ActiveLayerMask) ? "Base" : padVm.ActiveLayerMask;

                foreach (var mapping in padVm.Mappings)
                {
                    if (mapping == null || string.IsNullOrEmpty(mapping.TargetSettingName)) continue;

                    // Find or create the row for this Target on the active layer.
                    MappingRow row = null;
                    foreach (var r in ms.Rows)
                    {
                        if (r != null
                            && string.Equals(r.LayerMask ?? "Base", activeMask, StringComparison.Ordinal)
                            && string.Equals(r.Target, mapping.TargetSettingName, StringComparison.Ordinal))
                        {
                            row = r;
                            break;
                        }
                    }
                    if (row == null)
                    {
                        row = new MappingRow { Target = mapping.TargetSettingName, LayerMask = activeMask };
                        ms.Rows.Add(row);
                    }

                    row.CombineMode = mapping.CombineMode ?? "";
                    row.CombineExpression = mapping.CombineExpression ?? "";
                    row.TrimDeadzone = mapping.TrimDeadzone;
                    row.TrimRate = mapping.TrimRate;
                    row.TrimResetOnRelease = mapping.TrimResetOnRelease;

                    // NoInherit is meaningful only on non-Base rows. Force
                    // false on Base regardless of the MappingItem state so
                    // an unsetter on Base (defensive) can't leak.
                    row.NoInherit = !string.Equals(activeMask, "Base", StringComparison.Ordinal)
                                    && mapping.NoInherit;

                    // Preserve the absolute-pointer region geometry (#9 B-15)
                    // across the clear+rebuild below. Unlike flick (which has a
                    // hand-author card to re-stamp from), ParamPointerCenter/
                    // Extent have no VM card, so the row's own pre-rebuild
                    // sources are the only source of truth. Without this, the
                    // first pad-page save of an imported mouse_region mapping
                    // reset the geometry to the full-screen identity map.
                    var preservedPointer = CaptureTouchpadPointerParams(row);

                    // Same reason, same shape: preserve the half-axis output
                    // flip across the clear+rebuild below. InvertOutput has no
                    // VM card (the user cannot author it; only the Workshop
                    // translator and the legacy migrator emit it), and the
                    // rebuild reconstructs the primary from a legacy I/H
                    // prefix string that has no third flag to carry it. Without
                    // this, an imported source survived exactly until the first
                    // autosave and then silently lost its inversion.
                    var preservedInvertOutput = CaptureInvertOutputFlags(row);

                    // Phase 2C. Clear and rebuild Sources from the UI
                    // state so MappingSet is fully authoritative for
                    // this row. This stops keyboard primaries (which
                    // the legacy gamepad-only migrator filters out of
                    // rebuilt rows) from disappearing across save
                    // round-trips when a non-gamepad device authored
                    // the primary descriptor.
                    row.Sources.Clear();

                    // Push the primary as Sources[0] when present.
                    // Cross-device mappings are intentional — a keyboard
                    // key mapped to ButtonA legitimately lives here as
                    // {DeviceGuid=keyboard, Descriptor="Button 5"}, and
                    // an earlier defensive filter that rejected non-
                    // gamepad device sources on gamepad targets was
                    // dropping those legit rows. The SaveViewModelToPadSetting
                    // owning-device routing now prevents bleed at the
                    // source (the only legitimate path that writes a
                    // descriptor into a device's PadSetting), so the
                    // filter here is no longer needed.
                    string primaryDesc = mapping.SourceDescriptor ?? "";
                    bool primaryIsKind = mapping.PrimaryKindSource != null
                        && !string.Equals(mapping.PrimaryKindSource.Kind ?? "Direct", "Direct", StringComparison.Ordinal);
                    if (primaryIsKind)
                    {
                        // Non-Direct primary kind (Incremental / Ramped / InvertOnHold,
                        // #111 follow-up). The descriptor is unused; persist the kind and
                        // its params as Sources[0] so the load reads it as the primary.
                        row.Sources.Add(mapping.PrimaryKindSource.ToDomain());
                    }
                    else if (!string.IsNullOrEmpty(primaryDesc))
                    {
                        // Strip any I/H prefix off the descriptor so
                        // the new schema's per-source bool flags are
                        // the source of truth, matching how the
                        // migrator emits sources.
                        bool inv = false, half = false;
                        string clean = primaryDesc;
                        if (clean.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                        { inv = true; half = true; clean = clean.Substring(2); }
                        else if (clean.StartsWith("I", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1])
                                 && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(clean))
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
                            Bidirectional = mapping.IsBidirectional,
                            DeadZone = mapping.MappingDeadZone,
                            GyroSensitivity = mapping.GyroSensitivity > 0 ? mapping.GyroSensitivity : 1.0,
                            MouseCursorSensitivity = mapping.MouseCursorSensitivity > 0 ? mapping.MouseCursorSensitivity : 1.0,
                            IrPointerSensitivity = mapping.IrPointerSensitivity > 0 ? mapping.IrPointerSensitivity : 1.0,
                            Sensitivity = mapping.Sensitivity > 0 ? mapping.Sensitivity : 1.0,
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
                            else if (ncl.StartsWith("I", StringComparison.OrdinalIgnoreCase) && ncl.Length > 1 && !char.IsDigit(ncl[1])
                                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(ncl))
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
                                Bidirectional = mapping.IsBidirectional,
                                DeadZone = mapping.MappingDeadZone,
                                GyroSensitivity = mapping.GyroSensitivity > 0 ? mapping.GyroSensitivity : 1.0,
                                MouseCursorSensitivity = mapping.MouseCursorSensitivity > 0 ? mapping.MouseCursorSensitivity : 1.0,
                                IrPointerSensitivity = mapping.IrPointerSensitivity > 0 ? mapping.IrPointerSensitivity : 1.0,
                                Sensitivity = mapping.Sensitivity > 0 ? mapping.Sensitivity : 1.0,
                            });
                        }
                    }

                    foreach (var extra in mapping.ExtraSources)
                    {
                        if (extra != null) row.Sources.Add(extra.ToDomain());
                    }

                    // Steering source kind (#94): steering is a per-stick GLOBAL, not a
                    // per-layer mapping, so it has to live on the Base row that normal
                    // (no-shift) play evaluates. Only stamp here when authoring Base; the
                    // reconciliation pass below keeps the Base row in sync when the user is
                    // authoring a shift layer. Persists on MappingSource (survives clones
                    // via Clone()).
                    if (string.Equals(activeMask, "Base", StringComparison.Ordinal))
                        ApplySteeringKindToRow(row, mapping.TargetSettingName, padVm, slot);

                    // Motion Lean tuning rides every layer's rows (the descriptor is a
                    // normal input, not a Base-only steering mode).
                    ApplyMotionLeanParamsToRow(row, padVm, slot);

                    // Flick stick tuning (#225) rides every layer's rows the same
                    // way: the descriptor is a normal input and #225's headline is
                    // the shift-layer host, so layer rows must carry the card's
                    // knobs too.
                    ApplyFlickStickParamsToRow(row, padVm, slot);

                    // Restore the absolute-pointer region geometry captured
                    // before the rebuild (#9 B-15). No card sources these, so
                    // the pre-rebuild values are re-stamped onto the matching
                    // rebuilt pointer sources.
                    ApplyTouchpadPointerParamsToRow(row, preservedPointer);
                    ApplyInvertOutputFlagsToRow(row, preservedInvertOutput);
                }

                // Steering Kind reconciliation on the Base layer (#94). The per-mapping
                // loop above only rebuilds the active layer's rows, but steering Kind must
                // track the StickConfigItem state on the Base row regardless of which layer
                // is being authored. When authoring a shift layer, re-stamp the Base
                // steering rows when steering is on, and clear a stale Kind when it's off —
                // otherwise turning steering off from a shift layer leaves the Base row
                // stuck in a steering mode forever.
                if (!string.Equals(activeMask, "Base", StringComparison.Ordinal))
                {
                    foreach (var stick in padVm.GetSteerableSticks())
                    foreach (var target in new[] { stick.XTarget, stick.YTarget })
                    {
                        MappingRow baseRow = null;
                        foreach (var r in ms.Rows)
                        {
                            if (r != null
                                && string.Equals(r.LayerMask ?? "Base", "Base", StringComparison.Ordinal)
                                && string.Equals(r.Target, target, StringComparison.Ordinal))
                            { baseRow = r; break; }
                        }
                        if (baseRow?.Sources == null || baseRow.Sources.Count == 0) continue;
                        // Per-source stamp handles revert of non-steered sources internally.
                        ApplySteeringKindToRow(baseRow, target, padVm, slot);
                    }
                }
            }
        }

        // Stamps per-assigned-device steering onto EVERY source in a stick-axis row. Each
        // source's steering mode + tunables come from ITS OWN device's PadSetting, and it
        // reads that device's OWN stick axes (so two devices on one slot each get their own
        // wheel with the right axes). A source whose device isn't steering on this channel
        // is reverted to a plain Direct mapping on its own axis. Returns true if any source
        // on the row received a steering kind.
        private static bool ApplySteeringKindToRow(MappingRow row, string target, PadViewModel padVm, int slot)
        {
            if (row?.Sources == null || row.Sources.Count == 0 || padVm == null) return false;
            // Resolve this row's target to a stick index + its X/Y axis targets, covering both
            // the standard gamepad pair and an Extended custom layout's numbered sticks.
            if (!ResolveSteerTarget(padVm, target, out int stickIdx, out bool rowIsY, out string xTarget, out string yTarget))
                return false;
            bool any = false;

            foreach (var src in row.Sources)
            {
                if (src == null) continue;
                var cfg = ReadDeviceSteering(slot, src.DeviceGuid, stickIdx, padVm);
                // Motion Lean is NOT stamped here. It's a first-class input descriptor
                // ("Motion Lean" in the picker) the user maps to an axis like any gyro
                // input; its sources keep Kind=Direct, so the revert guard below leaves
                // them alone, and ApplyMotionLeanParamsToRow pushes the gyro-tab card's
                // tuning onto them. The old design (gyro-tab Enable + target dropdown
                // overriding the chosen stick axis's source with MotionLeanX) replaced
                // one input with another — removed.
                // AngleToAxisY outputs to the Y channel; every other mode to X. Only stamp
                // this source when its device's mode targets THIS row's channel.
                bool wantY = string.Equals(cfg.Kind, "AngleToAxisY", StringComparison.Ordinal);
                if (cfg.Active && wantY == rowIsY)
                {
                    src.Kind = cfg.Kind;
                    // Motion-lean reads gravity (by DeviceGuid), not stick axes. The 2D
                    // modes read the DEVICE's own X axis (Descriptor) + Y axis
                    // (ParamYDescriptor) — independent of which row we're stamping, so
                    // AngleToAxisY on the Y row still gets X from the X target.
                    if (!string.Equals(cfg.Kind, "MotionLeanX", StringComparison.Ordinal))
                    {
                        src.Descriptor = GetDeviceAxisDescriptor(padVm, xTarget, src.DeviceGuid);
                        src.ParamYDescriptor = GetDeviceAxisDescriptor(padVm, yTarget, src.DeviceGuid);
                    }
                    src.ParamWindRangeDeg = cfg.WindRange;
                    src.ParamWindPower = cfg.WindPower;
                    src.ParamWindUnwindRate = cfg.WindUnwind;
                    src.ParamAngleInnerDz = cfg.AngleInner;
                    src.ParamAngleOuterDz = cfg.AngleOuter;
                    src.ParamMotionInnerDz = cfg.MotionInner;
                    src.ParamMotionOuterDz = cfg.MotionOuter;
                    src.ParamControllerOrientation = cfg.Orient;
                    any = true;
                }
                else
                {
                    // Not the steering output for this source's device. Revert it to a plain
                    // Direct read of its own axis — but ONLY if it's currently a steering kind.
                    // A normal mapping the user put on this axis (a button, a two-button
                    // neg-pair, an axis) is left untouched: rewriting every source's descriptor
                    // here clobbered neg-pairs down to the primary and broke button→axis maps.
                    RevertSourceToDirect(src, target, padVm);
                }
            }
            return any;
        }

        private static bool IsSteeringKind(string k)
            => k == "WindingStick" || k == "AngleToAxisX" || k == "AngleToAxisY" || k == "MotionLeanX";

        // Reads one device's per-stick steering config from its own PadSetting (per
        // slot+device), applying the same defaults the UI load path uses. A guid-less
        // source falls back to the live UI config for the selected stick.
        private static (bool Active, string Kind, double WindRange, double WindPower, double WindUnwind,
            double AngleInner, double AngleOuter, double MotionInner, double MotionOuter, string Orient)
            ReadDeviceSteering(int slot, string deviceGuid, int stickIdx, PadViewModel padVm)
        {
            // The SELECTED device's latest steering lives in the live StickConfigs (the UI),
            // which can be newer than its PadSetting; read it there so the stamp never lags a
            // save. A guid-less source also uses the UI. Other devices read their own stored
            // PadSetting so each keeps its own wheel.
            Guid sel = padVm.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;
            bool useUi = string.IsNullOrEmpty(deviceGuid)
                || (sel != Guid.Empty && Guid.TryParse(deviceGuid, out var dg) && dg == sel);
            if (useUi)
            {
                var s = padVm.StickConfigs?.FirstOrDefault(x => x.Index == stickIdx);
                if (s == null) return (false, "Direct", 900, 1, 1800, 0, 10, 15, 135, "Forward");
                // Motion fields are constants now — per-stick Motion Lean moved to Motion
                // Steering, which supplies its own inner/outer/orient via the cfg override.
                return (s.IsSteeringActive, s.SteeringKind, s.WindRangeDeg, s.WindPower, s.WindUnwindRate,
                    s.AngleInnerDz, s.AngleOuterDz, 15, 135, "Forward");
            }
            PadSetting ps = Guid.TryParse(deviceGuid, out var g)
                ? SettingsManager.FindSettingByInstanceGuidAndSlot(g, slot)?.GetPadSetting()
                : null;
            if (ps == null) return (false, "Direct", 900, 1, 1800, 0, 10, 15, 135, "Forward");
            string kind = ps.GetExtendedMapping($"Stick{stickIdx}SteerKind");
            // MotionLeanX is no longer a per-stick mode — it's driven by Motion Steering
            // (gyro tab) via its own override. Treat a stored per-stick MotionLeanX as
            // Direct so an old profile doesn't ghost-lean while the new card sits idle.
            if (string.IsNullOrEmpty(kind) || kind == "MotionLeanX") kind = "Direct";
            double D(string key, double dflt)
                => double.TryParse(ps.GetExtendedMapping(key), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : dflt;
            return (kind != "Direct", kind,
                D($"Stick{stickIdx}SteerWindRange", 900),
                D($"Stick{stickIdx}SteerWindPower", 1),
                D($"Stick{stickIdx}SteerWindUnwind", 1800),
                D($"Stick{stickIdx}SteerAngleInner", 0),
                D($"Stick{stickIdx}SteerAngleOuter", 10),
                15, 135, "Forward");
        }

        // Resolves a stick-axis target (standard Left/Right or Extended ExtendedAxis{n}) to a
        // stick index + whether it's the Y axis, plus that stick's X/Y targets. One code path
        // for both layouts, driven by the slot's actual sticks (PadViewModel.GetSteerableSticks).
        private static bool ResolveSteerTarget(PadViewModel padVm, string target,
            out int stickIdx, out bool rowIsY, out string xTarget, out string yTarget)
        {
            stickIdx = -1; rowIsY = false; xTarget = ""; yTarget = "";
            if (string.IsNullOrEmpty(target)) return false;
            var sticks = padVm.GetSteerableSticks();
            for (int g = 0; g < sticks.Count; g++)
            {
                if (string.Equals(sticks[g].XTarget, target, StringComparison.Ordinal))
                { stickIdx = g; rowIsY = false; xTarget = sticks[g].XTarget; yTarget = sticks[g].YTarget; return true; }
                if (string.Equals(sticks[g].YTarget, target, StringComparison.Ordinal))
                { stickIdx = g; rowIsY = true;  xTarget = sticks[g].XTarget; yTarget = sticks[g].YTarget; return true; }
            }
            return false;
        }

        // Pushes the Motion Steering card's per-device tuning (tilt deadzones + grip
        // orientation) onto every "Motion Lean" source in a row. Motion Lean is a
        // first-class input descriptor — the user maps it to an axis from the picker
        // — so this NEVER changes a source's Kind, Descriptor, or target; it only
        // refreshes the lean parameters the evaluator reads (ParamMotion* on the
        // source). Mirrors ReadDeviceSteering's per-device resolution: the SELECTED
        // device reads the live UI (newer than its PadSetting), every other device
        // reads its own stored PadSetting so each keeps its own tilt tuning.
        private static void ApplyMotionLeanParamsToRow(MappingRow row, PadViewModel padVm, int slot)
        {
            if (row?.Sources == null || padVm == null) return;
            foreach (var src in row.Sources)
            {
                // Both lean families ride the same Motion Steering card
                // parameters: the primary "Motion Lean" and the aux
                // "Motion Lean L" (#199, Nunchuk / left Joy-Con).
                if (src == null
                    || (!Engine.Common.Mapping.SourceCoercion.IsMotionLeanDescriptor(src.Descriptor)
                        && !Engine.Common.Mapping.SourceCoercion.IsMotionLeanAuxDescriptor(src.Descriptor)))
                    continue;

                Guid sel = padVm.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;
                bool useUi = string.IsNullOrEmpty(src.DeviceGuid)
                    || (sel != Guid.Empty && Guid.TryParse(src.DeviceGuid, out var dg) && dg == sel);
                if (useUi)
                {
                    src.ParamMotionInnerDz = padVm.MotionSteerInnerDz;
                    src.ParamMotionOuterDz = padVm.MotionSteerOuterDz;
                    src.ParamControllerOrientation = padVm.MotionSteerOrient;
                    continue;
                }

                PadSetting ps = Guid.TryParse(src.DeviceGuid, out var g)
                    ? SettingsManager.FindSettingByInstanceGuidAndSlot(g, slot)?.GetPadSetting()
                    : null;
                if (ps == null) continue; // keep the source's existing params
                double D(string key, double dflt)
                    => double.TryParse(ps.GetExtendedMapping(key), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : dflt;
                string orient = ps.GetExtendedMapping("MotionSteerOrient");
                src.ParamMotionInnerDz = D("MotionSteerInner", 15);
                src.ParamMotionOuterDz = D("MotionSteerOuter", 135);
                src.ParamControllerOrientation = string.IsNullOrEmpty(orient) ? "Forward" : orient;
            }
        }

        // Pushes the Flick Stick card's per-device tuning (#225) onto every
        // "Flick Stick ..." source in a row. Same shape as
        // ApplyMotionLeanParamsToRow: never changes Kind, Descriptor, or
        // target; the SELECTED device reads the live UI, other devices read
        // their own stored PadSetting. A device whose PadSetting has never
        // stored the card ("FlickStickDots" absent) keeps the source's
        // existing params, so a fresh Workshop import's translator-carried
        // Dots Per 360 survives until the user actually tunes the card (the
        // load path seeds the card FROM the source in that state, so the
        // selected device's stamp writes the same values back).
        // Captures the absolute-pointer region geometry (#9 B-15) from a row's
        // sources before the save rebuild clears them. Keyed by (device,
        // descriptor) so a multi-source / multi-device row restores each
        // pointer source's own geometry. Returns an empty list when the row
        // holds no pointer source (the common case, zero allocation churn).
        private static System.Collections.Generic.List<(string device, string desc, double center, double extent)>
            CaptureTouchpadPointerParams(MappingRow row)
        {
            var list = new System.Collections.Generic.List<(string, string, double, double)>();
            if (row?.Sources == null) return list;
            foreach (var s in row.Sources)
            {
                if (s == null
                    || !Engine.Common.Mapping.SourceCoercion.IsTouchpadPointerDescriptor(s.Descriptor))
                    continue;
                list.Add((s.DeviceGuid ?? "", s.Descriptor ?? "", s.ParamPointerCenter, s.ParamPointerExtent));
            }
            return list;
        }

        // Re-stamps the captured pointer geometry onto the rebuilt pointer
        // sources. The rebuild strips I/H prefixes, but pointer descriptors
        // carry none (they start with "Touchpad"), so the clean descriptor
        // matches the captured one exactly.
        private static void ApplyTouchpadPointerParamsToRow(MappingRow row,
            System.Collections.Generic.List<(string device, string desc, double center, double extent)> preserved)
        {
            if (row?.Sources == null || preserved == null || preserved.Count == 0) return;
            foreach (var src in row.Sources)
            {
                if (src == null
                    || !Engine.Common.Mapping.SourceCoercion.IsTouchpadPointerDescriptor(src.Descriptor))
                    continue;
                foreach (var p in preserved)
                {
                    if (string.Equals(p.desc, src.Descriptor, StringComparison.Ordinal)
                        && string.Equals(p.device ?? "", src.DeviceGuid ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        src.ParamPointerCenter = p.center;
                        src.ParamPointerExtent = p.extent;
                        break;
                    }
                }
            }
        }

        // Captures the half-axis OUTPUT flip from a row's sources before the
        // save rebuild clears them. Direct analog of CaptureTouchpadPointerParams
        // above, for the same reason: MappingSource.InvertOutput has no VM card
        // to re-stamp from, so the row's own pre-rebuild sources are the only
        // source of truth.
        //
        // Why it needs preserving at all: the rebuild reconstructs each source
        // from the VM's legacy prefix-encoded descriptor, whose grammar is only
        // I / H / IH. There is no prefix for "select this half AND negate the
        // result", which is exactly what InvertOutput expresses, so a straight
        // rebuild drops it. MappingSourceItem.ToDomain / FromDomain omit it for
        // the same reason (see the N/A note there): a field the UI cannot author
        // is carried by a post-rebuild re-stamp instead.
        //
        // Keyed by (device, descriptor) so a multi-source / multi-device row
        // restores each source's own flag. Returns an empty list for the common
        // case of a row with no such source.
        private static System.Collections.Generic.List<(string device, string desc)>
            CaptureInvertOutputFlags(MappingRow row)
        {
            var list = new System.Collections.Generic.List<(string, string)>();
            if (row?.Sources == null) return list;
            foreach (var s in row.Sources)
            {
                if (s == null || !s.InvertOutput) continue;
                list.Add((s.DeviceGuid ?? "", s.Descriptor ?? ""));
            }
            return list;
        }

        // Re-stamps the captured output flips onto the rebuilt sources. The
        // rebuild strips the I/H prefix back off, so the rebuilt descriptor
        // matches the captured (already-clean) one exactly. A source the user
        // re-authored to a different descriptor no longer matches and correctly
        // keeps the default: the translator's polarity was about the descriptor
        // it was emitted for.
        private static void ApplyInvertOutputFlagsToRow(MappingRow row,
            System.Collections.Generic.List<(string device, string desc)> preserved)
        {
            if (row?.Sources == null || preserved == null || preserved.Count == 0) return;
            foreach (var src in row.Sources)
            {
                if (src == null) continue;
                foreach (var p in preserved)
                {
                    if (string.Equals(p.desc, src.Descriptor ?? "", StringComparison.Ordinal)
                        && string.Equals(p.device ?? "", src.DeviceGuid ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        src.InvertOutput = true;
                        break;
                    }
                }
            }
        }

        private static void ApplyFlickStickParamsToRow(MappingRow row, PadViewModel padVm, int slot)
        {
            if (row?.Sources == null || padVm == null) return;
            foreach (var src in row.Sources)
            {
                if (src == null
                    || !Engine.Common.Mapping.SourceCoercion.IsFlickStickDescriptor(src.Descriptor))
                    continue;

                Guid sel = padVm.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;
                bool useUi = string.IsNullOrEmpty(src.DeviceGuid)
                    || (sel != Guid.Empty && Guid.TryParse(src.DeviceGuid, out var dg) && dg == sel);
                if (useUi)
                {
                    src.ParamFlickCountsPer360 = padVm.FlickCountsPer360;
                    src.ParamFlickTime = padVm.FlickTime;
                    src.ParamFlickThreshold = padVm.FlickThreshold;
                    src.ParamFlickSnapMode = padVm.FlickSnapMode;
                    src.ParamFlickSnapStrength = padVm.FlickSnapStrength;
                    src.ParamFlickDeadzoneAngle = padVm.FlickForwardDeadzone;
                    src.ParamFlickSmooth = padVm.FlickSmoothing;
                    src.ParamFlickOnEngage = padVm.FlickOnEngage;
                    continue;
                }

                PadSetting ps = Guid.TryParse(src.DeviceGuid, out var g)
                    ? SettingsManager.FindSettingByInstanceGuidAndSlot(g, slot)?.GetPadSetting()
                    : null;
                if (ps == null) continue; // keep the source's existing params
                if (string.IsNullOrEmpty(ps.GetExtendedMapping("FlickStickDots")))
                    continue; // card never stored for this device: keep import values
                double D(string key, double dflt)
                    => double.TryParse(ps.GetExtendedMapping(key), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : dflt;
                string snap = ps.GetExtendedMapping("FlickStickSnapMode");
                src.ParamFlickCountsPer360 = D("FlickStickDots", 14400);
                src.ParamFlickTime = D("FlickStickTime", 0.1);
                src.ParamFlickThreshold = D("FlickStickThreshold", 0.9);
                src.ParamFlickSnapMode = string.IsNullOrEmpty(snap) ? "None" : snap;
                src.ParamFlickSnapStrength = D("FlickStickSnapStrength", 1.0);
                src.ParamFlickDeadzoneAngle = D("FlickStickForwardDz", 0);
                src.ParamFlickSmooth = D("FlickStickSmoothing", -1);
                src.ParamFlickOnEngage = ps.GetExtendedMapping("FlickStickOnEngage") == "1";
            }
        }

        /// <summary>Loads the Flick Stick card fields (#225) from a device's
        /// PadSetting extended mappings into the VM. When the card was never
        /// stored for this device ("FlickStickDots" absent) and the slot's
        /// MappingSet carries a flick source, the card seeds FROM that
        /// source, so a Workshop import's translator-carried Dots Per 360
        /// surfaces instead of defaults (and the next save persists it).
        /// One body shared by the startup load (LoadPadSettings) and the
        /// device-switch load (InputService.LoadPadSettingToViewModel) so
        /// the two legs cannot drift.</summary>
        internal static void LoadFlickStickCard(PadViewModel padVm, PadSetting ps)
        {
            if (padVm == null || ps == null) return;
            string dots = ps.GetExtendedMapping("FlickStickDots");
            if (string.IsNullOrEmpty(dots))
            {
                var seed = FindSlotFlickStickSource(padVm.PadIndex);
                if (seed != null)
                {
                    padVm.FlickCountsPer360 = seed.ParamFlickCountsPer360;
                    padVm.FlickTime = seed.ParamFlickTime;
                    padVm.FlickThreshold = seed.ParamFlickThreshold;
                    padVm.FlickSnapMode = seed.ParamFlickSnapMode;
                    padVm.FlickSnapStrength = seed.ParamFlickSnapStrength;
                    padVm.FlickForwardDeadzone = seed.ParamFlickDeadzoneAngle;
                    padVm.FlickSmoothing = seed.ParamFlickSmooth;
                    padVm.FlickOnEngage = seed.ParamFlickOnEngage;
                    return;
                }
            }
            double D(string key, double dflt)
                => double.TryParse(ps.GetExtendedMapping(key), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : dflt;
            string snap = ps.GetExtendedMapping("FlickStickSnapMode");
            padVm.FlickCountsPer360 = D("FlickStickDots", 14400);
            padVm.FlickTime = D("FlickStickTime", 0.1);
            padVm.FlickThreshold = D("FlickStickThreshold", 0.9);
            padVm.FlickSnapMode = string.IsNullOrEmpty(snap) ? "None" : snap;
            padVm.FlickSnapStrength = D("FlickStickSnapStrength", 1.0);
            padVm.FlickForwardDeadzone = D("FlickStickForwardDz", 0);
            padVm.FlickSmoothing = D("FlickStickSmoothing", -1);
            padVm.FlickOnEngage = ps.GetExtendedMapping("FlickStickOnEngage") == "1";
        }

        /// <summary>Writes the Flick Stick card fields (#225) into a device's
        /// PadSetting extended mappings. Shared by the serialize sweep
        /// (UpdatePadSettingsFromViewModels) and the device-switch save
        /// (InputService.SaveViewModelToPadSetting), the same mirror
        /// discipline the Motion Steering keys follow.</summary>
        internal static void SaveFlickStickCard(PadViewModel padVm, PadSetting ps)
        {
            if (padVm == null || ps == null) return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ps.SetExtendedMapping("FlickStickDots", padVm.FlickCountsPer360.ToString(ic));
            ps.SetExtendedMapping("FlickStickTime", padVm.FlickTime.ToString(ic));
            ps.SetExtendedMapping("FlickStickThreshold", padVm.FlickThreshold.ToString(ic));
            ps.SetExtendedMapping("FlickStickSnapMode", padVm.FlickSnapMode);
            ps.SetExtendedMapping("FlickStickSnapStrength", padVm.FlickSnapStrength.ToString(ic));
            ps.SetExtendedMapping("FlickStickForwardDz", padVm.FlickForwardDeadzone.ToString(ic));
            ps.SetExtendedMapping("FlickStickSmoothing", padVm.FlickSmoothing.ToString(ic));
            ps.SetExtendedMapping("FlickStickOnEngage", padVm.FlickOnEngage ? "1" : "0");
        }

        /// <summary>First "Flick Stick ..." source in a slot's MappingSet, or
        /// null. The load legs seed the Flick Stick card from it when the
        /// device's PadSetting has never stored the card, so a Workshop
        /// import's translator-carried tuning surfaces instead of defaults.</summary>
        internal static Engine.Data.MappingSource FindSlotFlickStickSource(int slot)
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || slot < 0 || slot >= sets.Length) return null;
            var rows = sets[slot]?.Rows;
            if (rows == null) return null;
            foreach (var row in rows)
            {
                var sources = row?.Sources;
                if (sources == null) continue;
                foreach (var src in sources)
                    if (src != null
                        && Engine.Common.Mapping.SourceCoercion.IsFlickStickDescriptor(src.Descriptor))
                        return src;
            }
            return null;
        }

        // The bare stick-axis descriptor a given device reads for <paramref name="target"/>
        // (e.g. "Axis 2"), from the UI mappings' primary source or the matching ExtraSource.
        // Empty when the device has no source on that target.
        private static string GetDeviceAxisDescriptor(PadViewModel padVm, string target, string deviceGuid)
        {
            var m = padVm?.Mappings?.FirstOrDefault(x => x.TargetSettingName == target);
            if (m == null) return "";
            if (string.Equals(m.PrimarySourceDeviceGuid ?? "", deviceGuid ?? "", StringComparison.OrdinalIgnoreCase))
                return StripSourcePrefix(m.SourceDescriptor);
            var extra = m.ExtraSources?.FirstOrDefault(e =>
                string.Equals(e.DeviceGuid ?? "", deviceGuid ?? "", StringComparison.OrdinalIgnoreCase));
            return extra != null ? StripSourcePrefix(extra.Descriptor) : "";
        }

        // Reverts ONE source to a plain Direct read of its row's own device axis — but only
        // if it's currently a steering kind. A normal Direct mapping (button, neg-pair, axis)
        // is left untouched so this stamp never rewrites the user's own axis mappings.
        private static void RevertSourceToDirect(Engine.Data.MappingSource src, string target, PadViewModel padVm)
        {
            if (src == null || !IsSteeringKind(src.Kind)) return;
            src.Kind = "Direct";
            src.Descriptor = GetDeviceAxisDescriptor(padVm, target, src.DeviceGuid);
            src.ParamYDescriptor = "";
            src.ParamWindRangeDeg = 0; src.ParamWindPower = 0; src.ParamWindUnwindRate = 0;
            src.ParamAngleInnerDz = 0; src.ParamAngleOuterDz = 0;
            src.ParamMotionInnerDz = 0; src.ParamMotionOuterDz = 0;
            src.ParamControllerOrientation = null;
        }

        // Strips a leading I / H / IH inversion prefix off a source descriptor, matching
        // the primary-source cleaning above; steering reads the bare axis.
        private static string StripSourcePrefix(string d)
        {
            if (string.IsNullOrEmpty(d)) return "";
            if (d.StartsWith("IH", StringComparison.OrdinalIgnoreCase)) return d.Substring(2);
            if (d.StartsWith("I", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1])
                && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(d)) return d.Substring(1);
            if (d.StartsWith("H", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1])) return d.Substring(1);
            return d;
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
        /// <summary>Post-refresh hook wired by the SettingsService instance
        /// constructor so static callers of <see cref="RefreshMappingSetsFromLegacy"/>
        /// trigger the per-instance motion-rows backfill without taking an
        /// instance reference. Lets DeviceService / MainWindow keep calling
        /// the static refresh while the backfill still runs.</summary>
        public static Action AfterMappingSetsRefreshed { get; set; }

        public static void RefreshMappingSetsFromLegacy()
        {
            MergeMappingSetsFromLegacy();
            AfterMappingSetsRefreshed?.Invoke();
        }

        /// <summary>Removes every source bound to the given device from
        /// every slot's MappingSet, drops any row that ends up empty as
        /// a result, and clears the device's cached PadSetting. Called
        /// on unassign so that an immediate reassign gets a clean auto-
        /// map slate — the prior mappings (auto-mapped or user-edited)
        /// must not persist across the unassign / reassign round-trip.</summary>
        public static void StripDeviceFromAllSlots(Guid instanceGuid)
        {
            string guidStr = instanceGuid.ToString().ToLowerInvariant();
            var sets = SettingsManager.SlotMappingSets;
            if (sets != null)
            {
                for (int slot = 0; slot < sets.Length; slot++)
                {
                    var ms = sets[slot];
                    if (ms?.Rows == null) continue;
                    foreach (var row in ms.Rows)
                    {
                        if (row?.Sources == null) continue;
                        row.Sources.RemoveAll(s =>
                            !string.IsNullOrEmpty(s?.DeviceGuid)
                            && string.Equals(s.DeviceGuid.ToLowerInvariant(), guidStr, StringComparison.Ordinal));
                    }
                    ms.Rows.RemoveAll(r => r?.Sources == null || r.Sources.Count == 0);
                }
            }
        }

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

                // Everything on the set that ISN'T Rows survives the merge
                // container swap. The merge rebuilds Base rows from the
                // per-device PadSetting fields, so every field the legacy
                // data can't repopulate — ownership, shift activators,
                // menus, and the Base layer's appearance — has to be
                // carried across by hand or it's dropped on every legacy
                // load. Lists carry by reference; `current` is discarded.
                var merged = new Engine.Data.MappingSet
                {
                    Authoritative = current.Authoritative,
                    ShiftActivators = current.ShiftActivators
                        ?? new List<Engine.Data.ShiftActivator>(),
                    Menus = current.Menus
                        ?? new List<PadForge.Engine.Menus.MenuDefinitionEntry>(),
                    BaseLayerName = current.BaseLayerName ?? "",
                    BaseColor = current.BaseColor ?? "",
                    BaseIcon = current.BaseIcon ?? "",
                };
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

                    // An authoritative set (Workshop import) owns its rows
                    // completely: the rebuilt-from-legacy set contributes
                    // nothing, because the imported rows already spell out
                    // every binding and auto-mapped legacy descriptors
                    // would double each input. Departed-device cleanup
                    // (above) and the empty-row drop (below) still run.
                    //
                    // Only Base-layer rows merge with rebuilt; non-Base
                    // (Shift) rows carry forward intact.
                    if (!current.Authoritative
                        && string.Equals(er.LayerMask ?? "Base", "Base", StringComparison.Ordinal)
                        && rebuiltByKey.TryGetValue(key, out var rrow))
                    {
                        // Only inject rebuilt sources for devices that
                        // aren't already represented on this row. If the
                        // row already has any source from a given device,
                        // the user's authoring is authoritative for that
                        // device — don't double-add the same device's
                        // auto-mapped legacy descriptor as an extra
                        // source (the "deleted extra keeps coming back"
                        // alias bug). Device-by-device gate is stricter
                        // than the previous (DeviceGuid, Descriptor)
                        // dedup which couldn't distinguish a
                        // user-deleted duplicate from a never-seen one.
                        var devicesPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var s in er.Sources)
                        {
                            if (s == null) continue;
                            devicesPresent.Add((s.DeviceGuid ?? "").ToLowerInvariant());
                        }
                        if (rrow.Sources != null)
                        {
                            foreach (var s in rrow.Sources)
                            {
                                if (s == null) continue;
                                if (devicesPresent.Contains((s.DeviceGuid ?? "").ToLowerInvariant()))
                                    continue;
                                er.Sources.Add(s);
                            }
                        }
                        // Only mark the rebuilt row consumed when the
                        // existing row actually has surviving sources.
                        // If every source dropped (e.g. the device that
                        // owned them left the slot), the existing row
                        // gets discarded below and we WANT the rebuilt
                        // row to land via the unconsumed-rebuilt pass
                        // — otherwise auto-mappings for a freshly-
                        // assigned replacement device vanish.
                        if (er.Sources != null && er.Sources.Count > 0)
                            consumedRebuilt.Add(key);
                    }

                    // Drop empty rows. They'll come back through the
                    // unconsumed-rebuilt pass below if the slot's
                    // current devices contribute anything for this
                    // target.
                    if (er.Sources == null || er.Sources.Count == 0) continue;

                    merged.Rows.Add(er);
                }

                // Add rebuilt rows that didn't match any existing row
                // (newly-added device whose auto-mapping introduces a
                // target the user hasn't authored yet). Skipped entirely
                // for authoritative sets, same rationale as above.
                if (!current.Authoritative)
                {
                    foreach (var rr in rebuilt.Rows)
                    {
                        if (rr == null) continue;
                        var key = (rr.Target ?? "", rr.LayerMask ?? "Base");
                        if (consumedRebuilt.Contains(key)) continue;
                        if (rr.Sources == null || rr.Sources.Count == 0) continue;
                        merged.Rows.Add(rr);
                    }
                }

                sets[slot] = merged;
            }
        }

        /// <summary>
        /// Pushes application-level settings to the SettingsViewModel.
        /// </summary>
        private void LoadAppSettings(AppSettingsData appSettings)
        {
            var vm = _mainVm.Settings;
            PadForge.Common.SoundPackageManager.LoadRegistry(
                appSettings.SoundPackages?.Select(p => (p.Name, p.Path)));
            PadForge.Common.Input.NfcTagRegistry.LoadRegistry(
                appSettings.NfcTags?.Select(t => (t.Uid, t.Name, t.Button)));

            // Remote Link (issue #138): carry the stored identity + trust list into
            // the runtime holder. No minting here — the identity is created lazily
            // on first Remote Link start — so this stays behavior-neutral on load.
            try
            {
                // Mutate the existing holder + trust store in place (don't swap the
                // instances) so a running LinkServer's references stay current across
                // a Reload — else peers paired after a reload would be dropped on save
                // and their gamepad-only restriction bypassed.
                RemoteLink.ProtectedPrivateBase64 = appSettings.RemoteLinkIdentityPrivate ?? "";
                RemoteLink.PublicBase64 = appSettings.RemoteLinkIdentityPublic ?? "";
                RemoteLink.IdentityProtection =
                    Enum.TryParse<PadForge.Engine.RemoteLink.IdentityProtectionMode>(appSettings.RemoteLinkIdentityProtection, out var ipm)
                        ? ipm : PadForge.Engine.RemoteLink.IdentityProtectionMode.Secure;
                RemoteLink.Trust.ReplaceAll(appSettings.RemoteLinkPeers);
            }
            catch (Exception ex)
            {
                // A partial failure here (e.g. ReplaceAll throwing on a malformed peer)
                // leaves the trust store half-restored, and the next Save() would persist
                // that loss back to PadForge.xml, silently dropping paired peers and their
                // gamepad-only restriction. Surface it instead of swallowing blind.
                System.Diagnostics.Debug.WriteLine("[RemoteLink] identity/trust restore failed: " + ex);
            }
            vm.RefreshTrustedPeers(RemoteLink.Trust?.Peers);
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
            // Default-profile custom-gesture catalog. InputService
            // wires the applier from StartEngine, which runs AFTER
            // this load path on cold start, so stash the loaded list
            // in a pending slot when the applier isn't ready yet. The
            // setter on TouchpadGesturesApplier flushes it on first
            // assignment. When a NAMED profile is active, the
            // block in LoadProfiles overrides this with that profile's
            // own catalog: ApplyProfile (and with it
            // ApplyProfileTouchpadGestures) does NOT run on the cold
            // path, so this stash would otherwise stay live and the
            // first autosave would write the default's gestures back
            // over the named profile's.
            ApplyOrStageTouchpadGestures(appSettings.TouchpadGestures);
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
            vm.EnableCommunityConfigLookup = appSettings.EnableCommunityConfigLookup;
            vm.ShowLegacyWorkshopConfigs = appSettings.ShowLegacyWorkshopConfigs;
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

            // Audio tab (issue #83): per-slot macro-sound master volume.
            if (appSettings.SlotSoundVolumes != null)
            {
                for (int i = 0; i < _mainVm.Pads.Count && i < appSettings.SlotSoundVolumes.Length; i++)
                    _mainVm.Pads[i].SoundMasterVolume = appSettings.SlotSoundVolumes[i];
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
            ApplyDeviceSlotConfigs(appSettings.DeviceSlotConfigs);
            ApplyMidiConfigs(appSettings.MidiConfigs);
            ApplyKbmConfigs(appSettings.KbmConfigs);

            // Load DSU motion server settings (now on Dashboard VM).
            _mainVm.Dashboard.EnableDsuMotionServer = appSettings.EnableDsuMotionServer;
            _mainVm.Dashboard.DsuMotionServerPort = appSettings.DsuMotionServerPort > 0
                ? appSettings.DsuMotionServerPort : 26760;

            // Load web controller server settings.
            _mainVm.Dashboard.EnableWebController = appSettings.EnableWebController;
            _mainVm.Dashboard.WebControllerPort = appSettings.WebControllerPort > 0
                ? appSettings.WebControllerPort : 8080;
            _mainVm.Dashboard.EnableRemoteLink = appSettings.EnableRemoteLink;
            _mainVm.Dashboard.AutoReconnect = appSettings.RemoteLinkAutoReconnect;
            _mainVm.Dashboard.RemoteLinkPort = appSettings.RemoteLinkPort >= 1024 && appSettings.RemoteLinkPort <= 65535
                ? appSettings.RemoteLinkPort : 27500;

            // Load touchpad overlay settings.
            _mainVm.Dashboard.EnableTouchpadOverlay = appSettings.EnableTouchpadOverlay;
            _mainVm.Dashboard.EnableMenuOverlay = appSettings.EnableMenuOverlay;
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
            vm.FirstRunTourCompleted = appSettings.FirstRunTourCompleted;

            // Restore main window position/size (profile-independent).
            vm.MainWindowLeft = appSettings.MainWindowLeft;
            vm.MainWindowTop = appSettings.MainWindowTop;
            vm.MainWindowWidth = appSettings.MainWindowWidth > 0 ? appSettings.MainWindowWidth : 1100;
            vm.MainWindowHeight = appSettings.MainWindowHeight > 0 ? appSettings.MainWindowHeight : 720;
            vm.MainWindowState = appSettings.MainWindowState;
            vm.MainWindowFullScreen = appSettings.MainWindowFullScreen;
        }

        /// <summary>Copy From companion: clones the per-slot config tabs
        /// (Lighting, custom Extended layout, MIDI CC/note layout) from
        /// <paramref name="srcSlot"/> to <paramref name="dstSlot"/>.
        /// Device features (lightbar / adaptive triggers / mic LED /
        /// player LED / audio-reactive / tone filter) are physical-device
        /// passthrough and copy unconditionally — a DualSense mapped to
        /// an Xbox slot still has its lightbar driven by DeviceConfig.
        /// Extended custom layouts and MIDI CC/note ranges are slot-shape
        /// data, so they only copy when both source and destination share
        /// that output type. Mappings and per-device tuning live on
        /// PadSetting and are handled separately by the InputService copy
        /// path; this method fills the gap for tabs that live on
        /// PadViewModel.</summary>
        public void CopySlotConfigsAcrossSlots(int srcSlot, int dstSlot)
        {
            if (srcSlot < 0 || dstSlot < 0) return;
            if (srcSlot >= _mainVm.Pads.Count || dstSlot >= _mainVm.Pads.Count) return;
            if (srcSlot == dstSlot) return;

            var src = _mainVm.Pads[srcSlot];
            var dst = _mainVm.Pads[dstSlot];
            if (src == null || dst == null) return;

            // The Lighting / Adaptive Triggers / Audio tabs are per-device. Match
            // each destination device to the SOURCE config for the same device GUID,
            // so a slot with two differently-configured controllers keeps both rather
            // than flattening to one. A destination device the source slot did not
            // have falls back to a representative source config (a configured anchor,
            // else the first configured per-device entry).
            dst.EnsureDeviceSlotConfigsForMappedDevices();
            var srcByGuid = src.PerDeviceSlotConfigs;
            var srcAnchor = src.DeviceConfig;
            var fallbackCfg = IsDeviceConfigConfigured(srcAnchor) ? srcAnchor : null;
            if (fallbackCfg == null && srcByGuid != null)
                foreach (var kvp in srcByGuid)
                    if (IsDeviceConfigConfigured(kvp.Value)) { fallbackCfg = kvp.Value; break; }
            fallbackCfg ??= srcAnchor;

            if (fallbackCfg != null || (srcByGuid != null && srcByGuid.Count > 0))
            {
                if (dst.PerDeviceSlotConfigs != null)
                {
                    foreach (var kvp in dst.PerDeviceSlotConfigs)
                    {
                        var dstCfg = kvp.Value;
                        if (dstCfg == null) continue;
                        var srcCfg = (srcByGuid != null && srcByGuid.TryGetValue(kvp.Key, out var m) && m != null)
                            ? m : fallbackCfg;
                        if (srcCfg != null)
                            ApplyDeviceSlotConfigData(dstCfg, BuildDeviceSlotConfigData(srcCfg, dstSlot, Guid.Empty));
                    }
                }
                // Anchor write only when a real device is selected on the
                // destination (otherwise dst.DeviceConfig is the shared
                // sentinel that would leak into every no-device view).
                if (dst.SelectedMappedDevice != null
                    && dst.SelectedMappedDevice.InstanceGuid != Guid.Empty)
                {
                    var selGuid = dst.SelectedMappedDevice.InstanceGuid;
                    var srcCfg = (srcByGuid != null && srcByGuid.TryGetValue(selGuid, out var m2) && m2 != null)
                        ? m2 : (srcAnchor ?? fallbackCfg);
                    if (srcCfg != null)
                        ApplyDeviceSlotConfigData(dst.DeviceConfig, BuildDeviceSlotConfigData(srcCfg, dstSlot, Guid.Empty));
                }
            }

            // Extended custom layout is virtual-controller-shape data
            // (thumbstick count / button count / OEM name / FFB), so it
            // only makes sense when both src and dst are Extended slots.
            if (src.OutputType == Engine.VirtualControllerType.Extended
                && dst.OutputType == Engine.VirtualControllerType.Extended)
            {
                var s = src.ExtendedConfig;
                var d = dst.ExtendedConfig;
                if (s != null && d != null)
                {
                    d.ThumbstickCount = s.ThumbstickCount;
                    d.TriggerCount = s.TriggerCount;
                    d.PovCount = s.PovCount;
                    d.ButtonCount = s.ButtonCount;
                    d.OemNameOverride = s.OemNameOverride;
                    d.ProductString = s.ProductString ?? string.Empty;
                    d.VendorId = s.VendorId;
                    d.ProductId = s.ProductId;
                    d.Customize = s.Customize;
                    d.ForceFeedbackEnabled = s.ForceFeedbackEnabled;
                }
            }

            // MIDI port layout (channel / velocity / CC + note ranges) is
            // also slot-output-shape data, only meaningful across MIDI slots.
            if (src.OutputType == Engine.VirtualControllerType.Midi
                && dst.OutputType == Engine.VirtualControllerType.Midi)
            {
                var s = src.MidiConfig;
                var d = dst.MidiConfig;
                if (s != null && d != null)
                {
                    d.Channel = s.Channel;
                    d.Velocity = s.Velocity;
                    d.StartCc = s.StartCc;
                    d.CcCount = s.CcCount;
                    d.StartNote = s.StartNote;
                    d.NoteCount = s.NoteCount;
                    dst.RebuildMappings();
                }
            }

            // SOCD (#205) is KBM slot-output-shape data, only meaningful
            // across Keyboard & Mouse slots.
            if (src.OutputType == Engine.VirtualControllerType.KeyboardMouse
                && dst.OutputType == Engine.VirtualControllerType.KeyboardMouse)
            {
                var s = src.KbmConfig;
                var d = dst.KbmConfig;
                if (s != null && d != null)
                {
                    d.SocdMode = s.SocdMode;
                    d.SocdPairs = s.SocdPairs;
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Copy / Paste clipboard helpers (per-slot)
        //
        //  Clipboard Copy/Paste needs to round-trip the per-slot config
        //  tabs (Lighting / Adaptive Triggers / Mic LED / Player LED /
        //  Extended custom layout / MIDI CC + note layout) across the
        //  serialization boundary. The existing Build* / Apply* methods
        //  above iterate every slot for profile storage; these per-slot
        //  variants snapshot or apply one slot at a time, mirroring the
        //  in-process CopySlotConfigsAcrossSlots semantics but via DTOs
        //  the caller can JSON-serialise.
        // ─────────────────────────────────────────────

        /// <summary>Snapshots every device config on a single slot
        /// (anchor + per-device entries). Returns an empty array when
        /// the slot has nothing configured. Caller is responsible for
        /// JSON-serialising the result into the clipboard payload.</summary>
        public ViewModels.DeviceSlotConfigData[] BuildDeviceConfigSnapshotForSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _mainVm.Pads.Count)
                return Array.Empty<ViewModels.DeviceSlotConfigData>();
            var padVm = _mainVm.Pads[slotIndex];
            if (padVm == null) return Array.Empty<ViewModels.DeviceSlotConfigData>();

            var list = new System.Collections.Generic.List<ViewModels.DeviceSlotConfigData>();
            if (padVm.DeviceConfig != null)
                list.Add(BuildDeviceSlotConfigData(padVm.DeviceConfig, slotIndex, Guid.Empty));
            if (padVm.PerDeviceSlotConfigs != null)
            {
                foreach (var kvp in padVm.PerDeviceSlotConfigs)
                {
                    if (kvp.Key == Guid.Empty || kvp.Value == null) continue;
                    list.Add(BuildDeviceSlotConfigData(kvp.Value, slotIndex, kvp.Key));
                }
            }
            return list.ToArray();
        }

        /// <summary>Paste companion. Applies a clipboard's device-config
        /// snapshot to the destination slot: the anchor entry
        /// (DeviceGuid = Empty) writes to <c>padVm.DeviceConfig</c>;
        /// per-device entries fan out across every entry already in the
        /// destination's <c>PerDeviceSlotConfigs</c> dict so
        /// device-switching on the destination doesn't bring back the
        /// old lightbar. Like the in-process Copy From, this runs
        /// unconditionally regardless of slot output type. Device
        /// features are physical-device passthrough.</summary>
        /// <summary>True when a device-config DTO carries any non-default
        /// device setting: lighting, adaptive triggers, Mic LED, Player LED, OR audio
        /// (passthrough / audio-lightbar / mirror source). The audio arm matters because
        /// the old "is this configured" checks ignored it, so an audio-only setup was
        /// treated as empty when Copy / Paste / Copy From picked a representative.</summary>
        public static bool IsDeviceSlotConfigDataConfigured(ViewModels.DeviceSlotConfigData c)
            => c != null
            // Rev-aware default checks: a rev-0 DTO (old save / old
            // profile store, not yet lifted by ApplyDeviceSlotConfigData)
            // spells "unset" as Off; rev-1 spells it PlayerNumber and
            // Off there is a deliberate, copy-worthy configuration.
            && (c.LightbarMode != (c.LightingRev >= 1
                    ? ViewModels.LightbarMode.PlayerNumber : ViewModels.LightbarMode.Off)
                || c.LeftTriggerMode != ViewModels.AdaptiveTriggerMode.Off
                || c.RightTriggerMode != ViewModels.AdaptiveTriggerMode.Off
                || c.MicLedMode != ViewModels.MicLedMode.Off
                || c.PlayerLedMode != (c.LightingRev >= 1
                    ? ViewModels.PlayerLedMode.PlayerNumber : ViewModels.PlayerLedMode.Off)
                // #209: a chosen Guide LED mode is a deliberate,
                // copy-worthy configuration (DeviceDefault writes nothing).
                || c.GuideLedMode != ViewModels.GuideLedMode.DeviceDefault
                || c.AudioPassthroughEnabled
                || c.AudioLightbarEnabled
                || !string.IsNullOrEmpty(c.AudioMirrorSourceId)
                // #185: a configured engage gate keeps the config alive even
                // while the passthrough toggle is momentarily off.
                || (c.AudioMirrorEngageMode != null && c.AudioMirrorEngageMode != "Always")
                || !string.IsNullOrEmpty(c.AudioMirrorEngageButton)
                // #202: same keep-alive rule. A chosen tone filter is a
                // deliberate, copy-worthy configuration.
                || (c.AudioToneFilterMode != null && c.AudioToneFilterMode != "Off"));

        /// <summary>VM-shape twin of <see cref="IsDeviceSlotConfigDataConfigured"/>,
        /// for the in-process Copy From path.</summary>
        public static bool IsDeviceConfigConfigured(ViewModels.DeviceSlotConfig c)
            => c != null
            && (c.LightbarMode != ViewModels.LightbarMode.PlayerNumber
                || c.LeftTriggerMode != ViewModels.AdaptiveTriggerMode.Off
                || c.RightTriggerMode != ViewModels.AdaptiveTriggerMode.Off
                || c.MicLedMode != ViewModels.MicLedMode.Off
                || c.PlayerLedMode != ViewModels.PlayerLedMode.PlayerNumber
                || c.GuideLedMode != ViewModels.GuideLedMode.DeviceDefault
                || c.AudioPassthroughEnabled
                || c.AudioLightbarEnabled
                || !string.IsNullOrEmpty(c.AudioMirrorSourceId)
                || c.AudioMirrorEngageMode != "Always"
                || !string.IsNullOrEmpty(c.AudioMirrorEngageButton)
                || c.AudioToneFilterMode != "Off");

        public void ApplyDeviceSlotConfigsToSlot(int slotIndex,
            ViewModels.DeviceSlotConfigData[] configs)
        {
            if (configs == null || configs.Length == 0) return;
            if (slotIndex < 0 || slotIndex >= _mainVm.Pads.Count) return;
            var padVm = _mainVm.Pads[slotIndex];
            if (padVm == null) return;

            // Index the snapshot by device GUID, plus the anchor (Empty GUID).
            // The copy carries one entry per source device, so the apply lands
            // each device's settings on the matching destination device instead
            // of flattening the whole slot to one representative config.
            var byGuid = new System.Collections.Generic.Dictionary<Guid, ViewModels.DeviceSlotConfigData>();
            ViewModels.DeviceSlotConfigData anchor = null;
            foreach (var c in configs)
            {
                if (c == null) continue;
                if (c.DeviceGuid == Guid.Empty) { anchor ??= c; continue; }
                byGuid[c.DeviceGuid] = c;
            }

            // Representative config for destination devices the source slot did
            // not have: a configured anchor, else the first configured per-device
            // entry, else the anchor so a new device still gets something.
            var fallback = IsDeviceSlotConfigDataConfigured(anchor) ? anchor : null;
            if (fallback == null)
                foreach (var c in configs)
                    if (IsDeviceSlotConfigDataConfigured(c)) { fallback = c; break; }
            fallback ??= anchor;
            if (fallback == null)
                foreach (var v in byGuid.Values) { fallback = v; break; }
            if (fallback == null) return;

            padVm.EnsureDeviceSlotConfigsForMappedDevices();
            if (padVm.PerDeviceSlotConfigs != null)
            {
                foreach (var kvp in padVm.PerDeviceSlotConfigs)
                {
                    if (kvp.Value == null) continue;
                    var srcData = byGuid.TryGetValue(kvp.Key, out var m) ? m : fallback;
                    ApplyDeviceSlotConfigData(kvp.Value, srcData);
                }
            }
            if (padVm.SelectedMappedDevice != null
                && padVm.SelectedMappedDevice.InstanceGuid != Guid.Empty)
            {
                var srcData = byGuid.TryGetValue(padVm.SelectedMappedDevice.InstanceGuid, out var m2)
                    ? m2 : (anchor ?? fallback);
                ApplyDeviceSlotConfigData(padVm.DeviceConfig, srcData);
            }
        }

        /// <summary>Snapshots the Extended custom layout on a single
        /// slot. Returns null when the slot isn't Extended or has no
        /// config. Caller JSON-serialises the result.</summary>
        public ViewModels.ExtendedSlotConfigData BuildExtendedConfigSnapshotForSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _mainVm.Pads.Count) return null;
            var padVm = _mainVm.Pads[slotIndex];
            if (padVm == null) return null;
            // The "isn't Extended" half of the contract above. EVERY pad owns an
            // ExtendedConfig object whether or not it is an Extended slot, so
            // without this gate copying an Xbox / PlayStation slot exported that
            // slot's dormant DEFAULTS, and pasting onto a real Extended slot
            // overwrote its authored counts, VID/PID, and product string.
            if (padVm.OutputType != Engine.VirtualControllerType.Extended) return null;
            var cfg = padVm.ExtendedConfig;
            if (cfg == null) return null;
            return new ViewModels.ExtendedSlotConfigData
            {
                SlotIndex = slotIndex,
                ThumbstickCount = cfg.ThumbstickCount,
                TriggerCount = cfg.TriggerCount,
                PovCount = cfg.PovCount,
                ButtonCount = cfg.ButtonCount,
                OemNameOverride = cfg.OemNameOverride,
                ProductString = cfg.ProductString,
                VendorId = cfg.VendorId,
                ProductId = cfg.ProductId,
                Customize = cfg.Customize,
                ForceFeedbackEnabled = cfg.ForceFeedbackEnabled,
            };
        }

        /// <summary>Paste companion. Only applies when both source and
        /// destination are Extended slots — the custom layout is the
        /// virtual controller's shape and doesn't translate to other
        /// output types.</summary>
        public void ApplyExtendedConfigToSlot(int slotIndex, ViewModels.ExtendedSlotConfigData cfg)
        {
            if (cfg == null) return;
            if (slotIndex < 0 || slotIndex >= _mainVm.Pads.Count) return;
            var padVm = _mainVm.Pads[slotIndex];
            if (padVm == null) return;
            if (padVm.OutputType != Engine.VirtualControllerType.Extended) return;
            var d = padVm.ExtendedConfig;
            if (d == null) return;
            d.ThumbstickCount = cfg.ThumbstickCount;
            d.TriggerCount = cfg.TriggerCount;
            d.PovCount = cfg.PovCount;
            d.ButtonCount = cfg.ButtonCount;
            d.OemNameOverride = cfg.OemNameOverride;
            d.ProductString = cfg.ProductString ?? string.Empty;
            d.VendorId = cfg.VendorId;
            d.ProductId = cfg.ProductId;
            d.Customize = cfg.Customize;
            d.ForceFeedbackEnabled = cfg.ForceFeedbackEnabled;
        }

        /// <summary>Snapshots the MIDI port layout for a single slot.
        /// Returns null when the slot isn't MIDI.</summary>
        public ViewModels.MidiSlotConfigData BuildMidiConfigSnapshotForSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _mainVm.Pads.Count) return null;
            var padVm = _mainVm.Pads[slotIndex];
            if (padVm == null) return null;
            // Same contract gate as the Extended builder above: every pad owns a
            // dormant MidiConfig, so an ungated copy exported channel 1 /
            // default ranges from an Xbox slot and clobbered a real MIDI slot's
            // authored channel and CC/note ranges on paste.
            if (padVm.OutputType != Engine.VirtualControllerType.Midi) return null;
            var cfg = padVm.MidiConfig;
            if (cfg == null) return null;
            return new ViewModels.MidiSlotConfigData
            {
                SlotIndex = slotIndex,
                Channel = cfg.Channel,
                Velocity = cfg.Velocity,
                StartCc = cfg.StartCc,
                CcCount = cfg.CcCount,
                StartNote = cfg.StartNote,
                NoteCount = cfg.NoteCount,
            };
        }

        /// <summary>Paste companion. Only applies when both source and
        /// destination are MIDI slots.</summary>
        public void ApplyMidiConfigToSlot(int slotIndex, ViewModels.MidiSlotConfigData cfg)
        {
            if (cfg == null) return;
            if (slotIndex < 0 || slotIndex >= _mainVm.Pads.Count) return;
            var padVm = _mainVm.Pads[slotIndex];
            if (padVm == null) return;
            if (padVm.OutputType != Engine.VirtualControllerType.Midi) return;
            var d = padVm.MidiConfig;
            if (d == null) return;
            d.Channel = cfg.Channel;
            d.Velocity = cfg.Velocity;
            d.StartCc = cfg.StartCc;
            d.CcCount = cfg.CcCount;
            d.StartNote = cfg.StartNote;
            d.NoteCount = cfg.NoteCount;
            padVm.RebuildMappings();
        }

        /// <summary>Snapshots the KBM (SOCD) layout for a single slot.
        /// Returns null when the slot isn't KeyboardMouse.</summary>
        public ViewModels.KbmSlotConfigData BuildKbmConfigSnapshotForSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _mainVm.Pads.Count) return null;
            var padVm = _mainVm.Pads[slotIndex];
            if (padVm == null) return null;
            if (padVm.OutputType != Engine.VirtualControllerType.KeyboardMouse) return null;
            var cfg = padVm.KbmConfig;
            if (cfg == null) return null;
            return new ViewModels.KbmSlotConfigData
            {
                SlotIndex = slotIndex,
                SocdMode = cfg.SocdMode,
                SocdPairs = cfg.SocdPairs,
            };
        }

        /// <summary>Paste companion. Only applies when both source and
        /// destination are KeyboardMouse slots.</summary>
        public void ApplyKbmConfigToSlot(int slotIndex, ViewModels.KbmSlotConfigData cfg)
        {
            if (cfg == null) return;
            if (slotIndex < 0 || slotIndex >= _mainVm.Pads.Count) return;
            var padVm = _mainVm.Pads[slotIndex];
            if (padVm == null) return;
            if (padVm.OutputType != Engine.VirtualControllerType.KeyboardMouse) return;
            var d = padVm.KbmConfig;
            if (d == null) return;
            d.SocdMode = cfg.SocdMode;
            d.SocdPairs = cfg.SocdPairs;
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
                    cfg.VendorId = cfgData.VendorId;
                    cfg.ProductId = cfgData.ProductId;
                    cfg.Customize = cfgData.Customize;
                    cfg.ForceFeedbackEnabled = cfgData.ForceFeedbackEnabled;
                }
            }
        }

        /// <summary>Applies per-(slot, device) configurations (adaptive
        /// triggers, lighting, audio, tone filter). Only restores configs
        /// for slots that are currently created; the body never gates on
        /// slot output type.
        /// <para>Internal: both apply lanes (this file's load path and
        /// InputService.ApplyProfile) reuse it so a runtime profile switch
        /// restores lighting / adaptive triggers exactly as app load does.
        /// Every write mutates the existing DeviceSlotConfig in place and
        /// never reassigns one, which is load-bearing: UserEffectsDispatcher
        /// holds a direct PropertyChanged subscription to those instances,
        /// so replacing one would silently orphan the dispatcher.</para></summary>
        internal void ApplyDeviceSlotConfigs(ViewModels.DeviceSlotConfigData[] configs)
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
                // DeviceSlotConfig only. The Lighting tab is
                // per-device, so two pads on one slot legitimately
                // carry different lightbar / overlay state.
                if (cfgData.DeviceGuid != Guid.Empty)
                {
                    var devCfg = padVm.GetOrCreateDeviceConfig(cfgData.DeviceGuid);
                    if (devCfg != null)
                        ApplyDeviceSlotConfigData(devCfg, cfgData);
                    continue;
                }

                // Slot-level entry — always apply to the anchor so the
                // Lighting tab shows something reasonable before any
                // device is selected. Fan out to per-device configs
                // ONLY when this slot has zero per-device entries (a
                // pre-v3.1 save where lighting was slot-wide); v3.1+
                // saves are authoritative per-device.
                if (padVm.DeviceConfig != null)
                    ApplyDeviceSlotConfigData(padVm.DeviceConfig, cfgData);
                if (!slotsWithPerDeviceEntries.Contains(idx))
                {
                    foreach (var devCfg in padVm.PerDeviceSlotConfigs.Values)
                    {
                        if (devCfg != null && !ReferenceEquals(devCfg, padVm.DeviceConfig))
                            ApplyDeviceSlotConfigData(devCfg, cfgData);
                    }
                }
            }
        }

        /// <summary>Writes the saved DTO fields into a single
        /// DeviceSlotConfig instance. Extracted so the loader can
        /// call it once per per-device entry, or once per slot when
        /// fanning out a legacy slot-level entry to every device.</summary>
        // Internal (not private) so the tests can drive the LightingRev
        // migration below directly (InternalsVisibleTo PadForge.Tests).
        internal static void ApplyDeviceSlotConfigData(ViewModels.DeviceSlotConfig cfg, ViewModels.DeviceSlotConfigData cfgData)
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
                    cfg.AudioPassthroughEnabled = cfgData.AudioPassthroughEnabled;
                    cfg.AudioMirrorSourceId = cfgData.AudioMirrorSourceId ?? string.Empty;
                    cfg.AudioMirrorEngageMode = cfgData.AudioMirrorEngageMode ?? "Always";
                    cfg.AudioMirrorEngageDeviceGuid = cfgData.AudioMirrorEngageDeviceGuid ?? string.Empty;
                    cfg.AudioMirrorEngageButton = cfgData.AudioMirrorEngageButton ?? string.Empty;
                    cfg.AudioMirrorEngageReleaseMs = cfgData.AudioMirrorEngageReleaseMs;
                    cfg.AudioToneFilterMode = cfgData.AudioToneFilterMode ?? "Off";
                    cfg.AudioToneLimitHz = cfgData.AudioToneLimitHz;
                    // Migrate legacy MicLightOn to the new MicLedMode if
                    // the new field hasn't been set explicitly.
                    if (cfgData.MicLedMode != ViewModels.MicLedMode.Off)
                        cfg.MicLedMode = cfgData.MicLedMode;
                    else
                        cfg.MicLightOn = cfgData.MicLightOn;
                    cfg.MicLedFollowDeviceId = cfgData.MicLedFollowDeviceId ?? string.Empty;
                    cfg.PlayerLedMode = cfgData.PlayerLedMode;
                    cfg.PlayerLedBrightness = cfgData.PlayerLedBrightness;
                    cfg.GuideLedMode = cfgData.GuideLedMode;
                    cfg.GuideLedBrightness = cfgData.GuideLedBrightness;
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
                    // Rev-1 saves skip the fallback entirely: Off is a
                    // deliberate hard-off there, and the legacy bools
                    // round-trip stale (LightbarEnabled is never cleared
                    // when the user changes modes), so consulting them
                    // would resurrect Static over a chosen Off.
                    cfg.LightbarMode = cfgData.LightingRev >= 1
                        || cfgData.LightbarMode != ViewModels.LightbarMode.Off
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
                    // InputReactive = Cycle now has its own palette. Pre-split saves don't carry
                    // it (null), so seed it from the shared LightbarPalette to preserve existing
                    // setups; once present it round-trips independently.
                    if (cfgData.LightbarInputReactivePalette != null && cfgData.LightbarInputReactivePalette.Length > 0)
                    {
                        cfg.ReplaceLightbarInputReactivePalette(cfgData.LightbarInputReactivePalette
                            .Select(e => new ViewModels.LightbarPaletteEntry(e.R, e.G, e.B)));
                    }
                    else if (cfgData.LightbarPalette != null && cfgData.LightbarPalette.Length > 0)
                    {
                        cfg.ReplaceLightbarInputReactivePalette(cfgData.LightbarPalette
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

                    // LightingRev 0 → 1 (#191 follow-up): before the
                    // PlayerNumber default existed, Off doubled as
                    // "unset". Every untouched slot serialized Off, so
                    // lift those to the v4 default and upgraders get the
                    // player-identity idle out of the box. Rev-1 saves
                    // take Off literally: it means dark.
                    //
                    // Guard the lightbar lift on "no reactive overlay".
                    // A slot with an active input-reactive overlay rested
                    // on a dark base in every prior release (the v3.2
                    // split parks the legacy InputReactive base at Off,
                    // and #191 rendered that Off as black under the
                    // flash). Lifting it to PlayerNumber would swap the
                    // user's reactive-from-darkness effect for a
                    // player-color glow that merely brightens on press.
                    // Leave those dark; only genuinely-idle lightbars
                    // (no overlay) inherit the floor. Pips carry no
                    // overlay concept, so they always lift.
                    if (cfgData.LightingRev < 1)
                    {
                        if (cfg.LightbarMode == ViewModels.LightbarMode.Off
                            && cfg.InputReactiveMode == ViewModels.InputReactiveMode.Off)
                            cfg.LightbarMode = ViewModels.LightbarMode.PlayerNumber;
                        if (cfg.PlayerLedMode == ViewModels.PlayerLedMode.Off)
                            cfg.PlayerLedMode = ViewModels.PlayerLedMode.PlayerNumber;
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
        /// Applies per-slot KBM (SOCD) configurations. Only restores configs
        /// for slots that are currently created as KeyboardMouse.
        /// </summary>
        private void ApplyKbmConfigs(ViewModels.KbmSlotConfigData[] configs)
        {
            if (configs == null) return;
            foreach (var cfgData in configs)
            {
                int idx = cfgData.SlotIndex;
                if (idx >= 0 && idx < _mainVm.Pads.Count &&
                    SettingsManager.SlotCreated[idx] &&
                    _mainVm.Pads[idx].OutputType == Engine.VirtualControllerType.KeyboardMouse)
                {
                    var cfg = _mainVm.Pads[idx].KbmConfig;
                    cfg.SocdMode = cfgData.SocdMode;
                    cfg.SocdPairs = cfgData.SocdPairs;
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
                padVm.WheelRotationRange = TryParseInt(ps.RotationRange, 900);
                padVm.WheelAutoCenter = TryParseInt(ps.AutoCenterStrength, 0);
                padVm.WheelRpmLeds = ps.WheelRpmLeds == "1";
                padVm.LeftMotorStrength = TryParseInt(ps.LeftMotorStrength, 100);
                padVm.RightMotorStrength = TryParseInt(ps.RightMotorStrength, 100);
                padVm.SwapMotors = ps.ForceSwapMotor == "1" ||
                    (ps.ForceSwapMotor ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);

                // Load impulse trigger settings (Xbox One+).
                padVm.ImpulseOverallGain = TryParseInt(ps.ImpulseOverallGain, 100);
                padVm.ImpulseLeftStrength = TryParseInt(ps.ImpulseLeftStrength, 100);
                padVm.ImpulseRightStrength = TryParseInt(ps.ImpulseRightStrength, 100);
                padVm.ImpulseSwapTriggers = ps.ImpulseSwapTriggers == "1" ||
                    (ps.ImpulseSwapTriggers ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
                padVm.ConstantTriggerForceEnabled = ps.ConstantTriggerForceEnabled == "1";
                padVm.ConstantTriggerForceLeft = TryParseDouble(ps.ConstantTriggerForceLeft, 0.0);
                padVm.ConstantTriggerForceRight = TryParseDouble(ps.ConstantTriggerForceRight, 0.0);
                padVm.AudioRumbleTriggersEnabled = ps.AudioRumbleTriggersEnabled == "1";
                padVm.AudioRumbleTriggersSensitivity = TryParseDouble(ps.AudioRumbleTriggersSensitivity, 4.0);
                padVm.AudioRumbleTriggersCutoffHz = TryParseDouble(ps.AudioRumbleTriggersCutoffHz, 80.0);
                padVm.AudioRumbleLeftTrigger = TryParseInt(ps.AudioRumbleLeftTrigger, 100);
                padVm.AudioRumbleRightTrigger = TryParseInt(ps.AudioRumbleRightTrigger, 100);

                // Load gyro tuning (per-(device, slot)).
                padVm.GyroSensitivityH = TryParseDouble(ps.GyroSensitivityH, 1.0);
                padVm.GyroSensitivityV = TryParseDouble(ps.GyroSensitivityV, 1.0);
                padVm.GyroDeadZoneDegPerSec = TryParseDouble(ps.GyroDeadZoneDegPerSec, 3.0);
                padVm.GyroSmoothingAlpha = TryParseDouble(ps.GyroSmoothingAlpha, 0);
                padVm.GyroAcceleration = TryParseDouble(ps.GyroAcceleration, 0);
                padVm.GyroOutputCurve = string.IsNullOrEmpty(ps.GyroOutputCurve) ? "Linear" : ps.GyroOutputCurve;
                padVm.GyroSensitivityUnits = string.IsNullOrEmpty(ps.GyroSensitivityUnits) ? "Multiplier" : ps.GyroSensitivityUnits;
                padVm.GyroEasyAimStickThreshold = TryParseDouble(ps.GyroEasyAimStickThreshold, 0);
                padVm.GyroEngageStickSide = string.IsNullOrEmpty(ps.GyroEngageStickSide) ? "Right" : ps.GyroEngageStickSide;
                padVm.GyroEngageStickDirection = string.IsNullOrEmpty(ps.GyroEngageStickDirection) ? "Full" : ps.GyroEngageStickDirection;
                padVm.IrSensorBarPos = TryParseInt(ps.IrSensorBarPos, 0);
                padVm.IrSensorBarCompPercent = (int)Math.Round(TryParseDouble(ps.IrSensorBarComp, 0) * 100.0);
                padVm.IrSmoothingPercent = (int)Math.Round(TryParseDouble(ps.IrSmoothing, 0) * 100.0);
                padVm.PointerMode = string.IsNullOrEmpty(ps.PointerMode) ? "Mouse" : ps.PointerMode;
                padVm.PointerFpsSpeed = TryParseInt(ps.PointerFpsSpeed, 35);

                // Load JoyShockMapper-canongyro extensions.
                padVm.GyroSpace = string.IsNullOrEmpty(ps.GyroSpace) ? "Local" : ps.GyroSpace;
                padVm.GyroPlayerSpaceYawRelaxFactor = TryParseDouble(ps.GyroPlayerSpaceYawRelaxFactor, 1.41);
                padVm.GyroWorldSpaceSideReductionThreshold = TryParseDouble(ps.GyroWorldSpaceSideReductionThreshold, 0.125);
                padVm.GyroTighteningThresholdDegPerSec = TryParseDouble(ps.GyroTighteningThresholdDegPerSec, 3.0);
                padVm.GyroSmoothingThresholdDegPerSec = TryParseDouble(ps.GyroSmoothingThresholdDegPerSec, 8.0);
                padVm.GyroSmoothingWindowMs = TryParseDouble(ps.GyroSmoothingWindowMs, 50);
                padVm.GyroRealWorldCalibration = TryParseDouble(ps.GyroRealWorldCalibration, 0);
                padVm.GyroAimEngageButton = ps.GyroAimEngageButton ?? "";
                padVm.GyroAimEngageDeviceGuid = ps.GyroAimEngageDeviceGuid ?? "";
                padVm.GyroAimEngageMode = string.IsNullOrEmpty(ps.GyroAimEngageMode) ? "Hold" : ps.GyroAimEngageMode;
                padVm.GyroInvertPitch = ps.GyroInvertPitch == "1";
                padVm.GyroInvertYawRoll = ps.GyroInvertYawRoll == "1";
                padVm.GyroApplyTuningToPassthrough = ps.GyroApplyTuningToPassthrough == "1";

                // Load Motion Steering tuning (per-(device, slot)) — settings for the
                // "Motion Lean" input descriptor. The old Enabled/Target keys are gone
                // (the input is mapped from the picker, never stamped onto a target);
                // stale keys in old profiles are simply never read.
                padVm.MotionSteerInnerDz = TryParseDouble(ps.GetExtendedMapping("MotionSteerInner"), 15);
                padVm.MotionSteerOuterDz = TryParseDouble(ps.GetExtendedMapping("MotionSteerOuter"), 135);
                padVm.SetMotionSteerOrient(ps.GetExtendedMapping("MotionSteerOrient"));

                // Load Flick Stick card tuning (#225), same per-(device, slot)
                // extended-mapping bag; seeds from a Workshop import's flick
                // source when the card was never stored.
                LoadFlickStickCard(padVm, ps);

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

                // Steering at-lock feedback (#94).
                padVm.SteeringLockRumbleEnabled = ps.SteeringLockRumbleEnabled == "1";
                padVm.SteeringLockTriggerVibEnabled = ps.SteeringLockTriggerVibEnabled == "1";
                padVm.SteeringLockLightbarEnabled = ps.SteeringLockLightbarEnabled == "1";
                padVm.SteeringLockATResistanceEnabled = ps.SteeringLockATResistanceEnabled == "1";
                padVm.SteeringLockPulseMs = TryParseDouble(ps.SteeringLockPulseMs, 80);
                padVm.SteeringLockLightbarColor = string.IsNullOrWhiteSpace(ps.SteeringLockLightbarColor) ? "#FF0000" : ps.SteeringLockLightbarColor;
                padVm.SteeringLockLightbarColorSource =
                    Enum.TryParse<ViewModels.MacroLightbarColorSource>(ps.SteeringLockLightbarColorSource, out var slcs)
                        ? slcs : ViewModels.MacroLightbarColorSource.Fixed;
                padVm.SteeringLockLightbarPaletteCsv = ps.SteeringLockLightbarPaletteCsv ?? "";
                padVm.SteeringLockLightbarHoldMs = TryParseDouble(ps.SteeringLockLightbarHoldMs, 80);
                padVm.SteeringLockLightbarFadeMs = TryParseDouble(ps.SteeringLockLightbarFadeMs, 250);

                // Trigger rumble routing (#102), mirroring the device-switch
                // load in InputService.LoadPadSettingToViewModel so the first
                // device per slot gets the same field set at startup.
                padVm.LeftTriggerRouteSource = string.IsNullOrEmpty(ps.LeftTriggerRouteSource) ? "None" : ps.LeftTriggerRouteSource;
                padVm.RightTriggerRouteSource = string.IsNullOrEmpty(ps.RightTriggerRouteSource) ? "None" : ps.RightTriggerRouteSource;
                padVm.LeftTriggerRouteMode = string.IsNullOrEmpty(ps.LeftTriggerRouteMode) ? "Duplicate" : ps.LeftTriggerRouteMode;
                padVm.RightTriggerRouteMode = string.IsNullOrEmpty(ps.RightTriggerRouteMode) ? "Duplicate" : ps.RightTriggerRouteMode;
                padVm.LeftTriggerRouteScale = TryParseInt(ps.LeftTriggerRouteScale, 100);
                padVm.RightTriggerRouteScale = TryParseInt(ps.RightTriggerRouteScale, 100);
                padVm.LeftTriggerRouteActivator = ps.LeftTriggerRouteActivator ?? "";
                padVm.RightTriggerRouteActivator = ps.RightTriggerRouteActivator ?? "";
                padVm.LeftTriggerRouteActivatorDeviceGuid = ps.LeftTriggerRouteActivatorDeviceGuid ?? "";
                padVm.RightTriggerRouteActivatorDeviceGuid = ps.RightTriggerRouteActivatorDeviceGuid ?? "";
                padVm.LeftTriggerRouteActivatorMode = string.IsNullOrEmpty(ps.LeftTriggerRouteActivatorMode) ? "Hold" : ps.LeftTriggerRouteActivatorMode;
                padVm.RightTriggerRouteActivatorMode = string.IsNullOrEmpty(ps.RightTriggerRouteActivatorMode) ? "Hold" : ps.RightTriggerRouteActivatorMode;

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
                // Stick boundary calibration maps (#174). MUST load here: the
                // 30 Hz SaveViewModelToPadSetting writes ps.*BoundaryMap =
                // padVm.*BoundaryMap on the selected device, so without this
                // read-back the empty startup VM clobbers a persisted map on
                // the first sync tick (the dirty-gate persistence trap).
                padVm.LeftThumbBoundaryMap = ps.LeftThumbBoundaryMap ?? string.Empty;
                padVm.RightThumbBoundaryMap = ps.RightThumbBoundaryMap ?? string.Empty;

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
                    // GetExtendedMapping returns "" (never null) for a missing
                    // key, so a bare ?? default never fires (audit G4).
                    string curveX = ps.GetExtendedMapping($"ExtendedStick{g}CurveX");
                    string curveY = ps.GetExtendedMapping($"ExtendedStick{g}CurveY");
                    stick.SensitivityCurveX = string.IsNullOrEmpty(curveX) ? "0,0;1,1" : curveX;
                    stick.SensitivityCurveY = string.IsNullOrEmpty(curveY) ? "0,0;1,1" : curveY;
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
                    string trigCurve = ps.GetExtendedMapping($"ExtendedTrigger{g}Curve");
                    trig.SensitivityCurve = string.IsNullOrEmpty(trigCurve) ? "0,0;1,1" : trigCurve;
                }

                // Steering is per assigned device (#94): the Sticks-tab card loads the
                // SELECTED device's steering via InputService.LoadPadSettingToViewModel on
                // device select, not the first device here. The engine reads each source's
                // own device steering off the MappingSet (stamped per source in
                // ApplySteeringKindToRow), so nothing per-device is loaded into the shared
                // StickConfigs at profile-load time.

                // Mappings are per-slot (live in SlotMappingSets), NOT per-device.
                // Read from the authoritative MappingSet rather than the legacy
                // per-device PadSetting fields. The legacy fields are stale
                // whenever the row's primary lives on a device other than the
                // SELECTED one (e.g. cross-device multi-source rows), and the
                // save path's UpdatePadSettingsFromViewModels writes the primary
                // descriptor into the SELECTED device's PadSetting without
                // checking PrimarySourceDeviceGuid, so legacy fields can hold
                // descriptors that don't belong to the device they're stored on,
                // or get blanked out across save / load cycles. Reading
                // SlotMappingSets here is what every other "refresh the Mappings
                // tab" path already does — slot init was the missing call site.
                InputService.RefreshMappingsToViewModel(padVm);
            }
        }

        /// <summary>
        /// Populates pad ViewModels with macros from serialized data.
        /// </summary>
        internal void LoadMacros(MacroData[] macros)
        {
            // Macro sounds are keyed to the MacroItem objects being replaced;
            // a looping sound would have no owner left to stop it.
            PadForge.Common.Input.SoundMacroService.StopAll();

            // Clear existing macros on all pads.
            foreach (var pad in _mainVm.Pads)
                pad.Macros.Clear();

            foreach (var md in macros)
            {
                if (md.PadIndex < 0 || md.PadIndex >= _mainVm.Pads.Count)
                    continue;

                var padVm = _mainVm.Pads[md.PadIndex];
                var macro = LoadMacroFromData(md, padVm.OutputType, padVm.ExtendedConfig?.ButtonCount);
                padVm.Macros.Add(macro);
            }
        }

        /// <summary>Deserializes one <see cref="MacroData"/> DTO into a fresh
        /// <see cref="MacroItem"/>, applying the target pad's button style and count.
        /// Extracted from <see cref="LoadMacros"/> so Duplicate / Paste (#112) rebuild a
        /// macro through the same mapping. The returned macro is not added to any pad.</summary>
        public static MacroItem LoadMacroFromData(MacroData md, VirtualControllerType outputType, int? extendedButtonCount)
        {
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
                TriggerHoldMs = md.TriggerHoldMs,
                ConsumeTriggerButtons = md.ConsumeTriggerButtons,
                RepeatMode = md.RepeatMode,
                RepeatCount = md.RepeatCount,
                RepeatDelayMs = md.RepeatDelayMs,
                TriggerAxisTargetList = md.TriggerAxisTargets,
                TriggerAxisDirectionList = md.TriggerAxisDirections,
                TriggerAxisThreshold = md.TriggerAxisThreshold > 0 ? md.TriggerAxisThreshold : 50,
                TriggerPovs = md.TriggerPovs ?? Array.Empty<string>(),
                TriggerInputs = md.TriggerInputs,
                TriggerExpression = md.TriggerExpression ?? "",
                TriggerExpressionVariableSpecs = md.TriggerExpressionVariables
            };

            if (md.Actions != null)
                foreach (var ad in md.Actions)
                    macro.Actions.Add(BuildMacroAction(ad));

            // Set after actions are populated so propagation reaches all of them.
            var style = MacroButtonNames.DeriveStyle(outputType);
            int btnCount = (outputType == VirtualControllerType.Extended ? extendedButtonCount : null) ?? 11;
            macro.CustomButtonCount = btnCount;
            macro.ButtonStyle = style;
            foreach (var action in macro.Actions)
                action.CustomButtonCount = btnCount;

            return macro;
        }

        /// <summary>Deserializes one <see cref="ActionData"/> DTO into a fresh
        /// <see cref="MacroAction"/>. Extracted from <see cref="LoadMacros"/> so the
        /// action Duplicate command (#112) reuses the same field mapping.</summary>
        public static MacroAction BuildMacroAction(ActionData ad)
        {
            return new MacroAction
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
                LightbarCycleModesCsv = ad.LightbarCycleModesCsv,
                PointerCycleModesCsv = ad.PointerCycleModesCsv,
                PointerSetMode = ad.PointerSetMode ?? "Mouse",
                GuideLedPercent = ad.GuideLedPercent,
                SoundFilePath = ad.SoundFilePath ?? string.Empty,
                SoundVolume = ad.SoundVolume > 0 ? ad.SoundVolume : 100,
                SoundLoop = ad.SoundLoop,
                SetGyroEngagedMode = ad.SetGyroEngagedMode,
                RumbleHoldMode = ad.RumbleHoldMode,
                RumbleStrengthLeft = ad.RumbleStrengthLeft,
                RumbleStrengthRight = ad.RumbleStrengthRight,
                RumbleHoldMs = ad.RumbleHoldMs,
                RumbleFadeMs = ad.RumbleFadeMs,
                CursorRecenterMode = ad.CursorRecenterMode,
                CursorPinMode = ad.CursorPinMode,
                CursorPinX = ad.CursorPinX,
                CursorPinY = ad.CursorPinY,
                MouseX = ad.MouseX,
                MouseY = ad.MouseY,
                IntervalMs = ad.IntervalMs,
                CursorClampMode = ad.CursorClampMode,
                CursorClampInsetX = ad.CursorClampInsetX,
                CursorClampInsetY = ad.CursorClampInsetY,
                DisconnectTarget = ad.DisconnectTarget,
                DisconnectDeviceGuid = Guid.TryParse(ad.DisconnectDeviceGuid, out Guid dcGuid)
                    ? dcGuid : Guid.Empty,
                ProgramPath = ad.ProgramPath ?? "",
                ProgramArgs = ad.ProgramArgs ?? "",
                ProgramWorkingDir = ad.ProgramWorkingDir ?? "",
                TextContent = ad.TextContent ?? "",
                TextPerCharDelayMs = ad.TextPerCharDelayMs
            };
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
        /// Internal (not private) so the cold-start tests can drive it with
        /// plain DTOs: it takes no file handle, and the active-profile branch
        /// below is the only lane that restores a named profile at startup, so
        /// a gap in it is invisible to every runtime-switch test.
        /// </summary>
        internal void LoadProfiles(ProfileData[] profiles, AppSettingsData appSettings)
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
                int maxPads = Common.Input.InputManager.MaxPads;
                bool anyProfileCompacted = false;
                foreach (var p in profiles)
                {
                    // Compact gappy profile snapshots in place so the file
                    // heals itself. Profiles saved before compaction-on-delete
                    // can have non-contiguous slot indices; rewriting them as
                    // contiguous fixes the source data, not just the runtime
                    // view of it.
                    //
                    // NOT the active profile. Its topology is the ROOT
                    // topology: the active branch below Array.Copy's
                    // p.SlotCreated straight into SettingsManager.SlotCreated,
                    // while UserSettings.MapTo, SlotMappingSets, the pad
                    // ViewModels, the macros and the per-slot sound volumes all
                    // hydrate from this same file at the OLD indices. Compacting
                    // it here would move only the created-slot flags and leave
                    // every one of those mirrors behind.
                    //
                    // The healer for exactly that already exists and is already
                    // wired: InputService.Start calls CompactSlotsForGaps right
                    // after load, which snapshots the whole live state, shifts
                    // every slot-indexed field through one map, and drives the
                    // PadViewModel rebuild through ApplyProfile. It arms itself
                    // off SettingsManager.SlotCreated, so compacting the active
                    // profile here is precisely what disarmed it: root came up
                    // contiguous, the healer saw no gap, and the mappings stayed
                    // at the old indices with the slot created and empty.
                    //
                    // Leaving the active profile gappy keeps every mirror
                    // mutually consistent until that healer runs, and its
                    // MarkDirty + UpdateActiveProfileSnapshot then persist the
                    // compacted layout back into this profile, so the gap is
                    // gone on the next load. Do NOT hand-move the root mirrors
                    // instead: that list is exactly the set of lanes the rebuild
                    // exists to keep in one place, and a hand list silently
                    // omitted the ViewModels the 250 ms autosave rebuilds from,
                    // which then cleared the rows it had just moved.
                    bool isActive = !string.IsNullOrEmpty(appSettings?.ActiveProfileId)
                        && string.Equals(p.Id, appSettings.ActiveProfileId, StringComparison.Ordinal);
                    var (map, needs) = InputService.BuildCompactionMap(p);
                    if (needs && !isActive)
                    {
                        InputService.CompactProfileDataInPlace(p, map, maxPads);
                        anyProfileCompacted = true;
                    }
                    SettingsManager.Profiles.Add(p);
                    var item = new ViewModels.ProfileListItem
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Executables = InputService.FormatExePaths(p.ExecutableNames),
                        ExecutablePaths = p.ExecutableNames,
                    };
                    UpdateTopologyCounts(item, p.SlotCreated, p.SlotControllerTypes);
                    _mainVm.Settings.ProfileItems.Add(item);
                }
                if (anyProfileCompacted)
                    MarkDirty();
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

                // Now that SlotCreated and OutputType are restored, apply Extended/MIDI/device
                // configs from the profile's own snapshot.
                ApplyExtendedConfigs(active.ExtendedConfigs);
                ApplyDeviceSlotConfigs(active.DeviceSlotConfigs);
                ApplyMidiConfigs(active.MidiConfigs);
                ApplyKbmConfigs(active.KbmConfigs);

                // The profile's OWN gesture catalog, overriding the default's
                // that LoadAppSettings staged a moment ago. ApplyProfile never
                // runs on the cold path, so without this the default's catalog
                // stayed live under a named profile AND the first autosave
                // wrote it back over the profile's stored gestures, destroying
                // them. The default's copy survives in
                // AppSettings.DefaultProfileSnapshot.TouchpadGestures, which
                // PendingDefaultSnapshot (above) carries to the switch-back.
                ApplyOrStageTouchpadGestures(active.TouchpadGestures);

                // Apply DSU/Web/overlay settings from the active profile.
                _mainVm.Dashboard.EnableDsuMotionServer = active.EnableDsuMotionServer;
                if (active.DsuMotionServerPort >= 1024 && active.DsuMotionServerPort <= 65535)
                    _mainVm.Dashboard.DsuMotionServerPort = active.DsuMotionServerPort;
                _mainVm.Dashboard.EnableWebController = active.EnableWebController;
                if (active.WebControllerPort >= 1024 && active.WebControllerPort <= 65535)
                    _mainVm.Dashboard.WebControllerPort = active.WebControllerPort;
                _mainVm.Dashboard.EnableTouchpadOverlay = active.EnableTouchpadOverlay;
                _mainVm.Dashboard.EnableMenuOverlay = active.EnableMenuOverlay;
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
        // Internal (not private) so the mirror tests can drive it directly.
        // It is one of the three runtime-state mirrors named on ProfileData,
        // and the only one Save() reaches, so an untested gap here is invisible
        // until an export ships stale data.
        internal void UpdateActiveProfileSnapshot()
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
            // Mapping sets ride the autosave like every other runtime field.
            // Without this leg the stored profile keeps whatever mappings it
            // had at activation: switching away repairs it via
            // SaveActiveProfileState, but Export reads the STORED object
            // (MainWindow.ExportProfile), so exporting the active profile
            // without switching away shipped its pre-edit mappings. Deep-cloned
            // for the same reason SnapshotCurrentProfile clones: a reference
            // copy lets later live edits mutate the stored snapshot.
            profile.SlotMappingSets = Enumerable
                .Range(0, SettingsManager.SlotMappingSets.Length)
                .Select(s => InputService.CloneMappingSetDeep(SettingsManager.SlotMappingSets[s]))
                .ToArray();
            profile.SlotCreated = (bool[])SettingsManager.SlotCreated.Clone();
            profile.SlotEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            profile.SlotControllerTypes = Enumerable.Range(0, _mainVm.Pads.Count)
                .Select(i => (int)_mainVm.Pads[i].OutputType).ToArray();
            profile.SlotProfileIds = Enumerable.Range(0, _mainVm.Pads.Count)
                .Select(i => _mainVm.Pads[i].ProfileId).ToArray();
            profile.ExtendedConfigs = BuildExtendedConfigSnapshot();
            profile.DeviceSlotConfigs = BuildDeviceConfigSnapshot();
            profile.MidiConfigs = BuildMidiConfigSnapshot();
            profile.KbmConfigs = BuildKbmConfigSnapshot();
            // Macros ride profiles: edits made while this profile is active
            // persist into it, so switching away and back keeps them, and a
            // .pfprofile export carries them (with their sound packages).
            profile.Macros = BuildMacroData();
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
            profile.EnableMenuOverlay = _mainVm.Dashboard.EnableMenuOverlay;
            profile.TouchpadOverlayOpacity = _mainVm.Dashboard.TouchpadOverlayOpacity;
            profile.TouchpadOverlayMonitor = _mainVm.Dashboard.TouchpadOverlayMonitor;
            profile.TouchpadOverlayLeft = _mainVm.Dashboard.TouchpadOverlayLeft;
            profile.TouchpadOverlayTop = _mainVm.Dashboard.TouchpadOverlayTop;
            profile.TouchpadOverlayWidth = _mainVm.Dashboard.TouchpadOverlayWidth;
            profile.TouchpadOverlayHeight = _mainVm.Dashboard.TouchpadOverlayHeight;

            // Custom touchpad gestures live in InputService's working
            // list; pull through the provider hook so the 250 ms
            // autosave path captures them the same way SnapshotCurrentProfile
            // does. Without this, gestures recorded while a named
            // profile is active vanish at the next save / reload.
            var tg = TouchpadGesturesProvider?.Invoke();
            profile.TouchpadGestures = (tg != null && tg.Length > 0) ? tg : null;

            // Identity members (Id, Name, ExecutableNames, WorkshopSource) are
            // intentionally NOT rewritten here. This mirror copies runtime
            // state only. Assigning identity members from runtime would null
            // the Workshop provenance on every autosave.
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
                            ps.FlushMappingBidirectional();
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

                // Issue #61: do NOT run MergeMappingSetsFromLegacy in
                // SaveToFile. The merge appends rebuilt sources from each
                // device's PadSetting fields to existing rows — useful
                // when a NEW device gets assigned to a slot (auto-map
                // surfacing), but at save-time it re-injects auto-mapped
                // descriptors that the user has explicitly DELETED from
                // ExtraSources. Symptom: user removes a duplicate-looking
                // extra source on a multi-device slot, save fires, merge
                // re-adds the same (DeviceGuid, Descriptor) pair as an
                // extra. The PushUi step above is now authoritative for
                // save-time MappingSet state; the merge stays wired to
                // the device-assign paths (DeviceService.RefreshMappingSetsFromLegacy)
                // where its job actually is.
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
                _mainVm.SetStatus(string.Format(Strings.Instance.Status_ErrorSavingSettings_Format, ex.Message), persist: true);
            }
        }

        /// <summary>
        /// Builds an AppSettingsData from the current SettingsViewModel state.
        /// </summary>
        private AppSettingsData BuildAppSettings()
        {
            var vm = _mainVm.Settings;
            var soundPackages = PadForge.Common.SoundPackageManager.SaveRegistry()
                .Select(p => new SoundPackageData { Name = p.Name, Path = p.Path })
                .ToArray();
            var nfcTags = PadForge.Common.Input.NfcTagRegistry.SaveRegistry()
                .Select(t => new NfcTagData { Uid = t.Uid, Name = t.Name, Button = t.Button })
                .ToArray();
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
                    VendorId = cfg.VendorId,
                    ProductId = cfg.ProductId,
                    Customize = cfg.Customize,
                    ForceFeedbackEnabled = cfg.ForceFeedbackEnabled
                });
            }

            // Collect the per-(slot, device) configurations.
            // Lighting tab is per-device — different physical devices
            // mapped to the same slot can have different mode / colors
            // / palette. We write:
            //   1. ONE slot-level entry (DeviceGuid = Empty) holding
            //      the slot's anchor DeviceConfig — this is a
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
            // ONLY in the slot-level entry, and ApplyDeviceSlotConfigs's
            // fan-out skip (which prevents the slot-level entry from
            // bleeding into other devices' dict entries) leaves the
            // active device's dict entry at defaults. Result: user's
            // Lighting tab edits don't survive an app restart.
            var deviceSlotConfigs = new System.Collections.Generic.List<ViewModels.DeviceSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                if (padVm.DeviceConfig != null)
                    deviceSlotConfigs.Add(BuildDeviceSlotConfigData(padVm.DeviceConfig, i, Guid.Empty));
                foreach (var kvp in padVm.PerDeviceSlotConfigs)
                {
                    if (kvp.Key == Guid.Empty || kvp.Value == null) continue;
                    deviceSlotConfigs.Add(BuildDeviceSlotConfigData(kvp.Value, i, kvp.Key));
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
                SoundPackages = soundPackages,
                NfcTags = nfcTags,
                // Remote Link (issue #138): persist the identity + trust list from
                // the runtime holder (set on load / updated on pairing + revocation).
                RemoteLinkIdentityPrivate = RemoteLink?.ProtectedPrivateBase64 ?? "",
                RemoteLinkIdentityPublic = RemoteLink?.PublicBase64 ?? "",
                RemoteLinkIdentityProtection = (RemoteLink?.IdentityProtection ?? PadForge.Engine.RemoteLink.IdentityProtectionMode.Secure).ToString(),
                RemoteLinkPeers = RemoteLink?.Trust?.Peers?.ToArray(),
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
                EnableCommunityConfigLookup = vm.EnableCommunityConfigLookup,
                ShowLegacyWorkshopConfigs = vm.ShowLegacyWorkshopConfigs,
                ActiveProfileId = SettingsManager.ActiveProfileId,
                GlobalMacros = SettingsManager.GlobalMacros,
                SlotControllerTypes = isDefault ? slotTypes : defaultSnap.SlotControllerTypes,
                SlotProfileIds = isDefault ? slotProfileIds : defaultSnap.SlotProfileIds,
                SlotSoundVolumes = _mainVm.Pads.Select(p => p.SoundMasterVolume).ToArray(),
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
                EnableRemoteLink = _mainVm.Dashboard.EnableRemoteLink,
                RemoteLinkAutoReconnect = _mainVm.Dashboard.AutoReconnect,
                RemoteLinkPort = _mainVm.Dashboard.RemoteLinkPort,
                EnableTouchpadOverlay = _mainVm.Dashboard.EnableTouchpadOverlay,
                EnableMenuOverlay = _mainVm.Dashboard.EnableMenuOverlay,
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
                FirstRunTourCompleted = vm.FirstRunTourCompleted,
                EnableInputHiding = vm.EnableInputHiding,
                KeepHidHideCloaksBetweenLaunches = vm.KeepHidHideCloaksBetweenLaunches,
                // Default profile's custom gestures. When a named profile is
                // active, defaultSnap.TouchpadGestures carries the gestures
                // recorded on the default; when default is active, pull
                // straight from InputService's working list via the provider
                // so saves of the default profile's gestures round-trip too.
                TouchpadGestures = isDefault
                    ? (TouchpadGesturesProvider?.Invoke() is { Length: > 0 } liveTg ? liveTg : null)
                    : defaultSnap.TouchpadGestures,
                HidHideWhitelistPaths = vm.HidHideWhitelistPaths.Count > 0
                    ? vm.HidHideWhitelistPaths.ToArray()
                    : null,
                ExtendedConfigs = isDefault ? extendedConfigs.ToArray() : defaultSnap.ExtendedConfigs,
                DeviceSlotConfigs = isDefault ? deviceSlotConfigs.ToArray() : defaultSnap.DeviceSlotConfigs,
                UserProfiles = _userProfiles.Count > 0 ? _userProfiles.ToArray() : null,
                MidiConfigs = isDefault ? BuildMidiConfigs() : defaultSnap.MidiConfigs,
                KbmConfigs = isDefault ? BuildKbmConfigs() : defaultSnap.KbmConfigs,
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
                    VendorId = cfg.VendorId,
                    ProductId = cfg.ProductId,
                    Customize = cfg.Customize,
                    ForceFeedbackEnabled = cfg.ForceFeedbackEnabled
                });
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>
        /// Snapshots device configs for every slot for profile
        /// storage. One DTO per slot's anchor (DeviceGuid empty) plus
        /// one per (slot, device) entry. Mirrors the load path's
        /// per-device handling.
        /// <para>Internal: the profile snapshot lanes (SnapshotCurrentProfile
        /// in InputService, UpdateActiveProfileSnapshot here) reuse it so a
        /// runtime profile switch captures lighting / adaptive triggers
        /// through the same converter as the main settings file.</para>
        /// </summary>
        internal ViewModels.DeviceSlotConfigData[] BuildDeviceConfigSnapshot()
        {
            var list = new System.Collections.Generic.List<ViewModels.DeviceSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                if (padVm.DeviceConfig != null)
                    list.Add(BuildDeviceSlotConfigData(padVm.DeviceConfig, i, Guid.Empty));
                // Always emit every per-device entry. See the comment
                // in BuildAppSettingsForActiveProfile's main collector
                // for why we don't dedup against the anchor — the
                // active device's per-device entry is what reloads
                // back into its dict slot on next launch.
                foreach (var kvp in padVm.PerDeviceSlotConfigs)
                {
                    if (kvp.Key == Guid.Empty || kvp.Value == null) continue;
                    list.Add(BuildDeviceSlotConfigData(kvp.Value, i, kvp.Key));
                }
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>Encodes a single <see cref="ViewModels.DeviceSlotConfig"/>
        /// into a <see cref="ViewModels.DeviceSlotConfigData"/> tagged with
        /// the (slot index, device GUID) pair. Empty <paramref name="deviceGuid"/>
        /// produces a legacy slot-level entry.</summary>
        private static ViewModels.DeviceSlotConfigData BuildDeviceSlotConfigData(
            ViewModels.DeviceSlotConfig cfg, int slotIndex, Guid deviceGuid)
        {
            return new ViewModels.DeviceSlotConfigData
            {
                SlotIndex = slotIndex,
                DeviceGuid = deviceGuid,
                // PlayerNumber-aware writer: Off below is deliberate,
                // never the pre-#191 "unset" sentinel.
                LightingRev = 1,
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
                AudioPassthroughEnabled = cfg.AudioPassthroughEnabled,
                AudioMirrorSourceId = cfg.AudioMirrorSourceId ?? string.Empty,
                AudioMirrorEngageMode = cfg.AudioMirrorEngageMode ?? "Always",
                AudioMirrorEngageDeviceGuid = cfg.AudioMirrorEngageDeviceGuid ?? string.Empty,
                AudioMirrorEngageButton = cfg.AudioMirrorEngageButton ?? string.Empty,
                AudioMirrorEngageReleaseMs = cfg.AudioMirrorEngageReleaseMs,
                AudioToneFilterMode = cfg.AudioToneFilterMode ?? "Off",
                AudioToneLimitHz = cfg.AudioToneLimitHz,
                MicLedMode = cfg.MicLedMode,
                MicLedFollowDeviceId = cfg.MicLedFollowDeviceId ?? string.Empty,
                MicLightOn = cfg.MicLightOn,
                PlayerLedMode = cfg.PlayerLedMode,
                PlayerLedBrightness = cfg.PlayerLedBrightness,
                GuideLedMode = cfg.GuideLedMode,
                GuideLedBrightness = cfg.GuideLedBrightness,
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
                LightbarInputReactivePalette = cfg.LightbarInputReactivePalette
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
        /// Snapshots KBM (SOCD) configs for only created KeyboardMouse slots
        /// (for profile storage).
        /// </summary>
        private ViewModels.KbmSlotConfigData[] BuildKbmConfigSnapshot()
        {
            var list = new System.Collections.Generic.List<ViewModels.KbmSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i] ||
                    _mainVm.Pads[i].OutputType != Engine.VirtualControllerType.KeyboardMouse)
                    continue;
                var cfg = _mainVm.Pads[i].KbmConfig;
                list.Add(new ViewModels.KbmSlotConfigData
                {
                    SlotIndex = i,
                    SocdMode = cfg.SocdMode,
                    SocdPairs = cfg.SocdPairs
                });
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private ViewModels.KbmSlotConfigData[] BuildKbmConfigs()
        {
            var list = new System.Collections.Generic.List<ViewModels.KbmSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var cfg = _mainVm.Pads[i].KbmConfig;
                list.Add(new ViewModels.KbmSlotConfigData
                {
                    SlotIndex = i,
                    SocdMode = cfg.SocdMode,
                    SocdPairs = cfg.SocdPairs
                });
            }
            return list.ToArray();
        }

        /// <summary>
        /// Collects macro data from all pad ViewModels for serialization.
        /// Internal: the profile snapshot lanes (SnapshotCurrentProfile in
        /// InputService, UpdateActiveProfileSnapshot here) reuse it so
        /// profiles carry macros through the same converter as the main
        /// settings file.
        /// <para>Returns EMPTY, never null, when no pad has a macro. On
        /// ProfileData, null Macros is the legacy sentinel for "saved before
        /// macros rode profiles, leave the live set alone" (see
        /// InputService.ApplyProfile). Emitting null for a profile the user
        /// authored with zero macros made it claim to be pre-macros-era, so
        /// switching to it kept the OUTGOING profile's macros live and
        /// firing. Only genuinely old XML, where the element is absent, still
        /// deserializes to null and keeps the legacy behavior.</para>
        /// </summary>
        internal MacroData[] BuildMacroData()
        {
            var list = new System.Collections.Generic.List<MacroData>();

            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                foreach (var macro in padVm.Macros)
                    list.Add(BuildMacroDataForMacro(macro, i));
            }

            return list.ToArray();
        }

        /// <summary>Serializes one <see cref="MacroItem"/> into its
        /// <see cref="MacroData"/> DTO. Extracted from <see cref="BuildMacroData"/>
        /// so the macro Duplicate / Copy / Paste commands (#112) can round-trip a
        /// single macro through the same mapping the save path uses.</summary>
        public static MacroData BuildMacroDataForMacro(MacroItem macro, int padIndex)
        {
            return new MacroData
            {
                PadIndex = padIndex,
                Name = macro.Name,
                IsEnabled = macro.IsEnabled,
                TriggerButtons = macro.TriggerButtons,
                TriggerDeviceGuid = macro.TriggerDeviceGuid != Guid.Empty
                    ? macro.TriggerDeviceGuid.ToString("N") : null,
                TriggerRawButtons = macro.TriggerRawButtons.Length > 0
                    ? string.Join(",", macro.TriggerRawButtons) : null,
                TriggerSource = macro.TriggerSource,
                TriggerMode = macro.TriggerMode,
                TriggerHoldMs = macro.TriggerHoldMs,
                ConsumeTriggerButtons = macro.ConsumeTriggerButtons,
                RepeatMode = macro.RepeatMode,
                RepeatCount = macro.RepeatCount,
                RepeatDelayMs = macro.RepeatDelayMs,
                TriggerCustomButtons = macro.TriggerCustomButtons,
                TriggerAxisTargets = macro.TriggerAxisTargetList,
                TriggerAxisDirections = macro.TriggerAxisDirectionList,
                TriggerAxisThreshold = macro.TriggerAxisThreshold,
                TriggerPovs = macro.TriggerPovs?.Length > 0 ? macro.TriggerPovs : null,
                TriggerInputs = string.IsNullOrEmpty(macro.TriggerInputs) ? null : macro.TriggerInputs,
                TriggerExpression = string.IsNullOrEmpty(macro.TriggerExpression) ? null : macro.TriggerExpression,
                TriggerExpressionVariables = macro.TriggerExpressionVariableSpecs,
                Actions = macro.Actions.Select(BuildActionData).ToArray()
            };
        }

        /// <summary>Serializes one <see cref="MacroAction"/> into its
        /// <see cref="ActionData"/> DTO. Extracted from <see cref="BuildMacroData"/>
        /// so the action Duplicate command (#112) reuses the same field mapping.</summary>
        public static ActionData BuildActionData(MacroAction a)
        {
            return new ActionData
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
                LightbarCycleModesCsv = a.LightbarCycleModesCsv,
                PointerCycleModesCsv = a.PointerCycleModesCsv,
                PointerSetMode = a.PointerSetMode,
                GuideLedPercent = a.GuideLedPercent,
                SoundFilePath = string.IsNullOrEmpty(a.SoundFilePath) ? null : a.SoundFilePath,
                SoundVolume = a.SoundVolume,
                SoundLoop = a.SoundLoop,
                SetGyroEngagedMode = a.SetGyroEngagedMode,
                RumbleHoldMode = a.RumbleHoldMode,
                RumbleStrengthLeft = a.RumbleStrengthLeft,
                RumbleStrengthRight = a.RumbleStrengthRight,
                RumbleHoldMs = a.RumbleHoldMs,
                RumbleFadeMs = a.RumbleFadeMs,
                CursorRecenterMode = a.CursorRecenterMode,
                CursorPinMode = a.CursorPinMode,
                CursorPinX = a.CursorPinX,
                CursorPinY = a.CursorPinY,
                MouseX = a.MouseX,
                MouseY = a.MouseY,
                IntervalMs = a.IntervalMs,
                CursorClampMode = a.CursorClampMode,
                CursorClampInsetX = a.CursorClampInsetX,
                CursorClampInsetY = a.CursorClampInsetY,
                DisconnectTarget = a.DisconnectTarget,
                DisconnectDeviceGuid = a.DisconnectDeviceGuid == Guid.Empty
                    ? null : a.DisconnectDeviceGuid.ToString(),
                ProgramPath = string.IsNullOrEmpty(a.ProgramPath) ? null : a.ProgramPath,
                ProgramArgs = string.IsNullOrEmpty(a.ProgramArgs) ? null : a.ProgramArgs,
                ProgramWorkingDir = string.IsNullOrEmpty(a.ProgramWorkingDir) ? null : a.ProgramWorkingDir,
                TextContent = string.IsNullOrEmpty(a.TextContent) ? null : a.TextContent,
                TextPerCharDelayMs = a.TextPerCharDelayMs
            };
        }

        /// <summary>JSON envelope for the macro clipboard (#112).</summary>
        public sealed class MacroClipboardEnvelope
        {
            public string Type { get; set; }
            public int Version { get; set; }
            public MacroData[] Macros { get; set; }
        }

        private const string MacroClipboardType = "PadForgeMacro";

        /// <summary>Serializes macros into the clipboard envelope JSON (#112).</summary>
        public static string SerializeMacrosToClipboard(MacroData[] macros)
            => System.Text.Json.JsonSerializer.Serialize(new MacroClipboardEnvelope
            {
                Type = MacroClipboardType,
                Version = 1,
                Macros = macros,
            });

        /// <summary>Parses a clipboard string into a macro envelope, or null when the
        /// text is not a PadForge macro envelope (#112). Never throws.</summary>
        public static MacroClipboardEnvelope TryParseMacroClipboard(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var env = System.Text.Json.JsonSerializer.Deserialize<MacroClipboardEnvelope>(json);
                return env != null
                    && string.Equals(env.Type, MacroClipboardType, StringComparison.Ordinal)
                    && env.Macros != null
                    ? env : null;
            }
            catch { return null; }
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
                    ps.RotationRange = padVm.WheelRotationRange.ToString();
                    ps.AutoCenterStrength = padVm.WheelAutoCenter.ToString();
                    ps.WheelRpmLeds = padVm.WheelRpmLeds ? "1" : "0";
                    ps.LeftMotorStrength = padVm.LeftMotorStrength.ToString();
                    ps.RightMotorStrength = padVm.RightMotorStrength.ToString();
                    ps.ForceSwapMotor = padVm.SwapMotors ? "1" : "0";

                    // Write impulse trigger settings.
                    ps.ImpulseOverallGain = padVm.ImpulseOverallGain.ToString();
                    ps.ImpulseLeftStrength = padVm.ImpulseLeftStrength.ToString();
                    ps.ImpulseRightStrength = padVm.ImpulseRightStrength.ToString();
                    ps.ImpulseSwapTriggers = padVm.ImpulseSwapTriggers ? "1" : "0";
                    ps.ConstantTriggerForceEnabled = padVm.ConstantTriggerForceEnabled ? "1" : "0";
                    ps.AudioRumbleTriggersEnabled = padVm.AudioRumbleTriggersEnabled ? "1" : "0";
                    ps.AudioRumbleLeftTrigger = padVm.AudioRumbleLeftTrigger.ToString();
                    ps.AudioRumbleRightTrigger = padVm.AudioRumbleRightTrigger.ToString();

                    // Issue #50: all double→string conversions MUST use InvariantCulture.
                    // WARNING: if you add a new double property below, use .ToString(ic)
                    // — NOT bare .ToString(). See InputService.SaveViewModelToPadSetting
                    // for the full explanation of the locale data-loss bug.
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    ps.ConstantTriggerForceLeft = padVm.ConstantTriggerForceLeft.ToString("F4", ic);
                    ps.ConstantTriggerForceRight = padVm.ConstantTriggerForceRight.ToString("F4", ic);
                    ps.AudioRumbleTriggersSensitivity = padVm.AudioRumbleTriggersSensitivity.ToString("F1", ic);
                    ps.AudioRumbleTriggersCutoffHz = padVm.AudioRumbleTriggersCutoffHz.ToString("F0", ic);

                    // Write gyro tuning (per-(device, slot)).
                    ps.GyroSensitivityH = padVm.GyroSensitivityH.ToString(ic);
                    ps.GyroSensitivityV = padVm.GyroSensitivityV.ToString(ic);
                    ps.GyroDeadZoneDegPerSec = padVm.GyroDeadZoneDegPerSec.ToString(ic);
                    ps.GyroSmoothingAlpha = padVm.GyroSmoothingAlpha.ToString(ic);
                    ps.GyroAcceleration = padVm.GyroAcceleration.ToString(ic);
                    ps.GyroOutputCurve = padVm.GyroOutputCurve ?? "Linear";
                    ps.GyroSensitivityUnits = padVm.GyroSensitivityUnits ?? "Multiplier";
                    ps.GyroEasyAimStickThreshold = padVm.GyroEasyAimStickThreshold.ToString(ic);
                    ps.GyroEngageStickSide = string.IsNullOrEmpty(padVm.GyroEngageStickSide) ? "Right" : padVm.GyroEngageStickSide;
                    ps.GyroEngageStickDirection = string.IsNullOrEmpty(padVm.GyroEngageStickDirection) ? "Full" : padVm.GyroEngageStickDirection;
                    ps.IrSensorBarPos = padVm.IrSensorBarPos.ToString(ic);
                    ps.IrSensorBarComp = (padVm.IrSensorBarCompPercent / 100.0).ToString(ic);
                    ps.IrSmoothing = (padVm.IrSmoothingPercent / 100.0).ToString(ic);
                    ps.PointerMode = string.IsNullOrEmpty(padVm.PointerMode) ? "Mouse" : padVm.PointerMode;
                    ps.PointerFpsSpeed = padVm.PointerFpsSpeed.ToString(ic);

                    // Write JoyShockMapper-canongyro extensions.
                    ps.GyroSpace = padVm.GyroSpace ?? "Local";
                    ps.GyroPlayerSpaceYawRelaxFactor = padVm.GyroPlayerSpaceYawRelaxFactor.ToString(ic);
                    ps.GyroWorldSpaceSideReductionThreshold = padVm.GyroWorldSpaceSideReductionThreshold.ToString(ic);
                    ps.GyroTighteningThresholdDegPerSec = padVm.GyroTighteningThresholdDegPerSec.ToString(ic);
                    ps.GyroSmoothingThresholdDegPerSec = padVm.GyroSmoothingThresholdDegPerSec.ToString(ic);
                    ps.GyroSmoothingWindowMs = padVm.GyroSmoothingWindowMs.ToString(ic);
                    ps.GyroRealWorldCalibration = padVm.GyroRealWorldCalibration.ToString(ic);
                    ps.GyroAimEngageButton = padVm.GyroAimEngageButton ?? "";
                    ps.GyroAimEngageDeviceGuid = padVm.GyroAimEngageDeviceGuid ?? "";
                    ps.GyroAimEngageMode = string.IsNullOrEmpty(padVm.GyroAimEngageMode) ? "Hold" : padVm.GyroAimEngageMode;
                    ps.GyroInvertPitch = padVm.GyroInvertPitch ? "1" : "0";
                    ps.GyroInvertYawRoll = padVm.GyroInvertYawRoll ? "1" : "0";
                    ps.GyroApplyTuningToPassthrough = padVm.GyroApplyTuningToPassthrough ? "1" : "0";

                    // Write Motion Steering tuning (per-(device, slot)) — settings for
                    // the "Motion Lean" input descriptor. No Enabled/Target keys: the
                    // input is mapped from the picker, never stamped onto a target.
                    ps.SetExtendedMapping("MotionSteerInner", padVm.MotionSteerInnerDz.ToString(ic));
                    ps.SetExtendedMapping("MotionSteerOuter", padVm.MotionSteerOuterDz.ToString(ic));
                    ps.SetExtendedMapping("MotionSteerOrient", padVm.MotionSteerOrient);

                    // Write Flick Stick card tuning (#225), same bag.
                    SaveFlickStickCard(padVm, ps);

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

                    // Steering at-lock feedback (#94).
                    ps.SteeringLockRumbleEnabled = padVm.SteeringLockRumbleEnabled ? "1" : "0";
                    ps.SteeringLockTriggerVibEnabled = padVm.SteeringLockTriggerVibEnabled ? "1" : "0";
                    ps.SteeringLockLightbarEnabled = padVm.SteeringLockLightbarEnabled ? "1" : "0";
                    ps.SteeringLockATResistanceEnabled = padVm.SteeringLockATResistanceEnabled ? "1" : "0";
                    ps.SteeringLockPulseMs = ((int)padVm.SteeringLockPulseMs).ToString(ic);
                    ps.SteeringLockLightbarColor = padVm.SteeringLockLightbarColor ?? "#FF0000";
                    ps.SteeringLockLightbarColorSource = padVm.SteeringLockLightbarColorSource.ToString();
                    ps.SteeringLockLightbarPaletteCsv = padVm.SteeringLockLightbarPaletteCsv ?? "";
                    ps.SteeringLockLightbarHoldMs = ((int)padVm.SteeringLockLightbarHoldMs).ToString(ic);
                    ps.SteeringLockLightbarFadeMs = ((int)padVm.SteeringLockLightbarFadeMs).ToString(ic);

                    // Trigger rumble routing (#102), mirroring
                    // InputService.SaveViewModelToPadSetting so the autosave
                    // writer covers the same fields as the 30 Hz sync.
                    ps.LeftTriggerRouteSource = string.IsNullOrEmpty(padVm.LeftTriggerRouteSource) ? "None" : padVm.LeftTriggerRouteSource;
                    ps.RightTriggerRouteSource = string.IsNullOrEmpty(padVm.RightTriggerRouteSource) ? "None" : padVm.RightTriggerRouteSource;
                    ps.LeftTriggerRouteMode = string.IsNullOrEmpty(padVm.LeftTriggerRouteMode) ? "Duplicate" : padVm.LeftTriggerRouteMode;
                    ps.RightTriggerRouteMode = string.IsNullOrEmpty(padVm.RightTriggerRouteMode) ? "Duplicate" : padVm.RightTriggerRouteMode;
                    ps.LeftTriggerRouteScale = padVm.LeftTriggerRouteScale.ToString();
                    ps.RightTriggerRouteScale = padVm.RightTriggerRouteScale.ToString();
                    ps.LeftTriggerRouteActivator = padVm.LeftTriggerRouteActivator ?? "";
                    ps.RightTriggerRouteActivator = padVm.RightTriggerRouteActivator ?? "";
                    ps.LeftTriggerRouteActivatorDeviceGuid = padVm.LeftTriggerRouteActivatorDeviceGuid ?? "";
                    ps.RightTriggerRouteActivatorDeviceGuid = padVm.RightTriggerRouteActivatorDeviceGuid ?? "";
                    ps.LeftTriggerRouteActivatorMode = string.IsNullOrEmpty(padVm.LeftTriggerRouteActivatorMode) ? "Hold" : padVm.LeftTriggerRouteActivatorMode;
                    ps.RightTriggerRouteActivatorMode = string.IsNullOrEmpty(padVm.RightTriggerRouteActivatorMode) ? "Hold" : padVm.RightTriggerRouteActivatorMode;

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
                    // Boundary calibration maps (#174). This flush mirrors the
                    // 30 Hz SaveViewModelToPadSetting, but it also runs when the
                    // engine is stopped (SaveToFile calls it first), so without
                    // these a boundary reset or recalibration made while stopped
                    // would not persist (the 30 Hz writer is gated on IsRunning).
                    ps.LeftThumbBoundaryMap = padVm.LeftThumbBoundaryMap ?? string.Empty;
                    ps.RightThumbBoundaryMap = padVm.RightThumbBoundaryMap ?? string.Empty;

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
                    // Per-stick steering mode + tunables (#94), every stick index. The
                    // engine reads Kind off the MappingSet rows (stamped in
                    // SaveViewModelToMappingSet), but the settings persist here too so
                    // the Sticks-tab card can reload them.
                    foreach (var stick in padVm.StickConfigs)
                    {
                        int g = stick.Index;
                        if (g < 0) continue;
                        ps.SetExtendedMapping($"Stick{g}SteerKind", stick.SteeringKind);
                        ps.SetExtendedMapping($"Stick{g}SteerWindRange", stick.WindRangeDeg.ToString(ic));
                        ps.SetExtendedMapping($"Stick{g}SteerWindPower", stick.WindPower.ToString(ic));
                        ps.SetExtendedMapping($"Stick{g}SteerWindUnwind", stick.WindUnwindRate.ToString(ic));
                        ps.SetExtendedMapping($"Stick{g}SteerAngleInner", stick.AngleInnerDz.ToString(ic));
                        ps.SetExtendedMapping($"Stick{g}SteerAngleOuter", stick.AngleOuterDz.ToString(ic));
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

                        ps.SetMappingBidirectional(mapping.TargetSettingName, mapping.IsBidirectional ? "1" : "");
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
            // Closure by construction, not by hand-list. The old body
            // enumerated fields one by one and rotted as features landed
            // (user-confirmed: the HidHide toggles and whitelist, the
            // touchpad overlay group, gyro / steering / wheel / trigger
            // routing / touchpad-gesture tuning, and the mapping extras
            // all survived a reset). The rebuild leans on the two load
            // mirrors that normal persistence already keeps complete:
            // a fresh AppSettingsData carries every app-level default,
            // and a blank PadSetting carries every per-pad tuning
            // default. Anything a future feature persists through the
            // save/load chain is then reset automatically.

            // 1. Stores. Devices (including per-device HidHideEnabled
            //    records), slot bindings + their PadSettings, per-slot
            //    mapping sets (multi-source extras, shift layers), any
            //    pending default-profile snapshot, and imported
            //    HIDMaestro profile JSONs.
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                SettingsManager.UserDevices.Items.Clear();
            }

            lock (SettingsManager.UserSettings.SyncRoot)
            {
                SettingsManager.UserSettings.Items.Clear();
            }

            SettingsManager.SlotMappingSets =
                new Engine.Data.MappingSet[Common.Input.InputManager.MaxPads];
            SettingsManager.PendingDefaultSnapshot = null;
            // Sibling contract from RemoveUserProfile: every _userProfiles
            // mutation reloads the HM catalog, or the Extended profile
            // dropdown keeps serving the wiped imports for the session.
            _userProfiles.Clear();
            Common.Input.HMaestroProfileCatalog.Reload();

            // 2. Per-pad surfaces. ResetAllSettings covers what the load
            //    mirror can't reach (macros, sensitivity-curve items,
            //    dead-zone shapes, per-device configs), the
            //    blank-PadSetting load resets every writer-mirrored
            //    tuning field to its canonical default, and the mapping
            //    refresh rebuilds every row from the now-empty mapping
            //    set (descriptors, extra sources, per-mapping dead zones,
            //    combine modes all clear in one pass).
            foreach (var padVm in _mainVm.Pads)
            {
                padVm.ResetAllSettings();
                InputService.LoadPadSettingIntoViewModel(padVm, new Engine.Data.PadSetting());
                padVm.SoundMasterVolume = 100;
                // In place, never by instance replacement: MainWindow's
                // MarkDirty autosave hook subscribes to these objects'
                // PropertyChanged once at startup, and replacing them
                // would silently sever Extended-config persistence for
                // the rest of the session.
                padVm.ExtendedConfig.ResetToDefaults();
                padVm.MidiConfig.ResetToDefaults();
                padVm.KbmConfig.ResetToDefaults();
                padVm.OutputType = Engine.VirtualControllerType.Xbox;
                padVm.ProfileId = Common.Input.InputManager.GetDefaultProfileId(padVm.OutputType);
                padVm.ActiveLayerMask = "Base";
                // Sibling of ApplyProfile's sequence: without the rebuild
                // the old shift-layer tab strip ghosts over the emptied
                // mapping set until an engine restart.
                padVm.RebuildLayerTabs(null);
                InputService.RefreshMappingsToViewModel(padVm);
            }

            // 3. App-level surface: Settings page (HidHide toggles +
            //    whitelist, language, theme, engine options, 2D view),
            //    Dashboard page (DSU / web controller / Remote Link /
            //    touchpad overlay), Remote Link identity + trusted
            //    peers, sound-package and NFC registries, global macro
            //    shortcuts, window placement. A fresh AppSettingsData's
            //    initializers ARE the fresh-install defaults, and
            //    LoadAppSettings pushes every one of them through the
            //    same setters startup uses (so side effects like the
            //    start-at-login scheduled task and the live DSU / web
            //    server toggles apply themselves).
            _mainVm.Settings.ProfileShortcuts.Clear();
            LoadAppSettings(new AppSettingsData());
            // LoadAppSettings can't reset the language: a fresh
            // AppSettingsData carries Language = "" and
            // SetLanguageFromCode no-ops on the empty code. Fresh-install
            // state is "no explicit selection, follow the OS display
            // language", which takes its own path.
            _mainVm.Settings.ResetLanguageToSystemDefault();

            // 4. Profiles: back to the single built-in Default entry.
            var settingsVm = _mainVm.Settings;
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

            // MarkDirty, not a bare IsDirty write: only MarkDirty arms
            // the autosave debounce, and nothing else in this method is
            // guaranteed to (store clears and collection Clears fire no
            // watched property changes). Without it a reset could sit
            // unsaved until app close and resurrect on a crash.
            MarkDirty();
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
                _mainVm.SetStatus(Strings.Instance.Status_NoSettingsFile, persist: true);
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
        /// after a 250 ms debounce period. Safe from any thread: a
        /// DispatcherTimer constructed on a non-pumping threadpool thread
        /// never ticks, and the first caller can be a worker (gyro
        /// auto-calibration persists via this method), which would have left
        /// autosave dead for the whole session (audit F8). The dirty flags
        /// are set immediately; the timer work marshals to the UI dispatcher.
        /// </summary>
        public void MarkDirty()
        {
            IsDirty = true;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return; // shutdown: OnClosing's dirty-gated save covers it
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(MarkDirtyOnUiThread));
                return;
            }
            MarkDirtyOnUiThread();
        }

        private void MarkDirtyOnUiThread()
        {
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

    /// <summary>One registered sound package (issue #83 follow-up).</summary>
    public class SoundPackageData
    {
        [XmlAttribute]
        public string Name { get; set; }

        /// <summary>Exe-relative when under the application directory.</summary>
        [XmlAttribute]
        public string Path { get; set; }
    }

    /// <summary>A registered NFC tag (issue #150): UID (uppercase hex), name, and
    /// the stable raw-button index it occupies (so saved macro bindings survive).</summary>
    public class NfcTagData
    {
        [XmlAttribute]
        public string Uid { get; set; }

        [XmlAttribute]
        public string Name { get; set; }

        [XmlAttribute]
        public int Button { get; set; }
    }

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

        /// <summary>Migrates pre-v4.x element spellings forward after
        /// deserialization: the per-(slot, device) config arrays were
        /// written as PlayStationConfigs / ProfilePlayStationConfigs before
        /// the bag was genericized. New saves emit only the new names; the
        /// legacy properties exist for reading old files and are cleared
        /// here so nothing re-serializes them.</summary>
        public void MigrateLegacySchema()
        {
            AppSettings?.MigrateLegacySchema();
            if (Profiles != null)
                foreach (var p in Profiles)
                    p?.MigrateLegacySchema();
        }
    }

    /// <summary>
    /// Application-level settings stored in the XML file.
    /// </summary>
    public class AppSettingsData
    {
        /// <summary>Registered sound packages (issue #83 follow-up):
        /// Name + stored path (exe-relative when the package sits in the
        /// application directory, for portable kits).</summary>
        [XmlArray("SoundPackages")]
        [XmlArrayItem("Package")]
        public SoundPackageData[] SoundPackages { get; set; }

        /// <summary>Registered NFC tags (issue #150): each UID + chosen name,
        /// exposed by the NFC reader device as a named, bindable button.</summary>
        [XmlArray("NfcTags")]
        [XmlArrayItem("Tag")]
        public NfcTagData[] NfcTags { get; set; }

        // ── Remote Link (issue #138) — global (per-machine), not per-profile ──
        /// <summary>This instance's static identity private key, DPAPI-protected
        /// (base64). Empty until the first Remote Link use mints one.</summary>
        [XmlElement]
        public string RemoteLinkIdentityPrivate { get; set; } = "";

        /// <summary>This instance's static identity public key (base64). Not secret.</summary>
        [XmlElement]
        public string RemoteLinkIdentityPublic { get; set; } = "";

        /// <summary>How the private key is wrapped at rest: Secure (machine-bound, default),
        /// PortablePassword, or PortableOpen. Drives thumb-drive portability (issue #138).</summary>
        [XmlElement]
        public string RemoteLinkIdentityProtection { get; set; } = "Secure";

        /// <summary>Trusted paired peers. Old files lack this element and load as null.</summary>
        [XmlArray("RemoteLinkPeers")]
        [XmlArrayItem("Peer")]
        public PadForge.Engine.RemoteLink.PeerTrust[] RemoteLinkPeers { get; set; }

        /// <summary>Whether the Remote Link server listens. Global, not per-profile.</summary>
        [XmlElement]
        public bool EnableRemoteLink { get; set; }

        /// <summary>Auto-reconnect: when a paired PC is seen on the LAN, establish the link
        /// without a click (issue #138). Default on.</summary>
        [XmlElement]
        public bool RemoteLinkAutoReconnect { get; set; } = true;

        [XmlElement]
        public int RemoteLinkPort { get; set; } = 27500;

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

        /// <summary>
        /// Master opt-in for the Steam Workshop community-config feature
        /// (issue #9). Default false: PadForge sends nothing to Steam until
        /// the user flips this in Settings or in the browse dialog's
        /// cold-forge state.
        /// </summary>
        [XmlElement]
        public bool EnableCommunityConfigLookup { get; set; }

        /// <summary>
        /// Sub-toggle: surface 2016-era Workshop configs that have no CDN
        /// file (Legacy badge + Steam-subscribe fallback). Default false.
        /// </summary>
        [XmlElement]
        public bool ShowLegacyWorkshopConfigs { get; set; }

        [XmlElement]
        public string ActiveProfileId { get; set; }

        /// <summary>
        /// Per-slot virtual controller output types.
        /// Array of ints matching VirtualControllerType enum values.
        /// </summary>
        [XmlArray("SlotControllerTypes")]
        [XmlArrayItem("Type")]
        public int[] SlotControllerTypes { get; set; }

        /// <summary>Per-slot master volume for macro sounds (0-100).</summary>
        [XmlArray("SlotSoundVolumes")]
        [XmlArrayItem("Volume")]
        public int[] SlotSoundVolumes { get; set; }

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

        /// <summary>Radial / touch menu overlay (#9 B-17). Default on;
        /// menus still hover and commit blind when disabled.</summary>
        [XmlElement]
        public bool EnableMenuOverlay { get; set; } = true;

        /// <summary>Default profile's custom touchpad gestures.
        /// Named profiles store theirs under each profile's
        /// <c>ProfileData.TouchpadGestures</c>; when the default profile
        /// is active there's no ProfileData being saved alongside, so
        /// the active gesture catalog lives here directly. Round-trips
        /// via the SettingsService load + save paths and re-seeds
        /// InputService._activeTouchpadGestures at startup.</summary>
        [XmlArray("DefaultProfileTouchpadGestures")]
        [XmlArrayItem("Gesture")]
        public PadForge.Engine.Touchpad.TouchpadCustomGesture[] TouchpadGestures { get; set; }

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
        /// True once the first-run welcome tour has been completed or
        /// skipped. Replaces the pre-v4 PadForge.firstrun marker file
        /// beside the exe: all persisted state lives in this single
        /// settings file.
        /// </summary>
        [XmlElement]
        public bool FirstRunTourCompleted { get; set; }

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
        /// Per-(slot, device) configuration (adaptive triggers, lighting,
        /// audio, tone filter) for ANY hardware on any slot type. Null on
        /// settings files older than v3.1.0, where slots fall back to
        /// out-of-the-box defaults (all triggers Off, lightbar disabled,
        /// audio neutral) so the schema add is a clean no-op for legacy
        /// files. Written as DeviceSlotConfigs since the v4.x schema
        /// rename; older files spelled it PlayStationConfigs (see
        /// <see cref="LegacyDeviceSlotConfigs"/>).
        /// </summary>
        [XmlArray("DeviceSlotConfigs")]
        [XmlArrayItem("Config")]
        public ViewModels.DeviceSlotConfigData[] DeviceSlotConfigs { get; set; }

        /// <summary>Read-only compatibility spelling. Files saved before the
        /// v4.x schema rename carry the bag as PlayStationConfigs, a name
        /// that predates the config growing Xbox / Nintendo / generic
        /// device features. <see cref="MigrateLegacySchema"/> moves it into
        /// <see cref="DeviceSlotConfigs"/> on load, and ShouldSerialize
        /// keeps it out of every save, so one load-save cycle modernizes
        /// the file.</summary>
        [XmlArray("PlayStationConfigs")]
        [XmlArrayItem("Config")]
        public ViewModels.DeviceSlotConfigData[] LegacyDeviceSlotConfigs { get; set; }
        public bool ShouldSerializeLegacyDeviceSlotConfigs() => false;

        /// <summary>Adopts the legacy spelling when the new one is absent
        /// (new wins if a hand-edited file somehow carries both), then
        /// clears the legacy slot so nothing downstream sees two copies.
        /// Also migrates <see cref="DefaultProfileSnapshot"/>: it is a full
        /// ProfileData, so an old file's snapshot can carry the legacy
        /// profile-level spelling too.</summary>
        public void MigrateLegacySchema()
        {
            if ((DeviceSlotConfigs == null || DeviceSlotConfigs.Length == 0)
                && LegacyDeviceSlotConfigs != null && LegacyDeviceSlotConfigs.Length > 0)
                DeviceSlotConfigs = LegacyDeviceSlotConfigs;
            LegacyDeviceSlotConfigs = null;
            DefaultProfileSnapshot?.MigrateLegacySchema();
        }

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
        /// Per-slot keyboard + mouse configuration (SOCD / Snap Tap,
        /// discussion #205). Null on old settings files, which use defaults.
        /// </summary>
        [XmlArray("KbmConfigs")]
        [XmlArrayItem("Config")]
        public ViewModels.KbmSlotConfigData[] KbmConfigs { get; set; }

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

        /// <summary>Continuous-hold threshold in ms for
        /// <see cref="MacroTriggerMode.HoldForMs"/> (issue #9 wave 1b).
        /// Default 500 so profiles saved before the mode existed load with
        /// the same value a fresh macro gets.</summary>
        [XmlElement]
        public int TriggerHoldMs { get; set; } = 500;

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

        /// <summary>Comma-separated per-axis direction filters (Any/Positive/
        /// Negative), parallel to <see cref="TriggerAxisTargets"/> (#154).
        /// Null means Any for every axis, which is also the pre-#154 shape.</summary>
        [XmlElement]
        public string TriggerAxisDirections { get; set; }

        /// <summary>Axis trigger threshold percentage (1-100).</summary>
        [XmlElement]
        public int TriggerAxisThreshold { get; set; } = 50;

        /// <summary>POV triggers stored as "povIndex:centidegrees" entries.</summary>
        [XmlArray("TriggerPovs")]
        [XmlArrayItem("Pov")]
        public string[] TriggerPovs { get; set; }

        /// <summary>Pipe-separated <see cref="MacroItem.TriggerInputEntry.Spec"/>
        /// entries for the multi-device trigger combo (cross-device button +
        /// POV combos, e.g. controller-X + keyboard-A + mouse-Left). When
        /// non-empty, this is authoritative over the single-device legacy
        /// fields above. Null / empty on macros saved by older PadForge
        /// versions that pre-dated multi-device support — the runtime
        /// migrates from the legacy fields on first access.</summary>
        [XmlElement]
        public string TriggerInputs { get; set; }

        /// <summary>Custom-expression formula (used when
        /// <see cref="TriggerMode"/> is <c>CustomExpression</c>). Empty / null
        /// when the macro uses one of the legacy trigger modes.</summary>
        [XmlElement]
        public string TriggerExpression { get; set; }

        /// <summary>Pipe-separated <see cref="MacroExpressionVariable.Spec"/> entries
        /// in a/b/c/... order. Empty entries are preserved so indexing remains
        /// stable across a load even if some variables are still unbound.</summary>
        [XmlElement]
        public string TriggerExpressionVariables { get; set; }

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
        [XmlElement] public string PointerCycleModesCsv { get; set; } = "Mouse,FpsMouse,Mouse43,Mouse169";
        /// <summary>Target mode name for PointerModeSet (issue #203 follow-up).</summary>
        [XmlElement] public string PointerSetMode { get; set; } = "Mouse";
        /// <summary>Brightness percent for GuideLedBrightness (#209).</summary>
        [XmlElement] public int GuideLedPercent { get; set; } = 100;

        /// <summary>Sound file path for PlaySound (issue #83). Null when unset.</summary>
        [XmlElement]
        public string SoundFilePath { get; set; }

        /// <summary>Per-action sound volume percentage (1-100). Default 100.</summary>
        [XmlElement]
        public int SoundVolume { get; set; } = 100;

        /// <summary>Loop the sound until SoundStop / trigger release.</summary>
        [XmlElement]
        public bool SoundLoop { get; set; }

        /// <summary>Write mode for SetGyroEngaged (Toggle / On / Off).</summary>
        [XmlElement] public ViewModels.MacroSetGyroEngagedMode SetGyroEngagedMode { get; set; } = ViewModels.MacroSetGyroEngagedMode.Toggle;

        /// <summary>Reactive / Sticky hold for Rumble and RumbleTrigger (issue #102).</summary>
        [XmlElement] public ViewModels.MacroRumbleHoldMode RumbleHoldMode { get; set; } = ViewModels.MacroRumbleHoldMode.Reactive;
        /// <summary>Left (heavy) motor strength 0..100 for Rumble / RumbleTrigger.</summary>
        [XmlElement] public int RumbleStrengthLeft { get; set; } = 100;
        /// <summary>Right (light) motor strength 0..100 for Rumble / RumbleTrigger.</summary>
        [XmlElement] public int RumbleStrengthRight { get; set; } = 100;
        /// <summary>Full-strength hold window for Reactive rumble, ms.</summary>
        [XmlElement] public int RumbleHoldMs { get; set; } = 100;
        /// <summary>Fade-out window for Reactive rumble, ms.</summary>
        [XmlElement] public int RumbleFadeMs { get; set; } = 200;

        /// <summary>Which axes a MouseRecenter action snaps to center (issue #108).</summary>
        [XmlElement] public ViewModels.CursorRecenterMode CursorRecenterMode { get; set; } = ViewModels.CursorRecenterMode.XAndY;

        /// <summary>Which axes a MouseFixPosition action pins (issue #109).</summary>
        [XmlElement] public ViewModels.CursorPinMode CursorPinMode { get; set; } = ViewModels.CursorPinMode.XAndY;
        /// <summary>Pin target X in primary-monitor pixels (issue #109).</summary>
        [XmlElement] public int CursorPinX { get; set; }
        /// <summary>Pin target Y in primary-monitor pixels (issue #109).</summary>
        [XmlElement] public int CursorPinY { get; set; }

        /// <summary>MoveMouseToScreenPosition target X in primary-monitor pixels (issue #9).</summary>
        [XmlElement] public int MouseX { get; set; }
        /// <summary>MoveMouseToScreenPosition target Y in primary-monitor pixels (issue #9).</summary>
        [XmlElement] public int MouseY { get; set; }
        /// <summary>Turbo interval in milliseconds (issue #9): the
        /// RepeatKeyWhileHeld pulse period, shared by the wave-1b
        /// RepeatVcButtonWhileHeld square wave. The wave-1b actions add no
        /// DTO fields of their own: the VC-button pair targets via the
        /// existing ButtonFlags / CustomButtons above (the ButtonPress
        /// addressing pair) and ToggleKey targets via KeyCode / KeyString,
        /// so this element plus those carry the whole family.</summary>
        [XmlElement] public int IntervalMs { get; set; } = 100;

        /// <summary>Which axes a MouseLimitRegion action clamps (issue #110).</summary>
        [XmlElement] public ViewModels.CursorClampMode CursorClampMode { get; set; } = ViewModels.CursorClampMode.XAndY;
        /// <summary>Per-edge X inset for the region clamp, pixels (issue #110).</summary>
        [XmlElement] public int CursorClampInsetX { get; set; } = 50;
        /// <summary>Per-edge Y inset for the region clamp, pixels (issue #110).</summary>
        [XmlElement] public int CursorClampInsetY { get; set; } = 50;

        /// <summary>Target mode for a DisconnectController action (issue #162).</summary>
        [XmlElement] public ViewModels.MacroDisconnectTarget DisconnectTarget { get; set; } = ViewModels.MacroDisconnectTarget.TriggeringDevice;
        /// <summary>Victim device GUID for Specific-device mode (issue #162).</summary>
        [XmlElement] public string DisconnectDeviceGuid { get; set; }

        /// <summary>Program/file path for a RunProgram action (user request).</summary>
        [XmlElement] public string ProgramPath { get; set; }
        /// <summary>Command-line arguments for a RunProgram action.</summary>
        [XmlElement] public string ProgramArgs { get; set; }
        /// <summary>Working folder for a RunProgram action.</summary>
        [XmlElement] public string ProgramWorkingDir { get; set; }

        /// <summary>Plain text a TextBlock action types out (issue #201). No
        /// packing or delimiter rules apply: this is a discrete XML element.
        /// The MacroAction.TextContent setter normalizes line endings to LF and
        /// strips C0 control characters before the value ever reaches this DTO,
        /// because XmlSerializer throws on C0 characters and the XML parser
        /// normalizes CR to LF on load (verified both ways).</summary>
        [XmlElement] public string TextContent { get; set; }
        /// <summary>Milliseconds between typed characters for a TextBlock
        /// action. 0 = the whole text in one batched call.</summary>
        [XmlElement] public int TextPerCharDelayMs { get; set; }
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
    /// Steam Workshop provenance stamped on a profile when it is imported
    /// through Browse Community Configs (#9). Update detection compares the
    /// stored <see cref="TimeUpdated"/> against a fresh
    /// GetPublishedFileDetails read to flag profiles whose Workshop item
    /// changed since import.
    /// </summary>
    public class SteamWorkshopSource
    {
        /// <summary>Workshop published-file id the profile was translated from.</summary>
        [XmlElement] public ulong PublishedFileId { get; set; }

        /// <summary>Steam app the config targets.</summary>
        [XmlElement] public int AppId { get; set; }

        /// <summary>Game name at import time, for update-result display.</summary>
        [XmlElement] public string GameName { get; set; }

        /// <summary>Workshop item title at import time.</summary>
        [XmlElement] public string Title { get; set; }

        /// <summary>The Workshop item's time_updated (unix seconds) at import time.</summary>
        [XmlElement] public long TimeUpdated { get; set; }

        /// <summary>UTC timestamp of the import.</summary>
        [XmlElement] public DateTime ImportedAt { get; set; }

        /// <summary>TranslationReport.ToSummaryString() digest of the import run.</summary>
        [XmlElement] public string TranslationSummary { get; set; }
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

        /// <summary>Custom touchpad gestures recorded by the user.
        /// Per-profile so different games can use different gesture
        /// catalogs. Each entry compiles to a
        /// <see cref="PadForge.Engine.Touchpad.ShapeTemplate"/> at
        /// profile load and joins the active in-box catalog the
        /// gesture engine evaluates against. Null on profiles captured
        /// before v3.3 — the in-box catalog still applies; just no
        /// custom gestures.</summary>
        [XmlArray("TouchpadGestures")]
        [XmlArrayItem("Gesture")]
        public PadForge.Engine.Touchpad.TouchpadCustomGesture[] TouchpadGestures { get; set; }

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

        /// <summary>Per-(slot, device) configurations (adaptive triggers,
        /// lighting, audio, tone filter) saved with this profile. Null on
        /// profiles predating v3.1.0, where defaults apply on load. Written as
        /// ProfileDeviceSlotConfigs since the v4.x schema rename; older
        /// files and exported profile.xml archives spelled it
        /// ProfilePlayStationConfigs (see
        /// <see cref="LegacyDeviceSlotConfigs"/>).</summary>
        [XmlArray("ProfileDeviceSlotConfigs")]
        [XmlArrayItem("Config")]
        public ViewModels.DeviceSlotConfigData[] DeviceSlotConfigs { get; set; }

        /// <summary>Read-only compatibility spelling for files and profile
        /// exports saved before the v4.x schema rename. Same contract as
        /// AppSettingsData.LegacyDeviceSlotConfigs: migrated forward on
        /// load, never serialized.</summary>
        [XmlArray("ProfilePlayStationConfigs")]
        [XmlArrayItem("PlayStationConfig")]
        public ViewModels.DeviceSlotConfigData[] LegacyDeviceSlotConfigs { get; set; }
        public bool ShouldSerializeLegacyDeviceSlotConfigs() => false;

        /// <summary>Adopts the legacy spelling when the new one is absent,
        /// then clears it. Called by SettingsFileData.MigrateLegacySchema
        /// on settings load and by ProfileTransfer.Import for standalone
        /// profile archives.</summary>
        public void MigrateLegacySchema()
        {
            if ((DeviceSlotConfigs == null || DeviceSlotConfigs.Length == 0)
                && LegacyDeviceSlotConfigs != null && LegacyDeviceSlotConfigs.Length > 0)
                DeviceSlotConfigs = LegacyDeviceSlotConfigs;
            LegacyDeviceSlotConfigs = null;
        }

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

        /// <summary>Per-slot KBM (SOCD) configurations saved with this profile.</summary>
        [XmlArray("ProfileKbmConfigs")]
        [XmlArrayItem("KbmConfig")]
        public ViewModels.KbmSlotConfigData[] KbmConfigs { get; set; }

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

        /// <summary>Radial / touch menu overlay (#9 B-17), per-profile
        /// copy. Default on.</summary>
        [XmlElement]
        public bool EnableMenuOverlay { get; set; } = true;

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

        /// <summary>Steam Workshop provenance for profiles imported via
        /// Browse Community Configs (#9). Null on every other profile.
        /// Identity-scoped like <see cref="Name"/> and <see cref="Id"/>:
        /// the runtime-state mirrors (InputService.SnapshotCurrentProfile,
        /// SaveActiveProfileState, SettingsService.UpdateActiveProfileSnapshot)
        /// intentionally leave it alone, because it describes where the
        /// profile came from, not what state it holds. Slot compaction
        /// (CompactProfileDataInPlace) mutates in place and never touches it.</summary>
        [XmlElement("SteamWorkshopSource")]
        public SteamWorkshopSource WorkshopSource { get; set; }
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
        ToggleWindow,
        ToggleVCsDisabled,
    }
}
