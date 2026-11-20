using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Flydigi;
using Proto = PadForge.Engine.Flydigi.FlydigiApexProtocol;

namespace PadForge.Common.Input.Flydigi
{
    /// <summary>
    /// The Flydigi Apex pads PadForge has a session with (#586): one
    /// <see cref="FlydigiApexSession"/> per pad that is on, opened when the
    /// poll loop first reads the pad and closed when the pad leaves.
    ///
    /// <para>A pad is asked what it is once, on a pool thread, since the
    /// answer can take seconds. One that does not answer is asked again
    /// after 5 s, then every 30 s: Flydigi Space Station holds the pad's
    /// replies while it runs, and a pad asleep on its receiver answers
    /// nothing. Until a session is up the pad stays what it was before,
    /// read and rumbled by SDL.</para>
    ///
    /// <para>The model a pad answered with is kept on its record
    /// (<see cref="UserDevice.FlydigiApexModel"/>), so its tabs stay up
    /// while it sleeps, as a gamepad's do. A record that takes a model
    /// raises <see cref="ModelRecorded"/>, which saves the settings.</para>
    ///
    /// <para>A pad is here for as long as the poll loop reads it. One that
    /// has gone unread for <see cref="StaleAfterMs"/> has left, whichever
    /// of the input manager's many paths took it offline, and a sweep once
    /// a second closes its session.</para>
    /// </summary>
    internal static class FlydigiApexService
    {
        /// <summary>How long a pad can go unread before it counts as gone.
        /// The poll loop reads every pad at 60 Hz at the least, so this is
        /// 180 missed reads.</summary>
        internal const long StaleAfterMs = 3000;

        private const int SweepMs = 1000;

        private sealed class Entry
        {
            public FlydigiApexSession Session;
            public int Opening;
            public long RetryAt;
            public int Failures;
            public string Path;
            public long PolledAt;
            /// <summary>The model on the pad's record, where it belongs to
            /// the pad's IDs: what an earlier run verified.</summary>
            public FlydigiApexModel Kept;
            /// <summary>The last poll's answer to who writes the rumble,
            /// so a change of hands is seen (<see cref="TakeRumble"/>).</summary>
            public bool SessionRumbled;
            /// <summary>The pad has no slot and was let go for it.</summary>
            public bool LetGo;
        }

        private static readonly ConcurrentDictionary<Guid, Entry> s_entries = new();
        private static readonly ConcurrentDictionary<string, Entry> s_byPath = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object s_sweepGate = new();
        private static Timer s_sweep;
        private static volatile bool s_shutdown;

        /// <summary>The millisecond clock. Tests move it.</summary>
        internal static Func<long> Clock = () => Environment.TickCount64;

        /// <summary>Raised on a pool thread when a pad's model becomes
        /// known or a session comes or goes, so a page that shows by the
        /// model looks again and the lighting worker walks the
        /// collections again.</summary>
        internal static event Action Changed;

        /// <summary>Raised on a pool thread when a pad's record takes a
        /// model it did not hold. The engine saves the settings for it, as
        /// it does when a row finds an output, since nothing else marks the
        /// record changed and the model would otherwise be lost at
        /// exit.</summary>
        internal static event Action ModelRecorded;

        /// <summary>Runs the work that raises <see cref="Changed"/> and
        /// <see cref="ModelRecorded"/>, off the poll thread, which is where
        /// a pad's leaving is seen. Tests run it in place.</summary>
        internal static Action<Action> Post = static work => ThreadPool.UnsafeQueueUserWorkItem(static w => ((Action)w)(), work);

        /// <summary>Whether these IDs can be an Apex pad this code drives.
        /// The pad's own answer decides.</summary>
        internal static bool IsCandidate(ushort vendorId, ushort productId)
            => Proto.CandidateModel(vendorId, productId) != FlydigiApexModel.None;

        /// <summary>The model the pad answered with, this run or an
        /// earlier one. None for a pad never verified.</summary>
        internal static FlydigiApexModel ModelOf(UserDevice ud)
        {
            if (ud == null || !IsCandidate(ud.VendorId, ud.ProdId)) return FlydigiApexModel.None;
            var session = SessionOf(ud.InstanceGuid);
            if (session != null) return session.Identity.Model;
            return KeptModel(ud);
        }

        /// <summary>A record's model is trusted only for the IDs it
        /// belongs to.</summary>
        private static FlydigiApexModel KeptModel(UserDevice ud)
        {
            var kept = (FlydigiApexModel)ud.FlydigiApexModel;
            return kept != FlydigiApexModel.None && kept == Proto.CandidateModel(ud.VendorId, ud.ProdId)
                ? kept : FlydigiApexModel.None;
        }

        /// <summary>Puts the model the pad answered with on its record.
        /// True when the record changed, and then
        /// <see cref="ModelRecorded"/> was raised.</summary>
        internal static bool Record(UserDevice ud, FlydigiApexModel model)
        {
            if (ud.FlydigiApexModel == (int)model) return false;
            ud.FlydigiApexModel = (int)model;
            RaiseModelRecorded();
            return true;
        }

        /// <summary>The Apex 6 Pro's grips play samples.</summary>
        internal static bool HasVoiceCoils(UserDevice ud) => ModelOf(ud) == FlydigiApexModel.Apex6Pro;

        internal static FlydigiApexSession SessionOf(Guid device)
            => s_entries.TryGetValue(device, out var entry) ? Live(entry) : null;

        /// <summary>The session on a collection path, for the lighting
        /// family, which finds the pad by its collection.</summary>
        internal static FlydigiApexSession SessionAt(string path)
            => path != null && s_byPath.TryGetValue(path, out var entry) ? Live(entry) : null;

        /// <summary>The model of the pad the poll loop reads at a
        /// collection path: the one its session answered with, else the
        /// one on its record. None for a path no pad is read at, so a
        /// receiver whose pad is off lists no lights.</summary>
        internal static FlydigiApexModel ModelAt(string path)
        {
            if (path == null || !s_byPath.TryGetValue(path, out var entry)) return FlydigiApexModel.None;
            if (Clock() - Volatile.Read(ref entry.PolledAt) > StaleAfterMs) return FlydigiApexModel.None;
            return Live(entry)?.Identity.Model ?? entry.Kept;
        }

        private static FlydigiApexSession Live(Entry entry)
        {
            var session = Volatile.Read(ref entry.Session);
            return session != null && !session.Failed ? session : null;
        }

        /// <summary>Poll thread, once per read of a pad: keeps its session
        /// and hands it the triggers' positions. Costs a dictionary read
        /// for an Apex pad and a compare for any other.</summary>
        internal static void OnPolled(UserDevice ud, CustomInputState state)
        {
            if (ud == null || s_shutdown || !IsCandidate(ud.VendorId, ud.ProdId)) return;
            if (!s_entries.TryGetValue(ud.InstanceGuid, out var entry))
            {
                entry = s_entries.GetOrAdd(ud.InstanceGuid, static _ => new Entry());
                EnsureSweep();
            }
            Volatile.Write(ref entry.PolledAt, Clock());

            string path = ud.DevicePath;
            if (!ReferenceEquals(entry.Path, path) && !string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                // The pad came back at another path: a new session there.
                // The path changes before the old session is taken, so an
                // open that stores its session after the take sees the new
                // path and closes it (Keep).
                string old = entry.Path;
                entry.Path = path;
                if (old != null) s_byPath.TryRemove(new KeyValuePair<string, Entry>(old, entry));
                Close(entry);
                entry.Kept = KeptModel(ud);
                if (!string.IsNullOrEmpty(path)) s_byPath[path] = entry;
                if (entry.Kept != FlydigiApexModel.None) RaiseChanged();
            }

            var session = Volatile.Read(ref entry.Session);
            if (session != null)
            {
                if (session.Failed)
                {
                    Close(entry);
                    return;
                }
                if (state?.Axis != null && state.Axis.Length > 5)
                    session.SetPositions((byte)(Math.Clamp(state.Axis[2], 0, 65535) >> 8),
                        (byte)(Math.Clamp(state.Axis[5], 0, 65535) >> 8));
                return;
            }

            if (string.IsNullOrEmpty(path) || RealHidAccess.Refused) return;
            long now = Clock();
            if (now < Volatile.Read(ref entry.RetryAt)) return;
            if (Interlocked.Exchange(ref entry.Opening, 1) != 0) return;
            var candidate = Proto.CandidateModel(ud.VendorId, ud.ProdId);
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                FlydigiApexSession opened = null;
                try { opened = FlydigiApexSession.Open(path, candidate, () => s_shutdown); }
                catch (Exception ex) { SdlDiagLog.WriteLine("FLYDIGI open fault: " + ex.GetType().Name); }
                if (opened == null)
                {
                    int failures = ++entry.Failures;
                    Volatile.Write(ref entry.RetryAt, Clock() + (failures <= 1 ? 5000 : 30000));
                    if (failures == 1)
                        SdlDiagLog.WriteLine($"FLYDIGI no identity from vid={ud.VendorId:X4} pid={ud.ProdId:X4}, asking again later");
                }
                else
                {
                    Keep(entry, ud, path, opened);
                }
                Interlocked.Exchange(ref entry.Opening, 0);
            }, null);
        }

        /// <summary>Stores a session that has just opened, then checks
        /// that its pad is still listed at the path it was opened on and
        /// that the engine still runs. The pad's leaving, a new path and
        /// the engine's stop each change their mark before they take the
        /// session, and the store is a full fence ahead of this check, so
        /// a session stored after such a take is seen here. Whichever side
        /// takes the session back closes it. True when it was
        /// kept.</summary>
        private static bool Keep(Entry entry, UserDevice ud, string path, FlydigiApexSession opened)
        {
            Interlocked.Exchange(ref entry.Session, opened);
            if (!s_shutdown && ReferenceEquals(Find(ud.InstanceGuid), entry)
                && string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                entry.Failures = 0;
                entry.Kept = opened.Identity.Model;
                Record(ud, opened.Identity.Model);
                SdlDiagLog.WriteLine($"FLYDIGI session up: {opened.Identity.Model} type={opened.Identity.DeviceType} "
                    + (opened.Identity.Wired ? "cable" : "receiver"));
                RaiseChanged();
                return true;
            }
            // The pad left, moved or the engine stopped while it was asked.
            if (ReferenceEquals(Interlocked.CompareExchange(ref entry.Session, null, opened), opened))
                opened.Dispose();
            return false;
        }

        private static Entry Find(Guid device) => s_entries.TryGetValue(device, out var entry) ? entry : null;

        /// <summary>The pad went offline or left the list.</summary>
        internal static void DeviceGone(Guid device)
        {
            if (!s_entries.TryRemove(device, out var entry)) return;
            if (entry.Path != null) s_byPath.TryRemove(new KeyValuePair<string, Entry>(entry.Path, entry));
            if (!Close(entry)) RaiseChanged();
        }

        /// <summary>Lets the entry's session go. True when it had one, and
        /// then <see cref="Changed"/> was raised.</summary>
        private static bool Close(Entry entry)
        {
            var session = Interlocked.Exchange(ref entry.Session, null);
            if (session == null) return false;
            entry.SessionRumbled = false;
            // The close writes the stop frames, which can wait on the pad.
            ThreadPool.UnsafeQueueUserWorkItem(static s => { try { ((FlydigiApexSession)s).Dispose(); } catch { } }, session);
            RaiseChanged();
            return true;
        }

        private static void EnsureSweep()
        {
            lock (s_sweepGate)
                s_sweep ??= new Timer(static _ => Sweep(), null, SweepMs, SweepMs);
        }

        /// <summary>Lets go of every pad the poll loop has stopped reading,
        /// and stops itself once none is left.</summary>
        internal static void Sweep()
        {
            long now = Clock();
            foreach (var pair in s_entries)
                if (now - Volatile.Read(ref pair.Value.PolledAt) > StaleAfterMs) DeviceGone(pair.Key);
            lock (s_sweepGate)
            {
                if (!s_entries.IsEmpty || s_sweep == null) return;
                s_sweep.Dispose();
                s_sweep = null;
            }
        }

        /// <summary>Whether the pad's session writes its rumble this pass,
        /// with <paramref name="changedHands"/> true on the pass the writer
        /// changes, so the path that held the rumble before can let it go.
        /// A pad without a session is SDL's.</summary>
        internal static bool TakeRumble(UserDevice ud, byte low, byte high, out bool changedHands)
        {
            changedHands = false;
            if (ud == null || !s_entries.TryGetValue(ud.InstanceGuid, out var entry)) return false;
            entry.LetGo = false;
            var session = Volatile.Read(ref entry.Session);
            bool taken = session != null && !session.Failed && session.SetRumble(low, high);
            if (taken != entry.SessionRumbled)
            {
                entry.SessionRumbled = taken;
                changedHands = true;
            }
            return taken;
        }

        /// <summary>Poll thread: the pad is assigned to no slot. Its
        /// triggers are released and its rumble stopped, once, since no
        /// dispatcher drives a pad without a slot.</summary>
        internal static void Unassigned(Guid device)
        {
            if (!s_entries.TryGetValue(device, out var entry) || entry.LetGo) return;
            // A session still asking the pad what it is has nothing to let go
            // yet: the pass after it comes up does it.
            var session = Volatile.Read(ref entry.Session);
            if (session == null) return;
            entry.LetGo = true;
            try { session.Quiet(); } catch { }
        }

        /// <summary>Releases every pad's triggers and stops its rumble,
        /// through each session's own thread: the engine stopped, or the
        /// outputs are suspended.</summary>
        internal static void QuietAll()
        {
            foreach (var entry in s_entries.Values)
                try { Volatile.Read(ref entry.Session)?.Quiet(); } catch { }
        }

        /// <summary>The crash path: the same, written on this thread
        /// within each write's own bound.</summary>
        internal static void QuietAllNow()
        {
            foreach (var entry in s_entries.Values)
                try { Volatile.Read(ref entry.Session)?.QuietNow(); } catch { }
        }

        /// <summary>Closes every session at exit.</summary>
        internal static void Shutdown()
        {
            s_shutdown = true;
            lock (s_sweepGate)
            {
                s_sweep?.Dispose();
                s_sweep = null;
            }
            // On the pool, as Close does: each close writes the stop frames,
            // which can wait on the pad, and the engine stops on the UI
            // thread. The wait is bounded, so a pad that hangs holds no one.
            var closing = new List<System.Threading.Tasks.Task>();
            foreach (var pair in s_entries)
            {
                var session = Interlocked.Exchange(ref pair.Value.Session, null);
                if (session != null)
                    closing.Add(System.Threading.Tasks.Task.Run(() => { try { session.Dispose(); } catch { } }));
            }
            try { System.Threading.Tasks.Task.WaitAll(closing.ToArray(), ShutdownWaitMs); } catch { }
            s_entries.Clear();
            s_byPath.Clear();
        }

        /// <summary>How long <see cref="Shutdown"/> waits for the sessions'
        /// stop frames.</summary>
        internal const int ShutdownWaitMs = 2000;

        /// <summary>Lets sessions open again after <see cref="Shutdown"/>,
        /// for an engine that is started a second time.</summary>
        internal static void Restart() => s_shutdown = false;

        /// <summary>Test seam: a pad the poll loop reads at
        /// <paramref name="path"/>, with <paramref name="session"/> up, as
        /// a finished open leaves it.</summary>
        internal static void AddForTest(Guid device, string path, FlydigiApexSession session)
        {
            var entry = s_entries.GetOrAdd(device, static _ => new Entry());
            entry.Path = path;
            entry.Kept = session?.Identity.Model ?? FlydigiApexModel.None;
            Volatile.Write(ref entry.PolledAt, Clock());
            Volatile.Write(ref entry.Session, session);
            s_byPath[path] = entry;
        }

        /// <summary>Test seam: an open of <paramref name="session"/> at
        /// <paramref name="path"/> that finishes now, for the pad's entry
        /// as it stands, or for one the pad has left when it has none. True
        /// when the session was kept.</summary>
        internal static bool KeepForTest(UserDevice ud, string path, FlydigiApexSession session)
            => Keep(Find(ud.InstanceGuid) ?? new Entry { Path = path }, ud, path, session);

        /// <summary>Test seam: forgets every pad, closing nothing.</summary>
        internal static void ForgetAllForTest()
        {
            s_entries.Clear();
            s_byPath.Clear();
        }

        private static void RaiseChanged()
        {
            try
            {
                Post(static () =>
                {
                    try { Changed?.Invoke(); } catch { }
                });
            }
            catch { }
        }

        private static void RaiseModelRecorded()
        {
            try
            {
                Post(static () =>
                {
                    try { ModelRecorded?.Invoke(); } catch { }
                });
            }
            catch { }
        }
    }
}
