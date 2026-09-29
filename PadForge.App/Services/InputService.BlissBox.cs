using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    public partial class InputService
    {
        private DreamcastScreenService _dreamcastScreen;
        private string _blissBoxStatusLast;

        /// <summary>The Dreamcast screens and the ports' other choices (#469).</summary>
        internal DreamcastScreenService DreamcastScreen
            => _dreamcastScreen ??= new DreamcastScreenService(_mainVm.Settings,
                () => _settingsService?.MarkDirty(),
                () => _settingsService?.SaveNow() ?? true,
                () => _settingsService?.IsDirty != true);

        /// <summary>Every UI tick, ungated by focus: a clock or a macro show
        /// on a VMU keeps running while PadForge sits behind a game. The
        /// service holds itself to four passes a second, and runs one at once
        /// after a port's choices change. Once it exists it ticks with no port
        /// open too, which drops the shows and requests of the ports that
        /// closed.</summary>
        private void TickBlissBox()
        {
            if (BlissBoxRuntime.Ports.Length == 0 && _dreamcastScreen == null) return;
            try { DreamcastScreen.Tick(); }
            catch { /* a failed pass is retried on the next tick */ }
        }

        /// <summary>
        /// The Bliss-Box line under the Settings switch (#469): each port and
        /// the controller in it. Empty while the feature is off, which
        /// collapses the line, the analog keyboards shape.
        /// </summary>
        private void UpdateBlissBoxStatus()
        {
            var settings = _mainVm.Settings;
            var im = _inputManager;
            string status = string.Empty;
            if (im != null && im.IsRunning && settings.BlissBoxEnabled)
            {
                var s = Strings.Instance;
                var culture = CultureInfo.CurrentCulture;
                var ports = BlissBoxRuntime.Ports;
                status = ports.Length == 0
                    ? s.Settings_BlissBoxStatus_None
                    : string.Format(culture, s.Settings_BlissBoxStatus_Reading_Format,
                        string.Join(", ", ports.OrderBy(p => p.Session.Player).Select(p =>
                            string.Format(culture, s.Settings_BlissBoxStatus_Port_Format,
                                p.Session.Player, PortContents(p)))));
            }
            if (string.Equals(_blissBoxStatusLast, status, StringComparison.Ordinal)) return;
            _blissBoxStatusLast = status;
            settings.BlissBoxStatus = status;
        }

        private static string PortContents(BlissBoxPort port)
        {
            if (!port.IsOpen || port.Session.Info == null) return Strings.Instance.BlissBox_Connecting;
            var info = port.Session.LiveInfo;
            return info == null ? Strings.Instance.BlissBox_NoController : BlissBoxControllers.Name(info.Type);
        }

        /// <summary>The firmware as the tools write it, major.minor.</summary>
        internal static string FirmwareText(BlissBoxInfo info)
            => string.Format(CultureInfo.InvariantCulture, "{0}.{1}", info.Major, info.Minor);

        /// <summary>
        /// The selected row's Bliss-Box line, its actions and its pressure
        /// chips (#469), on the Devices page's tick. A row that is not a
        /// port, or a port while the switch is off, shows none of them.
        /// </summary>
        private void UpdateBlissBoxDeviceRow(DeviceRowViewModel row, UserDevice ud)
        {
            var port = BlissBoxRuntime.Enabled ? BlissBoxRuntime.Find(ud) : null;
            var devVm = _mainVm.Devices;
            if (port == null)
            {
                row.BlissBoxLine = string.Empty;
                row.ShowBlissBoxPlayer = row.ShowDreamcastScreen = row.ShowControllerPak = row.ShowNativeArrows = false;
                row.BlissBoxIdle = true;
                devVm.UpdateBlissBoxPressure(null);
                return;
            }

            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            var info = port.Session.Info;
            var live = port.Session.LiveInfo;
            row.BlissBoxLine = !port.IsOpen || info == null
                ? string.Format(culture, s.Devices_BlissBoxLineConnecting_Format, port.Session.Player)
                : string.Format(culture, s.Devices_BlissBoxLine_Format, port.Session.Player,
                    live == null ? s.BlissBox_NoController : BlissBoxControllers.Name(live.Type), FirmwareText(info));
            row.ShowBlissBoxPlayer = port.IsOpen && info != null;
            row.ShowDreamcastScreen = live != null && BlissBoxControllers.HasScreen(live.Type);
            // The pak rides the native channel, which PadForge frames the way
            // firmware 3.0 and later read it.
            row.ShowControllerPak = live != null && live.Major >= BlissBoxControllers.NativeChannelMajor
                && BlissBoxControllers.HasControllerPak(live.Type);
            row.ShowNativeArrows = live != null && live.Major == 3 && BlissBoxControllers.IsPlayStationDigital(live.Type);
            row.BlissBoxNativeArrows = DreamcastScreen.Get(port.InstanceGuid)?.NativeArrows == true;
            // A player change or a Controller Pak transfer holds the channel
            // until it ends, so their buttons wait for it.
            row.BlissBoxIdle = !port.Session.Busy;
            devVm.UpdateBlissBoxPressure(
                live != null && BlissBoxControllers.HasPressure(live.Type) ? port.Session.Pressure : null);
        }
    }
}
