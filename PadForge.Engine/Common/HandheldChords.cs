using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// One learned hidden button on a handheld PC that the firmware delivers as
    /// a key combination (issue #343). <see cref="Keys"/> holds the codes that
    /// go down together: virtual-key codes 0..255 for keyboard keys, and
    /// <see cref="MouseCode"/> + button id (0 left, 1 middle, 2 right, 3 X1,
    /// 4 X2) for mouse buttons, since a few firmwares type a mouse pair
    /// (LButton + XButton2) for a menu key. Keys are stored as the
    /// left/right-specific modifier codes the low-level hook reports
    /// (0xA0..0xA5, 0x5B/0x5C), never the generic 0x10/0x11/0x12.
    /// </summary>
    public sealed class HandheldChordDefinition
    {
        /// <summary>Code space offset for mouse buttons inside <see cref="Keys"/>.</summary>
        public const int MouseCode = 0x1000;

        public string Name { get; set; }
        public int[] Keys { get; set; } = Array.Empty<int>();
        /// <summary>STABLE raw-button index this chord occupies on the
        /// handheld device (NFC's rule: assigned once, never renumbered).</summary>
        public int Button { get; set; }
        /// <summary>Raw Input device path of the keyboard that typed the
        /// chord when it was learned. Informational (the low-level hook
        /// cannot see the source device), shown in the UI so a user can tell
        /// which embedded keyboard a chord came from.</summary>
        public string SourceDevicePath { get; set; }

        public static bool IsMouse(int code) => code >= MouseCode;
        public static bool IsModifier(int code) =>
            code == 0x10 || code == 0x11 || code == 0x12 || code == 0x5B || code == 0x5C
            || (code >= 0xA0 && code <= 0xA5);
        public static bool IsWin(int code) => code == 0x5B || code == 0x5C;
        public static bool IsAlt(int code) => code == 0x12 || code == 0xA4 || code == 0xA5;
        /// <summary>Keys the shell acts on when tapped alone: Win opens
        /// Start, Alt focuses the menu bar. A swallowed chord that contains
        /// one needs the mask key so the release reads as a combination.</summary>
        public static bool NeedsMask(int code) => IsWin(code) || IsAlt(code);
    }

    /// <summary>What the hook should do with the event it just handed in.</summary>
    public enum ChordDecision
    {
        /// <summary>Let the OS see the event.</summary>
        Pass,
        /// <summary>Drop the event. The OS never sees it.</summary>
        Swallow,
    }

    /// <summary>
    /// Pure state machine behind the handheld chord device (issue #343).
    /// The low-level keyboard and mouse hooks feed every key event through
    /// <see cref="OnEvent"/>; the engine answers pass or swallow, keeps the
    /// per-chord button state, and queues replays for keys it held back on
    /// a prefix that never completed. No Win32 in here, so the whole
    /// contract is unit-testable with a fake clock.
    ///
    /// <para>Rules, each one a field-derived requirement:</para>
    /// <list type="bullet">
    /// <item>A key that belongs to no chord passes untouched, and it ends
    /// any prefix in flight (the held keys replay).</item>
    /// <item>Modifiers (Ctrl, Shift, Alt, Win) are never held back on the
    /// way down. Holding a modifier breaks every other shortcut on the
    /// machine, and the firmware chords always end on a non-modifier or on
    /// the last modifier of an all-modifier chord, so the completion point
    /// is still catchable.</item>
    /// <item>A non-modifier key that is a strict prefix of a chord is held
    /// (swallowed) for <see cref="HoldMs"/>. If the chord completes, the
    /// held keys are consumed. If it does not, they replay in order so a
    /// typed F11 still reaches the game.</item>
    /// <item>Chord completion swallows the completing key and asserts the
    /// button. The button releases when any key of the chord goes up; ups
    /// of keys whose downs were swallowed are swallowed too.</item>
    /// <item>A completed chord that contains a Win key asks for a mask key
    /// (<see cref="WinMaskRequested"/>): the OS saw Win go down alone, and
    /// would open Start on its release unless another key lands in
    /// between. The AutoHotkey technique, reserved VK 0xFF.</item>
    /// <item>Capture mode swallows everything and records the set of codes
    /// pressed until all are released, which is how Learn Button records a
    /// chord without the shell reacting to it.</item>
    /// </list>
    /// </summary>
    public sealed class HandheldChordEngine
    {
        /// <summary>How long a prefix key is held before it replays. Firmware
        /// chords arrive within a few milliseconds; a human typing two chord
        /// keys inside this window is rare.</summary>
        public const int HoldMs = 100;

        /// <summary>A capture that sees no key within this window ends empty.</summary>
        public const int CaptureIdleTimeoutMs = 10000;

        private readonly object _lock = new();
        private volatile HandheldChordDefinition[] _chords = Array.Empty<HandheldChordDefinition>();
        private HashSet<int> _chordKeys = new();

        // Physical down state as the hook sees it, by code, plus the order
        // the keys went down in: a prefix is judged in the chord's learned
        // order, so D alone is never held for a Win+D chord.
        private readonly HashSet<int> _down = new();
        private readonly List<int> _downOrder = new();
        // Keys whose DOWN we swallowed and have not yet replayed or consumed.
        private readonly List<(int Code, long AtMs)> _held = new();
        // Keys whose DOWN we swallowed as part of a completed chord, so their UP
        // is swallowed as well.
        private readonly HashSet<int> _consumed = new();
        // Active chords by button.
        private readonly Dictionary<int, HandheldChordDefinition> _active = new();
        private readonly bool[] _buttonState = new bool[CustomInputState.MaxButtons];

        // Capture mode.
        private bool _capturing;
        private long _captureStartMs;
        private long _captureLastEventMs;
        private readonly List<int> _captureOrder = new();
        private readonly HashSet<int> _captureDown = new();
        private bool _captureSawKey;

        /// <summary>Replays the engine wants the hook to inject, in order.
        /// Each entry is a code and whether it is a down. Drained by the hook
        /// after every call that can queue one.</summary>
        public List<(int Code, bool Down)> PendingReplays { get; } = new();

        /// <summary>Set when a completed chord contained a Win key. The hook
        /// injects the mask key and clears it.</summary>
        public bool WinMaskRequested { get; set; }

        /// <summary>Fires on the hook thread when a button changes state.</summary>
        public event Action<int, bool> ButtonChanged;

        /// <summary>Fires on the hook thread when a capture completes with the
        /// codes pressed, in first-down order. Empty on timeout.</summary>
        public event Action<int[]> CaptureCompleted;

        public bool IsCapturing => _capturing;

        public IReadOnlyList<HandheldChordDefinition> Chords => _chords;

        /// <summary>Replaces the chord set. An active chord survives when
        /// the new set still defines the same keys on the same button (the
        /// registry hands out fresh objects on every change); one whose
        /// definition vanished or changed releases its button, with the
        /// event, so the device row does not hold it down.</summary>
        public void SetChords(IEnumerable<HandheldChordDefinition> chords)
        {
            var arr = new List<HandheldChordDefinition>();
            var keys = new HashSet<int>();
            if (chords != null)
                foreach (var c in chords)
                {
                    if (c == null || c.Keys == null || c.Keys.Length == 0) continue;
                    if (c.Button < 0 || c.Button >= CustomInputState.MaxButtons) continue;
                    arr.Add(c);
                    foreach (var k in c.Keys) keys.Add(k);
                }
            List<int> gone = null;
            lock (_lock)
            {
                _chords = arr.ToArray();
                _chordKeys = keys;
                foreach (var kv in _active)
                {
                    bool kept = false;
                    foreach (var c in arr)
                        if (c.Button == kv.Key && SameKeys(c.Keys, kv.Value.Keys)) { kept = true; break; }
                    if (!kept) (gone ??= new List<int>()).Add(kv.Key);
                }
                if (gone != null)
                    foreach (var b in gone) Deactivate(b);
            }
            if (gone != null)
                foreach (var b in gone) ButtonChanged?.Invoke(b, false);
        }

        private static bool SameKeys(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>Forgets every key the hooks reported and releases every
        /// active chord (with events). Called when the hooks detach: an up
        /// the engine never sees would otherwise leave a key "down" here,
        /// and a held prefix would replay as a stale keystroke on the next
        /// attach.</summary>
        public void Reset()
        {
            List<int> gone = null;
            lock (_lock)
            {
                _down.Clear();
                _downOrder.Clear();
                _held.Clear();
                _consumed.Clear();
                PendingReplays.Clear();
                WinMaskRequested = false;
                _capturing = false;
                _captureOrder.Clear();
                _captureDown.Clear();
                foreach (var kv in _active) (gone ??= new List<int>()).Add(kv.Key);
                if (gone != null) foreach (var b in gone) Deactivate(b);
            }
            if (gone != null)
                foreach (var b in gone) ButtonChanged?.Invoke(b, false);
        }

        /// <summary>True when any chord is defined, so the hook host knows
        /// the hooks must stay installed even with nothing suppressed.</summary>
        public bool HasChords => _chords.Length > 0;

        /// <summary>Copies the chord button states into a device state array.</summary>
        public void CopyButtonState(bool[] dest)
        {
            if (dest == null) return;
            lock (_lock)
            {
                int n = Math.Min(dest.Length, _buttonState.Length);
                Array.Copy(_buttonState, dest, n);
            }
        }

        public bool IsButtonDown(int button)
        {
            if (button < 0 || button >= _buttonState.Length) return false;
            lock (_lock) return _buttonState[button];
        }

        // ─────────────────────────────────────────────
        //  Capture (Learn Button)
        // ─────────────────────────────────────────────

        public void BeginCapture(long nowMs)
        {
            lock (_lock)
            {
                _capturing = true;
                _captureStartMs = nowMs;
                _captureLastEventMs = nowMs;
                _captureOrder.Clear();
                _captureDown.Clear();
                _captureSawKey = false;
            }
        }

        public void CancelCapture()
        {
            lock (_lock)
            {
                _capturing = false;
                _captureOrder.Clear();
                _captureDown.Clear();
            }
        }

        // ─────────────────────────────────────────────
        //  Event entry
        // ─────────────────────────────────────────────

        /// <summary>Feeds one key or mouse-button event. <paramref name="code"/>
        /// is a VK code or <see cref="HandheldChordDefinition.MouseCode"/> +
        /// button id. Returns whether the hook must swallow it.</summary>
        public ChordDecision OnEvent(int code, bool down, long nowMs)
        {
            Action<int[]> captureDone = null;
            int[] captureResult = null;
            List<(int, bool)> changes = null;
            ChordDecision decision;
            lock (_lock)
            {
                if (_capturing)
                {
                    decision = CaptureEvent(code, down, nowMs, out captureResult);
                    if (captureResult != null) captureDone = CaptureCompleted;
                }
                else
                {
                    decision = MatchEvent(code, down, nowMs, ref changes);
                }
            }
            if (changes != null)
                foreach (var (b, s) in changes) ButtonChanged?.Invoke(b, s);
            captureDone?.Invoke(captureResult);
            return decision;
        }

        /// <summary>Time-driven housekeeping: replays prefixes whose hold
        /// window expired and ends idle captures. The hook host calls this
        /// on a short timer (the hold window) whenever keys are held.</summary>
        public void Tick(long nowMs)
        {
            Action<int[]> captureDone = null;
            int[] captureResult = null;
            lock (_lock)
            {
                if (_capturing)
                {
                    if (nowMs - _captureLastEventMs >= CaptureIdleTimeoutMs)
                    {
                        _capturing = false;
                        captureResult = _captureSawKey ? _captureOrder.ToArray() : Array.Empty<int>();
                        captureDone = CaptureCompleted;
                        _captureOrder.Clear();
                        _captureDown.Clear();
                    }
                }
                else if (_held.Count > 0 && nowMs - _held[0].AtMs >= HoldMs)
                {
                    ReplayHeld();
                }
            }
            captureDone?.Invoke(captureResult);
        }

        /// <summary>True while a prefix is held, so the host knows to tick.</summary>
        public bool HasHeldKeys { get { lock (_lock) return _held.Count > 0; } }

        /// <summary>Moves the queued replays into <paramref name="dest"/>
        /// under the engine lock. The hook thread queues while the replay
        /// thread drains, so the list is never read bare.</summary>
        public void DrainReplays(List<(int Code, bool Down)> dest)
        {
            lock (_lock)
            {
                if (PendingReplays.Count == 0) return;
                dest.AddRange(PendingReplays);
                PendingReplays.Clear();
            }
        }

        /// <summary>Consumes the Win mask request. True once per request.</summary>
        public bool TakeWinMask()
        {
            lock (_lock)
            {
                bool r = WinMaskRequested;
                WinMaskRequested = false;
                return r;
            }
        }

        /// <summary>True when the hook host has something to do soon: a
        /// replay or mask to inject, a held prefix to time out, or a
        /// capture that may go idle.</summary>
        public bool HasPendingWork
        {
            get
            {
                lock (_lock)
                    return PendingReplays.Count > 0 || WinMaskRequested || _held.Count > 0 || _capturing;
            }
        }

        // ─────────────────────────────────────────────
        //  Internals (caller holds _lock)
        // ─────────────────────────────────────────────

        private ChordDecision CaptureEvent(int code, bool down, long nowMs, out int[] result)
        {
            result = null;
            _captureLastEventMs = nowMs;
            if (down)
            {
                if (_captureDown.Add(code))
                {
                    _captureSawKey = true;
                    if (!_captureOrder.Contains(code)) _captureOrder.Add(code);
                }
            }
            else
            {
                // An up for a key whose down the OS already saw (the Enter
                // that clicked Start Learning) must reach the OS too, or the
                // key stays down there until its next press.
                if (!_captureDown.Remove(code))
                    return ChordDecision.Pass;
                if (_captureSawKey && _captureDown.Count == 0)
                {
                    _capturing = false;
                    result = _captureOrder.ToArray();
                    _captureOrder.Clear();
                }
            }
            // Everything else is swallowed during a capture, so pressing the
            // paddle to learn it cannot minimize the desktop or open Start.
            return ChordDecision.Swallow;
        }

        private ChordDecision MatchEvent(int code, bool down, long nowMs, ref List<(int, bool)> changes)
        {
            var chords = _chords;
            if (down)
            {
                // Repeat of an already-down key (auto-repeat): keep whatever
                // decision the first down got.
                if (_down.Contains(code))
                    return _consumed.Contains(code) || IsHeld(code) ? ChordDecision.Swallow : ChordDecision.Pass;
                _down.Add(code);
                _downOrder.Add(code);

                if (!_chordKeys.Contains(code))
                {
                    // Foreign key ends any prefix in flight.
                    if (_held.Count > 0) ReplayHeld();
                    return ChordDecision.Pass;
                }

                // Complete chord? Every key of the chord is physically down.
                // When two chords complete at once (Win+D and Ctrl+Win+D
                // with all three down), the longer one is the one the user
                // pressed; the shorter is its subset.
                HandheldChordDefinition completed = null;
                foreach (var c in chords)
                {
                    if (_active.ContainsKey(c.Button)) continue;
                    if (!AllDown(c.Keys)) continue;
                    if (completed == null || c.Keys.Length > completed.Keys.Length) completed = c;
                }
                if (completed != null)
                {
                    // Held prefix keys are consumed, never replayed.
                    foreach (var h in _held) _consumed.Add(h.Code);
                    _held.Clear();
                    _consumed.Add(code);
                    _active[completed.Button] = completed;
                    _buttonState[completed.Button] = true;
                    (changes ??= new List<(int, bool)>()).Add((completed.Button, true));
                    foreach (var k in completed.Keys)
                        if (HandheldChordDefinition.NeedsMask(k)) { WinMaskRequested = true; break; }
                    return ChordDecision.Swallow;
                }

                // Prefix of at least one chord?
                bool prefix = false;
                foreach (var c in chords)
                {
                    if (IsPrefixOf(c.Keys)) { prefix = true; break; }
                }
                if (!prefix)
                {
                    if (_held.Count > 0) ReplayHeld();
                    return ChordDecision.Pass;
                }
                if (HandheldChordDefinition.IsModifier(code))
                    return ChordDecision.Pass; // modifiers are never held back
                _held.Add((code, nowMs));
                return ChordDecision.Swallow;
            }
            else
            {
                _down.Remove(code);
                _downOrder.Remove(code);

                // Release of an active chord's key drops the chord.
                List<int> release = null;
                foreach (var kv in _active)
                    if (Array.IndexOf(kv.Value.Keys, code) >= 0)
                        (release ??= new List<int>()).Add(kv.Key);
                if (release != null)
                    foreach (var b in release)
                    {
                        Deactivate(b);
                        (changes ??= new List<(int, bool)>()).Add((b, false));
                    }

                if (_consumed.Remove(code))
                    return ChordDecision.Swallow;

                // A held prefix key released before its chord completed: the
                // user tapped it. Replay the whole prefix now so the tap types.
                if (IsHeld(code))
                {
                    ReplayHeld();
                    PendingReplays.Add((code, false));
                    return ChordDecision.Swallow;
                }
                return ChordDecision.Pass;
            }
        }

        private void Deactivate(int button)
        {
            if (_active.Remove(button))
                _buttonState[button] = false;
        }

        private bool IsHeld(int code)
        {
            foreach (var h in _held) if (h.Code == code) return true;
            return false;
        }

        private bool AllDown(int[] keys)
        {
            foreach (var k in keys) if (!_down.Contains(k)) return false;
            return true;
        }

        /// <summary>True when the chord keys currently down, in the order
        /// they went down, are a proper leading segment of
        /// <paramref name="keys"/> (the learned order). Order matters: for
        /// a Win+D chord, D alone is typing, not a prefix, so WASD never
        /// pays the hold. Firmware always types its chord in one order,
        /// the order the capture recorded.</summary>
        private bool IsPrefixOf(int[] keys)
        {
            int n = 0;
            foreach (var d in _downOrder)
            {
                if (!_chordKeys.Contains(d)) continue;
                if (n >= keys.Length || keys[n] != d) return false;
                n++;
            }
            return n > 0 && n < keys.Length;
        }

        private void ReplayHeld()
        {
            foreach (var h in _held)
                PendingReplays.Add((h.Code, true));
            _held.Clear();
        }
    }
}
