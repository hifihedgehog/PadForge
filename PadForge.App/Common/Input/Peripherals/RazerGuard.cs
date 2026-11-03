using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>What the Razer worker takes around a request and its reply
    /// (<see cref="RazerGuard"/>), apart from the lock itself so a test
    /// hands the worker one it scripts.</summary>
    internal interface IRazerGuard
    {
        /// <summary>Waits at most <paramref name="waitMs"/> for the lock.
        /// True when the caller may go on: it holds the lock now, or there
        /// is no lock to hold. False when another program kept it for the
        /// whole wait.</summary>
        bool Take(int waitMs);

        /// <summary>Lets go after a <see cref="Take"/> that answered true,
        /// on the thread that took it.</summary>
        void Release();
    }

    /// <summary>
    /// The lock third-party Razer tools share (#494): the named mutex
    /// Global\RazerLinkReadWriteGuardMutex, which OpenRGB creates and holds
    /// around every report it sends (OpenRGB
    /// Controllers/RazerController/RazerDeviceGuard.cpp:21-39 and 61,
    /// RazerController.cpp:2095-2096 and 2133) and uncoil around each request and
    /// its reply (uncoil crates/uncoil-hid/src/guard.rs:24). PadForge takes
    /// it around each request and its reply too, so neither tool reads a
    /// reply to PadForge's request, nor PadForge theirs.
    ///
    /// <para>Nothing shows Razer's own software taking it. uncoil's notes
    /// say that software "appears to share it (not confirmed)" (uncoil
    /// docs/PROTOCOL.md:172-173), and uncoil still tells its user to quit
    /// Synapse, since two programs driving the same devices "fight over
    /// them" (apps/uncoild/src/conflicts.rs:34-37). So this lock is no way
    /// to write beside Synapse: the worker stands down while a Razer
    /// program runs (<see cref="PeripheralPresence.RazerSoftwareRunning"/>).
    /// A second name, Global\RazerReadWriteGuardMutex, guards Razer's fan
    /// controller in LibreHardwareMonitor
    /// (LibreHardwareMonitorLib/Hardware/Mutexes.cs:24) and is not taken.</para>
    ///
    /// <para>The mutex is created with a rule that gives everyone full
    /// control. PadForge runs elevated, and a mutex an elevated process
    /// creates with its default security shuts out an unelevated OpenRGB,
    /// whose CreateMutex asks for every right on a mutex that exists
    /// already and whose wait loop has no way out once that fails
    /// (RazerDeviceGuard.cpp:24-37). OpenRGB creates it with
    /// no access list at all (RazerDeviceGuard.cpp:52-61), and Aurora and
    /// LibreHardwareMonitor with this same rule (Aurora
    /// Project-Aurora/Project-Aurora/Nodes/Razer/RazerFetcher.cs:32-40,
    /// Mutexes.cs:27-39). Where another program made it first with rights
    /// that refuse that, it is opened for waiting alone, and where neither
    /// works the worker goes on without it, which is said once in the log
    /// (Mutexes.cs:41-53 and 119-122, uncoil guard.rs:403-424).</para>
    ///
    /// <para>Waits are bounded: <see cref="PaintWaitMs"/> for a paint,
    /// uncoil's wait, after which that paint is skipped and the next one
    /// carries the newest color (guard.rs:26 and 130-137), and
    /// <see cref="CommandWaitMs"/> for a claim or a hand-back, the wait
    /// LibreHardwareMonitor gives its own Razer lock
    /// (Controller/Razer/RazerFanController.cs:48). A mutex whose owner
    /// died without releasing it belongs to the next waiter, which carries
    /// on with it (guard.rs:390-392, RazerFetcher.cs:53-56).</para>
    ///
    /// <para>A Windows mutex belongs to the thread that took it. The worker
    /// takes and releases it on its own thread and may take it again while
    /// it holds it, for a second device, as long as each take is released.
    /// The crash path takes it on the thread it runs on.</para>
    ///
    /// <para>The test host never makes, opens or takes the shared mutex
    /// (<see cref="KeepOffTheSharedMutexForTest"/>): a test that held it
    /// would hold up every tool on the bench PC that waits for it. A test
    /// gives the guard a mutex of its own, with no name or with a name
    /// nothing else knows.</para>
    /// </summary>
    internal sealed class RazerGuard : IRazerGuard, IDisposable
    {
        /// <summary>The name OpenRGB gives the mutex
        /// (RazerDeviceGuard.cpp:61).</summary>
        internal const string Name = @"Global\RazerLinkReadWriteGuardMutex";

        internal const int PaintWaitMs = 25;
        internal const int CommandWaitMs = 250;

        private static int s_sharedOffLimits;

        private readonly string _name;
        private readonly object _gate = new();
        private Mutex _mutex;
        private bool _opened;
        private bool _disposed;

        public RazerGuard()
            : this(Name) { }

        /// <summary>Test seam: the name, or null for a mutex with no name,
        /// which only this guard can reach.</summary>
        internal RazerGuard(string name)
        {
            _name = name;
        }

        /// <summary>True in the test host, for its whole life, and nowhere
        /// else: the shared mutex is then off limits, and a guard on its
        /// name goes on without a lock, the answer a mutex that could be
        /// neither made nor opened gets. This is for the build in which
        /// the code is wrong, one with a guard taken out to see whether a
        /// test notices, which could otherwise take the lock a Razer tool
        /// on the bench PC waits for.</summary>
        internal static bool SharedMutexOffLimits => Volatile.Read(ref s_sharedOffLimits) != 0;

        /// <summary>Set once by the test host. Nothing lifts it, and
        /// nothing in the app sets it.</summary>
        internal static void KeepOffTheSharedMutexForTest() => Volatile.Write(ref s_sharedOffLimits, 1);

        /// <summary>Whether a name is the shared mutex's in any namespace:
        /// its part after the last backslash, compared without case, so
        /// the session's own namespace is refused with the global one.</summary>
        internal static bool IsShared(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string shared = Name.Substring(Name.LastIndexOf('\\') + 1);
            return string.Equals(name.Substring(name.LastIndexOf('\\') + 1), shared, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True once the mutex could be neither created nor
        /// opened, and the worker goes on without it.</summary>
        internal bool Unavailable
        {
            get { lock (_gate) return _opened && _mutex == null; }
        }

        /// <summary>The mutex, made or opened on first use: a device nobody
        /// lights takes no lock, so none is made for it.</summary>
        private Mutex Open()
        {
            lock (_gate)
            {
                if (_opened || _disposed) return _mutex;
                _opened = true;
                // The test host touches no mutex other tools share
                // (KeepOffTheSharedMutexForTest).
                if (SharedMutexOffLimits && IsShared(_name))
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        "PERIPHERAL Razer lock not available (off limits in the test host), going on without it");
                    return _mutex;
                }
                try
                {
                    var security = new MutexSecurity();
                    security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                        MutexRights.FullControl, AccessControlType.Allow));
                    _mutex = MutexAcl.Create(false, _name, out _, security);
                }
                catch (Exception created)
                {
                    try
                    {
                        _mutex = MutexAcl.OpenExisting(_name, MutexRights.Synchronize | MutexRights.Modify);
                    }
                    catch (Exception opened)
                    {
                        _mutex = null;
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL Razer lock not available ({created.GetType().Name}, {opened.GetType().Name}), going on without it");
                    }
                }
                return _mutex;
            }
        }

        public bool Take(int waitMs)
        {
            var mutex = Open();
            if (mutex == null) return true;
            try { return mutex.WaitOne(Math.Max(0, waitMs)); }
            // Its last owner exited without releasing it. The wait gave it
            // to this thread all the same.
            catch (AbandonedMutexException) { return true; }
            // The handle went away under the wait: nothing is held.
            catch (ObjectDisposedException) { return true; }
        }

        public void Release()
        {
            Mutex mutex;
            lock (_gate) mutex = _mutex;
            if (mutex == null) return;
            try { mutex.ReleaseMutex(); }
            // Not this thread's: a take that went on without the lock.
            catch (ApplicationException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try { _mutex?.Dispose(); } catch { }
                _mutex = null;
            }
        }
    }
}
