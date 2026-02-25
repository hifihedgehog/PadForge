using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    internal sealed class DS4VirtualController : IVirtualController
    {
        private readonly IDualShock4Controller _controller;

        public VirtualControllerType Type => VirtualControllerType.DualShock4;
        public bool IsConnected { get; private set; }

        public DS4VirtualController(ViGEmClient client)
        {
            _controller = client.CreateDualShock4Controller();
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

        public void SubmitGamepadState(Gamepad gp)
        {
            // ── Face buttons ──
            _controller.SetButtonState(DualShock4Button.Cross, (gp.Buttons & Gamepad.A) != 0);
            _controller.SetButtonState(DualShock4Button.Circle, (gp.Buttons & Gamepad.B) != 0);
            _controller.SetButtonState(DualShock4Button.Square, (gp.Buttons & Gamepad.X) != 0);
            _controller.SetButtonState(DualShock4Button.Triangle, (gp.Buttons & Gamepad.Y) != 0);

            // ── Shoulders ──
            _controller.SetButtonState(DualShock4Button.ShoulderLeft, (gp.Buttons & Gamepad.LEFT_SHOULDER) != 0);
            _controller.SetButtonState(DualShock4Button.ShoulderRight, (gp.Buttons & Gamepad.RIGHT_SHOULDER) != 0);

            // ── Center buttons ──
            _controller.SetButtonState(DualShock4Button.Share, (gp.Buttons & Gamepad.BACK) != 0);
            _controller.SetButtonState(DualShock4Button.Options, (gp.Buttons & Gamepad.START) != 0);

            // ── Thumbstick clicks ──
            _controller.SetButtonState(DualShock4Button.ThumbLeft, (gp.Buttons & Gamepad.LEFT_THUMB) != 0);
            _controller.SetButtonState(DualShock4Button.ThumbRight, (gp.Buttons & Gamepad.RIGHT_THUMB) != 0);

            // ── Special buttons ──
            _controller.SetButtonState(DualShock4SpecialButton.Ps, (gp.Buttons & Gamepad.GUIDE) != 0);

            // ── Digital trigger buttons (pressed when analog trigger > 0) ──
            _controller.SetButtonState(DualShock4Button.TriggerLeft, gp.LeftTrigger > 0);
            _controller.SetButtonState(DualShock4Button.TriggerRight, gp.RightTrigger > 0);

            // ── D-Pad ──
            bool up = (gp.Buttons & Gamepad.DPAD_UP) != 0;
            bool down = (gp.Buttons & Gamepad.DPAD_DOWN) != 0;
            bool left = (gp.Buttons & Gamepad.DPAD_LEFT) != 0;
            bool right = (gp.Buttons & Gamepad.DPAD_RIGHT) != 0;
            _controller.SetDPadDirection(GetDPadDirection(up, down, left, right));

            // ── Axes ──
            // Xbox short (-32768..32767) → DS4 byte (0..255, center=128)
            // DS4 Y-axis: 0=up, 255=down (inverted from Xbox where positive=up)
            _controller.SetAxisValue(DualShock4Axis.LeftThumbX, ShortToByte(gp.ThumbLX));
            _controller.SetAxisValue(DualShock4Axis.LeftThumbY, ShortToByteInvertY(gp.ThumbLY));
            _controller.SetAxisValue(DualShock4Axis.RightThumbX, ShortToByte(gp.ThumbRX));
            _controller.SetAxisValue(DualShock4Axis.RightThumbY, ShortToByteInvertY(gp.ThumbRY));

            // ── Triggers (byte 0-255 direct) ──
            _controller.SetSliderValue(DualShock4Slider.LeftTrigger, gp.LeftTrigger);
            _controller.SetSliderValue(DualShock4Slider.RightTrigger, gp.RightTrigger);

            _controller.SubmitReport();
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            int capturedIndex = padIndex;
#pragma warning disable CS0618 // FeedbackReceived is obsolete but functional
            _controller.FeedbackReceived += (sender, args) =>
            {
                if (capturedIndex >= 0 && capturedIndex < vibrationStates.Length)
                {
                    ushort newL = (ushort)(args.LargeMotor * 257);
                    ushort newR = (ushort)(args.SmallMotor * 257);
                    ushort oldL = vibrationStates[capturedIndex].LeftMotorSpeed;
                    ushort oldR = vibrationStates[capturedIndex].RightMotorSpeed;

                    vibrationStates[capturedIndex].LeftMotorSpeed = newL;
                    vibrationStates[capturedIndex].RightMotorSpeed = newR;

                    if (newL != oldL || newR != oldR)
                        RumbleLogger.Log($"[ViGEm DS4] Pad{capturedIndex} feedback L:{oldL}->{newL} R:{oldR}->{newR}");
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

        /// <summary>Combines 4 D-pad booleans into a DS4 hat direction (supports diagonals).</summary>
        private static DualShock4DPadDirection GetDPadDirection(bool up, bool down, bool left, bool right)
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
    }
}
