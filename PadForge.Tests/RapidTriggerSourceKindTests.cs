using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.AnalogKeyboard;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #482: Rapid Trigger as a Primary Mode. Past the row's deadzone the
    /// input presses. A rise of more than the distance from its deepest point
    /// releases, and a push of more than the distance from its shallowest
    /// point presses again. Back short of the deadzone it leaves the zone.
    /// libhmk's matrix_scan, non-continuous.
    /// </summary>
    public class RapidTriggerSourceKindTests
    {
        private const int Actuation = 30;

        private static MappingSource Rapid(string descriptor = "Axis 2", int deadZone = Actuation, int distance = 10)
            => new() { Kind = "RapidTrigger", Descriptor = descriptor, DeadZone = deadZone, ParamRapidTriggerDistance = distance };

        private static CustomInputState Axis(int index, double depth)
        {
            var s = new CustomInputState();
            s.Axis[index] = (int)Math.Round(depth * 65535);
            return s;
        }

        private static bool Press(SourceKindRuntime rt, MappingSource src, double depth,
            string target = "ButtonA", int slot = 0)
            => SourceEvaluator.EvaluateForButtonTarget(Axis(2, depth), src, 50, slot, target, 0, rt, 0.001);

        [Fact]
        public void ARiseReleasesAndAPushPressesAgainInsideTheZone()
        {
            var rt = new SourceKindRuntime();
            var src = Rapid();
            (double depth, bool pressed)[] frames =
            {
                (0.00, false),
                (0.20, false), // short of the 30% deadzone
                (0.35, true),  // past it: pressed
                (0.60, true),  // deeper
                (0.55, true),  // a 5% rise is inside the 10% distance
                (0.45, false), // 15% up from 60%: released
                (0.40, false), // shallower while released
                (0.48, false), // 8% down from 40%
                (0.52, true),  // 12% down: pressed again
                (0.20, false), // back short of the deadzone: out of the zone
                (0.32, true),  // past it again: pressed at once
            };
            foreach (var (depth, pressed) in frames)
            {
                rt.FrameSeq++;
                Assert.Equal(pressed, Press(rt, src, depth));
            }
        }

        [Fact]
        public void TravelOfExactlyTheDistanceChangesNothing()
        {
            // libhmk compares "distance + rt_up < extremum" and
            // "extremum + rt_down < distance". Binary fractions keep the sums
            // exact.
            var rt = new SourceKindRuntime();
            bool Tick(double depth)
            {
                rt.FrameSeq++;
                return rt.TickRapidTrigger(0, "ButtonA", 0, true, depth, 0.125);
            }
            Assert.True(Tick(0.5));
            Assert.True(Tick(0.375));       // exactly 0.125 up
            Assert.False(Tick(0.359375));   // past it: released at 0.359375
            Assert.False(Tick(0.484375));   // exactly 0.125 down
            Assert.True(Tick(0.5));         // past it: pressed
        }

        [Fact]
        public void TheFirstPressLandsWhereADirectRowPresses()
        {
            // An analog key presses at or past its deadzone and an axis
            // strictly past it. Rapid Trigger asks the Direct read for the
            // zone, so both agree with Direct at the boundary.
            string w = SourceCoercion.AnalogKeyDescriptor(AnalogKeyCodes.W);
            foreach (float depth in new[] { 0f, 0.29f, 0.30f, 0.31f })
            {
                var state = new CustomInputState { AnalogKeys = new AnalogKeyInputState() };
                state.AnalogKeys.Set(AnalogKeyCodes.W, depth);
                var direct = new MappingSource { Descriptor = w, DeadZone = Actuation };
                var rt = new SourceKindRuntime();
                rt.FrameSeq++;
                Assert.Equal(
                    SourceEvaluator.EvaluateForButtonTarget(state, direct, 50, 0, "ButtonA", 0, null, 0),
                    SourceEvaluator.EvaluateForButtonTarget(state, Rapid(w), 50, 0, "ButtonA", 0, rt, 0.001));
            }

            int threshold = (int)(Actuation / 100.0 * 65535);
            foreach (int raw in new[] { threshold - 1, threshold, threshold + 1 })
            {
                var state = new CustomInputState();
                state.Axis[2] = raw;
                var direct = new MappingSource { Descriptor = "Axis 2", DeadZone = Actuation };
                var rt = new SourceKindRuntime();
                rt.FrameSeq++;
                Assert.Equal(
                    SourceEvaluator.EvaluateForButtonTarget(state, direct, 50, 0, "ButtonA", 0, null, 0),
                    SourceEvaluator.EvaluateForButtonTarget(state, Rapid(), 50, 0, "ButtonA", 0, rt, 0.001));
            }
        }

        [Fact]
        public void TwoDeviceReadsStepTheFrameOnceInEitherOrder()
        {
            // An any-device source is read once per device. The frame steps
            // from where the last frame ended on its deepest read, so the
            // order the devices come in cannot change the outcome.
            var a = new SourceKindRuntime();
            var b = new SourceKindRuntime();
            bool Read(SourceKindRuntime rt, bool past, double depth)
                => rt.TickRapidTrigger(0, "ButtonA", 0, past, depth, 0.1);

            a.FrameSeq++; b.FrameSeq++;
            Assert.False(Read(a, false, 0.1));
            Assert.True(Read(a, true, 0.6));
            Assert.True(Read(b, true, 0.6));
            Assert.True(Read(b, false, 0.1));   // a shallower read changes nothing

            // The deep device rises 15% while the other stays short.
            a.FrameSeq++; b.FrameSeq++;
            Assert.False(Read(a, true, 0.45));
            Assert.False(Read(a, false, 0.2));
            Assert.False(Read(b, false, 0.2));
            Assert.False(Read(b, true, 0.45));

            // Both kept 45% as the shallow point: an 11% push presses.
            a.FrameSeq++; b.FrameSeq++;
            Assert.True(Read(a, true, 0.56));
            Assert.True(Read(b, true, 0.56));
        }

        [Fact]
        public void AFrameWithoutAReadPutsTheInputBackOutsideTheZone()
        {
            var rt = new SourceKindRuntime();
            var src = Rapid();
            rt.FrameSeq++;
            Assert.True(Press(rt, src, 0.6));
            rt.FrameSeq++;
            Assert.False(Press(rt, src, 0.45));   // released, shallow point 45%
            rt.FrameSeq++;
            Assert.False(Press(rt, src, 0.5));    // 5% down: still released

            // The row goes unread for a frame (its shift layer closed). The
            // next read starts outside the zone, so an input past the
            // deadzone presses at once, as a Direct row would.
            rt.FrameSeq += 2;
            Assert.True(Press(rt, src, 0.5));
        }

        [Fact]
        public void ResetsDropOnlyTheirOwnState()
        {
            var rt = new SourceKindRuntime();
            var src = Rapid();

            // Released inside the zone on two rows. A 5% push stays released
            // unless the state was dropped, which starts the row outside the
            // zone and presses at once.
            void ReleaseBoth()
            {
                rt.Clear();
                rt.FrameSeq++;
                Press(rt, src, 0.6, "ButtonA");
                Press(rt, src, 0.6, "ButtonB");
                rt.FrameSeq++;
                Assert.False(Press(rt, src, 0.45, "ButtonA"));
                Assert.False(Press(rt, src, 0.45, "ButtonB"));
            }

            ReleaseBoth();
            rt.ResetForSlot(1);
            rt.FrameSeq++;
            Assert.False(Press(rt, src, 0.5, "ButtonA"));

            ReleaseBoth();
            rt.ResetForSlot(0);
            rt.FrameSeq++;
            Assert.True(Press(rt, src, 0.5, "ButtonA"));
            Assert.True(Press(rt, src, 0.5, "ButtonB"));

            ReleaseBoth();
            rt.ResetForRow(0, "ButtonA");
            rt.FrameSeq++;
            Assert.True(Press(rt, src, 0.5, "ButtonA"));
            Assert.False(Press(rt, src, 0.5, "ButtonB"));

            ReleaseBoth();
            rt.Clear();
            rt.FrameSeq++;
            Assert.True(Press(rt, src, 0.5, "ButtonA"));
        }

        [Fact]
        public void ATriggerRowSendsAFullPullWhilePressed()
        {
            var rt = new SourceKindRuntime();
            var src = Rapid("Axis 5");
            float Pull(double depth)
            {
                rt.FrameSeq++;
                return SourceEvaluator.EvaluateForTriggerTarget(Axis(5, depth), src, 0, "RightTrigger", 0, rt, 0.001);
            }
            Assert.Equal(0f, Pull(0.2));
            Assert.Equal(1f, Pull(0.35));
            Assert.Equal(1f, Pull(0.6));
            Assert.Equal(0f, Pull(0.45));
            Assert.Equal(0f, Pull(0.52));
            Assert.Equal(1f, Pull(0.56));
            Assert.Equal(0f, Pull(0.1));
        }

        [Fact]
        public void ATriggerRowsZoneOpensAtHalfUnlessTheRowSetsAnother()
        {
            // The trigger lane carries no caller threshold. The source's own
            // deadzone applies, 50 percent when untouched, Toggle's default.
            var rt = new SourceKindRuntime();
            var src = Rapid("Axis 5", deadZone: 50);
            rt.FrameSeq++;
            Assert.Equal(0f, SourceEvaluator.EvaluateForTriggerTarget(Axis(5, 0.45), src, 0, "RightTrigger", 0, rt, 0.001));
            rt.FrameSeq++;
            Assert.Equal(1f, SourceEvaluator.EvaluateForTriggerTarget(Axis(5, 0.55), src, 0, "RightTrigger", 0, rt, 0.001));
        }

        [Fact]
        public void AStickRowReadsItAsDirect()
        {
            var rt = new SourceKindRuntime();
            var rapid = Rapid("Axis 0");
            var direct = new MappingSource { Descriptor = "Axis 0", DeadZone = Actuation };
            foreach (double depth in new[] { 0.0, 0.2, 0.5, 0.9, 0.45 })
            {
                rt.FrameSeq++;
                var s = Axis(0, depth);
                Assert.Equal(
                    SourceEvaluator.EvaluateForBipolarAxisTarget(s, direct, 0, "LeftThumbAxisX", 0, rt, 0.001),
                    SourceEvaluator.EvaluateForBipolarAxisTarget(s, rapid, 0, "LeftThumbAxisX", 0, rt, 0.001));
            }
        }

        [Fact]
        public void NoRuntimeReadsTheFirstPress()
        {
            // The previews pass no runtime so they never advance the polling
            // thread's state. Without one, Rapid Trigger reads as its first
            // press: the Direct read, a full pull on a trigger row.
            var src = Rapid("Axis 5");
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(Axis(5, 0.2), src, 50, 0, "ButtonA", 0, null, 0));
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(Axis(5, 0.4), src, 50, 0, "ButtonA", 0, null, 0));
            Assert.Equal(0f, SourceEvaluator.EvaluateForTriggerTarget(Axis(5, 0.2), src, 0, "RightTrigger", 0, null, 0));
            Assert.Equal(1f, SourceEvaluator.EvaluateForTriggerTarget(Axis(5, 0.4), src, 0, "RightTrigger", 0, null, 0));
        }

        [Fact]
        public void AnInputWithoutPressDepthReadsAsDirect()
        {
            // IR Brightness thresholds on the deadzone like a depth would,
            // but it measures light, so Rapid Trigger leaves it alone: a 15%
            // dim from 60% would release under Rapid Trigger and does not.
            var rt = new SourceKindRuntime();
            var src = Rapid("IR Brightness");
            CustomInputState Light(float v) => new() { JoyConIrIntensity = v };
            rt.FrameSeq++;
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(Light(0.6f), src, 50, 0, "ButtonA", 0, rt, 0.001));
            rt.FrameSeq++;
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(Light(0.45f), src, 50, 0, "ButtonA", 0, rt, 0.001));
            rt.FrameSeq++;
            Assert.Equal(0.45, SourceEvaluator.EvaluateForTriggerTarget(Light(0.45f), src, 0, "RightTrigger", 0, rt, 0.001), 3);
        }

        [Theory]
        [InlineData("Axis 2", true)]
        [InlineData("Slider 0", true)]
        [InlineData("Gamepad RightTrigger", true)]
        [InlineData("Gamepad LeftStickX", true)]
        [InlineData("Midi CC 7", true)]
        [InlineData("Midi Pitch Bend", true)]
        [InlineData("Touchpad 0 Finger 0 Pressure", true)]
        [InlineData("Ring-Con Squeeze", true)]
        [InlineData("Ring-Con Pull", true)]
        [InlineData("Midi CC 7 Up", false)]
        [InlineData("Midi Note 60", false)]
        [InlineData("Button 0", false)]
        [InlineData("POV 0 Up", false)]
        [InlineData("Gamepad ButtonA", false)]
        [InlineData("Gyro Pitch", false)]
        [InlineData("Mouse Motion X", false)]
        [InlineData("Mouse Position X", false)]
        [InlineData("IR Pointer X", false)]
        [InlineData("IR Brightness", false)]
        [InlineData("Touchpad 0 Finger 0 X", false)]
        [InlineData("Gamepad LeftStickRing", false)]
        [InlineData("Analog Key 0", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyInputsWithPressDepthTakeIt(string descriptor, bool expected)
            => Assert.Equal(expected, SourceCoercion.IsRapidTriggerSource(descriptor));

        [Fact]
        public void AnAnalogKeyTakesIt()
            => Assert.True(SourceCoercion.IsRapidTriggerSource(SourceCoercion.AnalogKeyDescriptor(AnalogKeyCodes.W)));

        [Fact]
        public void KindHelpersSortRapidTriggerWithTheDescriptorKinds()
        {
            var src = Rapid();
            Assert.True(SourceEvaluator.IsRapidTriggerKind(src));
            Assert.False(SourceEvaluator.IsRapidTriggerKind(new MappingSource()));
            Assert.False(SourceEvaluator.IsRapidTriggerKind(null));
            Assert.True(SourceEvaluator.IsDescriptorKind("RapidTrigger"));
            Assert.True(SourceEvaluator.ReadsEveryDevice(src));
            Assert.True(SourceEvaluator.ReadsEveryDevice(new MappingSource { Kind = "Toggle" }));
            Assert.False(SourceEvaluator.ReadsEveryDevice(new MappingSource()));

            // A Rapid Trigger with no input is a blank position, like a blank Direct.
            Assert.True(SourceEvaluator.IsUnmappedDirect(new MappingSource { Kind = "RapidTrigger" }));
            Assert.False(SourceEvaluator.IsUnmappedDirect(src));
        }

        [Fact]
        public void TheEngineAndTheEditorsReadOneDistance()
        {
            Assert.Equal(10, new MappingSource().ParamRapidTriggerDistance);
            Assert.Equal(10, MappingSource.EffectiveRapidTriggerDistance(0));
            Assert.Equal(10, MappingSource.EffectiveRapidTriggerDistance(-3));
            Assert.Equal(1, MappingSource.EffectiveRapidTriggerDistance(1));
            Assert.Equal(50, MappingSource.EffectiveRapidTriggerDistance(50));
            Assert.Equal(50, MappingSource.EffectiveRapidTriggerDistance(80));

            var msi = MappingSourceItem.FromDomain(Rapid(distance: 25));
            Assert.Equal(25, msi.ParamRapidTriggerDistance);
            Assert.Equal(25, msi.ToDomain().ParamRapidTriggerDistance);
            Assert.Equal("RapidTrigger", msi.ToDomain().Kind);
            msi.ParamRapidTriggerDistance = 0;
            Assert.Equal(1, msi.ParamRapidTriggerDistance);
            msi.ParamRapidTriggerDistance = 90;
            Assert.Equal(50, msi.ParamRapidTriggerDistance);
            Assert.True(msi.ResetSettingCommand.CanExecute(nameof(MappingSourceItem.ParamRapidTriggerDistance)));
            msi.ResetSettingCommand.Execute(nameof(MappingSourceItem.ParamRapidTriggerDistance));
            Assert.Equal(10, msi.ParamRapidTriggerDistance);
            Assert.Equal(10, MappingSourceItem.FromDomain(new MappingSource { ParamRapidTriggerDistance = 0 }).ParamRapidTriggerDistance);

            // A longer stored distance reads as the longest the editor offers.
            var rt = new SourceKindRuntime();
            var far = Rapid(distance: 80);
            rt.FrameSeq++;
            Assert.True(Press(rt, far, 1.0));
            rt.FrameSeq++;
            Assert.False(Press(rt, far, 0.49));   // 51% up: released at the 50% cap
        }

        [Fact]
        public void KindPickerOffersRapidTriggerRightAfterToggle()
        {
            var options = MappingSourceItem.KindOptions;
            Assert.Equal("Direct", options[0].Value);
            Assert.Equal("Toggle", options[1].Value);
            Assert.Equal("RapidTrigger", options[2].Value);
            Assert.Equal(Strings.Instance.Pad_Mapping_Kind_RapidTrigger, options[2].Name);
        }

        [Theory]
        [InlineData("ButtonA", "Axis 2", true)]
        [InlineData("ButtonA", "", true)]              // no input yet
        [InlineData("ButtonA", "IHAxis 2", true)]      // the legacy flag prefix
        [InlineData("ButtonA", "Button 0", false)]
        [InlineData("ButtonA", "Gyro Pitch", false)]
        [InlineData("DPadUp", "Axis 1", true)]
        [InlineData("RightTrigger", "Axis 5", true)]
        [InlineData("LeftTrigger", "Button 4", false)]
        [InlineData("LeftThumbAxisX", "Axis 0", false)]
        [InlineData("KbmMouseX", "Axis 0", false)]
        // The touchpad X/Y rows read every source as a position.
        [InlineData("TouchpadX1", "Axis 2", false)]
        [InlineData("TouchpadY1", "Axis 2", false)]
        [InlineData("TouchpadX2", "Axis 2", false)]
        [InlineData("TouchpadY2", "Axis 2", false)]
        // Every other press family goes through the button evaluator.
        [InlineData("TouchpadContact1", "Axis 2", true)]
        [InlineData("TouchpadClick", "Axis 2", true)]
        [InlineData("KbmKey41", "Axis 2", true)]
        [InlineData("KbmMBtn0", "Axis 2", true)]
        [InlineData("MidiNote0", "Axis 2", true)]
        [InlineData("VrRA", "Axis 2", true)]
        [InlineData("VrRTrigger", "Axis 2", false)]
        [InlineData("RawBtn0", "Axis 2", true)]
        [InlineData("RawPov0Up", "Axis 2", true)]
        public void PrimaryModeOffersRapidTriggerOnlyWhereItActs(string target, string descriptor, bool offered)
        {
            var item = new MappingItem("T", target, MappingCategory.Buttons);
            item.LoadDescriptor(descriptor);
            Assert.Equal(offered, item.IsRapidTriggerOffered);
            Assert.Equal(offered, item.PrimaryKindOptions.Any(k => k.Value == "RapidTrigger"));

            // An extra source on the same row answers the same way.
            var extra = new MappingSourceItem { Descriptor = descriptor };
            item.ExtraSources.Add(extra);
            Assert.Equal(offered, extra.IsRapidTriggerOffered);
            Assert.Equal(offered, extra.KindChoices.Any(k => k.Value == "RapidTrigger"));
        }

        [Fact]
        public void PickingAnInputWithoutDepthDropsRapidTrigger()
        {
            var item = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            item.PrimaryKindSource.Kind = "RapidTrigger";   // chosen before the input
            Assert.Equal("RapidTrigger", item.PrimaryKindSource.Kind);
            item.LoadDescriptor("Axis 2");
            Assert.Equal("RapidTrigger", item.PrimaryKindSource.Kind);
            Assert.True(item.IsPrimaryDescriptor);
            Assert.False(item.IsPrimaryDirect);
            Assert.False(item.IsMultiSource);
            Assert.False(item.IsTrivialDirect);
            item.LoadDescriptor("Button 3");
            Assert.Equal("Direct", item.PrimaryKindSource.Kind);
            Assert.DoesNotContain(item.PrimaryKindOptions, k => k.Value == "RapidTrigger");
        }

        [Fact]
        public void ALoadJudgesTheRowOnceItIsIn()
        {
            var item = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            item.BeginLoadRow();
            item.LoadPrimaryKind(Rapid(distance: 25));
            item.LoadDescriptor("Axis 2");
            item.EndLoadRow();
            Assert.Equal("RapidTrigger", item.PrimaryKindSource.Kind);
            Assert.Equal(25, item.PrimaryKindSource.ParamRapidTriggerDistance);

            // A stored Rapid Trigger on a stick row cannot act: it loads as Direct.
            var stick = new MappingItem("LX", "LeftThumbAxisX", MappingCategory.LeftStick);
            stick.BeginLoadRow();
            stick.LoadDescriptor("Axis 0");
            stick.LoadPrimaryKind(Rapid("Axis 0"));
            stick.EndLoadRow();
            Assert.Equal("Direct", stick.PrimaryKindSource.Kind);

            // Nor on a touchpad X/Y row, which reads the input as a position.
            var touchX = new MappingItem("TX", "TouchpadX1", MappingCategory.Touchpad);
            touchX.BeginLoadRow();
            touchX.LoadDescriptor("Axis 2");
            touchX.LoadPrimaryKind(Rapid());
            touchX.EndLoadRow();
            Assert.Equal("Direct", touchX.PrimaryKindSource.Kind);

            // A null source (an unmapped row) resets the distance with the rest.
            item.LoadPrimaryKind(null);
            Assert.Equal("Direct", item.PrimaryKindSource.Kind);
            Assert.Equal(10, item.PrimaryKindSource.ParamRapidTriggerDistance);
        }

        [Fact]
        public void ATriggerRowShowsItsDeadzoneOnlyInRapidTrigger()
        {
            var item = new MappingItem("RT", "RightTrigger", MappingCategory.Triggers);
            item.LoadDescriptor("Axis 5");
            Assert.False(item.IsDeadZoneApplicable);
            item.PrimaryKindSource.Kind = "RapidTrigger";
            Assert.True(item.IsDeadZoneApplicable);
            item.PrimaryKindSource.Kind = "Toggle";
            Assert.False(item.IsDeadZoneApplicable);

            // A button row shows it in every mode, as before.
            var button = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            button.LoadDescriptor("Axis 5");
            Assert.True(button.IsDeadZoneApplicable);
        }

        [Fact]
        public void AnExtraSourceListsRapidTriggerOnlyWhereItActs()
        {
            var item = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            var extra = new MappingSourceItem { Descriptor = SourceCoercion.AnalogKeyDescriptor(AnalogKeyCodes.W) };
            item.ExtraSources.Add(extra);
            Assert.Contains(extra.KindChoices, k => k.Value == "RapidTrigger");
            extra.Kind = "RapidTrigger";
            extra.Descriptor = "Button 2";
            Assert.Equal("Direct", extra.Kind);
            Assert.DoesNotContain(extra.KindChoices, k => k.Value == "RapidTrigger");

            // A stick row never lists it, and a stored one drops when the
            // source joins the row.
            var stick = new MappingItem("LX", "LeftThumbAxisX", MappingCategory.LeftStick);
            var stored = MappingSourceItem.FromDomain(Rapid("Axis 0"));
            Assert.Equal("RapidTrigger", stored.Kind);
            stick.ExtraSources.Add(stored);
            Assert.Equal("Direct", stored.Kind);
            Assert.DoesNotContain(stored.KindChoices, k => k.Value == "RapidTrigger");

            // A touchpad X/Y row is discrete for the deadzone, yet reads its
            // sources as positions, so a stored one drops there too.
            var touchX = new MappingItem("TX", "TouchpadX1", MappingCategory.Touchpad);
            var onPad = MappingSourceItem.FromDomain(Rapid("Axis 2"));
            touchX.ExtraSources.Add(onPad);
            Assert.Equal("Direct", onPad.Kind);
            Assert.DoesNotContain(onPad.KindChoices, k => k.Value == "RapidTrigger");

            // A trigger row lists it, and the source's deadzone opens with it.
            var trigger = new MappingItem("RT", "RightTrigger", MappingCategory.Triggers);
            var pull = new MappingSourceItem { Descriptor = "Axis 5" };
            trigger.ExtraSources.Add(pull);
            Assert.Contains(pull.KindChoices, k => k.Value == "RapidTrigger");
            Assert.False(pull.IsDeadZoneApplicable);
            pull.Kind = "RapidTrigger";
            Assert.True(pull.IsDeadZoneApplicable);
        }

        [Fact]
        public void TheEditorsRefreshWhenTheModeOrTheInputChanges()
        {
            var item = new MappingItem("RT", "RightTrigger", MappingCategory.Triggers);
            item.LoadDescriptor("Axis 5");
            var raised = new System.Collections.Generic.List<string>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            item.PrimaryKindSource.Kind = "RapidTrigger";
            Assert.Contains(nameof(MappingItem.IsDeadZoneApplicable), raised);
            Assert.Contains(nameof(MappingItem.PrimaryKindOptions), raised);
            raised.Clear();
            item.LoadDescriptor("Axis 2");
            Assert.Contains(nameof(MappingItem.IsRapidTriggerOffered), raised);
            Assert.Contains(nameof(MappingItem.PrimaryKindOptions), raised);

            var row = new MappingItem("RT", "RightTrigger", MappingCategory.Triggers);
            var extra = new MappingSourceItem { Descriptor = "Axis 5" };
            row.ExtraSources.Add(extra);
            var extraRaised = new System.Collections.Generic.List<string>();
            extra.PropertyChanged += (_, e) => extraRaised.Add(e.PropertyName);
            extra.Kind = "RapidTrigger";
            Assert.Contains(nameof(MappingSourceItem.IsRapidTriggerKind), extraRaised);
            Assert.Contains(nameof(MappingSourceItem.IsDeadZoneApplicable), extraRaised);
            extraRaised.Clear();
            extra.Descriptor = "Axis 2";
            Assert.Contains(nameof(MappingSourceItem.KindChoices), extraRaised);
            Assert.Contains(nameof(MappingSourceItem.IsRapidTriggerOffered), extraRaised);
            extraRaised.Clear();
            extra.ParentTargetTakesRapidTrigger = false;
            Assert.Contains(nameof(MappingSourceItem.KindChoices), extraRaised);
            Assert.Contains(nameof(MappingSourceItem.IsRapidTriggerOffered), extraRaised);
            extraRaised.Clear();
            extra.ParentTargetIsTrigger = false;
            Assert.Contains(nameof(MappingSourceItem.IsDeadZoneApplicable), extraRaised);
        }

        [Fact]
        public void ClearResetsTheModeAndTheDistance()
        {
            var item = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            item.LoadDescriptor("Axis 2");
            item.PrimaryKindSource.Kind = "RapidTrigger";
            item.PrimaryKindSource.ParamRapidTriggerDistance = 30;
            item.ClearCommand.Execute(null);
            Assert.Equal("Direct", item.PrimaryKindSource.Kind);
            Assert.Equal(10, item.PrimaryKindSource.ParamRapidTriggerDistance);

            var vm = new PadViewModel(0);
            var row = new MappingItem("B", "ButtonB", MappingCategory.Buttons);
            row.LoadDescriptor("Axis 2");
            row.PrimaryKindSource.Kind = "RapidTrigger";
            row.PrimaryKindSource.ParamRapidTriggerDistance = 30;
            vm.Mappings.Add(row);
            vm.ClearMappingsCommand.Execute(null);
            Assert.Equal("Direct", row.PrimaryKindSource.Kind);
            Assert.Equal(10, row.PrimaryKindSource.ParamRapidTriggerDistance);
        }
    }

    [Collection("CultureSwitching")]
    public class RapidTriggerSourceKindCultureTests
    {
        [Theory]
        [InlineData("en", "Rapid Trigger")]
        [InlineData("de", "Rapid Trigger")]
        [InlineData("es", "Rapid Trigger")]
        [InlineData("fr", "Rapid Trigger")]
        [InlineData("it", "Rapid Trigger")]
        [InlineData("ja", "ラピッドトリガー")]
        [InlineData("ko", "래피드 트리거")]
        [InlineData("nl", "Rapid Trigger")]
        [InlineData("pt-BR", "Rapid Trigger")]
        [InlineData("zh-Hans", "快速触发")]
        public void RapidTriggerIsNamedInEveryLanguage(string culture, string expected)
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo(culture));
                Assert.Equal(expected, MappingSourceItem.KindOptions[2].Name);
                Assert.Contains(expected, Strings.Instance.Pad_Mapping_Kind_Tooltip);
                Assert.False(string.IsNullOrWhiteSpace(Strings.Instance.Pad_Mapping_RapidTrigger_Distance));
                Assert.False(string.IsNullOrWhiteSpace(Strings.Instance.Pad_Mapping_RapidTrigger_Distance_Tooltip));
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }
    }

    [Collection("SettingsManagerStatics")]
    public class RapidTriggerSourceKindSlotTests : IDisposable
    {
        private const int Slot = 0;
        private static readonly Guid PadA = new("d4820001-0000-4000-8000-000000000001");
        private static readonly Guid PadB = new("d4820002-0000-4000-8000-000000000002");

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly bool _stale = InputService.VmMappingsStale;
        private readonly bool _suppressPush = InputService.SuppressMappingEditPush;
        private readonly Action _afterRefresh = SettingsService.AfterMappingSetsRefreshed;

        private readonly CustomInputState _stateA = RestState();
        private readonly CustomInputState _stateB = RestState();

        private static readonly MethodInfo BeginFrame = typeof(InputManager)
            .GetMethod("BeginFrameMultiSourceTracking", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo Apply = typeof(InputManager)
            .GetMethod("ApplyMappingSetToGamepad", BindingFlags.NonPublic | BindingFlags.Static);

        public RapidTriggerSourceKindSlotTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated);
            Array.Clear(SettingsManager.SlotEnabled);
            SettingsManager.SlotCreated[Slot] = true;
            SettingsManager.SlotEnabled[Slot] = true;
            InputService.VmMappingsStale = false;
            InputService.SuppressMappingEditPush = false;
            SettingsService.AfterMappingSetsRefreshed = null;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
            AddDevice(PadA, _stateA);
            AddDevice(PadB, _stateB);
        }

        public void Dispose()
        {
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
            InputService.VmMappingsStale = _stale;
            InputService.SuppressMappingEditPush = _suppressPush;
            SettingsService.AfterMappingSetsRefreshed = _afterRefresh;
        }

        private static CustomInputState RestState()
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            state.Axis[2] = state.Axis[5] = 0;
            Array.Fill(state.Povs, -1);
            return state;
        }

        private static void AddDevice(Guid id, CustomInputState state)
        {
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, IsOnline = true,
                ProductName = id.ToString(), InstanceName = id.ToString(),
                CapType = InputDeviceType.Gamepad, CapButtonCount = 16, InputState = state,
            });
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = Slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
        }

        private static MappingSet OneRow(params MappingSource[] sources)
        {
            var set = new MappingSet();
            set.Rows.Add(new MappingRow
            {
                Target = "ButtonA", LayerMask = "Base", CombineMode = "OR",
                Sources = sources.ToList(),
            });
            SettingsManager.SlotMappingSets[Slot] = set;
            return set;
        }

        private static MappingSource AnyDeviceRapid()
            => new() { Kind = "RapidTrigger", DeviceGuid = "", Descriptor = "Axis 5", DeadZone = 30 };

        /// <summary>One polling frame the way UpdateOutputStates runs it:
        /// the frame begins, every device on the slot takes its pass, and
        /// Step 4 ORs the passes' buttons.</summary>
        private bool Frame(MappingSet set, double a, double b)
        {
            _stateA.Axis[5] = (int)Math.Round(a * 65535);
            _stateB.Axis[5] = (int)Math.Round(b * 65535);
            Assert.NotNull(BeginFrame);
            Assert.NotNull(Apply);
            BeginFrame.Invoke(null, null);
            bool pressed = false;
            foreach (var (state, id) in new[] { (_stateA, PadA), (_stateB, PadB) })
            {
                object[] args = { state, set, id.ToString(), 50, Slot, new Gamepad() };
                Apply.Invoke(null, args);
                pressed |= ((Gamepad)args[5]).IsButtonPressed(Gamepad.A);
            }
            return pressed;
        }

        [Fact]
        public void AnyDeviceRapidTriggerInASingleSourceRowStepsOnTheDeepestDevice()
        {
            var set = OneRow(AnyDeviceRapid());
            Assert.False(Frame(set, 0, 0));
            Assert.True(Frame(set, 0, 0.6));       // B past the deadzone
            Assert.False(Frame(set, 0, 0.45));     // B rises 15%: released
            Assert.True(Frame(set, 0.56, 0.45));   // A is 11% past B's shallow point
            Assert.True(Frame(set, 0.6, 0.9));     // B deeper still
            Assert.False(Frame(set, 0.6, 0.75));   // 15% up from B's 90%, whatever A holds
            Assert.False(Frame(set, 0.2, 0.2));    // both short of the deadzone
        }

        [Fact]
        public void AnyDeviceRapidTriggerInAMultiSourceRowReadsEveryDevice()
        {
            // The multi-source walk stopped at the first device that
            // answered true. A pressed Rapid Trigger answers true on the
            // first device, so a deeper second device never set the deepest
            // point, and its rise from there never released.
            var set = OneRow(AnyDeviceRapid(), new MappingSource { DeviceGuid = "", Descriptor = "Button 12" });
            Assert.False(Frame(set, 0, 0));
            Assert.True(Frame(set, 0.6, 0.9));
            Assert.False(Frame(set, 0.6, 0.75));
            Assert.True(Frame(set, 0.6, 0.86));
        }

        [Fact]
        public void AMotionRowKeepsItsOneWayReadUnderRapidTrigger()
        {
            // Rapid Trigger reads as Direct on a stick lane, the Motion rows
            // included, whose resting-at-zero sources read one way.
            var keep = SourceCoercion.SourceRestsAtZeroProvider;
            try
            {
                SourceCoercion.SourceRestsAtZeroProvider = (d, g) => true;
                var rt = new SourceKindRuntime();
                var rapid = new MappingSource { Kind = "RapidTrigger", Descriptor = "Axis 5" };
                var direct = new MappingSource { Descriptor = "Axis 5" };
                foreach (int raw in new[] { 0, 30000, 65535 })
                {
                    var s = new CustomInputState();
                    s.Axis[5] = raw;
                    rt.FrameSeq++;
                    Assert.Equal(
                        SourceEvaluator.EvaluateForBipolarAxisTarget(s, direct, 0, MappingSetMigrator.MotionYawTarget, 0, rt, 0.001),
                        SourceEvaluator.EvaluateForBipolarAxisTarget(s, rapid, 0, MappingSetMigrator.MotionYawTarget, 0, rt, 0.001));
                }
            }
            finally
            {
                SourceCoercion.SourceRestsAtZeroProvider = keep;
            }
        }

        [Fact]
        public void LegacyMergeKeepsAnAnyDeviceRapidTriggerRowAsAuthored()
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var setting = SettingsManager.FindSettingByInstanceGuidAndSlot(PadA, Slot);
                setting.GetPadSetting().ButtonA = "Axis 5";
                var rapid = AnyDeviceRapid();
                SettingsManager.SlotMappingSets[Slot] = new MappingSet
                {
                    Rows = new() { new MappingRow { Target = "ButtonA", LayerMask = "Base", Sources = new() { rapid } } },
                };
                SettingsService.RefreshMappingSetsFromLegacy();
                var row = SettingsManager.SlotMappingSets[Slot].Rows.Single(r => r.Target == "ButtonA");
                Assert.Same(rapid, Assert.Single(row.Sources));
            }
            finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
        }

        private readonly MainViewModel _vm = new();

        private MappingItem Hydrate()
        {
            var pad = _vm.Pads[Slot];
            pad.Mappings.Clear();
            var item = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            pad.Mappings.Add(item);
            InputService.RefreshMappingsToViewModel(pad);
            Assert.True(pad.MappingsViewLoaded);
            return item;
        }

        private MappingItem SaveReload(SettingsService service)
        {
            service.PushUiExtraSourcesIntoSlotMappingSets();
            var serializer = new XmlSerializer(typeof(MappingSet));
            using var stream = new MemoryStream();
            serializer.Serialize(stream, SettingsManager.SlotMappingSets[Slot]);
            stream.Position = 0;
            var persisted = new MappingSet[InputManager.MaxPads];
            persisted[Slot] = (MappingSet)serializer.Deserialize(stream);
            SettingsService.LoadOrMigrateSlotMappingSetsForTest(persisted);
            SettingsManager.SlotMappingSets[Slot] = InputService.CloneMappingSetDeep(
                SettingsManager.SlotMappingSets[Slot]);
            return Hydrate();
        }

        [Fact]
        public void RapidTriggerRowSurvivesTheEditorAndASaveReload()
        {
            var service = new SettingsService(_vm);
            OneRow(new MappingSource
            {
                Kind = "RapidTrigger", DeviceGuid = PadA.ToString(), Descriptor = "Axis 5",
                DeadZone = 20, ParamRapidTriggerDistance = 25,
            });

            var item = Hydrate();
            Assert.Equal("Axis 5", item.SourceDescriptor);
            Assert.Equal("RapidTrigger", item.PrimaryKindSource.Kind);
            Assert.Equal(25, item.PrimaryKindSource.ParamRapidTriggerDistance);
            Assert.Equal(20, item.MappingDeadZone);

            item = SaveReload(service);
            var saved = Assert.Single(SettingsManager.SlotMappingSets[Slot].Rows.Single(r => r.Target == "ButtonA").Sources);
            Assert.Equal("RapidTrigger", saved.Kind);
            Assert.Equal(25, saved.ParamRapidTriggerDistance);
            Assert.Equal(20, saved.DeadZone);
            Assert.Equal("Axis 5", saved.Descriptor);
            Assert.Equal("RapidTrigger", item.PrimaryKindSource.Kind);
            Assert.Equal(25, item.PrimaryKindSource.ParamRapidTriggerDistance);

            // Back to Direct saves a plain Direct source that still remembers
            // the distance for a later switch back.
            item.PrimaryKindSource.Kind = "Direct";
            item = SaveReload(service);
            saved = Assert.Single(SettingsManager.SlotMappingSets[Slot].Rows.Single(r => r.Target == "ButtonA").Sources);
            Assert.Equal("Direct", saved.Kind);
            Assert.Equal(25, saved.ParamRapidTriggerDistance);
            item.PrimaryKindSource.Kind = "RapidTrigger";
            SaveReload(service);
            saved = Assert.Single(SettingsManager.SlotMappingSets[Slot].Rows.Single(r => r.Target == "ButtonA").Sources);
            Assert.Equal("RapidTrigger", saved.Kind);
            Assert.Equal(25, saved.ParamRapidTriggerDistance);
        }
    }
}
