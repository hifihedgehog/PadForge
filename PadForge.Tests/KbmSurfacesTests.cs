using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PadForge.Views;
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
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        private static string Src(string rel)
            => File.ReadAllText(Path.Combine(RepoRoot(), rel));

        /// <summary>WPF elements demand STA, so each body runs on its own STA
        /// thread and rethrows what it caught. Same helper shape as
        /// TreeWalkTests.</summary>
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(15000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        private static (Viewbox kb, Viewbox mouse, RowDefinition kbRow, RowDefinition mouseRow)
            Parts(KBMPreviewView v)
            => ((Viewbox)v.FindName("KeyboardHost"),
                (Viewbox)v.FindName("MouseHost"),
                (RowDefinition)v.FindName("KeyboardRow"),
                (RowDefinition)v.FindName("MouseRow"));

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

        // ── The mode survives the binding, which is what forgot it ───────

        /// <summary>THE BUG THE OWNER HIT: the slot did not remember its
        /// preset. A TwoWay SelectedValue binding pushes NULL back into its
        /// source when the Selector resolves before its ItemsSource is
        /// populated, which happens on every load of a slot that already had
        /// a mode. The setter took that null, KbmSlotConfig coerced it to
        /// Both, and the autosave wrote Both over the user's choice.
        ///
        /// <para>Same shape as the negative index the Remote Link identity
        /// picker pushes back on a culture change.</para></summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void KbmSurfaces_IgnoresTheSelectorsEmptyWriteBack(string writeBack)
        {
            var vm = new PadViewModel(0) { OutputType = VirtualControllerType.KeyboardMouse };
            vm.KbmSurfaces = "MouseOnly";
            Assert.Equal("MouseOnly", vm.KbmSurfaces);

            // What the ComboBox does before its items exist.
            vm.KbmSurfaces = writeBack;

            Assert.Equal("MouseOnly", vm.KbmSurfaces);
            Assert.Equal("MouseOnly", vm.KbmConfig.Surfaces);
        }

        /// <summary>Same-window positive control: a real pick still lands, so
        /// the guard above rejects only the empty write-back.</summary>
        [Fact]
        public void KbmSurfaces_StillAcceptsARealPick()
        {
            var vm = new PadViewModel(0) { OutputType = VirtualControllerType.KeyboardMouse };
            vm.KbmSurfaces = "MouseOnly";
            Assert.Equal("MouseOnly", vm.KbmConfig.Surfaces);
            vm.KbmSurfaces = "KeyboardOnly";
            Assert.Equal("KeyboardOnly", vm.KbmConfig.Surfaces);
            vm.KbmSurfaces = "Both";
            Assert.Equal("Both", vm.KbmConfig.Surfaces);
        }

        /// <summary>The mode round-trips through the persisted snapshot, which
        /// is the shape AppSettings and every profile store.</summary>
        [Fact]
        public void Surfaces_RoundTripsThroughTheXmlSnapshot()
        {
            var data = new KbmSlotConfigData { SlotIndex = 3, Surfaces = "MouseOnly" };
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(KbmSlotConfigData));
            string xml;
            using (var sw = new StringWriter()) { ser.Serialize(sw, data); xml = sw.ToString(); }

            Assert.Contains("Surfaces=\"MouseOnly\"", xml);

            using var sr = new StringReader(xml);
            var back = (KbmSlotConfigData)ser.Deserialize(sr);
            Assert.Equal("MouseOnly", back.Surfaces);
            Assert.Equal(3, back.SlotIndex);
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

        // ── The preview shows only the halves the slot drives ────────────

        /// <summary>A slot set to Mouse Only sends no keystrokes, so drawing a
        /// keyboard for it misrepresents what the pad does. The row height
        /// goes with the visibility: collapsing the Viewbox alone would leave
        /// its star row holding the space and the mouse would sit in the
        /// bottom two fifths under an empty gap.
        ///
        /// <para>Drives the element half directly. Bind builds both canvases,
        /// which needs application resources a bare test host does not
        /// load.</para></summary>
        [Fact]
        public void Preview_ShowsOnlyTheHalvesTheSlotDrives()
        {
            RunSta(() =>
            {
                var view = new KBMPreviewView();
                var (kb, mouse, kbRow, mouseRow) = Parts(view);

                view.ApplySurfaceVisibility(keyboard: true, mouse: true);
                Assert.Equal(Visibility.Visible, kb.Visibility);
                Assert.Equal(Visibility.Visible, mouse.Visibility);
                Assert.Equal(3, kbRow.Height.Value);
                Assert.Equal(2, mouseRow.Height.Value);

                view.ApplySurfaceVisibility(keyboard: false, mouse: true);
                Assert.Equal(Visibility.Collapsed, kb.Visibility);
                Assert.Equal(Visibility.Visible, mouse.Visibility);
                Assert.Equal(0, kbRow.Height.Value);
                Assert.True(mouseRow.Height.IsStar, "the surviving half must take the space");

                view.ApplySurfaceVisibility(keyboard: true, mouse: false);
                Assert.Equal(Visibility.Visible, kb.Visibility);
                Assert.Equal(Visibility.Collapsed, mouse.Visibility);
                Assert.True(kbRow.Height.IsStar, "the surviving half must take the space");
                Assert.Equal(0, mouseRow.Height.Value);

                // Same-window positive control: back to both restores the
                // original 3:2 split rather than leaving one half stretched.
                view.ApplySurfaceVisibility(keyboard: true, mouse: true);
                Assert.Equal(Visibility.Visible, kb.Visibility);
                Assert.Equal(Visibility.Visible, mouse.Visibility);
                Assert.Equal(3, kbRow.Height.Value);
                Assert.Equal(2, mouseRow.Height.Value);
            });
        }

        /// <summary>The preview listens for the mode, so changing it repaints
        /// without waiting for a slot-type change, and the first paint after a
        /// layout rebuild is gated too.</summary>
        [Fact]
        public void Preview_ListensForTheSurfaceMode()
        {
            string src = Src(Path.Combine("PadForge.App", "Views", "KBMPreviewView.xaml.cs"));
            Assert.Contains(
                "if (e.PropertyName == nameof(PadViewModel.KbmSurfaces))",
                src);
            // And it marks the surface dirty, the way both sibling branches
            // do. A half coming back into view has widgets painted from a
            // stale snapshot behind it.
            Assert.Contains(
                "ApplySurfaceVisibility(); _paintedValid = false; _dirty = true;",
                src);
            int build = src.IndexOf("BuildMouseCanvas();", StringComparison.Ordinal);
            int gate = src.IndexOf("ApplySurfaceVisibility();", build, StringComparison.Ordinal);
            Assert.True(build > 0 && gate > build,
                "RebuildLayout must apply the surface gate after building the canvases");
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
