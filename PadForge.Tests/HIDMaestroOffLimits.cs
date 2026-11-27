using System.Runtime.CompilerServices;
using PadForge.Common.Input;

namespace PadForge.Tests
{
    /// <summary>
    /// Keeps the test process from writing to this PC through HIDMaestro
    /// for its whole life.
    /// </summary>
    /// <remarks>
    /// Making a HIDMaestro context is itself a write: in an elevated test
    /// host on a PC whose driver is not this build's, the constructor takes
    /// an owner claim, which writes a record under
    /// HKLM\SOFTWARE\HIDMaestroOwners that outlives the test host, and
    /// stages the driver under %ProgramFiles%\HIDMaestro\Staging. The
    /// profile catalog makes one in nearly every test run. With this set,
    /// a context for devices refuses in <see cref="HMaestroContexts"/>, the
    /// one place the application makes a context, and the catalog's
    /// context is a USB/IP one, which claims and stages nothing. Nothing in
    /// the application sets it (MachineOffLimitsTests).
    /// </remarks>
    internal static class HIDMaestroOffLimits
    {
#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Set() => HMaestroContexts.OffLimitsForTest = true;
#pragma warning restore CA2255
    }
}
