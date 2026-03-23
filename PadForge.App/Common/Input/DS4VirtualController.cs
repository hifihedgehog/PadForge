using System;
using System.Runtime.InteropServices;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    internal sealed class DS4VirtualController : IVirtualController
    {
        private readonly IDualShock4Controller _controller;
        private bool _disposed;
        private Gamepad _lastState;
        private TouchpadState _lastTouchpad;

        /// <summary>Raw DS4_REPORT_EX buffer (size probed at construction).</summary>
        private byte[] _rawReport;

        /// <summary>Tracking numbers for touchpad fingers (bits 6:0 = ID, bit 7 = lifted).</summary>
        private byte _trackNum0 = 0x80; // finger 0 starts lifted
        private byte _trackNum1 = 0x81; // finger 1 starts lifted
        private byte _touchPacketCounter;

        /// <summary>Monotonic timestamp in 10µs ticks (wraps at ushort.MaxValue).</summary>
        private ushort _timestamp;

        public VirtualControllerType Type => VirtualControllerType.DualShock4;
        public bool IsConnected { get; private set; }
        public int FeedbackPadIndex { get; set; }

        /// <summary>True after first successful SubmitRawReport — locks out per-field fallback.</summary>
        private bool _rawReportWorks = true;

        /// <summary>Expected buffer size for DS4_REPORT_EX, probed at runtime.</summary>
        private int _rawReportSize = 63;

        public DS4VirtualController(ViGEmClient client)
        {
            _controller = client.CreateDualShock4Controller();

            // Probe the correct DS4_REPORT_EX size by trying increasing buffer sizes.
            // ViGEm validates buffer.Length == Marshal.SizeOf<DS4_REPORT_EX>().
            _controller.AutoSubmitReport = false;
        }

        /// <summary>
        /// Tries buffer sizes to find what SubmitRawReport accepts.
        /// DS4_REPORT_EX is typically 63 bytes but varies by ViGEm version.
        /// </summary>
        private int ProbeRawReportSize()
        {
            int[] candidates = { 63, 47, 64, 78 };
            foreach (int size in candidates)
            {
                try
                {
                    _controller.SubmitRawReport(new byte[size]);
                    return size;
                }
                catch { }
            }
            // None worked — disable raw reports.
            _rawReportWorks = false;
            _controller.AutoSubmitReport = true;
            return 0;
        }

        public void Connect()
        {
            _controller.Connect();
            IsConnected = true;
        }

        public void Disconnect()
        {
            _controller.Disconnect();
            IsConnected = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (IsConnected)
                Disconnect();

            (_controller as IDisposable)?.Dispose();
        }

        public void SubmitGamepadState(Gamepad gp) => SubmitGamepadState(gp, default);

        public void SubmitGamepadState(Gamepad gp, TouchpadState tp)
        {
            // Skip if nothing changed.
            if (gp.Equals(_lastState) && tp.Equals(_lastTouchpad))
                return;
            _lastState = gp;
            _lastTouchpad = tp;

            // Probe raw report size on first connected call.
            if (_rawReport == null && _rawReportWorks)
            {
                _rawReportSize = ProbeRawReportSize();
                if (_rawReportWorks)
                    _rawReport = new byte[_rawReportSize];
            }

            // If raw reports don't work, use per-field API directly.
            if (!_rawReportWorks)
            {
                SubmitViaPerFieldApi(gp, tp);
                return;
            }

            var buf = _rawReport;
            Array.Clear(buf, 0, buf.Length);

            // Advance timestamp (~188 ticks per report at 800Hz ≈ 1.88ms)
            _timestamp += 188;

            // ── Axes (offsets 0-3) ──
            buf[0] = ShortToByte(gp.ThumbLX);         // bThumbLX
            buf[1] = ShortToByteInvertY(gp.ThumbLY);  // bThumbLY (DS4: 0=up)
            buf[2] = ShortToByte(gp.ThumbRX);         // bThumbRX
            buf[3] = ShortToByteInvertY(gp.ThumbRY);  // bThumbRY

            // ── Buttons (offsets 4-5: wButtons LE) ──
            ushort buttons = 0;

            // D-pad direction (bits 3:0)
            bool up = (gp.Buttons & Gamepad.DPAD_UP) != 0;
            bool down = (gp.Buttons & Gamepad.DPAD_DOWN) != 0;
            bool left = (gp.Buttons & Gamepad.DPAD_LEFT) != 0;
            bool right = (gp.Buttons & Gamepad.DPAD_RIGHT) != 0;
            buttons |= EncodeDPad(up, down, left, right);

            // Face buttons (bits 7:4)
            if ((gp.Buttons & Gamepad.X) != 0) buttons |= (1 << 4);   // Square
            if ((gp.Buttons & Gamepad.A) != 0) buttons |= (1 << 5);   // Cross
            if ((gp.Buttons & Gamepad.B) != 0) buttons |= (1 << 6);   // Circle
            if ((gp.Buttons & Gamepad.Y) != 0) buttons |= (1 << 7);   // Triangle

            // Shoulders and digital triggers (bits 11:8)
            if ((gp.Buttons & Gamepad.LEFT_SHOULDER) != 0) buttons |= (1 << 8);   // L1
            if ((gp.Buttons & Gamepad.RIGHT_SHOULDER) != 0) buttons |= (1 << 9);  // R1
            if (gp.LeftTrigger > 0) buttons |= (1 << 10);  // L2 digital
            if (gp.RightTrigger > 0) buttons |= (1 << 11); // R2 digital

            // Center buttons (bits 13:12)
            if ((gp.Buttons & Gamepad.BACK) != 0) buttons |= (1 << 12);  // Share
            if ((gp.Buttons & Gamepad.START) != 0) buttons |= (1 << 13); // Options

            // Thumbstick clicks (bits 15:14)
            if ((gp.Buttons & Gamepad.LEFT_THUMB) != 0) buttons |= (1 << 14);  // L3
            if ((gp.Buttons & Gamepad.RIGHT_THUMB) != 0) buttons |= (1 << 15); // R3

            buf[4] = (byte)(buttons & 0xFF);
            buf[5] = (byte)(buttons >> 8);

            // ── Special buttons (offset 6: bSpecial) ──
            byte special = 0;
            if ((gp.Buttons & Gamepad.GUIDE) != 0) special |= 0x01;  // PS
            if (tp.Click) special |= 0x02;  // Touchpad Click
            buf[6] = special;

            // ── Triggers (offsets 7-8) ──
            buf[7] = (byte)(gp.LeftTrigger >> 8);   // bTriggerL
            buf[8] = (byte)(gp.RightTrigger >> 8);  // bTriggerR

            // ── Timestamp (offsets 9-10: wTimestamp LE) ──
            buf[9] = (byte)(_timestamp & 0xFF);
            buf[10] = (byte)(_timestamp >> 8);

            // ── Battery (offset 11 + 29) ──
            buf[11] = 0xFF;  // bBatteryLvl (full)
            buf[29] = 0x0B;  // bBatteryLvlSpecial (not charging, near full)

            // ── Touchpad (offsets 32-41) ──
            buf[32] = 1; // bTouchPacketsN = always 1

            // Track finger state transitions for packet counter.
            bool wasDown0 = (_trackNum0 & 0x80) == 0;
            bool wasDown1 = (_trackNum1 & 0x80) == 0;

            if (tp.Down0 && !wasDown0)
                _trackNum0 = (byte)((_touchPacketCounter++ * 2) & 0x7F);      // finger down
            else if (!tp.Down0 && wasDown0)
                _trackNum0 |= 0x80;                                             // finger up

            if (tp.Down1 && !wasDown1)
                _trackNum1 = (byte)(((_touchPacketCounter++ * 2) + 1) & 0x7F); // finger down
            else if (!tp.Down1 && wasDown1)
                _trackNum1 |= 0x80;                                              // finger up

            // sCurrentTouch (9 bytes at offset 33)
            buf[33] = tp.PacketCounter; // bPacketCounter

            // Finger 0
            buf[34] = _trackNum0; // bIsUpTrackingNum1
            int x0 = (int)(tp.X0 * 1919f);
            int y0 = (int)(tp.Y0 * 942f);
            x0 = Math.Clamp(x0, 0, 1919);
            y0 = Math.Clamp(y0, 0, 942);
            buf[35] = (byte)(x0 & 0xFF);
            buf[36] = (byte)(((x0 >> 8) & 0x0F) | ((y0 << 4) & 0xF0));
            buf[37] = (byte)(y0 >> 4);

            // Finger 1
            buf[38] = _trackNum1; // bIsUpTrackingNum2
            int x1 = (int)(tp.X1 * 1919f);
            int y1 = (int)(tp.Y1 * 942f);
            x1 = Math.Clamp(x1, 0, 1919);
            y1 = Math.Clamp(y1, 0, 942);
            buf[39] = (byte)(x1 & 0xFF);
            buf[40] = (byte)(((x1 >> 8) & 0x0F) | ((y1 << 4) & 0xF0));
            buf[41] = (byte)(y1 >> 4);

            _controller.SubmitRawReport(buf);
        }

        /// <summary>
        /// Fallback: submit via individual SetButtonState/SetAxisValue calls when
        /// SubmitRawReport buffer size doesn't match DS4_REPORT_EX.
        /// Touchpad data cannot be submitted this way (no per-field touchpad API).
        /// </summary>
        private void SubmitViaPerFieldApi(Gamepad gp, TouchpadState tp)
        {
            _controller.SetButtonState(DualShock4Button.Cross, (gp.Buttons & Gamepad.A) != 0);
            _controller.SetButtonState(DualShock4Button.Circle, (gp.Buttons & Gamepad.B) != 0);
            _controller.SetButtonState(DualShock4Button.Square, (gp.Buttons & Gamepad.X) != 0);
            _controller.SetButtonState(DualShock4Button.Triangle, (gp.Buttons & Gamepad.Y) != 0);
            _controller.SetButtonState(DualShock4Button.ShoulderLeft, (gp.Buttons & Gamepad.LEFT_SHOULDER) != 0);
            _controller.SetButtonState(DualShock4Button.ShoulderRight, (gp.Buttons & Gamepad.RIGHT_SHOULDER) != 0);
            _controller.SetButtonState(DualShock4Button.Share, (gp.Buttons & Gamepad.BACK) != 0);
            _controller.SetButtonState(DualShock4Button.Options, (gp.Buttons & Gamepad.START) != 0);
            _controller.SetButtonState(DualShock4Button.ThumbLeft, (gp.Buttons & Gamepad.LEFT_THUMB) != 0);
            _controller.SetButtonState(DualShock4Button.ThumbRight, (gp.Buttons & Gamepad.RIGHT_THUMB) != 0);
            _controller.SetButtonState(DualShock4SpecialButton.Ps, (gp.Buttons & Gamepad.GUIDE) != 0);
            _controller.SetButtonState(DualShock4SpecialButton.Touchpad, tp.Click);
            _controller.SetButtonState(DualShock4Button.TriggerLeft, gp.LeftTrigger > 0);
            _controller.SetButtonState(DualShock4Button.TriggerRight, gp.RightTrigger > 0);

            bool up = (gp.Buttons & Gamepad.DPAD_UP) != 0;
            bool down = (gp.Buttons & Gamepad.DPAD_DOWN) != 0;
            bool left = (gp.Buttons & Gamepad.DPAD_LEFT) != 0;
            bool right = (gp.Buttons & Gamepad.DPAD_RIGHT) != 0;
            _controller.SetDPadDirection(GetDPadDirectionEnum(up, down, left, right));

            _controller.SetAxisValue(DualShock4Axis.LeftThumbX, ShortToByte(gp.ThumbLX));
            _controller.SetAxisValue(DualShock4Axis.LeftThumbY, ShortToByteInvertY(gp.ThumbLY));
            _controller.SetAxisValue(DualShock4Axis.RightThumbX, ShortToByte(gp.ThumbRX));
            _controller.SetAxisValue(DualShock4Axis.RightThumbY, ShortToByteInvertY(gp.ThumbRY));
            _controller.SetSliderValue(DualShock4Slider.LeftTrigger, (byte)(gp.LeftTrigger >> 8));
            _controller.SetSliderValue(DualShock4Slider.RightTrigger, (byte)(gp.RightTrigger >> 8));

            _controller.SubmitReport();
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            FeedbackPadIndex = padIndex;
#pragma warning disable CS0618 // FeedbackReceived is obsolete but functional
            _controller.FeedbackReceived += (sender, args) =>
            {
                int idx = FeedbackPadIndex;
                if (idx >= 0 && idx < vibrationStates.Length)
                {
                    ushort newL = (ushort)(args.LargeMotor * 257);
                    ushort newR = (ushort)(args.SmallMotor * 257);
                    ushort oldL = vibrationStates[idx].LeftMotorSpeed;
                    ushort oldR = vibrationStates[idx].RightMotorSpeed;

                    vibrationStates[idx].LeftMotorSpeed = newL;
                    vibrationStates[idx].RightMotorSpeed = newR;

                    if (newL != oldL || newR != oldR)
                        RumbleLogger.Log($"[ViGEm DS4] Pad{idx} feedback L:{oldL}->{newL} R:{oldR}->{newR}");
                }
            };
#pragma warning restore CS0618
        }

        /// <summary>Signed short → unsigned byte (0-255, center=128).</summary>
        private static byte ShortToByte(short value) =>
            (byte)((value + 32768) >> 8);

        /// <summary>Signed short → unsigned byte with Y-axis inversion for DS4 convention.</summary>
        private static byte ShortToByteInvertY(short value) =>
            (byte)((32767 - value) >> 8);

        /// <summary>Combines D-pad booleans into ViGEm DualShock4DPadDirection (per-field API).</summary>
        private static DualShock4DPadDirection GetDPadDirectionEnum(bool up, bool down, bool left, bool right)
        {
            if (up && right) return DualShock4DPadDirection.Northeast;
            if (up && left) return DualShock4DPadDirection.Northwest;
            if (down && right) return DualShock4DPadDirection.Southeast;
            if (down && left) return DualShock4DPadDirection.Southwest;
            if (up) return DualShock4DPadDirection.North;
            if (down) return DualShock4DPadDirection.South;
            if (left) return DualShock4DPadDirection.West;
            if (right) return DualShock4DPadDirection.East;
            return DualShock4DPadDirection.None;
        }

        /// <summary>Encodes D-pad booleans into DS4 hat direction (bits 3:0).</summary>
        private static ushort EncodeDPad(bool up, bool down, bool left, bool right)
        {
            if (up && right) return 1;  // NE
            if (right && down) return 3; // SE
            if (down && left) return 5;  // SW
            if (left && up) return 7;    // NW
            if (up) return 0;            // N
            if (right) return 2;         // E
            if (down) return 4;          // S
            if (left) return 6;          // W
            return 8;                     // None
        }
    }
}
