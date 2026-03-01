using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Virtual joystick controller via the vJoy driver.
    /// Uses direct P/Invoke to vJoyInterface.dll (no NuGet dependency).
    /// vJoy devices are configurable (axes, buttons, hats) via the
    /// vJoy Configuration utility — this controller maps the standard
    /// Gamepad struct to whatever axes/buttons the device supports.
    /// </summary>
    internal sealed class VJoyVirtualController : IVirtualController
    {
        private static bool _dllLoaded;

        /// <summary>
        /// Number of DeviceNN registry descriptors currently written.
        /// The vJoy driver reads ALL DeviceNN keys from a single device node
        /// to create that many virtual joysticks. Tracked so we know when a
        /// device restart is needed (descriptor count changed).
        /// </summary>
        private static int _currentDescriptorCount;

        /// <summary>
        /// The current number of vJoy registry descriptors (Device01..DeviceNN).
        /// Used by Step 5 to determine whether EnsureDevicesAvailable needs to be
        /// called for scale-down (deletion) as well as scale-up (creation).
        /// </summary>
        public static int CurrentDescriptorCount => _currentDescriptorCount;

        /// <summary>Whether we've already ensured the driver is in the Windows driver store this session.</summary>
        private static bool _driverStoreChecked;

        /// <summary>
        /// Incremented whenever the device node is restarted. Each VJoyVirtualController
        /// instance captures the generation at Connect() time; if a newer generation exists
        /// during SubmitGamepadState, the controller re-acquires its device handle.
        /// </summary>
        private static int _generation;

        /// <summary>Whether vJoyInterface.dll has been successfully loaded into the process.</summary>
        public static bool IsDllLoaded => _dllLoaded;

        /// <summary>
        /// Preloads vJoyInterface.dll from the vJoy installation directory.
        /// Once loaded into the process, all [DllImport] calls resolve to it.
        /// Only caches success — retries on next call if the DLL wasn't found
        /// (e.g., user installed vJoy after app startup).
        /// </summary>
        internal static void EnsureDllLoaded()
        {
            if (_dllLoaded) return;

            // Already loadable from default search paths?
            if (NativeLibrary.TryLoad("vJoyInterface.dll", out _))
            {
                _dllLoaded = true;
                return;
            }

            // Try vJoy installation directory (root first, then arch subdirectory for legacy installs).
            string vjoyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
            string vjoyPath = Path.Combine(vjoyDir, "vJoyInterface.dll");
            if (!File.Exists(vjoyPath))
            {
                string arch = Environment.Is64BitProcess ? "x64" : "x86";
                vjoyPath = Path.Combine(vjoyDir, arch, "vJoyInterface.dll");
            }
            if (File.Exists(vjoyPath) && NativeLibrary.TryLoad(vjoyPath, out _))
                _dllLoaded = true;
        }

        private readonly uint _deviceId;
        private bool _connected;
        private int _connectedGeneration;

        public VirtualControllerType Type => VirtualControllerType.VJoy;
        public bool IsConnected => _connected;

        public VJoyVirtualController(uint deviceId)
        {
            if (deviceId < 1 || deviceId > 16)
                throw new ArgumentOutOfRangeException(nameof(deviceId), "vJoy device ID must be 1–16.");
            _deviceId = deviceId;
        }

        public void Connect()
        {
            EnsureDllLoaded();
            var status = VJoyNative.GetVJDStatus(_deviceId);
            DiagLog($"Connect: deviceId={_deviceId}, status={status}, dllLoaded={_dllLoaded}");

            if (status != VjdStat.VJD_STAT_FREE && status != VjdStat.VJD_STAT_OWN)
                throw new InvalidOperationException($"vJoy device {_deviceId} is not available (status: {status}).");

            if (status == VjdStat.VJD_STAT_FREE)
            {
                bool acquired = VJoyNative.AcquireVJD(_deviceId);
                DiagLog($"AcquireVJD({_deviceId}): {acquired}");
                if (!acquired)
                    throw new InvalidOperationException($"Failed to acquire vJoy device {_deviceId}.");
            }

            VJoyNative.ResetVJD(_deviceId);
            _connected = true;
            _connectedGeneration = _generation;

            // Verify output works: send a single test frame with non-zero axes.
            var testPos = new JoystickPositionV2 { bDevice = (byte)_deviceId, wAxisX = 16383, wAxisY = 16383 };
            testPos.bHats = 0xFFFF_FFFFu;
            testPos.bHatsEx1 = 0xFFFF_FFFFu;
            testPos.bHatsEx2 = 0xFFFF_FFFFu;
            testPos.bHatsEx3 = 0xFFFF_FFFFu;
            bool testOk = VJoyNative.UpdateVJD(_deviceId, ref testPos);
            DiagLog($"Post-connect test UpdateVJD({_deviceId}): {testOk}");
        }

        /// <summary>The vJoy device ID (1–16) this controller was created with.</summary>
        public uint DeviceId => _deviceId;

        public void Disconnect()
        {
            if (_connected)
            {
                DiagLog($"Disconnect: deviceId={_deviceId}, submitCalls={_submitCallCount}, submitFails={_submitFailCount}");

                // Remove FFB routing for this device.
                lock (_ffbLock)
                {
                    _ffbDeviceMap.Remove(_deviceId);
                    _ffbDeviceStates.Remove(_deviceId);
                }

                VJoyNative.ResetVJD(_deviceId);
                VJoyNative.RelinquishVJD(_deviceId);
                _connected = false;
            }
        }

        public void Dispose()
        {
            Disconnect();
        }

        // TrimDeviceNodes removed — single-node model means there's always
        // exactly 0 or 1 device nodes. Scaling is done via registry descriptors.

        private int _submitCallCount;
        private int _submitFailCount;

        private static readonly string _diagLogPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_diag.log");
        private static bool _diagLogCleared;

        internal static void DiagLog(string msg)
        {
            try
            {
                if (!_diagLogCleared)
                {
                    File.WriteAllText(_diagLogPath, "");
                    _diagLogCleared = true;
                }
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
                Debug.Write(line);
                File.AppendAllText(_diagLogPath, line);
            }
            catch { }
        }

        /// <summary>
        /// Re-acquires the vJoy device if the device node was restarted
        /// (generation mismatch). Called by Step 5 after EnsureDevicesAvailable
        /// to ensure existing controllers re-claim their device IDs BEFORE
        /// new controllers are created via FindFreeDeviceId.
        /// </summary>
        public void ReAcquireIfNeeded()
        {
            if (!_connected || _connectedGeneration == _generation)
                return;

            DiagLog($"ReAcquireIfNeeded: generation mismatch ({_connectedGeneration}→{_generation}), re-acquiring device {_deviceId}");
            try
            {
                VJoyNative.RelinquishVJD(_deviceId);
                bool acquired = VJoyNative.AcquireVJD(_deviceId);
                DiagLog($"Re-AcquireVJD({_deviceId}): {acquired}");
                if (!acquired) { _connected = false; return; }
                VJoyNative.ResetVJD(_deviceId);
                _connectedGeneration = _generation;
            }
            catch { _connected = false; }
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            if (!_connected) return;

            // If the device node was restarted (descriptor count changed),
            // our AcquireVJD handle is stale. Re-acquire transparently.
            if (_connectedGeneration != _generation)
            {
                DiagLog($"SubmitGamepadState: generation mismatch ({_connectedGeneration}→{_generation}), re-acquiring device {_deviceId}");
                try
                {
                    VJoyNative.RelinquishVJD(_deviceId);
                    bool acquired = VJoyNative.AcquireVJD(_deviceId);
                    DiagLog($"Re-AcquireVJD({_deviceId}): {acquired}");
                    if (!acquired) { _connected = false; return; }
                    VJoyNative.ResetVJD(_deviceId);
                    _connectedGeneration = _generation;
                }
                catch { _connected = false; return; }
            }

            uint id = _deviceId;

            // Axes: signed short (-32768..32767) → vJoy range (0..32767)
            int lx = (gp.ThumbLX + 32768) / 2;
            int ly = 32767 - (gp.ThumbLY + 32768) / 2;   // Y inverted (HID Y-down=max)
            int rx = (gp.ThumbRX + 32768) / 2;
            int ry = 32767 - (gp.ThumbRY + 32768) / 2;   // Y inverted
            int lt = gp.LeftTrigger * 32767 / 255;
            int rt = gp.RightTrigger * 32767 / 255;

            // Buttons 1–11 bitmask (Xbox 360 layout: A/B/X/Y/LB/RB/Back/Start/LS/RS/Guide)
            int buttons = 0;
            if (gp.IsButtonPressed(Gamepad.A))              buttons |= 1 << 0;
            if (gp.IsButtonPressed(Gamepad.B))              buttons |= 1 << 1;
            if (gp.IsButtonPressed(Gamepad.X))              buttons |= 1 << 2;
            if (gp.IsButtonPressed(Gamepad.Y))              buttons |= 1 << 3;
            if (gp.IsButtonPressed(Gamepad.LEFT_SHOULDER))  buttons |= 1 << 4;
            if (gp.IsButtonPressed(Gamepad.RIGHT_SHOULDER)) buttons |= 1 << 5;
            if (gp.IsButtonPressed(Gamepad.BACK))           buttons |= 1 << 6;
            if (gp.IsButtonPressed(Gamepad.START))          buttons |= 1 << 7;
            if (gp.IsButtonPressed(Gamepad.LEFT_THUMB))     buttons |= 1 << 8;
            if (gp.IsButtonPressed(Gamepad.RIGHT_THUMB))    buttons |= 1 << 9;
            if (gp.IsButtonPressed(Gamepad.GUIDE))          buttons |= 1 << 10;

            // D-Pad → continuous POV hat (hundredths of degrees).
            // Supports 8-way diagonals: 0=N, 4500=NE, 9000=E, 13500=SE, etc.
            // -1 = centered.
            bool up    = (gp.Buttons & Gamepad.DPAD_UP) != 0;
            bool right = (gp.Buttons & Gamepad.DPAD_RIGHT) != 0;
            bool down  = (gp.Buttons & Gamepad.DPAD_DOWN) != 0;
            bool left  = (gp.Buttons & Gamepad.DPAD_LEFT) != 0;
            int pov;
            if      (up && right)   pov = 4500;
            else if (up && left)    pov = 31500;
            else if (down && right) pov = 13500;
            else if (down && left)  pov = 22500;
            else if (up)            pov = 0;
            else if (right)         pov = 9000;
            else if (down)          pov = 18000;
            else if (left)          pov = 27000;
            else                    pov = -1;

            // Single UpdateVJD call per frame (1 kernel IOCTL) instead of individual
            // SetAxis/SetBtn/SetDiscPov calls (18+ IOCTLs). Critical for multi-controller.
            var pos = new JoystickPositionV2
            {
                bDevice = (byte)id,
                wAxisX = lx,
                wAxisY = ly,
                wAxisZ = lt,
                wAxisXRot = rx,
                wAxisYRot = ry,
                wAxisZRot = rt,
                lButtons = buttons,
                bHats = pov < 0 ? 0xFFFF_FFFFu : (uint)pov,
                bHatsEx1 = 0xFFFF_FFFFu,
                bHatsEx2 = 0xFFFF_FFFFu,
                bHatsEx3 = 0xFFFF_FFFFu,
            };

            bool ok = VJoyNative.UpdateVJD(id, ref pos);
            _submitCallCount++;
            if (!ok) _submitFailCount++;

            // Log first call and periodic status (every ~5 seconds at 1000Hz)
            if (_submitCallCount == 1 || _submitCallCount % 5000 == 0)
            {
                DiagLog($"SubmitGamepadState(UpdateVJD) devId={id} call#{_submitCallCount} fails={_submitFailCount} X={lx} Y={ly} btns=0x{buttons:X} pov={pov}");
            }
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            // Register this device for FFB routing: vJoy device ID → pad index + vibration array.
            lock (_ffbLock)
            {
                _ffbDeviceMap[_deviceId] = (padIndex, vibrationStates);

                // Register the global FFB callback once (shared across all vJoy devices).
                if (!_ffbCallbackRegistered)
                {
                    try
                    {
                        // Must keep a strong reference to the delegate to prevent GC.
                        _ffbCallbackDelegate = FfbCallback;
                        VJoyNative.FfbRegisterGenCB(_ffbCallbackDelegate, IntPtr.Zero);
                        _ffbCallbackRegistered = true;
                        DiagLog($"FFB callback registered (device {_deviceId}, pad {padIndex})");
                    }
                    catch (DllNotFoundException)
                    {
                        DiagLog("FFB callback registration failed: DLL not found");
                    }
                    catch (Exception ex)
                    {
                        DiagLog($"FFB callback registration failed: {ex.Message}");
                    }
                }
                else
                {
                    DiagLog($"FFB callback already registered, added device {_deviceId} → pad {padIndex}");
                }
            }
        }

        // ─────────────────────────────────────────────
        //  FFB (Force Feedback) passthrough
        //
        //  vJoy's FfbRegisterGenCB fires on a thread pool thread whenever a
        //  game sends a DirectInput force feedback effect to the virtual joystick.
        //  We parse the FFB packets and map them to left/right motor vibration,
        //  matching the ViGEm FeedbackReceived pattern.
        // ─────────────────────────────────────────────

        private static readonly object _ffbLock = new object();
        private static bool _ffbCallbackRegistered;
        private static VJoyNative.FfbGenCB _ffbCallbackDelegate;

        /// <summary>Maps vJoy device ID → (padIndex, vibrationStates array).</summary>
        private static readonly System.Collections.Generic.Dictionary<uint, (int padIndex, Vibration[] states)>
            _ffbDeviceMap = new();

        /// <summary>
        /// Per-device FFB effect state. Tracks the most recently set constant/periodic
        /// magnitude and gain so we can compute motor output when effects start/stop.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<uint, FfbDeviceState>
            _ffbDeviceStates = new();

        private class FfbDeviceState
        {
            /// <summary>Device-level gain (0–255, default 255 = 100%).</summary>
            public byte DeviceGain = 255;
            /// <summary>Per-effect: last known magnitude (0–10000 absolute).</summary>
            public System.Collections.Generic.Dictionary<byte, FfbEffectState> Effects = new();
        }

        private class FfbEffectState
        {
            public FFBEType Type;
            public int Magnitude;           // absolute, 0–10000
            public byte Gain = 255;         // per-effect gain from effect report (0–255)
            public ushort Duration;         // ms, 0xFFFF=infinite
            public bool Running;
            public ushort Direction;        // polar direction 0–35999 (hundredths of degrees)
        }

        /// <summary>
        /// Global FFB callback invoked by vJoyInterface.dll on its thread pool.
        /// Routes FFB packets by device ID to the correct VibrationStates[] slot.
        /// </summary>
        private static void FfbCallback(IntPtr data, IntPtr userData)
        {
            try
            {
                uint deviceId = 0;
                if (VJoyNative.Ffb_h_DeviceID(data, ref deviceId) != 0)
                    return;

                FFBPType packetType = 0;
                if (VJoyNative.Ffb_h_Type(data, ref packetType) != 0)
                    return;

                // Get or create per-device state.
                FfbDeviceState devState;
                lock (_ffbLock)
                {
                    if (!_ffbDeviceStates.TryGetValue(deviceId, out devState))
                    {
                        devState = new FfbDeviceState();
                        _ffbDeviceStates[deviceId] = devState;
                    }
                }

                switch (packetType)
                {
                    case FFBPType.PT_EFFREP: // Set Effect Report — contains effect type, gain, direction, duration
                    {
                        var eff = new FFB_EFF_REPORT();
                        if (VJoyNative.Ffb_h_Eff_Report(data, ref eff) == 0)
                        {
                            byte ebi = eff.EffectBlockIndex;
                            if (!devState.Effects.TryGetValue(ebi, out var es))
                            {
                                es = new FfbEffectState();
                                devState.Effects[ebi] = es;
                            }
                            es.Type = eff.EffectType;
                            es.Gain = eff.Gain;
                            es.Duration = eff.Duration;
                            es.Direction = eff.Direction;
                        }
                        break;
                    }

                    case FFBPType.PT_CONSTREP: // Set Constant Force
                    {
                        var cst = new FFB_EFF_CONSTANT();
                        if (VJoyNative.Ffb_h_Eff_Constant(data, ref cst) == 0)
                        {
                            if (devState.Effects.TryGetValue(cst.EffectBlockIndex, out var es))
                                es.Magnitude = Math.Abs(cst.Magnitude); // -10000..+10000 → 0..10000
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_PRIDREP: // Set Periodic (Sine, Square, Triangle, etc.)
                    {
                        var prd = new FFB_EFF_PERIOD();
                        if (VJoyNative.Ffb_h_Eff_Period(data, ref prd) == 0)
                        {
                            if (devState.Effects.TryGetValue(prd.EffectBlockIndex, out var es))
                                es.Magnitude = (int)prd.Magnitude; // 0..10000
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_RAMPREP: // Set Ramp Force
                    {
                        var ramp = new FFB_EFF_RAMP();
                        if (VJoyNative.Ffb_h_Eff_Ramp(data, ref ramp) == 0)
                        {
                            if (devState.Effects.TryGetValue(ramp.EffectBlockIndex, out var es))
                                es.Magnitude = Math.Max(Math.Abs(ramp.Start), Math.Abs(ramp.End));
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_CONDREP: // Set Condition (Spring, Damper, Friction, Inertia)
                    {
                        var cond = new FFB_EFF_COND();
                        if (VJoyNative.Ffb_h_Eff_Cond(data, ref cond) == 0)
                        {
                            if (devState.Effects.TryGetValue(cond.EffectBlockIndex, out var es))
                            {
                                // Use the larger of pos/neg coefficients as magnitude.
                                es.Magnitude = Math.Max(Math.Abs(cond.PosCoeff), Math.Abs(cond.NegCoeff));
                            }
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_EFOPREP: // Effect Operation (Start/Stop/Solo)
                    {
                        var op = new FFB_EFF_OP();
                        if (VJoyNative.Ffb_h_EffOp(data, ref op) == 0)
                        {
                            byte ebi = op.EffectBlockIndex;
                            if (op.EffectOp == FFBOP.EFF_START || op.EffectOp == FFBOP.EFF_SOLO)
                            {
                                if (op.EffectOp == FFBOP.EFF_SOLO)
                                {
                                    // Solo: stop all other effects.
                                    foreach (var kv in devState.Effects)
                                        if (kv.Key != ebi) kv.Value.Running = false;
                                }
                                if (devState.Effects.TryGetValue(ebi, out var es))
                                    es.Running = true;
                            }
                            else if (op.EffectOp == FFBOP.EFF_STOP)
                            {
                                if (devState.Effects.TryGetValue(ebi, out var es))
                                    es.Running = false;
                            }
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_GAINREP: // Device Gain
                    {
                        byte gain = 0;
                        if (VJoyNative.Ffb_h_DevGain(data, ref gain) == 0)
                        {
                            devState.DeviceGain = gain;
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_CTRLREP: // Device Control (Reset, Stop All, etc.)
                    {
                        FFB_CTRL ctrl = 0;
                        if (VJoyNative.Ffb_h_DevCtrl(data, ref ctrl) == 0)
                        {
                            if (ctrl == FFB_CTRL.CTRL_STOPALL || ctrl == FFB_CTRL.CTRL_DEVRST)
                            {
                                foreach (var kv in devState.Effects)
                                    kv.Value.Running = false;
                                if (ctrl == FFB_CTRL.CTRL_DEVRST)
                                {
                                    devState.Effects.Clear();
                                    devState.DeviceGain = 255;
                                }
                            }
                            else if (ctrl == FFB_CTRL.CTRL_DISACT)
                            {
                                foreach (var kv in devState.Effects)
                                    kv.Value.Running = false;
                            }
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_BLKFRREP: // Block Free (delete effect)
                    {
                        uint blockIndex = 0;
                        if (VJoyNative.Ffb_h_EffectBlockIndex(data, ref blockIndex) == 0)
                        {
                            devState.Effects.Remove((byte)blockIndex);
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy FFB] Callback exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Computes aggregate motor output from all running effects and writes to VibrationStates[].
        /// Uses directional mapping: effects pointing left drive the left motor, right → right motor.
        /// Constant/ramp forces use magnitude directly; periodic uses peak amplitude.
        /// </summary>
        private static void ApplyMotorOutput(uint deviceId, FfbDeviceState devState)
        {
            int padIndex;
            Vibration[] states;
            lock (_ffbLock)
            {
                if (!_ffbDeviceMap.TryGetValue(deviceId, out var entry))
                    return;
                padIndex = entry.padIndex;
                states = entry.states;
            }

            if (padIndex < 0 || padIndex >= states.Length)
                return;

            // Sum magnitudes from all running effects, split by direction into L/R motors.
            // Direction is polar: 0=north, 9000=east(right), 18000=south, 27000=west(left).
            // For simple rumble mapping: left-pointing force → left motor, right → right.
            // Non-directional effects (spring/damper/condition) go to both motors equally.
            double leftSum = 0, rightSum = 0;

            foreach (var kv in devState.Effects)
            {
                var es = kv.Value;
                if (!es.Running || es.Magnitude == 0) continue;

                // Apply per-effect gain (0–255).
                double mag = es.Magnitude * (es.Gain / 255.0);

                // Condition effects (spring/damper/friction/inertia) have no meaningful
                // direction for rumble — apply equally to both motors.
                if (es.Type >= FFBEType.ET_SPRNG && es.Type <= FFBEType.ET_FRCTN)
                {
                    leftSum += mag;
                    rightSum += mag;
                }
                else
                {
                    // Directional split using polar direction.
                    // Convert direction (0–35999 hundredths of degrees) to radians.
                    // 0=N, 9000=E, 18000=S, 27000=W.
                    // Map: sin(dir)=right component, cos(dir)=up/down (both motors).
                    double rad = es.Direction * Math.PI / 18000.0;
                    double sinD = Math.Sin(rad); // positive=right, negative=left
                    double cosD = Math.Cos(rad); // up/down component

                    // Both motors get the base effect; directional bias adds extra to one side.
                    double base_ = mag * Math.Abs(cosD);
                    double side = mag * Math.Abs(sinD);
                    if (sinD >= 0)
                    {
                        // Pointing right → emphasize right motor.
                        rightSum += base_ + side;
                        leftSum += base_;
                    }
                    else
                    {
                        // Pointing left → emphasize left motor.
                        leftSum += base_ + side;
                        rightSum += base_;
                    }
                }
            }

            // Apply device-level gain.
            double deviceGainFactor = devState.DeviceGain / 255.0;
            leftSum *= deviceGainFactor;
            rightSum *= deviceGainFactor;

            // Scale from 0..10000 → 0..65535 (ushort).
            ushort newL = (ushort)Math.Min(65535, (int)(leftSum * 65535.0 / 10000.0));
            ushort newR = (ushort)Math.Min(65535, (int)(rightSum * 65535.0 / 10000.0));

            ushort oldL = states[padIndex].LeftMotorSpeed;
            ushort oldR = states[padIndex].RightMotorSpeed;

            states[padIndex].LeftMotorSpeed = newL;
            states[padIndex].RightMotorSpeed = newR;

            if (newL != oldL || newR != oldR)
                RumbleLogger.Log($"[vJoy FFB] Dev{deviceId} Pad{padIndex} L:{oldL}->{newL} R:{oldR}->{newR}");
        }

        // ─────────────────────────────────────────────
        //  Static helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks if the vJoy driver is installed and enabled.
        /// Gracefully returns false if vJoyInterface.dll is not found.
        /// </summary>
        public static bool CheckVJoyInstalled()
        {
            try
            {
                EnsureDllLoaded();
                return VJoyNative.vJoyEnabled();
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the next available vJoy device ID (1–16), or 0 if none available.
        /// This is a fast, non-blocking scan — safe to call from the engine thread.
        /// </summary>
        public static uint FindFreeDeviceId()
        {
            try
            {
                EnsureDllLoaded();
                if (!_dllLoaded) return 0;

                for (uint id = 1; id <= 16; id++)
                {
                    var status = VJoyNative.GetVJDStatus(id);
                    if (status == VjdStat.VJD_STAT_FREE)
                        return id;
                }
            }
            catch (DllNotFoundException ex)
            {
                Debug.WriteLine($"[vJoy] DllNotFoundException in FindFreeDeviceId: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] Exception in FindFreeDeviceId: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// Checks whether the vjoy service is stuck in STOP_PENDING (zombie state).
        /// This happens when a previous uninstall failed to remove device nodes
        /// before stopping the service, and Windows Fast Startup preserved the
        /// broken kernel state across shutdowns. Only a full restart clears it.
        /// </summary>
        public static bool IsServiceStuck()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "query vjoy",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5_000);
                return output.Contains("STOP_PENDING");
            }
            catch { return false; }
        }

        /// <summary>
        /// Ensures the vJoy driver INF is in the Windows driver store.
        /// Without it, PnP won't apply UpperFilters=mshidkmdf from the INF
        /// when binding new device nodes — vjoy.sys handles IOCTLs but no
        /// HID reports reach Windows (joy.cpl shows no output).
        /// Safe to call once at session start, before any device nodes exist.
        /// </summary>
        private static void EnsureDriverInStore()
        {
            try
            {
                string vjoyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
                string infPath = Path.Combine(vjoyDir, "vjoy.inf");
                if (!File.Exists(infPath))
                {
                    DiagLog("EnsureDriverInStore: vjoy.inf not found, skipping");
                    return;
                }

                // Check if vjoy is already in the driver store.
                var checkPsi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/enum-drivers",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var checkProc = Process.Start(checkPsi);
                if (checkProc == null) return;
                string output = checkProc.StandardOutput.ReadToEnd();
                checkProc.WaitForExit(5_000);

                if (output.IndexOf("vjoy", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    DiagLog("EnsureDriverInStore: driver already in store");
                    return;
                }

                // Driver not in store — add it.
                DiagLog("EnsureDriverInStore: driver NOT in store, adding...");
                var addPsi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/add-driver \"{infPath}\" /install",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var addProc = Process.Start(addPsi);
                if (addProc == null) return;
                string addOutput = addProc.StandardOutput.ReadToEnd();
                addProc.WaitForExit(10_000);
                DiagLog($"EnsureDriverInStore: pnputil exit={addProc.ExitCode}, output={addOutput.Trim()}");
            }
            catch (Exception ex)
            {
                DiagLog($"EnsureDriverInStore exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes the OEMForceFeedback registry keys that DirectInput needs to
        /// recognize vJoy as an FFB-capable device. Without these, DirectInput's
        /// ForceFeedback enumeration flag won't find the device even though
        /// vjoy.sys creates COL02 (the PID collection) with PID_BEAD.
        /// Keys go under HKCU — no elevation needed.
        /// </summary>
        private static void EnsureFfbRegistryKeys()
        {
            try
            {
                const string basePath =
                    @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_1234&PID_BEAD";

                using var oemKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(basePath);
                if (oemKey == null) return;
                if (oemKey.GetValue("OEMName") == null)
                    oemKey.SetValue("OEMName", "vJoy Device");

                using var ffbKey = oemKey.CreateSubKey("OEMForceFeedback");
                if (ffbKey == null) return;
                // Standard HID PID FFB class driver CLSID.
                ffbKey.SetValue("CLSID", "{EEC6993A-B3FD-11D2-A916-00C04FB98638}");
                ffbKey.SetValue("CreatedBy", new byte[] { 0x00, 0x08, 0x00, 0x00 },
                    Microsoft.Win32.RegistryValueKind.Binary);
                // Attributes: flags=0, maxForce=1000000 (0x000F4240), minForce=1000000
                ffbKey.SetValue("Attributes",
                    new byte[] { 0x00, 0x00, 0x00, 0x00, 0x40, 0x42, 0x0F, 0x00, 0x40, 0x42, 0x0F, 0x00 },
                    Microsoft.Win32.RegistryValueKind.Binary);

                using var effectsKey = ffbKey.CreateSubKey("Effects");
                if (effectsKey == null) return;

                // Effect GUIDs and their attribute data (from AddvJoyFFB.reg).
                var effects = new (string guid, string name, byte[] attrs)[]
                {
                    ("{13541C20-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_ConstantForce",
                     new byte[] { 0x26, 0x00, 0x0F, 0x00, 0x01, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C21-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_RampForce",
                     new byte[] { 0x27, 0x00, 0x0F, 0x00, 0x02, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C22-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Square",
                     new byte[] { 0x30, 0x00, 0x0F, 0x00, 0x03, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C23-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Sine",
                     new byte[] { 0x31, 0x00, 0x0F, 0x00, 0x03, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C24-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Triangle",
                     new byte[] { 0x32, 0x00, 0x0F, 0x00, 0x03, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C25-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_SawtoothUp",
                     new byte[] { 0x33, 0x00, 0x0F, 0x00, 0x03, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C26-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_SawtoothDown",
                     new byte[] { 0x34, 0x00, 0x0F, 0x00, 0x03, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C27-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Spring",
                     new byte[] { 0x40, 0x00, 0x0F, 0x00, 0x04, 0xC8, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C28-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Damper",
                     new byte[] { 0x41, 0x00, 0x0F, 0x00, 0x04, 0xC8, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C29-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Inertia",
                     new byte[] { 0x42, 0x00, 0x0F, 0x00, 0x04, 0xC8, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C2A-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Friction",
                     new byte[] { 0x43, 0x00, 0x0F, 0x00, 0x04, 0xC8, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                };

                foreach (var (guid, name, attrs) in effects)
                {
                    using var effKey = effectsKey.CreateSubKey(guid);
                    if (effKey == null) continue;
                    effKey.SetValue("", name); // default value
                    effKey.SetValue("Attributes", attrs, Microsoft.Win32.RegistryValueKind.Binary);
                }

                DiagLog("EnsureFfbRegistryKeys: OEMForceFeedback keys written");
            }
            catch (Exception ex)
            {
                DiagLog($"EnsureFfbRegistryKeys exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Counts existing vJoy device nodes by querying PnP (pnputil).
        /// More reliable than GetVJDStatus which can return stale data.
        /// </summary>
        public static int CountExistingDevices()
        {
            return EnumerateVJoyInstanceIds().Count;
        }

        /// <summary>
        /// Ensures <paramref name="requiredCount"/> vJoy virtual joysticks are available.
        ///
        /// vJoy architecture: ONE device node + N registry descriptor keys.
        /// The driver reads ALL DeviceNN keys from HKLM\..\vjoy\Parameters\ and
        /// creates one HID top-level collection per key — all within a single
        /// ROOT\HIDCLASS device node. Multiple device nodes cause phantom
        /// controllers (each node reads ALL keys → N nodes × N keys = N² joysticks).
        ///
        /// To change the count: update registry keys, then restart the device node
        /// so the driver re-reads the report descriptor.
        /// </summary>
        public static bool EnsureDevicesAvailable(int requiredCount = 1)
        {
            if (!_driverStoreChecked)
            {
                _driverStoreChecked = true;
                EnsureDriverInStore();
                EnsureFfbRegistryKeys();
            }

            // Fast path: if the count hasn't changed and we're already loaded,
            // skip the expensive pnputil enumeration and registry writes.
            if (_currentDescriptorCount == requiredCount && _dllLoaded && requiredCount > 0)
                return true;

            EnsureDllLoaded();
            int existingNodes = CountExistingDevices();

            DiagLog($"EnsureDevicesAvailable: required={requiredCount}, nodes={existingNodes}, descriptors={_currentDescriptorCount}, dllLoaded={_dllLoaded}");

            // Write registry descriptors for the required count.
            // WriteDeviceDescriptors is smart — only writes if changed.
            bool descriptorsChanged = _currentDescriptorCount != requiredCount;
            WriteDeviceDescriptors(requiredCount, nAxes: 6, nButtons: 11, nPovs: 1);
            _currentDescriptorCount = requiredCount;

            // If no vJoy devices are needed, remove the device node entirely.
            // A disable/enable restart doesn't make the driver drop its HID
            // collections when 0 descriptors remain — the last controller stays
            // visible in joy.cpl. Removing the node forces Windows to tear down
            // all HID children. The node is recreated on demand when vJoy slots
            // get devices assigned again.
            if (requiredCount == 0)
            {
                if (existingNodes >= 1)
                {
                    DiagLog("Removing device node (all vJoy slots deleted)");
                    RemoveAllDeviceNodes();
                }
                return true;
            }

            // Ensure exactly 1 device node exists.
            if (existingNodes == 0)
            {
                DiagLog("Creating single vJoy device node");
                if (!CreateVJoyDevices(1))
                {
                    DiagLog("CreateVJoyDevices FAILED");
                    return false;
                }

                // Wait for PnP to bind the driver and make devices available.
                _dllLoaded = false;
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    Thread.Sleep(250);
                    EnsureDllLoaded();
                    if (_dllLoaded && FindFreeDeviceId() > 0)
                    {
                        DiagLog($"Device ready after {(attempt + 1) * 250}ms");
                        return true;
                    }
                }
                DiagLog("Device created but not ready after 5 seconds");
                return false;
            }

            // Remove excess device nodes (should be exactly 1).
            if (existingNodes > 1)
            {
                DiagLog($"Removing {existingNodes - 1} excess device node(s)");
                var instanceIds = EnumerateVJoyInstanceIds();
                for (int i = instanceIds.Count - 1; i >= 1; i--)
                    RemoveDeviceNode(instanceIds[i]);

                // Restart the remaining node so the driver re-reads descriptors.
                descriptorsChanged = true;
                _dllLoaded = false;
            }

            // If descriptor count changed, restart the device node so the driver
            // re-reads the registry and creates the right number of collections.
            if (descriptorsChanged && existingNodes >= 1)
            {
                DiagLog($"Restarting device node (descriptor count changed to {requiredCount})");
                RestartDeviceNode();
                _dllLoaded = false;

                for (int attempt = 0; attempt < 20; attempt++)
                {
                    Thread.Sleep(250);
                    EnsureDllLoaded();
                    if (_dllLoaded && FindFreeDeviceId() > 0)
                    {
                        DiagLog($"Device ready after restart ({(attempt + 1) * 250}ms)");
                        return true;
                    }
                }
                DiagLog("Device not ready after restart (5 seconds)");
                return false;
            }

            return _dllLoaded;
        }

        /// <summary>
        /// Enumerates vJoy device instance IDs via pnputil.
        /// Looks for ROOT\HIDCLASS\* devices whose description contains "vJoy".
        /// </summary>
        internal static System.Collections.Generic.List<string> EnumerateVJoyInstanceIds()
        {
            var results = new System.Collections.Generic.List<string>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/enum-devices /class HIDClass",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return results;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5_000);

                // Parse pnputil output: blocks separated by blank lines.
                // Each block has "Instance ID:", "Device Description:", etc.
                string currentInstanceId = null;
                foreach (string rawLine in output.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.IndexOf("Instance ID", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        line.Contains(":"))
                    {
                        currentInstanceId = line.Substring(line.IndexOf(':') + 1).Trim();
                    }
                    else if (currentInstanceId != null &&
                             line.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) >= 0 &&
                             currentInstanceId.StartsWith("ROOT\\HIDCLASS\\", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(currentInstanceId);
                        currentInstanceId = null;
                    }
                    else if (string.IsNullOrEmpty(line))
                    {
                        currentInstanceId = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] EnumerateVJoyInstanceIds exception: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Restarts the single vJoy device node (disable → enable) so the driver
        /// re-reads registry descriptors and creates the correct number of HID
        /// top-level collections. This invalidates all AcquireVJD handles — callers
        /// must re-acquire after restart.
        /// </summary>
        private static void RestartDeviceNode()
        {
            try
            {
                var instanceIds = EnumerateVJoyInstanceIds();
                if (instanceIds.Count == 0) return;

                string id = instanceIds[0];
                DiagLog($"RestartDeviceNode: disabling {id}");

                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/disable-device \"{id}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    proc?.WaitForExit(5_000);
                }

                Thread.Sleep(200);

                DiagLog($"RestartDeviceNode: enabling {id}");
                psi.Arguments = $"/enable-device \"{id}\"";
                using (var proc = Process.Start(psi))
                {
                    proc?.WaitForExit(5_000);
                }

                // Increment generation so existing controllers know to re-acquire.
                _generation++;
                DiagLog($"RestartDeviceNode: generation={_generation}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] RestartDeviceNode exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes a single device node by instance ID via pnputil.
        /// App must be running elevated (which it is when vJoy is installed).
        /// </summary>
        internal static bool RemoveDeviceNode(string instanceId)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/remove-device \"{instanceId}\" /subtree",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                Debug.WriteLine($"[vJoy] Removing device node: {instanceId}");
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(5_000);
                Debug.WriteLine($"[vJoy] pnputil exit code: {proc.ExitCode}");
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] RemoveDeviceNode exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes ALL vJoy device nodes via direct pnputil calls.
        /// App must be running elevated (auto-elevation happens at startup when vJoy is installed).
        /// No PowerShell, no UAC prompt — fast and synchronous.
        /// </summary>
        internal static bool RemoveAllDeviceNodes()
        {
            try
            {
                var instanceIds = EnumerateVJoyInstanceIds();
                Debug.WriteLine($"[vJoy] RemoveAllDeviceNodes: found {instanceIds.Count} device(s)");

                int removed = 0;
                foreach (var id in instanceIds)
                {
                    if (RemoveDeviceNode(id))
                        removed++;
                }

                Debug.WriteLine($"[vJoy] Removed {removed}/{instanceIds.Count} device node(s)");
                // Reset state so vJoyEnabled() is re-evaluated.
                _dllLoaded = false;
                _currentDescriptorCount = 0;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] RemoveAllDeviceNodes exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates one or more vJoy device nodes using SetupAPI in a single
        /// elevated PowerShell script (one UAC prompt for the whole batch).
        /// Each node gets DICD_GENERATE_ID so Windows picks a unique instance ID.
        /// </summary>
        internal static bool CreateVJoyDevices(int count = 1)
        {
            if (count < 1) return true;
            try
            {
                string scriptPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_create_device.ps1");
                string logPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_create_device.log");

                string vjoyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");

                File.WriteAllText(scriptPath, $@"
$ErrorActionPreference = 'Continue'
$log = '{logPath.Replace("'", "''")}'
try {{
    $svcPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\vjoy'
    if (-not (Test-Path $svcPath)) {{
        New-Item -Path $svcPath -Force | Out-Null
        Set-ItemProperty $svcPath -Name 'Type' -Value 1 -Type DWord
        Set-ItemProperty $svcPath -Name 'Start' -Value 3 -Type DWord
        Set-ItemProperty $svcPath -Name 'ErrorControl' -Value 0 -Type DWord
        Set-ItemProperty $svcPath -Name 'ImagePath' -Value 'System32\DRIVERS\vjoy.sys' -Type ExpandString
    }}
    $src = '{vjoyDir.Replace("'", "''")}\vjoy.sys'
    $dst = ""$env:SystemRoot\System32\drivers\vjoy.sys""
    if (-not (Test-Path $dst) -and (Test-Path $src)) {{ Copy-Item $src $dst -Force }}

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PF_SetupApi {{
    public const int DIF_REGISTERDEVICE = 0x19;
    public const int SPDRP_HARDWAREID = 0x01;
    public const int DICD_GENERATE_ID = 0x01;
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA {{ public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }}
    [DllImport(""setupapi.dll"", SetLastError = true)]
    public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);
    [DllImport(""setupapi.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiCreateDeviceInfoW(IntPtr DeviceInfoSet, string DeviceName, ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport(""setupapi.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, int Property, byte[] PropertyBuffer, int PropertyBufferSize);
    [DllImport(""setupapi.dll"", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport(""setupapi.dll"", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
    [DllImport(""newdev.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string HardwareId, string FullInfPath, int InstallFlags, out bool bRebootRequired);
}}
'@
    $hidGuid = [Guid]::new('{{745a17a0-74d3-11d0-b6fe-00a0c90f57da}}')
    $hwid = 'root\VID_1234&PID_BEAD&REV_0222'
    $infPath = '{vjoyDir.Replace("'", "''")}\vjoy.inf'
    $hwidBytes = [System.Text.Encoding]::Unicode.GetBytes($hwid + [char]0 + [char]0)
    $created = 0
    for ($i = 0; $i -lt {count}; $i++) {{
        $dis = [PF_SetupApi]::SetupDiCreateDeviceInfoList([ref]$hidGuid, [IntPtr]::Zero)
        if ($dis -eq [IntPtr]::new(-1)) {{ continue }}
        $did = New-Object PF_SetupApi+SP_DEVINFO_DATA
        $did.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][PF_SetupApi+SP_DEVINFO_DATA])
        $ok = [PF_SetupApi]::SetupDiCreateDeviceInfoW($dis, 'HIDClass', [ref]$hidGuid, 'vJoy Device', [IntPtr]::Zero, [PF_SetupApi]::DICD_GENERATE_ID, [ref]$did)
        if (-not $ok) {{ [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; continue }}
        $ok = [PF_SetupApi]::SetupDiSetDeviceRegistryPropertyW($dis, [ref]$did, [PF_SetupApi]::SPDRP_HARDWAREID, $hwidBytes, $hwidBytes.Length)
        if (-not $ok) {{ [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; continue }}
        $ok = [PF_SetupApi]::SetupDiCallClassInstaller([PF_SetupApi]::DIF_REGISTERDEVICE, $dis, [ref]$did)
        if ($ok) {{ $created++ }}
        [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null
    }}
    if ($created -eq 0) {{ 'FAIL: No devices created' | Out-File $log -Force; exit 1 }}
    # Bind driver to new device nodes. Flag=0 (no INSTALLFLAG_FORCE) so already-bound
    # devices are left alone — only unmatched nodes get the driver installed.
    # INSTALLFLAG_FORCE (1) would re-bind ALL matching devices, creating duplicate
    # HID children and invalidating existing controller handles.
    $reboot = $false
    $ok = [PF_SetupApi]::UpdateDriverForPlugAndPlayDevicesW([IntPtr]::Zero, $hwid, $infPath, 0, [ref]$reboot)
    if (-not $ok) {{ $e = [Runtime.InteropServices.Marshal]::GetLastWin32Error(); ""FAIL: UpdateDriver err=$e (created=$created)"" | Out-File $log -Force; exit 1 }}
    ""OK:$created"" | Out-File $log -Force
}} catch {{
    ""EXCEPTION: $_"" | Out-File $log -Force
    exit 1
}}
");
                try { File.Delete(logPath); } catch { }

                // App runs elevated when vJoy is installed (auto-elevation in App.xaml.cs),
                // so no Verb="runas" needed. Use redirected output for better control.
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                Debug.WriteLine($"[vJoy] Creating {count} device node(s) via SetupAPI (PowerShell)...");
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(30_000);

                string result = File.Exists(logPath) ? File.ReadAllText(logPath).Trim() : "NO_LOG";
                Debug.WriteLine($"[vJoy] CreateVJoyDevices result: {result} (exit code: {proc.ExitCode})");

                try { File.Delete(scriptPath); } catch { }
                try { File.Delete(logPath); } catch { }

                return result.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] CreateVJoyDevices exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Writes HID report descriptors for the required number of vJoy devices.
        /// The descriptor matches the vJoyConf format exactly:
        ///   16 axes × 32-bit (active = Data, inactive = Constant padding)
        ///   + 128-bit POV area (discrete 4-bit nibbles, padded to 32 nibbles)
        ///   + 128-bit button area (1-bit per button, padded to 128 bits)
        /// Total report: 1 byte report ID + 96 bytes data = 97 bytes always.
        /// </summary>
        private static void WriteDeviceDescriptors(int requiredCount,
            int nAxes = 6, int nButtons = 11, int nPovs = 1)
        {
            try
            {
                using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\services\vjoy\Parameters", writable: true);
                if (baseKey == null) return;

                // Remove DeviceNN keys beyond the required count.
                foreach (string subKeyName in baseKey.GetSubKeyNames())
                {
                    if (subKeyName.StartsWith("Device", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(subKeyName.Substring(6), out int keyNum) &&
                        keyNum > requiredCount)
                    {
                        try { baseKey.DeleteSubKeyTree(subKeyName, false); } catch { }
                    }
                }

                // Write descriptor for each device (Device01..DeviceNN).
                // Only overwrite if the existing descriptor doesn't match — avoids
                // disturbing live device nodes whose driver has already read the registry.
                for (int i = 1; i <= requiredCount; i++)
                {
                    byte[] descriptor = BuildHidDescriptor((byte)i, nAxes, nButtons, nPovs);
                    string keyName = $"Device{i:D2}";
                    using var devKey = baseKey.CreateSubKey(keyName);

                    // Check if existing descriptor already matches.
                    bool needsWrite = true;
                    try
                    {
                        if (devKey.GetValue("HidReportDescriptor") is byte[] existing &&
                            existing.Length == descriptor.Length)
                        {
                            needsWrite = false;
                            for (int b = 0; b < descriptor.Length; b++)
                            {
                                if (existing[b] != descriptor[b]) { needsWrite = true; break; }
                            }
                        }
                    }
                    catch { needsWrite = true; }

                    if (needsWrite)
                    {
                        devKey.SetValue("HidReportDescriptor", descriptor,
                            Microsoft.Win32.RegistryValueKind.Binary);
                        devKey.SetValue("HidReportDescriptorSize", descriptor.Length,
                            Microsoft.Win32.RegistryValueKind.DWord);
                        DiagLog($"Wrote {keyName}: {descriptor.Length} bytes ({nAxes} axes, {nButtons} buttons, {nPovs} POVs)");
                    }
                    else
                    {
                        DiagLog($"{keyName}: descriptor already correct, skipping");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] WriteDeviceDescriptors exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a HID Report Descriptor matching the vJoyConf format.
        /// The report always has a fixed 97-byte layout:
        ///   1 byte report ID + 16 axes × 4 bytes + 4 POV DWORDs + 128 button bits (16 bytes).
        /// Disabled axes/POVs/buttons are constant padding so offsets always match.
        /// </summary>
        private static byte[] BuildHidDescriptor(byte reportId, int nAxes, int nButtons, int nPovs)
        {
            nAxes = Math.Clamp(nAxes, 0, 6);
            nButtons = Math.Clamp(nButtons, 0, 128);
            nPovs = Math.Clamp(nPovs, 0, 4);

            byte[] axisUsages = {
                0x30, 0x31, 0x32, 0x33, 0x34, 0x35,   // X, Y, Z, RX, RY, RZ
                0x36, 0x37, 0x38,                       // Slider, Dial, Wheel
                0xC4, 0xC5, 0xC6, 0xC8,                // Accelerator, Brake, Clutch, Steering
                0xB0, 0xBA, 0xBB                        // Aileron, Rudder, Throttle
            };

            var d = new System.Collections.Generic.List<byte>();

            // ── Outer header ──
            d.AddRange(new byte[] { 0x05, 0x01 });         // USAGE_PAGE (Generic Desktop)
            d.AddRange(new byte[] { 0x15, 0x00 });         // LOGICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x09, 0x04 });         // USAGE (Joystick)
            d.AddRange(new byte[] { 0xA1, 0x01 });         // COLLECTION (Application)

            // ── Axes collection ──
            d.AddRange(new byte[] { 0x05, 0x01 });         //   USAGE_PAGE (Generic Desktop)
            d.AddRange(new byte[] { 0x85, reportId });      //   REPORT_ID
            d.AddRange(new byte[] { 0x09, 0x01 });         //   USAGE (Pointer)
            d.AddRange(new byte[] { 0x15, 0x00 });         //   LOGICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x26, 0xFF, 0x7F });   //   LOGICAL_MAXIMUM (32767)
            d.AddRange(new byte[] { 0x75, 0x20 });         //   REPORT_SIZE (32)
            d.AddRange(new byte[] { 0x95, 0x01 });         //   REPORT_COUNT (1)
            d.AddRange(new byte[] { 0xA1, 0x00 });         //   COLLECTION (Physical)

            for (int i = 0; i < 16; i++)
            {
                if (i < nAxes)
                {
                    d.AddRange(new byte[] { 0x09, axisUsages[i] });
                    d.AddRange(new byte[] { 0x81, 0x02 });  // INPUT (Data, Var, Abs)
                }
                else
                {
                    d.AddRange(new byte[] { 0x81, 0x01 });  // INPUT (Cnst, Ary, Abs)
                }
            }

            d.Add(0xC0);                                    //   END_COLLECTION (Physical)

            // ── Continuous POV hats — always 128 bits (4 × 32-bit DWORDs) ──
            // Continuous POV uses degree values × 100 (0–35900), enabling 8-way diagonals.
            // Matches vJoyConf continuous POV format.
            if (nPovs > 0)
            {
                d.AddRange(new byte[] { 0x15, 0x00 });              // LOGICAL_MINIMUM (0)
                d.AddRange(new byte[] { 0x27, 0x3C, 0x8C, 0x00, 0x00 }); // LOGICAL_MAXIMUM (35900)
                d.AddRange(new byte[] { 0x35, 0x00 });              // PHYSICAL_MINIMUM (0)
                d.AddRange(new byte[] { 0x47, 0x3C, 0x8C, 0x00, 0x00 }); // PHYSICAL_MAXIMUM (35900)
                d.AddRange(new byte[] { 0x65, 0x14 });              // UNIT (Eng Rot:Angular Pos)
                d.AddRange(new byte[] { 0x75, 0x20 });              // REPORT_SIZE (32)
                d.AddRange(new byte[] { 0x95, 0x01 });              // REPORT_COUNT (1)

                for (int p = 0; p < nPovs; p++)
                {
                    d.AddRange(new byte[] { 0x09, 0x39 });          // USAGE (Hat Switch)
                    d.AddRange(new byte[] { 0x81, 0x02 });          // INPUT (Data, Var, Abs)
                }

                if (nPovs < 4)
                {
                    d.AddRange(new byte[] { 0x95, (byte)(4 - nPovs) }); // REPORT_COUNT (remaining)
                    d.AddRange(new byte[] { 0x81, 0x01 });          // INPUT (Cnst, Ary, Abs)
                }
            }
            else
            {
                d.AddRange(new byte[] { 0x75, 0x20 });              // REPORT_SIZE (32)
                d.AddRange(new byte[] { 0x95, 0x04 });              // REPORT_COUNT (4)
                d.AddRange(new byte[] { 0x81, 0x01 });              // INPUT (Cnst, Ary, Abs)
            }

            // ── Buttons — always 128 bits ──
            byte usageMin = (byte)(nButtons > 0 ? 0x01 : 0x00);
            d.AddRange(new byte[] { 0x05, 0x09 });
            d.AddRange(new byte[] { 0x15, 0x00 });
            d.AddRange(new byte[] { 0x25, 0x01 });
            d.AddRange(new byte[] { 0x55, 0x00 });
            d.AddRange(new byte[] { 0x65, 0x00 });
            d.AddRange(new byte[] { 0x19, usageMin });
            d.Add(0x29); d.Add((byte)nButtons);
            d.AddRange(new byte[] { 0x75, 0x01 });
            d.Add(0x95); d.Add((byte)nButtons);
            d.AddRange(new byte[] { 0x81, 0x02 });

            if (nButtons < 128)
            {
                int padBits = 128 - nButtons;
                d.Add(0x75); d.Add((byte)padBits);
                d.AddRange(new byte[] { 0x95, 0x01 });
                d.AddRange(new byte[] { 0x81, 0x01 });
            }

            // ── FFB/PID section (Usage Page 0x0F Physical Interface) ──
            // Appended inside the Application collection so DirectInput
            // discovers FFB actuators and can create effects.
            // Report IDs are offset by 0x10 * reportId to avoid
            // collisions with the joystick input report ID.
            AppendFfbDescriptor(d, reportId);

            d.Add(0xC0);                                    // END_COLLECTION (Application)
            return d.ToArray();
        }

        /// <summary>
        /// Appends the full PID (Physical Interface Device) HID descriptor
        /// for force feedback support. Transcribed from vJoy's hidReportDescFfb.h.
        /// Report IDs are offset by 0x10 * (deviceIndex) to support multi-device.
        /// </summary>
        private static void AppendFfbDescriptor(System.Collections.Generic.List<byte> d, byte reportId)
        {
            // vJoyConf uses: HID_ID_EFFREP + 0x10 * ReportId (1-based).
            // Device 1 → FFB IDs start at 0x11, device 2 → 0x21, etc.
            // This avoids collisions with the joystick input report ID (0x01..0x10).
            int tlid = reportId;
            const byte MAX_EBI = 0x64; // VJOY_FFB_MAX_EFFECTS_BLOCK_INDEX = 100

            // Report ID helpers: base + 0x10 * tlid
            byte rid(int baseId) => (byte)(baseId + 0x10 * tlid);

            d.AddRange(new byte[] { 0x05, 0x0F });         // USAGE_PAGE (Physical Interface)

            // ── Set Effect Report (Output, Report ID 1) ──
            d.AddRange(new byte[] { 0x09, 0x21 });         // Usage Set Effect Report
            d.AddRange(new byte[] { 0xA1, 0x02 });         // Collection Datalink
            d.AddRange(new byte[] { 0x85, rid(0x01) });    // Report ID
            d.AddRange(new byte[] { 0x09, 0x22 });         // Usage Effect Block Index
            d.AddRange(new byte[] { 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 }); // Output 8-bit

            // Effect Type subcollection
            d.AddRange(new byte[] { 0x09, 0x25, 0xA1, 0x02 }); // Usage Effect Type, Collection
            d.AddRange(new byte[] {
                0x09, 0x26,  // ET Constant Force
                0x09, 0x27,  // ET Ramp
                0x09, 0x30,  // ET Square
                0x09, 0x31,  // ET Sine
                0x09, 0x32,  // ET Triangle
                0x09, 0x33,  // ET Sawtooth Up
                0x09, 0x34,  // ET Sawtooth Down
                0x09, 0x40,  // ET Spring
                0x09, 0x41,  // ET Damper
                0x09, 0x42,  // ET Inertia
                0x09, 0x43,  // ET Friction
                0x09, 0x29,  // ET Reserved
            });
            d.AddRange(new byte[] { 0x25, 0x0C, 0x15, 0x01, 0x35, 0x01, 0x45, 0x0C });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 }); // Output
            d.Add(0xC0); // End Effect Type collection

            // Duration, Trigger Repeat, Sample Period, Start Delay (4 × 16-bit, ms)
            d.AddRange(new byte[] { 0x09, 0x50, 0x09, 0x54, 0x09, 0x51, 0x09, 0xA7 });
            d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x7F });
            d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD }); // Unit: ms
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x04, 0x91, 0x02 });

            // Gain (8-bit)
            d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 }); // reset unit
            d.AddRange(new byte[] { 0x09, 0x52 }); // Usage Gain
            d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });

            // Trigger Button (8-bit)
            d.AddRange(new byte[] { 0x09, 0x53 }); // Usage Trigger Button
            d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x08, 0x35, 0x01, 0x45, 0x08 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });

            // Axes Enable (X, Y actuators)
            d.AddRange(new byte[] { 0x09, 0x55, 0xA1, 0x02 }); // Usage Axes Enable, Collection
            d.AddRange(new byte[] { 0x05, 0x01 }); // Usage Page Generic Desktop
            d.AddRange(new byte[] { 0x09, 0x30, 0x09, 0x31 }); // Usage X, Usage Y
            d.AddRange(new byte[] { 0x15, 0x00, 0x25, 0x01 }); // 0-1
            d.AddRange(new byte[] { 0x75, 0x01, 0x95, 0x02, 0x91, 0x02 }); // 2 bits
            d.Add(0xC0); // End Axes Enable

            // Direction Enable + padding
            d.AddRange(new byte[] { 0x05, 0x0F }); // Usage Page Physical Interface
            d.AddRange(new byte[] { 0x09, 0x56, 0x95, 0x01, 0x91, 0x02 }); // Direction Enable, 1 bit
            d.AddRange(new byte[] { 0x95, 0x05, 0x91, 0x03 }); // 5 bits padding (7-2 axes=5)

            // Direction (2 ordinals, 16-bit each, degrees)
            d.AddRange(new byte[] { 0x09, 0x57, 0xA1, 0x02 }); // Usage Direction, Collection
            d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00 }); // Ordinal Instance 1
            d.AddRange(new byte[] { 0x0B, 0x02, 0x00, 0x0A, 0x00 }); // Ordinal Instance 2
            d.AddRange(new byte[] { 0x66, 0x14, 0x00, 0x55, 0xFE }); // Unit: degrees ×10^-2
            d.AddRange(new byte[] { 0x15, 0x00, 0x27, 0xFF, 0x7F, 0x00, 0x00 }); // LogMax 32767
            d.AddRange(new byte[] { 0x35, 0x00, 0x47, 0xA0, 0x8C, 0x00, 0x00 }); // PhyMax 36000
            d.AddRange(new byte[] { 0x66, 0x00, 0x00 }); // Unit 0
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 }); // 2×16-bit
            d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 }); // reset
            d.Add(0xC0); // End Direction

            // Type Specific Block Offset (2 ordinals, 16-bit)
            d.AddRange(new byte[] { 0x05, 0x0F, 0x09, 0x58, 0xA1, 0x02 });
            d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00 });
            d.AddRange(new byte[] { 0x0B, 0x02, 0x00, 0x0A, 0x00 });
            d.AddRange(new byte[] { 0x26, 0xFD, 0x7F, 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
            d.Add(0xC0); // End Type Specific Block Offset
            d.Add(0xC0); // End Set Effect Report

            // ── Set Envelope Report (Output, Report ID 2) ──
            d.AddRange(new byte[] { 0x09, 0x5A, 0xA1, 0x02, 0x85, rid(0x02) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            // Attack/Fade Level (2×16-bit, 0-10000)
            d.AddRange(new byte[] { 0x09, 0x5B, 0x09, 0x5D });
            d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
            // Attack/Fade Time (2×32-bit, ms)
            d.AddRange(new byte[] { 0x09, 0x5C, 0x09, 0x5E });
            d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD });
            d.AddRange(new byte[] { 0x27, 0xFF, 0x7F, 0x00, 0x00, 0x47, 0xFF, 0x7F, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x20, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x45, 0x00, 0x66, 0x00, 0x00, 0x55, 0x00 });
            d.Add(0xC0); // End Envelope

            // ── Set Condition Report (Output, Report ID 3) ──
            d.AddRange(new byte[] { 0x09, 0x5F, 0xA1, 0x02, 0x85, rid(0x03) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            // Parameter Block Offset (4-bit)
            d.AddRange(new byte[] { 0x09, 0x23, 0x15, 0x00, 0x25, 0x03, 0x35, 0x00, 0x45, 0x03 });
            d.AddRange(new byte[] { 0x75, 0x04, 0x95, 0x01, 0x91, 0x02 });
            // Type Specific Block Offset (2 ordinals, 2-bit each)
            d.AddRange(new byte[] { 0x09, 0x58, 0xA1, 0x02 });
            d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00, 0x0B, 0x02, 0x00, 0x0A, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x02, 0x95, 0x02, 0x91, 0x02 });
            d.Add(0xC0);
            // CP Offset, Pos/Neg Coefficient (signed 16-bit)
            d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x09, 0x60, 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x09, 0x61, 0x09, 0x62, 0x95, 0x02, 0x91, 0x02 });
            // Pos/Neg Saturation (unsigned 16-bit, 0-10000)
            d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x09, 0x63, 0x09, 0x64, 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
            // Dead Band
            d.AddRange(new byte[] { 0x09, 0x65 });
            d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x95, 0x01, 0x91, 0x02 });
            d.Add(0xC0); // End Condition

            // ── Set Periodic Report (Output, Report ID 4) ──
            d.AddRange(new byte[] { 0x09, 0x6E, 0xA1, 0x02, 0x85, rid(0x04) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            // Magnitude (16-bit, 0-10000)
            d.AddRange(new byte[] { 0x09, 0x70, 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            // Offset (signed 16-bit)
            d.AddRange(new byte[] { 0x09, 0x6F, 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x95, 0x01, 0x75, 0x10, 0x91, 0x02 });
            // Phase (16-bit, degrees × 10^-2)
            d.AddRange(new byte[] { 0x09, 0x71, 0x66, 0x14, 0x00, 0x55, 0xFE });
            d.AddRange(new byte[] { 0x15, 0x00, 0x27, 0x9F, 0x8C, 0x00, 0x00, 0x35, 0x00, 0x47, 0x9F, 0x8C, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            // Period (32-bit, ms)
            d.AddRange(new byte[] { 0x09, 0x72, 0x15, 0x00, 0x27, 0xFF, 0x7F, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x35, 0x00, 0x47, 0xFF, 0x7F, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD });
            d.AddRange(new byte[] { 0x75, 0x20, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x66, 0x00, 0x00, 0x55, 0x00 });
            d.Add(0xC0); // End Periodic

            // ── Set Constant Force Report (Output, Report ID 5) ──
            d.AddRange(new byte[] { 0x09, 0x73, 0xA1, 0x02, 0x85, rid(0x05) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x70 }); // Magnitude (signed 16-bit)
            d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            d.Add(0xC0); // End Constant Force

            // ── Set Ramp Force Report (Output, Report ID 6) ──
            d.AddRange(new byte[] { 0x09, 0x74, 0xA1, 0x02, 0x85, rid(0x06) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x75, 0x09, 0x76 }); // Ramp Start/End
            d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
            d.Add(0xC0); // End Ramp

            // ── Custom Force Data Report (Output, Report ID 7) ──
            d.AddRange(new byte[] { 0x09, 0x68, 0xA1, 0x02, 0x85, rid(0x07) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x6C, 0x15, 0x00, 0x26, 0x10, 0x27, 0x35, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x69, 0x15, 0x81, 0x25, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x0C, 0x92, 0x02, 0x01 }); // Output Buffered
            d.Add(0xC0); // End Custom Force

            // ── Download Force Sample (Output, Report ID 8) ──
            d.AddRange(new byte[] { 0x09, 0x66, 0xA1, 0x02, 0x85, rid(0x08) });
            d.AddRange(new byte[] { 0x05, 0x01, 0x09, 0x30, 0x09, 0x31 }); // X, Y
            d.AddRange(new byte[] { 0x15, 0x81, 0x25, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x02, 0x91, 0x02 });
            d.Add(0xC0); // End Download Force Sample

            // ── Effect Operation Report (Output, Report ID 0x0A) ──
            d.AddRange(new byte[] { 0x05, 0x0F });
            d.AddRange(new byte[] { 0x09, 0x77, 0xA1, 0x02, 0x85, rid(0x0A) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x78, 0xA1, 0x02 }); // Effect Operation
            d.AddRange(new byte[] { 0x09, 0x79, 0x09, 0x7A, 0x09, 0x7B }); // Start/StartSolo/Stop
            d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x03, 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 });
            d.Add(0xC0);
            d.AddRange(new byte[] { 0x09, 0x7C }); // Loop Count
            d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0xFF, 0x00 });
            d.AddRange(new byte[] { 0x91, 0x02 });
            d.Add(0xC0); // End Effect Operation

            // ── PID Block Free Report (Output, Report ID 0x0B) ──
            d.AddRange(new byte[] { 0x09, 0x90, 0xA1, 0x02, 0x85, rid(0x0B) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x25, MAX_EBI, 0x15, 0x01, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.Add(0xC0);

            // ── PID Device Control (Output, Report ID 0x0C) ──
            d.AddRange(new byte[] { 0x09, 0x96, 0xA1, 0x02, 0x85, rid(0x0C) });
            d.AddRange(new byte[] { 0x09, 0x97, 0x09, 0x98, 0x09, 0x99, 0x09, 0x9A, 0x09, 0x9B, 0x09, 0x9C });
            d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x06, 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 });
            d.Add(0xC0);

            // ── Device Gain Report (Output, Report ID 0x0D) ──
            d.AddRange(new byte[] { 0x09, 0x7D, 0xA1, 0x02, 0x85, rid(0x0D) });
            d.AddRange(new byte[] { 0x09, 0x7E, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.Add(0xC0);

            // ── Set Custom Force Report (Output, Report ID 0x0E) ──
            d.AddRange(new byte[] { 0x09, 0x6B, 0xA1, 0x02, 0x85, rid(0x0E) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x6D, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0xFF, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x51, 0x66, 0x03, 0x10, 0x55, 0xFD });
            d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x7F });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 });
            d.Add(0xC0);

            // ── Create New Effect Report (Feature, Report ID 0x01+0x10*tlid but uses same RID space) ──
            // Note: NEWEFREP uses same base ID as EFFREP (0x01) — vJoy quirk.
            // Feature reports use different HID report type so IDs can overlap with Output.
            d.AddRange(new byte[] { 0x09, 0xAB, 0xA1, 0x02, 0x85, rid(0x01) });
            d.AddRange(new byte[] { 0x09, 0x25, 0xA1, 0x02 }); // Effect Type
            d.AddRange(new byte[] {
                0x09, 0x26, 0x09, 0x27, 0x09, 0x30, 0x09, 0x31, 0x09, 0x32,
                0x09, 0x33, 0x09, 0x34, 0x09, 0x40, 0x09, 0x41, 0x09, 0x42,
                0x09, 0x43, 0x09, 0x29
            });
            d.AddRange(new byte[] { 0x25, 0x0C, 0x15, 0x01, 0x35, 0x01, 0x45, 0x0C });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x00 }); // Feature
            d.Add(0xC0);
            d.AddRange(new byte[] { 0x05, 0x01, 0x09, 0x3B }); // Usage Page Generic Desktop, Reserved
            d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x01, 0x35, 0x00, 0x46, 0xFF, 0x01 });
            d.AddRange(new byte[] { 0x75, 0x0A, 0x95, 0x01, 0xB1, 0x02 }); // Feature 10-bit
            d.AddRange(new byte[] { 0x75, 0x06, 0xB1, 0x01 }); // Feature padding
            d.Add(0xC0);

            // ── Block Load Report (Feature, Report ID 0x02) ──
            d.AddRange(new byte[] { 0x05, 0x0F });
            d.AddRange(new byte[] { 0x09, 0x89, 0xA1, 0x02, 0x85, rid(0x02) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x25, MAX_EBI, 0x15, 0x01, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x02 }); // Feature
            d.AddRange(new byte[] { 0x09, 0x8B, 0xA1, 0x02 }); // Block Load Status
            d.AddRange(new byte[] { 0x09, 0x8C, 0x09, 0x8D, 0x09, 0x8E }); // Success/Full/Error
            d.AddRange(new byte[] { 0x25, 0x03, 0x15, 0x01, 0x35, 0x01, 0x45, 0x03 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x00 });
            d.Add(0xC0);
            d.AddRange(new byte[] { 0x09, 0xAC }); // RAM Pool Available
            d.AddRange(new byte[] { 0x15, 0x00, 0x27, 0xFF, 0xFF, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x35, 0x00, 0x47, 0xFF, 0xFF, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0xB1, 0x00 });
            d.Add(0xC0);

            // ── PID Pool Report (Feature, Report ID 0x03) ──
            d.AddRange(new byte[] { 0x09, 0x7F, 0xA1, 0x02, 0x85, rid(0x03) });
            d.AddRange(new byte[] { 0x09, 0x80, 0x75, 0x10, 0x95, 0x01 }); // RAM Pool Size
            d.AddRange(new byte[] { 0x15, 0x00, 0x35, 0x00 });
            d.AddRange(new byte[] { 0x27, 0xFF, 0xFF, 0x00, 0x00, 0x47, 0xFF, 0xFF, 0x00, 0x00 });
            d.AddRange(new byte[] { 0xB1, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x83 }); // Simultaneous Effects Max
            d.AddRange(new byte[] { 0x26, 0xFF, 0x00, 0x46, 0xFF, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x02 });
            d.AddRange(new byte[] { 0x09, 0xA9, 0x09, 0xAA }); // Device Managed Pool, Shared Param
            d.AddRange(new byte[] { 0x75, 0x01, 0x95, 0x02, 0x15, 0x00, 0x25, 0x01, 0x35, 0x00, 0x45, 0x01 });
            d.AddRange(new byte[] { 0xB1, 0x02 });
            d.AddRange(new byte[] { 0x75, 0x06, 0x95, 0x01, 0xB1, 0x03 }); // Padding
            d.Add(0xC0);

            // ── PID State Report (Feature, Report ID 0x04) ──
            d.AddRange(new byte[] { 0x09, 0x92, 0xA1, 0x02, 0x85, rid(0x04) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x02 });
            d.AddRange(new byte[] { 0xA1, 0x02 }); // Subcollection
            d.AddRange(new byte[] { 0x09, 0x94, 0x09, 0x9F, 0x09, 0xA0, 0x09, 0xA4, 0x09, 0xA5, 0x09, 0xA6 });
            d.AddRange(new byte[] { 0x75, 0x01, 0x95, 0x06, 0x81, 0x02 }); // Input 6 bits
            d.AddRange(new byte[] { 0x95, 0x02, 0x81, 0x03 }); // 2-bit padding
            d.Add(0xC0);
            d.Add(0xC0);
        }
    }

    // ─────────────────────────────────────────────
    //  vJoy P/Invoke declarations
    // ─────────────────────────────────────────────

    internal enum VjdStat
    {
        VJD_STAT_OWN = 0,
        VJD_STAT_FREE = 1,
        VJD_STAT_BUSY = 2,
        VJD_STAT_MISS = 3,
        VJD_STAT_UNKN = 4
    }

    /// <summary>
    /// JOYSTICK_POSITION_V2 — matches public.h _JOYSTICK_POSITION_V2 struct (108 bytes).
    /// Used by UpdateVJD for single-IOCTL-per-frame output.
    /// Verified working against vJoyInterface.dll v2.2.2 by standalone test tool.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 108)]
    internal struct JoystickPositionV2
    {
        [FieldOffset(0)]  public byte bDevice;       // 1-based device index
        [FieldOffset(4)]  public int wThrottle;
        [FieldOffset(8)]  public int wRudder;
        [FieldOffset(12)] public int wAileron;
        [FieldOffset(16)] public int wAxisX;
        [FieldOffset(20)] public int wAxisY;
        [FieldOffset(24)] public int wAxisZ;
        [FieldOffset(28)] public int wAxisXRot;
        [FieldOffset(32)] public int wAxisYRot;
        [FieldOffset(36)] public int wAxisZRot;
        [FieldOffset(40)] public int wSlider;
        [FieldOffset(44)] public int wDial;
        [FieldOffset(48)] public int wWheel;
        [FieldOffset(52)] public int wAxisVX;
        [FieldOffset(56)] public int wAxisVY;
        [FieldOffset(60)] public int wAxisVZ;
        [FieldOffset(64)] public int wAxisVBRX;
        [FieldOffset(68)] public int wAxisVBRY;
        [FieldOffset(72)] public int wAxisVBRZ;
        [FieldOffset(76)] public int lButtons;       // Buttons 1-32 bitmask
        [FieldOffset(80)] public uint bHats;          // Discrete POV 1 (low nibble)
        [FieldOffset(84)] public uint bHatsEx1;
        [FieldOffset(88)] public uint bHatsEx2;
        [FieldOffset(92)] public uint bHatsEx3;
        [FieldOffset(96)]  public int lButtonsEx1;
        [FieldOffset(100)] public int lButtonsEx2;
        [FieldOffset(104)] public int lButtonsEx3;
    }

    /// <summary>
    /// Direct P/Invoke to vJoyInterface.dll (native C DLL from vJoy SDK).
    /// Uses UpdateVJD for single-IOCTL-per-frame output (fastest path).
    /// </summary>
    internal static class VJoyNative
    {
        private const string DLL = "vJoyInterface.dll";

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool vJoyEnabled();

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern VjdStat GetVJDStatus(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AcquireVJD(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void RelinquishVJD(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ResetVJD(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateVJD(uint rID, ref JoystickPositionV2 pData);

        // ── Individual axis/button/POV setters ──

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetAxis(int value, uint rID, uint axis);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetBtn([MarshalAs(UnmanagedType.Bool)] bool value, uint rID, byte nBtn);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDiscPov(int value, uint rID, byte nPov);

        // HID Usage IDs for axes (Generic Desktop page 0x01)
        public const uint HID_USAGE_X  = 0x30;
        public const uint HID_USAGE_Y  = 0x31;
        public const uint HID_USAGE_Z  = 0x32;
        public const uint HID_USAGE_RX = 0x33;
        public const uint HID_USAGE_RY = 0x34;
        public const uint HID_USAGE_RZ = 0x35;

        // ── Force Feedback (FFB) ──

        /// <summary>Callback delegate for FfbRegisterGenCB.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FfbGenCB(IntPtr data, IntPtr userData);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FfbRegisterGenCB(FfbGenCB cb, IntPtr data);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_DeviceID(IntPtr packet, ref uint deviceId);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Type(IntPtr packet, ref FFBPType type);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_EffectBlockIndex(IntPtr packet, ref uint index);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Eff_Report(IntPtr packet, ref FFB_EFF_REPORT effect);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Eff_Constant(IntPtr packet, ref FFB_EFF_CONSTANT effect);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Eff_Ramp(IntPtr packet, ref FFB_EFF_RAMP effect);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Eff_Period(IntPtr packet, ref FFB_EFF_PERIOD effect);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Eff_Cond(IntPtr packet, ref FFB_EFF_COND effect);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_EffOp(IntPtr packet, ref FFB_EFF_OP operation);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_DevCtrl(IntPtr packet, ref FFB_CTRL control);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_DevGain(IntPtr packet, ref byte gain);
    }

    // ─────────────────────────────────────────────
    //  FFB enums and structs (matching vJoy SDK public.h)
    // ─────────────────────────────────────────────

    internal enum FFBEType : uint
    {
        ET_NONE  = 0,
        ET_CONST = 1,   // Constant Force
        ET_RAMP  = 2,   // Ramp
        ET_SQR   = 3,   // Square
        ET_SINE  = 4,   // Sine
        ET_TRNGL = 5,   // Triangle
        ET_STUP  = 6,   // Sawtooth Up
        ET_STDN  = 7,   // Sawtooth Down
        ET_SPRNG = 8,   // Spring
        ET_DMPR  = 9,   // Damper
        ET_INRT  = 10,  // Inertia
        ET_FRCTN = 11,  // Friction
        ET_CSTM  = 12,  // Custom Force Data
    }

    internal enum FFBPType : uint
    {
        PT_EFFREP   = 0x01,  // Set Effect Report
        PT_ENVREP   = 0x02,  // Set Envelope Report
        PT_CONDREP  = 0x03,  // Set Condition Report
        PT_PRIDREP  = 0x04,  // Set Periodic Report
        PT_CONSTREP = 0x05,  // Set Constant Force Report
        PT_RAMPREP  = 0x06,  // Set Ramp Force Report
        PT_CSTMREP  = 0x07,  // Custom Force Data Report
        PT_SMPLREP  = 0x08,  // Download Force Sample
        PT_EFOPREP  = 0x0A,  // Effect Operation Report
        PT_BLKFRREP = 0x0B,  // PID Block Free Report
        PT_CTRLREP  = 0x0C,  // PID Device Control
        PT_GAINREP  = 0x0D,  // Device Gain Report
        PT_SETCREP  = 0x0E,  // Set Custom Force Report
        PT_NEWEFREP = 0x11,  // Create New Effect Report
        PT_BLKLDREP = 0x12,  // Block Load Report
        PT_POOLREP  = 0x13,  // PID Pool Report
        PT_STATEREP = 0x14,  // PID State Report
    }

    internal enum FFB_CTRL : uint
    {
        CTRL_ENACT   = 1,  // Enable Actuators
        CTRL_DISACT  = 2,  // Disable Actuators
        CTRL_STOPALL = 3,  // Stop All Effects
        CTRL_DEVRST  = 4,  // Device Reset
        CTRL_DEVPAUSE = 5, // Device Pause
        CTRL_DEVCONT = 6,  // Device Continue
    }

    internal enum FFBOP : uint
    {
        EFF_START = 1,
        EFF_SOLO  = 2,
        EFF_STOP  = 3,
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_REPORT
    {
        [FieldOffset(0)]  public byte EffectBlockIndex;
        [FieldOffset(4)]  public FFBEType EffectType;
        [FieldOffset(8)]  public ushort Duration;
        [FieldOffset(10)] public ushort TrigerRpt;
        [FieldOffset(12)] public ushort SamplePrd;
        [FieldOffset(14)] public ushort StartDelay;
        [FieldOffset(16)] public byte Gain;
        [FieldOffset(17)] public byte TrigerBtn;
        [FieldOffset(18)] public byte AxesEnabledDirection;
        [FieldOffset(20)] public bool Polar;
        [FieldOffset(24)] public ushort Direction;   // Polar: 0–35999 hundredths of degrees
        [FieldOffset(24)] public ushort DirX;        // Cartesian (overlapped with Direction)
        [FieldOffset(26)] public ushort DirY;        // Cartesian
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_CONSTANT
    {
        [FieldOffset(0)] public byte EffectBlockIndex;
        [FieldOffset(4)] public short Magnitude;     // -10000..+10000
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_RAMP
    {
        [FieldOffset(0)] public byte EffectBlockIndex;
        [FieldOffset(4)] public short Start;         // -10000..+10000
        [FieldOffset(8)] public short End;           // -10000..+10000
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_PERIOD
    {
        [FieldOffset(0)]  public byte EffectBlockIndex;
        [FieldOffset(4)]  public uint Magnitude;      // 0..10000
        [FieldOffset(8)]  public short Offset;        // -10000..+10000
        [FieldOffset(12)] public uint Phase;           // 0..35999
        [FieldOffset(16)] public uint Period;          // ms
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_COND
    {
        [FieldOffset(0)]  public byte EffectBlockIndex;
        [FieldOffset(4)]  public bool IsY;
        [FieldOffset(8)]  public short CenterPointOffset;
        [FieldOffset(12)] public short PosCoeff;       // -10000..+10000
        [FieldOffset(16)] public short NegCoeff;       // -10000..+10000
        [FieldOffset(20)] public uint PosSatur;
        [FieldOffset(24)] public uint NegSatur;
        [FieldOffset(28)] public int DeadBand;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_OP
    {
        [FieldOffset(0)] public byte EffectBlockIndex;
        [FieldOffset(4)] public FFBOP EffectOp;
        [FieldOffset(8)] public byte LoopCount;
    }

}
