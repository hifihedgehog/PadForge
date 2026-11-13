using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Falcon;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    public partial class InputService
    {
        /// <summary>How long a Falcon's line asks for homing after it opens.
        /// Force Dimension's library names no status that says a Falcon is
        /// homed, so the line asks for a while and then gives the rate.</summary>
        private const int FalconHomingHintMs = 20000;

        private string _falconStatusLast;
        private Guid _falconRowShown;

        /// <summary>The Falcon section of a device's settings into the pad's
        /// view model (#521), the wheel fields' neighbor.</summary>
        internal static void LoadFalconSettings(PadViewModel padVm, PadSetting ps)
        {
            static int Int(string s, int fallback)
                => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
            padVm.FalconFeel = Int(ps.FalconFeel, 0);
            padVm.FalconSpring = Int(ps.FalconSpring, 100);
            padVm.FalconKickButton = Int(ps.FalconKickButton, 1);
            padVm.FalconKick = Int(ps.FalconKick, 100);
            padVm.FalconShake = Int(ps.FalconShake, 100);
            padVm.FalconBraceButton = Int(ps.FalconBraceButton, 0);
            padVm.FalconPreloadButton = Int(ps.FalconPreloadButton, 0);
            padVm.FalconGravity = Int(ps.FalconGravity, 100);
            padVm.FalconForceCap = Int(ps.FalconForceCap, 8);
        }

        /// <summary>The pad's Falcon section back into a device's settings.</summary>
        internal static void SaveFalconSettings(PadViewModel padVm, PadSetting ps)
        {
            var ic = CultureInfo.InvariantCulture;
            ps.FalconFeel = padVm.FalconFeel.ToString(ic);
            ps.FalconSpring = padVm.FalconSpring.ToString(ic);
            ps.FalconKickButton = padVm.FalconKickButton.ToString(ic);
            ps.FalconKick = padVm.FalconKick.ToString(ic);
            ps.FalconShake = padVm.FalconShake.ToString(ic);
            ps.FalconBraceButton = padVm.FalconBraceButton.ToString(ic);
            ps.FalconPreloadButton = padVm.FalconPreloadButton.ToString(ic);
            ps.FalconGravity = padVm.FalconGravity.ToString(ic);
            ps.FalconForceCap = padVm.FalconForceCap.ToString(ic);
        }

        /// <summary>Whether a relayed frame is one this PC applies: anything
        /// to a device that is no Falcon, and to a Falcon a Vibration frame
        /// only with the peer slot's settings (#521). A peer from before the
        /// settings rode the frame sends none.</summary>
        internal static bool TakesRemoteFalconFrame(ISdlInputDevice source, PadForge.Engine.RemoteLink.OutputEffectCodec.OutputEffect effect)
            => source is not FalconDevice
               || (effect.Kind == PadForge.Engine.RemoteLink.OutputEffectCodec.Kind.Vibration && effect.Falcon.HasValue);

        /// <summary>The owner's half of a Falcon shared over Remote Link
        /// (#521, #138): the peer's combined force goes to the haptic thread
        /// with the peer slot's settings, or the Falcon is let go when the
        /// peer's slots let it go. The hand-off is built here, off the poll
        /// thread's caches, since this runs on the link's receive
        /// thread.</summary>
        internal static void ApplyRemoteFalcon(FalconDevice falcon, PadForge.Engine.RemoteLink.OutputEffectCodec.OutputEffect effect)
        {
            if (effect.Falcon is not { } tail || effect.Vibration == null) return;
            if (!tail.Assigned)
            {
                falcon.Handoff = FalconHandoff.Unassigned;
                return;
            }
            var settings = FalconSettings.FromWire(tail);
            var v = effect.Vibration;
            falcon.Handoff = new FalconHandoff(settings,
                FalconGameForce.From(v, v.LeftMotorSpeed, v.RightMotorSpeed, settings.Overall), true);
        }

        /// <summary>The open Falcon with this row's identity, or null.</summary>
        internal FalconDevice FindFalcon(Guid instanceGuid)
            => _inputManager?.FalconRows.FirstOrDefault(f => f.InstanceGuid == instanceGuid);

        /// <summary>The Settings card's lines (#521): the library, and why no
        /// Falcon opened when none did. Each open Falcon's own line is on its
        /// row's panel (<see cref="UpdateFalconDeviceRow"/>). Empty while
        /// the switch is off or the engine is stopped.</summary>
        private void UpdateFalconStatus()
        {
            var settings = _mainVm.Settings;
            var im = _inputManager;
            string status = string.Empty;
            bool canInstall = false;
            bool warns = false;
            if (im != null && im.IsRunning && settings.NovintFalconEnabled)
            {
                var s = Strings.Instance;
                var culture = CultureInfo.CurrentCulture;
                var sweep = FalconRuntime.Status;
                var lines = new List<string>();
                switch (sweep.LibraryReason)
                {
                    case "":
                        lines.Add(string.Format(culture, s.Falcon_Status_Library_Format,
                            string.IsNullOrEmpty(sweep.SdkVersion) ? "?" : sweep.SdkVersion, sweep.LibraryPath));
                        if (sweep.PickNotLoaded) lines.Add(s.Falcon_Status_PickNotLoaded);
                        break;
                    case "platform":
                        lines.Add(s.Falcon_Status_Arm64);
                        break;
                    case "load":
                        lines.Add(s.Falcon_Status_LoadFailed);
                        break;
                    case "exports":
                        lines.Add(s.Falcon_Status_NotLibrary);
                        break;
                    default:
                        lines.Add(s.Falcon_Status_NoSdk);
                        break;
                }

                var rows = im.FalconRows;
                canInstall = FalconCanInstall(sweep, rows.Count);
                warns = FalconStatusIsWarning(sweep, rows.Count);
                if (rows.Count == 0 && sweep.LibraryReason.Length == 0)
                    lines.Add(FalconNoRowLine(sweep, canInstall));
                status = string.Join(Environment.NewLine, lines);
            }
            settings.FalconCanInstallDriver = canInstall;
            settings.FalconStatusIsWarning = warns;
            if (string.Equals(_falconStatusLast, status, StringComparison.Ordinal)) return;
            _falconStatusLast = status;
            settings.FalconStatus = status;
        }

        /// <summary>The Install button: no Falcon open, a Falcon with no
        /// driver, none on any driver (the install stops on one), and an SDK
        /// folder that holds Force Dimension's package, which the sweep found
        /// on its worker.</summary>
        internal static bool FalconCanInstall(FalconSweepStatus sweep, int rows)
            => rows == 0 && sweep.HasDriverlessNode && sweep.OtherDrivers.Length == 0
               && sweep.OwnDriverNodes == 0 && sweep.DriverPackage != null;

        /// <summary>The card's lines warn (#574) when the library did not
        /// load, a picked copy was not the one loaded, or no Falcon is open.
        /// The loaded library's line beside an open Falcon is information.</summary>
        internal static bool FalconStatusIsWarning(FalconSweepStatus sweep, int rows)
            => sweep.LibraryReason.Length != 0 || sweep.PickNotLoaded || rows == 0;

        /// <summary>Why no Falcon opened, with the library loaded. A Falcon
        /// on Force Dimension's own driver that did not open gets the
        /// library's reason, since installing that driver again would change
        /// nothing.</summary>
        internal static string FalconNoRowLine(FalconSweepStatus sweep, bool canInstall)
        {
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            if (sweep.InUse > 0) return s.Falcon_Status_InUse;
            if (sweep.OtherDrivers.Length > 0)
                return string.Format(culture, s.Falcon_Status_OtherDriver_Format, sweep.OtherDrivers);
            if (sweep.OwnDriverNodes > 0)
                return string.Format(culture, s.Falcon_Status_OwnDriverNotOpen_Format,
                    string.IsNullOrEmpty(sweep.OpenError) ? "?" : sweep.OpenError);
            if (sweep.HasDriverlessNode)
                return canInstall ? s.Falcon_Status_NoDriver : s.Falcon_Status_NoDriverNoPackage;
            return s.Falcon_Status_NotFound;
        }

        /// <summary>
        /// The selected row's Falcon line and Calibrate Reach (#521), on the
        /// Devices page's tick: the Falcon's name and how it runs, a homing
        /// prompt for its first seconds, then its rate. The calibration line
        /// shows the reach the Falcon uses each time its row is selected,
        /// until a sweep replaces it. A row that is no open Falcon shows
        /// neither.
        /// </summary>
        private void UpdateFalconDeviceRow(DeviceRowViewModel row, UserDevice ud)
        {
            bool newlySelected = _falconRowShown != ud.InstanceGuid;
            _falconRowShown = ud.InstanceGuid;
            var falcon = FalconRuntime.Enabled && ud.CapType == InputDeviceType.NovintFalcon ? FindFalcon(ud.InstanceGuid) : null;
            if (falcon == null || !falcon.IsAttached)
            {
                row.FalconLine = string.Empty;
                row.ShowFalconCalibrate = false;
                return;
            }
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            string name = falcon.Serial.HasValue
                ? string.Format(culture, s.Falcon_Name_Format, falcon.Serial.Value)
                : string.Format(culture, s.Falcon_Name_Format, falcon.Index + 1);
            long now = Environment.TickCount64;
            string state = now - falcon.OpenedAtTicks < FalconHomingHintMs ? s.Falcon_Status_Home
                : falcon.RateKhz > 0 ? string.Format(culture, s.Falcon_Status_Rate_Format, falcon.RateKhz)
                : s.Falcon_Status_Open;
            row.FalconLine = string.Format(culture, s.Falcon_Status_Line_Format, name, state);
            row.ShowFalconCalibrate = true;
            // A sweep running keeps its own line.
            if ((newlySelected || string.IsNullOrEmpty(row.FalconCalibrationStatus)) && !falcon.IsCalibrating)
                row.FalconCalibrationStatus = FalconReachLine(FalconDevice.ParseReach(ud.FalconCalibration));
        }

        /// <summary>The calibration line: the reach a Falcon uses, in
        /// millimeters.</summary>
        internal static string FalconReachLine(FalconReach reach)
            => string.Format(CultureInfo.CurrentCulture, Strings.Instance.Falcon_Reach_Format,
                reach.Y * 1000.0, reach.Z * 1000.0, reach.X * 1000.0);
    }
}
