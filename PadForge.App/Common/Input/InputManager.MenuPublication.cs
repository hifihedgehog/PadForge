using System;
using System.Threading;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // Acquire before device, settings, or shift-runtime locks.
        // A frame keeps this gate through key and virtual-controller output.
        internal static readonly object MenuPublicationSync = new();

        internal static bool MenuDataLockHeldByCurrentThread
        {
            get
            {
                var settings = SettingsManager.UserSettings?.SyncRoot;
                var devices = SettingsManager.UserDevices?.SyncRoot;
                return (settings != null && Monitor.IsEntered(settings))
                    || (devices != null && Monitor.IsEntered(devices));
            }
        }

        internal bool MenuLifecycleLockHeldByCurrentThread => Monitor.IsEntered(_vcLifecycleLock);

        internal static MenuPublicationScope EnterMenuPublication() => new();

        internal readonly struct MenuPublicationScope : IDisposable
        {
            public MenuPublicationScope()
            {
                if (!Monitor.IsEntered(MenuPublicationSync) && MenuDataLockHeldByCurrentThread)
                    throw new InvalidOperationException("Acquire menu publication before device and settings locks.");
                Monitor.Enter(MenuPublicationSync);
            }
            public void Dispose() => Monitor.Exit(MenuPublicationSync);
        }
    }
}
