namespace PadForge.Engine
{
    /// <summary>
    /// A device with no HID report of its own to carry its feedback: what
    /// feeds it, a phone's page or a program on the Automation pipe (#600),
    /// draws its light and its player number and plays its trigger motors.
    /// PadForge hands each value to the device and the device passes it on.
    ///
    /// <para>The Lighting group, the identity pass and the dispatcher ask a
    /// device this interface, never its class, so a third family that
    /// relays its feedback joins them by implementing it. Trigger rumble
    /// reaches such a device through
    /// <see cref="ISdlInputDevice.SetTriggerRumble"/>, which the device
    /// answers itself in place of the gamepad handle it does not
    /// have.</para>
    /// </summary>
    public interface IRelayedFeedbackDevice
    {
        /// <summary>Whether the device shows a light bar, which the Lighting
        /// group then lets the user set.</summary>
        bool HasLightbar { get; }

        /// <summary>Whether that bar is a DualShock 4's, which the Lighting
        /// group previews with its own art.</summary>
        bool LightbarIsDs4 { get; }

        /// <summary>Whether the device shows a DualSense's player row and
        /// microphone light.</summary>
        bool HasIndicatorLeds { get; }

        /// <summary>The light's color. A device passes on a change and drops
        /// a repeat.</summary>
        void SetLed(byte r, byte g, byte b);

        /// <summary>The player number the device shows, from 1.</summary>
        void SetPlayerNumber(int number);
    }
}
