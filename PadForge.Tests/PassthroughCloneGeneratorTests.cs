using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the 1:1 passthrough clone generator (issue #196). The generator turns a
    /// physical device's axes / sliders / buttons / hats into identity Extended
    /// rows (physical index i → same-indexed Extended output), sizes the Extended
    /// layout to fit, and reports anything past an Extended cap as unmapped.
    ///
    /// <para>The diff target is the reporter's hand-built PadForge.xml: a raw
    /// joystick's slot 1 mapped ExtendedAxis0←Axis 0, ExtendedAxis1←Axis 1, and
    /// ExtendedBtn0..35←Button 0..35, all sticks-only (TriggerCount 0). These tests
    /// assert the generator reproduces those rows and never emits a trigger.</para>
    /// </summary>
    public class PassthroughCloneGeneratorTests
    {
        // ── DeviceObjectItem factories (mirror SdlDeviceWrapper.GetDeviceObjects) ──

        private static DeviceObjectItem Axis(int idx) => new()
        {
            InputIndex = idx,
            ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
            ObjectTypeGuid = ObjectGuid.ZAxis,   // any non-Slider axis GUID
            Name = $"Axis {idx}",
        };

        private static DeviceObjectItem Slider(int idx) => new()
        {
            InputIndex = idx,
            ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
            ObjectTypeGuid = ObjectGuid.Slider,
            Name = $"Slider {idx}",
        };

        private static DeviceObjectItem Button(int idx) => new()
        {
            InputIndex = idx,
            ObjectType = DeviceObjectTypeFlags.PushButton,
            ObjectTypeGuid = ObjectGuid.Button,
            Name = $"Button {idx}",
        };

        private static DeviceObjectItem Pov(int idx) => new()
        {
            InputIndex = idx,
            ObjectType = DeviceObjectTypeFlags.PointOfViewController,
            ObjectTypeGuid = ObjectGuid.PovController,
            Name = idx == 0 ? "POV" : $"POV {idx}",
        };

        private static UserDevice Device(IEnumerable<DeviceObjectItem> objects) => new()
        {
            DeviceObjects = objects.ToArray(),
        };

        // Small helper so tests read as target → descriptor.
        private sealed class CloneResultRowLookup
        {
            private readonly Dictionary<string, string> _map;
            public CloneResultRowLookup(PassthroughCloneGenerator.CloneResult r)
                => _map = r.Rows.ToDictionary(x => x.Target, x => x.Descriptor);
            public string this[string target] => _map.TryGetValue(target, out var d) ? d : null;
            public bool Has(string target) => _map.ContainsKey(target);
        }

        private static CloneResultRowLookup Lookup(PassthroughCloneGenerator.CloneResult r)
            => new(r);

        // ── Identity: axes and buttons map to same-indexed Extended outputs ──

        [Fact]
        public void Axes_And_Buttons_Map_Identity()
        {
            var objs = new List<DeviceObjectItem>();
            for (int i = 0; i < 4; i++) objs.Add(Axis(i));
            for (int i = 0; i < 6; i++) objs.Add(Button(i));

            var r = PassthroughCloneGenerator.Generate(Device(objs));
            var rows = Lookup(r);

            for (int i = 0; i < 4; i++)
                Assert.Equal($"Axis {i}", rows[$"ExtendedAxis{i}"]);
            for (int i = 0; i < 6; i++)
                Assert.Equal($"Button {i}", rows[$"ExtendedBtn{i}"]);

            // 4 axes → 2 sticks, no triggers; 6 buttons; 0 POVs.
            Assert.Equal(2, r.Sticks);
            Assert.Equal(0, r.Triggers);
            Assert.Equal(6, r.Buttons);
            Assert.Equal(0, r.Povs);
            Assert.False(r.HasOverflow);
        }

        // ── The reporter's exact diff target (FFBeast raw joystick) ──

        [Fact]
        public void Reporter_Joystick_Reproduces_HandBuilt_Rows()
        {
            // FFBeast(Joystick): 8 axes, 64 buttons, 0 hats (as PadForge enumerated it).
            var objs = new List<DeviceObjectItem>();
            for (int i = 0; i < 8; i++) objs.Add(Axis(i));
            for (int i = 0; i < 64; i++) objs.Add(Button(i));

            var r = PassthroughCloneGenerator.Generate(Device(objs));
            var rows = Lookup(r);

            // The rows the reporter hand-authored must be present and identical.
            Assert.Equal("Axis 0", rows["ExtendedAxis0"]);
            Assert.Equal("Axis 1", rows["ExtendedAxis1"]);
            for (int i = 0; i <= 35; i++)
                Assert.Equal($"Button {i}", rows[$"ExtendedBtn{i}"]);

            // Full clone: sticks-only layout, all 8 axes and all 64 buttons.
            Assert.Equal(4, r.Sticks);       // ceil(8/2)
            Assert.Equal(0, r.Triggers);     // never a trigger for passthrough
            Assert.Equal(64, r.Buttons);
            Assert.Equal(0, r.Povs);
            Assert.False(r.HasOverflow);
            // No trigger targets are ever emitted.
            Assert.All(r.Rows, row => Assert.DoesNotContain("Trigger", row.Target));
        }

        // ── Odd axis count rounds sticks up ──

        [Fact]
        public void Odd_Axis_Count_Rounds_Sticks_Up()
        {
            var r = PassthroughCloneGenerator.Generate(Device(new[] { Axis(0), Axis(1), Axis(2) }));
            Assert.Equal(2, r.Sticks);       // ceil(3/2) = 2 → 4 axis slots, 3 filled
            Assert.Equal(0, r.Triggers);
            Assert.Equal(3, r.AxesMapped);
            Assert.Equal(4, r.LayoutAxes);   // what the confirm dialog reports
            var rows = Lookup(r);
            Assert.Equal("Axis 0", rows["ExtendedAxis0"]);
            Assert.Equal("Axis 1", rows["ExtendedAxis1"]);
            Assert.Equal("Axis 2", rows["ExtendedAxis2"]);
            Assert.False(rows.Has("ExtendedAxis3"));   // 4th slot exists but unmapped
        }

        // ── POV fans out to four directions, source hat index preserved ──

        [Fact]
        public void Pov_Fans_Out_Four_Directions_With_Source_Index()
        {
            // Two axes then a hat at InputIndex 2.
            var r = PassthroughCloneGenerator.Generate(Device(new[] { Axis(0), Axis(1), Pov(2) }));
            var rows = Lookup(r);

            Assert.Equal(1, r.Povs);
            Assert.Equal("POV 2 Up", rows["ExtendedPov0Up"]);
            Assert.Equal("POV 2 Down", rows["ExtendedPov0Down"]);
            Assert.Equal("POV 2 Left", rows["ExtendedPov0Left"]);
            Assert.Equal("POV 2 Right", rows["ExtendedPov0Right"]);
        }

        // ── Sliders append after axes, both feed ExtendedAxis ──

        [Fact]
        public void Sliders_Append_After_Axes()
        {
            // 2 axes + 1 slider → axis descriptors [Axis 0, Axis 1, Slider 0].
            var r = PassthroughCloneGenerator.Generate(Device(new[] { Axis(0), Axis(1), Slider(0) }));
            var rows = Lookup(r);

            Assert.Equal(3, r.AxesMapped);
            Assert.Equal(2, r.Sticks);
            Assert.Equal("Axis 0", rows["ExtendedAxis0"]);
            Assert.Equal("Axis 1", rows["ExtendedAxis1"]);
            Assert.Equal("Slider 0", rows["ExtendedAxis2"]);
        }

        // ── Overflow past Extended caps is reported, not dropped silently ──

        [Fact]
        public void Overflow_Past_Caps_Is_Reported()
        {
            var objs = new List<DeviceObjectItem>();
            for (int i = 0; i < 10; i++) objs.Add(Axis(i));    // > 8
            for (int i = 0; i < 130; i++) objs.Add(Button(i));  // > 128
            for (int i = 0; i < 5; i++) objs.Add(Pov(i));       // > 4

            var r = PassthroughCloneGenerator.Generate(Device(objs));

            Assert.Equal(10, r.AxesAvailable);
            Assert.Equal(8, r.AxesMapped);
            Assert.Equal(4, r.Sticks);
            Assert.Equal(130, r.ButtonsAvailable);
            Assert.Equal(128, r.ButtonsMapped);
            Assert.Equal(128, r.Buttons);
            Assert.Equal(5, r.PovsAvailable);
            Assert.Equal(4, r.PovsMapped);
            Assert.True(r.HasOverflow);

            var rows = Lookup(r);
            Assert.True(rows.Has("ExtendedAxis7"));
            Assert.False(rows.Has("ExtendedAxis8"));
            Assert.True(rows.Has("ExtendedBtn127"));
            Assert.False(rows.Has("ExtendedBtn128"));
            Assert.True(rows.Has("ExtendedPov3Up"));
            Assert.False(rows.Has("ExtendedPov4Up"));
        }

        // ── Offline device (no DeviceObjects) falls back to capability counts ──

        [Fact]
        public void Offline_Device_Uses_Capability_Counts()
        {
            var ud = new UserDevice
            {
                DeviceObjects = null,
                CapAxeCount = 2,
                CapButtonCount = 6,
                CapPovCount = 0,
            };

            var r = PassthroughCloneGenerator.Generate(ud);
            var rows = Lookup(r);

            Assert.Equal(2, r.AxesMapped);
            Assert.Equal(1, r.Sticks);
            Assert.Equal(6, r.Buttons);
            Assert.Equal("Axis 0", rows["ExtendedAxis0"]);
            Assert.Equal("Axis 1", rows["ExtendedAxis1"]);
            Assert.Equal("Button 5", rows["ExtendedBtn5"]);
        }

        // ── Empty and null devices are safe ──

        [Fact]
        public void Empty_Device_Yields_No_Rows()
        {
            var r = PassthroughCloneGenerator.Generate(Device(System.Array.Empty<DeviceObjectItem>()));
            Assert.Empty(r.Rows);
            Assert.Equal(0, r.Sticks);
            Assert.Equal(0, r.Buttons);
            Assert.Equal(0, r.Povs);
            Assert.False(r.HasOverflow);
        }

        [Fact]
        public void Null_Device_Yields_Empty_Result()
        {
            var r = PassthroughCloneGenerator.Generate(null);
            Assert.NotNull(r);
            Assert.Empty(r.Rows);
        }

        // ── Non-contiguous enumerated indices compact positionally but keep
        // their original index in the DESCRIPTOR ──
        // SDL gamepads can skip phantom positions (GetDeviceObjects gates on
        // SDL_GamepadHasAxis/Button), so enumeration may yield indices 0, 3, 4.
        // The k-th enumerated input lands on ExtendedAxis{k} (layout slots
        // can't have holes) while the descriptor keeps the device's own index,
        // so the mapping still reads the right physical input. Raw joysticks
        // (the #196 population) enumerate densely, where this is plain identity.

        [Fact]
        public void Sparse_Axis_Indices_Compact_But_Keep_Descriptor()
        {
            var r = PassthroughCloneGenerator.Generate(
                Device(new[] { Axis(0), Axis(3), Axis(4) }));
            var rows = Lookup(r);

            Assert.Equal(3, r.AxesMapped);
            Assert.Equal(2, r.Sticks);
            Assert.Equal(4, r.LayoutAxes);
            Assert.Equal("Axis 0", rows["ExtendedAxis0"]);
            Assert.Equal("Axis 3", rows["ExtendedAxis1"]);
            Assert.Equal("Axis 4", rows["ExtendedAxis2"]);
            Assert.False(rows.Has("ExtendedAxis3"));
        }

        // ── Interleaved DeviceObjects: axes still precede sliders ──
        // Enumeration is two-pass (all non-slider axes, then all sliders),
        // matching the picker's class ordering even when the device's object
        // array physically interleaves them.

        [Fact]
        public void Interleaved_Slider_Still_Appends_After_Axes()
        {
            var r = PassthroughCloneGenerator.Generate(
                Device(new DeviceObjectItem[] { Axis(0), Slider(0), Axis(1) }));
            var rows = Lookup(r);

            Assert.Equal("Axis 0", rows["ExtendedAxis0"]);
            Assert.Equal("Axis 1", rows["ExtendedAxis1"]);
            Assert.Equal("Slider 0", rows["ExtendedAxis2"]);
        }
    }
}
