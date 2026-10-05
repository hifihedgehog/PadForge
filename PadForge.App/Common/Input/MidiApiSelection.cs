using System;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>Which Windows MIDI Services API PadForge drives.</summary>
    internal enum MidiApiKind
    {
        /// <summary>Neither API is available on this PC.</summary>
        None = 0,

        /// <summary>The API built into Windows (Windows.Devices.Midi2),
        /// registered in System32 from the late-November 2026 update for
        /// Windows 11 25H2.</summary>
        InBox = 1,

        /// <summary>The older Windows MIDI Services App SDK runtime
        /// (Microsoft.Windows.Devices.Midi2 1.0.16-rc.3.7), on a PC that still
        /// has Microsoft's separate "Windows MIDI Services Runtime and Tools"
        /// install. Microsoft deleted those installers on 2026-10-01, so this
        /// path serves only PCs that already have it. It is the only path on
        /// Windows 11 24H2, which the in-box API does not cover.</summary>
        AppSdk = 2,
    }

    /// <summary>
    /// Picks the Windows MIDI Services API. The in-box API is tried first.
    /// When its classes are not registered (REGDB_E_CLASSNOTREG) and the
    /// older runtime is installed, the older runtime is used. Microsoft gave
    /// the in-box API new namespaces and class IDs so the two can sit side by
    /// side (In-box Preview 1 release notes). The in-box path is gated at
    /// Windows 11 25H2 (build 26200), the older runtime at 24H2 (26100).
    /// </summary>
    internal static class MidiApiSelection
    {
        internal const int InBoxMinBuild = 26200;
        internal const int AppSdkMinBuild = 26100;
        internal const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

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
        /// as REGDB_E_CLASSNOTREG.</summary>
        internal static MidiApiKind PredictForUi(int osBuild, bool inBoxRegistered, bool appSdkRuntimeInstalled)
            => Choose(osBuild, () => inBoxRegistered ? 0 : REGDB_E_CLASSNOTREG, () => appSdkRuntimeInstalled);

        /// <summary>What the Settings card shows. The registry's
        /// <paramref name="predicted"/> answer stands until the engine's
        /// probe has run, then the probe's answer does. A failed probe for an
        /// API the registry says is present reads as not started: the in-box
        /// EnsureServiceAvailable returns false in Legacy API mode (Microsoft's
        /// MidiApi reference), and a stopped or wedged service fails the same
        /// way. A failed probe with no API present still reads as missing.</summary>
        internal static (MidiApiKind Api, bool NotStarted) ForCard(
            MidiApiKind predicted, MidiApiKind engineActive, bool engineProbeFailed)
        {
            if (engineActive != MidiApiKind.None) return (engineActive, false);
            if (engineProbeFailed && predicted != MidiApiKind.None) return (MidiApiKind.None, true);
            return (predicted, false);
        }

        internal static int OsBuild => Environment.OSVersion.Version.Build;

        /// <summary>Whether the older runtime's install is present. Reads the
        /// "Windows MIDI Services Runtime and Tools" uninstall entry, which
        /// the in-box API never creates.</summary>
        internal static bool IsAppSdkRuntimeInstalled() => DriverInstaller.IsMidiRuntimeInstalled();

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

        /// <summary>The UI's answer for this PC, given whether the older
        /// runtime is installed (the caller reads that once per refresh).</summary>
        internal static MidiApiKind PredictForUi(bool appSdkRuntimeInstalled)
            => PredictForUi(OsBuild, IsInBoxRegistered(), appSdkRuntimeInstalled);

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
