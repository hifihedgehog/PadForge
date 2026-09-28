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

        /// <summary>Records the keys a registered row reports, in keyboard
        /// order. Many routes learn the exact model in their handshake, so the
        /// list is the row's rather than one the VID and PID alone could give.</summary>
        public static void SetKeyOrder(System.Guid instanceGuid, int[] keys)
        {
            if (keys != null) _keyOrders[instanceGuid] = keys;
        }

        /// <summary>The keys the input picker lists for an analog keyboard
        /// row: the ones its route reported when it last opened, else the
        /// catalog's list for its VID and PID.</summary>
        public static int[] KeysFor(PadForge.Engine.Data.UserDevice ud)
        {
            if (ud == null) return PadForge.Engine.Common.AnalogKeyboard.AnalogKeyCodes.FullKeyboard;
            return _keyOrders.TryGetValue(ud.InstanceGuid, out var keys)
                ? keys
                : PadForge.Engine.Common.AnalogKeyboard.AnalogKeyboardCatalog.KeysFor(ud.VendorId, ud.ProdId);
        }
    }
}
