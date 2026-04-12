using System;
using HIDMaestro;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Unified virtual controller backed by HIDMaestro. Replaces the v2
    /// Xbox360VirtualController, DS4VirtualController, and VJoyVirtualController
    /// classes — one IVirtualController implementation handles every preset
    /// and custom HID descriptor through a single SDK surface.
    ///
    /// The Type property reports the user-facing category (Microsoft / Sony /
    /// Extended) so existing per-type counting logic in InputService keeps
    /// working. The actual HIDMaestro profile is supplied at construction.
    /// </summary>
    internal sealed class HMaestroVirtualController : IVirtualController
    {
        private readonly HMContext _ctx;
        private readonly HMProfile _profile;
        private readonly VirtualControllerType _type;
        private HMController _controller;
        private bool _disposed;
        private Gamepad _lastState;

        public VirtualControllerType Type => _type;
        public bool IsConnected { get; private set; }
        public int FeedbackPadIndex { get; set; }

        public HMaestroVirtualController(HMContext ctx, HMProfile profile, VirtualControllerType type)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _type = type;
        }

        public void Connect()
        {
            if (IsConnected) return;
            _controller = _ctx.CreateController(_profile);
            IsConnected = true;
        }

        public void Disconnect()
        {
            if (!IsConnected) return;
            _controller?.Dispose();
            _controller = null;
            IsConnected = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            if (_controller == null) return;
            if (gp.Equals(_lastState)) return;
            _lastState = gp;

            var state = new HMGamepadState
            {
                LeftStickX = gp.ThumbLX / 32767f,
                LeftStickY = gp.ThumbLY / 32767f,
                RightStickX = gp.ThumbRX / 32767f,
                RightStickY = gp.ThumbRY / 32767f,
                LeftTrigger = gp.LeftTrigger / 65535f,
                RightTrigger = gp.RightTrigger / 65535f,
                Buttons = MapButtons(gp.Buttons),
                Hat = MapHat(gp.Buttons),
            };

            _controller.SubmitState(state);
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            FeedbackPadIndex = padIndex;
            if (_controller == null) return;

            _controller.OutputReceived += (ctrl, pkt) =>
            {
                int idx = FeedbackPadIndex;
                if (idx < 0 || idx >= vibrationStates.Length) return;

                var data = pkt.Data.Span;

                // XInput rumble: [0x00, 0x04, leftMotor, rightMotor, 0x00]
                if (pkt.Source == HMOutputSource.XInput && data.Length >= 5)
                {
                    ushort newL = (ushort)(data[2] * 257);
                    ushort newR = (ushort)(data[3] * 257);
                    vibrationStates[idx].LeftMotorSpeed = newL;
                    vibrationStates[idx].RightMotorSpeed = newR;
                    return;
                }

                // DualShock 4 / DualSense (Sony VID 0x054C) HID output report
                // Report ID 0x05, bytes [2]/[3] are the rumble motors.
                if (pkt.Source == HMOutputSource.HidOutput
                    && _profile.VendorId == 0x054C
                    && data.Length >= 4)
                {
                    ushort newL = (ushort)(data[2] * 257);
                    ushort newR = (ushort)(data[3] * 257);
                    vibrationStates[idx].LeftMotorSpeed = newL;
                    vibrationStates[idx].RightMotorSpeed = newR;
                    return;
                }

                // Xbox HID rumble (Microsoft VID 0x045E) — vendor-specific
                // bytes 3..6 hold low/high motor strengths.
                if (pkt.Source == HMOutputSource.HidOutput
                    && _profile.VendorId == 0x045E
                    && data.Length >= 8)
                {
                    ushort newL = (ushort)(data[5] * 257);
                    ushort newR = (ushort)(data[6] * 257);
                    vibrationStates[idx].LeftMotorSpeed = newL;
                    vibrationStates[idx].RightMotorSpeed = newR;
                }
            };
        }

        private static HMButton MapButtons(ushort xinputButtons)
        {
            HMButton b = HMButton.None;
            if ((xinputButtons & Gamepad.A) != 0) b |= HMButton.A;
            if ((xinputButtons & Gamepad.B) != 0) b |= HMButton.B;
            if ((xinputButtons & Gamepad.X) != 0) b |= HMButton.X;
            if ((xinputButtons & Gamepad.Y) != 0) b |= HMButton.Y;
            if ((xinputButtons & Gamepad.LEFT_SHOULDER) != 0) b |= HMButton.LeftBumper;
            if ((xinputButtons & Gamepad.RIGHT_SHOULDER) != 0) b |= HMButton.RightBumper;
            if ((xinputButtons & Gamepad.BACK) != 0) b |= HMButton.Back;
            if ((xinputButtons & Gamepad.START) != 0) b |= HMButton.Start;
            if ((xinputButtons & Gamepad.LEFT_THUMB) != 0) b |= HMButton.LeftStick;
            if ((xinputButtons & Gamepad.RIGHT_THUMB) != 0) b |= HMButton.RightStick;
            if ((xinputButtons & Gamepad.GUIDE) != 0) b |= HMButton.Guide;
            return b;
        }

        private static HMHat MapHat(ushort xinputButtons)
        {
            bool up = (xinputButtons & Gamepad.DPAD_UP) != 0;
            bool down = (xinputButtons & Gamepad.DPAD_DOWN) != 0;
            bool left = (xinputButtons & Gamepad.DPAD_LEFT) != 0;
            bool right = (xinputButtons & Gamepad.DPAD_RIGHT) != 0;

            if (up && right) return HMHat.NorthEast;
            if (up && left) return HMHat.NorthWest;
            if (down && right) return HMHat.SouthEast;
            if (down && left) return HMHat.SouthWest;
            if (up) return HMHat.North;
            if (down) return HMHat.South;
            if (left) return HMHat.West;
            if (right) return HMHat.East;
            return HMHat.None;
        }
    }
}
