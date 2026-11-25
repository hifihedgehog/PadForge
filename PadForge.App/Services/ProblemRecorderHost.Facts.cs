using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using PadForge.Common.Input;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// What a recording says of the PC and of PadForge's settings beyond its
    /// first build (#600): the Windows build, version and edition, the
    /// display scale, the drivers' versions, the XInput library, which
    /// readers started, whether the virtual controllers are on, every app
    /// setting and the profile's own, every tuning value of a mapping row,
    /// how a device is held, and the force-feedback driver of a device. And
    /// what the recorder's watch asks: whether the engine runs, which
    /// devices Raw Input lists, which controller programs run.
    /// </summary>
    internal partial class RecorderHost
    {
        /// <summary>The app settings a recording writes: each simple setting
        /// PadForge.xml holds (AppSettingsData), but text whose name says it
        /// holds something private. Read once.</summary>
        internal static IReadOnlyList<string> AppSettingNames => _appSettingNames ??= BuildAppSettingNames();
        private static IReadOnlyList<string> _appSettingNames;

        private static IReadOnlyList<string> BuildAppSettingNames()
        {
            var names = new List<string>();
            foreach (var p in SimpleOf(typeof(AppSettingsData))) names.Add(p.Name);
            return names;
        }

        /// <summary>The app settings whose holder gives them a name of its
        /// own, or keeps them outside the Settings page's view model and the
        /// Dashboard's: the theme, the language, Remote Link's reconnect, the
        /// chat's claim approval, voice macros, the OpenXR runtime and the
        /// web controller's layouts built in the browser, read where
        /// SettingsService reads them to save PadForge.xml.</summary>
        private static readonly Dictionary<string, Func<MainViewModel, object>> Renamed = new(StringComparer.Ordinal)
        {
            ["ThemeIndex"] = m => m.Settings.SelectedThemeIndex,
            ["ThemeId"] = m => m.Settings.SelectedThemeId,
            ["Language"] = m => m.Settings.LanguageCode,
            ["RemoteLinkAutoReconnect"] = m => m.Dashboard.AutoReconnect,
            ["TwitchChatClaimApproval"] = m => m.Dashboard.TwitchChatClaimApprovalIndex,
            ["VoiceMacrosEnabled"] = _ => VoiceMacroService.Enabled,
            ["VoiceMinConfidence"] = _ => VoiceMacroService.MinConfidence,
            ["VoiceListeningMode"] = _ => VoiceMacroService.ListeningMode,
            ["HeadTrackingOpenXrRuntime"] = _ => HeadTrackingRuntime.OpenXrRuntimeManifest,
            ["WebCustomLayoutsJson"] = _ => WebLayoutsSeam?.Invoke() ?? WebCustomLayoutStore.Json,
        };

        /// <summary>Test seam: the web controller's layouts as their store
        /// holds them. Null reads the store.</summary>
        internal static Func<string> WebLayoutsSeam;

        /// <summary>Where each app setting is read while PadForge runs: a
        /// holder of <see cref="Renamed"/>, else the Settings page's view
        /// model by the same name, else the Dashboard's. A setting no running
        /// holder keeps has none: one read once from an older file, or one
        /// written fixed.</summary>
        private static IReadOnlyList<(string Name, Func<MainViewModel, object> Read)> AppHolders => _appHolders ??= BuildAppHolders();
        private static IReadOnlyList<(string Name, Func<MainViewModel, object> Read)> _appHolders;

        private static IReadOnlyList<(string Name, Func<MainViewModel, object> Read)> BuildAppHolders()
        {
            var holders = new List<(string, Func<MainViewModel, object>)>();
            foreach (string name in AppSettingNames)
            {
                if (Renamed.TryGetValue(name, out var renamed)) holders.Add((name, renamed));
                else if (PropertyOf(typeof(SettingsViewModel), name) is { } setting) holders.Add((name, m => setting.GetValue(m.Settings)));
                else if (PropertyOf(typeof(DashboardViewModel), name) is { } dashboard) holders.Add((name, m => dashboard.GetValue(m.Dashboard)));
            }
            return holders;
        }

        private static PropertyInfo PropertyOf(Type type, string name)
        {
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p != null && p.CanRead && p.GetIndexParameters().Length == 0 ? p : null;
        }

        /// <summary>The app settings as they stand now, by name, from the
        /// view models and services that hold them: no settings file is
        /// built for it, so a look costs reads alone.</summary>
        internal static IEnumerable<(string Name, string Value)> AppValues()
        {
            var main = Main;
            if (main?.Settings == null || main.Dashboard == null) yield break;
            foreach (var (name, read) in AppHolders)
            {
                object value;
                try { value = read(main); }
                catch (TargetInvocationException) { continue; }
                yield return (name, TextOf(value));
            }
        }

        /// <summary>A setting's value as text, whole: a switch as 1 or 0,
        /// a number in invariant form.</summary>
        private static string TextOf(object value) => value switch
        {
            null => string.Empty,
            bool b => b ? "1" : "0",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        /// <summary>A value as a settings line writes it: scrubbed whole, then
        /// cut at 100 characters. Cut first, a path could end inside a
        /// folder's name, which the scrubber would keep as the file's.</summary>
        internal static string Written(PrivacyScrubber scrubber, string value)
        {
            string text = scrubber.Text(value ?? string.Empty);
            return text.Length > 100 ? text.Substring(0, 100) : text;
        }

        /// <summary>Test seam: the Windows build, its update revision, its
        /// version name and its edition. Null reads the registry.</summary>
        internal static Func<(string Build, string Revision, string Version, string Edition)> WindowsSeam;

        // ─────────────────────────────────────────────
        //  The PC
        // ─────────────────────────────────────────────

        /// <summary>The Windows build, its version name and edition, read from
        /// the registry, where they are not altered for compatibility, and
        /// the display scale.</summary>
        internal static string WindowsLine()
        {
            var (build, revision, version, edition) = WindowsSeam?.Invoke() ?? ReadWindows();
            var sb = new StringBuilder("Windows build ");
            sb.Append(string.IsNullOrEmpty(build) ? System.Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture) : build);
            if (!string.IsNullOrEmpty(revision)) sb.Append('.').Append(revision);
            if (!string.IsNullOrEmpty(version)) sb.Append(", version ").Append(version);
            if (!string.IsNullOrEmpty(edition)) sb.Append(", ").Append(edition);
            sb.Append(", ").Append(System.Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)).Append(" processors");
            if (WindowWatch.DisplayScale > 0)
                sb.Append(", display at ").Append(WindowWatch.DisplayScale.ToString(CultureInfo.InvariantCulture)).Append(" %");
            return sb.ToString();
        }

        private static (string, string, string, string) ReadWindows()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key == null) return default;
                return (key.GetValue("CurrentBuild") as string, key.GetValue("UBR")?.ToString(),
                    key.GetValue("DisplayVersion") as string, key.GetValue("EditionID") as string);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                return default;
            }
        }

        /// <summary>The time zone the file's local times are in.</summary>
        internal static string TimeZoneLine()
        {
            var zone = TimeZoneInfo.Local;
            var offset = zone.GetUtcOffset(DateTime.Now);
            return "Time zone: " + (zone.IsDaylightSavingTime(DateTime.Now) ? zone.DaylightName : zone.StandardName) + ", UTC"
                + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().Hours.ToString("00", CultureInfo.InvariantCulture)
                + ":" + offset.Duration().Minutes.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>The lines of the system part beyond the first build's:
        /// Windows, the time zone, the drivers, the XInput library, the
        /// readers and the virtual controllers' switch.</summary>
        public virtual void FactLines(List<string> into)
        {
            into.Add(WindowsLine());
            into.Add(TimeZoneLine());
            var settings = Main?.Settings;
            into.Add("Virtual controller driver: " + (settings == null ? "not known"
                : settings.IsHIDMaestroInstalled ? "HIDMaestro " + (settings.HIDMaestroVersion ?? "of no known version") : "HIDMaestro not installed"));
            into.Add("HidHide: " + (settings == null ? "not known"
                : settings.IsHidHideInstalled ? settings.HidHideVersion ?? "of no known version" : "not installed"));
            into.Add(XInputReadBack.LibraryLine());
            into.Add("Readers: SDL, and Raw Input " + RawInputListener.Status);
            var engine = Engine;
            into.Add("Virtual controllers: " + (engine == null ? "not known" : engine.VirtualControllersEnabled ? "on" : "off"));
        }

        // ─────────────────────────────────────────────
        //  Devices
        // ─────────────────────────────────────────────

        /// <summary>How a device is held: hidden by HidHide, exclusive to
        /// PadForge (Use Only in PadForge), or shared with every program.</summary>
        internal static string HeldWord(UserDevice device)
            => device.HidHideEnabled ? "hidden" : device.ConsumeInputEnabled ? "exclusive" : "shared";

        /// <summary>The XInput place a device read through XInput holds,
        /// counted from 1, or 0.</summary>
        internal static int XInputPlace(UserDevice device)
            => XInputSlotIdentity.TryParseSlot(device.DevicePath, out int slot) ? slot + 1 : 0;

        /// <summary>Test seam: a force-feedback device's driver file by its
        /// vendor and product. Null reads the registry.</summary>
        internal static Func<ushort, ushort, string> ForceDriverSeam;

        /// <summary>
        /// The file of a device's force-feedback driver, as DirectInput finds
        /// it: the OEM key of its vendor and product names a COM class, and
        /// the class names the file. Only the file's name. Null when the
        /// device has none of its own, as most pads read through SDL.
        /// </summary>
        internal static string ForceDriver(ushort vendor, ushort product)
        {
            var seam = ForceDriverSeam;
            if (seam != null) return seam(vendor, product);
            string oem = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_"
                + vendor.ToString("X4", CultureInfo.InvariantCulture) + "&PID_" + product.ToString("X4", CultureInfo.InvariantCulture) + @"\OEMForceFeedback";
            try
            {
                foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                {
                    using var key = root.OpenSubKey(oem);
                    if (key?.GetValue("CLSID") is not string clsid || clsid.Length == 0) continue;
                    using var server = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid + @"\InprocServer32");
                    if (server?.GetValue(null) is string file && file.Length > 0) return System.IO.Path.GetFileName(file.Trim('"'));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException or ArgumentException) { }
            return null;
        }

        /// <summary>A device's motor axes, as SDL counts them, or -1.</summary>
        protected virtual int MotorAxes(ISdlInputDevice device)
        {
            if (device == null || device.HapticHandle == IntPtr.Zero) return -1;
            try { return SDL3.SDL.SDL_GetNumHapticAxes(device.HapticHandle); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return -1; }
        }

        /// <summary>Whether a device has motors SDL drives through a haptic
        /// device, the ones whose axes have names: the condition under which
        /// its motor axes are counted (MotorAxes). A pad's plain rumble has
        /// none.</summary>
        internal static bool HasMotors(UserDevice device)
            => device?.Device is { } live && live.HapticHandle != IntPtr.Zero;

        /// <summary>A device's facts beyond its first build's line: how it is
        /// held, its XInput place, and for a force-feedback device its driver
        /// file and motor axes.</summary>
        internal void AppendDeviceFacts(StringBuilder sb, UserDevice device)
        {
            RecordingCodec.AppendFact(sb, "held", HeldWord(device));
            int place = XInputPlace(device);
            if (place > 0) RecordingCodec.AppendFact(sb, "xinput", place);
            if (!device.HasForceFeedback) return;
            string driver = ForceDriver(device.VendorId, device.ProdId);
            if (driver != null) RecordingCodec.AppendFact(sb, "ffdriver", driver);
            int axes = MotorAxes(device.Device);
            if (axes >= 0) RecordingCodec.AppendFact(sb, "ffaxes", axes);
            // The axes the motors act on, when a read already gave them. The
            // recorder says them among the events otherwise.
            var motors = KeptMotorAxisNames(device);
            if (motors != null && motors.Count > 0) RecordingCodec.AppendFact(sb, "ffmotors", ForceActuators.Join(motors));
        }

        /// <summary>The axes a force-feedback device's motors act on, as a
        /// read already gave them, or null. It never waits: a product not yet
        /// read is asked for (ForceActuators.ReadSoon).</summary>
        public virtual IReadOnlyList<string> KeptMotorAxisNames(UserDevice device)
            => HasMotors(device) ? ForceActuators.Kept(device.VendorId, device.ProdId) : null;

        // ─────────────────────────────────────────────
        //  Settings, as lines and as a state to compare
        // ─────────────────────────────────────────────

        /// <summary>The words of a setting's name that say its text holds
        /// something private, which is not written unless
        /// <see cref="WrittenThoughPrivate"/> names it: Token to Etag, and the
        /// words PadForge's own settings need besides (an identity's keys, an
        /// access code, a host, a chat channel, a name the user typed, a file
        /// or a folder).</summary>
        private static readonly string[] Private =
        {
            "Token", "Key", "Password", "Secret", "Mail", "Url", "Uri", "Path", "Folder", "Directory", "User", "Login",
            "Account", "Computer", "Machine", "Cookie", "Session", "Server", "Address", "Certificate", "Etag", "Private",
            "Public", "Code", "Host", "Channel", "Identity", "Peer", "Phrase", "Serial", "Name", "Dir", "File", "Exe",
            "Location", "Install", "Hash", "Clsid", "Ssid", "Wifi", "Pin",
        };

        /// <summary>The words of a PascalCase name: GKeysEnabled is G, Keys
        /// and Enabled, URLPath is URL and Path.</summary>
        private static readonly System.Text.RegularExpressions.Regex Words = new(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>True when a whole word of the name, or its plural, is one
        /// of <see cref="Private"/>: ProblemRecordingSaveFolder and
        /// HeadsetTrackerAddresses are, CenteringDamping and
        /// KeyboardScreenPicture are not.</summary>
        internal static bool IsPrivateName(string name)
        {
            foreach (System.Text.RegularExpressions.Match match in Words.Matches(name))
            {
                string word = match.Value;
                foreach (string p in Private)
                {
                    if (!word.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
                    string tail = word.Substring(p.Length);
                    if (tail.Length == 0 || tail.Equals("s", StringComparison.OrdinalIgnoreCase) || tail.Equals("es", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>Whether a setting of this type holds text. A switch, a
        /// number or a choice holds nothing private and is written whatever
        /// its name. A setting kept as text is text, even when it holds a
        /// number, so a code typed into a private one stays out.</summary>
        private static bool HoldsText(Type type) => type == typeof(string) || type == typeof(Guid) || type == typeof(char);

        /// <summary>Text written though a word of its name is private,
        /// scrubbed like every value: the profile's name and the programs
        /// that switch to it, which show a wrong profile or a missed
        /// switch.</summary>
        private static readonly HashSet<(Type Type, string Name)> WrittenThoughPrivate = new()
        {
            (typeof(ProfileData), nameof(ProfileData.Name)),
            (typeof(ProfileData), nameof(ProfileData.ExecutableNames)),
        };

        private static readonly Dictionary<Type, PropertyInfo[]> Simple = new();

        private static PropertyInfo[] SimpleOf(Type type)
        {
            lock (Simple)
            {
                if (Simple.TryGetValue(type, out var known)) return known;
                var list = new List<PropertyInfo>();
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                    if (p.GetCustomAttribute<System.Xml.Serialization.XmlIgnoreAttribute>() != null) continue;
                    var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                    if (!(t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid))) continue;
                    if (HoldsText(t) && IsPrivateName(p.Name) && !WrittenThoughPrivate.Contains((type, p.Name))) continue;
                    list.Add(p);
                }
                list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return Simple[type] = list.ToArray();
            }
        }

        /// <summary>Every simple value of an object that is not private, as
        /// name and text, whole. A line cuts it once scrubbed.</summary>
        internal static IEnumerable<(string Name, string Value)> SimpleValues(object data)
        {
            if (data == null) yield break;
            foreach (var p in SimpleOf(data.GetType()))
            {
                object value;
                try { value = p.GetValue(data); }
                catch (TargetInvocationException) { continue; }
                yield return (p.Name, TextOf(value));
            }
        }

        /// <summary>The settings a file's settings part writes beyond the
        /// first build's: every app setting, the active profile's own
        /// settings, and every tuning value of each controller's devices
        /// (their PadSetting) and of each mapping row's sources.</summary>
        public virtual void MoreSettingsLines(PrivacyScrubber scrubber, List<string> into)
        {
            {
                var sb = new StringBuilder("app");
                foreach (var (name, value) in AppValues()) RecordingCodec.AppendFact(sb, name, Written(scrubber, value));
                if (sb.Length > 3) into.Add(sb.ToString());
            }
            string activeId = SettingsManager.ActiveProfileId;
            foreach (var profile in Snapshot(SettingsManager.Profiles))
            {
                if (profile == null || profile.Id != activeId) continue;
                var sb = new StringBuilder("profile.settings");
                foreach (var (name, value) in SimpleValues(profile)) RecordingCodec.AppendFact(sb, name, Written(scrubber, value));
                into.Add(sb.ToString());
            }
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;
            List<UserSetting> assigned;
            lock (settings.SyncRoot) assigned = new List<UserSetting>(settings.Items);
            foreach (var setting in assigned)
            {
                if (setting == null || setting.MapTo < 0 || setting.MapTo >= InputManager.MaxPads) continue;
                var pad = setting.GetPadSetting();
                if (pad == null) continue;
                var sb = new StringBuilder("tuning");
                RecordingCodec.AppendFact(sb, "device", scrubber.DeviceAlias(setting.InstanceGuid.ToString()));
                RecordingCodec.AppendFact(sb, "controller", PrivacyScrubber.ControllerAlias(setting.MapTo));
                foreach (var (name, value) in SimpleValues(pad))
                    if (value.Length > 0 && value != "0") RecordingCodec.AppendFact(sb, name, Written(scrubber, value));
                into.Add(sb.ToString());
            }
        }

        /// <summary>A mapping row source's every tuning value that is not at
        /// its default, as facts after its number.</summary>
        internal static void AppendSourceTuning(StringBuilder sb, MappingSource source, string n, PrivacyScrubber scrubber)
        {
            var fresh = new MappingSource();
            var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in SimpleValues(fresh)) defaults[name] = value;
            foreach (var (name, value) in SimpleValues(source))
            {
                if (name is "Descriptor" or "Kind" or "Invert" or "HalfAxis" or "DeadZone") continue;
                if (defaults.TryGetValue(name, out string d) && d == value) continue;
                RecordingCodec.AppendFact(sb, name + n, Written(scrubber, value));
            }
        }

        /// <summary>
        /// The settings as names and values, for the watch that says what
        /// changed while recording: every app setting, the active profile's,
        /// every mapping row's sources and every controller's tuning. Values
        /// are raw here, and scrubbed when a change is written. The window's
        /// thread.
        /// </summary>
        public virtual Dictionary<string, string> SettingsState()
        {
            var state = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in AppValues()) state["app " + name] = value;
            string activeId = SettingsManager.ActiveProfileId;
            state["profile"] = activeId ?? "default";
            foreach (var profile in Snapshot(SettingsManager.Profiles))
                if (profile != null && profile.Id == activeId)
                    foreach (var (name, value) in SimpleValues(profile)) state["profile " + name] = value;
            var created = SettingsManager.SlotCreated;
            var sets = SettingsManager.SlotMappingSets;
            for (int slot = 0; slot < InputManager.MaxPads; slot++)
            {
                if (created == null || !created[slot] || sets == null || slot >= sets.Length || sets[slot] == null) continue;
                string controller = PrivacyScrubber.ControllerAlias(slot);
                foreach (var row in Snapshot(sets[slot].Rows))
                {
                    if (row == null) continue;
                    var sb = new StringBuilder();
                    foreach (var source in Snapshot(row.Sources))
                    {
                        if (source == null) continue;
                        sb.Append('[');
                        foreach (var (name, value) in SimpleValues(source)) sb.Append(name).Append('=').Append(value).Append(' ');
                        sb.Append(']');
                    }
                    if (!string.IsNullOrEmpty(row.CombineExpression)) sb.Append(" formula");
                    state["row " + controller + " " + (row.Target ?? string.Empty) + " " + (string.IsNullOrEmpty(row.LayerMask) ? "Base" : row.LayerMask)] = sb.ToString();
                }
            }
            var settings = SettingsManager.UserSettings;
            if (settings != null)
            {
                List<UserSetting> assigned;
                lock (settings.SyncRoot) assigned = new List<UserSetting>(settings.Items);
                foreach (var setting in assigned)
                {
                    if (setting == null) continue;
                    state["assign " + setting.InstanceGuid] = setting.MapTo < 0 ? "none" : PrivacyScrubber.ControllerAlias(setting.MapTo);
                    var pad = setting.GetPadSetting();
                    if (pad == null) continue;
                    foreach (var (name, value) in SimpleValues(pad)) state["tuning " + setting.InstanceGuid + " " + name] = value;
                }
            }
            return state;
        }

        // ─────────────────────────────────────────────
        //  What the watch asks
        // ─────────────────────────────────────────────

        /// <summary>Automation's Activity as its own Export Activity… writes
        /// it, set by the window, or null with no Automation (#600).</summary>
        internal static Func<string> ActivitySource;

        /// <summary>The Activity part's text: every call programs made of
        /// PadForge this session, in Activity's own words. Empty with none.</summary>
        public virtual string ActivityText()
        {
            try { return ActivitySource?.Invoke() ?? string.Empty; }
            catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException) { return string.Empty; }
        }

        /// <summary>Whether the engine runs passes.</summary>
        public virtual bool EngineRunning() => Engine?.IsRunning ?? false;

        /// <summary>Each keyboard and mouse PadForge reads through Raw Input
        /// that is online, and whether Raw Input lists it now.</summary>
        public virtual List<(UserDevice Device, bool Listed)> RawInputListing()
        {
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var d in RawInputListener.EnumerateKeyboards()) if (d.DevicePath != null) listed.Add(d.DevicePath);
                foreach (var d in RawInputListener.EnumerateMice()) if (d.DevicePath != null) listed.Add(d.DevicePath);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                return null;
            }
            var result = new List<(UserDevice, bool)>();
            foreach (var device in Devices())
            {
                if (!(device.IsKeyboard || device.IsMouse) || !device.IsOnline) continue;
                string path = device.DevicePath;
                if (string.IsNullOrEmpty(path) || !path.StartsWith(@"\\?\", StringComparison.Ordinal)) continue;
                result.Add((device, listed.Contains(path)));
            }
            return result;
        }

        /// <summary>The controller programs that run now, or "none".</summary>
        public virtual string ControllerProgramsNow()
        {
            var running = RunningOf(Images(), ControllerPrograms);
            return running.Count == 0 ? "none" : string.Join(", ", running);
        }
    }
}
