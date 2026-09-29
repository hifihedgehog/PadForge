using System;

namespace PadForge.Engine.Common.BlissBox
{
    /// <summary>
    /// What the engine needs to know about the App's Bliss-Box runtime
    /// (issue #469): whether it is on, and the hook that names a port's
    /// objects. Static so the engine carries no reference to the App.
    /// </summary>
    public static class BlissBoxApi
    {
        private static volatile bool _enabled;

        /// <summary>Set by the App when Read Bliss-Box Adapters is on.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>True when PadForge writes this port's motors through the
        /// adapter's own commands, so SDL's rumble must stay off it. SDL's
        /// DirectInput path averages both motors into one sine effect.</summary>
        public static bool OwnsRumble(ushort vendorId, ushort productId)
            => _enabled && BlissBoxProtocol.IsPort(vendorId, productId);

        /// <summary>True when PadForge reads this port through SDL's joystick
        /// calls rather than its gamepad mapping. The adapter's report follows
        /// whichever controller is plugged in, and the API names that
        /// controller's buttons, while the community mapping SDL carries for
        /// the adapter ("4Play Adapter") describes one fixed layout.</summary>
        public static bool ReadsRaw(ushort vendorId, ushort productId)
            => _enabled && BlissBoxProtocol.IsPort(vendorId, productId);

        /// <summary>Renames a port's objects for the controller in it and
        /// appends the pressure axes and arrow buttons. Takes the port's
        /// wrapper and its own list, returns the list to publish. App-wired,
        /// null until then.</summary>
        public static Func<ISdlInputDevice, DeviceObjectItem[], DeviceObjectItem[]> DeviceObjectsProvider;
    }
}
