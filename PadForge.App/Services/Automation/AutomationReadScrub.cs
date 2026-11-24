using System;
using System.Collections.Generic;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The scrubber every free-text field a program reads goes through (#597):
    /// a device's name in <c>devices</c>, a control's name in <c>caps</c>,
    /// the names in <c>state</c>, <c>query</c> and <c>e profile</c>, and
    /// every string of an assistant's tool result (AssistantCalls). What
    /// a program reads carries no user name, PC name, serial number,
    /// Bluetooth address, folder path or access code, and no name the user
    /// gave a device. The problem recorder uses the same scrubber
    /// (PrivacyScrubber) told the same live values (ProblemRecorder's
    /// LiveValues), and every device's own name the user typed.
    ///
    /// <para>The values this PC could give away are read again once a
    /// second at most, so a program that asks in a loop does not have the
    /// audio devices and network adapters listed thousands of times a
    /// second, and a device that connected is known within that second.
    /// A read calls <see cref="Prepare"/> before it takes the device list's
    /// lock, so the listing never runs under it.</para>
    /// </summary>
    internal static class AutomationReadScrub
    {
        /// <summary>How long one reading of the live values serves.</summary>
        internal const int FreshForMs = 1000;

        private static readonly object Sync = new object();
        private static PrivacyScrubber _scrubber;
        private static long _madeMs = long.MinValue;

        /// <summary>Test seam: the scrubber reads use, made by the test.
        /// Null in the app.</summary>
        internal static Func<PrivacyScrubber> Seam;

        /// <summary>A free-text field of a read with every private value
        /// out. Null and empty come back as they are.</summary>
        internal static string Text(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Current().Text(text);
        }

        /// <summary>Reads the live values again when the last reading is
        /// more than <see cref="FreshForMs"/> old. Called outside every
        /// lock, before a read's rows are made.</summary>
        internal static void Prepare() => Scrubber(fresh: true);

        /// <summary>The scrubber, read again first when the last reading is
        /// more than <see cref="FreshForMs"/> old: what an assistant's tool
        /// result goes through. Called outside every lock.</summary>
        internal static PrivacyScrubber Fresh() => Scrubber(fresh: true);

        /// <summary>The scrubber made last, or a new one when none was
        /// made.</summary>
        private static PrivacyScrubber Current() => Scrubber(fresh: false);

        private static PrivacyScrubber Scrubber(bool fresh)
        {
            var seam = Seam;
            if (seam != null) return seam();
            long now = Environment.TickCount64;
            lock (Sync)
            {
                if (_scrubber != null && (!fresh || now - _madeMs < FreshForMs)) return _scrubber;
            }
            var made = Make();
            lock (Sync)
            {
                _scrubber = made;
                _madeMs = now;
            }
            return made;
        }

        /// <summary>A scrubber told this PC's live values: what the problem
        /// recorder's check reads, and the names the user gave its
        /// devices.</summary>
        internal static PrivacyScrubber Make(PrivacyEnvironment environment = null)
        {
            environment ??= PrivacyEnvironment.Current();
            var scrubber = new PrivacyScrubber(environment);
            foreach (var (kind, value) in LiveValues(environment, Devices())) scrubber.Know(kind, value);
            return scrubber;
        }

        private static List<UserDevice> Devices()
        {
            var list = PadForge.Common.Input.SettingsManager.UserDevices;
            if (list == null) return new List<UserDevice>();
            lock (list.SyncRoot) return new List<UserDevice>(list.Items);
        }

        /// <summary>The values a read must never carry.</summary>
        internal static List<(PrivateKind Kind, string Value)> LiveValues(PrivacyEnvironment environment, List<UserDevice> devices)
        {
            var live = new List<(PrivateKind, string)>
            {
                (PrivateKind.UserName, environment.UserName),
                (PrivateKind.PcName, environment.PcName),
                (PrivateKind.PcName, environment.DomainName),
                (PrivateKind.Folder, environment.ProfileFolder),
            };
            foreach (var device in devices)
            {
                if (device == null) continue;
                if (!string.IsNullOrWhiteSpace(device.SerialNumber))
                    live.Add((PrivacyScrubber.BareBluetooth(device.SerialNumber) != null ? PrivateKind.BluetoothAddress : PrivateKind.SerialNumber,
                        device.SerialNumber));
                string instance = PrivacyScrubber.InstancePartOf(device.DevicePath);
                if (!string.IsNullOrEmpty(instance)) live.Add((PrivateKind.DeviceInstance, instance));
                // The name the user gave the device on the Devices page.
                if (!string.IsNullOrWhiteSpace(device.DisplayName)) live.Add((PrivateKind.DeviceName, device.DisplayName));
                // A name its pairing gave it, which differs from its
                // product's own: a Bluetooth pad carries the name its owner
                // gave it.
                if (!string.IsNullOrWhiteSpace(device.InstanceName)
                    && !string.Equals(device.InstanceName.Trim(), device.ProductName?.Trim(), StringComparison.OrdinalIgnoreCase))
                    live.Add((PrivateKind.DeviceName, device.InstanceName));
                if (device.DevicePath != null && device.DevicePath.StartsWith(ProblemRecorder.WebDevicePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    ProblemRecorder.WebNames(device.ProductName, live);
                    ProblemRecorder.WebNames(device.InstanceName, live);
                }
            }
            try { ProblemRecorder.Instance.Host.LiveValues(live); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                // The device and user values above still go in.
            }
            live.RemoveAll(v => string.IsNullOrWhiteSpace(v.Item2));
            return live;
        }

        /// <summary>
        /// The scrub of a whole file a program can read, a snapshot it asked
        /// for: a scrubber made for it alone, told the live values and the
        /// names the user typed for profiles, macros, tags and phrases, as
        /// the problem recorder's is. Each line goes through it, and then the
        /// whole text is searched again for every value it learned on the
        /// way (PrivacyScrubber.SearchFinished), as the recorder does at its
        /// stop.
        /// </summary>
        internal static Func<string, string> FileScrub()
        {
            var seam = Seam;
            PrivacyScrubber scrubber;
            if (seam != null) scrubber = seam();
            else
            {
                scrubber = Make();
                try { ProblemRecorder.Instance.Host.KnowTypedNames(scrubber); }
                catch (InvalidOperationException) { /* a list that changed under the walk; the values above still go in */ }
            }
            return text =>
            {
                if (string.IsNullOrEmpty(text)) return text;
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++) lines[i] = scrubber.Text(lines[i]);
                return scrubber.SearchFinished(string.Join(Environment.NewLine, lines), count: false);
            };
        }

        /// <summary>Test seam: the next read makes its scrubber anew.</summary>
        internal static void ForgetForTest()
        {
            lock (Sync)
            {
                _scrubber = null;
                _madeMs = long.MinValue;
            }
        }
    }
}
