using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static PadForge.Engine.Common.OpenXr.OpenXrInterop;

namespace PadForge.Engine.Common.OpenXr
{
    /// <summary>Why the session, or the instance under it, went away, as the
    /// event pump reads it.</summary>
    internal enum OpenXrSessionEnd
    {
        /// <summary>Nothing ended.</summary>
        None,
        /// <summary>XR_SESSION_STATE_EXITING: the runtime wants the XR
        /// experience over and not restarted.</summary>
        Exiting,
        /// <summary>XR_SESSION_STATE_LOSS_PENDING: the session is lost and
        /// the instance stays.</summary>
        SessionLost,
        /// <summary>XrEventDataInstanceLossPending: the runtime retires the
        /// instance at a set time, as for an update. SteamVR also sends it
        /// to an instance that has no session when SteamVR quits.</summary>
        InstanceLossPending,
        /// <summary>xrPollEvent answered XR_ERROR_INSTANCE_LOST.</summary>
        InstanceLost,
        /// <summary>xrPollEvent failed another way.</summary>
        PollFailed,
        /// <summary>The runtime refused xrBeginSession, so the session it
        /// created never runs and has no pose to give.</summary>
        BeginRefused,
    }

    /// <summary>What an attempt to create a session came to.</summary>
    internal enum OpenXrSessionCreate
    {
        Created,
        /// <summary>xrGetSystem answered XR_ERROR_FORM_FACTOR_UNAVAILABLE: the
        /// headset is not there now and can be later.</summary>
        HeadsetUnavailable,
        /// <summary>No session, for a reason the failure state names.</summary>
        Failed,
        /// <summary>xrCreateReferenceSpace answered XR_ERROR_SESSION_LOST:
        /// the runtime lost the session as it was created and gave no grace
        /// period. What was built of it is destroyed and the instance
        /// stays, so the source asks again as after any lost session.</summary>
        SessionLost,
    }

    /// <summary>The runtime as <see cref="OpenXrHeadPoseSource"/> uses it: one
    /// instance, and the session on it when there is one.
    /// <see cref="OpenXrSession"/> is the real one, and the tests stand a
    /// scripted one in its place.</summary>
    internal interface IOpenXrLink : IDisposable
    {
        string RuntimeName { get; }

        /// <summary>The manifest this runtime was negotiated from. A new
        /// instance after a retired one is asked of this manifest and of no
        /// other.</summary>
        string ManifestPath { get; }

        bool HasSession { get; }
        bool HasControllers { get; }

        /// <summary>True when the runtime's library is an OpenVR client
        /// library, as SteamVR's is. The source then keeps no instance
        /// without a session and creates none on its own
        /// (<see cref="OpenXrSession.IsOpenVrClientLibrary"/>).</summary>
        bool SharesOpenVrClient { get; }

        /// <summary>Milliseconds to wait before asking for a new instance,
        /// read off the last instance-loss event.</summary>
        long InstanceLossWaitMs { get; }

        /// <summary>Asks the runtime for its headset and creates a session on
        /// it. With <see cref="OpenXrSessionCreate.Failed"/> the state says
        /// why. <paramref name="read"/> is what the runtime answered when
        /// no session came of the ask, the call and its result, for the one
        /// line the source writes about it. The link writes none itself.</summary>
        OpenXrSessionCreate TryCreateSession(out OpenXrSourceState failure, out string read);

        /// <summary>Drains the event queue, and says what ended, if
        /// anything. <paramref name="read"/> is what was read when
        /// something ended, the event or the failed call with its result,
        /// and null otherwise.</summary>
        OpenXrSessionEnd PumpEvents(out bool referenceSpaceChanged, out string read);

        /// <summary>Destroys the session, its spaces and its actions, and
        /// keeps the instance.</summary>
        void DestroySession();

        void ReadHands(ref OpenXrHeadPose.Baseline left, ref OpenXrHeadPose.Baseline right,
                       out OpenXrHandState leftState, out OpenXrHandState rightState);

        bool TryLocateHead(out XrVector3f position, out XrQuaternionf orientation);
    }

    /// <summary>
    /// One negotiated runtime, instance, headless session and reference
    /// space, owned together because they are destroyed in that order
    /// (issue #403). The session can go and come back on the same instance
    /// (<see cref="DestroySession"/>, <see cref="TryCreateSession"/>).
    ///
    /// <para>Everything here runs on the sampling thread. Nothing is
    /// thread-safe and nothing needs to be.</para>
    /// </summary>
    internal sealed unsafe class OpenXrSession : IOpenXrLink
    {
        private ulong _instance;
        private ulong _session;
        private ulong _baseSpace;
        private ulong _viewSpace;
        private int _sessionState = XR_SESSION_STATE_IDLE;
        private bool _began;
        // Polls in a row that stopped at the bound on events per poll, and
        // whether the log already holds the line about a state event naming
        // another session, and the one about a reference space event that
        // does.
        private int _fullPolls;
        private bool _strangerStateSaid;
        private bool _strangerSpaceSaid;
        private IntPtr _eventBuffer;
        private OpenXrActions _actions;
        private readonly Action<string> _log;

        private PFN_xrGetInstanceProcAddr _getProc;
        private PFN_xrDestroyInstance _destroyInstance;
        private PFN_xrGetSystem _getSystem;
        private PFN_xrCreateSession _createSession;
        private PFN_xrDestroySession _destroySession;
        private PFN_xrBeginSession _beginSession;
        private PFN_xrEndSession _endSession;
        private PFN_xrCreateReferenceSpace _createSpace;
        private PFN_xrDestroySpace _destroySpace;
        private PFN_xrLocateSpace _locateSpace;
        private PFN_xrPollEvent _pollEvent;

        public string RuntimeName { get; private set; } = string.Empty;

        public string ManifestPath { get; private set; } = string.Empty;

        public bool HasSession => _session != 0;

        public bool SharesOpenVrClient { get; private set; }

        /// <summary>
        /// Whether a runtime library is an OpenVR client library: SteamVR's
        /// OpenXR runtime is vrclient_x64.dll (its steamxr_win64.json names
        /// bin\vrclient_x64.dll), the library openvr_api.dll loads for an
        /// OpenVR application too.
        ///
        /// <para>That library keeps one client per process, whoever asks. A
        /// second VR_Init or xrCreateInstance rides on the first, and the
        /// first VR_Shutdown or xrDestroyInstance takes the client down for
        /// both. It logs both as "Behavior is undefined" (read from the
        /// vrclient_x64.dll of SteamVR 2.17.10). PadForge's OpenVR input
        /// (OpenVrConsumerService) uses that client in this process, coming
        /// and going with SteamVR, so an OpenXR instance kept with no session,
        /// or created again by a retry, would share its client for as long as
        /// it stood.</para>
        ///
        /// <para>Two more facts from the same library weigh the same way. It
        /// tells an instance that has no session that SteamVR is quitting
        /// through XrEventDataInstanceLossPending, where a new instance would
        /// start SteamVR again. And it never moves a session to
        /// XR_SESSION_STATE_LOSS_PENDING, so there is no lost session to bring
        /// back.</para>
        /// </summary>
        internal static bool IsOpenVrClientLibrary(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath)) return false;
            string name;
            try { name = System.IO.Path.GetFileNameWithoutExtension(libraryPath); }
            catch (ArgumentException) { return false; }
            return name != null && name.StartsWith("vrclient", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Milliseconds from the last instance-loss event to a little
        /// past its loss time, on the runtime's clock.</summary>
        public long InstanceLossWaitMs { get; private set; }

        /// <summary>The longest wait an instance-loss event can ask for. A loss
        /// time further out than this is not believed, and the creation retry
        /// after it covers a runtime that is still away.</summary>
        private const long MaxInstanceLossWaitMs = 300_000;

        private OpenXrSession(Action<string> log) => _log = log ?? (_ => { });

        /// <summary>Loads a runtime's library and negotiates with it: the
        /// runtime's xrGetInstanceProcAddr, or null with what was read.
        /// Production goes through <see cref="LoadRuntime"/>, and the tests
        /// hand in a managed runtime here, so they load nothing.</summary>
        internal delegate PFN_xrGetInstanceProcAddr RuntimeLoader(OpenXrRuntimeEntry entry, out string read);

        /// <summary>
        /// Negotiates with a runtime and creates an instance, or returns null
        /// with the reason. The session comes from
        /// <see cref="TryCreateSession"/>. The reason is a state rather than
        /// an exception because every one of them is an ordinary machine
        /// configuration, not a fault.
        ///
        /// <para><paramref name="read"/> is what was read when no instance
        /// came of it: the file that was missing, or the call and its
        /// result. The caller writes the one line about it, since it knows
        /// what the answer means for it, and nothing is written here.</para>
        ///
        /// <para><paramref name="runtimeUnavailable"/> says the runtime is
        /// away. It answered XR_ERROR_RUNTIME_UNAVAILABLE while the instance
        /// was set up, which it answers during maintenance (instance.adoc,
        /// XrEventDataInstanceLossPending), or it could not be found, loaded
        /// or negotiated with. The Khronos loader reports those states under
        /// the same code (OpenXR-SDK src/loader/runtime_interface.cpp,
        /// RuntimeInterface::LoadRuntime), and a runtime being updated is in
        /// one of them for as long as its files are off disk.</para>
        ///
        /// <para><paramref name="again"/> is set when the source asks on its
        /// own for a new instance after a retired one. The manifest then
        /// names the runtime by itself. The registered runtimes are not read,
        /// so the ask cannot pass to another runtime, and no instance is
        /// created on an OpenVR client library
        /// (<see cref="IsOpenVrClientLibrary"/>): asking SteamVR for an
        /// instance starts SteamVR.</para>
        /// </summary>
        public static OpenXrSession TryCreate(string manifestPath, bool again, Action<string> log,
                                              out OpenXrSourceState failure, out bool runtimeUnavailable,
                                              out string read)
        {
            // Open in the app. Closed to a test, which gets an exception
            // here in place of whichever runtime the PC has registered.
            VrRuntimeGate.Enter("OpenXrSession.TryCreate, the opener the app uses");
            return TryCreate(manifestPath, again, log, LoadRuntime, OpenXrRuntimeCatalog.Discover,
                             out failure, out runtimeUnavailable, out read);
        }

        /// <summary>The same, with the two things that reach outside the
        /// process handed in: the loader of the runtime's library and the
        /// list of registered runtimes. Every test opens through this one
        /// with its own two. The opener above loads whichever runtime the
        /// PC has registered, and asking SteamVR for an instance starts
        /// SteamVR, so it and <see cref="LoadRuntime"/> refuse a test
        /// (<see cref="VrRuntimeGate"/>).</summary>
        internal static OpenXrSession TryCreate(string manifestPath, bool again, Action<string> log,
                                                RuntimeLoader load,
                                                Func<IReadOnlyList<OpenXrRuntimeEntry>> discover,
                                                out OpenXrSourceState failure, out bool runtimeUnavailable,
                                                out string read)
        {
            failure = OpenXrSourceState.Failed;
            runtimeUnavailable = false;
            read = null;
            var session = new OpenXrSession(log);
            try
            {
                if (again && string.IsNullOrWhiteSpace(manifestPath))
                {
                    // Nothing names the runtime that was in use, and the
                    // choice is not read in its place.
                    read = "no manifest names the runtime that was in use";
                    failure = OpenXrSourceState.Ended;
                    session.Dispose();
                    return null;
                }

                var entry = Choose(manifestPath, discover, out read);
                if (entry != null && again && IsOpenVrClientLibrary(entry.LibraryPath))
                {
                    read = $"{entry.ManifestPath} names an OpenVR client library, {entry.LibraryPath}, " +
                           "where a request for an instance starts SteamVR";
                    failure = OpenXrSourceState.Ended;
                    session.Dispose();
                    return null;
                }
                if (entry != null && !entry.LibraryExists)
                {
                    read = $"the library of {entry.Name}, {entry.LibraryPath}, is not on disk";
                    entry = null;
                }

                if (entry != null) session._getProc = load(entry, out read);
                if (session._getProc == null)
                {
                    read ??= $"the library of {entry?.Name} gave no xrGetInstanceProcAddr";
                    failure = OpenXrSourceState.NoRuntime;
                    runtimeUnavailable = true;
                    session.Dispose();
                    return null;
                }
                session.RuntimeName = entry.Name;
                session.ManifestPath = entry.ManifestPath;
                session.SharesOpenVrClient = IsOpenVrClientLibrary(entry.LibraryPath);

                if (!session.CreateInstance(out failure, out runtimeUnavailable, out read))
                {
                    session.Dispose();
                    return null;
                }
                failure = OpenXrSourceState.Connecting;
                read = null;
                return session;
            }
            catch (Exception ex)
            {
                read = "the setup threw: " + ex.Message;
                session.Dispose();
                failure = OpenXrSourceState.Failed;
                runtimeUnavailable = false;
                return null;
            }
        }

        /// <summary>The runtime to open: the one a manifest names when a
        /// manifest is given, and otherwise the first registered runtime
        /// whose library is on disk, the active one first. Null when there is
        /// none, with what was read.</summary>
        private static OpenXrRuntimeEntry Choose(string manifestPath,
                                                 Func<IReadOnlyList<OpenXrRuntimeEntry>> discover,
                                                 out string read)
        {
            read = null;
            if (!string.IsNullOrWhiteSpace(manifestPath))
            {
                OpenXrRuntimeEntry named = null;
                bool onDisk = false;
                try
                {
                    onDisk = System.IO.File.Exists(manifestPath);
                    if (onDisk)
                        named = OpenXrRuntimeCatalog.TryParseManifest(
                            manifestPath, System.IO.File.ReadAllText(manifestPath));
                }
                catch (Exception) { }
                if (named == null)
                    read = onDisk
                        ? $"{manifestPath} does not read as a runtime manifest"
                        : $"the runtime manifest {manifestPath} is not on disk";
                return named;
            }
            foreach (var entry in discover())
                if (entry.LibraryExists) return entry;
            read = "no registered runtime has its library on disk";
            return null;
        }

        /// <summary>
        /// Loads the manifest's library and negotiates with it: the runtime's
        /// xrGetInstanceProcAddr, or null with what was read.
        ///
        /// <para>The library is deliberately left loaded. A runtime that has
        /// had an instance created and destroyed does not always survive
        /// being unloaded and reloaded in the same process, and the handle
        /// costs nothing.</para>
        /// </summary>
        private static PFN_xrGetInstanceProcAddr LoadRuntime(OpenXrRuntimeEntry entry, out string read)
        {
            read = null;
            // The last step before a runtime's library is in the process.
            VrRuntimeGate.Enter($"the library of {entry.Name}, {entry.LibraryPath}");
            if (!NativeLibrary.TryLoad(entry.LibraryPath, out IntPtr module))
            {
                read = $"the library of {entry.Name}, {entry.LibraryPath}, did not load";
                return null;
            }
            if (!NativeLibrary.TryGetExport(module, "xrNegotiateLoaderRuntimeInterface", out var negotiatePtr))
            {
                read = $"the library of {entry.Name} exports no xrNegotiateLoaderRuntimeInterface";
                return null;
            }

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
                read = NegotiationRead(entry.Name, result);
                return null;
            }

            return Marshal.GetDelegateForFunctionPointer<PFN_xrGetInstanceProcAddr>(
                request.getInstanceProcAddr);
        }

        /// <summary>What a negotiation that gave no xrGetInstanceProcAddr
        /// answered, as it is handed back. It stands apart from the call so
        /// the tests reach its text, which they cannot reach through a
        /// runtime's library.</summary>
        internal static string NegotiationRead(string runtime, int result)
            => $"{runtime} answered xrNegotiateLoaderRuntimeInterface with {Number(result)}" +
               (result == XR_SUCCESS ? " and no xrGetInstanceProcAddr" : string.Empty);

        private T Resolve<T>(string name) where T : Delegate
        {
            if (_getProc(_instance, name, out IntPtr fn) != XR_SUCCESS || fn == IntPtr.Zero)
                return null;
            return Marshal.GetDelegateForFunctionPointer<T>(fn);
        }

        /// <summary>Lists the runtime's extensions and creates the instance.
        /// <paramref name="runtimeUnavailable"/> is set when either call
        /// answers XR_ERROR_RUNTIME_UNAVAILABLE, the code for a runtime that
        /// is away (xr.xml lists it for both). <paramref name="read"/> is
        /// what the runtime answered when no instance came of it.</summary>
        private bool CreateInstance(out OpenXrSourceState failure, out bool runtimeUnavailable, out string read)
        {
            failure = OpenXrSourceState.NotSupported;
            runtimeUnavailable = false;
            read = null;

            var enumerate = Resolve<PFN_xrEnumerateInstanceExtensionProperties>(
                "xrEnumerateInstanceExtensionProperties");
            var create = Resolve<PFN_xrCreateInstance>("xrCreateInstance");
            if (enumerate == null || create == null)
            {
                read = $"{RuntimeName} hands out no " +
                       (enumerate == null ? "xrEnumerateInstanceExtensionProperties" : "xrCreateInstance");
                return false;
            }

            var extensions = ReadExtensions(enumerate, out int listed);
            if (listed != XR_SUCCESS)
            {
                // A listing that failed says nothing about what the runtime
                // offers, so it is not read as a runtime without
                // XR_MND_headless. It used to be, about a runtime a session
                // had just run on.
                read = $"{RuntimeName} answered xrEnumerateInstanceExtensionProperties with {Number(listed)}";
                runtimeUnavailable = listed == XR_ERROR_RUNTIME_UNAVAILABLE;
                return false;
            }
            if (!extensions.Contains(XR_MND_HEADLESS_EXTENSION_NAME))
            {
                read = $"{RuntimeName} lists no {XR_MND_HEADLESS_EXTENSION_NAME}, " +
                       "so a background session would need to own the display";
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
                    read = $"{RuntimeName} answered xrCreateInstance with {Number(result)}";
                    runtimeUnavailable = result == XR_ERROR_RUNTIME_UNAVAILABLE;
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
            _getSystem = Resolve<PFN_xrGetSystem>("xrGetSystem");
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
            // Only claim success once the entry points this needs are all
            // there. Claiming it before the check reported a healthy source
            // for a runtime that never started. xrGetSystem is one of them:
            // without it the ask for the headset failed with nothing in the
            // log, and read as a runtime that reports no headset.
            // xrBeginSession is another: a session that cannot begin has no
            // pose to give, and the row waited on it for one.
            var lacking = new List<string>();
            if (_getSystem == null) lacking.Add("xrGetSystem");
            if (_createSession == null) lacking.Add("xrCreateSession");
            if (_beginSession == null) lacking.Add("xrBeginSession");
            if (_createSpace == null) lacking.Add("xrCreateReferenceSpace");
            if (_locateSpace == null) lacking.Add("xrLocateSpace");
            if (_pollEvent == null) lacking.Add("xrPollEvent");
            if (lacking.Count != 0)
            {
                read = $"{RuntimeName} hands out no {string.Join(", ", lacking)}";
                return false;
            }
            failure = OpenXrSourceState.Connecting;
            return true;
        }

        /// <summary>The names of the extensions the runtime lists.
        /// <paramref name="listed"/> is what the listing answered, from the
        /// call that asks for the count or the one that fills the list.</summary>
        private HashSet<string> ReadExtensions(PFN_xrEnumerateInstanceExtensionProperties enumerate, out int listed)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            listed = enumerate(null, 0, out uint count, IntPtr.Zero);
            if (listed != XR_SUCCESS || count == 0)
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
                listed = enumerate(null, count, out uint written, buffer);
                if (listed != XR_SUCCESS) return names;
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

        /// <summary>
        /// Asks the runtime for its headset (xrGetSystem) and creates a
        /// headless session on it, with its reference spaces and actions.
        ///
        /// <para>XR_ERROR_FORM_FACTOR_UNAVAILABLE means the headset is not
        /// there now, and the runtime may answer the same call with success
        /// later (system.adoc, xrGetSystem), so it is
        /// <see cref="OpenXrSessionCreate.HeadsetUnavailable"/> and the
        /// instance stays for the next ask.</para>
        ///
        /// <para>XR_ERROR_SESSION_LOST from xrCreateReferenceSpace is a
        /// session the runtime lost as it was created: a runtime that
        /// cannot give a lost session a grace period may answer it
        /// (session.adoc, XR_SESSION_STATE_LOSS_PENDING). It is
        /// <see cref="OpenXrSessionCreate.SessionLost"/>, with what was
        /// built destroyed again, and the instance stays for the next ask
        /// here too.</para>
        ///
        /// <para>Anything else that is not a session is
        /// <see cref="OpenXrSessionCreate.Failed"/>: final, with the call
        /// and its result in <paramref name="read"/> and a session half
        /// built destroyed again. The state says why. A lost instance is
        /// <see cref="OpenXrSourceState.Ended"/>, whichever call reports it.
        /// Any other error from xrGetSystem is
        /// <see cref="OpenXrSourceState.Failed"/>, a call that failed. A
        /// session or a reference space the runtime refuses is
        /// <see cref="OpenXrSourceState.NotSupported"/>. None of them is
        /// <see cref="OpenXrSourceState.NoHeadset"/>: this runs at the start
        /// and again at every ask while a headset is away or a session is
        /// lost, and that state is the line of a source still asking.</para>
        /// </summary>
        public OpenXrSessionCreate TryCreateSession(out OpenXrSourceState failure, out string read)
        {
            var systemInfo = new XrSystemGetInfo
            {
                type = XR_TYPE_SYSTEM_GET_INFO,
                formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY,
            };
            int got = _getSystem(_instance, ref systemInfo, out ulong systemId);
            if (got == XR_ERROR_FORM_FACTOR_UNAVAILABLE)
            {
                failure = OpenXrSourceState.NoHeadset;
                read = $"xrGetSystem answered {Number(got)}";
                return OpenXrSessionCreate.HeadsetUnavailable;
            }
            if (got != XR_SUCCESS)
            {
                failure = FailureOf(got, OpenXrSourceState.Failed);
                read = $"xrGetSystem answered {Number(got)}";
                return OpenXrSessionCreate.Failed;
            }

            // No graphics binding in next, which is what makes this headless.
            var sessionInfo = new XrSessionCreateInfo
            {
                type = XR_TYPE_SESSION_CREATE_INFO,
                systemId = systemId,
            };
            int result = _createSession(_instance, ref sessionInfo, out _session);
            if (result != XR_SUCCESS)
            {
                _session = 0;
                failure = FailureOf(result, OpenXrSourceState.NotSupported);
                read = $"xrCreateSession answered {Number(result)} to a session with no graphics binding";
                return OpenXrSessionCreate.Failed;
            }

            _baseSpace = CreateReferenceSpace(XR_REFERENCE_SPACE_TYPE_STAGE, out int based);
            if (_baseSpace == 0) _baseSpace = CreateReferenceSpace(XR_REFERENCE_SPACE_TYPE_LOCAL, out based);
            _viewSpace = CreateReferenceSpace(XR_REFERENCE_SPACE_TYPE_VIEW, out int viewed);
            if (_baseSpace == 0 || _viewSpace == 0)
            {
                // The answer that left a space missing: the local space's
                // when there is no base space, since it is asked for last.
                int answered = _baseSpace == 0 ? based : viewed;
                read = $"xrCreateReferenceSpace answered {Number(answered)} for the " +
                       (_baseSpace == 0 ? "local space, after the stage space" : "view space");
                DestroySession();
                if (answered == XR_ERROR_SESSION_LOST)
                {
                    // Lost between its creation and this call, with no
                    // grace period. With one the same call answers
                    // XR_SESSION_LOSS_PENDING and the pump reads the loss,
                    // and one call later xrBeginSession answers this code
                    // (EndOfBegin). All three are a lost session, which
                    // the source replaces. Read as a runtime that cannot
                    // supply a session, this one stopped the source.
                    failure = OpenXrSourceState.Connecting;
                    return OpenXrSessionCreate.SessionLost;
                }
                failure = FailureOf(answered, OpenXrSourceState.NotSupported);
                return OpenXrSessionCreate.Failed;
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
            read = null;
            return OpenXrSessionCreate.Created;
        }

        /// <summary>
        /// Destroys the session, its actions and its reference spaces, and
        /// keeps the instance. A lost session is destroyed this way, and the
        /// instance asks for the headset again (session.adoc,
        /// XR_SESSION_STATE_LOSS_PENDING).
        /// </summary>
        public void DestroySession()
        {
            try { _actions?.Dispose(); } catch (Exception) { }
            _actions = null;
            try { End(); } catch (Exception) { }
            try { if (_viewSpace != 0) _destroySpace?.Invoke(_viewSpace); } catch (Exception) { }
            try { if (_baseSpace != 0) _destroySpace?.Invoke(_baseSpace); } catch (Exception) { }
            try { if (_session != 0) _destroySession?.Invoke(_session); } catch (Exception) { }
            _viewSpace = _baseSpace = _session = 0;
            _sessionState = XR_SESSION_STATE_IDLE;
            _began = false;
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

        private ulong CreateReferenceSpace(int type, out int result)
        {
            var info = new XrReferenceSpaceCreateInfo
            {
                type = XR_TYPE_REFERENCE_SPACE_CREATE_INFO,
                referenceSpaceType = type,
            };
            info.poseInReferenceSpace.orientation.w = 1f;   // identity
            result = _createSpace(_session, ref info, out ulong space);
            // The call has two success codes (xr.xml), and
            // XR_SESSION_LOSS_PENDING is the second: a session that is being
            // lost simulates success, and its loss arrives as an event
            // (session.adoc, XR_SESSION_STATE_LOSS_PENDING). Read as a
            // failure, it ended the source over a session the pump was about
            // to report lost, which the next ask recovers.
            return result == XR_SUCCESS || result == XR_SESSION_LOSS_PENDING ? space : 0;
        }

        /// <summary>The state a failed call of session creation settles on. A
        /// lost instance is <see cref="OpenXrSourceState.Ended"/> whichever
        /// call reports it (instance.adoc, The Instance Lost Error), and any
        /// other error is the state its call gives.</summary>
        internal static OpenXrSourceState FailureOf(int result, OpenXrSourceState otherwise)
            => result == XR_ERROR_INSTANCE_LOST ? OpenXrSourceState.Ended : otherwise;

        /// <summary>Drains the event queue. Returns what ended, or
        /// <see cref="OpenXrSessionEnd.None"/> while the session and the
        /// instance stand, with what was read in <paramref name="read"/>.
        /// Runs with no session too, since an instance hears its own loss
        /// without one.</summary>
        public OpenXrSessionEnd PumpEvents(out bool referenceSpaceChanged, out string read)
        {
            referenceSpaceChanged = false;
            read = null;
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
                // this thread forever with nothing in the log to say so. The
                // log says so once when a poll first stops at the bound and
                // once when the queue is empty again. A line per poll was
                // 125 a second for as long as the storm lasted.
                for (int drained = 0; ; drained++)
                {
                    if (drained >= MaxEventsPerPoll)
                    {
                        if (_fullPolls++ == 0)
                            _log($"OpenXR: {RuntimeName} gave {Number(MaxEventsPerPoll)} events in one poll, so its queue is read on over the next polls");
                        return OpenXrSessionEnd.None;
                    }
                    var header = (XrEventDataBuffer*)buffer;
                    header->type = XR_TYPE_EVENT_DATA_BUFFER;
                    header->next = IntPtr.Zero;
                    int result = _pollEvent(_instance, buffer);
                    if (result == XR_EVENT_UNAVAILABLE)
                    {
                        if (_fullPolls != 0)
                        {
                            _log($"OpenXR: {RuntimeName} has its event queue empty again, after {Number(_fullPolls)} polls that stopped at {Number(MaxEventsPerPoll)} events");
                            _fullPolls = 0;
                        }
                        return OpenXrSessionEnd.None;
                    }
                    if (result != XR_SUCCESS)
                    {
                        read = $"xrPollEvent answered {Number(result)}";
                        return EndOf(result, 0, 0);
                    }

                    switch (header->type)
                    {
                        case XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED:
                            var changed = (XrEventDataSessionStateChanged*)buffer;
                            if (!IsAboutThisSession(changed->session, ref _strangerStateSaid, "a state")) break;
                            _sessionState = changed->state;
                            if (_sessionState == XR_SESSION_STATE_READY && !_began)
                            {
                                int begun = Begin();
                                var refused = EndOfBegin(begun);
                                if (refused != OpenXrSessionEnd.None)
                                {
                                    read = $"xrBeginSession answered {Number(begun)}";
                                    return refused;
                                }
                            }
                            else if (_sessionState == XR_SESSION_STATE_STOPPING) End();
                            var ended = EndOf(XR_SUCCESS, header->type, _sessionState);
                            if (ended != OpenXrSessionEnd.None)
                            {
                                read = ended == OpenXrSessionEnd.Exiting
                                    ? "XR_SESSION_STATE_EXITING"
                                    : "XR_SESSION_STATE_LOSS_PENDING";
                                return ended;
                            }
                            break;

                        case XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING:
                            // The wait is read while the instance can still
                            // convert the clock, since lossTime is an XrTime.
                            var loss = (XrEventDataInstanceLossPending*)buffer;
                            long nowNs = NowXrTime();
                            long waitMs = nowNs == 0 ? 0 : (loss->lossTime - nowNs) / 1_000_000;
                            InstanceLossWaitMs = Math.Clamp(waitMs, 0, MaxInstanceLossWaitMs) + 500;
                            read = $"XrEventDataInstanceLossPending, its loss time {Number(waitMs)} ms out";
                            return EndOf(XR_SUCCESS, header->type, 0);

                        case XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING:
                            // The event names the session whose space is
                            // changing (spaces.adoc), and it is held to the
                            // rule of the state event. Applied unread, a
                            // stale one from an earlier session recaptured
                            // the neutral of the session that stood.
                            var moved = (XrEventDataReferenceSpaceChangePending*)buffer;
                            if (IsAboutThisSession(moved->session, ref _strangerSpaceSaid, "a reference space change"))
                                referenceSpaceChanged = true;
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Whether an event that names a session is about the one that
        /// stands.
        ///
        /// <para>With no session there is none for the event to be about.
        /// The runtime discards queued events naming a destroyed handle
        /// (fundamentals.adoc, xrPollEvent), so one arriving then would be
        /// its fault, and it is dropped. That covers a runtime whose one
        /// session handle has the same value every time, as Virtual
        /// Desktop's has: what a destroyed session left in the queue is
        /// read with no session standing, before the next one is asked
        /// for.</para>
        ///
        /// <para>An event that names another session is not about this
        /// one. The Khronos sample drops a state event that way and lets a
        /// null handle pass (HandleSessionStateChangedEvent, as ALVR's
        /// engine carries it in openxr_program.cpp), and the log says so
        /// once for each kind of event. Virtual Desktop, Monado and
        /// SteamVR 2.17.10 all write the session's own handle into both
        /// kinds (instance.cpp xrPollEvent, oxr_event.c,
        /// vrclient_x64.dll).</para>
        /// </summary>
        private bool IsAboutThisSession(ulong named, ref bool said, string what)
        {
            if (_session == 0) return false;
            if (named == 0 || named == _session) return true;
            if (!said)
            {
                said = true;
                _log($"OpenXR: {RuntimeName} sent {what} for a session that is not this one, and it is dropped");
            }
            return false;
        }

        /// <summary>
        /// What one poll ends: its result, and when it read an event, the
        /// event's type and the session state it carries.
        /// XR_SESSION_STATE_EXITING ends the XR experience,
        /// XR_SESSION_STATE_LOSS_PENDING the session alone (session.adoc),
        /// the instance-loss event the instance (instance.adoc), and a poll
        /// that failed ends everything, XR_ERROR_INSTANCE_LOST named apart.
        /// </summary>
        internal static OpenXrSessionEnd EndOf(int pollResult, int eventType, int sessionState)
        {
            if (pollResult == XR_EVENT_UNAVAILABLE) return OpenXrSessionEnd.None;
            if (pollResult == XR_ERROR_INSTANCE_LOST) return OpenXrSessionEnd.InstanceLost;
            if (pollResult != XR_SUCCESS) return OpenXrSessionEnd.PollFailed;
            if (eventType == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) return OpenXrSessionEnd.InstanceLossPending;
            if (eventType != XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) return OpenXrSessionEnd.None;
            return sessionState switch
            {
                XR_SESSION_STATE_EXITING => OpenXrSessionEnd.Exiting,
                XR_SESSION_STATE_LOSS_PENDING => OpenXrSessionEnd.SessionLost,
                _ => OpenXrSessionEnd.None,
            };
        }

        /// <summary>
        /// What xrBeginSession's answer ends. Its two success codes end
        /// nothing: XR_SESSION_LOSS_PENDING is a session being lost that
        /// simulates success, and its loss arrives as an event
        /// (session.adoc). A lost instance and a lost session end what they
        /// end from any call. Any other answer leaves a session that never
        /// runs: the runtime sends XR_SESSION_STATE_READY once, the ask is
        /// the same every time, and a session that has not begun locates
        /// nothing, so it is <see cref="OpenXrSessionEnd.BeginRefused"/> and
        /// the source lets the session go. It used to be kept, with the row
        /// waiting for a pose that could not come. On SteamVR such a
        /// session also hears nothing when SteamVR quits, and the client
        /// library logs "Aborting due to ignoring Quit event" for a caller
        /// that still polls it five seconds later (vrclient_x64.dll, SteamVR
        /// 2.17.10).
        /// </summary>
        internal static OpenXrSessionEnd EndOfBegin(int result) => result switch
        {
            XR_SUCCESS or XR_SESSION_LOSS_PENDING => OpenXrSessionEnd.None,
            XR_ERROR_INSTANCE_LOST => OpenXrSessionEnd.InstanceLost,
            XR_ERROR_SESSION_LOST => OpenXrSessionEnd.SessionLost,
            _ => OpenXrSessionEnd.BeginRefused,
        };

        /// <summary>Begins the session, on XR_SESSION_STATE_READY, and
        /// returns what xrBeginSession answered
        /// (<see cref="EndOfBegin"/>).</summary>
        private int Begin()
        {
            var info = new XrSessionBeginInfo
            {
                type = XR_TYPE_SESSION_BEGIN_INFO,
                primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
            };
            int result = _beginSession(_session, ref info);
            if (result == XR_SUCCESS || result == XR_SESSION_LOSS_PENDING) _began = true;
            if (result == XR_SUCCESS) _log($"OpenXR: session running on {RuntimeName}");
            return result;
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
            // XR_SESSION_LOSS_PENDING is a success code of this call too
            // (xr.xml), and it was written as a refusal.
            if (result != XR_SUCCESS && result != XR_SESSION_LOSS_PENDING)
                _log($"OpenXR: {RuntimeName} refused to end the session ({Number(result)})");
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
            DestroySession();
            try { if (_instance != 0) _destroyInstance?.Invoke(_instance); } catch (Exception) { }
            _instance = 0;
            if (_eventBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_eventBuffer);
                _eventBuffer = IntPtr.Zero;
            }
            // The runtime's library stays loaded (LoadRuntime).
        }
    }
}
