using System.Runtime.CompilerServices;
using PadForge.Common.Input;

namespace PadForge.Tests
{
    /// <summary>The settings statics hold real collections from the test
    /// host's start, as the app's do from its own. Test classes in other
    /// collections save and restore them around their tests, and a class that
    /// saved the null they start as restored it in the middle of another
    /// collection's test, which then failed reading it (2026-11-04,
    /// SlotDeleteGhostMappingTests, a NullReferenceException at
    /// SettingsService's lock on UserSettings, right after that test had set
    /// it).</summary>
    internal static class SettingsStaticsStart
    {
        [ModuleInitializer]
        internal static void Set()
        {
            SettingsManager.UserDevices ??= new DeviceCollection();
            SettingsManager.UserSettings ??= new SettingsCollection();
        }
    }
}
