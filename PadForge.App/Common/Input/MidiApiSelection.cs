using System;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>Which MIDI API PadForge drives.</summary>
    internal enum MidiApiKind
    {
        /// <summary>No MIDI API started on this PC.</summary>
        None = 0,

        /// <summary>The API built into Windows (Windows.Devices.Midi2),
        /// registered in System32 from the late-November 2026 update for
        /// Windows 11 25H2.</summary>
        InBox = 1,

        /// <summary>The Windows MIDI Services App SDK runtime
        /// (Microsoft.Windows.Devices.Midi2, compiled against 1.0.16-rc.3.7):
        /// Microsoft's own install where a PC still has one, or PadForge's
        /// build of the same runtime from hifihedgehog/PadForge-MIDI-Runtime.
        /// It is the Windows MIDI Services path on Windows 11 24H2, which the
        /// in-box API does not cover.</summary>
        AppSdk = 2,

        /// <summary>The legacy Windows MIDI API (WinMM), for a PC where
        /// neither Windows MIDI Services API runs: Windows before 24H2, Legacy
        /// API mode, or a 24H2 or later PC with no runtime installed. It
        /// cannot create a port, so each MIDI slot sends to an existing port
        /// the user picks (<see cref="MidiBackendLegacy"/>).</summary>
        Legacy = 3,
    }

    /// <summary>
    /// Picks the Windows MIDI Services API. The in-box API is tried first.
    /// When its classes are not registered (REGDB_E_CLASSNOTREG) and the
    /// App SDK runtime is installed, the runtime is used. Microsoft gave the
    /// in-box API new namespaces and class IDs so the two can sit side by
    /// side (In-box Preview 1 release notes). The in-box path is gated at
    /// Windows 11 25H2 (build 26200), the runtime at 24H2 (26100). Where
    /// neither starts, <see cref="MidiVirtualController"/> falls back to the
    /// legacy API.
    /// </summary>
    internal static class MidiApiSelection
    {
        internal const int InBoxMinBuild = 26200;
        internal const int AppSdkMinBuild = 26100;
        internal const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

        /// <summary>Where Windows records the MIDI API mode (Microsoft's
        /// "Windows MIDI Services vs Legacy API Mode Switch" article): a DWORD
        /// of 0 for full Windows MIDI Services, 1 for Legacy API mode, 2 for
        /// Hybrid. It takes effect at the next restart.</summary>
        internal const string ApiModeKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32";
        internal const string ApiModeValue = "UseLegacyMidi";

        /// <summary>The class the in-box probe activates. Its activation
        /// factory exists exactly when Windows registers the API.</summary>
        internal const string InBoxProbeClass = "Windows.Devices.Midi2.MidiApi";

        /// <summary>The rule. <paramref name="inBoxActivationHr"/> returns the
        /// HRESULT of activating the in-box API, and
        /// <paramref name="appSdkRuntimeInstalled"/> whether the older runtime
        /// is installed. Each is asked only when its gate admits it. An
        /// activation failure other than an unregistered class stops here:
        /// the in-box API is present but broken, and swapping in the older
        /// runtime would hide that.</summary>
        internal static MidiApiKind Choose(int osBuild, Func<int> inBoxActivationHr, Func<bool> appSdkRuntimeInstalled)
        {
            if (osBuild >= InBoxMinBuild)
            {
                int hr = inBoxActivationHr();
                if (hr >= 0) return MidiApiKind.InBox;
                if (hr != REGDB_E_CLASSNOTREG) return MidiApiKind.None;
            }
            if (osBuild >= AppSdkMinBuild && appSdkRuntimeInstalled())
                return MidiApiKind.AppSdk;
            return MidiApiKind.None;
        }

        /// <summary>The same rule for the UI thread, from registration alone:
        /// a registered in-box class reads as activating, an unregistered one
        /// as REGDB_E_CLASSNOTREG. Legacy API mode stops the MIDI service, so
        /// both Windows MIDI Services APIs fail to start there and the engine
        /// takes the legacy API, as it does where neither API is
        /// present.</summary>
        internal static MidiApiKind PredictForUi(int osBuild, bool inBoxRegistered, bool appSdkRuntimeInstalled, bool legacyApiMode)
        {
            if (osBuild >= AppSdkMinBuild && legacyApiMode) return MidiApiKind.Legacy;
            var api = Choose(osBuild, () => inBoxRegistered ? 0 : REGDB_E_CLASSNOTREG, () => appSdkRuntimeInstalled);
            return api == MidiApiKind.None ? MidiApiKind.Legacy : api;
        }

        /// <summary>Whether the Settings card offers to install the App SDK
        /// runtime: Windows 11 24H2 or later, Windows MIDI Services on, no
        /// in-box API and no runtime yet. In Legacy API mode the service does
        /// not run, so the runtime would not start until the mode
        /// changes.</summary>
        internal static bool CanOfferRuntimeInstall(int osBuild, bool inBoxRegistered, bool legacyApiMode, bool appSdkRuntimeInstalled)
            => osBuild >= AppSdkMinBuild && !inBoxRegistered && !legacyApiMode && !appSdkRuntimeInstalled;

        /// <summary>What the Settings card shows. The registry's
        /// <paramref name="predicted"/> answer stands until the engine's
        /// probe has run, then the probe's answer does. A probe that found
        /// no API it could start, the legacy one included, or that timed out
        /// on a stuck service, reads as not started.</summary>
        internal static (MidiApiKind Api, bool NotStarted) ForCard(
            MidiApiKind predicted, MidiApiKind engineActive, bool engineProbeFailed)
        {
            if (engineActive != MidiApiKind.None) return (engineActive, false);
            if (engineProbeFailed && predicted != MidiApiKind.None) return (MidiApiKind.None, true);
            return (predicted, false);
        }

        internal static int OsBuild => Environment.OSVersion.Version.Build;

        /// <summary>Whether an App SDK runtime install is present, Microsoft's
        /// or PadForge's build (<see cref="DriverInstaller.IsMidiRuntimeInstalled"/>).
        /// The in-box API never creates either.</summary>
        internal static bool IsAppSdkRuntimeInstalled() => DriverInstaller.IsMidiRuntimeInstalled();

        /// <summary>Whether Windows is set to Legacy API mode.</summary>
        internal static bool IsLegacyApiMode()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ApiModeKey);
                return key?.GetValue(ApiModeValue) is int mode && mode == 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Whether Windows registers the in-box API, read from the
        /// registry so the UI thread never activates anything. Windows lists
        /// every in-box WinRT class under ActivatableClassId with the System32
        /// DLL that serves it.</summary>
        internal static bool IsInBoxRegistered()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\WindowsRuntime\ActivatableClassId\" + InBoxProbeClass);
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The UI's answer for this PC, given whether the App SDK
        /// runtime is installed (the caller reads that once per refresh).</summary>
        internal static MidiApiKind PredictForUi(bool appSdkRuntimeInstalled)
            => PredictForUi(OsBuild, IsInBoxRegistered(), appSdkRuntimeInstalled, IsLegacyApiMode());

        /// <summary><see cref="CanOfferRuntimeInstall(int, bool, bool, bool)"/>
        /// for this PC.</summary>
        internal static bool CanOfferRuntimeInstall(bool appSdkRuntimeInstalled)
            => CanOfferRuntimeInstall(OsBuild, IsInBoxRegistered(), IsLegacyApiMode(), appSdkRuntimeInstalled);

        /// <summary>
        /// Activates the in-box API's factory through RoGetActivationFactory
        /// and returns the HRESULT. PadForge ships none of Microsoft's API
        /// binaries, so only the copy Windows registers can answer. The
        /// projection's own activation is not used for this probe: when
        /// RoGetActivationFactory fails, C#/WinRT goes on to load a DLL named
        /// after the namespace through the normal search path
        /// (WinRT.Runtime ActivationFactory.Get, 2.2.0), which could pick up a
        /// preview copy from another program's folder.
        /// </summary>
        internal static int ProbeInBoxActivation() => ProbeActivation(InBoxProbeClass);

        /// <summary>The probe for any class name. Internal for the test seam,
        /// which checks it against a class every Windows registers and one no
        /// Windows does.</summary>
        internal static int ProbeActivation(string activatableClassId)
        {
            EnsureMta();
            IntPtr hstring = IntPtr.Zero;
            IntPtr factory = IntPtr.Zero;
            try
            {
                int hr = WindowsCreateString(activatableClassId, (uint)activatableClassId.Length, out hstring);
                if (hr < 0) return hr;
                Guid iid = IID_IActivationFactory;
                hr = RoGetActivationFactory(hstring, ref iid, out factory);
                return hr;
            }
            catch (DllNotFoundException)
            {
                return REGDB_E_CLASSNOTREG;
            }
            catch (EntryPointNotFoundException)
            {
                return REGDB_E_CLASSNOTREG;
            }
            finally
            {
                if (factory != IntPtr.Zero) Marshal.Release(factory);
                if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
            }
        }

        // The multithreaded apartment stays alive for the process, the way
        // C#/WinRT's WinRTModule holds one (CoIncrementMTAUsage, released
        // never), so the probe can run on any thread.
        private static readonly object s_mtaLock = new();
        private static bool s_mtaHeld;
        private static void EnsureMta()
        {
            lock (s_mtaLock)
            {
                if (s_mtaHeld) return;
                if (CoIncrementMTAUsage(out _) >= 0) s_mtaHeld = true;
            }
        }

        private static readonly Guid IID_IActivationFactory = new("00000035-0000-0000-C000-000000000046");

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int CoIncrementMTAUsage(out IntPtr cookie);
    }
}
