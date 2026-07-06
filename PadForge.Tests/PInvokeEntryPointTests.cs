using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guards against a P/Invoke declaring an entry point that does not exist
    /// in the target DLL. That mistake compiles clean and only throws
    /// EntryPointNotFoundException the first time the method is called at
    /// runtime, so a typo in a rarely-hit path ships and crashes on the pump
    /// thread. Real case: RawInputListener declared "HidP_GetMaxUsageListLength"
    /// (the real hid.dll export is "HidP_MaxUsageListLength", no "Get"), which
    /// crashed the process the first time any Consumer Control (#168) device
    /// reported a usage.
    ///
    /// Marshal.PrelinkAll forces the runtime to resolve every [DllImport] entry
    /// point on the type using the SAME resolution logic real calls use
    /// (explicit EntryPoint override, CharSet name suffixing, ExactSpelling),
    /// so a bad name fails here exactly as it would in production, minus the
    /// crash. Only types whose native libraries are present on the test host
    /// are prelinked; hid.dll / user32.dll / kernel32.dll always are on Windows.
    /// </summary>
    public class PInvokeEntryPointTests
    {
        [Theory]
        [InlineData(typeof(PadForge.Engine.RawInputListener))]
        public void AllDeclaredEntryPointsResolve(Type type)
        {
            // Throws EntryPointNotFoundException (or DllNotFoundException) if any
            // [DllImport] on the type names an export the DLL does not have.
            var ex = Record.Exception(() => Marshal.PrelinkAll(type));
            Assert.Null(ex);
        }

        [Fact]
        public void HidPMaxUsageListLength_ResolvesToRealExport()
        {
            // Pin the specific regression: the method must resolve to a live
            // hid.dll export. PrelinkAll covers this, but naming it keeps the
            // fixed bug documented and green.
            var mi = typeof(PadForge.Engine.RawInputListener).GetMethod(
                "HidP_MaxUsageListLength",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(mi);
            var ex = Record.Exception(() => Marshal.Prelink(mi));
            Assert.Null(ex);
        }
    }
}
