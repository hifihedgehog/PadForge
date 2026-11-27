using System;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        /// <summary>The process's power throttling under the PADFORGE_POLL_QOS
        /// bench switch: opted out (unset, the shipping behavior), left to
        /// Windows (off), or both by turns, one heartbeat window each
        /// (flip).</summary>
        internal enum PollQosMode { High, Default, Flip }

        internal static PollQosMode ParsePollQos(string value)
        {
            if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)) return PollQosMode.Default;
            if (string.Equals(value, "flip", StringComparison.OrdinalIgnoreCase)) return PollQosMode.Flip;
            return PollQosMode.High;
        }

        private static readonly PollQosMode s_pollQos = ParsePollQos(Environment.GetEnvironmentVariable("PADFORGE_POLL_QOS"));

        // Whether the switch is set at all: unset, the heartbeat says
        // nothing of the opt-out.
        private static readonly bool s_pollQosSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PADFORGE_POLL_QOS"));

        // Poll thread only.
        private PollQosMode _qosMode;
        private bool _qosHigh;
        private bool _qosFailed;

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessPowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        internal const uint ProcessPowerThrottlingCurrentVersion = 1;
        internal const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
        internal const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;
        internal const int ProcessPowerThrottlingClass = 4;

        /// <summary>The state SetProcessInformation takes. High: both
        /// mechanisms in the control mask and clear in the state mask, so
        /// Windows neither slows the process's threads (EcoQoS) nor ignores
        /// its timer resolution requests while its window is hidden or
        /// minimized. Default: neither mask set, which hands both back to
        /// Windows. A Windows that refuses the pair is asked for execution
        /// speed alone (<see cref="SetOptOut"/>).</summary>
        internal static ProcessPowerThrottlingState PowerThrottlingFor(bool high) => new()
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = high ? ProcessPowerThrottlingExecutionSpeed | ProcessPowerThrottlingIgnoreTimerResolution : 0,
            StateMask = 0,
        };

        /// <summary>Sets the process's throttling. False, with the error,
        /// when Windows refuses.</summary>
        internal static bool SetPowerThrottling(bool high, out int error)
            => SetPowerThrottlingMask(PowerThrottlingFor(high).ControlMask, out error);

        /// <summary>Sets the process's throttling with
        /// <paramref name="controlMask"/> in the control mask and the state
        /// mask clear. False, with the error, when Windows refuses.</summary>
        internal static bool SetPowerThrottlingMask(uint controlMask, out int error)
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion, ControlMask = controlMask, StateMask = 0,
            };
            bool ok = SetProcessInformation(new IntPtr(-1), ProcessPowerThrottlingClass, ref state,
                (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }

        /// <summary>Sets the opt-out, or the default, through
        /// <paramref name="set"/>, which takes a control mask and says
        /// whether Windows took it. A Windows that refuses the pair is asked
        /// once more for execution speed alone, the one Windows 10 knows
        /// (timer resolution is Windows 11's). The mask Windows took, or
        /// null when it took none.</summary>
        internal static uint? SetOptOut(bool high, Func<uint, bool> set)
        {
            uint mask = PowerThrottlingFor(high).ControlMask;
            if (set(mask)) return mask;
            if (high && set(ProcessPowerThrottlingExecutionSpeed)) return ProcessPowerThrottlingExecutionSpeed;
            return null;
        }

        /// <summary>The process's throttling masks as Windows reports them,
        /// "control/state" in hex, or "?" when it does not say.</summary>
        internal static string PowerThrottlingMasks()
        {
            try
            {
                var state = new ProcessPowerThrottlingState { Version = ProcessPowerThrottlingCurrentVersion };
                return GetProcessInformation(new IntPtr(-1), ProcessPowerThrottlingClass, ref state,
                        (uint)Marshal.SizeOf<ProcessPowerThrottlingState>())
                    ? state.ControlMask.ToString("X") + "/" + state.StateMask.ToString("X")
                    : "?";
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>At the loop's start, on the poll thread. The process is
        /// opted out of throttling unless the switch says off, here rather
        /// than at process start because it is the poll loop's timer and
        /// passes that Windows would slow, and each start sets it again
        /// after anything that changed it.</summary>
        private void BeginPollQos()
        {
            _qosMode = s_pollQos;
            ApplyPollQos(_qosMode != PollQosMode.Default);
        }

        private void ApplyPollQos(bool high)
        {
            _qosHigh = high;
            if (_qosFailed) return;
            try
            {
                int error = 0;
                if (SetOptOut(high, mask => SetPowerThrottlingMask(mask, out error)) != null) return;
                _qosFailed = true;
                Engine.SdlDiagLog.WriteLine($"POLL QOS throttling not set, error {error}; Windows manages it");
            }
            catch (Exception ex)
            {
                _qosFailed = true;
                Engine.SdlDiagLog.WriteLine("POLL QOS throttling not set: " + ex.Message);
            }
        }

        /// <summary>The heartbeat's words for the window it reports, under
        /// the switch: the mode and the masks Windows reports now. Nothing
        /// with the switch unset, so the shipping heartbeat makes no call
        /// for them, as the pin word says nothing with its switch
        /// unset.</summary>
        private string PollQosWord()
            => !s_pollQosSet ? "" : (_qosHigh && !_qosFailed ? " qos=high" : " qos=default") + " throttle=" + PowerThrottlingMasks();

        /// <summary>After each heartbeat, under flip: the next window takes
        /// the other mode.</summary>
        private void NextPollQosWindow()
        {
            if (_qosMode == PollQosMode.Flip) ApplyPollQos(!_qosHigh);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessInformation(IntPtr process, int informationClass,
            ref ProcessPowerThrottlingState information, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessInformation(IntPtr process, int informationClass,
            ref ProcessPowerThrottlingState information, uint size);
    }
}
