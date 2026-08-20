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
        /// <summary>Whether the poll thread is live. Internal for the
        /// release-latch test, which asserts Start refuses after
        /// ReleaseRuntime.</summary>
        internal bool IsRunning => _running;

        /// <summary>True while the background client is connected to a
        /// running SteamVR. Feeds the dashboard's SteamVR status tiering
        /// (the issue's "the UI can only say installed" gap).</summary>
        public static bool ServerConnected => _serverConnected;
        private static volatile bool _serverConnected;
        // The instance whose session set the flag: a timed-out Stop can
        // leave an old thread unwinding while a successor's session is
        // live, and the old finally must not clear the successor's state
        // (2026-08-18 audit).
        private static volatile OpenVrConsumerService _serverOwner;

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

        /// <summary>Runtime root recorded in the path registry, or null.
        /// Same discovery <see cref="DiscoverRuntimeDll"/> uses, stopping at
        /// the install directory instead of descending to the dll.</summary>
        private static string DiscoverRuntimeRoot()
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
                string root = ParseRuntimePathFromVrPathJson(System.IO.File.ReadAllText(reg));
                return string.IsNullOrWhiteSpace(root) ? null : root;
            }
            catch { return null; }
        }

        /// <summary>
        /// Keeps SteamVR's log and config data inside the SteamVR folder.
        ///
        /// <para>openvr_api.dll writes "vrclient_{process}.txt" to the log
        /// directory named in openvrpaths.vrpath, and that entry is
        /// conventionally the runtime path with "-logs" appended, a SIBLING
        /// of the install rather than part of it. With SteamVR at its default
        /// C:\SteamVR, loading the client therefore made PadForge create
        /// C:\SteamVR-logs at the root of the system drive, with C:\SteamVR-config
        /// alongside it. SteamVR's data belongs in the SteamVR folder the user
        /// chose, so both are redirected under it.</para>
        ///
        /// <para>VR_LOG_PATH and VR_CONFIG_PATH override the registry for the
        /// CALLING PROCESS only (vrpathregistry_public.cpp:406 and :384, names
        /// from vrpathregistry_public.h:9-10), so this changes where PadForge's
        /// own client writes without editing a file every VR app shares. A
        /// value already in the environment is left alone: someone who set one
        /// meant it. With no runtime installed there is nothing to be inside
        /// of, so nothing is set.</para>
        /// </summary>
        private static void ContainVrClientLog()
        {
            try
            {
                string root = DiscoverRuntimeRoot();
                if (string.IsNullOrWhiteSpace(root)) return;
                Set("VR_LOG_PATH", System.IO.Path.Combine(root, "logs"));
                Set("VR_CONFIG_PATH", System.IO.Path.Combine(root, "config"));
            }
            catch { }

            static void Set(string name, string dir)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                        return;
                    System.IO.Directory.CreateDirectory(dir);
                    Environment.SetEnvironmentVariable(name, dir);
                }
                catch { }
            }
        }

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
                    // At the moment of load, never merely at registration:
                    // the client reads VR_LOG_PATH/VR_CONFIG_PATH as it
                    // initializes. Registration happens at engine start, and
                    // a SteamVR installed later in the session (the normal
                    // Settings-card flow) would otherwise load with neither
                    // set, putting C:\SteamVR-logs back at the drive root.
                    ContainVrClientLog();
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
        /// requester wanted over a digital bumper). Drivers on the modern
        /// input system (IVRDriverInput) never set these properties, and
        /// vrserver's legacy emulation does not synthesize them either
        /// (bench-measured: state flows, all five types read 0). Their rAxis
        /// layout is still fixed by the legacy-binding convention every
        /// shipped binding follows (axis0=stick/pad position, axis1=trigger
        /// pull, axis2=grip pull: legacy_bindings_pico_controller.json,
        /// legacy_bindings_index_controller.json), so an all-None read falls
        /// back to that convention. Pure.</summary>
        internal static VrAxisRole[] ClassifyAxes(int[] axisTypes)
        {
            var roles = new VrAxisRole[5];
            bool any = false, triggerSeen = false;
            for (int i = 0; i < 5 && i < axisTypes.Length; i++)
            {
                switch (axisTypes[i])
                {
                    case 2: roles[i] = VrAxisRole.Joystick; any = true; break;   // k_eControllerAxis_Joystick
                    case 1: roles[i] = VrAxisRole.TrackPad; any = true; break;   // k_eControllerAxis_TrackPad
                    case 3:                                                      // k_eControllerAxis_Trigger
                        roles[i] = triggerSeen ? VrAxisRole.Grip : VrAxisRole.Trigger;
                        triggerSeen = true;
                        any = true;
                        break;
                    default: roles[i] = VrAxisRole.None; break;
                }
            }
            if (!any)
            {
                roles[0] = VrAxisRole.Joystick;
                roles[1] = VrAxisRole.Trigger;
                roles[2] = VrAxisRole.Grip;
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

        /// <summary>The instance currently polling, for
        /// <see cref="ReleaseRuntime"/>. The uninstall has to stop the loop
        /// before it can free the dll, and it has no reference to the
        /// InputManager that owns this.</summary>
        private static volatile OpenVrConsumerService _live;

        /// <summary>
        /// Stops polling and unloads openvr_api.dll, so the SteamVR directory
        /// can be deleted.
        ///
        /// <para>The resolver loads the runtime's dll into this process and
        /// caches the handle for the lifetime of the process. Nothing ever
        /// freed it, so Directory.Delete could not remove the one file
        /// PadForge itself held open: an uninstall reported success and left
        /// SteamVR bin\win64\openvr_api.dll on disk, which most people would
        /// never notice and could only clear by hand after closing PadForge.</para>
        ///
        /// <para>A release latch IS needed afterwards, and an earlier version
        /// of this comment claimed otherwise. P/Invoke stubs bind to the
        /// resolved address on first use and are never re-resolved, so after
        /// the Free every OpenVR entry point this process already called
        /// points into an unloaded module. A reinstall plus an engine restart
        /// would march a fresh consumer straight into OpenVR.Init through
        /// that dangling stub: a native access violation, uncatchable. Start
        /// therefore refuses for the rest of the process, and VR device
        /// consumption resumes on the next PadForge launch. The VR virtual
        /// controllers are unaffected either way: HIDMaestro's OpenVR driver
        /// loads in vrserver's process, not this one.</para>
        /// </summary>
        internal static void ReleaseRuntime()
        {
            _runtimeReleased = true;
            try { _live?.Stop(); } catch { }
            // Only shut down a session that could exist. Calling OpenVR.Shutdown
            // with the module never loaded would RESOLVE it: the P/Invoke would
            // run discovery, load the dll this method exists to free, and bind
            // a stub to it, manufacturing the exact dangling reference the
            // latch guards against, on machines where VR was never used.
            if (_openvrModule != IntPtr.Zero)
            {
                try { OpenVR.Shutdown(); } catch { }
            }
            IntPtr h = _openvrModule;
            _openvrModule = IntPtr.Zero;
            if (h != IntPtr.Zero)
            {
                try { System.Runtime.InteropServices.NativeLibrary.Free(h); } catch { }
            }
        }

        /// <summary>True once ReleaseRuntime has freed the OpenVR module.
        /// Never cleared: the dangling-stub hazard above lasts until the
        /// process ends. Internal for the tests.</summary>
        internal static bool RuntimeReleased => _runtimeReleased;
        private static volatile bool _runtimeReleased;

        public void Start()
        {
            if (_running) return;
            if (_runtimeReleased)
            {
                // See ReleaseRuntime: the module was freed and this
                // process's OpenVR stubs dangle. Polling again would AV.
                _log("VRCONSUME runtime was released for an uninstall; VR device consumption resumes on the next PadForge launch.");
                return;
            }
            // Everything that can throw happens BEFORE the running latch
            // (2026-08-18 audit): the old order set _running first, so a
            // resolver or thread-construction throw left the instance
            // latched "running" with no thread, permanently unstartable.
            EnsureResolver();
            var t = new Thread(RunLoop) { IsBackground = true, Name = "OpenVrConsumer" };
            _thread = t;
            _running = true;
            _live = this;
            t.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _thread?.Join(2000); } catch { }
            // Same successor rule the server-owner latch follows: a late Stop
            // from a superseded instance must not unregister its replacement.
            if (ReferenceEquals(_live, this)) _live = null;
        }

        private void RunLoop()
        {
            // Top-level guard (2026-08-18 audit): an unhandled exception on
            // a managed background thread is process-fatal, and this loop
            // calls into two native stacks.
            try { RunLoopCore(); }
            catch (Exception ex)
            {
                try { _log("VRCONSUME loop fault " + ex.GetType().Name + ": " + ex.Message); }
                catch { }
            }
        }

        private void RunLoopCore()
        {
            bool loggedNoRuntime = false, loggedDllLoadFail = false;
            EVRInitError lastLoggedErr = EVRInitError.None;
            while (_running)
            {
                if (DiscoverRuntimeDll() == null)
                {
                    if (!loggedNoRuntime)
                    {
                        loggedNoRuntime = true;
                        _log("VRCONSUME no SteamVR runtime found (openvrpaths registry absent, stale, or unreadable); watching.");
                    }
                    SleepInterruptibly(5000);
                    continue;
                }
                loggedNoRuntime = false;

                EVRInitError err = EVRInitError.None;
                CVRSystem system = null;
                try { system = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background); }
                catch (DllNotFoundException)
                {
                    // The registry named a dll that exists but would not
                    // load (architecture, missing VC runtime, AV block).
                    // Without this line the state was fully silent
                    // (2026-08-18 audit).
                    if (!loggedDllLoadFail)
                    {
                        loggedDllLoadFail = true;
                        _log("VRCONSUME openvr_api.dll present but failed to load; watching.");
                    }
                    SleepInterruptibly(5000);
                    continue;
                }
                catch (Exception ex)
                {
                    // The binding can throw after the native init already
                    // succeeded (SetSDKVersion dispatch); OpenVR.Init only
                    // shuts down on error RETURNS, so release the token
                    // here or each retry leaks a session.
                    try { OpenVR.Shutdown(); } catch { }
                    _log("VRCONSUME init threw: " + ex.Message);
                    SleepInterruptibly(15000);
                    continue;
                }
                loggedDllLoadFail = false;

                if (system == null || err != EVRInitError.None)
                {
                    // Log each distinct error once, not once per 5 s retry
                    // (the old else-branch reset the latch every pass).
                    // NoServer and InitCanceledByUser are the quiet states:
                    // the first is SteamVR simply not running (openvr.h:1695),
                    // the second is the user telling the runtime no
                    // (openvr.h: "the calling application should silently
                    // exit"), so neither deserves a per-retry line.
                    if (err != lastLoggedErr)
                    {
                        lastLoggedErr = err;
                        if (err == EVRInitError.Init_NoServerForBackgroundApp)
                            _log("VRCONSUME SteamVR not running; a background consumer never launches it. Watching.");
                        else if (err == EVRInitError.Init_InitCanceledByUser)
                            _log("VRCONSUME init canceled by the user; watching quietly.");
                        else if (err == EVRInitError.Init_HmdNotFound
                              || err == EVRInitError.Init_HmdNotFoundPresenceFailed)
                            // No headset attached. This is the ORDINARY state
                            // for the many people who install SteamVR through
                            // PadForge's own Settings card to get virtual VR
                            // slots (#49) and own no HMD at all. Consuming real
                            // VR devices (#287) is what needs one. Wording it
                            // as a failure put an error-class line in every
                            // diagnostics harvest on such a machine, which is
                            // the same defect already fixed for a device with
                            // no SDL haptic interface (b1e09abd): an expected
                            // absence is not a failure.
                            _log("VRCONSUME no headset attached; virtual VR slots are unaffected. Watching.");
                        else
                            _log($"VRCONSUME init failed: {err}");
                    }
                    // A failed OpenVR.Init already shut its own session down
                    // (the binding calls ShutdownInternal on error returns),
                    // so no second Shutdown here.
                    SleepInterruptibly(5000);
                    continue;
                }
                lastLoggedErr = EVRInitError.None;
                _log("VRCONSUME connected to SteamVR (background client).");
                _serverOwner = this;
                _serverConnected = true;

                try { Session(system); }
                catch (Exception ex) { _log("VRCONSUME session error: " + ex.Message); }
                finally
                {
                    if (ReferenceEquals(_serverOwner, this)) _serverConnected = false;
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
            // Wire evidence (change-gated, capped): the legacy controller
            // state actually observed, so a silent lane is distinguishable
            // from a resting one without a debugger on the bench.
            public bool EvHaveState;
            public ulong EvPressed;
            public short EvA0, EvA1, EvA3;
            public int EvLines;
            // Whether the joystick was zeroed after a failed state read, so
            // the neutralize runs once instead of every tick.
            public bool Neutralized;
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
                // shut down promptly, and to SAY so: AcknowledgeQuit_Exiting
                // is the documented handshake (openvr.h), without which
                // vrserver waits out its force-quit timeout on this process.
                while (system.PollNextEvent(ref ev, evSize))
                {
                    if ((EVREventType)ev.eventType == EVREventType.VREvent_Quit)
                    {
                        _log("VRCONSUME SteamVR is quitting.");
                        try { system.AcknowledgeQuit_Exiting(); } catch { }
                        return;
                    }
                }

                system.GetDeviceToAbsoluteTrackingPose(
                    ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, _poses);

                long now = Environment.TickCount64;
                for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
                {
                    // The cheap managed pose flag first: GetTrackedDeviceClass
                    // is a P/Invoke, and probing all 64 indices with it every
                    // tick was ~5,800 native calls a second for slots that
                    // are never populated (2026-08-18 audit).
                    var slot = _consumed[i];
                    if (!_poses[i].bDeviceIsConnected)
                    {
                        if (slot != null) Detach(i);
                        _attachRetryAt[i] = 0;
                        continue;
                    }

                    var cls = system.GetTrackedDeviceClass(i);
                    bool wanted = cls == ETrackedDeviceClass.HMD || cls == ETrackedDeviceClass.Controller;
                    if (!wanted)
                    {
                        if (slot != null) Detach(i);
                        continue;
                    }

                    if (slot == null)
                    {
                        // Backoff (2026-08-18 audit): a self-filtered device
                        // (the DEFAULT configuration when a VR slot emits
                        // virtual hands) or a failed attach used to re-run
                        // the whole property-read + attach path at 90 Hz,
                        // allocating and P/Invoking forever.
                        if (now < _attachRetryAt[i]) continue;
                        slot = TryAttach(system, i, cls, consumeSelf, now);
                        if (slot == null) continue;   // filtered (self) or attach failed
                    }
                    else if (slot.IsHmd != (cls == ETrackedDeviceClass.HMD))
                    {
                        // A class flip on a live index (HMD <-> Controller)
                        // means a different device: recycle like a role
                        // change, or the old shape's push runs on the new
                        // device's data.
                        _log($"VRCONSUME device {i} class changed; recycling.");
                        Detach(i);
                        continue;
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

        /// <summary>Per-index earliest next attach attempt (Environment
        /// .TickCount64). long.MaxValue = self-filtered, cached until the
        /// index disconnects. Session-thread only.</summary>
        private readonly long[] _attachRetryAt = new long[OpenVR.k_unMaxTrackedDeviceCount];

        private Consumed TryAttach(CVRSystem system, uint idx, ETrackedDeviceClass cls, bool consumeSelf, long now)
        {
            var perr = ETrackedPropertyError.TrackedProp_Success;
            var sb = new System.Text.StringBuilder(128);
            system.GetStringTrackedDeviceProperty(idx, ETrackedDeviceProperty.Prop_ManufacturerName_String, sb, 128, ref perr);
            if (perr != ETrackedPropertyError.TrackedProp_Success
                && perr != ETrackedPropertyError.TrackedProp_ValueNotProvidedByDevice)
            {
                // A transient property failure reads as an empty string,
                // and empty is NOT "not self" (2026-08-18 audit): consuming
                // our own virtual hand on a bad read is the loop the filter
                // exists to prevent. Retry shortly instead of deciding.
                _attachRetryAt[idx] = now + 1000;
                return null;
            }
            string manufacturer = sb.ToString();
            if (IsSelfEmitted(manufacturer, consumeSelf))
            {
                // Cached until the index disconnects: the verdict cannot
                // change while the same device stays connected, and
                // re-deriving it was a 90 Hz allocation + P/Invoke.
                _attachRetryAt[idx] = long.MaxValue;
                if (_selfFilteredLogged.Add(idx))
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

            if (!AttachVirtual(slot, pid))
            {
                // Retry in 5 s rather than at 90 Hz: the failure path
                // allocated, P/Invoked, and logged every tick before the
                // backoff (2026-08-18 audit).
                _attachRetryAt[idx] = now + 5000;
                return null;
            }
            _consumed[idx] = slot;
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
                    // raw 6=vertical lean (up +), raw 7=roll (right +).
                    // Raw values sit at indices 6+ because the generic-axis
                    // surface (HasExtraGenericAxes) only exposes axes past
                    // the standardized six; at 4/5 they existed on the
                    // joystick but no PadForge surface could ever read them
                    // (2026-08-18 audit).
                    // Button 0 (South) = pose valid ("worn"), usable as an
                    // activator.
                    desc.naxes = 8;
                    desc.nbuttons = 1;
                    desc.axis_mask = 0x0F;      // LX LY RX RY -> sequential 0-3
                    desc.button_mask = 0x01;    // South -> 0
                }
                else
                {
                    // Axes: joystick->LX/LY (seq 0/1), grip->LT (seq 2),
                    // trigger->RT (seq 3), trackpad->raw 6/7 (indices past
                    // the standardized six so the generic-axis surface can
                    // expose them; see the HMD note above).
                    // Buttons: South=A, Back=System, Start=AppMenu,
                    // LeftStick=stick click, LeftShoulder=grip click,
                    // RightShoulder=trackpad click; sequential 0-5.
                    desc.naxes = 8;
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
                    SDL.SDL_SetJoystickVirtualAxis(j, 6, AxisFromScaled(dy, LeanFullScaleMeters));         // rise + (generic Axis 6)
                    SDL.SDL_SetJoystickVirtualAxis(j, 7, AxisFromScaled(roll, RollFullScaleDeg));          // tilt right + (generic Axis 7)
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

            // Zeroed before every read (2026-08-18 audit): one state struct
            // is shared across all controllers in a tick and the runtime
            // does not clear it on failure, so without this the evidence
            // line below printed the PREVIOUS controller's buttons under
            // this controller's name whenever a read failed.
            state = default;
            bool haveState = system.GetControllerState(slot.DeviceIndex, ref state, stateSize);

            if (slot.EvLines < 40)
            {
                var a0 = GetAxis(ref state, slot.JoyAxis >= 0 ? slot.JoyAxis : 0);
                var a1 = GetAxis(ref state, slot.TriggerAxis >= 0 ? slot.TriggerAxis : 1);
                short e0 = (short)(a0.x * 100), e1 = (short)(a1.x * 100), e3 = (short)(a0.y * 100);
                if (haveState != slot.EvHaveState || state.ulButtonPressed != slot.EvPressed
                    || e0 != slot.EvA0 || e1 != slot.EvA1 || e3 != slot.EvA3)
                {
                    slot.EvHaveState = haveState; slot.EvPressed = state.ulButtonPressed;
                    slot.EvA0 = e0; slot.EvA1 = e1; slot.EvA3 = e3; slot.EvLines++;
                    _log($"VRCONSUME state {slot.Name}: have={haveState} pressed=0x{state.ulButtonPressed:X} "
                        + $"joy=({e0},{e3})% trig={e1}% packet={state.unPacketNum}");
                }
            }

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
                    SDL.SDL_SetJoystickVirtualAxis(j, 6, (short)Math.Clamp(pad.x * 32767f, -32767f, 32767f));   // trackpad x (generic Axis 6)
                    SDL.SDL_SetJoystickVirtualAxis(j, 7, (short)Math.Clamp(pad.y * 32767f, -32767f, 32767f));   // trackpad y (generic Axis 7)
                    slot.Neutralized = false;
                }
                else if (!slot.Neutralized)
                {
                    // A failed state read used to LATCH the last written
                    // values on the virtual joystick (2026-08-18 audit): a
                    // controller that sleeps mid-hold kept its stick
                    // deflected and its trigger pulled indefinitely.
                    // Neutralize once; a recovered read resumes normally.
                    slot.Neutralized = true;
                    for (int b = 0; b < 6; b++) SDL.SDL_SetJoystickVirtualButton(j, b, false);
                    SDL.SDL_SetJoystickVirtualAxis(j, 0, 0);
                    SDL.SDL_SetJoystickVirtualAxis(j, 1, 0);
                    SDL.SDL_SetJoystickVirtualAxis(j, 2, short.MinValue + 1);   // triggers rest at MIN
                    SDL.SDL_SetJoystickVirtualAxis(j, 3, short.MinValue + 1);
                    SDL.SDL_SetJoystickVirtualAxis(j, 6, 0);
                    SDL.SDL_SetJoystickVirtualAxis(j, 7, 0);
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
