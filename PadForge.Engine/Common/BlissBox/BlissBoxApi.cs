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

        /// <summary>Renames a port's objects for the controller in it and
        /// appends the pressure axes and arrow buttons. Takes the port's
        /// wrapper and its own list, returns the list to publish. App-wired,
        /// null until then.</summary>
        public static Func<ISdlInputDevice, DeviceObjectItem[], DeviceObjectItem[]> DeviceObjectsProvider;
    }
}
