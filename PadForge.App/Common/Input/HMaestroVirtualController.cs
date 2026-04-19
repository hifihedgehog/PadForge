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
        private int _anyOutputPacketCount;
        private int _motorNonzeroCount;

        public VirtualControllerType Type => _type;
        public bool IsConnected { get; private set; }
        public int FeedbackPadIndex { get; set; }
        public string ProfileId => _profile.Id;
        public ushort ProfileVendorId => _profile.VendorId;
        public ushort ProfileProductId => _profile.ProductId;

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

            // No dedup and no rate limit here — Step 5 already honors the
            // user-configured polling interval (default 1kHz). HIDMaestro is
            // consumer-driven ("the consumer drives the cadence" per the SDK
            // docstring) so every call forwards a fresh frame. Deduping on
            // unchanged state risked dropping rapid press+release bursts
            // between the game's HID reads.

            // XInput convention: Y+ = stick up. HIDMaestro maps LeftStickY=+1
            // straight to HID logical max (Y-down in HID convention), and the
            // XUSB companion in driver/companion.c:387 computes sThumbLY as
            // `32767 - gipLy`, also inverted relative to XInput. Negate Y at
            // the boundary so both paths report Y+ = up to the game.
            var state = new HMGamepadState
            {
                LeftStickX = gp.ThumbLX / 32767f,
                LeftStickY = -gp.ThumbLY / 32767f,
                RightStickX = gp.ThumbRX / 32767f,
                RightStickY = -gp.ThumbRY / 32767f,
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

                // Diagnostic: log every packet up to 100 per controller.
                // Cannot filter on non-zero motors because the "stop" packet
                // (all-zero motors) is exactly what we need to see when
                // investigating stuck-vibration symptoms. Capture the full
                // stream, then review the log for the start-then-stop
                // sequence.
                if (_anyOutputPacketCount < 100)
                {
                    _anyOutputPacketCount++;
                    try
                    {
                        var hex = new System.Text.StringBuilder();
                        for (int b = 0; b < Math.Min(data.Length, 12); b++)
                            hex.Append($"{data[b]:X2} ");
                        System.IO.File.AppendAllText(@"C:\PadForge\vibration-debug.log",
                            $"[{DateTime.Now:HH:mm:ss.fff}] pad{idx} profile={_profile.Id} src={pkt.Source} id=0x{pkt.ReportId:X2} len={data.Length} [{hex}]\n");
                    } catch { }
                }

                // XInput vibration packet layout (from IOCTL_XUSB_SET_STATE):
                //   data[0] = 0x00 (command)
                //   data[1] = 0x08 (size)
                //   data[2] = left motor byte  (wLeftMotorSpeed  >> 8)
                //   data[3] = right motor byte (wRightMotorSpeed >> 8)
                //   data[4] = reserved
                // Chromium's browser Gamepad API sends dual-rumble via this
                // path; the alternating hi=127 / hi=0 pattern IS the browser's
                // square-wave vibration waveform (not keep-alive noise) — do
                // NOT filter packets where both bytes are 0 (that's the "off"
                // phase of the duty cycle).
                if (pkt.Source == HMOutputSource.XInput && data.Length >= 5)
                {
                    vibrationStates[idx].LeftMotorSpeed = (ushort)(data[2] * 257);
                    vibrationStates[idx].RightMotorSpeed = (ushort)(data[3] * 257);
                    return;
                }

                // DualShock 4 / DualSense (Sony VID 0x054C) HID output report:
                // Report ID 0x05, bytes [2]/[3] are the rumble motors.
                if (pkt.Source == HMOutputSource.HidOutput
                    && _profile.VendorId == 0x054C
                    && data.Length >= 4)
                {
                    vibrationStates[idx].LeftMotorSpeed = (ushort)(data[2] * 257);
                    vibrationStates[idx].RightMotorSpeed = (ushort)(data[3] * 257);
                    return;
                }

                // Xbox Series Bluetooth (Microsoft VID 0x045E) — browser
                // Gamepad API on Chromium sends vibration to Xbox Series BT
                // via a HID output report (NOT XInput, unlike wired Xbox 360).
                // Layout is 7 bytes: [trigL, trigR, motorL, motorR, duration,
                // startDelay, loopCount]. Motor bytes are 0..100 magnitudes.
                // Scale to ushort range (~655x). Verified against HIDMaestro
                // test app log of xbox-series-xs-bt + gamepad-tester.com.
                if (pkt.Source == HMOutputSource.HidOutput
                    && _profile.VendorId == 0x045E
                    && data.Length >= 4
                    && data.Length < 8)
                {
                    vibrationStates[idx].LeftMotorSpeed = (ushort)(data[2] * 655);
                    vibrationStates[idx].RightMotorSpeed = (ushort)(data[3] * 655);
                    return;
                }

                // Xbox wired / wireless-receiver long HID rumble report
                // (legacy format, vendor-specific bytes 5/6).
                if (pkt.Source == HMOutputSource.HidOutput
                    && _profile.VendorId == 0x045E
                    && data.Length >= 8)
                {
                    vibrationStates[idx].LeftMotorSpeed = (ushort)(data[5] * 257);
                    vibrationStates[idx].RightMotorSpeed = (ushort)(data[6] * 257);
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
