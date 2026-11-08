using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.OnScreenKeyboard;
using PadForge.Engine.Menus;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The On-Screen Keyboard's engine (#566), driven with a US layout and a
    /// record of what it would send, so no test types into the PC. A press
    /// here is the press of a gamepad position, read the way the runtime reads
    /// a keyboard menu with every control at its layout's default: each
    /// control whose default is that position is held.
    /// </summary>
    public class OnScreenKeyboardEngineTests
    {
        /// <summary>A US QWERTY layout by set 1 scan code, no AltGr level.</summary>
        internal sealed class UsLayout : IOskLayout
        {
            private static readonly Dictionary<ushort, OskKeyText> Map = Build();

            private static Dictionary<ushort, OskKeyText> Build()
            {
                var d = new Dictionary<ushort, OskKeyText>();
                void Row(ushort[] scans, string b, string s)
                {
                    for (int i = 0; i < scans.Length; i++)
                        d[scans[i]] = new OskKeyText(b[i].ToString(), s[i].ToString(), null, null);
                }
                Row(OskLayoutFrame.CharRows[0], "`1234567890-=", "~!@#$%^&*()_+");
                Row(OskLayoutFrame.CharRows[1], "qwertyuiop[]\\", "QWERTYUIOP{}|");
                Row(OskLayoutFrame.CharRows[2], "asdfghjkl;'", "ASDFGHJKL:\"");
                Row(OskLayoutFrame.CharRows[3], "zxcvbnm,./", "ZXCVBNM<>?");
                return d;
            }

            public string Name => "English (United States)";
            public string LanguageTag => "en-US";
            public bool HasAltGr => false;
            public bool CapsOn { get; set; }
            public OskKeyText Text(ushort scan) => Map.TryGetValue(scan, out var t) ? t : default;
        }

        internal sealed class Record : IOskOutput
        {
            public readonly List<string> Sent = new();
            public void TapKey(ushort scan, OskMods mods) => Sent.Add($"tap {scan:X4} {mods}");
            public void TypeText(string text) => Sent.Add("text " + text);
        }

        private const string A = "Gamepad ButtonA", B = "Gamepad ButtonB", X = "Gamepad ButtonX", Y = "Gamepad ButtonY",
            LB = "Gamepad LeftShoulder", RB = "Gamepad RightShoulder", LT = "Gamepad LeftTrigger", RT = "Gamepad RightTrigger",
            Back = "Gamepad ButtonBack", Start = "Gamepad ButtonStart",
            Up = "Gamepad DPadUp", Down = "Gamepad DPadDown", Left = "Gamepad DPadLeft", Right = "Gamepad DPadRight",
            RsLeft = "IHGamepad RightStickX", RsUp = "IHGamepad RightStickY";

        private readonly Record _out = new();
        private readonly OskEngine _e;
        private readonly OskControlState _pad = new();
        private readonly HashSet<string> _held = new();
        private long _now = 1000;

        public OnScreenKeyboardEngineTests()
        {
            _e = new OskEngine(_out) { RepeatDelayMs = 500, RepeatIntervalMs = 50 };
            _e.SetLayout(new UsLayout());
            _e.ApplySettings(new OskSettings { Suggestions = false });
        }

        private KeyboardLayout Layout => (KeyboardLayout)_e.Style;

        /// <summary>The controls the held positions drive in the layout
        /// showing, and the shared numbers the runtime gives controls on one
        /// input (InputManager.OskSources.Build).</summary>
        private void Sync()
        {
            Array.Clear(_pad.Down);
            Array.Clear(_pad.Shared);
            var actions = KeyboardMenuDefaults.Actions(Layout);
            foreach (var a in actions)
            {
                string d = KeyboardMenuDefaults.DefaultFor(Layout, a);
                if (_held.Contains(d)) _pad.Down[(int)a] = true;
                foreach (var b in actions)
                    if (b != a && KeyboardMenuDefaults.DefaultFor(Layout, b) == d)
                        _pad.Shared[(int)a] = _pad.Shared[(int)b] = Math.Min((int)a, (int)b) + 1;
            }
        }

        private void Open(OskStyle? style = null)
        {
            if (style is { } s) _e.ApplySettings(new OskSettings { Suggestions = false, Style = s });
            _e.Open(new OskControlState());
        }

        private void Tick(int ms = 16)
        {
            _now += ms;
            Sync();
            _e.Tick(_pad, _now);
        }

        private void Hold(string input, bool down)
        {
            if (down) _held.Add(input);
            else _held.Remove(input);
        }

        private void Press(string input, int holdMs = 16)
        {
            Hold(input, true);
            Tick(holdMs);
            Hold(input, false);
            Tick();
        }

        private void Stick(float x, float y, bool second = false)
        {
            if (second) { _pad.S2X = x; _pad.S2Y = y; }
            else { _pad.SX = x; _pad.SY = y; }
            Tick();
        }

        // ── Grid ────────────────────────────────────────────────

        [Fact]
        public void Grid_TypeKeyTypesTheSelectedKeyByItsScanCode()
        {
            Open();
            Press(A);
            Assert.Equal(new[] { "tap 0010 None" }, _out.Sent);
        }

        [Fact]
        public void Grid_TheMoveControlsMoveAlongTheRowAndWrapAround()
        {
            Open();
            Press(Right);
            Press(A);
            Press(Left);
            Press(Left);
            Press(Left);
            Press(A);
            // w, then back past q and Tab to the row's last key, the backslash.
            Assert.Equal(new[] { "tap 0011 None", "tap 002B None" }, _out.Sent);
        }

        [Fact]
        public void Grid_TheSteeringInputMovesTheSelectionToo()
        {
            Open();
            Stick(1, 0);
            Stick(0, 0);
            Press(A);
            Assert.Equal(new[] { "tap 0011 None" }, _out.Sent);
        }

        [Fact]
        public void Grid_UpAndDownKeepTheColumn()
        {
            Open();
            Press(Down);
            Press(A);
            Press(Down);
            Press(Up);
            Press(A);
            // q to a, down to the wide Shift under it, and up to a again.
            Assert.Equal(new[] { "tap 001E None", "tap 001E None" }, _out.Sent);
            Assert.Equal(0, _e.ShiftState);
        }

        [Fact]
        public void Grid_ControlsFollowWindowsGamepadKeyboard()
        {
            Open();
            Press(X);
            Press(Y);
            Press(Start);
            Press(LB);
            Press(RB);
            Assert.Equal(new[] { "tap 000E None", "tap 0039 None", "tap 001C None", "tap E04B None", "tap E04D None" }, _out.Sent);
            Assert.False(_e.CloseRequested);
            Press(B);
            Assert.True(_e.CloseRequested);
        }

        [Fact]
        public void Grid_ShiftLatchesForOneKey_AndASecondPressLocksIt()
        {
            Open();
            Press(LT);
            Assert.Equal(1, _e.ShiftState);
            Press(A);
            Press(A);
            Assert.Equal(new[] { "tap 0010 Shift", "tap 0010 None" }, _out.Sent);
            Press(LT);
            Press(LT);
            Assert.Equal(2, _e.ShiftState);
            Press(A);
            Press(A);
            Assert.Equal(2, _e.ShiftState);
            Assert.Equal(new[] { "tap 0010 Shift", "tap 0010 Shift" }, _out.Sent.Skip(2));
            Press(LT);
            Assert.Equal(0, _e.ShiftState);
        }

        [Fact]
        public void Grid_CtrlFromTheBoard_GoesOutWithTheNextKeyOnly()
        {
            Open();
            // From q: down three rows reaches the bottom row's Fn under Tab's
            // column, and Ctrl is right of it.
            Press(Left);
            Press(Down);
            Press(Down);
            Press(Down);
            Press(Right);
            Press(A);
            Assert.Equal(1, _e.CtrlState);
            Assert.Empty(_out.Sent);
            // Up to the z row's Shift, then right past z and x to c.
            Press(Up);
            Press(Right);
            Press(Right);
            Press(Right);
            Press(A);
            Assert.Equal(new[] { "tap 002E Ctrl" }, _out.Sent);
            Assert.Equal(0, _e.CtrlState);
        }

        [Fact]
        public void Grid_SymbolsPage_TypesUnicode()
        {
            Open();
            Press(RT);
            Assert.Equal(1, _e.Page);
            Press(A);
            // q's place on the symbols page holds the second row's first character.
            Assert.Equal(new[] { "text á" }, _out.Sent);
        }

        [Fact]
        public void ASymbolsPageOfTheMenusOwn_TakesThePlaceOfTheDefault()
        {
            // Thirteen for the number row's places, then q's place.
            _e.ApplySettings(new OskSettings { Suggestions = false, Symbols = "1234567890-=+Ω" });
            Open();
            Press(RT);
            Press(A);
            Assert.Equal(new[] { "text Ω" }, _out.Sent);
        }

        [Fact]
        public void Open_IgnoresWhatIsHeldUntilItIsLetGo()
        {
            var held = new OskControlState();
            held.Down[(int)KeyboardControlAction.TypeKey] = true;
            Hold(A, true);
            _e.Open(held);
            Tick();
            Tick(600);
            Tick(100);
            Assert.Empty(_out.Sent);
            Hold(A, false);
            Tick();
            Press(A);
            Assert.Equal(new[] { "tap 0010 None" }, _out.Sent);
        }

        [Fact]
        public void AHeldBackspace_RepeatsAtWindowsDelayAndRate()
        {
            Open();
            Hold(X, true);
            Tick(1);
            Assert.Single(_out.Sent);
            Tick(400);
            Assert.Single(_out.Sent);
            Tick(110);
            Assert.Equal(2, _out.Sent.Count);
            Tick(50);
            Assert.Equal(3, _out.Sent.Count);
            Hold(X, false);
            Tick(200);
            Assert.Equal(3, _out.Sent.Count);
            Assert.All(_out.Sent, s => Assert.Equal("tap 000E None", s));
        }

        [Fact]
        public void FunctionRow_KeepsTheSelectionOnItsKey()
        {
            Open();
            _e.ApplySettings(new OskSettings { Suggestions = false, FunctionRow = true });
            Press(A);
            Press(Up);
            Press(Up);
            Press(A);
            // q, then two rows up is the function row's F1.
            Assert.Equal(new[] { "tap 0010 None", "tap 003B None" }, _out.Sent);
        }

        [Fact]
        public void AControlOnTheMoveInput_FiresOnRelease()
        {
            // Split puts the symbols page and Move the Keyboard both on Back.
            Open(OskStyle.Split);
            Hold(Back, true);
            Tick();
            Assert.Equal(0, _e.Page);
            Hold(Back, false);
            Tick();
            Assert.Equal(1, _e.Page);
        }

        [Fact]
        public void HoldToMove_MovesTheKeyboardAndFiresNothingElse()
        {
            Open(OskStyle.Split);
            Hold(Back, true);
            Tick();
            _pad.SX = 1;
            Tick(40);
            Tick(40);
            _pad.SX = 0;
            Tick();
            Hold(Back, false);
            Tick();
            var (x, y) = _e.TakeMove();
            Assert.True(x > 0);
            Assert.Equal(0, y);
            Assert.Equal(0, _e.Page);
            Assert.Empty(_out.Sent);
        }

        // ── Its own keys: Fn and the layout key ─────────────────

        [Fact]
        public void TheFnKey_ShowsAndHidesTheFunctionRow()
        {
            Open();
            // The bottom row: Fn, Ctrl, Win, Alt, the page key, the layout key.
            Assert.Equal(OskSpecial.Fn, _e.Boards()[0].Rows[4][0].Special);
            _e.ClickKey(0, 4, 0);
            Assert.True(_e.FunctionRow);
            Assert.Equal(6, _e.Boards()[0].Rows.Length);
            // The bottom row is one row lower now.
            _e.ClickKey(0, 5, 0);
            Assert.False(_e.FunctionRow);
        }

        [Fact]
        public void TheLayoutKey_GoesGridSplitWheelAndBack()
        {
            Open();
            Assert.Equal(OskSpecial.Layout, _e.Boards()[0].Rows[4][5].Special);
            _e.ClickKey(0, 4, 5);
            Assert.Equal(OskStyle.Split, _e.Style);
            // The left half keeps the bottom row's first keys, the layout key among them.
            Assert.Equal(OskSpecial.Layout, _e.Boards()[0].Rows[4][5].Special);
            _e.ClickKey(0, 4, 5);
            Assert.Equal(OskStyle.Wheel, _e.Style);
            // A poll passes before the triggers go down. Held at the switch,
            // they would wait for their release (the test below).
            Tick();
            // The wheel's keys set, both trigger holds down: the layout key is
            // in the last group, where Enter was.
            Hold(LT, true);
            Hold(RT, true);
            Tick();
            _e.ClickWheel(7, 2);
            Assert.Equal(OskStyle.Grid, _e.Style);
        }

        [Fact]
        public void ASwitchedLayout_IgnoresWhatIsHeldUntilItIsLetGo()
        {
            Open(OskStyle.Split);
            Hold(A, true);
            Tick();
            _e.ClickKey(0, 4, 5);
            Assert.Equal(OskStyle.Wheel, _e.Style);
            // A picks a group's south character in Wheel: not while the press
            // that switched is still down. The stick goes up a poll later.
            Tick();
            _pad.SY = -1;
            Tick();
            Tick();
            Assert.Empty(_out.Sent);
            Hold(A, false);
            Tick();
            Press(A);
            Assert.Equal(new[] { "tap 0020 None" }, _out.Sent);
        }

        [Fact]
        public void ALayoutSwitch_GuardsAnInputThatIsAnotherControlInTheNewLayout()
        {
            // B picks east in the wheel and closes Grid. Held through the
            // switch, it must not close the keyboard it just switched.
            Open(OskStyle.Wheel);
            Hold(LT, true);
            Hold(RT, true);
            Hold(B, true);
            Tick();
            _e.ClickWheel(7, 2);
            Assert.Equal(OskStyle.Grid, _e.Style);
            Tick();
            Tick();
            Assert.False(_e.CloseRequested);
            Hold(LT, false);
            Hold(RT, false);
            Hold(B, false);
            Tick();
            Press(B);
            Assert.True(_e.CloseRequested);
        }

        [Fact]
        public void ForcedSettings_SetTheMenusLayoutAndFunctionRow_OverTheKeyboardsOwn()
        {
            Open();
            _e.ClickKey(0, 4, 5);
            Assert.Equal(OskStyle.Split, _e.Style);
            // The same settings again leave the keyboard's own choice alone...
            _e.ApplySettings(new OskSettings { Suggestions = false });
            Assert.Equal(OskStyle.Split, _e.Style);
            // ...and a keyboard opening for a menu takes the menu's.
            _e.ApplySettings(new OskSettings { Suggestions = false }, force: true);
            Assert.Equal(OskStyle.Grid, _e.Style);
        }

        // ── Touch ───────────────────────────────────────────────

        [Fact]
        public void OneTouchpad_PointsAtTheWholeBoard_AndItsClickTypes()
        {
            Open();
            // Five rows. A fifth of the way down is the q row, and x at 0.11 of
            // fourteen columns is q's column.
            _pad.Touch = new[] { new OskTouch(true, 0.11f, 0.3f, false) };
            _e.Tick(_pad, _now += 16);
            _pad.Touch = new[] { new OskTouch(true, 0.11f, 0.3f, true) };
            _e.Tick(_pad, _now += 16);
            _pad.Touch = new[] { new OskTouch(false, 0, 0, false) };
            _e.Tick(_pad, _now += 16);
            Assert.Equal(new[] { "tap 0010 None" }, _out.Sent);
        }

        // ── Split ───────────────────────────────────────────────

        [Fact]
        public void Split_EachTypeInputTypesItsHalf_AndBothAtOnceTypeASpace()
        {
            Open(OskStyle.Split);
            Press(LT);
            Tick(100);
            Press(RT);
            Tick(100);
            Assert.Equal(new[] { "tap 0010 None", "tap 0015 None" }, _out.Sent);
            Hold(LT, true);
            Tick(10);
            Hold(RT, true);
            Tick(10);
            Hold(LT, false);
            Hold(RT, false);
            Tick(200);
            Assert.Equal("tap 0039 None", _out.Sent.Last());
            Assert.Equal(3, _out.Sent.Count);
        }

        [Fact]
        public void Split_EachSteeringInputMovesItsOwnHalf()
        {
            Open(OskStyle.Split);
            Stick(1, 0);
            Stick(0, 0);
            Stick(1, 0, second: true);
            Stick(0, 0, second: true);
            Press(LT);
            Tick(100);
            Press(RT);
            Tick(100);
            Assert.Equal(new[] { "tap 0011 None", "tap 0016 None" }, _out.Sent);
        }

        [Fact]
        public void Split_ATapOfBack_SwitchesThePage()
        {
            Open(OskStyle.Split);
            Press(Back);
            Assert.Equal(1, _e.Page);
        }

        [Fact]
        public void Split_EitherHandsShiftLatches()
        {
            Open(OskStyle.Split);
            Press("Gamepad RightStick");
            Assert.Equal(1, _e.ShiftState);
            Press("Gamepad LeftStick");
            Assert.Equal(2, _e.ShiftState);
        }

        [Fact]
        public void Split_TheArrowControlsSendTheArrowKeys()
        {
            Open(OskStyle.Split);
            Press(Up);
            Press(Left);
            Assert.Equal(new[] { "tap E048 None", "tap E04B None" }, _out.Sent);
        }

        [Fact]
        public void Split_HalvesFollowTheHands()
        {
            var (left, right) = OskLayoutFrame.SplitRows(OskLayoutFrame.Rows(new UsLayout(), null, false), false);
            Assert.Equal(new ushort[] { 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14 }, left[1].Select(k => k.Scan));
            Assert.Equal((ushort)0x15, right[1][0].Scan);
            Assert.Equal((ushort)0x07, left[0].Last().Scan);
            Assert.Equal((ushort)0x30, left[3].Last().Scan);
            Assert.Equal(OskSpecial.Space, left[4].Last().Special);
            Assert.Equal(OskSpecial.Space, right[4][0].Special);
        }

        // ── Wheel ───────────────────────────────────────────────

        [Fact]
        public void Wheel_OnAUsLayout_HoldsDaisywheeljsSets()
        {
            var sets = OskLayoutFrame.WheelSets(new UsLayout());
            Assert.Equal("abcdefghijklmnopqrstuvwxyz?!;\\&-", string.Concat(sets[0].Cast<string>()));
            Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZ+.@#$%", string.Concat(sets[1].Cast<string>()));
            Assert.Equal("0123456789*,_=\"'()[]{}:~^<>|", string.Concat(sets[2].Cast<string>()));
            Assert.Equal(32, sets[3].Length);
        }

        [Fact]
        public void Wheel_TheSteeringInputPicksAGroup_AndAPickControlTypesItsSpot()
        {
            Open(OskStyle.Wheel);
            Stick(0, -1);
            Press(X);
            Assert.Equal(0, _e.Petal);
            // East group with capitals held: I J K L, south is L.
            Hold(RT, true);
            Stick(1, 0);
            Press(A);
            Hold(RT, false);
            Stick(0, 0);
            Press(LB);
            Press(RB);
            Assert.Equal(new[] { "tap 001E None", "tap 0026 Shift", "tap 000E None", "tap 0039 None" }, _out.Sent);
        }

        [Fact]
        public void Wheel_BothHolds_OpenTheKeysSet()
        {
            Open(OskStyle.Wheel);
            Hold(RT, true);
            Hold(LT, true);
            Stick(0, -1);
            Assert.Equal(3, _e.WheelSet);
            Press(X);
            Assert.Equal(1, _e.CtrlState);
        }

        [Fact]
        public void Wheel_ATapOfBackCloses_AndAHoldMovesWithoutClosing()
        {
            Open(OskStyle.Wheel);
            Hold(Back, true);
            Tick();
            _pad.SX = 1;
            Tick(40);
            _pad.SX = 0;
            Tick();
            Hold(Back, false);
            Tick();
            Assert.False(_e.CloseRequested);
            Assert.True(_e.TakeMove().X > 0);
            Press(Back);
            Assert.True(_e.CloseRequested);
        }

        [Fact]
        public void Wheel_HomeAndEndAreOnTheDpad()
        {
            Open(OskStyle.Wheel);
            Press(Up);
            Press(Down);
            Assert.Equal(new[] { "tap E047 None", "tap E04F None" }, _out.Sent);
        }

        // ── Suggestions ─────────────────────────────────────────

        [Fact]
        public void Suggestions_KeepOnlyWordsThatStartWithWhatWasTyped()
        {
            _e.ApplySettings(new OskSettings());
            Open();
            foreach (var c in new[] { "h", "e", "l" }) _e.TypeCharacter(c);
            var q = _e.SuggestionQuery;
            Assert.Equal("hel", q.Word);
            _e.SetSuggestions(q.Version, new[] { "yep", "hello", "gel", "help" });
            Assert.Equal(new[] { "hello", "help" }, _e.Suggestions);
            _out.Sent.Clear();
            Press(RsLeft);
            Assert.Equal(new[] { "text lo " }, _out.Sent);
            Assert.Equal("", _e.SuggestionQuery.Word);
            Assert.Equal("hello", _e.SuggestionQuery.Previous);
        }

        [Fact]
        public void EachSuggestionControl_TakesItsOwnPlace()
        {
            _e.ApplySettings(new OskSettings());
            Open();
            foreach (var c in new[] { "h", "e" }) _e.TypeCharacter(c);
            _e.SetSuggestions(_e.SuggestionQuery.Version, new[] { "hello", "help", "here" });
            _out.Sent.Clear();
            Press(RsUp);
            Assert.Equal(new[] { "text lp " }, _out.Sent);
        }

        [Fact]
        public void Suggestions_TakeTheCaseOfWhatWasTyped()
        {
            _e.ApplySettings(new OskSettings());
            Open();
            foreach (var c in new[] { "H", "e", "l" }) _e.TypeCharacter(c);
            var q = _e.SuggestionQuery;
            _e.SetSuggestions(q.Version, new[] { "hello" });
            Assert.Equal(new[] { "Hello" }, _e.Suggestions);
        }

        [Fact]
        public void Suggestions_FromAnOlderQuestionAreDropped()
        {
            _e.ApplySettings(new OskSettings());
            Open();
            _e.TypeCharacter("h");
            int old = _e.SuggestionQuery.Version;
            _e.TypeCharacter("e");
            _e.SetSuggestions(old, new[] { "house" });
            Assert.Empty(_e.Suggestions);
        }

        [Fact]
        public void TypeCharacter_UsesTheLayoutsKey_OrUnicodeWhenItHasNone()
        {
            Open();
            _e.TypeCharacter("A");
            _e.TypeCharacter("?");
            _e.TypeCharacter("é");
            Assert.Equal(new[] { "tap 001E Shift", "tap 0035 Shift", "text é" }, _out.Sent);
        }

        [Fact]
        public void CapsLockOn_FlipsShiftForALetter_SoTheCharacterStaysWhatWasPicked()
        {
            var layout = new UsLayout { CapsOn = true };
            _e.SetLayout(layout);
            Open();
            _e.TypeCharacter("a");
            Assert.Equal(new[] { "tap 001E Shift" }, _out.Sent);
        }

        // ── Layout data ─────────────────────────────────────────

        /// <summary>The US layout with some keys replaced, for the data of
        /// other layouts.</summary>
        internal sealed class Overlay : IOskLayout
        {
            private readonly IOskLayout _under = new UsLayout();
            private readonly Dictionary<ushort, OskKeyText> _keys;
            public Overlay(Dictionary<ushort, OskKeyText> keys) => _keys = keys;
            public string Name => _under.Name;
            public string LanguageTag => _under.LanguageTag;
            public bool HasAltGr => _keys.Values.Any(t => t.AltGr != null || t.ShiftAltGr != null);
            public bool CapsOn => false;
            public OskKeyText Text(ushort scan) => _keys.TryGetValue(scan, out var t) ? t : _under.Text(scan);
        }

        private static bool DrawsIsoKey(IOskLayout layout)
            => OskLayoutFrame.Rows(layout, null, functionRow: false)[3]
                .Any(k => k.Kind == OskKeyKind.Layout && k.Scan == OskLayoutFrame.IsoKey);

        [Fact]
        public void TheIsoKey_IsDrawnOnlyWhenItTypesSomethingOfItsOwn()
        {
            // Windows' US layout gives the ISO key the backslash key's \ and |.
            Assert.False(DrawsIsoKey(new Overlay(new() { [OskLayoutFrame.IsoKey] = new("\\", "|", null, null) })));
            // US-International's backslash key also has ¬ and ¦ on AltGr.
            Assert.False(DrawsIsoKey(new Overlay(new()
            {
                [0x2B] = new("\\", "|", "¬", "¦"),
                [OskLayoutFrame.IsoKey] = new("\\", "|", null, null),
            })));
            // UK: the key beside Enter types # and ~, so \ and | are the ISO key's own.
            Assert.True(DrawsIsoKey(new Overlay(new()
            {
                [0x2B] = new("#", "~", null, null),
                [OskLayoutFrame.IsoKey] = new("\\", "|", null, null),
            })));
            // An AltGr character no other key types keeps it on the board.
            Assert.True(DrawsIsoKey(new Overlay(new() { [OskLayoutFrame.IsoKey] = new("\\", "|", "¦", null) })));
        }

        /// <summary>Windows' Brazilian ABNT2 layout as ToUnicodeEx reads it:
        /// Shift+6 a dead diaeresis, the ~ key a dead tilde with a dead
        /// circumflex shifted, the ' key plain.</summary>
        private static Overlay Abnt2() => new(new()
        {
            [0x07] = new("6", "¨", "¬", null, DeadShift: true),
            [0x28] = new("~", "^", null, null, Dead: true, DeadShift: true),
            [0x29] = new("'", "\"", null, null),
        });

        [Fact]
        public void ADeadLevelsCharacter_GoesOutAsText_SoNoAccentWaitsForTheNextKey()
        {
            _e.SetLayout(Abnt2());
            Open();
            foreach (var c in new[] { "^", "~", "¨", "6", "¬", "\"" }) _e.TypeCharacter(c);
            Assert.Equal(new[] { "text ^", "text ~", "text ¨", "tap 0007 None", "tap 0007 AltGr", "tap 0029 Shift" }, _out.Sent);
        }

        [Fact]
        public void AKeysLevel_IsDeadOnlyWhereTheLayoutSaysSo()
        {
            var six = Abnt2().Text(0x07);
            Assert.False(six.DeadAt(shift: false, altGr: false));
            Assert.True(six.DeadAt(shift: true, altGr: false));
            Assert.False(six.DeadAt(shift: false, altGr: true));
            _e.SetLayout(Abnt2());
            Assert.False(_e.LevelDead(six));
            Assert.True(_e.LevelDead(Abnt2().Text(0x28)));
        }
    }
}
