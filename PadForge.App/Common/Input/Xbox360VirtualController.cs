using System;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    internal sealed class Xbox360VirtualController : IVirtualController
    {
        private readonly IXbox360Controller _controller;
        private bool _disposed;

        public VirtualControllerType Type => VirtualControllerType.Xbox360;
        public bool IsConnected { get; private set; }

        public Xbox360VirtualController(ViGEmClient client)
        {
            _controller = client.CreateXbox360Controller();
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

        public void SubmitGamepadState(Gamepad gp)
        {
            _controller.SetButtonState(Xbox360Button.A, (gp.Buttons & Gamepad.A) != 0);
            _controller.SetButtonState(Xbox360Button.B, (gp.Buttons & Gamepad.B) != 0);
            _controller.SetButtonState(Xbox360Button.X, (gp.Buttons & Gamepad.X) != 0);
            _controller.SetButtonState(Xbox360Button.Y, (gp.Buttons & Gamepad.Y) != 0);
            _controller.SetButtonState(Xbox360Button.LeftShoulder, (gp.Buttons & Gamepad.LEFT_SHOULDER) != 0);
            _controller.SetButtonState(Xbox360Button.RightShoulder, (gp.Buttons & Gamepad.RIGHT_SHOULDER) != 0);
            _controller.SetButtonState(Xbox360Button.Back, (gp.Buttons & Gamepad.BACK) != 0);
            _controller.SetButtonState(Xbox360Button.Start, (gp.Buttons & Gamepad.START) != 0);
            _controller.SetButtonState(Xbox360Button.LeftThumb, (gp.Buttons & Gamepad.LEFT_THUMB) != 0);
            _controller.SetButtonState(Xbox360Button.RightThumb, (gp.Buttons & Gamepad.RIGHT_THUMB) != 0);
            _controller.SetButtonState(Xbox360Button.Guide, (gp.Buttons & Gamepad.GUIDE) != 0);
            _controller.SetButtonState(Xbox360Button.Up, (gp.Buttons & Gamepad.DPAD_UP) != 0);
            _controller.SetButtonState(Xbox360Button.Down, (gp.Buttons & Gamepad.DPAD_DOWN) != 0);
            _controller.SetButtonState(Xbox360Button.Left, (gp.Buttons & Gamepad.DPAD_LEFT) != 0);
            _controller.SetButtonState(Xbox360Button.Right, (gp.Buttons & Gamepad.DPAD_RIGHT) != 0);

            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, gp.ThumbLX);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, gp.ThumbLY);
            _controller.SetAxisValue(Xbox360Axis.RightThumbX, gp.ThumbRX);
            _controller.SetAxisValue(Xbox360Axis.RightThumbY, gp.ThumbRY);

            _controller.SetSliderValue(Xbox360Slider.LeftTrigger, gp.LeftTrigger);
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, gp.RightTrigger);

            _controller.SubmitReport();
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            int capturedIndex = padIndex;
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
                        RumbleLogger.Log($"[ViGEm Xbox] Pad{capturedIndex} feedback L:{oldL}->{newL} R:{oldR}->{newR}");
                }
            };
        }
    }
}
