using System;
using System.Runtime.InteropServices;
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
        private int? _detectedXInputSlot;

        public VirtualControllerType Type => _type;
        public bool IsConnected { get; private set; }
        public int FeedbackPadIndex { get; set; }
        public string ProfileId => _profile.Id;

        /// <summary>
        /// The XInput slot this virtual controller occupies (0..3), or null
        /// if the profile doesn't use XInput or the slot claim didn't land
        /// within the settle window. Detected locally via XInputGetState
        /// before/after delta — HMController.XInputSlot is declared but
        /// never actually assigned inside the HIDMaestro SDK, so we can't
        /// rely on it.
        /// </summary>
        public int? XInputSlot => _detectedXInputSlot;

        public HMaestroVirtualController(HMContext ctx, HMProfile profile, VirtualControllerType type)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _type = type;
        }

        public void Connect()
        {
            if (IsConnected) return;

            bool isXbox = _profile.VendorId == 0x045E;
            _controller = _ctx.CreateController(_profile);

            // Detect which XInput slot the virtual landed on by injecting a
            // unique "sentinel" state via SubmitState and polling each slot
            // via XInputGetState. The slot that reflects the sentinel is
            // ours — independent of any slot reshuffling xinputhid may do.
            //
            // The simpler before/after bitmask approach can't distinguish
            // direction: when xinputhid receives a new Xbox-like device, it
            // may bump the existing real controller to a higher slot and
            // give slot 0 to the new one. The delta bitmask incorrectly
            // identifies the old slot as the "new" one, causing the filter
            // to filter the REAL device and keep the virtual.
            //
            // The sentinel value is chosen outside the typical stick
            // deadzone so real user input won't alias it: LeftStickX = 1.0f
            // (HID logical max on the virtual's descriptor → XInput sThumbLX
            // near +32767). After detection the state is immediately reset
            // to zero.
            IsConnected = true;
        }

        /// <summary>
        /// For Xbox-VID profiles, identify which XInput slot the virtual
        /// controller landed on by comparing packet numbers before and after
        /// creation. Called from InputManager.Step5 AFTER CreateVirtualController
        /// returns so the caller has already captured the before snapshot.
        /// </summary>
        public void DetectXInputSlotUsingBefore(uint[] beforePkt)
        {
            if (_profile.VendorId != 0x045E || beforePkt == null) return;

            // Give xinputhid a moment to settle its slot assignment. When a
            // second xinputhid-backed Xbox device arrives while another is
            // already on slot 0, xinputhid may briefly hold a transient
            // state before the final slot assignment stabilizes. Detecting
            // too early can latch onto the transient and point the filter
            // at the wrong slot.
            System.Threading.Thread.Sleep(500);

            // Sample each slot's packet number several times over ~500ms.
            // The virtual's packet count stays essentially flat (we haven't
            // submitted any state yet), while the real controller's count
            // climbs as the user moves the stick or just as XInput polls
            // the wireless link. The slot with the LOWEST delta between
            // the first and last sample — AND a packet number below the
            // real's before-value — is the virtual.
            var first = new uint[4];
            for (int s = 0; s < 4; s++)
                first[s] = XInputGetState(s, out var st0) == ERROR_SUCCESS
                    ? st0.dwPacketNumber : uint.MaxValue;

            System.Threading.Thread.Sleep(500);

            var last = new uint[4];
            for (int s = 0; s < 4; s++)
                last[s] = XInputGetState(s, out var st1) == ERROR_SUCCESS
                    ? st1.dwPacketNumber : uint.MaxValue;

            int? detected = null;
            long bestScore = long.MaxValue;
            for (int s = 0; s < 4; s++)
            {
                if (last[s] == uint.MaxValue) continue;   // empty slot
                // The virtual has low absolute packet count AND a very
                // small delta (near zero). A device receiving user input
                // has a large delta over 500ms.
                long delta = (long)last[s] - (long)first[s];
                long score = delta * 1_000_000 + (long)last[s];
                if (score < bestScore)
                {
                    bestScore = score;
                    detected = s;
                }
            }

            _detectedXInputSlot = detected;

            try { System.IO.File.AppendAllText(@"C:\PadForge\filter-debug.log",
                $"[{DateTime.Now:HH:mm:ss.fff}] DetectXInputSlotUsingBefore profile={_profile.Id} before=[{beforePkt[0]},{beforePkt[1]},{beforePkt[2]},{beforePkt[3]}] first=[{first[0]},{first[1]},{first[2]},{first[3]}] last=[{last[0]},{last[1]},{last[2]},{last[3]}] detected={detected?.ToString() ?? "null"}\n"); } catch { }
        }

        /// <summary>
        /// Snapshot XInput slot packet numbers. uint.MaxValue for empty slots.
        /// </summary>
        public static uint[] SnapshotXInputPackets()
        {
            var snap = new uint[4];
            for (int s = 0; s < 4; s++)
            {
                if (XInputGetState(s, out var st) == ERROR_SUCCESS)
                    snap[s] = st.dwPacketNumber;
                else
                    snap[s] = uint.MaxValue;
            }
            return snap;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

        private const int ERROR_SUCCESS = 0;


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
