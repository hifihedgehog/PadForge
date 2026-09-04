using System;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #408, @Xaklse on Discord: the Keyboard + Mouse table put roughly 110
    /// keyboard rows in front of 9 mouse rows, so anyone binding a stick to
    /// mouse look scrolled the whole keyboard to reach it. Mouse rows come
    /// first now, the slot carries a surface mode, and the table carries a
    /// keyboard or mouse scope.
    /// </summary>
    public class KbmSurfacesTests
    {
        private static MappingItem Row(string settingName)
            => new MappingItem(settingName, settingName, MappingCategory.Buttons);

        // ── The classifier ───────────────────────────────────────────────

        /// <summary>One classifier serves the surface mode and the table
        /// scope, reading the same descriptor vocabulary the engine
        /// dispatches on. Two copies of a rule drift, which is what happened
        /// to the POV sector test.</summary>
        [Theory]
        [InlineData("KbmKey41", KbmSurfaceKind.Keyboard)]   // A
        [InlineData("KbmKey20", KbmSurfaceKind.Keyboard)]   // Space
        [InlineData("KbmKey6F", KbmSurfaceKind.Keyboard)]   // Num /
        [InlineData("KbmMBtn0", KbmSurfaceKind.Mouse)]
        [InlineData("KbmMBtn4", KbmSurfaceKind.Mouse)]
        [InlineData("KbmMouseX", KbmSurfaceKind.Mouse)]
        [InlineData("KbmMouseY", KbmSurfaceKind.Mouse)]
        [InlineData("KbmScroll", KbmSurfaceKind.Mouse)]
        [InlineData("KbmScrollH", KbmSurfaceKind.Mouse)]
        public void KbmSurfaceOf_ClassifiesEveryKbmDescriptor(string setting, KbmSurfaceKind expected)
            => Assert.Equal(expected, PadViewModel.KbmSurfaceOf(setting));

        /// <summary>A row that is not Keyboard + Mouse at all classifies as
        /// neither, so the scope leaves every other slot type alone.</summary>
        [Theory]
        [InlineData("ButtonA")]
        [InlineData("LeftStickX")]
        [InlineData("MidiCC7")]
        [InlineData("RawAxis3")]
        [InlineData("")]
        [InlineData(null)]
        public void KbmSurfaceOf_IsNullForNonKbmRows(string setting)
            => Assert.Null(PadViewModel.KbmSurfaceOf(setting));

        // ── Row order and coverage, against the real built table ─────────

        /// <summary>Every mouse row precedes every keyboard row, which is the
        /// whole request. Built from the real slot, not from a fixture.</summary>
        [Fact]
        public void KeyboardMouseTable_PutsEveryMouseRowBeforeEveryKeyboardRow()
        {
            var vm = new PadViewModel(0) { OutputType = VirtualControllerType.KeyboardMouse };

            int lastMouse = -1, firstKeyboard = int.MaxValue;
            for (int i = 0; i < vm.Mappings.Count; i++)
            {
                var kind = PadViewModel.KbmSurfaceOf(vm.Mappings[i].TargetSettingName);
                if (kind == KbmSurfaceKind.Mouse) lastMouse = Math.Max(lastMouse, i);
                if (kind == KbmSurfaceKind.Keyboard) firstKeyboard = Math.Min(firstKeyboard, i);
            }

            Assert.True(lastMouse >= 0, "the table has no mouse rows at all");
            Assert.True(firstKeyboard != int.MaxValue, "the table has no keyboard rows at all");
            Assert.True(lastMouse < firstKeyboard,
                $"last mouse row at {lastMouse} is not before the first keyboard row at {firstKeyboard}");
        }

        /// <summary>Every row the slot builds lands in one bucket or the
        /// other. A row in neither would be invisible to the mode and to the
        /// scope, which is how a filter silently eats a binding.</summary>
        [Fact]
        public void KeyboardMouseTable_LeavesNoRowUnclassified()
        {
            var vm = new PadViewModel(0) { OutputType = VirtualControllerType.KeyboardMouse };

            var orphans = vm.Mappings
                .Where(m => PadViewModel.KbmSurfaceOf(m.TargetSettingName) == null)
                .Select(m => m.TargetSettingName)
                .ToList();

            Assert.True(orphans.Count == 0,
                "rows in neither bucket: " + string.Join(", ", orphans));

            // Same-window positive control, and the exact shape of the
            // complaint: 100 keyboard rows (26 letters, 10 digits, 12
            // function keys, 6 modifiers, 10 special, 10 navigation, 11
            // punctuation, 15 numpad) in front of 9 mouse rows.
            int mouse = vm.Mappings.Count(m =>
                PadViewModel.KbmSurfaceOf(m.TargetSettingName) == KbmSurfaceKind.Mouse);
            int keys = vm.Mappings.Count(m =>
                PadViewModel.KbmSurfaceOf(m.TargetSettingName) == KbmSurfaceKind.Keyboard);
            Assert.Equal(9, mouse);
            Assert.Equal(100, keys);
        }

        // ── The surface mode ─────────────────────────────────────────────

        [Fact]
        public void Surfaces_DefaultsToBothAndGatesEachHalf()
        {
            var cfg = new KbmSlotConfig();
            Assert.Equal("Both", cfg.Surfaces);
            Assert.True(cfg.KeyboardEnabled);
            Assert.True(cfg.MouseEnabled);
            Assert.True(cfg.Allows(KbmSurfaceKind.Keyboard));
            Assert.True(cfg.Allows(KbmSurfaceKind.Mouse));

            cfg.Surfaces = "MouseOnly";
            Assert.False(cfg.KeyboardEnabled);
            Assert.True(cfg.MouseEnabled);

            cfg.Surfaces = "KeyboardOnly";
            Assert.True(cfg.KeyboardEnabled);
            Assert.False(cfg.MouseEnabled);
        }

        /// <summary>An unknown or absent value reads as Both rather than
        /// disabling a half nobody asked to disable.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Nonsense")]
        public void Surfaces_FallsBackToBoth(string value)
        {
            var cfg = new KbmSlotConfig { Surfaces = value };
            Assert.Equal("Both", cfg.Surfaces);
        }

        /// <summary>The persisted snapshot defaults to Both, so a file
        /// written before #408 has no attribute and behaves exactly as it
        /// always did. That is the trap a plain bool walked into on the
        /// Chroma toggle, where every pre-existing profile read as false.
        /// </summary>
        [Fact]
        public void KbmSlotConfigData_DefaultsToBothForFilesWrittenBefore408()
        {
            Assert.Equal("Both", new KbmSlotConfigData().Surfaces);
            Assert.Equal("Both", KbmSlotConfig.DefaultSurfaces);
        }

        /// <summary>Reset puts the mode back with the rest of the card.</summary>
        [Fact]
        public void ResetToDefaults_RestoresBoth()
        {
            var cfg = new KbmSlotConfig { Surfaces = "MouseOnly" };
            cfg.ResetToDefaults();
            Assert.Equal("Both", cfg.Surfaces);
        }

        // ── The dispatch gate ────────────────────────────────────────────

        /// <summary>A half the slot does not drive sends nothing. Gated on
        /// the whole raw state rather than inside the mapper's loops,
        /// because there are a dozen mouse lanes and threading a flag
        /// through each would miss the next one added.</summary>
        [Fact]
        public void WithSurfaces_DropsTheHalfTheSlotDoesNotDrive()
        {
            var full = new KbmRawState();
            full.SetKey(0x41, true);              // A
            full.SetMouseButton(0, true);         // LMB
            full.MouseDeltaX = 40;
            full.MouseDeltaY = -25;
            full.ScrollDelta = 3;
            full.ScrollDeltaH = -2;
            full.MouseFlickX = 700;
            full.MouseGyroX = 1.5f;
            full.MouseTouchY = 2.5f;
            full.MouseStickCoastX = 3.5f;
            full.MouseAbsValid = true;

            var mouseOnly = full.WithSurfaces(keyboardEnabled: false, mouseEnabled: true);
            Assert.False(mouseOnly.GetKey(0x41));
            Assert.True(mouseOnly.GetMouseButton(0));
            Assert.Equal(40, mouseOnly.MouseDeltaX);
            Assert.Equal(700, mouseOnly.MouseFlickX);

            var keyboardOnly = full.WithSurfaces(keyboardEnabled: true, mouseEnabled: false);
            Assert.True(keyboardOnly.GetKey(0x41));
            Assert.False(keyboardOnly.GetMouseButton(0));
            Assert.Equal(0, keyboardOnly.MouseDeltaX);
            Assert.Equal(0, keyboardOnly.MouseDeltaY);
            Assert.Equal(0, keyboardOnly.ScrollDelta);
            Assert.Equal(0, keyboardOnly.ScrollDeltaH);
            Assert.Equal(0, keyboardOnly.MouseFlickX);
            Assert.Equal(0f, keyboardOnly.MouseGyroX);
            Assert.Equal(0f, keyboardOnly.MouseTouchY);
            Assert.Equal(0f, keyboardOnly.MouseStickCoastX);
            Assert.False(keyboardOnly.MouseAbsValid);

            // Same-window positive control: Both changes nothing.
            var both = full.WithSurfaces(true, true);
            Assert.True(both.GetKey(0x41));
            Assert.Equal(40, both.MouseDeltaX);
            Assert.Equal(700, both.MouseFlickX);
        }

        /// <summary>Clear stays the union of the two halves, so a field added
        /// to either one keeps being covered by the surface gate.</summary>
        [Fact]
        public void ClearIsTheUnionOfBothHalves()
        {
            var s = new KbmRawState();
            s.SetKey(0x41, true);
            s.MouseDeltaX = 9;
            s.MouseGyroY = 4f;
            s.Clear();
            Assert.False(s.GetKey(0x41));
            Assert.Equal(0, s.MouseDeltaX);
            Assert.Equal(0f, s.MouseGyroY);
        }

        // ── The table scope ──────────────────────────────────────────────

        /// <summary>The scope narrows rows, and the slot's mode narrows them
        /// first. A non-KBM row is never touched by either.</summary>
        [Fact]
        public void RowMatchesSurface_AppliesTheModeThenTheScope()
        {
            var both = new KbmSlotConfig();
            var mouseOnly = new KbmSlotConfig { Surfaces = "MouseOnly" };

            var key = Row("KbmKey41");
            var mouse = Row("KbmMouseX");
            var gamepad = Row("ButtonA");

            // Mode Both, scope All: everything shows.
            Assert.True(PadViewModel.RowMatchesSurface(key, "All", both));
            Assert.True(PadViewModel.RowMatchesSurface(mouse, "All", both));

            // Scope alone.
            Assert.False(PadViewModel.RowMatchesSurface(key, "Mouse", both));
            Assert.True(PadViewModel.RowMatchesSurface(mouse, "Mouse", both));
            Assert.True(PadViewModel.RowMatchesSurface(key, "Keyboard", both));
            Assert.False(PadViewModel.RowMatchesSurface(mouse, "Keyboard", both));

            // Mode alone: a half the slot does not drive shows no rows even
            // with the scope wide open.
            Assert.False(PadViewModel.RowMatchesSurface(key, "All", mouseOnly));
            Assert.True(PadViewModel.RowMatchesSurface(mouse, "All", mouseOnly));

            // The mode wins over a scope that contradicts it.
            Assert.False(PadViewModel.RowMatchesSurface(key, "Keyboard", mouseOnly));

            // A row from any other slot type is unaffected by both.
            Assert.True(PadViewModel.RowMatchesSurface(gamepad, "Mouse", mouseOnly));
            Assert.True(PadViewModel.RowMatchesSurface(gamepad, "Keyboard", mouseOnly));

            // A null config is "no opinion", which is what every non-KBM slot
            // passes, so only the scope applies.
            Assert.True(PadViewModel.RowMatchesSurface(key, "All", null));
        }

        /// <summary>The scope is a view control, so it is session-only and
        /// falls back to All rather than persisting a narrow view a user
        /// cannot see the cause of.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Nonsense")]
        public void MappingSurfaceScope_FallsBackToAll(string value)
        {
            var vm = new PadViewModel(0) { MappingSurfaceScope = value };
            Assert.Equal("All", vm.MappingSurfaceScope);
        }

        /// <summary>The scope and the search AND together, so typing while a
        /// scope is set narrows within it instead of replacing it.</summary>
        [Fact]
        public void ScopeAndSearchNarrowTogether()
        {
            var both = new KbmSlotConfig();
            var mouseX = Row("KbmMouseX");

            Assert.True(PadViewModel.RowMatchesSearch(mouseX, "KbmMouse")
                        && PadViewModel.RowMatchesSurface(mouseX, "Mouse", both));

            // The same row fails once either half rejects it.
            Assert.False(PadViewModel.RowMatchesSearch(mouseX, "zzz")
                         && PadViewModel.RowMatchesSurface(mouseX, "Mouse", both));
            Assert.False(PadViewModel.RowMatchesSearch(mouseX, "KbmMouse")
                         && PadViewModel.RowMatchesSurface(mouseX, "Keyboard", both));
        }
    }
}
