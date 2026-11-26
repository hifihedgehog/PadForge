using System;
using System.Diagnostics;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// The app's own part of <see cref="RightsVariables"/>: a program started
    /// through ProcessStartInfo with PadForge's rights gets the variables the
    /// allow list builds, never a copy of PadForge's (#600).
    /// </summary>
    internal static partial class RightsVariables
    {
        /// <summary>Test seam: takes the start, with its variables already
        /// given, in place of Windows. Null in production.</summary>
        internal static Func<ProcessStartInfo, Process> StartSeam;

        /// <summary>
        /// Starts <paramref name="psi"/> with the variables
        /// <see cref="Give"/> sets. Every program PadForge starts with its
        /// rights through ProcessStartInfo starts here. A start through the
        /// shell (UseShellExecute) cannot be given variables, so two kinds
        /// pass here unchanged: the runas start of a process without rights,
        /// whose variables Windows builds after its prompt, and the updated
        /// PadForge the update helper starts, which takes the helper's, built
        /// here when the app started the helper.
        /// </summary>
        internal static Process Start(ProcessStartInfo psi)
        {
            Give(psi);
            var seam = StartSeam;
            return seam != null ? seam(psi) : Process.Start(psi);
        }

        /// <summary>
        /// Sets the variables <paramref name="psi"/> starts with to those
        /// <see cref="ForChild"/> builds, with the protected unpack folder,
        /// in place of the copy of this process's that ProcessStartInfo
        /// starts from. A start through the shell is left as it is, since
        /// ProcessStartInfo refuses variables for such a start.
        /// When this process's variables cannot be read, the start gets none
        /// of them but the machine's.
        /// </summary>
        internal static void Give(ProcessStartInfo psi)
        {
            if (psi == null || psi.UseShellExecute) return;
            var lines = ForChild(ProtectedFolder.Natives) ?? Build(System32Calls.MachineVariables(), null, ProtectedFolder.Natives);
            var variables = psi.Environment;
            variables.Clear();
            foreach (string line in lines)
            {
                string name = ProtectedStart.NameOf(line);
                variables[name] = line.Substring(name.Length + 1);
            }
        }
    }
}
