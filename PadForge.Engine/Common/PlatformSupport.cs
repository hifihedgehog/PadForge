using System.Runtime.InteropServices;

namespace PadForge.Engine
{
    /// <summary>
    /// What this build can do on this machine, for the features whose
    /// native half does not exist for every architecture (4.5.1, ARM64).
    ///
    /// <para>Two different architectures decide two different kinds of
    /// feature, and mixing them up is the whole hazard here.</para>
    ///
    /// <para>A KERNEL DRIVER follows the MACHINE. A kernel driver cannot run
    /// emulated, so an x64 PadForge running under emulation on ARM64 Windows
    /// has to install the ARM64 driver, never the x64 one. Those features ask
    /// <see cref="RuntimeInformation.OSArchitecture"/>, which reports the
    /// real machine under emulation.</para>
    ///
    /// <para>An IN-PROCESS LIBRARY follows the PROCESS. An x64 DLL loads fine
    /// into an emulated x64 process on an ARM64 machine, and cannot load
    /// into a native ARM64 process at all. Those features ask
    /// <see cref="RuntimeInformation.ProcessArchitecture"/>.</para>
    ///
    /// <para>So HidHide works on both machines with the package built for
    /// each, Vosk works in both processes with the library built for each,
    /// and Sensa is the one the ARM64 build loses, because its vendor ships
    /// no ARM64 engine. Every rule is a pure function of the architecture it
    /// is handed, so each one is testable on a bench that has only one
    /// architecture to offer.</para>
    /// </summary>
    public static class PlatformSupport
    {
        /// <summary>The machine's real architecture, emulation or not.</summary>
        public static Architecture MachineArchitecture => RuntimeInformation.OSArchitecture;

        /// <summary>The architecture this process was built for and runs as.</summary>
        public static Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;

        /// <summary>True on ARM64 Windows, whichever build is running.</summary>
        public static bool IsArm64Machine => MachineArchitecture == Architecture.Arm64;

        // ── Kernel drivers: the machine decides ──────────────────────────

        // HidHide is a kernel filter driver, and upstream publishes a
        // Microsoft-signed package for each of the two machines PadForge
        // builds for (win-x64 and win-arm64, PadForge.App.csproj): the x64
        // MSI, and drivers/HidHide_ARM64.zip (driver 1.6.280.0) with a
        // documented manual install, which HidHideArm64Installer performs.
        // Upstream's SETUP is x64 only (nefarius/HidHide#57 tracks that).
        // The driver is not. So HidHide needs no availability rule, and
        // IsArm64Machine picks the package.

        // ── In-process libraries: the process decides ────────────────────

        /// <summary>The Vosk recognizer rides libvosk.dll. The Vosk package
        /// carries the x64 one, and the ARM64 build bundles one built from
        /// upstream's own unshipped Windows ARM64 recipe
        /// (tools/build-libvosk-arm64.sh). Any other process has none, and
        /// there VoiceMacroService stays on SAPI, which it falls back to
        /// whenever the Vosk model store is not ready.
        /// </summary>
        public static bool VoskAvailableOn(Architecture process)
            => process == Architecture.X64 || process == Architecture.Arm64;

        /// <inheritdoc cref="VoskAvailableOn"/>
        public static bool VoskAvailable => VoskAvailableOn(ProcessArchitecture);

        /// <summary>Razer Sensa HD haptics ride HAR.dll and its Razer
        /// provider. The Interhaptics SDK ships them for Win32 and x64, and
        /// PadForge bundles the x64 pair alone.
        /// </summary>
        public static bool SensaAvailableOn(Architecture process)
            => process == Architecture.X64;

        /// <inheritdoc cref="SensaAvailableOn"/>
        public static bool SensaAvailable => SensaAvailableOn(ProcessArchitecture);
    }
}
