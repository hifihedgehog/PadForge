using System;
using HIDMaestro;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The one place PadForge makes a HIDMaestro context or calls the SDK's
    /// static sweep, removal and backend reads. Making a context is a
    /// write to the PC: in an elevated process on a PC whose driver is not
    /// this build's, the constructor's warm-up takes the process's owner
    /// claim, which writes a record under HKLM\SOFTWARE\HIDMaestroOwners,
    /// and stages the driver payload under %ProgramFiles%\HIDMaestro\Staging
    /// (HIDMaestro 2.4.0 HMContext's constructor, MachineClaims.TryEnsureOwner
    /// and DriverBuilder.EnsureExtracted).
    /// </summary>
    internal static class HMaestroContexts
    {
        /// <summary>Set by the test host for its whole life, false in
        /// production. While it is set, no context that can install the
        /// driver or create a controller is made, and the profile catalog's
        /// context is a USB/IP one, whose constructor takes no owner claim
        /// and stages nothing.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam for the constructor, null in production. The
        /// test of the guard records here which context would have been
        /// made, so that test never makes a real one. The setting thread's
        /// alone, so a catalog built by a test beside it is made as it would
        /// be.</summary>
        [ThreadStatic]
        internal static Func<HMBackend, HMContext> MakeSeam;

        /// <summary>A context for the engine, which installs the driver and
        /// creates controllers through it. Throws in the test host.</summary>
        internal static HMContext ForDevices()
        {
            if (OffLimitsForTest) throw new InvalidOperationException("The test host makes no HIDMaestro context for devices.");
            return Make(HMBackend.Auto);
        }

        /// <summary>A context for reading profiles only: the built-in
        /// catalog and the user's imported files. In production it is the
        /// context the default constructor makes. In the test host it is a
        /// USB/IP one, which reads the same profiles.</summary>
        internal static HMContext ForProfiles()
            => Make(OffLimitsForTest ? HMBackend.Usbip : HMBackend.Auto);

        private static HMContext Make(HMBackend backend)
        {
            var seam = MakeSeam;
            return seam != null ? seam(backend) : new HMContext(backend);
        }

        /// <summary>Test seam for the SDK's static calls below, null in
        /// production. The test of their guards records here the call that
        /// would have been made, so that test makes none. The setting
        /// thread's alone.</summary>
        [ThreadStatic]
        internal static Action<string> CallSeam;

        /// <summary>HIDMaestro's sweep of the virtual controllers that
        /// programs which have ended left on the PC, its install kept
        /// (HMContext.RemoveAllVirtualControllers). Does nothing in the test
        /// host.</summary>
        internal static void SweepEndedPrograms()
        {
            if (OffLimitsForTest) return;
            if (Seamed("RemoveAllVirtualControllers")) return;
            HMContext.RemoveAllVirtualControllers(preserveInstall: true);
        }

        /// <summary>HIDMaestro's removal of itself from the PC
        /// (HMUninstall.Run). Null in the test host.</summary>
        internal static HMUninstallReport Uninstall(HMUninstallOptions options)
        {
            if (OffLimitsForTest) return null;
            if (Seamed("HMUninstall.Run")) return null;
            return HMUninstall.Run(options);
        }

        /// <summary>The backend HIDMaestro picks on this PC
        /// (HMContext.MachineBackend). Throws in the test host.</summary>
        internal static HMBackend MachineBackend()
        {
            if (OffLimitsForTest) throw new InvalidOperationException("The test host reads no HIDMaestro backend.");
            if (Seamed("MachineBackend")) return HMBackend.Auto;
            return HMContext.MachineBackend;
        }

        /// <summary>True while usbip-win2's host controller is on this PC
        /// (HMContext.IsUsbipBackendAvailable). Throws in the test
        /// host.</summary>
        internal static bool UsbipBackendAvailable()
        {
            if (OffLimitsForTest) throw new InvalidOperationException("The test host reads no HIDMaestro backend.");
            if (Seamed("IsUsbipBackendAvailable")) return false;
            return HMContext.IsUsbipBackendAvailable;
        }

        private static bool Seamed(string call)
        {
            var seam = CallSeam;
            if (seam == null) return false;
            seam(call);
            return true;
        }
    }
}
