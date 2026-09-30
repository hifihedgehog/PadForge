namespace PadForge.Common.Input
{
    /// <summary>
    /// The Settings mirror the engine reads for analog keyboards (issue
    /// #468), the <see cref="LogitechGKeysRuntime"/> shape: static, written by
    /// <c>SettingsViewModel.AnalogKeyboardsEnabled</c> on the UI thread, read
    /// by the poll thread's device sweep.
    ///
    /// <para>Off by default. The polled families talk over the channel their
    /// vendors' configurators use, VIA for the Keychron and Lemokey boards
    /// among them, and nobody who has not asked for it should have PadForge
    /// polling there.</para>
    /// </summary>
    internal static class AnalogKeyboardRuntime
    {
        private static volatile bool _enabled;

        /// <summary>Whether analog keyboards are read. Turning reading back
        /// on starts a new MCHOSE Mix 87 generation, which allows its one
        /// automatic enable again, HallJoy's resume after a pause.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (value && !_enabled)
                    PadForge.Engine.Common.AnalogKeyboard.MchoseMix87Session.BeginGeneration();
                _enabled = value;
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Guid, int[]> _keyOrders = new();

        /// <summary>A row's key list changed: its route's list published at
        /// open, a key it reported that the list lacked, or, raised by
        /// InputService, a Remote Link owner's list for a peer's copy. The
        /// input pickers and the Devices preview are built from the list, so
        /// they rebuild on it, the fan-out a learned handheld button gets.</summary>
        public static event System.EventHandler KeyOrdersChanged;

        /// <summary>Records the keys a registered row reports, in keyboard
        /// order. Many routes learn the exact model in their handshake, so the
        /// list is the row's rather than one the VID and PID alone could give.</summary>
        public static void SetKeyOrder(System.Guid instanceGuid, int[] keys)
        {
            if (keys == null) return;
            bool changed = !_keyOrders.TryGetValue(instanceGuid, out var old)
                           || !System.MemoryExtensions.SequenceEqual<int>(old, keys);
            _keyOrders[instanceGuid] = keys;
            if (changed) NotifyKeyOrdersChanged();
        }

        public static void NotifyKeyOrdersChanged() => KeyOrdersChanged?.Invoke(null, System.EventArgs.Empty);

        /// <summary>The keys the input picker lists for an analog keyboard
        /// row: the ones its route reported when it last opened, else, for a
        /// Remote Link peer's copy, the owner's list (the device list's v9
        /// tail), else the catalog's list for its VID and PID.</summary>
        public static int[] KeysFor(PadForge.Engine.Data.UserDevice ud)
        {
            if (ud == null) return PadForge.Engine.Common.AnalogKeyboard.AnalogKeyCodes.FullKeyboard;
            if (_keyOrders.TryGetValue(ud.InstanceGuid, out var keys)) return keys;
            if (ud.Device is PadForge.Engine.RemoteLink.RemotePeerDevice { Info.AnalogKeyOrder: { } owners })
                return owners;
            return PadForge.Engine.Common.AnalogKeyboard.AnalogKeyboardCatalog.KeysFor(ud.VendorId, ud.ProdId);
        }
    }
}
