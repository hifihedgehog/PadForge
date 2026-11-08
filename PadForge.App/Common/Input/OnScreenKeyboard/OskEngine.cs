using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine.Menus;

namespace PadForge.Common.Input.OnScreenKeyboard
{
    /// <summary>One pointer: whether it points, where, 0 to 1 from the left
    /// and the top, and whether its click is held.</summary>
    public readonly record struct OskTouch(bool Down, float X, float Y, bool Click);

    /// <summary>What the keyboard's controls read this tick (#566): each
    /// control held or not, by <see cref="KeyboardControlAction"/>, the two
    /// steering inputs and the pointers. The runtime fills it from the
    /// keyboard menu's recorded inputs on the real state of the controllers
    /// the keyboard holds, so any device drives it. Steering runs -1 to 1
    /// with +Y down, from four direction controls, each a button or an
    /// axis's depth.</summary>
    public sealed class OskControlState
    {
        /// <summary>One past the last control. The enum is append-only, so
        /// its last member is the bound.</summary>
        public const int ActionCount = (int)KeyboardControlAction.Pointer2Click + 1;

        public readonly bool[] Down = new bool[ActionCount];

        /// <summary>Controls bound to one input share a nonzero number here.
        /// A tap on an input that also moves the keyboard when held fires on
        /// release, so the two are told apart.</summary>
        public readonly int[] Shared = new int[ActionCount];

        /// <summary>The main steering input: Grid's selection, Split's left
        /// half, the wheel's group.</summary>
        public float SX, SY;

        /// <summary>Split's right half.</summary>
        public float S2X, S2Y;

        public OskTouch[] Touch = Array.Empty<OskTouch>();

        public bool Button(KeyboardControlAction a) => Down[(int)a];

        public void Clear()
        {
            Array.Clear(Down);
            Array.Clear(Shared);
            SX = SY = S2X = S2Y = 0;
            Touch = Array.Empty<OskTouch>();
        }

        public void CopyFrom(OskControlState s)
        {
            Array.Copy(s.Down, Down, ActionCount);
            Array.Copy(s.Shared, Shared, ActionCount);
            SX = s.SX; SY = s.SY; S2X = s.S2X; S2Y = s.S2Y;
            Touch = s.Touch;
        }
    }

    /// <summary>A board: rows of keys with each key's starting column. The
    /// suggestion row, when shown, is row 0. A left half aligns its rows to
    /// the right edge, so the split runs straight down the middle.</summary>
    public sealed class OskBoard
    {
        public OskKey[][] Rows;
        public float[][] Starts;
        public float MaxW;
        public bool AlignRight;
        public bool HasSuggestionRow;

        public int Offset => HasSuggestionRow ? 1 : 0;

        internal static OskBoard Build(List<OskKey[]> rows, bool alignRight, bool suggestionRow, float[] suggestionWidths)
        {
            var all = new List<OskKey[]>(rows);
            if (suggestionRow)
            {
                var sug = new OskKey[suggestionWidths.Length];
                int first = 0;
                for (int i = 0; i < sug.Length; i++)
                    sug[i] = new OskKey { Kind = OskKeyKind.Suggestion, SuggestionIndex = first + i, W = suggestionWidths[i] };
                all.Insert(0, sug);
            }
            var b = new OskBoard { Rows = all.ToArray(), AlignRight = alignRight, HasSuggestionRow = suggestionRow };
            float[] widths = b.Rows.Select(r => r.Sum(k => k.W)).ToArray();
            b.MaxW = widths.Length == 0 ? 0 : widths.Max();
            b.Starts = new float[b.Rows.Length][];
            for (int r = 0; r < b.Rows.Length; r++)
            {
                float x = alignRight ? b.MaxW - widths[r] : 0;
                b.Starts[r] = new float[b.Rows[r].Length];
                for (int i = 0; i < b.Rows[r].Length; i++)
                {
                    b.Starts[r][i] = x;
                    x += b.Rows[r][i].W;
                }
            }
            return b;
        }

        /// <summary>Sets the suggestion keys' first index, for the right half
        /// of a split board, whose keys show suggestions 2 and 3.</summary>
        internal OskBoard WithSuggestionStart(int start)
        {
            if (!HasSuggestionRow) return this;
            for (int i = 0; i < Rows[0].Length; i++)
                Rows[0][i] = new OskKey { Kind = OskKeyKind.Suggestion, SuggestionIndex = start + i, W = Rows[0][i].W };
            return this;
        }
    }

    /// <summary>
    /// The On-Screen Keyboard's engine (#566). It runs on the poll thread,
    /// reads the owned controllers each tick, moves the selection, types
    /// through <see cref="IOskOutput"/>, and keeps the modifier latches, the
    /// page, the wheel and the word suggestions. It holds no Windows state of
    /// its own: the layout and the output are handed in, so tests drive it
    /// with a fake layout and record what it would type.
    /// </summary>
    public sealed class OskEngine
    {
        public const int NavDelayMs = 380, NavIntervalMs = 90;
        /// <summary>Both split triggers within this window type a space
        /// (CHI 2006: "hitting both triggers simultaneously enters a space").</summary>
        public const int ChordWindowMs = 90;
        /// <summary>A steering input counts as back under this share of the
        /// deadzone it was pushed past, so one resting at the deadzone does
        /// not flicker.</summary>
        public const float SteerReleaseShare = 0.7f;
        /// <summary>The menu's deadzone: a steering input moves the selection
        /// or picks a group once pushed past it.</summary>
        private float SteerPress => Math.Clamp(Settings.SteerDeadzone, 0.01f, 0.95f);
        private float SteerRelease => SteerPress * SteerReleaseShare;
        /// <summary>Hold-to-move speed: a full push moves the keyboard this
        /// many percent of the work area per second.</summary>
        public const float MovePercentPerSecond = 60f;

        private readonly IOskOutput _out;

        public OskEngine(IOskOutput output) { _out = output; }

        public OskSettings Settings { get; private set; } = OskSettings.Default;
        public IOskLayout Layout { get; private set; }
        /// <summary>The style showing: the saved one at first, then whatever
        /// the style key picks, which the UI saves.</summary>
        public OskStyle Style { get; private set; } = OskStyle.Grid;
        /// <summary>Whether the function row shows: the saved choice at first,
        /// then whatever the Fn key sets, which the UI saves.</summary>
        public bool FunctionRow { get; private set; }
        public bool IsOpen { get; private set; }
        /// <summary>Bumps on every change the window shows.</summary>
        public int Version { get; private set; }

        /// <summary>Windows' own repeat delay and interval for a held key.</summary>
        public int RepeatDelayMs { get; set; } = 500;
        public int RepeatIntervalMs { get; set; } = 33;

        /// <summary>0 off, 1 latched for the next key, 2 locked.</summary>
        public int ShiftState { get; private set; }
        public int CtrlState { get; private set; }
        public int AltState { get; private set; }
        public int WinState { get; private set; }
        public int AltGrState { get; private set; }
        /// <summary>0 letters, 1 symbols.</summary>
        public int Page { get; private set; }

        public sealed class Focus
        {
            public int R, I;
            public float Col = -1;
            public Focus(int r, int i) { R = r; I = i; }
        }

        public readonly Focus GridFocus = new(1, 1);
        public readonly Focus LeftFocus = new(1, 1);
        public readonly Focus RightFocus = new(1, 0);

        public int Petal { get; private set; } = -1;
        public int WheelSet { get; private set; }

        public string[] Suggestions { get; private set; } = Array.Empty<string>();
        private string _word = "", _prevWord = "";
        /// <summary>The word being typed and the one before it, for the
        /// suggestion source, and a number that changes with them.</summary>
        public (string Word, string Previous, int Version) SuggestionQuery => (_word, _prevWord, _queryVersion);
        private int _queryVersion;

        /// <summary>Set when a close control fires. The runtime closes the
        /// keyboard, since it holds the guard that keeps the closing button
        /// away from the game.</summary>
        public bool CloseRequested { get; set; }

        /// <summary>Hold-to-move, in percent of the work area, waiting for the
        /// window. <see cref="TakeMove"/> empties it.</summary>
        private float _moveX, _moveY;

        // ── Settings and layout ─────────────────────────────────

        /// <summary>Takes saved settings. A style or function row that did
        /// not change since the last ones leaves the keyboard's own choice
        /// alone, so a save that lags a key press never undoes it.
        /// <paramref name="force"/> takes both as saved, for a keyboard that
        /// opens for a menu.</summary>
        public void ApplySettings(OskSettings s, bool force = false)
        {
            if (s == null) s = OskSettings.Default;
            var old = Settings;
            Settings = s;
            if (force || old.FunctionRow != s.FunctionRow) SetFunctionRow(s.FunctionRow);
            if (force || old.Style != s.Style) SetStyle(s.Style);
            if (!s.Suggestions && Suggestions.Length > 0) Suggestions = Array.Empty<string>();
            _boards = null;
            Version++;
        }

        private void SetFunctionRow(bool on)
        {
            if (on == FunctionRow) return;
            foreach (var f in new[] { GridFocus, LeftFocus, RightFocus })
            {
                if (on && f.R >= 0) f.R++;
                else if (!on && f.R > 0) f.R--;
            }
            FunctionRow = on;
            _boards = null;
            Version++;
        }

        /// <summary>Switches style. What the controller holds stays ignored
        /// until it is let go, as at opening, so the press that switched types
        /// nothing in the new style. The guard waits for the next tick: the
        /// runtime reads the controls of the style showing, so only then does
        /// a held input read as the new style's control. An input that drives
        /// nothing in the old style and a control in the new one (B, a wheel
        /// pick, is Grid's Close) would otherwise act at once.</summary>
        private void SetStyle(OskStyle style)
        {
            if (style == Style) return;
            Style = style;
            ResetTransients();
            _guardOnNextTick = true;
            _boards = null;
            Version++;
        }

        private bool _guardOnNextTick;

        /// <summary>The style the style key switches to, which the key is
        /// labeled with.</summary>
        internal static OskStyle NextStyle(OskStyle s) => s switch
        {
            OskStyle.Grid => OskStyle.Split,
            OskStyle.Split => OskStyle.Wheel,
            _ => OskStyle.Grid,
        };

        public void SetLayout(IOskLayout layout)
        {
            if (ReferenceEquals(layout, Layout)) return;
            Layout = layout;
            _boards = null;
            _wheel = null;
            _reverse = null;
            Version++;
        }

        /// <summary>The window shows Caps Lock from the layout, which the
        /// runtime refreshes. A change bumps the version.</summary>
        public void NoteCapsChanged() => Version++;

        // ── Open and close ──────────────────────────────────────

        /// <summary>Opens the keyboard. What the controller holds right now
        /// stays ignored until it is let go, so the opener's own buttons type
        /// nothing.</summary>
        public void Open(OskControlState heldNow)
        {
            IsOpen = true;
            _guardOnNextTick = false;
            ShiftState = CtrlState = AltState = WinState = AltGrState = 0;
            Page = 0;
            _word = _prevWord = "";
            _queryVersion++;
            Suggestions = Array.Empty<string>();
            CloseRequested = false;
            _moveX = _moveY = 0;
            ResetTransients();
            GuardEverything(heldNow);
            _boards = null;
            Version++;
        }

        public void Close()
        {
            IsOpen = false;
            CloseRequested = false;
            ResetTransients();
            Version++;
        }

        public (float X, float Y) TakeMove()
        {
            var m = (_moveX, _moveY);
            _moveX = _moveY = 0;
            return m;
        }

        // ── Boards ──────────────────────────────────────────────

        private OskBoard[] _boards;

        /// <summary>The style's boards: one for Grid, the left and right
        /// halves for Split, none for Wheel.</summary>
        public OskBoard[] Boards()
        {
            if (_boards != null) return _boards;
            if (Style == OskStyle.Wheel) return _boards = Array.Empty<OskBoard>();
            var rows = OskLayoutFrame.Rows(Layout, Page == 1 ? OskLayoutFrame.SymbolsOrDefault(Settings.Symbols) : null, FunctionRow);
            bool sug = Settings.Suggestions;
            if (Style == OskStyle.Grid)
            {
                float w = rows.Max(r => r.Sum(k => k.W));
                float a = (float)Math.Ceiling(w / 3), b = (float)Math.Ceiling((w - a) / 2), c = w - a - b;
                _boards = new[] { OskBoard.Build(rows, false, sug, new[] { a, b, c }) };
            }
            else
            {
                var (left, right) = OskLayoutFrame.SplitRows(rows, FunctionRow);
                float lw = left.Max(r => r.Sum(k => k.W)), rw = right.Max(r => r.Sum(k => k.W));
                float r1 = (float)Math.Ceiling(rw / 2);
                _boards = new[]
                {
                    OskBoard.Build(left, true, sug, new[] { lw }),
                    OskBoard.Build(right, false, sug, new[] { r1, rw - r1 }).WithSuggestionStart(1),
                };
            }
            foreach (var f in new[] { GridFocus, LeftFocus, RightFocus }) f.Col = -1;
            return _boards;
        }

        private object[][] _wheel;

        public object[][] WheelSets() => _wheel ??= OskLayoutFrame.WheelSets(Layout);

        public bool IsEnabled(OskKey k) => k.Kind == OskKeyKind.Suggestion
            ? k.SuggestionIndex < Suggestions.Length && !string.IsNullOrEmpty(Suggestions[k.SuggestionIndex])
            : k.IsEnabled;

        private int Nearest(OskBoard b, int br, float col)
        {
            int best = -1;
            float bd = float.MaxValue;
            var row = b.Rows[br];
            for (int i = 0; i < row.Length; i++)
            {
                if (!IsEnabled(row[i])) continue;
                float s = b.Starts[br][i], e = s + row[i].W;
                float d = col >= s && col < e ? 0 : Math.Min(Math.Abs(col - s), Math.Abs(col - e));
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>Puts the focus on an enabled key of the board.</summary>
        public void Normalize(Focus f, OskBoard b)
        {
            int main = b.Rows.Length - b.Offset;
            if (f.R < -b.Offset) f.R = 0;
            if (f.R >= main) f.R = main - 1;
            if (!b.HasSuggestionRow && f.R < 0) f.R = 0;
            int br = f.R + b.Offset;
            var row = b.Rows[br];
            if (f.I < 0) f.I = 0;
            if (f.I >= row.Length) f.I = row.Length - 1;
            if (f.Col < 0) f.Col = b.Starts[br][f.I] + row[f.I].W / 2;
            if (!IsEnabled(row[f.I]))
            {
                int p = Nearest(b, br, f.Col);
                if (p < 0)
                {
                    f.R = 0;
                    br = b.Offset;
                    p = Math.Max(0, Nearest(b, br, f.Col));
                }
                f.I = p;
            }
        }

        public void MoveFocus(Focus f, OskBoard b, string dir)
        {
            Normalize(f, b);
            int br = f.R + b.Offset;
            if (dir == "left" || dir == "right")
            {
                var row = b.Rows[br];
                int n = row.Length, i = f.I;
                for (int s = 0; s < n; s++)
                {
                    i = (i + (dir == "right" ? 1 : -1) + n) % n;
                    if (IsEnabled(row[i])) break;
                }
                f.I = i;
                f.Col = b.Starts[br][i] + row[i].W / 2;
            }
            else
            {
                int n = b.Rows.Length;
                for (int s = 0; s < n; s++)
                {
                    br = (br + (dir == "down" ? 1 : -1) + n) % n;
                    int p = Nearest(b, br, f.Col);
                    if (p >= 0)
                    {
                        f.I = p;
                        f.R = br - b.Offset;
                        break;
                    }
                }
            }
            Version++;
        }

        /// <summary>Puts the focus on the key under a point of the board, in
        /// board units (column, row).</summary>
        public void PointFocus(Focus f, OskBoard b, float x01, float y01)
        {
            int br = Math.Clamp((int)(y01 * b.Rows.Length), 0, b.Rows.Length - 1);
            float col = Math.Clamp(x01, 0, 0.9999f) * b.MaxW;
            int p = Nearest(b, br, col);
            if (p < 0) return;
            int r = br - b.Offset;
            if (f.R != r || f.I != p) Version++;
            f.R = r;
            f.I = p;
            f.Col = col;
        }

        public OskKey FocusedKey(Focus f, OskBoard b)
        {
            Normalize(f, b);
            return b.Rows[f.R + b.Offset][f.I];
        }

        // ── Input ───────────────────────────────────────────────

        private readonly bool[] _was = new bool[OskControlState.ActionCount];
        private readonly bool[] _guard = new bool[OskControlState.ActionCount];
        private bool _guardSteer, _guardSteer2;
        private bool[] _touchGuard = new bool[4];
        private readonly bool[] _touchClickWas = new bool[4];
        private OskControlState _pad = new();
        private long _lastTickMs = -1;
        private bool _movedDuringHold;
        private readonly Dictionary<string, (string Val, long Next)> _reps = new();
        private readonly string[] _steerDir = new string[2];
        private (int Side, long At)? _pendingTrigger;

        private void ResetTransients()
        {
            _reps.Clear();
            Array.Clear(_steerDir);
            _pendingTrigger = null;
            _movedDuringHold = false;
            Petal = -1;
            WheelSet = 0;
            Array.Clear(_was);
        }

        private void GuardEverything(OskControlState held)
        {
            Array.Clear(_guard);
            _guardSteer = _guardSteer2 = false;
            if (held == null) return;
            Array.Copy(held.Down, _guard, OskControlState.ActionCount);
            _guardSteer = Math.Sqrt(held.SX * held.SX + held.SY * held.SY) > SteerRelease;
            _guardSteer2 = Math.Sqrt(held.S2X * held.S2X + held.S2Y * held.S2Y) > SteerRelease;
            int pads = held.Touch?.Length ?? 0;
            if (_touchGuard.Length < pads) _touchGuard = new bool[pads];
            Array.Clear(_touchGuard);
            for (int p = 0; p < pads; p++)
                _touchGuard[p] = held.Touch[p].Down || held.Touch[p].Click;
        }

        private bool Held(KeyboardControlAction a)
        {
            int n = (int)a;
            return n >= 0 && n < OskControlState.ActionCount && _pad.Down[n] && !_guard[n];
        }

        private bool Pressed(KeyboardControlAction a) => Held(a) && !_was[(int)a];
        private bool Released(KeyboardControlAction a) => !Held(a) && _was[(int)a];

        /// <summary>True when a control is bound to the same input as Move the
        /// Keyboard, whose hold moves the keyboard.</summary>
        private bool SharesMove(KeyboardControlAction a)
        {
            if (a == KeyboardControlAction.MoveKeyboard) return false;
            int s = _pad.Shared[(int)a];
            return s != 0 && s == _pad.Shared[(int)KeyboardControlAction.MoveKeyboard];
        }

        /// <summary>A tap control's moment: on press, or on release when its
        /// input also moves the keyboard while held and was not used to.</summary>
        private bool Fire(KeyboardControlAction a)
        {
            if (SharesMove(a)) return Released(a) && !_movedDuringHold;
            return Pressed(a);
        }

        /// <summary>The main steering input is free when Move the Keyboard is
        /// not held, since that hold steers the keyboard instead.</summary>
        private bool SteerFree => !Held(KeyboardControlAction.MoveKeyboard);

        /// <summary>The direction four controls point, the first held one.</summary>
        private string DirButtons(KeyboardControlAction up, KeyboardControlAction down,
            KeyboardControlAction left, KeyboardControlAction right)
        {
            if (Held(up)) return "up";
            if (Held(down)) return "down";
            if (Held(left)) return "left";
            if (Held(right)) return "right";
            return null;
        }

        /// <summary>The direction a steering input points, with hysteresis. A
        /// steering input held at opening reads nothing until it centers.</summary>
        private string DirSteer(int which)
        {
            float x, y;
            if (which == 0)
            {
                if (_guardSteer || !SteerFree) return null;
                x = _pad.SX; y = _pad.SY;
            }
            else
            {
                if (_guardSteer2) return null;
                x = _pad.S2X; y = _pad.S2Y;
            }
            float m = (float)Math.Sqrt(x * x + y * y);
            if (m < (_steerDir[which] != null ? SteerRelease : SteerPress))
            {
                _steerDir[which] = null;
                return null;
            }
            string d = Math.Abs(x) > Math.Abs(y) ? (x > 0 ? "right" : "left") : (y > 0 ? "down" : "up");
            _steerDir[which] = d;
            return d;
        }

        private void Repeat(string name, string val, long now, int delay, int interval, Action<string, bool> fn)
        {
            _reps.TryGetValue(name, out var r);
            if (val == null)
            {
                if (r.Val != null) _reps[name] = (null, 0);
                return;
            }
            if (r.Val != val)
            {
                _reps[name] = (val, now + delay);
                fn(val, false);
                return;
            }
            if (now >= r.Next)
            {
                _reps[name] = (val, now + interval);
                fn(val, true);
            }
        }

        /// <summary>A control that repeats while held at Windows' key repeat.
        /// On an input that also moves the keyboard it fires once, on release.</summary>
        private void RepeatButton(string name, KeyboardControlAction a, long now, Action<bool> fn)
        {
            if (SharesMove(a))
            {
                if (Fire(a)) fn(false);
                return;
            }
            Repeat(name, Held(a) ? "held" : null, now, RepeatDelayMs, RepeatIntervalMs, (_, rep) => fn(rep));
        }

        public void Tick(OskControlState pad, long nowMs)
        {
            _pad = pad ?? new OskControlState();
            long dt = _lastTickMs < 0 ? 0 : Math.Clamp(nowMs - _lastTickMs, 0, 50);
            _lastTickMs = nowMs;
            if (_guardOnNextTick)
            {
                _guardOnNextTick = false;
                GuardEverything(_pad);
            }
            ReleaseGuards();
            if (IsOpen)
            {
                if (Pressed(KeyboardControlAction.MoveKeyboard)) _movedDuringHold = false;
                if (Held(KeyboardControlAction.MoveKeyboard) && !_guardSteer)
                {
                    float m = (float)Math.Sqrt(_pad.SX * _pad.SX + _pad.SY * _pad.SY);
                    if (m > SteerRelease)
                    {
                        float k = MovePercentPerSecond * dt / 1000f;
                        _moveX += _pad.SX * k;
                        _moveY += _pad.SY * k;
                        _movedDuringHold = true;
                    }
                }
                switch (Style)
                {
                    case OskStyle.Split: TickSplit(nowMs); break;
                    case OskStyle.Wheel: TickWheel(nowMs); break;
                    default: TickGrid(nowMs); break;
                }
                if (Style != OskStyle.Wheel) TickTouch(nowMs);
            }
            for (int i = 0; i < OskControlState.ActionCount; i++)
                _was[i] = _pad.Down[i] && !_guard[i];
        }

        private void ReleaseGuards()
        {
            for (int i = 0; i < OskControlState.ActionCount; i++)
                if (_guard[i] && !_pad.Down[i]) _guard[i] = false;
            if (_guardSteer && Math.Sqrt(_pad.SX * _pad.SX + _pad.SY * _pad.SY) < SteerRelease) _guardSteer = false;
            if (_guardSteer2 && Math.Sqrt(_pad.S2X * _pad.S2X + _pad.S2Y * _pad.S2Y) < SteerRelease) _guardSteer2 = false;
            var t = _pad.Touch ?? Array.Empty<OskTouch>();
            for (int p = 0; p < _touchGuard.Length; p++)
                if (_touchGuard[p] && (p >= t.Length || (!t[p].Down && !t[p].Click))) _touchGuard[p] = false;
        }

        private void TickGrid(long now)
        {
            var b = Boards()[0];
            string nav = DirButtons(KeyboardControlAction.MoveUp, KeyboardControlAction.MoveDown,
                KeyboardControlAction.MoveLeft, KeyboardControlAction.MoveRight) ?? DirSteer(0);
            Repeat("nav", nav, now, NavDelayMs, NavIntervalMs, (d, _) => MoveFocus(GridFocus, b, d));
            RepeatButton("type", KeyboardControlAction.TypeKey, now, rep => Activate(FocusedKey(GridFocus, Boards()[0]), rep));
            Common(now);
            if (Fire(KeyboardControlAction.ShiftNextKey)) Cycle(OskSpecial.Shift);
            if (Fire(KeyboardControlAction.SymbolsPage)) TogglePage();
            SuggestionControls();
        }

        private void TickSplit(long now)
        {
            var bs = Boards();
            Repeat("navL", DirSteer(0), now, NavDelayMs, NavIntervalMs, (d, _) => MoveFocus(LeftFocus, bs[0], d));
            Repeat("navR", DirSteer(1), now, NavDelayMs, NavIntervalMs, (d, _) => MoveFocus(RightFocus, bs[1], d));
            if (Fire(KeyboardControlAction.TypeLeftHalf)) TriggerPress(0, now);
            if (Fire(KeyboardControlAction.TypeRightHalf)) TriggerPress(1, now);
            if (_pendingTrigger is { } p && now - p.At > ChordWindowMs)
            {
                _pendingTrigger = null;
                TypeSide(p.Side);
            }
            Common(now);
            if (Fire(KeyboardControlAction.ShiftNextKey) || Fire(KeyboardControlAction.ShiftRightHand)) Cycle(OskSpecial.Shift);
            if (Fire(KeyboardControlAction.SymbolsPage)) TogglePage();
            Repeat("arrows", DirButtons(KeyboardControlAction.ArrowUp, KeyboardControlAction.ArrowDown,
                KeyboardControlAction.ArrowLeft, KeyboardControlAction.ArrowRight),
                now, RepeatDelayMs, RepeatIntervalMs, (d, _) => Special(ArrowOf(d)));
        }

        private void TypeSide(int side)
        {
            var bs = Boards();
            if (bs.Length < 2) return;
            Activate(FocusedKey(side == 0 ? LeftFocus : RightFocus, bs[side]), false);
        }

        /// <summary>Both type controls inside the chord window type a space.
        /// One alone types its half's key once the window passes.</summary>
        private void TriggerPress(int side, long now)
        {
            if (_pendingTrigger is { } p && p.Side != side && now - p.At <= ChordWindowMs)
            {
                _pendingTrigger = null;
                Special(OskSpecial.Space);
                return;
            }
            if (_pendingTrigger is { } q)
            {
                _pendingTrigger = null;
                TypeSide(q.Side);
            }
            _pendingTrigger = (side, now);
        }

        private void TickWheel(long now)
        {
            int petal = PetalOf();
            bool caps = Held(KeyboardControlAction.Capitals), nums = Held(KeyboardControlAction.Numbers);
            int set = caps && nums ? 3 : caps ? 1 : nums ? 2 : 0;
            if (petal != Petal || set != WheelSet)
            {
                Petal = petal;
                WheelSet = set;
                Version++;
            }
            if (Fire(KeyboardControlAction.PickWest)) Pick(0);
            if (Fire(KeyboardControlAction.PickNorth)) Pick(1);
            if (Fire(KeyboardControlAction.PickEast)) Pick(2);
            if (Fire(KeyboardControlAction.PickSouth)) Pick(3);
            Common(now);
            if (Fire(KeyboardControlAction.Home)) Special(OskSpecial.Home);
            if (Fire(KeyboardControlAction.End)) Special(OskSpecial.End);
            SuggestionControls();
        }

        /// <summary>The group the main steering input points at, 0 north and
        /// then clockwise, or -1 when it rests.</summary>
        private int PetalOf()
        {
            if (_guardSteer || !SteerFree) return -1;
            float x = _pad.SX, y = _pad.SY;
            float m = (float)Math.Sqrt(x * x + y * y);
            if (m < (Petal >= 0 ? SteerRelease : SteerPress)) return -1;
            double deg = (Math.Atan2(x, -y) * 180 / Math.PI + 360) % 360;
            return (int)Math.Round(deg / 45) % 8;
        }

        private void Pick(int k)
        {
            if (Petal < 0) return;
            var sets = WheelSets();
            var set = sets[Math.Clamp(WheelSet, 0, sets.Length - 1)];
            int at = Petal * 4 + k;
            if (at >= set.Length) return;
            switch (set[at])
            {
                case string s: TypeCharacter(s); break;
                case OskKey key: Activate(key, false); break;
            }
        }

        /// <summary>The controls every layout shares.</summary>
        private void Common(long now)
        {
            RepeatButton("bk", KeyboardControlAction.Backspace, now, _ => Special(OskSpecial.Backspace));
            RepeatButton("sp", KeyboardControlAction.Space, now, _ => Special(OskSpecial.Space));
            RepeatButton("cl", KeyboardControlAction.CursorLeft, now, _ => Special(OskSpecial.Left));
            RepeatButton("cr", KeyboardControlAction.CursorRight, now, _ => Special(OskSpecial.Right));
            if (Fire(KeyboardControlAction.Enter)) Special(OskSpecial.Enter);
            if (Fire(KeyboardControlAction.Close)) CloseRequested = true;
        }

        /// <summary>Suggestion 1, 2 and 3 take the word suggestion at that place.</summary>
        private void SuggestionControls()
        {
            if (Fire(KeyboardControlAction.Suggestion1)) PickSuggestion(0);
            if (Fire(KeyboardControlAction.Suggestion2)) PickSuggestion(1);
            if (Fire(KeyboardControlAction.Suggestion3)) PickSuggestion(2);
        }

        private void TickTouch(long now)
        {
            var t = _pad.Touch;
            if (t == null || t.Length == 0) return;
            var bs = Boards();
            int pads = Math.Min(2, t.Length);
            for (int p = 0; p < pads; p++)
            {
                var f = t[p];
                bool guarded = p < _touchGuard.Length && _touchGuard[p];
                if (guarded) { _touchClickWas[p] = f.Click; continue; }
                OskBoard board;
                Focus focus;
                float x = f.X;
                if (Style == OskStyle.Grid)
                {
                    board = bs[0];
                    focus = GridFocus;
                    if (pads >= 2) x = p == 0 ? f.X * 0.5f : 0.5f + f.X * 0.5f;
                }
                else if (pads >= 2)
                {
                    board = bs[p];
                    focus = p == 0 ? LeftFocus : RightFocus;
                }
                else
                {
                    bool right = f.X >= 0.5f;
                    board = bs[right ? 1 : 0];
                    focus = right ? RightFocus : LeftFocus;
                    x = right ? (f.X - 0.5f) * 2 : f.X * 2;
                }
                if (f.Down) PointFocus(focus, board, x, f.Y);
                if (f.Click && !_touchClickWas[p]) Activate(FocusedKey(focus, board), false);
                _touchClickWas[p] = f.Click;
            }
        }

        // ── Typing ──────────────────────────────────────────────

        /// <summary>A key clicked with a mouse or a touch screen.</summary>
        public void ClickKey(int board, int row, int index)
        {
            if (!IsOpen) return;
            var bs = Boards();
            if (board < 0 || board >= bs.Length) return;
            var b = bs[board];
            int br = row + b.Offset;
            if (br < 0 || br >= b.Rows.Length || index < 0 || index >= b.Rows[br].Length) return;
            var focus = Style == OskStyle.Grid ? GridFocus : board == 0 ? LeftFocus : RightFocus;
            focus.R = row;
            focus.I = index;
            focus.Col = -1;
            Activate(b.Rows[br][index], false);
            Version++;
        }

        /// <summary>A wheel character clicked with a mouse or a touch screen.</summary>
        public void ClickWheel(int petal, int k)
        {
            if (!IsOpen || petal < 0 || petal > 7 || k < 0 || k > 3) return;
            Petal = petal;
            Pick(k);
            Version++;
        }

        public OskMods CurrentMods()
        {
            var m = OskMods.None;
            if (ShiftState > 0) m |= OskMods.Shift;
            if (CtrlState > 0) m |= OskMods.Ctrl;
            if (AltState > 0) m |= OskMods.Alt;
            if (WinState > 0) m |= OskMods.Win;
            if (AltGrState > 0) m |= OskMods.AltGr;
            return m;
        }

        private bool ShortcutMods => CtrlState > 0 || AltState > 0 || WinState > 0;

        private void ReleaseOnce()
        {
            if (ShiftState == 1) ShiftState = 0;
            if (CtrlState == 1) CtrlState = 0;
            if (AltState == 1) AltState = 0;
            if (WinState == 1) WinState = 0;
            if (AltGrState == 1) AltGrState = 0;
            Version++;
        }

        /// <summary>The text a layout key types at the latched level, with
        /// Windows' Caps Lock, or null.</summary>
        public string LevelText(OskKeyText t)
        {
            bool shift = LevelShift(t), altGr = AltGrState > 0;
            return altGr ? (shift ? t.ShiftAltGr : t.AltGr) : (shift ? t.Shift : t.Base);
        }

        /// <summary>True when the key's latched level is a dead key.</summary>
        public bool LevelDead(OskKeyText t) => t.DeadAt(LevelShift(t), AltGrState > 0);

        private bool LevelShift(OskKeyText t)
        {
            bool shift = ShiftState > 0;
            if (t.CapsAffects && Layout?.CapsOn == true) shift = !shift;
            return shift;
        }

        public void Activate(OskKey k, bool repeat)
        {
            if (k == null || !IsEnabled(k)) return;
            if (repeat && !k.Repeats) return;
            switch (k.Kind)
            {
                case OskKeyKind.Layout:
                {
                    var mods = CurrentMods();
                    _out.TapKey(k.Scan, mods);
                    if (ShortcutMods) ResetWords();
                    else if (!LevelDead(k.LayoutText)) Track(LevelText(k.LayoutText));
                    ReleaseOnce();
                    break;
                }
                case OskKeyKind.Symbol:
                {
                    string s = k.Text;
                    if (ShiftState > 0 && s.Length == 1 && char.IsLetter(s[0])) s = s.ToUpperInvariant();
                    _out.TypeText(s);
                    Track(s);
                    ReleaseOnce();
                    break;
                }
                case OskKeyKind.Suggestion:
                    PickSuggestion(k.SuggestionIndex);
                    break;
                default:
                    Special(k.Special, k.Scan);
                    break;
            }
        }

        private static OskSpecial ArrowOf(string d) => d switch
        {
            "left" => OskSpecial.Left,
            "right" => OskSpecial.Right,
            "up" => OskSpecial.Up,
            _ => OskSpecial.Down,
        };

        public static ushort ScanOf(OskSpecial s) => s switch
        {
            OskSpecial.Backspace => 0x0E,
            OskSpecial.Tab => 0x0F,
            OskSpecial.CapsLock => 0x3A,
            OskSpecial.Enter => 0x1C,
            OskSpecial.Space => 0x39,
            OskSpecial.Esc => 0x01,
            OskSpecial.Left => OskLayoutFrame.Ext | 0x4B,
            OskSpecial.Up => OskLayoutFrame.Ext | 0x48,
            OskSpecial.Down => OskLayoutFrame.Ext | 0x50,
            OskSpecial.Right => OskLayoutFrame.Ext | 0x4D,
            OskSpecial.Home => OskLayoutFrame.Ext | 0x47,
            OskSpecial.End => OskLayoutFrame.Ext | 0x4F,
            OskSpecial.PageUp => OskLayoutFrame.Ext | 0x49,
            OskSpecial.PageDown => OskLayoutFrame.Ext | 0x51,
            OskSpecial.Insert => OskLayoutFrame.Ext | 0x52,
            OskSpecial.Delete => OskLayoutFrame.Ext | 0x53,
            OskSpecial.PrintScreen => OskLayoutFrame.Ext | 0x37,
            _ => 0,
        };

        private void Special(OskSpecial s, ushort scan = 0)
        {
            switch (s)
            {
                case OskSpecial.Shift:
                case OskSpecial.Ctrl:
                case OskSpecial.Alt:
                case OskSpecial.Win:
                case OskSpecial.AltGr:
                    Cycle(s);
                    return;
                case OskSpecial.Page:
                    TogglePage();
                    return;
                case OskSpecial.Fn:
                    SetFunctionRow(!FunctionRow);
                    return;
                case OskSpecial.Layout:
                    SetStyle(NextStyle(Style));
                    return;
                case OskSpecial.CapsLock:
                    _out.TapKey(0x3A, OskMods.None);
                    Version++;
                    return;
            }
            if (scan == 0) scan = ScanOf(s);
            if (scan == 0) return;
            _out.TapKey(scan, CurrentMods());
            if (ShortcutMods)
                ResetWords();
            else
                switch (s)
                {
                    case OskSpecial.Backspace:
                        if (_word.Length > 0) _word = _word.Substring(0, _word.Length - 1);
                        else _prevWord = "";
                        QueryChanged();
                        break;
                    case OskSpecial.Space:
                        Track(" ");
                        break;
                    case OskSpecial.Esc:
                    case OskSpecial.Function:
                    case OskSpecial.Insert:
                    case OskSpecial.PrintScreen:
                        break;
                    default:
                        ResetWords();
                        break;
                }
            ReleaseOnce();
        }

        private void Cycle(OskSpecial s)
        {
            switch (s)
            {
                case OskSpecial.Shift: ShiftState = (ShiftState + 1) % 3; break;
                case OskSpecial.Ctrl: CtrlState = (CtrlState + 1) % 3; break;
                case OskSpecial.Alt: AltState = (AltState + 1) % 3; break;
                case OskSpecial.Win: WinState = (WinState + 1) % 3; break;
                case OskSpecial.AltGr: AltGrState = (AltGrState + 1) % 3; break;
            }
            Version++;
        }

        private void TogglePage()
        {
            Page = Page == 0 ? 1 : 0;
            _boards = null;
            Version++;
        }

        // ── Characters the wheel types ──────────────────────────

        private Dictionary<string, (ushort Scan, OskMods Mods)> _reverse;

        /// <summary>The key and level that type a character on the layout,
        /// from the base level up, so the plainest press wins.</summary>
        private bool FindKey(string ch, out ushort scan, out OskMods mods)
        {
            if (_reverse == null)
            {
                _reverse = new Dictionary<string, (ushort, OskMods)>(StringComparer.Ordinal);
                var scans = OskLayoutFrame.CharRows.SelectMany(r => r)
                    .Concat(new[] { OskLayoutFrame.IsoKey, OskLayoutFrame.JisYen, OskLayoutFrame.JisRo });
                // A dead level is left out: it would hold the accent for the
                // next key instead of typing it, so the character goes out
                // as Unicode instead.
                var levels = new (Func<OskKeyText, string> Get, Func<OskKeyText, bool> Dead, OskMods Mods)[]
                {
                    (t => t.Base, t => t.Dead, OskMods.None),
                    (t => t.Shift, t => t.DeadShift, OskMods.Shift),
                    (t => t.AltGr, t => t.DeadAltGr, OskMods.AltGr),
                    (t => t.ShiftAltGr, t => t.DeadShiftAltGr, OskMods.Shift | OskMods.AltGr),
                };
                if (Layout != null)
                    foreach (var level in levels)
                        foreach (var sc in scans)
                        {
                            var t = Layout.Text(sc);
                            string s = level.Get(t);
                            if (s == null || level.Dead(t)) continue;
                            _reverse.TryAdd(s, (sc, level.Mods));
                        }
            }
            if (_reverse.TryGetValue(ch, out var hit))
            {
                scan = hit.Item1;
                mods = hit.Item2;
                return true;
            }
            scan = 0;
            mods = OskMods.None;
            return false;
        }

        /// <summary>Types one character: by its key when the layout has one,
        /// which Caps Lock would flip back, else as Unicode.</summary>
        public void TypeCharacter(string ch)
        {
            if (string.IsNullOrEmpty(ch)) return;
            if (ch == " ") { Special(OskSpecial.Space); return; }
            if (ShiftState > 0 && ch.Length == 1 && char.IsLetter(ch[0])) ch = ch.ToUpperInvariant();
            if (FindKey(ch, out ushort scan, out OskMods mods))
            {
                var t = Layout.Text(scan);
                if (t.CapsAffects && Layout.CapsOn && (mods & OskMods.AltGr) == 0) mods ^= OskMods.Shift;
                mods |= CurrentMods() & (OskMods.Ctrl | OskMods.Alt | OskMods.Win);
                _out.TapKey(scan, mods);
            }
            else
            {
                _out.TypeText(ch);
            }
            if (ShortcutMods) ResetWords();
            else Track(ch);
            ReleaseOnce();
        }

        // ── Words ───────────────────────────────────────────────

        private static bool IsWordChar(char c) => char.IsLetter(c) || c == '\'' || c == '’';

        private void Track(string produced)
        {
            if (string.IsNullOrEmpty(produced)) return;
            foreach (char c in produced)
            {
                if (IsWordChar(c))
                {
                    _word += c;
                    continue;
                }
                if (_word.Length > 0) _prevWord = _word;
                _word = "";
                if (c == '.' || c == '!' || c == '?' || c == '\n' || !(char.IsWhiteSpace(c) || c == ','))
                    _prevWord = "";
            }
            QueryChanged();
        }

        private void ResetWords()
        {
            _word = _prevWord = "";
            QueryChanged();
        }

        private void QueryChanged()
        {
            _queryVersion++;
            // Keep the suggestions that still fit, so the row does not blink
            // empty between a letter and Windows' answer.
            if (Suggestions.Length > 0)
                Suggestions = _word.Length == 0 ? Array.Empty<string>()
                    : Suggestions.Where(s => s.Length > _word.Length && s.StartsWith(_word, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            Version++;
        }

        /// <summary>Takes a suggestion source's answer for a query. Only words
        /// that start with what was typed show, in its case: Windows' touch
        /// keyboard model also offers near misses ("yep" for "hel").</summary>
        public void SetSuggestions(int queryVersion, IEnumerable<string> words)
        {
            if (queryVersion != _queryVersion || !Settings.Suggestions) return;
            var list = new List<string>();
            foreach (var w in words ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(w) || w.Contains(' ')) continue;
                if (_word.Length > 0 && (w.Length <= _word.Length || !w.StartsWith(_word, StringComparison.CurrentCultureIgnoreCase))) continue;
                string shaped = Shape(w);
                if (!list.Contains(shaped, StringComparer.CurrentCultureIgnoreCase)) list.Add(shaped);
                if (list.Count == 3) break;
            }
            Suggestions = list.ToArray();
            Version++;
        }

        /// <summary>A suggestion in the case of what was typed.</summary>
        private string Shape(string w)
        {
            if (_word.Length == 0) return w;
            bool allCaps = _word.Length > 1 && _word.All(c => !char.IsLetter(c) || char.IsUpper(c));
            if (allCaps) return w.ToUpperInvariant();
            string head = _word.Substring(0, Math.Min(_word.Length, w.Length));
            return head + w.Substring(head.Length);
        }

        public void PickSuggestion(int index)
        {
            if (index < 0 || index >= Suggestions.Length) return;
            string s = Suggestions[index];
            if (string.IsNullOrEmpty(s)) return;
            string rest = s.StartsWith(_word, StringComparison.CurrentCultureIgnoreCase) ? s.Substring(_word.Length) : s;
            _out.TypeText(rest + " ");
            _prevWord = s;
            _word = "";
            Suggestions = Array.Empty<string>();
            _queryVersion++;
            ReleaseOnce();
        }
    }
}
