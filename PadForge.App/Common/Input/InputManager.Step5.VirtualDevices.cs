using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 5: UpdateVirtualDevices
        //  Feeds combined Gamepad states to ViGEmBus virtual Xbox 360
        //  controllers via the Nefarius.ViGEm.Client NuGet package.
        // ─────────────────────────────────────────────

        /// <summary>Shared ViGEmClient instance (one per process).</summary>
        private static ViGEmClient _vigemClient;
        private static readonly object _vigemClientLock = new object();
        private static bool _vigemClientFailed;

        /// <summary>Virtual controller targets (one per slot).</summary>
        private IXbox360Controller[] _virtualControllers = new IXbox360Controller[MaxPads];

        /// <summary>
        /// XInput user indices currently occupied by our ViGEm virtual controllers.
        /// Updated by count-based + delta tracking (NOT by reading UserIndex).
        ///
        /// Used by Step 1 to skip our own virtual controllers during XInput
        /// enumeration (loopback prevention).
        ///
        /// Thread safety: always access under lock(_vigemOccupiedXInputSlots).
        /// </summary>
        private readonly HashSet<int> _vigemOccupiedXInputSlots = new HashSet<int>();

        /// <summary>Whether virtual controller output is enabled.</summary>
        public bool VirtualControllersEnabled { get; set; } = true;

        /// <summary>Whether ViGEmBus driver is reachable.</summary>
        public bool IsViGEmAvailable => _vigemClient != null;

        // ═══════════════════════════════════════════════════════════
        // ViGEm slot tracking state.
        //
        // Adapted from x360ce DInputHelper.Step1.UpdateDevices.cs:
        //   UpdateViGEmSlotTracking(), CountViGEmXInputDevices()
        //
        // HOW THIS WORKS:
        //   1. CountViGEmXInputDevices() walks the Windows PnP device
        //      tree (cfgmgr32 + registry) to AUTHORITATIVELY count
        //      how many ViGEm virtual Xbox controllers exist.
        //   2. On first detection: physical controllers occupy the
        //      LOWEST XInput slots; ViGEm controllers get the HIGHEST.
        //   3. On runtime changes: use slot-mask delta to identify
        //      exactly which slot appeared or disappeared — no
        //      guessing about ordering.
        //   4. Spin-wait in CreateVirtualController provides immediate
        //      slot detection as an optimization (before the next
        //      2-second enumeration cycle).
        // ═══════════════════════════════════════════════════════════

        private int _activeVigemCount;
        private int _lastKnownVigemCount = -1; // -1 = not yet initialized
        private uint _lastKnownSlotMask;

        /// <summary>
        /// Step 5: Feed each slot's combined gamepad state to ViGEmBus.
        /// Receives vibration feedback from games via the virtual controller.
        /// </summary>
        private void UpdateVirtualDevices()
        {
            if (!VirtualControllersEnabled || _vigemClientFailed)
                return;

            EnsureViGEmClient();
            if (_vigemClient == null)
                return;

            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                try
                {
                    var gp = CombinedXiStates[padIndex];
                    var vc = _virtualControllers[padIndex];
                    bool slotActive = IsSlotActive(padIndex);

                    if (slotActive)
                    {
                        if (vc == null)
                        {
                            vc = CreateVirtualController(padIndex);
                            _virtualControllers[padIndex] = vc;
                        }

                        if (vc != null)
                        {
                            SubmitGamepadToVirtual(vc, gp);
                        }
                    }
                    else
                    {
                        if (vc != null)
                        {
                            DestroyVirtualController(padIndex);
                            _virtualControllers[padIndex] = null;
                        }

                        VibrationStates[padIndex].LeftMotorSpeed = 0;
                        VibrationStates[padIndex].RightMotorSpeed = 0;
                    }
                }
                catch (Exception ex)
                {
                    RaiseError($"Error updating virtual controller for pad {padIndex}", ex);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  ViGEm client lifecycle
        // ─────────────────────────────────────────────

        private void EnsureViGEmClient()
        {
            if (_vigemClient != null || _vigemClientFailed)
                return;

            lock (_vigemClientLock)
            {
                if (_vigemClient != null || _vigemClientFailed)
                    return;

                try
                {
                    _vigemClient = new ViGEmClient();
                }
                catch (Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException)
                {
                    _vigemClientFailed = true;
                    RaiseError("ViGEmBus driver is not installed.", null);
                }
                catch (Exception ex)
                {
                    _vigemClientFailed = true;
                    RaiseError("Failed to initialize ViGEmClient.", ex);
                }
            }
        }

        /// <summary>
        /// Static check: is ViGEmBus driver installed?
        /// Called by the UI on startup to populate SettingsViewModel.
        /// </summary>
        public static bool CheckViGEmInstalled()
        {
            try
            {
                using var client = new ViGEmClient();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ─────────────────────────────────────────────
        //  Slot activity check
        // ─────────────────────────────────────────────

        private bool IsSlotActive(int padIndex)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return false;

            // Use non-allocating overload with pre-allocated buffer.
            int slotCount = settings.FindByPadIndex(padIndex, _padIndexBuffer);
            if (slotCount == 0)
                return false;

            for (int i = 0; i < slotCount; i++)
            {
                var us = _padIndexBuffer[i];
                if (us == null) continue;
                var ud = FindOnlineDeviceByInstanceGuid(us.InstanceGuid);
                if (ud != null && ud.IsOnline)
                    return true;
            }

            return false;
        }

        // ─────────────────────────────────────────────
        //  Virtual controller management
        //  NON-BLOCKING — uses before/after slot mask
        //  delta instead of reading UserIndex.
        // ─────────────────────────────────────────────

        private IXbox360Controller CreateVirtualController(int padIndex)
        {
            if (_vigemClient == null)
                return null;

            try
            {
                // ── Snapshot XInput slot mask BEFORE connecting ──
                uint maskBefore = GetXInputConnectedSlotMask();

                var controller = _vigemClient.CreateXbox360Controller();
                controller.Connect();

                // ── Wait for the new XInput slot to appear ──
                // After Connect(), the ViGEm kernel driver needs a few ms to
                // register the new device with the XInput stack. Spin-wait for
                // up to 50ms for the slot mask to change. This is a one-time
                // cost per controller creation (rare event), not per cycle.
                uint newBits = 0;
                var waitSw = Stopwatch.StartNew();
                while (waitSw.ElapsedMilliseconds < 50)
                {
                    uint maskAfter = GetXInputConnectedSlotMask();
                    newBits = maskAfter & ~maskBefore;
                    if (newBits != 0)
                        break;
                    Thread.SpinWait(100);
                }

                if (newBits != 0)
                {
                    // New slot(s) appeared — register as ViGEm-owned.
                    lock (_vigemOccupiedXInputSlots)
                    {
                        for (int i = 0; i < MaxPads; i++)
                        {
                            if ((newBits & (1u << i)) != 0)
                                _vigemOccupiedXInputSlots.Add(i);
                        }
                    }
                }
                // If detection still missed (very unlikely after 50ms wait),
                // UpdateViGEmSlotTracking() will catch it on the next
                // enumeration cycle in Step 1.

                _activeVigemCount++;

                int capturedIndex = padIndex;
                controller.FeedbackReceived += (sender, args) =>
                {
                    if (capturedIndex >= 0 && capturedIndex < MaxPads)
                    {
                        VibrationStates[capturedIndex].LeftMotorSpeed =
                            (ushort)(args.LargeMotor * 257);
                        VibrationStates[capturedIndex].RightMotorSpeed =
                            (ushort)(args.SmallMotor * 257);
                    }
                };

                return controller;
            }
            catch (Exception ex)
            {
                RaiseError($"Failed to create virtual controller for pad {padIndex}", ex);
                return null;
            }
        }

        private void DestroyVirtualController(int padIndex)
        {
            var vc = _virtualControllers[padIndex];
            if (vc == null) return;

            try
            {
                // ── Snapshot mask BEFORE disconnecting ──
                uint maskBefore = GetXInputConnectedSlotMask();

                vc.Disconnect();

                // ── Wait for the slot to disappear ──
                uint removedBits = 0;
                var waitSw = Stopwatch.StartNew();
                while (waitSw.ElapsedMilliseconds < 50)
                {
                    uint maskAfter = GetXInputConnectedSlotMask();
                    removedBits = maskBefore & ~maskAfter;
                    if (removedBits != 0)
                        break;
                    Thread.SpinWait(100);
                }

                // Unregister any ViGEm-owned slots that disappeared.
                if (removedBits != 0)
                {
                    lock (_vigemOccupiedXInputSlots)
                    {
                        for (int i = 0; i < MaxPads; i++)
                        {
                            if ((removedBits & (1u << i)) != 0)
                                _vigemOccupiedXInputSlots.Remove(i);
                        }
                    }
                }

                _activeVigemCount = Math.Max(0, _activeVigemCount - 1);
            }
            catch { /* best effort */ }
        }

        private void DestroyAllVirtualControllers()
        {
            for (int i = 0; i < MaxPads; i++)
            {
                DestroyVirtualController(i);
                _virtualControllers[i] = null;
            }

            // Clear all slot tracking and reset delta state
            // so the next Start() does fresh PnP detection.
            lock (_vigemOccupiedXInputSlots)
            {
                _vigemOccupiedXInputSlots.Clear();
            }

            _activeVigemCount = 0;
            _lastKnownVigemCount = -1;
            _lastKnownSlotMask = 0;
        }

        // ═══════════════════════════════════════════════════════════
        // ViGEm slot tracking — PnP-based count + delta approach
        //
        // Called by Step 1 (UpdateDevices) at the start of each
        // enumeration cycle. Determines which XInput slots are
        // occupied by ViGEm virtual controllers.
        //
        // Adapted from x360ce DInputHelper.Step1.UpdateDevices.cs.
        //
        // Primary: PnP device tree walk via cfgmgr32 to get an
        //          authoritative ViGEm device count, combined with
        //          slot-mask delta tracking to identify which slots
        //          appeared or disappeared.
        // Secondary: spin-wait delta in CreateVirtualController and
        //            DestroyVirtualController (immediate optimization).
        //
        // The PnP count + delta approach avoids the "rebuild from
        // scratch" heuristic that assumed lowest slots = physical.
        // That assumption breaks when ViGEm and physical controllers
        // are interleaved (e.g., hot-plug after ViGEm creation).
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Update ViGEm slot tracking using PnP-based count + delta detection.
        /// On first run: uses "highest N slots = ViGEm" heuristic.
        /// On subsequent runs: uses mask delta to track new/removed ViGEm slots.
        /// </summary>
        internal void UpdateViGEmSlotTracking()
        {
            // Get authoritative ViGEm count from PnP device tree.
            // Falls back to internal _activeVigemCount if PnP detection fails.
            int pnpCount = CountViGEmXInputDevices();
            int vigemCount = pnpCount >= 0 ? pnpCount : _activeVigemCount;
            uint currentMask = GetXInputConnectedSlotMask();

            if (_lastKnownVigemCount < 0)
            {
                // ── First run: initial detection ──
                _lastKnownVigemCount = vigemCount;
                _lastKnownSlotMask = currentMask;

                if (vigemCount <= 0)
                {
                    lock (_vigemOccupiedXInputSlots)
                        _vigemOccupiedXInputSlots.Clear();
                    return;
                }

                // Physical controllers connected before PadForge occupy
                // the LOWEST XInput slots. ViGEm controllers (if any
                // already exist on first run) get the HIGHEST slots.
                var connectedSlots = new List<int>();
                for (int i = 0; i < MaxPads; i++)
                {
                    if ((currentMask & (1u << i)) != 0)
                        connectedSlots.Add(i);
                }

                lock (_vigemOccupiedXInputSlots)
                {
                    _vigemOccupiedXInputSlots.Clear();
                    for (int vi = 0; vi < vigemCount && vi < connectedSlots.Count; vi++)
                        _vigemOccupiedXInputSlots.Add(connectedSlots[connectedSlots.Count - 1 - vi]);
                }
                return;
            }

            // ── Runtime: detect ViGEm count changes via delta ──
            if (vigemCount > _lastKnownVigemCount)
            {
                // New ViGEm device(s) appeared.
                // Any XInput slot that appeared since last check is ViGEm.
                uint newBits = currentMask & ~_lastKnownSlotMask;
                lock (_vigemOccupiedXInputSlots)
                {
                    for (int i = 0; i < MaxPads; i++)
                    {
                        if ((newBits & (1u << i)) != 0)
                            _vigemOccupiedXInputSlots.Add(i);
                    }
                }
            }
            else if (vigemCount < _lastKnownVigemCount)
            {
                // ViGEm device(s) removed.
                // Slots that disappeared and were ViGEm-owned → unregister.
                uint removedBits = _lastKnownSlotMask & ~currentMask;
                lock (_vigemOccupiedXInputSlots)
                {
                    for (int i = 0; i < MaxPads; i++)
                    {
                        if ((removedBits & (1u << i)) != 0)
                            _vigemOccupiedXInputSlots.Remove(i);
                    }
                }
            }

            _lastKnownVigemCount = vigemCount;
            _lastKnownSlotMask = currentMask;
        }

        // ═══════════════════════════════════════════════════════════
        // PnP-based ViGEm device counting
        //
        // Walks the Windows PnP device tree to authoritatively count
        // ViGEm virtual Xbox controllers. Enumerates the registry
        // under USB\VID_045E&PID_028E (the VID/PID that ViGEm
        // emulates for Xbox 360 controllers), then for each instance
        // walks the PnP parent chain via cfgmgr32 to check if any
        // ancestor is the ViGEmBus driver.
        //
        // Adapted from x360ce DInputHelper.Step1.UpdateDevices.cs:
        //   CountViGEmXInputDevices(), PnP.IsUnderViGEmBus_ByServiceOrName()
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Counts how many ViGEm virtual Xbox controllers exist in the system
        /// by walking the PnP device tree. Returns -1 if detection fails.
        /// </summary>
        private static int CountViGEmXInputDevices()
        {
            int count = 0;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\USB\VID_045E&PID_028E", false);
                if (key == null)
                    return 0;

                foreach (var instanceName in key.GetSubKeyNames())
                {
                    var instanceId = @"USB\VID_045E&PID_028E\" + instanceName;

                    // Skip devices that are not currently present (stale registry entries).
                    if (!ViGEmPnP.IsDevicePresent(instanceId))
                        continue;

                    if (ViGEmPnP.IsUnderViGEmBus(instanceId))
                        count++;
                }
            }
            catch
            {
                return -1; // PnP detection failed
            }
            return count;
        }

        /// <summary>
        /// PnP device tree helpers for ViGEm detection via cfgmgr32.
        /// </summary>
        private static class ViGEmPnP
        {
            private const int CR_SUCCESS = 0;
            private const uint DN_DEVICE_IS_PRESENT = 0x00000002;

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            private static extern int CM_Locate_DevNodeW(
                out uint pdnDevInst, string pDeviceID, int ulFlags);

            [DllImport("cfgmgr32.dll")]
            private static extern int CM_Get_Parent(
                out uint pdnDevInst, uint dnDevInst, int ulFlags);

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            private static extern int CM_Get_Device_IDW(
                uint dnDevInst, StringBuilder Buffer, int BufferLen, int ulFlags);

            [DllImport("cfgmgr32.dll")]
            private static extern int CM_Get_DevNode_Status(
                out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, int ulFlags);

            /// <summary>
            /// Returns true only if the device node is currently present
            /// (not a stale/disconnected registry entry).
            /// </summary>
            public static bool IsDevicePresent(string deviceInstanceId)
            {
                if (string.IsNullOrEmpty(deviceInstanceId))
                    return false;

                if (CM_Locate_DevNodeW(out var devInst, deviceInstanceId, 0) != CR_SUCCESS)
                    return false;

                if (CM_Get_DevNode_Status(out var status, out _, devInst, 0) != CR_SUCCESS)
                    return false;

                return (status & DN_DEVICE_IS_PRESENT) != 0;
            }

            /// <summary>
            /// Checks whether the given device instance is under the ViGEmBus
            /// by walking up the PnP parent chain and checking each ancestor's
            /// registry key for ViGEm signatures.
            /// </summary>
            public static bool IsUnderViGEmBus(string deviceInstanceId)
            {
                if (string.IsNullOrEmpty(deviceInstanceId))
                    return false;

                if (CM_Locate_DevNodeW(out var devInst, deviceInstanceId, 0) != CR_SUCCESS)
                    return false;

                // Walk up the device tree (max 64 levels to prevent infinite loops).
                for (int depth = 0; depth < 64; depth++)
                {
                    var id = GetDeviceInstanceId(devInst);
                    if (!string.IsNullOrEmpty(id) && IsViGEmBusNode(id))
                        return true;

                    if (CM_Get_Parent(out var parent, devInst, 0) != CR_SUCCESS)
                        break;

                    devInst = parent;
                }

                return false;
            }

            private static string GetDeviceInstanceId(uint devInst)
            {
                var sb = new StringBuilder(1024);
                return CM_Get_Device_IDW(devInst, sb, sb.Capacity, 0) == CR_SUCCESS
                    ? sb.ToString()
                    : null;
            }

            /// <summary>
            /// Checks if the given PnP instance is a ViGEmBus node by inspecting
            /// its registry Enum key for Service = "ViGEmBus" or recognized
            /// device descriptions.
            /// </summary>
            private static bool IsViGEmBusNode(string instanceId)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Enum\" + instanceId, false);
                    if (key == null)
                        return false;

                    // Check the Service value (most reliable indicator).
                    var service = key.GetValue("Service") as string;
                    if (!string.IsNullOrEmpty(service) &&
                        service.Equals("ViGEmBus", StringComparison.OrdinalIgnoreCase))
                        return true;

                    // Fallback: check FriendlyName and DeviceDesc for ViGEm signatures.
                    var friendly = key.GetValue("FriendlyName") as string;
                    var desc = key.GetValue("DeviceDesc") as string;
                    var text = (friendly ?? "") + "\n" + (desc ?? "");

                    if (text.Contains("Virtual Gamepad Emulation Bus", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (text.Contains("Nefarius", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }

                return false;
            }
        }

        // ─────────────────────────────────────────────
        //  XInput slot mask — direct P/Invoke to xinput1_4.dll
        //
        //  This bypasses SDL entirely and talks to the real
        //  XInput driver. Used for ViGEm slot detection only.
        //  Same approach as x360ce's XInputInterop.
        // ─────────────────────────────────────────────

        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        private static extern uint XInputGetStateEx(
            uint dwUserIndex, ref XInputStateInternal pState);

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepadInternal
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputStateInternal
        {
            public uint dwPacketNumber;
            public XInputGamepadInternal Gamepad;
        }

        private const uint XINPUT_ERROR_DEVICE_NOT_CONNECTED = 0x048F;

        /// <summary>
        /// Returns a bitmask of connected XInput slots (bit 0 = slot 0, etc.).
        /// Probes slots 0–3 directly via xinput1_4.dll.
        /// </summary>
        private static uint GetXInputConnectedSlotMask()
        {
            uint mask = 0;
            for (uint i = 0; i < 4; i++)
            {
                var state = new XInputStateInternal();
                if (XInputGetStateEx(i, ref state) != XINPUT_ERROR_DEVICE_NOT_CONNECTED)
                    mask |= (1u << (int)i);
            }
            return mask;
        }

        // ─────────────────────────────────────────────
        //  Report submission
        // ─────────────────────────────────────────────

        private static void SubmitGamepadToVirtual(IXbox360Controller vc, Gamepad gp)
        {
            vc.SetButtonState(Xbox360Button.A, (gp.Buttons & Gamepad.A) != 0);
            vc.SetButtonState(Xbox360Button.B, (gp.Buttons & Gamepad.B) != 0);
            vc.SetButtonState(Xbox360Button.X, (gp.Buttons & Gamepad.X) != 0);
            vc.SetButtonState(Xbox360Button.Y, (gp.Buttons & Gamepad.Y) != 0);
            vc.SetButtonState(Xbox360Button.LeftShoulder, (gp.Buttons & Gamepad.LEFT_SHOULDER) != 0);
            vc.SetButtonState(Xbox360Button.RightShoulder, (gp.Buttons & Gamepad.RIGHT_SHOULDER) != 0);
            vc.SetButtonState(Xbox360Button.Back, (gp.Buttons & Gamepad.BACK) != 0);
            vc.SetButtonState(Xbox360Button.Start, (gp.Buttons & Gamepad.START) != 0);
            vc.SetButtonState(Xbox360Button.LeftThumb, (gp.Buttons & Gamepad.LEFT_THUMB) != 0);
            vc.SetButtonState(Xbox360Button.RightThumb, (gp.Buttons & Gamepad.RIGHT_THUMB) != 0);
            vc.SetButtonState(Xbox360Button.Guide, (gp.Buttons & Gamepad.GUIDE) != 0);
            vc.SetButtonState(Xbox360Button.Up, (gp.Buttons & Gamepad.DPAD_UP) != 0);
            vc.SetButtonState(Xbox360Button.Down, (gp.Buttons & Gamepad.DPAD_DOWN) != 0);
            vc.SetButtonState(Xbox360Button.Left, (gp.Buttons & Gamepad.DPAD_LEFT) != 0);
            vc.SetButtonState(Xbox360Button.Right, (gp.Buttons & Gamepad.DPAD_RIGHT) != 0);

            vc.SetAxisValue(Xbox360Axis.LeftThumbX, gp.ThumbLX);
            vc.SetAxisValue(Xbox360Axis.LeftThumbY, gp.ThumbLY);
            vc.SetAxisValue(Xbox360Axis.RightThumbX, gp.ThumbRX);
            vc.SetAxisValue(Xbox360Axis.RightThumbY, gp.ThumbRY);

            vc.SetSliderValue(Xbox360Slider.LeftTrigger, gp.LeftTrigger);
            vc.SetSliderValue(Xbox360Slider.RightTrigger, gp.RightTrigger);

            vc.SubmitReport();
        }
    }
}
