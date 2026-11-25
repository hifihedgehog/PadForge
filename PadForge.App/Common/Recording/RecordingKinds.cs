using System;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Recording;

namespace PadForge.Common.Recording
{
    /// <summary>How a recording names a device's controls (#600): by the
    /// layout PadForge reads the device with right now.</summary>
    internal static class RecordingKinds
    {
        /// <summary>A keyboard's keys by their codes, a mouse's by its own
        /// words, a device read through SDL's gamepad layout by that layout,
        /// and any other device by its controls' numbers. A device that is
        /// no SDL device and reports itself as a gamepad uses the gamepad
        /// layout too, as Automation's rests do (AutomationDevices.RestOf).</summary>
        internal static RecordingControlKind Of(UserDevice device)
        {
            if (device == null) return RecordingControlKind.Generic;
            if (device.IsKeyboard) return RecordingControlKind.Keyboard;
            if (device.IsMouse) return RecordingControlKind.Mouse;
            var live = device.Device;
            bool gamepad = live is SdlDeviceWrapper wrapper
                ? wrapper.GamepadHandle != IntPtr.Zero && !device.ForceRawJoystickMode
                : device.CapType == InputDeviceType.Gamepad;
            return gamepad ? RecordingControlKind.Gamepad : RecordingControlKind.Generic;
        }

        /// <summary>The word a device's line says its controls are named
        /// by.</summary>
        internal static string Word(RecordingControlKind kind) => kind switch
        {
            RecordingControlKind.Gamepad => "gamepad",
            RecordingControlKind.Mouse => "mouse",
            RecordingControlKind.Keyboard => "keyboard",
            _ => "numbered",
        };
    }
}
