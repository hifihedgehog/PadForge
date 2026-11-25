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
        /// PadForge.xml holds (AppSettingsData) whose name says nothing
        /// private, read from the window's settings by the same name. Read
        /// once.</summary>
        internal static IReadOnlyList<string> AppSettingNames => _appSettingNames ??= BuildAppSettingNames();
        private static IReadOnlyList<string> _appSettingNames;

        private static IReadOnlyList<string> BuildAppSettingNames()
        {
            var names = new List<string>();
            foreach (var p in SimpleOf(typeof(AppSettingsData))) names.Add(p.Name);
            return names;
        }

        /// <summary>The app settings as they stand now, by name, from the
        /// window's view model: no settings file is built for it, so a look
        /// costs reads alone.</summary>
        internal static IEnumerable<(string Name, string Value)> AppValues()
        {
            var vm = Main?.Settings;
            if (vm == null) yield break;
            var type = vm.GetType();
            foreach (string name in AppSettingNames)
            {
                var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanRead || p.GetIndexParameters().Length != 0) continue;
                object value;
                try { value = p.GetValue(vm); }
                catch (TargetInvocationException) { continue; }
                yield return (name, TextOf(value));
            }
        }

        private static string TextOf(object value)
        {
            string text = value switch
            {
                null => string.Empty,
                bool b => b ? "1" : "0",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
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

        /// <summary>How a device is held: for PadForge alone (Use Only in
        /// PadForge), hidden by HidHide, or shared with every program.</summary>
        internal static string HeldWord(UserDevice device)
            => device.HidHideEnabled ? "hidden" : device.ConsumeInputEnabled ? "only" : "shared";

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
        }

        // ─────────────────────────────────────────────
        //  Settings, as lines and as a state to compare
        // ─────────────────────────────────────────────

        /// <summary>The words in a setting's name that say it holds something
        /// private, which is never written.</summary>
        private static readonly string[] Private =
        {
            "Token", "Key", "Password", "Secret", "Mail", "Url", "Uri", "Path", "Folder", "Directory", "User", "Login",
            "Account", "Computer", "Machine", "Cookie", "Session", "Server", "Address", "Certificate", "Etag", "Private",
            "Public", "Code", "Host", "Channel", "Identity", "Peer", "Phrase", "Serial", "Name", "Dir", "File", "Exe",
            "Location", "Install", "Hash", "Clsid", "Ssid", "Wifi", "Pin",
        };

        internal static bool IsPrivateName(string name)
        {
            foreach (string word in Private)
                if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static readonly Dictionary<Type, PropertyInfo[]> Simple = new();

        private static PropertyInfo[] SimpleOf(Type type)
        {
            lock (Simple)
            {
                if (Simple.TryGetValue(type, out var known)) return known;
                var list = new List<PropertyInfo>();
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length != 0 || IsPrivateName(p.Name)) continue;
                    if (p.GetCustomAttribute<System.Xml.Serialization.XmlIgnoreAttribute>() != null) continue;
                    var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                    if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid)) list.Add(p);
                }
                list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return Simple[type] = list.ToArray();
            }
        }

        /// <summary>Every simple value of an object that is not private, as
        /// name and text, each cut at 100 characters.</summary>
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
                foreach (var (name, value) in AppValues()) RecordingCodec.AppendFact(sb, name, scrubber.Text(value));
                if (sb.Length > 3) into.Add(sb.ToString());
            }
            string activeId = SettingsManager.ActiveProfileId;
            foreach (var profile in Snapshot(SettingsManager.Profiles))
            {
                if (profile == null || profile.Id != activeId) continue;
                var sb = new StringBuilder("profile.settings");
                foreach (var (name, value) in SimpleValues(profile)) RecordingCodec.AppendFact(sb, name, scrubber.Text(value));
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
                    if (value.Length > 0 && value != "0") RecordingCodec.AppendFact(sb, name, scrubber.Text(value));
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
                RecordingCodec.AppendFact(sb, name + n, scrubber.Text(value));
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
