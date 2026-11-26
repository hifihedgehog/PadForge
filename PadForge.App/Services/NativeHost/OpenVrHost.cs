using System;
using PadForge.Engine.Common.NativeHost;
using System.Runtime.InteropServices;
using PadForge.Common.Input;
using Valve.VR;
using PadForge.Engine.Common;

namespace PadForge.Services.NativeHost
{
    /// <summary>The OpenVR client's calls over the host's channel (#600):
    /// a start, a frame per tick of the consumer, a device's properties when
    /// it attaches, the quit handshake and the shutdown.</summary>
    internal static class OpenVrOps
    {
        internal const int Init = 1, Frame = 2, DeviceInfo = 3, AcknowledgeQuit = 4, Shutdown = 5;

        internal const uint Devices = OpenVR.k_unMaxTrackedDeviceCount;

        /// <summary>The bounds PadForge holds a frame to: a pose's matrix
        /// and velocities in meters, radians and their rates, a stick or
        /// trigger axis in [-1, 1] with room for a runtime's overshoot.</summary>
        internal const float MaxMatrix = 1.0e6f, MaxVelocity = 1.0e4f, MaxAxis = 4f;
        internal const int MaxEnum = 1 << 16, MaxText = 512, MaxManufacturer = 128;
    }

    /// <summary>
    /// The host's side of OpenVR: SteamVR's openvr_api.dll, found and loaded
    /// in the host by the consumer's own resolver
    /// (<see cref="OpenVrConsumerService.ResolveOpenVrApi"/>), started as a
    /// background application, and read once per frame of the consumer: the
    /// events up to a quit, every connected device's pose, class and role,
    /// and each controller's state.
    /// </summary>
    internal sealed class OpenVrHostService : IHostService
    {
        internal const string Name = "openvr";

        /// <summary>Test seams, set together: OpenVR's start, which gives the
        /// start's error, and its end, in place of the binding's. Null in
        /// production.</summary>
        internal static Func<EVRInitError> InitSeam;
        internal static Action ShutdownSeam;

        // OpenVR's session is the process's: VR_InitInternal2 and
        // VR_ShutdownInternal (openvr_api_public.cpp) start and end one
        // g_pHmdSystem whoever calls them, so every channel of the host shares
        // it. The channel whose start opened the session that runs owns it,
        // and only that channel ends it. A start ends the session that runs
        // first, as a channel's second start always did. Every use of the
        // session holds this lock, so no channel reads one another ended.
        private static readonly object s_session = new();
        private static OpenVrHostService s_owner;

        private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVrOps.Devices];
        private CVRSystem _system;

        public void Serve(HostConnection channel)
        {
            if (InitSeam == null) OpenVrConsumerService.EnsureResolver();
            try
            {
                while (channel.Receive(out var r))
                {
                    int op = r.Int();
                    bool sent;
                    switch (op)
                    {
                        case OpenVrOps.Init:
                        {
                            r.End();
                            var error = EVRInitError.None;
                            string failure;
                            lock (s_session) failure = Start(ref error);
                            string why = failure;
                            int code = (int)error;
                            sent = channel.Send(w => w.Int(code).Text(why));
                            break;
                        }
                        case OpenVrOps.Frame:
                            r.End();
                            sent = channel.Send(w =>
                            {
                                lock (s_session)
                                {
                                    if (_system != null) WriteFrame(_system, w);
                                    else w.Bool(true);
                                }
                            });
                            break;
                        case OpenVrOps.DeviceInfo:
                        {
                            uint index = (uint)r.Int(0, (int)OpenVrOps.Devices - 1);
                            r.End();
                            var error = ETrackedPropertyError.TrackedProp_Success;
                            string manufacturer = string.Empty;
                            var types = new int[5];
                            lock (s_session)
                            {
                                var system = _system;
                                if (system != null)
                                {
                                    var text = new System.Text.StringBuilder(OpenVrOps.MaxManufacturer);
                                    system.GetStringTrackedDeviceProperty(index, ETrackedDeviceProperty.Prop_ManufacturerName_String, text, OpenVrOps.MaxManufacturer, ref error);
                                    manufacturer = text.ToString();
                                    for (int a = 0; a < 5; a++)
                                    {
                                        var axisError = ETrackedPropertyError.TrackedProp_Success;
                                        types[a] = system.GetInt32TrackedDeviceProperty(index,
                                            (ETrackedDeviceProperty)((int)ETrackedDeviceProperty.Prop_Axis0Type_Int32 + a), ref axisError);
                                    }
                                }
                                else error = ETrackedPropertyError.TrackedProp_InvalidDevice;
                            }
                            int code = (int)error;
                            sent = channel.Send(w =>
                            {
                                w.Int(code).Text(manufacturer);
                                foreach (int t in types) w.Int(t);
                            });
                            break;
                        }
                        case OpenVrOps.AcknowledgeQuit:
                            r.End();
                            lock (s_session)
                            {
                                try { _system?.AcknowledgeQuit_Exiting(); } catch (Exception) { }
                            }
                            sent = channel.Send(w => { });
                            break;
                        case OpenVrOps.Shutdown:
                            r.End();
                            LetGo();
                            sent = channel.Send(w => { });
                            break;
                        default:
                            throw new HostProtocolException("unknown OpenVR call");
                    }
                    if (!sent) return;
                }
            }
            finally
            {
                // A PadForge that went away leaves no session of this
                // channel's behind.
                LetGo();
            }
        }

        /// <summary>Starts the process's session for this channel: null, or
        /// why the binding failed. Caller holds the session's lock.</summary>
        private string Start(ref EVRInitError error)
        {
            if (s_owner != null) s_owner.EndSession();
            string failure = null;
            CVRSystem system = null;
            bool opened;
            if (InitSeam != null)
            {
                error = InitSeam();
                opened = error == EVRInitError.None;
            }
            else
            {
                try { system = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Background); }
                catch (DllNotFoundException) { failure = "dll"; }
                catch (Exception ex)
                {
                    // The binding can throw after the native init went
                    // through, so its session is shut here.
                    try { OpenVR.Shutdown(); } catch (Exception) { }
                    failure = "threw: " + ex.Message;
                    system = null;
                }
                if (error != EVRInitError.None) system = null;
                opened = system != null;
            }
            if (opened)
            {
                _system = system;
                s_owner = this;
            }
            return failure;
        }

        /// <summary>Ends the process's session, which this channel owns.
        /// Caller holds the session's lock.</summary>
        private void EndSession()
        {
            _system = null;
            s_owner = null;
            var seam = ShutdownSeam;
            if (seam != null) seam();
            else
            {
                try { OpenVR.Shutdown(); } catch (Exception) { }
            }
        }

        /// <summary>This channel is done with the session: it ends when this
        /// channel owns it, and stays for the channel that does
        /// otherwise.</summary>
        private void LetGo()
        {
            lock (s_session)
            {
                if (ReferenceEquals(s_owner, this)) EndSession();
                else _system = null;
            }
        }

        /// <summary>One frame: whether a quit came, then for each device
        /// whether it is connected and, when it is, its pose, class and role,
        /// and a controller's state.</summary>
        private void WriteFrame(CVRSystem system, MessageWriter w)
        {
            bool quit = false;
            var ev = new VREvent_t();
            uint eventSize = (uint)Marshal.SizeOf<VREvent_t>();
            while (system.PollNextEvent(ref ev, eventSize))
                if ((EVREventType)ev.eventType == EVREventType.VREvent_Quit) { quit = true; break; }
            w.Bool(quit);
            if (quit) return;
            system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, _poses);
            var state = new VRControllerState_t();
            uint stateSize = (uint)Marshal.SizeOf<VRControllerState_t>();
            for (uint i = 0; i < OpenVrOps.Devices; i++)
            {
                ref var pose = ref _poses[i];
                w.Bool(pose.bDeviceIsConnected);
                if (!pose.bDeviceIsConnected) continue;
                var m = pose.mDeviceToAbsoluteTracking;
                w.Float(m.m0).Float(m.m1).Float(m.m2).Float(m.m3).Float(m.m4).Float(m.m5)
                 .Float(m.m6).Float(m.m7).Float(m.m8).Float(m.m9).Float(m.m10).Float(m.m11);
                w.Float(pose.vVelocity.v0).Float(pose.vVelocity.v1).Float(pose.vVelocity.v2);
                w.Float(pose.vAngularVelocity.v0).Float(pose.vAngularVelocity.v1).Float(pose.vAngularVelocity.v2);
                w.Int((int)pose.eTrackingResult).Bool(pose.bPoseIsValid);
                var cls = system.GetTrackedDeviceClass(i);
                w.Int((int)cls);
                w.Int(cls == ETrackedDeviceClass.Controller ? (int)system.GetControllerRoleForTrackedDeviceIndex(i) : 0);
                if (cls != ETrackedDeviceClass.Controller) continue;
                state = default;
                bool have = system.GetControllerState(i, ref state, stateSize);
                w.Bool(have).UInt(state.unPacketNum).ULong(state.ulButtonPressed).ULong(state.ulButtonTouched);
                foreach (var axis in new[] { state.rAxis0, state.rAxis1, state.rAxis2, state.rAxis3, state.rAxis4 })
                    w.Float(axis.x).Float(axis.y);
            }
        }
    }

    /// <summary>
    /// PadForge's side of OpenVR: the consumer's client
    /// (<see cref="OpenVrConsumerService.IVrClient"/>) over the host. A
    /// tick's <see cref="PollQuit"/> reads the whole frame in one call, and
    /// the tick's other reads answer from it. Every number in it is bounded
    /// (<see cref="OpenVrOps"/>), and a frame out of bounds ends the client,
    /// which the consumer takes as a lost session and starts again.
    /// </summary>
    internal sealed class RemoteVrClient : OpenVrConsumerService.IVrClient
    {
        private readonly HostConnection _channel;
        private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVrOps.Devices];
        private readonly ETrackedDeviceClass[] _classes = new ETrackedDeviceClass[OpenVrOps.Devices];
        private readonly ETrackedControllerRole[] _roles = new ETrackedControllerRole[OpenVrOps.Devices];
        private readonly bool[] _haveState = new bool[OpenVrOps.Devices];
        private readonly VRControllerState_t[] _states = new VRControllerState_t[OpenVrOps.Devices];

        private RemoteVrClient(HostConnection channel) => _channel = channel;

        /// <summary>The consumer's opener: a channel to the host, and OpenVR
        /// started there as a background application.</summary>
        internal static OpenVrConsumerService.VrClientOpener Opener(NativeHost host)
            => (out EVRInitError error, out string failure) =>
            {
                error = EVRInitError.None;
                failure = null;
                var channel = host.Open(OpenVrHostService.Name, out string reason);
                if (channel == null)
                {
                    failure = "host: " + reason;
                    return null;
                }
                int code = 0;
                string why = null;
                if (!channel.Call(w => w.Int(OpenVrOps.Init), r =>
                    {
                        code = r.Int(-1, OpenVrOps.MaxEnum);
                        why = r.Text(OpenVrOps.MaxText);
                    }))
                {
                    failure = "host: the library host ended";
                    return null;
                }
                error = (EVRInitError)code;
                failure = ShownText.Clean(why);
                if (failure != null || error != EVRInitError.None)
                {
                    channel.Dispose();
                    return null;
                }
                return new RemoteVrClient(channel);
            };

        public bool PollQuit()
        {
            bool quit = false;
            if (!_channel.Call(w => w.Int(OpenVrOps.Frame), ReadFrame(q => quit = q)))
                throw new InvalidOperationException("the library host ended");
            return quit;
        }

        private Action<MessageReader> ReadFrame(Action<bool> quit) => r =>
        {
            bool q = r.Bool();
            quit(q);
            if (q) return;
            for (int i = 0; i < OpenVrOps.Devices; i++)
            {
                ref var pose = ref _poses[i];
                pose = default;
                _classes[i] = ETrackedDeviceClass.Invalid;
                _roles[i] = ETrackedControllerRole.Invalid;
                _haveState[i] = false;
                _states[i] = default;
                pose.bDeviceIsConnected = r.Bool();
                if (!pose.bDeviceIsConnected) continue;
                float M() => r.Float(-OpenVrOps.MaxMatrix, OpenVrOps.MaxMatrix);
                float V() => r.Float(-OpenVrOps.MaxVelocity, OpenVrOps.MaxVelocity);
                pose.mDeviceToAbsoluteTracking = new HmdMatrix34_t
                {
                    m0 = M(), m1 = M(), m2 = M(), m3 = M(), m4 = M(), m5 = M(),
                    m6 = M(), m7 = M(), m8 = M(), m9 = M(), m10 = M(), m11 = M(),
                };
                pose.vVelocity = new HmdVector3_t { v0 = V(), v1 = V(), v2 = V() };
                pose.vAngularVelocity = new HmdVector3_t { v0 = V(), v1 = V(), v2 = V() };
                pose.eTrackingResult = (ETrackingResult)r.Int(0, OpenVrOps.MaxEnum);
                pose.bPoseIsValid = r.Bool();
                var cls = (ETrackedDeviceClass)r.Int(0, OpenVrOps.MaxEnum);
                _classes[i] = cls;
                _roles[i] = (ETrackedControllerRole)r.Int(0, OpenVrOps.MaxEnum);
                if (cls != ETrackedDeviceClass.Controller) continue;
                _haveState[i] = r.Bool();
                float A() => r.Float(-OpenVrOps.MaxAxis, OpenVrOps.MaxAxis);
                _states[i] = new VRControllerState_t
                {
                    unPacketNum = r.UInt(),
                    ulButtonPressed = r.ULong(),
                    ulButtonTouched = r.ULong(),
                    rAxis0 = new VRControllerAxis_t { x = A(), y = A() },
                    rAxis1 = new VRControllerAxis_t { x = A(), y = A() },
                    rAxis2 = new VRControllerAxis_t { x = A(), y = A() },
                    rAxis3 = new VRControllerAxis_t { x = A(), y = A() },
                    rAxis4 = new VRControllerAxis_t { x = A(), y = A() },
                };
            }
        };

        public void AcknowledgeQuit() => _channel.Call(w => w.Int(OpenVrOps.AcknowledgeQuit), null);

        public void ReadPoses(TrackedDevicePose_t[] poses) => Array.Copy(_poses, poses, Math.Min(poses.Length, _poses.Length));

        public ETrackedDeviceClass DeviceClass(uint index) => index < OpenVrOps.Devices ? _classes[index] : ETrackedDeviceClass.Invalid;

        public ETrackedControllerRole Role(uint index) => index < OpenVrOps.Devices ? _roles[index] : ETrackedControllerRole.Invalid;

        public bool ControllerState(uint index, ref VRControllerState_t state)
        {
            if (index >= OpenVrOps.Devices || !_haveState[index]) return false;
            state = _states[index];
            return true;
        }

        public string Manufacturer(uint index, out ETrackedPropertyError error, int[] axisTypes)
        {
            var e = ETrackedPropertyError.TrackedProp_InvalidDevice;
            string manufacturer = string.Empty;
            if (index >= OpenVrOps.Devices || !_channel.Call(w => w.Int(OpenVrOps.DeviceInfo).Int((int)index), r =>
                {
                    e = (ETrackedPropertyError)r.Int(0, OpenVrOps.MaxEnum);
                    manufacturer = ShownText.Clean(r.Text(OpenVrOps.MaxManufacturer)) ?? string.Empty;
                    for (int a = 0; a < 5; a++)
                    {
                        int t = r.Int(-1, OpenVrOps.MaxEnum);
                        if (axisTypes != null && a < axisTypes.Length) axisTypes[a] = t;
                    }
                }))
            {
                error = ETrackedPropertyError.TrackedProp_InvalidDevice;
                return string.Empty;
            }
            error = e;
            return manufacturer;
        }

        public void Dispose()
        {
            _channel.Call(w => w.Int(OpenVrOps.Shutdown), null);
            _channel.Dispose();
        }
    }
}
