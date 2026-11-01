using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Models2D;

namespace PadForge.Services
{
    /// <summary>
    /// One slot as the controller picture page (issue #555) draws it: the
    /// controls lit, by the web layouts' target names, the trigger pulls 0 to
    /// 1, and the sticks -1 to 1 with up positive. Built by the pad page's
    /// own two projections, so the page lights what the pad page lights:
    /// <see cref="FromGamepad"/> is PadViewModel.UpdateFromEngineState, and
    /// <see cref="FromRaw"/> is PadViewModel.UpdateNintendoPreviewFromRaw for
    /// the Nintendo and Valve slots, whose output is the raw surface.
    /// </summary>
    internal sealed class DisplayPose
    {
        public IReadOnlyList<string> Lit { get; init; } = Array.Empty<string>();
        public double LeftTrigger { get; init; }
        public double RightTrigger { get; init; }
        public double LeftX { get; init; }
        public double LeftY { get; init; }
        public double RightX { get; init; }
        public double RightY { get; init; }

        /// <summary>A stick axis, -32768 to 32767 with up positive, as -1 to
        /// 1.</summary>
        internal static double Stick(short v) => v < 0 ? v / 32768.0 : v / 32767.0;

        /// <summary>A gamepad-shaped slot's combined output.</summary>
        public static DisplayPose FromGamepad(Gamepad gp)
        {
            var lit = new List<string>(8);
            void Bit(ushort mask, string target)
            {
                if (gp.IsButtonPressed(mask)) lit.Add(target);
            }
            Bit(Gamepad.A, "ButtonA");
            Bit(Gamepad.B, "ButtonB");
            Bit(Gamepad.X, "ButtonX");
            Bit(Gamepad.Y, "ButtonY");
            Bit(Gamepad.LEFT_SHOULDER, "LeftShoulder");
            Bit(Gamepad.RIGHT_SHOULDER, "RightShoulder");
            Bit(Gamepad.BACK, "ButtonBack");
            Bit(Gamepad.START, "ButtonStart");
            Bit(Gamepad.LEFT_THUMB, "LeftThumbButton");
            Bit(Gamepad.RIGHT_THUMB, "RightThumbButton");
            Bit(Gamepad.GUIDE, "ButtonGuide");
            Bit(Gamepad.DPAD_UP, "DPadUp");
            Bit(Gamepad.DPAD_DOWN, "DPadDown");
            Bit(Gamepad.DPAD_LEFT, "DPadLeft");
            Bit(Gamepad.DPAD_RIGHT, "DPadRight");
            if (gp.Share) lit.Add("ButtonShare");
            if (gp.MicMute) lit.Add("ButtonMute");
            if (gp.LeftPaddle) lit.Add("LeftPaddle");
            if (gp.RightPaddle) lit.Add("RightPaddle");
            if (gp.LeftFunction) lit.Add("LeftFunction");
            if (gp.RightFunction) lit.Add("RightFunction");
            return new DisplayPose
            {
                Lit = lit,
                LeftTrigger = gp.LeftTrigger / 65535.0,
                RightTrigger = gp.RightTrigger / 65535.0,
                LeftX = Stick(gp.ThumbLX),
                LeftY = Stick(gp.ThumbLY),
                RightX = Stick(gp.ThumbRX),
                RightY = Stick(gp.ThumbRY),
            };
        }

        /// <summary>A Nintendo or Valve slot's raw surface, read through the
        /// profile's wire table.</summary>
        public static DisplayPose FromRaw(RawHidState raw, string profileId)
        {
            var lit = new List<string>(8);
            var table = NintendoPreviewMap.ButtonTable(profileId);
            bool dpadOnButtons = false, leftTriggerButton = false, rightTriggerButton = false;
            bool steamController = NintendoPreviewMap.FamilyOf(profileId) == NintendoPreviewMap.Family.SteamController;
            for (int i = 0; i < table.Length; i++)
            {
                string target = table[i];
                if (target is "DPadUp" or "DPadDown" or "DPadLeft" or "DPadRight") dpadOnButtons = true;
                if (!raw.IsButtonPressed(i) || string.IsNullOrEmpty(target)) continue;
                switch (target)
                {
                    // ZL and ZR are digital on the hardware: a full pull.
                    case "LeftTrigger": leftTriggerButton = true; break;
                    case "RightTrigger": rightTriggerButton = true; break;
                    case "RightTouchpadClick":
                        lit.Add(target);
                        // One bit, two jobs: on the 2015 pad this click is
                        // also the right stick button, as SDL reads it.
                        if (steamController) lit.Add("RightThumbButton");
                        break;
                    default: lit.Add(target); break;
                }
            }

            // The hat, when the profile spends no buttons on the D-pad. A
            // diagonal lights both cardinals.
            if (!dpadOnButtons)
            {
                int pov = raw.Povs is { Length: > 0 } ? raw.Povs[0] : -1;
                if (pov >= 0 && (pov > 27000 || pov < 9000)) lit.Add("DPadUp");
                if (pov > 0 && pov < 18000) lit.Add("DPadRight");
                if (pov > 9000 && pov < 27000) lit.Add("DPadDown");
                if (pov > 18000 && pov < 36000) lit.Add("DPadLeft");
            }

            // Raw axes are HID convention, positive down. A trigger is
            // bipolar, rest at the bottom of the range.
            short Ax(int i) => raw.Axes != null && i >= 0 && i < raw.Axes.Length ? raw.Axes[i] : (short)0;
            double Trig(int i) => raw.Axes != null && i >= 0 && i < raw.Axes.Length ? (raw.Axes[i] + 32768.0) / 65535.0 : 0;
            static short FlipY(short v) => v == short.MinValue ? short.MaxValue : (short)-v;
            int axLT = NintendoPreviewMap.AxisIndexOf(profileId, "LeftTrigger");
            int axRT = NintendoPreviewMap.AxisIndexOf(profileId, "RightTrigger");
            return new DisplayPose
            {
                Lit = lit,
                // An axis, where the wire has one, wins over the button,
                // the pad page's order.
                LeftTrigger = axLT >= 0 ? Trig(axLT) : leftTriggerButton ? 1.0 : 0,
                RightTrigger = axRT >= 0 ? Trig(axRT) : rightTriggerButton ? 1.0 : 0,
                LeftX = Stick(Ax(NintendoPreviewMap.AxisIndexOf(profileId, "LeftThumbAxisX"))),
                LeftY = Stick(FlipY(Ax(NintendoPreviewMap.AxisIndexOf(profileId, "LeftThumbAxisY")))),
                RightX = Stick(Ax(NintendoPreviewMap.AxisIndexOf(profileId, "RightThumbAxisX"))),
                RightY = Stick(FlipY(Ax(NintendoPreviewMap.AxisIndexOf(profileId, "RightThumbAxisY")))),
            };
        }

        /// <summary>The pose as the page reads it, numbers to three places so
        /// a still stick sends nothing new.</summary>
        public object ToMessage() => new
        {
            type = "state",
            lit = Lit,
            lt = Math.Round(LeftTrigger, 3),
            rt = Math.Round(RightTrigger, 3),
            lx = Math.Round(LeftX, 3),
            ly = Math.Round(LeftY, 3),
            rx = Math.Round(RightX, 3),
            ry = Math.Round(RightY, 3),
        };
    }
}
