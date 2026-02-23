using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
        // ViGEm slot tracking state (count-based + delta approach).
        //
        // Adapted from x360ce DInputHelper.Step1.UpdateDevices.cs:
        //   UpdateViGEmSlotTracking()
        //
        // WHY NOT controller.UserIndex?
        //   Reading UserIndex after Connect() can block or throw if
        //   the virtual device hasn't finished initializing. When called
        //   on the update thread this causes a hang/infinite loop.
        //
        // HOW THIS WORKS:
        //   1. Track how many ViGEm controllers WE have connected
        //      (_activeVigemCount — we know this exactly).
        //   2. Snapshot the XInput slot mask BEFORE and AFTER Connect().
        //   3. New bit = the slot our virtual controller landed on.
        //   4. If the delta is missed (timing), UpdateViGEmSlotTracking()
        //      catches it on the next enumeration cycle using count-based
        //      detection: total connected slots minus real physical slots
        //      = virtual slots, assigned from the TOP down.
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

                // ── Snapshot XInput slot mask AFTER connecting ──
                // The new slot should appear immediately or within a few ms.
                // We do ONE non-blocking check — no retries, no Thread.Sleep.
                uint maskAfter = GetXInputConnectedSlotMask();
                uint newBits = maskAfter & ~maskBefore;

                if (newBits != 0)
                {
                    // Exactly one new slot appeared — register it as ViGEm-owned.
                    lock (_vigemOccupiedXInputSlots)
                    {
                        for (int i = 0; i < MaxPads; i++)
                        {
                            if ((newBits & (1u << i)) != 0)
                                _vigemOccupiedXInputSlots.Add(i);
                        }
                    }
                }
                // If delta detection missed (timing), UpdateViGEmSlotTracking()
                // will catch it on the next enumeration cycle in Step 1.
                // The VID/PID and name-based filters in Step 1 also provide
                // fallback protection against loopback.

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

                // ── Snapshot mask AFTER disconnecting ──
                uint maskAfter = GetXInputConnectedSlotMask();
                uint removedBits = maskBefore & ~maskAfter;

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

            // Clear all slot tracking.
            lock (_vigemOccupiedXInputSlots)
            {
                _vigemOccupiedXInputSlots.Clear();
            }

            _activeVigemCount = 0;
            _lastKnownVigemCount = -1;
            _lastKnownSlotMask = 0;
        }

        // ═══════════════════════════════════════════════════════════
        // ViGEm slot tracking: count-based + delta approach.
        //
        // Called by Step 1 (UpdateDevices) at the start of each
        // enumeration cycle. Catches any slots that the before/after
        // delta in CreateVirtualController/DestroyVirtualController
        // may have missed due to timing.
        //
        // Adapted from x360ce DInputHelper.Step1.UpdateDevices.cs:
        //   UpdateViGEmSlotTracking()
        //
        // This method is NON-BLOCKING. No Thread.Sleep, no retries.
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Update ViGEm slot tracking using count-based + delta detection.
        /// Call this at the start of Step 1 (device enumeration) each cycle.
        /// </summary>
        internal void UpdateViGEmSlotTracking()
        {
            // We know exactly how many ViGEm controllers WE have active.
            int vigemCount = _activeVigemCount;
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

                // Physical controllers connected before the app started
                // occupy the LOWEST XInput slots (assigned by Windows at
                // boot/plug-in time). ViGEm controllers created during
                // app initialization get the next available (HIGHEST) slots.
                var allSlots = new List<int>();
                for (int i = 0; i < MaxPads; i++)
                    if ((currentMask & (1u << i)) != 0)
                        allSlots.Add(i);

                lock (_vigemOccupiedXInputSlots)
                {
                    _vigemOccupiedXInputSlots.Clear();
                    for (int vi = 0; vi < vigemCount && vi < allSlots.Count; vi++)
                        _vigemOccupiedXInputSlots.Add(allSlots[allSlots.Count - 1 - vi]);
                }
                return;
            }

            // ── Runtime: detect ViGEm count changes via delta ──
            if (vigemCount > _lastKnownVigemCount)
            {
                // New ViGEm device(s) created.
                // Any XInput slot that appeared since last check is ViGEm.
                uint newBits = currentMask & ~_lastKnownSlotMask;
                lock (_vigemOccupiedXInputSlots)
                {
                    for (int i = 0; i < MaxPads; i++)
                        if ((newBits & (1u << i)) != 0)
                            _vigemOccupiedXInputSlots.Add(i);
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
                        if ((removedBits & (1u << i)) != 0 && _vigemOccupiedXInputSlots.Contains(i))
                            _vigemOccupiedXInputSlots.Remove(i);
                }
            }

            _lastKnownVigemCount = vigemCount;
            _lastKnownSlotMask = currentMask;
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
