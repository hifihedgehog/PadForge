using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static PadForge.Engine.Common.OpenXr.OpenXrInterop;

namespace PadForge.Engine.Common.OpenXr
{
    /// <summary>
    /// One negotiated runtime, instance, headless session and reference
    /// space, owned together because they are destroyed in that order
    /// (issue #403).
    ///
    /// <para>Everything here runs on the sampling thread. Nothing is
    /// thread-safe and nothing needs to be.</para>
    /// </summary>
    internal sealed unsafe class OpenXrSession : IDisposable
    {
        private IntPtr _module;
        private ulong _instance;
        private ulong _session;
        private ulong _baseSpace;
        private ulong _viewSpace;
        private int _sessionState = XR_SESSION_STATE_IDLE;
        private bool _began;
        private IntPtr _eventBuffer;
        private OpenXrActions _actions;
        private readonly Action<string> _log;

        private PFN_xrGetInstanceProcAddr _getProc;
        private PFN_xrDestroyInstance _destroyInstance;
        private PFN_xrCreateSession _createSession;
        private PFN_xrDestroySession _destroySession;
        private PFN_xrBeginSession _beginSession;
        private PFN_xrEndSession _endSession;
        private PFN_xrCreateReferenceSpace _createSpace;
        private PFN_xrDestroySpace _destroySpace;
        private PFN_xrLocateSpace _locateSpace;
        private PFN_xrPollEvent _pollEvent;

        public string RuntimeName { get; private set; } = string.Empty;

        private OpenXrSession(Action<string> log) => _log = log ?? (_ => { });

        /// <summary>
        /// Negotiates with a runtime and brings up a headless session, or
        /// returns null with the reason. The reason is a state rather than an
        /// exception because every one of them is an ordinary machine
        /// configuration, not a fault.
        /// </summary>
        public static OpenXrSession TryCreate(string manifestPath, Action<string> log,
                                              out OpenXrSourceState failure)
        {
            failure = OpenXrSourceState.Failed;
            var session = new OpenXrSession(log);
            try
            {
                var entry = Choose(manifestPath);
                if (entry == null || !entry.LibraryExists)
                {
                    failure = OpenXrSourceState.NoRuntime;
                    session.Dispose();
                    return null;
                }

                if (!session.Negotiate(entry, out failure)) { session.Dispose(); return null; }
                if (!session.CreateInstance(out failure)) { session.Dispose(); return null; }
                if (!session.CreateSession(out failure)) { session.Dispose(); return null; }
                failure = OpenXrSourceState.Running;
                return session;
            }
            catch (Exception ex)
            {
                log?.Invoke("OpenXR: session setup failed: " + ex.Message);
                session.Dispose();
                failure = OpenXrSourceState.Failed;
                return null;
            }
        }

        private static OpenXrRuntimeEntry Choose(string manifestPath)
        {
            if (!string.IsNullOrWhiteSpace(manifestPath))
            {
                try
                {
                    if (System.IO.File.Exists(manifestPath))
                        return OpenXrRuntimeCatalog.TryParseManifest(
                            manifestPath, System.IO.File.ReadAllText(manifestPath));
                }
                catch (Exception) { }
                return null;
            }
            foreach (var entry in OpenXrRuntimeCatalog.Discover())
                if (entry.LibraryExists) return entry;
            return null;
        }

        private bool Negotiate(OpenXrRuntimeEntry entry, out OpenXrSourceState failure)
        {
            failure = OpenXrSourceState.NoRuntime;
            if (!NativeLibrary.TryLoad(entry.LibraryPath, out _module)) return false;
            if (!NativeLibrary.TryGetExport(_module, "xrNegotiateLoaderRuntimeInterface", out var negotiatePtr))
                return false;

            var negotiate = Marshal.GetDelegateForFunctionPointer<PFN_xrNegotiateLoaderRuntimeInterface>(
                negotiatePtr);

            var info = new XrNegotiateLoaderInfo
            {
                structType = XR_LOADER_INTERFACE_STRUCT_LOADER_INFO,
                structVersion = 1,
                structSize = (nuint)sizeof(XrNegotiateLoaderInfo),
                minInterfaceVersion = XR_CURRENT_LOADER_RUNTIME_VERSION,
                maxInterfaceVersion = XR_CURRENT_LOADER_RUNTIME_VERSION,
                minApiVersion = MakeVersion(1, 0, 0),
                maxApiVersion = MakeVersion(1, 1, 0xFFFFFFFF),
            };
            var request = new XrNegotiateRuntimeRequest
            {
                structType = XR_LOADER_INTERFACE_STRUCT_RUNTIME_REQUEST,
                structVersion = 1,
                structSize = (nuint)sizeof(XrNegotiateRuntimeRequest),
            };

            int result = negotiate(ref info, ref request);
            if (result != XR_SUCCESS || request.getInstanceProcAddr == IntPtr.Zero)
            {
                _log($"OpenXR: {entry.Name} declined negotiation ({result})");
                return false;
            }

            _getProc = Marshal.GetDelegateForFunctionPointer<PFN_xrGetInstanceProcAddr>(
                request.getInstanceProcAddr);
            RuntimeName = entry.Name;
            return true;
        }

        private T Resolve<T>(string name) where T : Delegate
        {
            if (_getProc(_instance, name, out IntPtr fn) != XR_SUCCESS || fn == IntPtr.Zero)
                return null;
            return Marshal.GetDelegateForFunctionPointer<T>(fn);
        }

        private bool CreateInstance(out OpenXrSourceState failure)
        {
            failure = OpenXrSourceState.NotSupported;

            var enumerate = Resolve<PFN_xrEnumerateInstanceExtensionProperties>(
                "xrEnumerateInstanceExtensionProperties");
            var create = Resolve<PFN_xrCreateInstance>("xrCreateInstance");
            if (enumerate == null || create == null) return false;

            var extensions = ReadExtensions(enumerate);
            if (!extensions.Contains(XR_MND_HEADLESS_EXTENSION_NAME))
            {
                _log($"OpenXR: {RuntimeName} does not offer {XR_MND_HEADLESS_EXTENSION_NAME}, " +
                     "so a background session would need to own the display");
                return false;
            }

            // Headless is required. The time conversion is asked for when the
            // runtime has it, because XrTime is on a clock the runtime picks
            // and only the runtime can convert into it.
            bool wantsTime = extensions.Contains(
                XR_KHR_WIN32_CONVERT_PERFORMANCE_COUNTER_TIME_EXTENSION_NAME);
            var wanted = new List<string> { XR_MND_HEADLESS_EXTENSION_NAME };
            if (wantsTime) wanted.Add(XR_KHR_WIN32_CONVERT_PERFORMANCE_COUNTER_TIME_EXTENSION_NAME);

            var extNames = new IntPtr[wanted.Count];
            IntPtr extArray = Marshal.AllocHGlobal(IntPtr.Size * wanted.Count);
            try
            {
                for (int i = 0; i < wanted.Count; i++)
                {
                    extNames[i] = Marshal.StringToHGlobalAnsi(wanted[i]);
                    Marshal.WriteIntPtr(extArray, i * IntPtr.Size, extNames[i]);
                }
                var info = new XrInstanceCreateInfo
                {
                    type = XR_TYPE_INSTANCE_CREATE_INFO,
                    enabledExtensionCount = (uint)wanted.Count,
                    enabledExtensionNames = extArray,
                };
                info.applicationInfo.apiVersion = ApiVersion1_0;
                WriteFixedUtf8(info.applicationInfo.applicationName, XR_MAX_APPLICATION_NAME_SIZE, "PadForge");
                WriteFixedUtf8(info.applicationInfo.engineName, XR_MAX_ENGINE_NAME_SIZE, "PadForge");

                int result = create(ref info, out _instance);
                if (result != XR_SUCCESS)
                {
                    _log($"OpenXR: {RuntimeName} refused an instance ({result})");
                    return false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(extArray);
                foreach (var pName in extNames)
                    if (pName != IntPtr.Zero) Marshal.FreeHGlobal(pName);
            }

            _destroyInstance = Resolve<PFN_xrDestroyInstance>("xrDestroyInstance");
            _createSession = Resolve<PFN_xrCreateSession>("xrCreateSession");
            _destroySession = Resolve<PFN_xrDestroySession>("xrDestroySession");
            _beginSession = Resolve<PFN_xrBeginSession>("xrBeginSession");
            _endSession = Resolve<PFN_xrEndSession>("xrEndSession");
            _createSpace = Resolve<PFN_xrCreateReferenceSpace>("xrCreateReferenceSpace");
            _destroySpace = Resolve<PFN_xrDestroySpace>("xrDestroySpace");
            _locateSpace = Resolve<PFN_xrLocateSpace>("xrLocateSpace");
            _pollEvent = Resolve<PFN_xrPollEvent>("xrPollEvent");
            if (wantsTime)
            {
                _convertTime = Resolve<PFN_xrConvertWin32PerformanceCounterToTimeKHR>(
                    "xrConvertWin32PerformanceCounterToTimeKHR");
                if (_convertTime == null)
                    _log($"OpenXR: {RuntimeName} advertised the time conversion and did not export it");
            }

            ReadRuntimeName();
            // Only claim Running once the entry points this needs are all
            // there. Setting it before the check reported a healthy source
            // for a runtime that never started.
            bool complete = _createSession != null && _createSpace != null
                && _locateSpace != null && _pollEvent != null;
            failure = complete ? OpenXrSourceState.Running : OpenXrSourceState.NotSupported;
            return complete;
        }

        private HashSet<string> ReadExtensions(PFN_xrEnumerateInstanceExtensionProperties enumerate)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (enumerate(null, 0, out uint count, IntPtr.Zero) != XR_SUCCESS || count == 0)
                return names;
            int stride = sizeof(XrExtensionProperties);
            IntPtr buffer = Marshal.AllocHGlobal(stride * (int)count);
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    var p = (XrExtensionProperties*)((byte*)buffer + i * stride);
                    p->type = XR_TYPE_EXTENSION_PROPERTIES;
                    p->next = IntPtr.Zero;
                }
                if (enumerate(null, count, out uint written, buffer) != XR_SUCCESS) return names;
                for (uint i = 0; i < written; i++)
                {
                    var p = (XrExtensionProperties*)((byte*)buffer + i * stride);
                    names.Add(ReadFixedUtf8(p->extensionName, XR_MAX_EXTENSION_NAME_SIZE));
                }
                return names;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private void ReadRuntimeName()
        {
            var get = Resolve<PFN_xrGetInstanceProperties>("xrGetInstanceProperties");
            if (get == null) return;
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(XrInstanceProperties));
            try
            {
                var p = (XrInstanceProperties*)buffer;
                p->type = XR_TYPE_INSTANCE_PROPERTIES;
                p->next = IntPtr.Zero;
                if (get(_instance, buffer) != XR_SUCCESS) return;
                string name = ReadFixedUtf8(p->runtimeName, XR_MAX_RUNTIME_NAME_SIZE);
                if (!string.IsNullOrWhiteSpace(name)) RuntimeName = name;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private bool CreateSession(out OpenXrSourceState failure)
        {
            failure = OpenXrSourceState.NoHeadset;
            var getSystem = Resolve<PFN_xrGetSystem>("xrGetSystem");
            if (getSystem == null) return false;

            var systemInfo = new XrSystemGetInfo
            {
                type = XR_TYPE_SYSTEM_GET_INFO,
                formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY,
            };
            if (getSystem(_instance, ref systemInfo, out ulong systemId) != XR_SUCCESS)
                return false;

            failure = OpenXrSourceState.NotSupported;
            // No graphics binding in next, which is what makes this headless.
            var sessionInfo = new XrSessionCreateInfo
            {
                type = XR_TYPE_SESSION_CREATE_INFO,
                systemId = systemId,
            };
            int result = _createSession(_instance, ref sessionInfo, out _session);
            if (result != XR_SUCCESS)
            {
                _log($"OpenXR: {RuntimeName} refused a headless session ({result})");
                return false;
            }

            _baseSpace = CreateReferenceSpace(XR_REFERENCE_SPACE_TYPE_STAGE);
            if (_baseSpace == 0) _baseSpace = CreateReferenceSpace(XR_REFERENCE_SPACE_TYPE_LOCAL);
            _viewSpace = CreateReferenceSpace(XR_REFERENCE_SPACE_TYPE_VIEW);
            if (_baseSpace == 0 || _viewSpace == 0)
            {
                _log("OpenXR: no usable reference space");
                return false;
            }

            // Controllers are optional. A headset with none still works, and
            // the action set has to be attached before the session begins, so
            // this is the only place it can happen.
            _actions = new OpenXrActions(_log);
            if (!_actions.TryCreate(_instance, _session, name => ResolveRaw(name)))
            {
                _actions.Dispose();
                _actions = null;
            }

            failure = OpenXrSourceState.Running;
            return true;
        }

        /// <summary>Resolves a function by name, typed by the caller. Used by
        /// the action set, which needs a dozen of them.</summary>
        private Delegate ResolveRaw(string name)
        {
            if (_getProc(_instance, name, out IntPtr fn) != XR_SUCCESS || fn == IntPtr.Zero)
                return null;
            return name switch
            {
                "xrCreateActionSet" => Marshal.GetDelegateForFunctionPointer<PFN_xrCreateActionSet>(fn),
                "xrDestroyActionSet" => Marshal.GetDelegateForFunctionPointer<PFN_xrDestroyActionSet>(fn),
                "xrCreateAction" => Marshal.GetDelegateForFunctionPointer<PFN_xrCreateAction>(fn),
                "xrStringToPath" => Marshal.GetDelegateForFunctionPointer<PFN_xrStringToPath>(fn),
                "xrSuggestInteractionProfileBindings" =>
                    Marshal.GetDelegateForFunctionPointer<PFN_xrSuggestInteractionProfileBindings>(fn),
                "xrAttachSessionActionSets" =>
                    Marshal.GetDelegateForFunctionPointer<PFN_xrAttachSessionActionSets>(fn),
                "xrSyncActions" => Marshal.GetDelegateForFunctionPointer<PFN_xrSyncActions>(fn),
                "xrGetActionStatePose" => Marshal.GetDelegateForFunctionPointer<PFN_xrGetActionStatePose>(fn),
                "xrGetActionStateFloat" => Marshal.GetDelegateForFunctionPointer<PFN_xrGetActionStateFloat>(fn),
                "xrGetActionStateBoolean" => Marshal.GetDelegateForFunctionPointer<PFN_xrGetActionStateBoolean>(fn),
                "xrGetActionStateVector2f" => Marshal.GetDelegateForFunctionPointer<PFN_xrGetActionStateVector2f>(fn),
                "xrCreateActionSpace" => Marshal.GetDelegateForFunctionPointer<PFN_xrCreateActionSpace>(fn),
                "xrLocateSpace" => Marshal.GetDelegateForFunctionPointer<PFN_xrLocateSpace>(fn),
                "xrDestroySpace" => Marshal.GetDelegateForFunctionPointer<PFN_xrDestroySpace>(fn),
                _ => null,
            };
        }

        /// <summary>True when the runtime accepted a controller profile.</summary>
        public bool HasControllers => _actions != null && _actions.Ready;

        /// <summary>
        /// Reads both controllers. The baselines are per hand, because a
        /// controller put down and picked up again is not where it was.
        /// </summary>
        public void ReadHands(ref OpenXrHeadPose.Baseline left, ref OpenXrHeadPose.Baseline right,
                              out OpenXrHandState leftState, out OpenXrHandState rightState)
        {
            leftState = OpenXrHandState.Empty;
            rightState = OpenXrHandState.Empty;
            if (_actions == null || !_actions.Ready || !_began) return;

            bool live = _actions.Sync();
            long time = NowXrTime();
            leftState = _actions.Read(OpenXrHand.Left, _baseSpace, time, live, ref left);
            rightState = _actions.Read(OpenXrHand.Right, _baseSpace, time, live, ref right);
        }

        private ulong CreateReferenceSpace(int type)
        {
            var info = new XrReferenceSpaceCreateInfo
            {
                type = XR_TYPE_REFERENCE_SPACE_CREATE_INFO,
                referenceSpaceType = type,
            };
            info.poseInReferenceSpace.orientation.w = 1f;   // identity
            return _createSpace(_session, ref info, out ulong space) == XR_SUCCESS ? space : 0;
        }

        /// <summary>Drains the event queue. Returns false when the session is
        /// gone for good.</summary>
        public bool PumpEvents(out bool lost, out bool referenceSpaceChanged)
        {
            lost = false;
            referenceSpaceChanged = false;
            // Allocated once and reused. This runs about 125 times a second
            // for as long as the feature is on, and the event union is 4 KB,
            // so allocating it per poll was half a megabyte a second of
            // pointless alloc and free.
            if (_eventBuffer == IntPtr.Zero)
                _eventBuffer = Marshal.AllocHGlobal(sizeof(XrEventDataBuffer));
            IntPtr buffer = _eventBuffer;
            {
                // Bounded. A runtime that returns success without writing the
                // buffer, or a sustained event storm, would otherwise spin
                // this thread forever with nothing in the log to say so.
                for (int drained = 0; ; drained++)
                {
                    if (drained >= MaxEventsPerPoll)
                    {
                        _log($"OpenXR: drained {MaxEventsPerPoll} events in one poll, deferring the rest");
                        return true;
                    }
                    var header = (XrEventDataBuffer*)buffer;
                    header->type = XR_TYPE_EVENT_DATA_BUFFER;
                    header->next = IntPtr.Zero;
                    int result = _pollEvent(_instance, buffer);
                    if (result == XR_EVENT_UNAVAILABLE) return true;
                    if (result != XR_SUCCESS) { lost = true; return false; }

                    switch (header->type)
                    {
                        case XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED:
                            var changed = (XrEventDataSessionStateChanged*)buffer;
                            _sessionState = changed->state;
                            if (_sessionState == XR_SESSION_STATE_READY && !_began) Begin();
                            else if (_sessionState == XR_SESSION_STATE_STOPPING) End();
                            else if (_sessionState == XR_SESSION_STATE_EXITING
                                     || _sessionState == XR_SESSION_STATE_LOSS_PENDING)
                            {
                                lost = true;
                                return false;
                            }
                            break;

                        case XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING:
                            lost = true;
                            return false;

                        case XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING:
                            referenceSpaceChanged = true;
                            break;
                    }
                }
            }
        }

        private void Begin()
        {
            if (_beginSession == null) return;
            var info = new XrSessionBeginInfo
            {
                type = XR_TYPE_SESSION_BEGIN_INFO,
                primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
            };
            int result = _beginSession(_session, ref info);
            if (result == XR_SUCCESS)
            {
                _began = true;
                _log($"OpenXR: session running on {RuntimeName}");
                return;
            }
            // The runtime emits READY once. A failure here is permanent for
            // this session, and every locate afterwards returns nothing, so
            // the silence would read as a healthy source producing no poses.
            _log($"OpenXR: {RuntimeName} refused to begin the session (result {result})");
        }

        /// <summary>
        /// Ends a running session.
        ///
        /// <para>Only from STOPPING. The specification, and every runtime
        /// that checks, rejects xrEndSession from any other state, so calling
        /// it on the way out of a FOCUSED session failed silently and left
        /// the runtime believing the session was still running when the
        /// destroy arrived.</para>
        /// </summary>
        private void End()
        {
            if (!_began || _endSession == null) return;
            if (_sessionState != XR_SESSION_STATE_STOPPING)
            {
                // Not an error. The dispose path takes this on an ordinary
                // shutdown, where destroying the session is what ends it.
                _began = false;
                return;
            }
            int result = _endSession(_session);
            if (result != XR_SUCCESS)
                _log($"OpenXR: {RuntimeName} refused to end the session ({result})");
            _began = false;
        }

        /// <summary>
        /// The headset pose in the base space, or false when the runtime says
        /// it does not have one.
        ///
        /// <para>Both validity bits are required. A runtime that has lost
        /// tracking still returns success and a pose, and using it would pin
        /// a mapped stick at wherever the user last was.</para>
        /// </summary>
        public bool TryLocateHead(out XrVector3f position, out XrQuaternionf orientation)
        {
            position = default;
            orientation = default;
            if (!_began) return false;

            var location = new XrSpaceLocation { type = XR_TYPE_SPACE_LOCATION };
            long time = NowXrTime();
            if (time == 0) return false;
            if (_locateSpace(_viewSpace, _baseSpace, time, ref location) != XR_SUCCESS) return false;

            const ulong needed = XR_SPACE_LOCATION_ORIENTATION_VALID_BIT
                               | XR_SPACE_LOCATION_POSITION_VALID_BIT
                               | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT
                               | XR_SPACE_LOCATION_POSITION_TRACKED_BIT;
            if ((location.locationFlags & needed) != needed) return false;

            position = location.pose.position;
            orientation = location.pose.orientation;
            return true;
        }

        /// <summary>
        /// Now, as an XrTime.
        ///
        /// <para>A frame-free client has no predicted display time to use, so
        /// it asks for the present. XrTime is nanoseconds on a runtime-chosen
        /// clock, and on Windows that clock is the performance counter, which
        /// is what XR_KHR_win32_convert_performance_counter_time converts.
        /// Rather than take a dependency on that extension for one value, the
        /// counter is converted here with the same arithmetic.</para>
        /// </summary>
        private long NowXrTime()
        {
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();

            // The runtime owns the mapping. Virtual Desktop's, for one,
            // calibrates an offset between the performance counter and its
            // own clock at startup and adds it here, so arithmetic that skips
            // the offset asks for poses at a time the runtime never meant.
            // The error is whatever that offset is, which grows with how long
            // the machine has been up relative to the runtime's service.
            if (_convertTime != null)
            {
                if (_convertTime(_instance, ref ticks, out long converted) == XR_SUCCESS)
                    return converted;
                // One failure is enough. A runtime that advertised the
                // extension and then refuses it is not going to start working.
                _convertTime = null;
                _log("OpenXR: the runtime's time conversion failed, falling back to the raw counter");
            }

            long freq = System.Diagnostics.Stopwatch.Frequency;
            if (freq <= 0) return 0;
            // Split to keep the nanosecond scaling inside 64 bits. Only
            // correct for a runtime whose clock IS the performance counter,
            // which is why the conversion above is preferred.
            long whole = ticks / freq;
            long rest = ticks % freq;
            return whole * 1_000_000_000L + rest * 1_000_000_000L / freq;
        }

        private PFN_xrConvertWin32PerformanceCounterToTimeKHR _convertTime;

        /// <summary>The most events one poll will drain before yielding.
        /// High enough that an ordinary burst is handled in one pass.</summary>
        private const int MaxEventsPerPoll = 64;

        public void Dispose()
        {
            try { _actions?.Dispose(); } catch (Exception) { }
            _actions = null;
            try { End(); } catch (Exception) { }
            try { if (_viewSpace != 0) _destroySpace?.Invoke(_viewSpace); } catch (Exception) { }
            try { if (_baseSpace != 0) _destroySpace?.Invoke(_baseSpace); } catch (Exception) { }
            try { if (_session != 0) _destroySession?.Invoke(_session); } catch (Exception) { }
            try { if (_instance != 0) _destroyInstance?.Invoke(_instance); } catch (Exception) { }
            _viewSpace = _baseSpace = _session = _instance = 0;
            if (_eventBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_eventBuffer);
                _eventBuffer = IntPtr.Zero;
            }
            // The module is deliberately left loaded. A runtime that has had
            // an instance created and destroyed does not always survive being
            // unloaded and reloaded in the same process, and the handle costs
            // nothing.
            _module = IntPtr.Zero;
        }
    }
}
