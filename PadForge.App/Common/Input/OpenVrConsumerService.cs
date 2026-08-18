using System;
using System.Runtime.InteropServices;
using System.Threading;
using SDL3;
using Valve.VR;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Consumes real VR hardware as PadForge input sources (#287): the HMD's
    /// pose becomes a "VR Headset" device (lean and orientation axes plus
    /// gyro/accel sensors, so head-lean can drive a stick axis and head
    /// motion rides the normal motion stack), and every tracked VR controller
    /// becomes a "VR Controller" device (joystick, analog trigger, analog
    /// grip, trackpad, buttons, gyro/accel).
    ///
    /// Architecture, each point grounded:
    ///  - One OpenVR CLIENT lane, initialized as VRApplication_Background:
    ///    such an app never launches SteamVR ("should not start SteamVR if
    ///    it's not already running", openvr.h:1695) and init answers
    ///    VRInitError_Init_NoServerForBackgroundApp (openvr.h:1821) while the
    ///    server is down, which is this service's quiet retry signal. This is
    ///    the architecture of the prior art the requester named (FreePIE VR
    ///    Companion): background poses + legacy GetControllerState.
    ///  - The client API is Valve's own vendored C# binding
    ///    (ThirdParty/OpenVR/openvr_api.cs, BSD-3), so the ABI layer is
    ///    Valve-maintained rather than hand-transcribed. The native
    ///    openvr_api.dll is NOT shipped: it is loaded from the user's own
    ///    SteamVR runtime, discovered through OpenVR's path registry
    ///    (%LOCALAPPDATA%\openvr\openvrpaths.vrpath, the exact file
    ///    vrpathregistry_public.cpp:145 reads), tolerant of stale entries
    ///    (this bench carried a registry pointing at a deleted install).
    ///  - Poses come from GetDeviceToAbsoluteTrackingPose, which also carries
    ///    runtime-computed velocity and angular velocity per device, so the
    ///    gyro/accel sensors are the runtime's own numbers rather than
    ///    finite differences of position (the #188 lesson: measure which
    ///    field carries the real signal; here the velocity fields are it).
    ///  - Devices provided by PadForge's OWN OpenVR driver are excluded
    ///    (Prop_ManufacturerName_String == "HIDMaestro",
    ///    controller_device.cpp:70), so a slot emitting virtual hands can
    ///    never consume its own output as input. Set PADFORGE_VR_CONSUME_SELF=1
    ///    to lift the filter (the hardware-free validation loop: PadForge's
    ///    virtual hands, tracked by SteamVR, read back through this lane).
    /// </summary>
    public sealed class OpenVrConsumerService
    {
        private const ushort VR_VID = 0x1209;        // pid.codes open-source VID, synthetic devices
        private const ushort PID_HMD = 0x2870;
        private const ushort PID_LEFT = 0x2871;
        private const ushort PID_RIGHT = 0x2872;
        private const ushort PID_OTHER = 0x2873;

        // Full-scale constants for the pose-derived axes. Lean full scale is
        // a seated torso lean (±35 cm reaches full deflection); orientation
        // scales favor usable in-game authority over full physical range.
        internal const float LeanFullScaleMeters = 0.35f;
        internal const float YawFullScaleDeg = 60f;
        internal const float PitchFullScaleDeg = 45f;
        internal const float RollFullScaleDeg = 45f;

        private readonly Action<string> _log;
        private Thread _thread;
        private volatile bool _running;

        /// <summary>True while the background client is connected to a
        /// running SteamVR. Feeds the dashboard's SteamVR status tiering
        /// (the issue's "the UI can only say installed" gap).</summary>
        public static bool ServerConnected => _serverConnected;
        private static volatile bool _serverConnected;

        /// <summary>Number of VR devices currently consumed as inputs.</summary>
        public static int ConsumedDeviceCount => _consumedCount;
        private static volatile int _consumedCount;

        public OpenVrConsumerService(Action<string> log = null) => _log = log ?? (_ => { });

        // ─── discovery (pure, test-locked) ──────────────────────────────────────

        /// <summary>Extracts runtime[0] from openvrpaths.vrpath JSON. Hand
        /// parser on purpose: the file is machine-written by OpenVR itself
        /// (vrpathregistry_public.cpp), tiny, and a malformed file must yield
        /// null rather than an exception on a poll thread. Pure.</summary>
        internal static string ParseRuntimePathFromVrPathJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int k = json.IndexOf("\"runtime\"", StringComparison.Ordinal);
            if (k < 0) return null;
            int open = json.IndexOf('[', k);
            if (open < 0) return null;
            int q1 = json.IndexOf('"', open + 1);
            if (q1 < 0) return null;
            int q2 = q1 + 1;
            var sb = new System.Text.StringBuilder();
            while (q2 < json.Length && json[q2] != '"')
            {
                char c = json[q2];
                if (c == '\\' && q2 + 1 < json.Length)
                {
                    q2++;
                    char e = json[q2];
                    sb.Append(e == '\\' ? '\\' : e == '/' ? '/' : e == '"' ? '"' : e);
                }
                else sb.Append(c);
                q2++;
            }
            string path = sb.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        internal static string RuntimeDllPath(string runtimeRoot)
            => string.IsNullOrEmpty(runtimeRoot)
                ? null
                : System.IO.Path.Combine(runtimeRoot, "bin", "win64", "openvr_api.dll");

        /// <summary>Finds the user's SteamVR runtime dll, or null. Honors the
        /// same VR_PATHREG_OVERRIDE the reference honors
        /// (vrpathregistry_public.cpp:136), then the standard per-user
        /// registry file. File-existence checked: a stale registry pointing
        /// at a removed install (observed on this bench) must read as
        /// absent, not as a load failure.</summary>
        private static string DiscoverRuntimeDll()
        {
            try
            {
                string reg = Environment.GetEnvironmentVariable("VR_PATHREG_OVERRIDE");
                if (string.IsNullOrEmpty(reg))
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    reg = System.IO.Path.Combine(local, "openvr", "openvrpaths.vrpath");
                }
                if (!System.IO.File.Exists(reg)) return null;
                string dll = RuntimeDllPath(ParseRuntimePathFromVrPathJson(System.IO.File.ReadAllText(reg)));
                return dll != null && System.IO.File.Exists(dll) ? dll : null;
            }
            catch { return null; }
        }

        // The vendored binding P/Invokes module "openvr_api"; this resolver
        // redirects that name to the discovered runtime dll. Registered once
        // per process (SetDllImportResolver throws on a second registration
        // for the same assembly).
        private static IntPtr _openvrModule = IntPtr.Zero;
        private static int _resolverRegistered;

        private static void EnsureResolver()
        {
            if (Interlocked.CompareExchange(ref _resolverRegistered, 1, 0) != 0) return;
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
                typeof(OpenVrConsumerService).Assembly,
                (name, assembly, searchPath) =>
                {
                    if (!string.Equals(name, "openvr_api", StringComparison.OrdinalIgnoreCase))
                        return IntPtr.Zero;
                    if (_openvrModule != IntPtr.Zero) return _openvrModule;
                    string dll = DiscoverRuntimeDll();
                    if (dll == null) return IntPtr.Zero;
                    if (System.Runtime.InteropServices.NativeLibrary.TryLoad(dll, out IntPtr h))
                        _openvrModule = h;
                    return _openvrModule;
                });
        }

        // ─── pose math (pure, test-locked) ──────────────────────────────────────
        //
        // OpenVR's tracking space is right-handed: +X right, +Y up, -Z forward
        // (so +Z points back toward the user, which is also SDL's sensor
        // convention). HmdMatrix34_t is row-major 3x4; the rotation's COLUMNS
        // are the device's basis axes expressed in world space:
        //   deviceX = (m0, m4, m8), deviceY = (m1, m5, m9), deviceZ = (m2, m6, m10)
        // and the device's forward vector is -deviceZ.

        /// <summary>Yaw/pitch/roll in degrees from a pose matrix. Conventions
        /// chosen for mapping ergonomics: yaw positive turning RIGHT, pitch
        /// positive looking UP, roll positive tilting the head RIGHT.
        /// Identity pose = (0, 0, 0). Pure.</summary>
        internal static (float YawDeg, float PitchDeg, float RollDeg) EulerFromPoseMatrix(in HmdMatrix34_t m)
        {
            // forward = -deviceZ
            float fx = -m.m2, fy = -m.m6, fz = -m.m10;
            float yaw = (float)(Math.Atan2(fx, -fz) * 180.0 / Math.PI);
            float pitch = (float)(Math.Asin(Math.Clamp(fy, -1f, 1f)) * 180.0 / Math.PI);
            // roll from the device X axis against the device Y axis's vertical
            // components: tilt-right lowers deviceX.y.
            float roll = (float)(Math.Atan2(-m.m4, m.m5) * 180.0 / Math.PI);
            return (yaw, pitch, roll);
        }

        /// <summary>Meters to a full SDL axis, clamped. Pure.</summary>
        internal static short AxisFromScaled(float value, float fullScale)
        {
            float n = Math.Clamp(value / fullScale, -1f, 1f);
            return (short)Math.Round(n * 32767f);
        }

        /// <summary>Wraps a degree delta into (-180, 180], so a yaw baseline
        /// subtraction can never produce a 359-degree lean. Pure.</summary>
        internal static float WrapDegrees(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg <= -180f) deg += 360f;
            return deg;
        }

        /// <summary>World-space vector into the device's frame (R-transpose
        /// times v: dot with each device basis column). The runtime reports
        /// velocity and angular velocity in tracking space, and the motion
        /// stack expects device-frame IMU rates. Pure.</summary>
        internal static (float X, float Y, float Z) WorldToDevice(in HmdMatrix34_t m, float wx, float wy, float wz)
        {
            return (
                m.m0 * wx + m.m4 * wy + m.m8 * wz,
                m.m1 * wx + m.m5 * wy + m.m9 * wz,
                m.m2 * wx + m.m6 * wy + m.m10 * wz);
        }

        // ─── controller-axis classification (pure, test-locked) ─────────────────

        internal enum VrAxisRole { None, Joystick, TrackPad, Trigger, Grip }

        /// <summary>Classifies the five rAxis slots from their
        /// Prop_AxisNType_Int32 values (k_eControllerAxis_*): the first
        /// trigger-typed axis is THE trigger, the second is the analog grip
        /// (the WMR/Index convention, and the analog grip is exactly what the
        /// requester wanted over a digital bumper). Pure.</summary>
        internal static VrAxisRole[] ClassifyAxes(int[] axisTypes)
        {
            var roles = new VrAxisRole[5];
            bool triggerSeen = false;
            for (int i = 0; i < 5 && i < axisTypes.Length; i++)
            {
                switch (axisTypes[i])
                {
                    case 2: roles[i] = VrAxisRole.Joystick; break;   // k_eControllerAxis_Joystick
                    case 1: roles[i] = VrAxisRole.TrackPad; break;   // k_eControllerAxis_TrackPad
                    case 3:                                          // k_eControllerAxis_Trigger
                        roles[i] = triggerSeen ? VrAxisRole.Grip : VrAxisRole.Trigger;
                        triggerSeen = true;
                        break;
                    default: roles[i] = VrAxisRole.None; break;
                }
            }
            return roles;
        }

        internal static bool ButtonPressed(ulong mask, int buttonId)
            => (mask & (1ul << buttonId)) != 0;

        /// <summary>The self-emission filter (#49 interplay rule): devices our
        /// own OpenVR driver provides report Manufacturer "HIDMaestro"
        /// (controller_device.cpp:70) and are never consumed, or a slot
        /// driving virtual hands would read its own output back as input.
        /// PADFORGE_VR_CONSUME_SELF=1 lifts it for the hardware-free
        /// validation loop. Pure but for the env read.</summary>
        internal static bool IsSelfEmitted(string manufacturer, bool consumeSelfOverride)
            => !consumeSelfOverride
               && string.Equals(manufacturer, "HIDMaestro", StringComparison.OrdinalIgnoreCase);

        // ─── lifecycle ──────────────────────────────────────────────────────────

        public void Start()
        {
            if (_running) return;
            _running = true;
            EnsureResolver();
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "OpenVrConsumer" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _thread?.Join(2000); } catch { }
        }

        private void RunLoop()
        {
            bool loggedNoRuntime = false, loggedNoServer = false;
            while (_running)
            {
                if (DiscoverRuntimeDll() == null)
                {
                    if (!loggedNoRuntime)
                    {
                        loggedNoRuntime = true;
                        _log("VRCONSUME no SteamVR runtime found (openvrpaths registry absent or stale); watching.");
                    }
                    SleepInterruptibly(5000);
                    continue;
                }
                loggedNoRuntime = false;

                EVRInitError err = EVRInitError.None;
                CVRSystem system = null;
                try { system = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background); }
                catch (DllNotFoundException) { SleepInterruptibly(5000); continue; }
                catch (Exception ex)
                {
                    _log("VRCONSUME init threw: " + ex.Message);
                    SleepInterruptibly(15000);
                    continue;
                }

                if (system == null || err != EVRInitError.None)
                {
                    if (err == EVRInitError.Init_NoServerForBackgroundApp)
                    {
                        // SteamVR is not running. The documented quiet state:
                        // a Background app never launches it (openvr.h:1695).
                        if (!loggedNoServer)
                        {
                            loggedNoServer = true;
                            _log("VRCONSUME SteamVR not running; a background consumer never launches it. Watching.");
                        }
                    }
                    else
                    {
                        _log($"VRCONSUME init failed: {err}");
                        loggedNoServer = false;
                    }
                    try { OpenVR.Shutdown(); } catch { }
                    SleepInterruptibly(5000);
                    continue;
                }
                loggedNoServer = false;
                _log("VRCONSUME connected to SteamVR (background client).");
                _serverConnected = true;

                try { Session(system); }
                catch (Exception ex) { _log("VRCONSUME session error: " + ex.Message); }
                finally
                {
                    _serverConnected = false;
                    DetachAll();
                    try { OpenVR.Shutdown(); } catch { }
                    _log("VRCONSUME disconnected from SteamVR.");
                }
                SleepInterruptibly(2000);
            }
            DetachAll();
        }

        private void SleepInterruptibly(int ms)
        {
            for (int slept = 0; slept < ms && _running; slept += 100)
                Thread.Sleep(100);
        }

        // ─── the session ────────────────────────────────────────────────────────

        private sealed class Consumed
        {
            public uint DeviceIndex;
            public uint SdlId;
            public IntPtr Joystick;
            public bool IsHmd;
            public string Name;
            public VrAxisRole[] AxisRoles;
            public int JoyAxis = -1, PadAxis = -1, TriggerAxis = -1, GripAxis = -1;
            public ETrackedControllerRole Role;
            // HMD baseline (position + yaw), captured at first valid pose and
            // re-captured after a long invalid stretch (taking the headset off
            // and back on should not leave a huge stale lean).
            public bool BaselineSet;
            public float BaseX, BaseY, BaseZ, BaseYawDeg;
            public long LastValidTick;
            // For accel derivation: previous world velocity + timestamp.
            public bool HadVel;
            public float PrevVx, PrevVy, PrevVz;
            public long PrevVelTick;
            public float[] AccelBuf = new float[3];
            public float[] GyroBuf = new float[3];
        }

        private readonly Consumed[] _consumed = new Consumed[OpenVR.k_unMaxTrackedDeviceCount];
        private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        private void Session(CVRSystem system)
        {
            bool consumeSelf = Environment.GetEnvironmentVariable("PADFORGE_VR_CONSUME_SELF") == "1";
            var ev = new VREvent_t();
            uint evSize = (uint)Marshal.SizeOf<VREvent_t>();
            var state = new VRControllerState_t();
            uint stateSize = (uint)Marshal.SizeOf<VRControllerState_t>();

            while (_running)
            {
                // Drain events; a quitting SteamVR expects background apps to
                // shut down promptly.
                while (system.PollNextEvent(ref ev, evSize))
                {
                    if ((EVREventType)ev.eventType == EVREventType.VREvent_Quit)
                    {
                        _log("VRCONSUME SteamVR is quitting.");
                        return;
                    }
                }

                system.GetDeviceToAbsoluteTrackingPose(
                    ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, _poses);

                long now = Environment.TickCount64;
                for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
                {
                    var cls = system.GetTrackedDeviceClass(i);
                    bool wanted = cls == ETrackedDeviceClass.HMD || cls == ETrackedDeviceClass.Controller;
                    bool connected = wanted && _poses[i].bDeviceIsConnected;

                    var slot = _consumed[i];
                    if (!connected)
                    {
                        if (slot != null) Detach(i);
                        continue;
                    }

                    if (slot == null)
                    {
                        slot = TryAttach(system, i, cls, consumeSelf);
                        if (slot == null) continue;   // filtered (self) or attach failed
                    }
                    else if (!slot.IsHmd)
                    {
                        // Hand roles can churn while SteamVR settles; a changed
                        // role recycles the device so its name stays truthful.
                        var role = system.GetControllerRoleForTrackedDeviceIndex(i);
                        if (role != slot.Role &&
                            (role == ETrackedControllerRole.LeftHand || role == ETrackedControllerRole.RightHand))
                        {
                            _log($"VRCONSUME device {i} hand role changed; recycling.");
                            Detach(i);
                            continue;
                        }
                    }

                    if (slot.IsHmd) PushHmd(slot, ref _poses[i], now);
                    else PushController(system, slot, ref _poses[i], ref state, stateSize, now);
                }

                Thread.Sleep(11);   // ~90 Hz
            }
        }

        private Consumed TryAttach(CVRSystem system, uint idx, ETrackedDeviceClass cls, bool consumeSelf)
        {
            var perr = ETrackedPropertyError.TrackedProp_Success;
            var sb = new System.Text.StringBuilder(128);
            system.GetStringTrackedDeviceProperty(idx, ETrackedDeviceProperty.Prop_ManufacturerName_String, sb, 128, ref perr);
            string manufacturer = sb.ToString();
            if (IsSelfEmitted(manufacturer, consumeSelf))
            {
                // Logged once per index per session via the null slot staying
                // null; keep it quiet (the check runs every tick).
                if (_consumed[idx] == null && _selfFilteredLogged.Add(idx))
                    _log($"VRCONSUME device {idx} is PadForge's own virtual hand; not consumed.");
                return null;
            }

            bool isHmd = cls == ETrackedDeviceClass.HMD;
            var role = isHmd ? ETrackedControllerRole.Invalid
                             : system.GetControllerRoleForTrackedDeviceIndex(idx);

            string name;
            ushort pid;
            if (isHmd) { name = "VR Headset"; pid = PID_HMD; }
            else if (role == ETrackedControllerRole.LeftHand) { name = "VR Controller (Left Hand)"; pid = PID_LEFT; }
            else if (role == ETrackedControllerRole.RightHand) { name = "VR Controller (Right Hand)"; pid = PID_RIGHT; }
            else { name = $"VR Controller {idx}"; pid = PID_OTHER; }

            var slot = new Consumed { DeviceIndex = idx, IsHmd = isHmd, Name = name, Role = role };

            if (!isHmd)
            {
                var types = new int[5];
                for (int a = 0; a < 5; a++)
                {
                    perr = ETrackedPropertyError.TrackedProp_Success;
                    types[a] = system.GetInt32TrackedDeviceProperty(idx,
                        (ETrackedDeviceProperty)((int)ETrackedDeviceProperty.Prop_Axis0Type_Int32 + a), ref perr);
                }
                slot.AxisRoles = ClassifyAxes(types);
                for (int a = 0; a < 5; a++)
                {
                    if (slot.AxisRoles[a] == VrAxisRole.Joystick && slot.JoyAxis < 0) slot.JoyAxis = a;
                    else if (slot.AxisRoles[a] == VrAxisRole.TrackPad && slot.PadAxis < 0) slot.PadAxis = a;
                    else if (slot.AxisRoles[a] == VrAxisRole.Trigger) slot.TriggerAxis = a;
                    else if (slot.AxisRoles[a] == VrAxisRole.Grip) slot.GripAxis = a;
                }
            }

            if (!AttachVirtual(slot, pid)) return null;
            _consumed[idx] = slot;
            _consumedCount++;
            _log($"VRCONSUME + {name} (device {idx}, class {cls}, mfr \"{manufacturer}\""
                + (isHmd ? "" : $", axes joy={slot.JoyAxis} pad={slot.PadAxis} trig={slot.TriggerAxis} grip={slot.GripAxis}")
                + ")");
            return slot;
        }

        private readonly System.Collections.Generic.HashSet<uint> _selfFilteredLogged = new();

        private bool AttachVirtual(Consumed slot, ushort pid)
        {
            var namePtr = Marshal.StringToHGlobalAnsi(slot.Name);
            var sensors = new SDL.SDL_VirtualJoystickSensorDesc[]
            {
                new SDL.SDL_VirtualJoystickSensorDesc { type = SDL_SENSOR_ACCEL, rate = 90.0f },
                new SDL.SDL_VirtualJoystickSensorDesc { type = SDL_SENSOR_GYRO,  rate = 90.0f },
            };
            int sensorSize = Marshal.SizeOf<SDL.SDL_VirtualJoystickSensorDesc>();
            IntPtr sensorsPtr = Marshal.AllocHGlobal(sensorSize * sensors.Length);
            try
            {
                for (int i = 0; i < sensors.Length; i++)
                    Marshal.StructureToPtr(sensors[i], sensorsPtr + i * sensorSize, false);

                // SEQUENTIAL-INDEX RULE (the #277 sparse-mask tattoo): SDL's
                // virtual mapping binds gamepad controls to sequential
                // joystick indices for the mask bits present. Push indices
                // below follow the sequential walk, never the bit positions.
                var desc = new SDL.SDL_VirtualJoystickDesc
                {
                    type = (ushort)SDL.SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD,
                    vendor_id = VR_VID,
                    product_id = pid,
                    nsensors = (ushort)sensors.Length,
                    sensors = sensorsPtr,
                    name = namePtr,
                };
                if (slot.IsHmd)
                {
                    // Axes: LX=lean X (right +), LY=lean fwd/back (forward =
                    // stick-up = negative), RX=yaw (right +), RY=pitch (up +),
                    // raw 4=vertical lean (up +), raw 5=roll (right +).
                    // Button 0 (South) = pose valid ("worn"), usable as an
                    // activator.
                    desc.naxes = 6;
                    desc.nbuttons = 1;
                    desc.axis_mask = 0x0F;      // LX LY RX RY -> sequential 0-3
                    desc.button_mask = 0x01;    // South -> 0
                }
                else
                {
                    // Axes: joystick->LX/LY (seq 0/1), grip->LT (seq 2),
                    // trigger->RT (seq 3), trackpad->raw 4/5.
                    // Buttons: South=A, Back=System, Start=AppMenu,
                    // LeftStick=stick click, LeftShoulder=grip click,
                    // RightShoulder=trackpad click; sequential 0-5.
                    desc.naxes = 6;
                    desc.nbuttons = 6;
                    desc.axis_mask = 0x33;      // LX LY LT RT -> sequential 0-3
                    desc.button_mask = 0x06D1;  // S,Back,Start,LStick,LShldr,RShldr
                }
                desc.version = (uint)Marshal.SizeOf<SDL.SDL_VirtualJoystickDesc>();

                slot.SdlId = SDL.SDL_AttachVirtualJoystick(ref desc);
                if (slot.SdlId == 0)
                {
                    _log($"VRCONSUME attach failed for {slot.Name}.");
                    return false;
                }
                slot.Joystick = SDL.SDL_OpenJoystick(slot.SdlId);
                if (slot.Joystick == IntPtr.Zero)
                {
                    SDL.SDL_DetachVirtualJoystick(slot.SdlId);
                    slot.SdlId = 0;
                    return false;
                }
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(sensorsPtr);
                Marshal.FreeHGlobal(namePtr);
            }
        }

        private void Detach(uint idx)
        {
            var slot = _consumed[idx];
            if (slot == null) return;
            _consumed[idx] = null;
            if (_consumedCount > 0) _consumedCount--;
            if (slot.Joystick != IntPtr.Zero) SDL.SDL_CloseJoystick(slot.Joystick);
            if (slot.SdlId != 0) SDL.SDL_DetachVirtualJoystick(slot.SdlId);
            _log($"VRCONSUME - {slot.Name} (device {idx}).");
        }

        private void DetachAll()
        {
            for (uint i = 0; i < _consumed.Length; i++) Detach(i);
            _selfFilteredLogged.Clear();
        }

        // ─── pushes ─────────────────────────────────────────────────────────────

        private const int SDL_SENSOR_ACCEL = 1;
        private const int SDL_SENSOR_GYRO = 2;
        private const float Gravity = 9.80665f;

        private void PushHmd(Consumed slot, ref TrackedDevicePose_t pose, long now)
        {
            IntPtr j = slot.Joystick;
            if (j == IntPtr.Zero) return;

            bool valid = pose.bPoseIsValid;
            ref var m = ref pose.mDeviceToAbsoluteTracking;
            float px = m.m3, py = m.m7, pz = m.m11;
            var (yaw, pitch, roll) = EulerFromPoseMatrix(in m);

            if (valid)
            {
                // Baseline at first validity, and again after a long invalid
                // stretch (headset taken off): lean is relative to where the
                // user settled, not to the room origin.
                if (!slot.BaselineSet || now - slot.LastValidTick > 5000)
                {
                    slot.BaselineSet = true;
                    slot.BaseX = px; slot.BaseY = py; slot.BaseZ = pz;
                    slot.BaseYawDeg = yaw;
                    slot.HadVel = false;
                    _log($"VRCONSUME headset baseline captured (yaw {yaw:F0} deg).");
                }
                slot.LastValidTick = now;
            }

            SDL.SDL_LockJoysticks();
            try
            {
                SDL.SDL_SetJoystickVirtualButton(j, 0, valid);
                if (valid && slot.BaselineSet)
                {
                    float dx = px - slot.BaseX, dy = py - slot.BaseY, dz = pz - slot.BaseZ;
                    float relYaw = WrapDegrees(yaw - slot.BaseYawDeg);
                    SDL.SDL_SetJoystickVirtualAxis(j, 0, AxisFromScaled(dx, LeanFullScaleMeters));         // lean right +
                    SDL.SDL_SetJoystickVirtualAxis(j, 1, AxisFromScaled(dz, LeanFullScaleMeters));         // fwd = -Z -> stick-up (negative)
                    SDL.SDL_SetJoystickVirtualAxis(j, 2, AxisFromScaled(relYaw, YawFullScaleDeg));         // yaw right +
                    SDL.SDL_SetJoystickVirtualAxis(j, 3, AxisFromScaled(-pitch, PitchFullScaleDeg));       // look up = RY negative (stick up)
                    SDL.SDL_SetJoystickVirtualAxis(j, 4, AxisFromScaled(dy, LeanFullScaleMeters));         // rise +
                    SDL.SDL_SetJoystickVirtualAxis(j, 5, AxisFromScaled(roll, RollFullScaleDeg));          // tilt right +
                    PushSensors(slot, ref pose, now);
                }
            }
            finally { SDL.SDL_UnlockJoysticks(); }
        }

        private void PushController(CVRSystem system, Consumed slot, ref TrackedDevicePose_t pose,
            ref VRControllerState_t state, uint stateSize, long now)
        {
            IntPtr j = slot.Joystick;
            if (j == IntPtr.Zero) return;

            bool haveState = system.GetControllerState(slot.DeviceIndex, ref state, stateSize);

            SDL.SDL_LockJoysticks();
            try
            {
                if (haveState)
                {
                    ulong pressed = state.ulButtonPressed;
                    // Buttons, sequential indices per the desc's mask:
                    // 0=South(A), 1=Back(System), 2=Start(AppMenu),
                    // 3=LeftStick(joystick click), 4=LeftShoulder(grip click),
                    // 5=RightShoulder(trackpad click). Note: SteamVR reserves
                    // the System button for its dashboard on many setups, so
                    // Back may never fire; kept for the setups that pass it.
                    SDL.SDL_SetJoystickVirtualButton(j, 0, ButtonPressed(pressed, 7));   // k_EButton_A
                    SDL.SDL_SetJoystickVirtualButton(j, 1, ButtonPressed(pressed, 0));   // System
                    SDL.SDL_SetJoystickVirtualButton(j, 2, ButtonPressed(pressed, 1));   // ApplicationMenu
                    if (slot.JoyAxis >= 0)
                        SDL.SDL_SetJoystickVirtualButton(j, 3, ButtonPressed(pressed, 32 + slot.JoyAxis));
                    SDL.SDL_SetJoystickVirtualButton(j, 4, ButtonPressed(pressed, 2));   // Grip click
                    if (slot.PadAxis >= 0)
                        SDL.SDL_SetJoystickVirtualButton(j, 5, ButtonPressed(pressed, 32 + slot.PadAxis));

                    // Axes. OpenVR joystick/trackpad y is up-positive; SDL
                    // stick y is up-negative. Triggers ride 0..1.
                    var ax = GetAxis(ref state, slot.JoyAxis);
                    SDL.SDL_SetJoystickVirtualAxis(j, 0, (short)Math.Clamp(ax.x * 32767f, -32767f, 32767f));
                    SDL.SDL_SetJoystickVirtualAxis(j, 1, (short)Math.Clamp(-ax.y * 32767f, -32767f, 32767f));
                    var grip = GetAxis(ref state, slot.GripAxis);
                    SDL.SDL_SetJoystickVirtualAxis(j, 2, TriggerAxis01(grip.x));
                    var trig = GetAxis(ref state, slot.TriggerAxis);
                    SDL.SDL_SetJoystickVirtualAxis(j, 3, TriggerAxis01(trig.x));
                    var pad = GetAxis(ref state, slot.PadAxis);
                    SDL.SDL_SetJoystickVirtualAxis(j, 4, (short)Math.Clamp(pad.x * 32767f, -32767f, 32767f));
                    SDL.SDL_SetJoystickVirtualAxis(j, 5, (short)Math.Clamp(pad.y * 32767f, -32767f, 32767f));
                }

                if (pose.bPoseIsValid)
                    PushSensors(slot, ref pose, now);
            }
            finally { SDL.SDL_UnlockJoysticks(); }
        }

        /// <summary>0..1 analog to the SDL trigger scale (released = MIN). Pure.</summary>
        internal static short TriggerAxis01(float v)
            => (short)Math.Round(Math.Clamp(v, 0f, 1f) * 65534f - 32767f);

        private static VRControllerAxis_t GetAxis(ref VRControllerState_t s, int idx)
        {
            switch (idx)
            {
                case 0: return s.rAxis0;
                case 1: return s.rAxis1;
                case 2: return s.rAxis2;
                case 3: return s.rAxis3;
                case 4: return s.rAxis4;
                default: return default;
            }
        }

        /// <summary>Sensors from the runtime's own velocity fields, rotated
        /// into the device frame (the motion stack expects IMU-style rates).
        /// Gyro = angular velocity (rad/s). Accel = the derivative of linear
        /// velocity plus gravity reaction, so a still device reads +1 g up
        /// like a real IMU.</summary>
        private void PushSensors(Consumed slot, ref TrackedDevicePose_t pose, long now)
        {
            ref var m = ref pose.mDeviceToAbsoluteTracking;
            ulong ts = SDL.SDL_GetTicksNS();

            var (gx, gy, gz) = WorldToDevice(in m,
                pose.vAngularVelocity.v0, pose.vAngularVelocity.v1, pose.vAngularVelocity.v2);
            slot.GyroBuf[0] = gx; slot.GyroBuf[1] = gy; slot.GyroBuf[2] = gz;
            SDL.SDL_SendJoystickVirtualSensorData(slot.Joystick, SDL_SENSOR_GYRO, ts, slot.GyroBuf, 3);

            float wx = pose.vVelocity.v0, wy = pose.vVelocity.v1, wz = pose.vVelocity.v2;
            float axw = 0f, ayw = Gravity, azw = 0f;   // still device: gravity reaction, up
            if (slot.HadVel)
            {
                float dt = (now - slot.PrevVelTick) / 1000f;
                if (dt > 0.001f && dt < 0.25f)
                {
                    axw += (wx - slot.PrevVx) / dt;
                    ayw += (wy - slot.PrevVy) / dt;
                    azw += (wz - slot.PrevVz) / dt;
                }
            }
            slot.PrevVx = wx; slot.PrevVy = wy; slot.PrevVz = wz;
            slot.PrevVelTick = now; slot.HadVel = true;

            var (ax, ay, az) = WorldToDevice(in m, axw, ayw, azw);
            slot.AccelBuf[0] = ax; slot.AccelBuf[1] = ay; slot.AccelBuf[2] = az;
            SDL.SDL_SendJoystickVirtualSensorData(slot.Joystick, SDL_SENSOR_ACCEL, ts, slot.AccelBuf, 3);
        }
    }
}
