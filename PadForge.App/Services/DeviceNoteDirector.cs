using System;
using System.Collections.Generic;
using System.Numerics;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>What a device note says (#597).</summary>
    internal enum DeviceNoteKind
    {
        /// <summary>Nothing.</summary>
        None,
        /// <summary>The device dropped.</summary>
        Disconnected,
        /// <summary>The device connected.</summary>
        Connected,
        /// <summary>The device dropped three times in a minute. Said once,
        /// with the count, and the device says nothing more for five
        /// minutes.</summary>
        KeepsDisconnecting,
        /// <summary>The same three drops with the Device Disconnected Note
        /// off: the five minutes begin and no line shows.</summary>
        Silenced,
        /// <summary>A device the note on screen names as dropped came back:
        /// the note turns to its return in place (#600 item 42).</summary>
        IsBack,
    }

    /// <summary>What the note on screen says of one device, for
    /// <see cref="DeviceNoteDirector.Decide"/>.</summary>
    internal enum DeviceNoteScreen
    {
        /// <summary>No note on screen names it.</summary>
        None,
        /// <summary>The note names it alone: a drop, a return or "is
        /// back".</summary>
        Alone,
        /// <summary>The note names it among others.</summary>
        InBurst,
        /// <summary>The note is its count of drops.</summary>
        Keeps,
    }

    /// <summary>One device assigned to a virtual controller that exists and
    /// is on, as one walk reads it.</summary>
    internal readonly struct DeviceNoteReading
    {
        public DeviceNoteReading(Guid device, bool online, int slots, bool raisesNotes,
            long ownEveryAt = DeviceNoteDirector.Never, long ownRowAt = DeviceNoteDirector.Never)
        {
            Device = device;
            Online = online;
            Slots = slots;
            RaisesNotes = raisesNotes;
            OwnEveryAt = ownEveryAt;
            OwnRowAt = ownRowAt;
        }

        public Guid Device { get; }

        public bool Online { get; }

        /// <summary>The controllers it is assigned to, a bit for each
        /// slot.</summary>
        public int Slots { get; }

        /// <summary>Whether the row is a connection. A row that PadForge or
        /// an extra feeds raises no note of its own, and still keeps a
        /// controller from reading as empty while it is online.</summary>
        public bool RaisesNotes { get; }

        /// <summary>The latest stamp of PadForge's own work that can take
        /// any row offline (PadForge.Engine.OwnDrops.EveryAt), or
        /// Never.</summary>
        public long OwnEveryAt { get; }

        /// <summary>The latest stamp of PadForge's own work that took this
        /// row offline (PadForge.Engine.OwnDrops.RowAt), or Never.</summary>
        public long OwnRowAt { get; }
    }

    /// <summary>What the walk keeps of one device between two reads. Made by
    /// <see cref="Seed"/>, never by default.</summary>
    internal struct DeviceNoteState
    {
        /// <summary>The state the user was last told, or the state the
        /// device was first read in: connected or not.</summary>
        public bool Told;

        /// <summary>When the device was first read away in the absence that
        /// runs now, or <see cref="DeviceNoteDirector.Never"/> while it is
        /// connected.</summary>
        public long AwaySince;

        /// <summary>Whether that absence has been counted as a drop, or
        /// needs no count.</summary>
        public bool DropCounted;

        /// <summary>When the absence that runs now stops being PadForge's
        /// own work: <see cref="DeviceNoteDirector.UntilBack"/> for a row
        /// PadForge took offline itself, or Never when it is none of
        /// it.</summary>
        public long OwnUntil;

        /// <summary>When a note last named the device, or Never.</summary>
        public long LastNote;

        /// <summary>When the last drop was counted, or Never.</summary>
        public long Drop1;

        /// <summary>When the drop before that one was counted, or
        /// Never.</summary>
        public long Drop2;

        /// <summary>The device dropped three times in a minute and its line
        /// is still to be said.</summary>
        public bool KeepsOwed;

        /// <summary>The drops its keeps-disconnecting line counts: three,
        /// and every drop after them while the line waits or shows.</summary>
        public int KeepsCount;

        /// <summary>When the five minutes of silence end, or Never.</summary>
        public long SilentUntil;

        /// <summary>A device read for the first time: what it is now is
        /// what is known of it, so nothing is said. One that is away when
        /// first read was never seen to drop, so that absence counts as no
        /// drop.</summary>
        public static DeviceNoteState Seed(bool online, long nowMs) => new DeviceNoteState
        {
            Told = online,
            AwaySince = online ? DeviceNoteDirector.Never : nowMs,
            DropCounted = !online,
            OwnUntil = DeviceNoteDirector.Never,
            LastNote = DeviceNoteDirector.Never,
            Drop1 = DeviceNoteDirector.Never,
            Drop2 = DeviceNoteDirector.Never,
            SilentUntil = DeviceNoteDirector.Never,
        };
    }

    /// <summary>One line for the flyout.</summary>
    internal readonly struct DeviceNote
    {
        public DeviceNote(DeviceNoteKind kind, int count, Guid device, int emptiedSlot)
            : this(kind, count, device, emptiedSlot, null)
        {
        }

        public DeviceNote(DeviceNoteKind kind, int count, Guid device, int emptiedSlot, IReadOnlyList<Guid> devices)
        {
            Kind = kind;
            Count = count;
            Device = device;
            EmptiedSlot = emptiedSlot;
            Devices = devices ?? (device == Guid.Empty ? Array.Empty<Guid>() : new[] { device });
        }

        public DeviceNoteKind Kind { get; }

        /// <summary>How many devices the line stands for. A line for one
        /// names it.</summary>
        public int Count { get; }

        /// <summary>The device, when the line names one.</summary>
        public Guid Device { get; }

        /// <summary>The controller a drop left with no device connected, or
        /// -1.</summary>
        public int EmptiedSlot { get; }

        /// <summary>Every device the line stands for, in the order they
        /// changed, so a burst names the devices it took (#600 item
        /// 42).</summary>
        public IReadOnlyList<Guid> Devices { get; }

        /// <summary>The drops a keeps-disconnecting line counts.</summary>
        public int Times { get; init; }

        /// <summary>The line changes the note on screen in place, which does
        /// not slide in again (#600 item 42).</summary>
        public bool InPlace { get; init; }

        /// <summary>In place, only the count of drops grew: the note keeps
        /// the time it has left.</summary>
        public bool Grows { get; init; }
    }

    /// <summary>
    /// Decides when the flyout names a mapped device that disconnects or
    /// connects (#597). A device has no event for either, and Step 2 of the
    /// engine clears a row's online flag with no word to anyone, so InputService
    /// reads every mapped row four times a second on the UI timer and hands
    /// the readings here. This class holds no window and reads no device, so
    /// its rules run in a test with no screen, as
    /// <see cref="LayerFlyoutDirector"/>'s do. The rule for one device is
    /// <see cref="Decide"/>, a pure function in the form of the battery
    /// alerts' (InputService.BatteryEdgeDecision).
    ///
    /// <para>A drop is said at the first walk that reads it, with no wait
    /// (#600 item 42): every walk a row is away is input its controller did
    /// not get. What keeps it quiet:</para>
    /// <list type="bullet">
    /// <item>Nothing is said of a state the user never saw change. The first
    /// read of a device is what is known of it, at an engine start, after a
    /// profile switch and for a device assigned since.</item>
    /// <item>A drop PadForge's own work causes says nothing and counts
    /// toward nothing: an absence that begins within <see cref="OwnWorkMs"/>
    /// of a stamp of that work (PadForge.Engine.OwnDrops), until the row
    /// returns when the stamp names the row, and for as long as that
    /// window lasts when it covers every row.</item>
    /// <item>After an engine start, a sleep or a USB port PadForge cycled
    /// itself, the rows are not believed until an enumeration that began
    /// <see cref="SettleMs"/> later has finished. Then every device is read
    /// as for the first time.</item>
    /// <item>A device is named by a new note once in <see cref="RateMs"/>.
    /// A change that falls inside that time is said when it ends, if it
    /// still stands then.</item>
    /// <item>The note on screen follows the devices it names: one that comes
    /// back turns it to its return, one that drops again turns it back, in
    /// place and with no new note.</item>
    /// <item>Every drop counts. The third in a minute is said once, with
    /// the count, and the device says nothing for five minutes.</item>
    /// <item>Devices that change within half a second share one line,
    /// which names them.</item>
    /// <item>Nothing is decided while the flyout shows a profile switch or
    /// the controllers turned on or off, so a change that ends before the
    /// flyout is free is never said. A line that is decided waits its turn
    /// behind a note that is up.</item>
    /// </list>
    /// </summary>
    internal sealed class DeviceNoteDirector
    {
        /// <summary>No time: a moment that has not come.</summary>
        internal const long Never = -1;

        /// <summary>How often InputService reads the rows.</summary>
        internal const int WalkMs = 250;

        /// <summary>The least time between two new notes that name one
        /// device.</summary>
        internal const int RateMs = 10_000;

        /// <summary>Devices that change within this time share one
        /// line.</summary>
        internal const int BurstMs = 500;

        /// <summary>Three drops within this time are a device that keeps
        /// disconnecting.</summary>
        internal const int FlapWindowMs = 60_000;

        /// <summary>How long such a device says nothing.</summary>
        internal const int SilenceMs = 300_000;

        /// <summary>After a mark, the rows are believed once an enumeration
        /// that began this much later has finished. The engine enumerates
        /// every two seconds, and a device that was there all along can
        /// reach its row an enumeration late: SDL's driver for the Xbox 360
        /// wireless receiver announces a pad when it reads the receiver's
        /// answer, and no enumeration waits for that. A USB device takes a
        /// moment to return after sleep or after its port was cycled.</summary>
        internal const int SettleMs = 5000;

        /// <summary>An absence that begins this long after a stamp of
        /// PadForge's own work is that work's, and stays unsaid for as long
        /// again from the stamp. The settle's five seconds: a row an own
        /// cause took away comes back at Step 1's next enumeration, two
        /// seconds apart, after SDL has opened the device again. One still
        /// away when the window ends is read as a drop then.</summary>
        internal const int OwnWorkMs = SettleMs;

        /// <summary>An absence that is PadForge's own work until the row
        /// returns: the row's own stamp names it.</summary>
        internal const long UntilBack = long.MaxValue;

        /// <summary>A walk that comes this long after the one before it
        /// stood still: the PC slept without a word, or the process was
        /// held. What the rows said before is old.</summary>
        internal const int StallMs = 5000;

        private readonly Dictionary<Guid, DeviceNoteState> _states = new Dictionary<Guid, DeviceNoteState>();
        private readonly HashSet<Guid> _read = new HashSet<Guid>();
        private readonly List<Guid> _gone = new List<Guid>();

        // The devices decided on and waiting out their half second, and
        // when the first of each kind was decided.
        private readonly List<Guid> _dropped = new List<Guid>();
        private readonly List<Guid> _returned = new List<Guid>();
        private long _droppedSince = Never;
        private long _returnedSince = Never;

        // The lines that wait for the flyout, the oldest first.
        private readonly List<DeviceNote> _lines = new List<DeviceNote>();

        // The note on screen: the devices it names, in order, the ones of
        // them it says are away, and whether it is a device's count of
        // drops. Empty while no device note shows.
        private readonly List<Guid> _screen = new List<Guid>();
        private readonly HashSet<Guid> _screenAway = new HashSet<Guid>();
        private bool _screenKeeps;
        private int _screenTimes;

        // The moment the rows were last known to be old, and whether an
        // enumeration from after it is still awaited.
        private long _mark = Never;
        private bool _settling;

        private long _lastTick = Never;

        /// <summary>Told of a device that went quiet for five minutes, with
        /// or without its line, and how many drops it counted. InputService
        /// writes the diagnostics line there.</summary>
        internal Action<Guid, int> Silenced { get; set; }

        /// <summary>Whether a device has been read since the last time
        /// everything was forgotten. For the tests.</summary>
        internal bool Knows(Guid device) => _states.ContainsKey(device);

        /// <summary>What the walk keeps of a device. For the tests.</summary>
        internal DeviceNoteState StateOf(Guid device) => _states[device];

        /// <summary>Forgets every device and every line, so the next walk
        /// reads each device as for the first time: a profile switch, which
        /// changes what is mapped, and an engine stop.</summary>
        internal void Reset()
        {
            _states.Clear();
            _dropped.Clear();
            _returned.Clear();
            _droppedSince = Never;
            _returnedSince = Never;
            _lines.Clear();
            ClearScreen();
        }

        private void ClearScreen()
        {
            _screen.Clear();
            _screenAway.Clear();
            _screenKeeps = false;
            _screenTimes = 0;
        }

        /// <summary>The rows are old from <paramref name="atMs"/> on: the
        /// engine started, Windows said it is going to sleep or has woken,
        /// or PadForge cycled a USB port or did other work that drops
        /// devices for a while (PadForge.Engine.OwnDrops.Settle). Everything
        /// is forgotten, and nothing is read until an enumeration that began
        /// <see cref="SettleMs"/> after the latest mark has finished. A
        /// sleep is marked at both ends, so the rows of the moments before
        /// it and of the moments after it are both left unread.</summary>
        internal void Mark(long atMs)
        {
            if (atMs > _mark) _mark = atMs;
            _settling = true;
            Reset();
        }

        /// <summary>One walk. <paramref name="rowsReadAt"/> is when the
        /// enumeration the rows come from began
        /// (InputManager.DevicesEnumeratedSince), or <see cref="Never"/>
        /// while the engine has listed nothing. <paramref name="waiting"/>
        /// holds every decision while the flyout shows something else.
        /// <paramref name="free"/> says the flyout can take a line now.
        /// <paramref name="shown"/> says a device note is on screen, the last
        /// one answered. The answer is a line, one that changes the note on
        /// screen in place, or one whose kind is None.</summary>
        internal DeviceNote Tick(long nowMs, long rowsReadAt, IReadOnlyList<DeviceNoteReading> mapped,
            bool noteDrops, bool noteReturns, bool waiting, bool free, bool shown = false)
        {
            bool stood = _lastTick != Never && nowMs - _lastTick > StallMs;
            _lastTick = nowMs;
            if (stood) Mark(nowMs);
            if (!shown) ClearScreen();
            if (rowsReadAt == Never) return default;
            if (_settling)
            {
                if (rowsReadAt < _mark + SettleMs) return default;
                _settling = false;
            }

            // Both notes off: nothing is kept, so a note turned on later
            // starts from what is true then.
            if (!noteDrops && !noteReturns)
            {
                Reset();
                return default;
            }

            bool changed = false, grows = true;
            _read.Clear();
            for (int i = 0; i < mapped.Count; i++)
            {
                var reading = mapped[i];
                if (!reading.RaisesNotes) continue;
                _read.Add(reading.Device);
                if (!_states.TryGetValue(reading.Device, out var state))
                {
                    _states[reading.Device] = DeviceNoteState.Seed(reading.Online, nowMs);
                    continue;
                }
                var (next, note, inPlace) = Decide(state, reading.Online, nowMs, noteDrops, noteReturns, waiting,
                    reading.OwnEveryAt, ScreenOf(reading.Device), reading.OwnRowAt);
                _states[reading.Device] = next;
                if (inPlace)
                {
                    changed = true;
                    switch (note)
                    {
                        case DeviceNoteKind.KeepsDisconnecting:
                            if (!_screenKeeps) grows = false;
                            if (!_screenKeeps) Silenced?.Invoke(reading.Device, next.KeepsCount);
                            _screen.Clear();
                            _screen.Add(reading.Device);
                            _screenAway.Clear();
                            _screenKeeps = true;
                            _screenTimes = next.KeepsCount;
                            break;
                        case DeviceNoteKind.Disconnected:
                            grows = false;
                            _screenAway.Add(reading.Device);
                            break;
                        case DeviceNoteKind.IsBack:
                            grows = false;
                            _screenAway.Remove(reading.Device);
                            break;
                    }
                    continue;
                }
                switch (note)
                {
                    case DeviceNoteKind.Disconnected:
                        if (_droppedSince == Never) _droppedSince = nowMs;
                        _dropped.Add(reading.Device);
                        break;
                    case DeviceNoteKind.Connected:
                        if (_returnedSince == Never) _returnedSince = nowMs;
                        _returned.Add(reading.Device);
                        break;
                    case DeviceNoteKind.KeepsDisconnecting:
                        _lines.Add(new DeviceNote(DeviceNoteKind.KeepsDisconnecting, 1, reading.Device, -1) { Times = next.KeepsCount });
                        Silenced?.Invoke(reading.Device, next.KeepsCount);
                        break;
                    case DeviceNoteKind.Silenced:
                        Silenced?.Invoke(reading.Device, next.KeepsCount);
                        break;
                }
            }

            // A device that is no longer mapped is forgotten with what was
            // about to be said of it: its controller was deleted or turned
            // off, or it was taken off it.
            _gone.Clear();
            foreach (var device in _states.Keys)
                if (!_read.Contains(device)) _gone.Add(device);
            foreach (var device in _gone)
            {
                _states.Remove(device);
                _dropped.Remove(device);
                _returned.Remove(device);
            }

            if (_droppedSince != Never && nowMs - _droppedSince >= BurstMs)
            {
                Line(DeviceNoteKind.Disconnected, _dropped, mapped);
                _droppedSince = Never;
            }
            if (_returnedSince != Never && nowMs - _returnedSince >= BurstMs)
            {
                Line(DeviceNoteKind.Connected, _returned, mapped);
                _returnedSince = Never;
            }

            // The note on screen changed: it is said again in place, ahead of
            // any line that waits.
            if (changed) return OnScreen(mapped, grows);

            while (free && _lines.Count > 0)
            {
                var line = _lines[0];
                _lines.RemoveAt(0);
                // A line whose note was turned off while it waited, or whose
                // device has left its controller since, is not said. A burst
                // keeps the devices still mapped, and one left of it is said
                // as its own line.
                if (!(line.Kind == DeviceNoteKind.Connected ? noteReturns : noteDrops)) continue;
                if (line.Count == 1 && !_states.ContainsKey(line.Device)) continue;
                if (line.Count > 1)
                {
                    var kept = new List<Guid>(line.Devices.Count);
                    foreach (var device in line.Devices)
                        if (_states.ContainsKey(device)) kept.Add(device);
                    if (kept.Count == 0) continue;
                    if (kept.Count == 1)
                        return Shows(new DeviceNote(line.Kind, 1, kept[0],
                            line.Kind == DeviceNoteKind.Disconnected ? EmptiedSlot(mapped, kept[0]) : -1));
                    if (kept.Count < line.Count) return Shows(new DeviceNote(line.Kind, kept.Count, Guid.Empty, -1, kept));
                }
                if (line.Kind == DeviceNoteKind.KeepsDisconnecting)
                    line = new DeviceNote(line.Kind, 1, line.Device, -1) { Times = _states[line.Device].KeepsCount };
                return Shows(line);
            }
            return default;
        }

        /// <summary>What the note on screen says of a device.</summary>
        private DeviceNoteScreen ScreenOf(Guid device)
        {
            if (_screen.Count == 0 || !_screen.Contains(device)) return DeviceNoteScreen.None;
            if (_screenKeeps) return DeviceNoteScreen.Keeps;
            return _screen.Count == 1 ? DeviceNoteScreen.Alone : DeviceNoteScreen.InBurst;
        }

        /// <summary>A line handed to the flyout as a new note: it is the
        /// note on screen from now.</summary>
        private DeviceNote Shows(DeviceNote line)
        {
            ClearScreen();
            _screen.AddRange(line.Devices);
            switch (line.Kind)
            {
                case DeviceNoteKind.Disconnected:
                    foreach (var device in line.Devices) _screenAway.Add(device);
                    break;
                case DeviceNoteKind.KeepsDisconnecting:
                    _screenKeeps = true;
                    _screenTimes = line.Times;
                    break;
            }
            return line;
        }

        /// <summary>The note on screen as it reads now: the count of drops,
        /// the devices it names that are still away, or all of them back.</summary>
        private DeviceNote OnScreen(IReadOnlyList<DeviceNoteReading> mapped, bool grows)
        {
            if (_screenKeeps)
                return new DeviceNote(DeviceNoteKind.KeepsDisconnecting, 1, _screen[0], -1) { Times = _screenTimes, InPlace = true, Grows = grows };
            if (_screenAway.Count == 0)
                return _screen.Count == 1
                    ? new DeviceNote(DeviceNoteKind.IsBack, 1, _screen[0], -1) { InPlace = true }
                    : new DeviceNote(DeviceNoteKind.IsBack, _screen.Count, Guid.Empty, -1, _screen.ToArray()) { InPlace = true };
            var away = new List<Guid>(_screenAway.Count);
            foreach (var device in _screen)
                if (_screenAway.Contains(device)) away.Add(device);
            return away.Count == 1
                ? new DeviceNote(DeviceNoteKind.Disconnected, 1, away[0], EmptiedSlot(mapped, away[0])) { InPlace = true }
                : new DeviceNote(DeviceNoteKind.Disconnected, away.Count, Guid.Empty, -1, away) { InPlace = true };
        }

        /// <summary>Makes one line of the devices that changed together and
        /// puts it behind the lines that wait. A line of the same kind that
        /// still waits there takes them in, so the flyout never falls
        /// behind what happened.</summary>
        private void Line(DeviceNoteKind kind, List<Guid> devices, IReadOnlyList<DeviceNoteReading> mapped)
        {
            if (devices.Count == 0) return;
            var line = devices.Count == 1
                ? new DeviceNote(kind, 1, devices[0], kind == DeviceNoteKind.Disconnected ? EmptiedSlot(mapped, devices[0]) : -1)
                : new DeviceNote(kind, devices.Count, Guid.Empty, -1, devices.ToArray());
            devices.Clear();
            int last = _lines.Count - 1;
            if (last >= 0 && _lines[last].Kind == kind)
            {
                var together = new List<Guid>(_lines[last].Devices);
                together.AddRange(line.Devices);
                _lines[last] = new DeviceNote(kind, _lines[last].Count + line.Count, Guid.Empty, -1, together);
            }
            else
                _lines.Add(line);
        }

        /// <summary>
        /// The rule for one device, pure so the tests pin it with no device:
        /// what the walk keeps of it, what it reads now, the clock, the
        /// stamp of PadForge's own work that covers it and what the note on
        /// screen says of it give what it keeps next, what, if anything, is
        /// said, and whether that changes the note on screen in place.
        ///
        /// <para>A drop and a return stand at once. An absence that begins
        /// within <see cref="OwnWorkMs"/> of a stamp of PadForge's own work is
        /// that work's, and nothing is said or counted of it: until the row
        /// returns for <paramref name="ownRowAt"/>, a stamp that names the
        /// row, and while the window lasts for <paramref name="ownEveryAt"/>,
        /// one that covers every row, after which a row still away is a drop.
        /// A stamp read after the walk's clock is as recent as can be.</para>
        ///
        /// <para>Every other drop counts. The third within
        /// <see cref="FlapWindowMs"/> is the device that keeps disconnecting,
        /// with a count of three, and each drop while its line waits or
        /// shows adds one. After that line the device says nothing for
        /// <see cref="SilenceMs"/>, during which what the user was told
        /// follows what is true.</para>
        ///
        /// <para>While the Device Disconnected Note is on and the note on
        /// screen names the device, a change of the device changes that note
        /// in place, with no ten seconds to wait: a return turns it to "is
        /// back", a drop back to the drop, and the third drop to the count.
        /// Otherwise what stands is said when it differs from what the user
        /// was last told and the device has not been named for
        /// <see cref="RateMs"/>. Inside that time nothing is said, and the
        /// difference is said when the time ends if it still stands, so the
        /// last thing said of a device is never left wrong. A note that is
        /// off is taken as said.</para>
        ///
        /// <para><paramref name="waiting"/> keeps the start of an absence
        /// and decides nothing: the flyout shows a profile switch. A drop
        /// that outlasts the wait is said after it, and one that ends inside
        /// it never is.</para>
        /// </summary>
        internal static (DeviceNoteState Next, DeviceNoteKind Note, bool InPlace) Decide(
            DeviceNoteState state, bool online, long nowMs, bool noteDrops, bool noteReturns, bool waiting,
            long ownEveryAt = Never, DeviceNoteScreen screen = DeviceNoteScreen.None, long ownRowAt = Never)
        {
            var s = state;
            if (online)
            {
                s.AwaySince = Never;
                s.DropCounted = false;
                s.OwnUntil = Never;
            }
            else if (s.AwaySince == Never)
            {
                s.AwaySince = nowMs;
                s.DropCounted = false;
                s.OwnUntil = Recent(ownRowAt, nowMs) ? UntilBack
                    : Recent(ownEveryAt, nowMs) ? ownEveryAt + OwnWorkMs
                    : Never;
            }
            if (waiting) return (s, DeviceNoteKind.None, false);

            // PadForge's own work: nothing is said or counted while it runs.
            if (!online && s.OwnUntil != Never && nowMs < s.OwnUntil) return (s, DeviceNoteKind.None, false);

            bool silent = s.SilentUntil != Never && nowMs < s.SilentUntil;
            bool dropped = !online && !s.DropCounted;
            if (dropped)
            {
                s.DropCounted = true;
                // The count on screen grows with the drop.
                if (screen == DeviceNoteScreen.Keeps)
                {
                    s.KeepsCount++;
                    s.LastNote = nowMs;
                    return (s, DeviceNoteKind.KeepsDisconnecting, true);
                }
                if (!silent)
                {
                    if (s.KeepsOwed)
                    {
                        s.KeepsCount++;
                    }
                    else if (s.Drop2 != Never && nowMs - s.Drop2 <= FlapWindowMs)
                    {
                        s.KeepsOwed = true;
                        s.KeepsCount = 3;
                        s.Drop1 = Never;
                        s.Drop2 = Never;
                    }
                    else
                    {
                        s.Drop2 = s.Drop1;
                        s.Drop1 = nowMs;
                    }
                }
            }

            // The note on screen names the device: it follows the device in
            // place. A burst cannot become one device's count, so a third
            // drop there waits for its own line.
            if (noteDrops && (screen == DeviceNoteScreen.Alone || screen == DeviceNoteScreen.InBurst))
            {
                if (s.KeepsOwed && screen == DeviceNoteScreen.Alone)
                {
                    s.KeepsOwed = false;
                    s.SilentUntil = nowMs + SilenceMs;
                    s.Told = online;
                    s.LastNote = nowMs;
                    return (s, DeviceNoteKind.KeepsDisconnecting, true);
                }
                if (dropped)
                {
                    s.Told = false;
                    s.LastNote = nowMs;
                    return (s, DeviceNoteKind.Disconnected, true);
                }
                if (online && !s.Told)
                {
                    s.Told = true;
                    s.LastNote = nowMs;
                    return (s, DeviceNoteKind.IsBack, true);
                }
            }

            if (silent)
            {
                s.Told = online;
                return (s, DeviceNoteKind.None, false);
            }

            bool named = s.LastNote != Never && nowMs - s.LastNote < RateMs;
            if (s.KeepsOwed)
            {
                if (noteDrops && named) return (s, DeviceNoteKind.None, false);
                s.KeepsOwed = false;
                s.SilentUntil = nowMs + SilenceMs;
                s.Told = online;
                if (!noteDrops) return (s, DeviceNoteKind.Silenced, false);
                s.LastNote = nowMs;
                return (s, DeviceNoteKind.KeepsDisconnecting, false);
            }

            if (s.Told == online) return (s, DeviceNoteKind.None, false);
            if (!(online ? noteReturns : noteDrops))
            {
                s.Told = online;
                return (s, DeviceNoteKind.None, false);
            }
            if (named) return (s, DeviceNoteKind.None, false);
            s.Told = online;
            s.LastNote = nowMs;
            return (s, online ? DeviceNoteKind.Connected : DeviceNoteKind.Disconnected, false);
        }

        /// <summary>Whether a stamp is recent enough to own an absence that
        /// begins now.</summary>
        private static bool Recent(long stampMs, long nowMs) => stampMs != Never && nowMs - stampMs <= OwnWorkMs;

        /// <summary>The lowest controller a device's drop left with no
        /// device connected, or -1. Every row assigned to the controller
        /// counts, a row that raises no note of its own included: a
        /// controller a merged keyboard row still drives has a device
        /// left.</summary>
        internal static int EmptiedSlot(IReadOnlyList<DeviceNoteReading> mapped, Guid device)
        {
            int own = 0, driven = 0;
            for (int i = 0; i < mapped.Count; i++)
            {
                var reading = mapped[i];
                if (reading.Device == device)
                {
                    // Back already: its controllers have it again.
                    if (reading.Online) return -1;
                    own |= reading.Slots;
                }
                else if (reading.Online)
                {
                    driven |= reading.Slots;
                }
            }
            int emptied = own & ~driven;
            return emptied == 0 ? -1 : BitOperations.TrailingZeroCount(emptied);
        }

        /// <summary>The words of a line. A line for one device names it, by
        /// the name the Devices list shows. <paramref name="controllerName"/>
        /// is the controller the drop emptied, or null.
        /// <paramref name="batteryPercent"/> is the returned device's level,
        /// or negative when PadForge does not know it. A line for several
        /// names each of <paramref name="deviceNames"/>, in the order they
        /// changed (#600 item 42). A device that keeps disconnecting is said
        /// with its count of drops, and a note turned to a return says the
        /// device is back.</summary>
        internal static string Text(DeviceNote note, string deviceName, string controllerName, int batteryPercent,
            IReadOnlyList<string> deviceNames = null)
        {
            var s = Strings.Instance;
            string names = deviceNames == null ? string.Empty : string.Join(", ", deviceNames);
            switch (note.Kind)
            {
                case DeviceNoteKind.Disconnected:
                    if (note.Count > 1) return string.Format(s.Main_DevicesDisconnectedNamed_Format, note.Count, names);
                    return string.IsNullOrEmpty(controllerName)
                        ? string.Format(s.Main_DeviceDisconnected_Format, deviceName)
                        : string.Format(s.Main_DeviceDisconnectedLast_Format, deviceName, controllerName);
                case DeviceNoteKind.Connected:
                    if (note.Count > 1) return string.Format(s.Main_DevicesConnectedNamed_Format, note.Count, names);
                    return batteryPercent >= 0
                        ? string.Format(s.Main_DeviceConnectedBattery_Format, deviceName, batteryPercent)
                        : string.Format(s.Main_DeviceConnected_Format, deviceName);
                case DeviceNoteKind.KeepsDisconnecting:
                    return string.Format(s.Main_DeviceKeepsDisconnecting_Format, deviceName, note.Times);
                case DeviceNoteKind.IsBack:
                    return note.Count > 1
                        ? string.Format(s.Main_DevicesAreBack_Format, note.Count, names)
                        : string.Format(s.Main_DeviceIsBack_Format, deviceName);
                default:
                    return string.Empty;
            }
        }
    }
}
