using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// What the problem recorder (#597) asks of the app around it: where the
    /// file goes, which devices there are, what this PC could give away, and
    /// the parts of the file that describe the system, the devices, the
    /// virtual controllers and the settings. This class answers from the
    /// running app. A test answers from a list of its own.
    ///
    /// <para>Every part is written through the scrubber's typed side: a
    /// device is D1, a controller C1, and a name the user typed, a path, a
    /// serial number or an address is a stand-in.</para>
    /// </summary>
    internal partial class RecorderHost
    {
        /// <summary>The engine whose slots, rate and macros the parts read,
        /// set by the service that owns it.</summary>
        internal static InputManager Engine;

        /// <summary>The service whose Remote Link values count among this
        /// PC's own.</summary>
        internal static InputService Input;

        /// <summary>The window's view model, which holds the access code and
        /// the chat channel.</summary>
        internal static MainViewModel Main;

        /// <summary>Test seam: stands in for the scrubber in the check at
        /// the start, to show what a failed check does.</summary>
        internal Func<string, string> CheckScrub;

        /// <summary>The signed-in user's own temp folder, where a recording
        /// is written as it runs and waits for its review (#600). Null when
        /// there is none to use.</summary>
        public virtual string Folder => PadForge.Common.Recording.RecordingFolder.Get();

        /// <summary>Whether the capture is the one the engine writes to and
        /// the diagnostics ring hands its lines to. A test's is neither.</summary>
        public virtual bool AttachesToEngine => true;

        public virtual PrivacyEnvironment Environment() => PrivacyEnvironment.Current();

        /// <summary>Diagnostics lines from before the start, for a host that
        /// is not attached to the diagnostics ring.</summary>
        public virtual List<(long Ticks, string Line)> History() => null;

        /// <summary>The devices of the Devices list, in its order.</summary>
        public virtual List<UserDevice> Devices()
        {
            var list = SettingsManager.UserDevices;
            if (list == null) return new List<UserDevice>();
            lock (list.SyncRoot) return new List<UserDevice>(list.Items);
        }

        // ─────────────────────────────────────────────
        //  This PC's own values
        // ─────────────────────────────────────────────

        /// <summary>Adds what this PC could give away beyond its devices and
        /// its user and PC names: the web controller's access code, the chat
        /// channel, Remote Link's keys, fingerprints and code, the addresses
        /// of its network adapters, and the names the user typed.</summary>
        public virtual void LiveValues(List<(PrivateKind Kind, string Value)> into)
        {
            var dashboard = Main?.Dashboard;
            if (dashboard != null)
            {
                into.Add((PrivateKind.AccessCode, dashboard.WebControllerAccessCode));
                into.Add((PrivateKind.Channel, dashboard.TwitchChatChannel));
                into.Add((PrivateKind.PairingCode, dashboard.RemoteLinkMyCode));
                into.Add((PrivateKind.HostName, dashboard.RemoteLinkConnectHost));
            }
            Input?.CollectPrivateValues(into);
            // Every microphone and speaker Windows lists. A Bluetooth
            // headset carries the name its owner gave it. Kept until Windows
            // reports a change (AudioMuteService.EndpointNames).
            foreach (string name in AudioMuteService.EndpointNames())
                into.Add((PrivateKind.AudioDevice, name));
            foreach (string host in Main?.Settings?.DjiRemoteHosts ?? (IEnumerable<string>)Array.Empty<string>())
                into.Add((PrivateKind.HostName, host));
            into.AddRange(Addresses.Current());
        }

        /// <summary>
        /// The addresses of this PC's network adapters, as the scrubber
        /// searches for them (#597). They are read once and kept until
        /// Windows reports an address changed (NetworkChange), since a read
        /// of every adapter took 68 ms at the median and up to 389 ms under a
        /// load of one busy process per core, on the line handler of every
        /// Automation read that refreshed the scrubber. A read that failed,
        /// or a report that could not be subscribed to, keeps nothing, so the
        /// next call reads again.
        /// </summary>
        internal static class Addresses
        {
            private static readonly object Sync = new object();
            private static List<(PrivateKind Kind, string Value)> _kept;
            private static bool _current;
            private static bool _watched;

            internal static IReadOnlyList<(PrivateKind Kind, string Value)> Current()
            {
                lock (Sync)
                {
                    if (_current && _kept != null) return _kept;
                }
                bool watched = Watch();
                lock (Sync)
                {
                    // Current from before the read: a change Windows reports
                    // during it marks the list stale again.
                    _current = watched;
                    _kept = null;
                }
                var read = Read(out bool ok);
                lock (Sync)
                {
                    if (!ok) _current = false;
                    else if (_current) _kept = read;
                }
                return read;
            }

            /// <summary>Whether a list is kept now, for tests.</summary>
            internal static bool Kept
            {
                get { lock (Sync) return _current && _kept != null; }
            }

            /// <summary>What Windows' report does, for tests too.</summary>
            internal static void Changed()
            {
                lock (Sync)
                {
                    _current = false;
                    _kept = null;
                }
            }

            private static bool Watch()
            {
                lock (Sync)
                {
                    if (_watched) return true;
                    try
                    {
                        NetworkChange.NetworkAddressChanged += (_, _) => Changed();
                        _watched = true;
                    }
                    catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or PlatformNotSupportedException)
                    {
                        return false;
                    }
                    return true;
                }
            }

            private static List<(PrivateKind Kind, string Value)> Read(out bool ok)
            {
                var list = new List<(PrivateKind Kind, string Value)>();
                try
                {
                    foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                        {
                            var ip = address.Address;
                            if (System.Net.IPAddress.IsLoopback(ip)) continue;
                            // An IPv6 address is written without its zone.
                            string text = ip.ToString();
                            int zone = text.IndexOf('%');
                            if (zone > 0) text = text.Substring(0, zone);
                            list.Add((PrivacyScrubber.IsLocal(ip) ? PrivateKind.LanAddress : PrivateKind.PublicAddress, text));
                        }
                    }
                    ok = true;
                }
                catch (NetworkInformationException)
                {
                    // No adapter list, no addresses to search for. The shape
                    // of an address is still found by the scrubber's patterns.
                    ok = false;
                }
                return list;
            }
        }

        /// <summary>Tells a scrubber the names the user typed, for a file
        /// other than a recording that is scrubbed the same way: a
        /// snapshot a program asked for (#597).</summary>
        internal void KnowTypedNames(PrivacyScrubber scrubber) => KnowNames(scrubber);

        /// <summary>Tells a scrubber the names the user typed, so a line of
        /// free text that holds one is scrubbed like the settings part.</summary>
        protected virtual void KnowNames(PrivacyScrubber scrubber)
        {
            foreach (var profile in Snapshot(SettingsManager.Profiles))
                scrubber.Know(PrivateKind.ProfileName, profile?.Name);
            foreach (var tag in NfcTagRegistry.Tags)
            {
                scrubber.Know(PrivateKind.NfcTagName, tag.Name);
                scrubber.Know(PrivateKind.Key, tag.Uid);
            }
            foreach (var phrase in VoicePhraseRegistry.Phrases)
            {
                scrubber.Know(PrivateKind.VoicePhrase, phrase.Phrase);
                scrubber.Know(PrivateKind.VoicePhrase, phrase.Name);
            }
            foreach (var word in PadForge.Common.Input.Twitch.ChatCommandRegistry.Words)
                scrubber.Know(PrivateKind.ChatWord, word.Word);
        }

        private static List<T> Snapshot<T>(List<T> list)
        {
            if (list == null) return new List<T>();
            // The UI thread changes these lists. A copy torn by a change is
            // taken again.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { return new List<T>(list); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            return new List<T>();
        }

        // ─────────────────────────────────────────────
        //  The system part
        // ─────────────────────────────────────────────

        /// <summary>The controller programs a problem often involves, by the
        /// names their processes run under: the tools PadForge imports from
        /// and the ones its own pages name. A process counts when its image
        /// name starts with one of a program's.</summary>
        internal static readonly IReadOnlyList<VendorProgram> ControllerPrograms = new[]
        {
            new VendorProgram("DS4Windows", "DS4Windows"),
            new VendorProgram("DSX", "DSX", "DualSenseX"),
            new VendorProgram("reWASD", "reWASD"),
            new VendorProgram("Steam", "steam") { WholeNames = true },
            new VendorProgram("HidHide Configuration Client", "HidHideClient"),
            new VendorProgram("DsHidMini Control", "DSHMC"),
            new VendorProgram("ScpToolkit", "ScpServer", "ScpService", "ScpMonitor"),
            new VendorProgram("vJoy", "vJoyConf", "vJoyFeeder", "vJoyList"),
            new VendorProgram("JoyShockMapper", "JoyShockMapper"),
            new VendorProgram("AntiMicroX", "antimicrox"),
            new VendorProgram("XOutput", "XOutput"),
            new VendorProgram("Handheld Companion", "HandheldCompanion"),
            new VendorProgram("Sunshine", "sunshine") { WholeNames = true },
            // More mappers a problem can come from (#600).
            new VendorProgram("JoyToKey", "JoyToKey"),
            new VendorProgram("InputMapper", "InputMapper"),
            new VendorProgram("BetterJoy", "BetterJoy"),
            new VendorProgram("Xpadder", "Xpadder"),
            new VendorProgram("Gopher360", "Gopher"),
            new VendorProgram("JoyXoff", "JoyXoff"),
            new VendorProgram("DS4Tool", "DS4Tool"),
        };

        /// <summary>Test seam: the image names of the running processes,
        /// without ".exe", or null for the real list.</summary>
        internal static Func<IReadOnlyCollection<string>> RunningImages;

        /// <summary>The names of the programs of <paramref name="known"/>
        /// that run, from one read of the process list.</summary>
        internal static List<string> RunningOf(IReadOnlyCollection<string> images, IReadOnlyList<VendorProgram> known)
        {
            var names = new List<string>();
            foreach (var program in RunningPrograms.Of(images, known).Programs) names.Add(program.Name);
            return names;
        }

        private static IReadOnlyCollection<string> Images()
        {
            var seam = RunningImages;
            if (seam != null) return seam();
            var running = RunningProcessList.Read();
            var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (running == null) return images;
            foreach (var process in running)
            {
                string name = process.Name;
                if (!string.IsNullOrEmpty(name)) images.Add(name);
            }
            return images;
        }

        public virtual void SystemLines(PrivacyScrubber scrubber, List<string> into)
        {
            KnowNames(scrubber);
            var version = typeof(RecorderHost).Assembly.GetName().Version;
            into.Add("PadForge " + (version == null ? "?" : version.ToString()) + ", .NET " + System.Environment.Version
                + ", " + (System.Environment.Is64BitProcess ? "64-bit" : "32-bit") + " " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            into.Add("Windows " + System.Environment.OSVersion.Version + ", " + System.Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) + " processors");

            var engine = Engine;
            if (engine == null) into.Add("Engine: none");
            else
                into.Add("Engine: " + (engine.IsRunning ? "running" : "stopped") + ", " + engine.PollingRateHz.ToString(CultureInfo.InvariantCulture)
                    + " passes a second set, " + engine.CurrentFrequency.ToString("F0", CultureInfo.InvariantCulture) + " measured"
                    + (engine.IsIdle ? ", idle" : string.Empty));
            FactLines(into);

            var images = Images();
            var controllers = RunningOf(images, ControllerPrograms);
            var vendors = RunningOf(images, VendorPrograms.All);
            into.Add("Other controller programs running: " + (controllers.Count == 0 ? "none of the " + ControllerPrograms.Count.ToString(CultureInfo.InvariantCulture) + " looked for" : string.Join(", ", controllers)));
            into.Add("Device makers' programs running: " + (vendors.Count == 0 ? "none of the " + VendorPrograms.All.Count.ToString(CultureInfo.InvariantCulture) + " looked for" : string.Join(", ", vendors)));

            // The driver and HidHide state a snapshot ends with, scrubbed:
            // its lines name device instances.
            foreach (string line in DiagnosticsLogControl.SnapshotStateLines())
                into.Add(scrubber.Text(line));
        }

        // ─────────────────────────────────────────────
        //  The devices part
        // ─────────────────────────────────────────────

        private static readonly Dictionary<int, string> TypeWords = BuildTypeWords();

        private static Dictionary<int, string> BuildTypeWords()
        {
            var words = new Dictionary<int, string>();
            foreach (var field in typeof(InputDeviceType).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.IsLiteral && field.GetRawConstantValue() is int value) words[value] = field.Name;
            return words;
        }

        /// <summary>A device type's name, or its number for one with none.</summary>
        internal static string TypeWord(int type)
            => TypeWords.TryGetValue(type, out string word) ? word : type.ToString(CultureInfo.InvariantCulture);

        /// <summary>The bus an SDL GUID's first two bytes name.</summary>
        internal static string BusOf(string sdlGuid)
        {
            if (string.IsNullOrEmpty(sdlGuid) || sdlGuid.Length < 4) return "unknown";
            return sdlGuid.Substring(0, 4).ToLowerInvariant() switch
            {
                "0300" => "usb",
                "0500" => "bluetooth",
                "0600" => "virtual",
                "0000" => "none",
                var other => other,
            };
        }

        /// <summary>
        /// A device's line in the devices part without its name: what a
        /// stand-in device needs to be it again. The product name stays, as
        /// the firmware gave it. The name the user typed, the path's
        /// instance and the serial number are stand-ins.
        /// </summary>
        public virtual string DescribeDevice(PrivacyScrubber scrubber, UserDevice device, string alias)
        {
            var sb = new StringBuilder(256);
            string name = !string.IsNullOrEmpty(device.ProductName) ? device.ProductName : device.InstanceName;
            RecordingCodec.AppendFact(sb, "name", scrubber.Text(name ?? string.Empty));
            if (!string.IsNullOrEmpty(device.DisplayName))
                RecordingCodec.AppendFact(sb, "label", scrubber.Alias(PrivateKind.DeviceName, device.DisplayName));
            RecordingCodec.AppendFact(sb, "vid", device.VendorId.ToString("X4", CultureInfo.InvariantCulture));
            RecordingCodec.AppendFact(sb, "pid", device.ProdId.ToString("X4", CultureInfo.InvariantCulture));
            RecordingCodec.AppendFact(sb, "type", TypeWord(device.CapType));
            RecordingCodec.AppendFact(sb, "controls", PadForge.Common.Recording.RecordingKinds.Word(PadForge.Common.Recording.RecordingKinds.Of(device)));
            RecordingCodec.AppendFact(sb, "bus", BusOf(device.SdlGuid));
            string guid = device.SdlGuid ?? string.Empty;
            RecordingCodec.AppendFact(sb, "guid", guid.Length == 0 || PrivacyScrubber.IsSdlGuid(guid) ? guid : scrubber.Text(guid));

            int sliders = 0;
            var objects = device.DeviceObjects;
            if (objects != null)
                foreach (var item in objects)
                    if (item != null && item.ObjectTypeGuid == ObjectGuid.Slider) sliders++;
            RecordingCodec.AppendFact(sb, "axes", device.CapAxeCount);
            RecordingCodec.AppendFact(sb, "sliders", sliders);
            RecordingCodec.AppendFact(sb, "hats", device.CapPovCount);
            RecordingCodec.AppendFact(sb, "buttons", device.CapButtonCount);
            RecordingCodec.AppendFact(sb, "rawaxes", device.RawAxisCount);
            RecordingCodec.AppendFact(sb, "rawbuttons", device.RawButtonCount);
            RecordingCodec.AppendFact(sb, "gyro", device.HasGyro ? 1 : 0);
            RecordingCodec.AppendFact(sb, "accel", device.HasAccel ? 1 : 0);
            RecordingCodec.AppendFact(sb, "gyroaux", device.HasGyroAux ? 1 : 0);
            RecordingCodec.AppendFact(sb, "accelaux", device.HasAccelAux ? 1 : 0);
            RecordingCodec.AppendFact(sb, "touchpads", device.CapTouchpadCount);
            var fingers = device.CapTouchpadFingerCounts;
            if (fingers != null && fingers.Length > 0)
                RecordingCodec.AppendFact(sb, "fingers", string.Join(",", fingers));

            var live = device.Device;
            RecordingCodec.AppendFact(sb, "rumble", device.HasForceFeedback || device.HasPeripheralHaptics ? 1 : 0);
            RecordingCodec.AppendFact(sb, "triggers", device.HasRumbleTriggers ? 1 : 0);
            RecordingCodec.AppendFact(sb, "gamepad", live != null && live.GamepadHandle != IntPtr.Zero ? 1 : 0);
            RecordingCodec.AppendFact(sb, "raw", device.ForceRawJoystickMode ? 1 : 0);
            RecordingCodec.AppendFact(sb, "online", device.IsOnline ? 1 : 0);
            RecordingCodec.AppendFact(sb, "enabled", device.IsEnabled ? 1 : 0);
            RecordingCodec.AppendFact(sb, "hidden", device.HidHideEnabled ? 1 : 0);
            RecordingCodec.AppendFact(sb, "only", device.ConsumeInputEnabled ? 1 : 0);
            AppendDeviceFacts(sb, device);
            if (!string.IsNullOrEmpty(device.DevicePath))
                RecordingCodec.AppendFact(sb, "path", scrubber.DevicePath(device.DevicePath));
            if (!string.IsNullOrWhiteSpace(device.SerialNumber))
                RecordingCodec.AppendFact(sb, "serial", scrubber.Serial(device.SerialNumber, alias));

            string mapping = MappingOf(live);
            if (!string.IsNullOrEmpty(mapping)) RecordingCodec.AppendFact(sb, "mapping", scrubber.Text(mapping));
            return sb.ToString();
        }

        /// <summary>
        /// A device's line when the file leaves out the settings and devices
        /// part (#600): its type, how its controls are named and how many
        /// there are, which a replay needs to stand the device up again. No
        /// name, no IDs, no path.
        /// </summary>
        internal static string DescribeShape(UserDevice device)
        {
            var sb = new StringBuilder(96);
            RecordingCodec.AppendFact(sb, "type", TypeWord(device.CapType));
            RecordingCodec.AppendFact(sb, "controls", PadForge.Common.Recording.RecordingKinds.Word(PadForge.Common.Recording.RecordingKinds.Of(device)));
            RecordingCodec.AppendFact(sb, "axes", device.CapAxeCount);
            RecordingCodec.AppendFact(sb, "hats", device.CapPovCount);
            RecordingCodec.AppendFact(sb, "buttons", device.CapButtonCount);
            RecordingCodec.AppendFact(sb, "touchpads", device.CapTouchpadCount);
            RecordingCodec.AppendFact(sb, "gyro", device.HasGyro ? 1 : 0);
            RecordingCodec.AppendFact(sb, "accel", device.HasAccel ? 1 : 0);
            return sb.ToString();
        }

        /// <summary>The SDL mapping a device opened as a gamepad is read
        /// through, or nothing. SDL checks the handle under its own lock, so
        /// one the poll thread closed meanwhile reads as none.</summary>
        protected virtual string MappingOf(ISdlInputDevice device)
        {
            if (device == null || device.GamepadHandle == IntPtr.Zero || device is not SdlDeviceWrapper) return null;
            try { return SDL3.SDL.GetGamepadMapping(device.GamepadHandle); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or AccessViolationException)
            {
                return null;
            }
        }

        // ─────────────────────────────────────────────
        //  The virtual controllers and the settings
        // ─────────────────────────────────────────────

        public virtual void ControllerLines(PrivacyScrubber scrubber, List<string> into)
        {
            var engine = Engine;
            var created = SettingsManager.SlotCreated;
            var enabled = SettingsManager.SlotEnabled;
            for (int slot = 0; slot < InputManager.MaxPads; slot++)
            {
                if (created == null || !created[slot]) continue;
                var sb = new StringBuilder(96).Append(PrivacyScrubber.ControllerAlias(slot));
                RecordingCodec.AppendFact(sb, "type", engine == null ? "unknown" : engine.SlotControllerTypes[slot].ToString());
                RecordingCodec.AppendFact(sb, "enabled", enabled != null && enabled[slot] ? 1 : 0);
                if (engine != null)
                {
                    RecordingCodec.AppendFact(sb, "preset", engine.SlotProfileIds[slot] ?? string.Empty);
                    RecordingCodec.AppendFact(sb, "connected", engine.IsVirtualControllerConnected(slot) ? 1 : 0);
                }
                into.Add(sb.ToString());
            }
            if (into.Count == 0) into.Add("none");
        }

        private static readonly Regex ButtonWord = new(@"\bButton (\d{1,3})\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public virtual void SettingsLines(PrivacyScrubber scrubber, List<string> into)
        {
            var profiles = Snapshot(SettingsManager.Profiles);
            string activeId = SettingsManager.ActiveProfileId;
            string active = "default";
            foreach (var profile in profiles)
                if (profile != null && profile.Id == activeId) active = scrubber.Alias(PrivateKind.ProfileName, profile.Name);
            {
                var sb = new StringBuilder("profile");
                RecordingCodec.AppendFact(sb, "active", active);
                RecordingCodec.AppendFact(sb, "profiles", profiles.Count);
                RecordingCodec.AppendFact(sb, "autoswitch", SettingsManager.EnableAutoProfileSwitching ? 1 : 0);
                into.Add(sb.ToString());
            }

            // Which device feeds which virtual controller.
            var settings = SettingsManager.UserSettings;
            if (settings != null)
            {
                List<UserSetting> assigned;
                lock (settings.SyncRoot) assigned = new List<UserSetting>(settings.Items);
                foreach (var setting in assigned)
                {
                    if (setting == null || setting.MapTo < 0 || setting.MapTo >= InputManager.MaxPads) continue;
                    var sb = new StringBuilder("assign");
                    RecordingCodec.AppendFact(sb, "device", scrubber.DeviceAlias(setting.InstanceGuid.ToString()));
                    RecordingCodec.AppendFact(sb, "controller", PrivacyScrubber.ControllerAlias(setting.MapTo));
                    into.Add(sb.ToString());
                }
            }

            var created = SettingsManager.SlotCreated;
            var sets = SettingsManager.SlotMappingSets;
            var engine = Engine;
            for (int slot = 0; slot < InputManager.MaxPads; slot++)
            {
                if (created == null || !created[slot]) continue;
                string controller = PrivacyScrubber.ControllerAlias(slot);
                var set = sets != null && slot < sets.Length ? sets[slot] : null;
                if (set != null)
                {
                    foreach (var layer in Snapshot(set.ShiftActivators))
                    {
                        if (layer == null) continue;
                        var sb = new StringBuilder("layer");
                        RecordingCodec.AppendFact(sb, "controller", controller);
                        RecordingCodec.AppendFact(sb, "name", scrubber.Alias(PrivateKind.LayerName, layer.LayerName));
                        RecordingCodec.AppendFact(sb, "mask", layer.LayerMask ?? string.Empty);
                        RecordingCodec.AppendFact(sb, "mode", layer.Mode ?? string.Empty);
                        RecordingCodec.AppendFact(sb, "input", scrubber.Text(layer.Descriptor ?? string.Empty));
                        into.Add(sb.ToString());
                    }
                    foreach (var row in Snapshot(set.Rows))
                    {
                        if (row == null) continue;
                        var sources = Snapshot(row.Sources);
                        var sb = new StringBuilder("row");
                        RecordingCodec.AppendFact(sb, "controller", controller);
                        RecordingCodec.AppendFact(sb, "target", row.Target ?? string.Empty);
                        RecordingCodec.AppendFact(sb, "layer", string.IsNullOrEmpty(row.LayerMask) ? "Base" : row.LayerMask);
                        string combine = !string.IsNullOrEmpty(row.CombineExpression) ? "formula" : row.CombineMode ?? string.Empty;
                        if (combine.Length > 0) RecordingCodec.AppendFact(sb, "combine", combine);
                        RecordingCodec.AppendFact(sb, "sources", sources.Count);
                        for (int i = 0; i < sources.Count; i++)
                        {
                            var source = sources[i];
                            if (source == null) continue;
                            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
                            RecordingCodec.AppendFact(sb, "src" + n, string.IsNullOrEmpty(source.DeviceGuid) ? "any" : scrubber.DeviceAlias(source.DeviceGuid));
                            RecordingCodec.AppendFact(sb, "in" + n, scrubber.Text(source.Descriptor ?? string.Empty));
                            RecordingCodec.AppendFact(sb, "kind" + n, source.Kind ?? string.Empty);
                            if (source.Invert) RecordingCodec.AppendFact(sb, "invert" + n, 1);
                            if (source.HalfAxis) RecordingCodec.AppendFact(sb, "half" + n, 1);
                            RecordingCodec.AppendFact(sb, "dz" + n, source.DeadZone);
                            AppendSourceTuning(sb, source, n, scrubber);
                        }
                        into.Add(sb.ToString());
                    }
                }

                var macros = engine?.MacroSnapshots[slot];
                if (macros == null) continue;
                foreach (var macro in macros)
                {
                    if (macro == null) continue;
                    var sb = new StringBuilder("macro");
                    RecordingCodec.AppendFact(sb, "controller", controller);
                    RecordingCodec.AppendFact(sb, "name", scrubber.Alias(PrivateKind.MacroName, macro.Name));
                    RecordingCodec.AppendFact(sb, "enabled", macro.IsEnabled ? 1 : 0);
                    RecordingCodec.AppendFact(sb, "trigger", macro.TriggerMode.ToString());
                    RecordingCodec.AppendFact(sb, "source", macro.TriggerSource.ToString());
                    RecordingCodec.AppendFact(sb, "does", MacroSteps(scrubber, macro));
                    into.Add(sb.ToString());
                }
            }

            var shortcuts = SettingsManager.GlobalMacros;
            if (shortcuts != null) into.Add("shortcuts count=" + shortcuts.Length.ToString(CultureInfo.InvariantCulture));
            MoreSettingsLines(scrubber, into);
        }

        /// <summary>A macro's steps as their kinds. A step that types text
        /// says how long the text is, and one that runs a program gives the
        /// program's file name and a stand-in for its arguments.</summary>
        private static string MacroSteps(PrivacyScrubber scrubber, MacroItem macro)
        {
            var sb = new StringBuilder(64);
            foreach (var action in Snapshot(new List<MacroAction>(macro.Actions)))
            {
                if (action == null) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(action.Type.ToString());
                if (!string.IsNullOrEmpty(action.TextContent)) sb.Append('(').Append(PrivacyScrubber.TextLength(action.TextContent)).Append(')');
                if (!string.IsNullOrEmpty(action.ProgramPath))
                {
                    sb.Append('(').Append(scrubber.FilePath(action.ProgramPath));
                    if (!string.IsNullOrEmpty(action.ProgramArgs)) sb.Append(' ').Append(PrivacyScrubber.ArgumentsStandIn);
                    sb.Append(')');
                }
            }
            return sb.ToString();
        }

        // ─────────────────────────────────────────────
        //  The keys a mapping or a macro reads
        // ─────────────────────────────────────────────

        /// <summary>
        /// The key indexes of a keyboard that a mapping row, a layer's
        /// input, a menu or a macro names, as 256 bits. With the switch that
        /// hides typed letters and numbers on, the keys set here are still
        /// written as pressed.
        /// </summary>
        public virtual ulong[] KeysRead(UserDevice keyboard)
        {
            var bits = new ulong[4];
            string guid = keyboard.InstanceGuidString;
            var sets = SettingsManager.SlotMappingSets;
            if (sets != null)
                foreach (var set in sets)
                    if (set != null) KeyUse.Collect(set, guid, bits);

            var engine = Engine;
            if (engine != null)
            {
                foreach (var macros in engine.MacroSnapshots)
                {
                    if (macros == null) continue;
                    foreach (var macro in macros)
                    {
                        if (macro == null) continue;
                        if (macro.TriggerDeviceGuid == keyboard.InstanceGuid)
                            foreach (int button in macro.TriggerRawButtons ?? Array.Empty<int>()) KeyUse.Set(bits, button);
                        foreach (var entry in macro.GetTriggerInputEntries())
                            if (entry != null && entry.DeviceGuid == keyboard.InstanceGuid) KeyUse.Set(bits, entry.RawButton);
                        foreach (var variable in Snapshot(new List<MacroExpressionVariable>(macro.TriggerExpressionVariables)))
                            if (variable != null) KeyUse.Collect(variable, guid, bits);
                    }
                }
            }
            return bits;
        }

        /// <summary>Walks settings objects for the inputs they name on one
        /// device: every text that says "Button N" beside that device's GUID,
        /// or beside no GUID at all, which means any device.</summary>
        internal static class KeyUse
        {
            private static readonly Dictionary<Type, PropertyInfo[]> Properties = new();

            internal static void Set(ulong[] bits, int index)
            {
                if ((uint)index < 256) bits[index >> 6] |= 1UL << (index & 63);
            }

            internal static void Collect(object root, string deviceGuid, ulong[] bits)
                => Collect(root, deviceGuid, bits, 0);

            private static PropertyInfo[] PropertiesOf(Type type)
            {
                lock (Properties)
                {
                    if (Properties.TryGetValue(type, out var known)) return known;
                    var list = new List<PropertyInfo>();
                    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                        var t = property.PropertyType;
                        bool data = t == typeof(string) || t == typeof(Guid)
                            || (typeof(IEnumerable).IsAssignableFrom(t) && t != typeof(string))
                            || (t.IsClass && t.Namespace != null && t.Namespace.StartsWith("PadForge.Engine", StringComparison.Ordinal));
                        if (data) list.Add(property);
                    }
                    return Properties[type] = list.ToArray();
                }
            }

            private static void Collect(object node, string deviceGuid, ulong[] bits, int depth)
            {
                if (node == null || depth > 6) return;
                if (node is IEnumerable items and not string)
                {
                    try
                    {
                        foreach (object item in items)
                            if (item != null && item is not string && !item.GetType().IsPrimitive) Collect(item, deviceGuid, bits, depth + 1);
                    }
                    catch (InvalidOperationException) { }
                    return;
                }

                bool namesDevice = false, namesOther = false;
                List<string> texts = null;
                foreach (var property in PropertiesOf(node.GetType()))
                {
                    object value;
                    try { value = property.GetValue(node); }
                    catch (TargetInvocationException) { continue; }
                    if (value == null) continue;
                    if (value is Guid g)
                    {
                        if (g == Guid.Empty) continue;
                        if (string.Equals(g.ToString(), deviceGuid, StringComparison.OrdinalIgnoreCase)) namesDevice = true;
                        else namesOther = true;
                    }
                    else if (value is string text)
                    {
                        if (text.Length == 0) continue;
                        if (property.Name.EndsWith("DeviceGuid", StringComparison.Ordinal))
                        {
                            if (string.Equals(text, deviceGuid, StringComparison.OrdinalIgnoreCase)) namesDevice = true;
                            else namesOther = true;
                        }
                        else (texts ??= new List<string>()).Add(text);
                    }
                    else Collect(value, deviceGuid, bits, depth + 1);
                }
                // An input beside another device's GUID alone is that
                // device's. Beside this one's, or beside none, it counts.
                if (texts == null || (namesOther && !namesDevice)) return;
                foreach (string text in texts)
                    foreach (Match match in ButtonWord.Matches(text))
                        if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) Set(bits, index);
            }
        }

        // ─────────────────────────────────────────────
        //  The program in front
        // ─────────────────────────────────────────────

        /// <summary>The program in front: its path, whether a profile rule
        /// names it, and that profile's name. False when it cannot be read.
        /// Window titles are never read.</summary>
        public virtual bool Front(out string path, out bool named, out string profile)
        {
            var (exePath, profileId) = ForegroundMonitorService.ReadInFront();
            path = exePath;
            named = profileId != null;
            profile = null;
            if (named)
                foreach (var candidate in Snapshot(SettingsManager.Profiles))
                    if (candidate != null && candidate.Id == profileId) profile = candidate.Name;
            return true;
        }
    }
}
