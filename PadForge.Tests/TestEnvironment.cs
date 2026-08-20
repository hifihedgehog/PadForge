using System;
using System.Runtime.CompilerServices;

namespace PadForge.Tests
{
    /// <summary>
    /// Clears bench-only environment variables before any test type loads.
    ///
    /// <para>PADFORGE_DIAG arms SdlDiagLog's file mirror from a static
    /// readonly field, and once armed that way SetMirror is a deliberate
    /// no-op for the rest of the process: the bench harness owns its file
    /// and the Diagnostics setting must not steal it. That is correct in the
    /// app and fatal in a test host, because a shell that exported the
    /// variable for one bench hands it to every dotnet test run afterwards,
    /// and SdlDiagLogMirrorTests then fails four ways with nothing wrong in
    /// the code. It has now cost two separate diagnoses.</para>
    ///
    /// <para>A module initializer runs at assembly load, before the first
    /// test touches SdlDiagLog and therefore before its static field reads
    /// the variable. Tests are hermetic from here regardless of what the
    /// shell was doing beforehand.</para>
    /// </summary>
    internal static class TestEnvironment
    {
        [ModuleInitializer]
        internal static void Clean()
        {
            try { Environment.SetEnvironmentVariable("PADFORGE_DIAG", null); }
            catch { }
        }
    }
}
