using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Removes PadForge MIDI endpoint devnodes that Windows MIDI Services
    /// stranded past their owner's death. The service deletes a virtual
    /// endpoint's devnodes only on the device-side disconnect, and a
    /// failed removal bails before the bookkeeping is erased (MIDI
    /// reference: MidiEndpointTable.cpp OnDeviceDisconnected). A stranded
    /// devnode is worse than clutter: the next create with the same
    /// unique id ADOPTS the corpse (MidiDeviceManager.cpp
    /// ERROR_ALREADY_EXISTS path), which is how MIDI slot creation went
    /// nondeterministic on this bench. PadForge now uses per-creation
    /// unique ids so adoption can't happen, and this janitor removes the
    /// corpses so they stop appearing in Device Manager and stop feeding
    /// the input scanner.
    ///
    /// Elevation: PadForge always runs elevated, which devnode removal
    /// requires. Corpses are identified by the PADFORGE_MIDI id family
    /// under the MIDISRV software-device enumerator and are skipped when
    /// a live MidiVirtualController in this process claims them.
    /// </summary>
    internal static class MidiEndpointJanitor
    {
        private const int CR_SUCCESS = 0;
        private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
        private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
        private const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;
        private const uint CM_REMOVE_UI_NOT_OK = 0x00000002;
        private const uint CM_REMOVE_NO_RESTART = 0x00000004;

        private const string MidiSrvEnumerator = "SWD\\MIDISRV";

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_ListW(string filter, char[] buffer, uint bufferLength, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        // Mirrors DsHidMini ControlApp SetupApiWrapper.cs (null-veto overload).
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Query_And_Remove_SubTreeW(uint ancestor, IntPtr vetoType, IntPtr vetoName, uint nameLength, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Uninstall_DevNode(uint devInst, uint flags);

        /// <summary>The PadForge endpoint id family. Matches both the
        /// device-side (MIDIU_APPDEV_) and client-visible (MIDIU_APPPUB_)
        /// forms, legacy fixed ids and the newer per-creation ids alike,
        /// in devnode-instance-id or endpoint-interface-id spelling.</summary>
        internal static bool IsPadForgeEndpointId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            return id.IndexOf("MIDIU_APPDEV_PADFORGE_MIDI", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("MIDIU_APPPUB_PADFORGE_MIDI", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True when the devnode should be removed: PadForge's id
        /// family, and no live controller in this process claims it.</summary>
        internal static bool IsSweepCandidate(string deviceInstanceId)
            => IsPadForgeEndpointId(deviceInstanceId)
               && !MidiVirtualController.IsLiveEndpointInstance(deviceInstanceId);

        private static int _sweepQueued;
        private static int _sweepAgain;

        /// <summary>Coalesced background sweep. Safe from any thread; CM
        /// calls talk to PnP, not to the (possibly wedged) MIDI service.
        /// A request landing while a sweep runs schedules one more pass
        /// instead of being dropped, so a teardown that races an active
        /// sweep still gets its corpse collected.</summary>
        public static void ScheduleSweep(int delayMs)
        {
            if (Interlocked.Exchange(ref _sweepQueued, 1) == 1)
            {
                Interlocked.Exchange(ref _sweepAgain, 1);
                return;
            }
            Task.Run(async () =>
            {
                try
                {
                    do
                    {
                        if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
                        try { Sweep(); } catch { /* best effort */ }
                    }
                    while (Interlocked.Exchange(ref _sweepAgain, 0) == 1);
                }
                finally { Interlocked.Exchange(ref _sweepQueued, 0); }
            });
        }

        /// <summary>Enumerates MIDISRV software devices and removes every
        /// sweep candidate. Returns the number removed.</summary>
        internal static int Sweep()
        {
            int removed = 0;
            try
            {
                if (CM_Get_Device_ID_List_SizeW(out uint length, MidiSrvEnumerator, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS
                    || length == 0)
                    return 0;

                var buffer = new char[length];
                if (CM_Get_Device_ID_ListW(MidiSrvEnumerator, buffer, length, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS)
                    return 0;

                foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!IsSweepCandidate(id)) continue;

                    bool phantom = false;
                    if (CM_Locate_DevNodeW(out uint devInst, id, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                    {
                        if (CM_Locate_DevNodeW(out devInst, id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
                            continue;
                        phantom = true;
                    }

                    if (!phantom)
                        CM_Query_And_Remove_SubTreeW(devInst, IntPtr.Zero, IntPtr.Zero, 0,
                            CM_REMOVE_UI_NOT_OK | CM_REMOVE_NO_RESTART);

                    // Best effort: clear the registry trace too, so the
                    // devnode doesn't linger as a phantom.
                    if (CM_Locate_DevNodeW(out uint phantomInst, id, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS)
                        CM_Uninstall_DevNode(phantomInst, 0);

                    removed++;
                    PadForge.Engine.SdlDiagLog.WriteLine($"MIDIJANITOR removed stranded endpoint devnode {id}");
                }
            }
            catch { /* best effort */ }
            return removed;
        }
    }
}
