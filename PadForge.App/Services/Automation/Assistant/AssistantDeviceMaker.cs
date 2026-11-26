using System;
using System.Collections.Concurrent;
using PadForge.Common.Input;
using PadForge.Engine.Data;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// A device's maker as the device names itself (#600): the HID
    /// manufacturer string, read with a query-only open of the device's path,
    /// the way the analog keyboard probe reads it
    /// (AnalogKeyboardHidRuntime.Probe: CreateFile with no access, then
    /// HidD_GetManufacturerString). A device with no HID path, or one that
    /// names no maker, has none. Read once per path and kept once the device
    /// opened: a device that is not there is not opened, and one whose open
    /// failed is read again at the next ask.
    /// </summary>
    internal static class AssistantDeviceMaker
    {
        internal static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The device's maker, or null.</summary>
        internal static string Of(UserDevice ud)
        {
            string path = ud?.DevicePath;
            if (string.IsNullOrEmpty(path) || !path.StartsWith(@"\\?\hid", StringComparison.OrdinalIgnoreCase)) return null;
            // A maker read before is kept: looking it up opens nothing.
            if (Known.TryGetValue(path, out string known)) return known;
            if (!ud.IsOnline) return null;
            var open = OpenForTest;
            if (open == null && DirectDeviceAccess.Refuses(path)) return null;
            var (opened, maker) = (open ?? Read)(path);
            return opened ? Known.GetOrAdd(path, maker) : null;
        }

        /// <summary>The open and read, for a test: whether the device
        /// opened, and the maker it named.</summary>
        internal static Func<string, (bool Opened, string Maker)> OpenForTest;

        private static (bool Opened, string Maker) Read(string path)
        {
            try
            {
                using var handle = SonyHeadsetHid.CreateFile(path, 0,
                    SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                    IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle.IsInvalid) return (false, null);
                var buffer = new byte[512];
                if (!AnalogKeyboardHidRuntime.HidD_GetManufacturerString(handle, buffer, (uint)buffer.Length)) return (true, null);
                string s = System.Text.Encoding.Unicode.GetString(buffer);
                int nul = s.IndexOf('\0');
                s = (nul >= 0 ? s.Substring(0, nul) : s).Trim();
                return (true, s.Length == 0 ? null : s);
            }
            catch (Exception)
            {
                return (false, null);
            }
        }
    }
}
