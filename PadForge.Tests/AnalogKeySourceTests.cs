using System;
using System.IO;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.AnalogKeyboard;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The "Analog Key N" source family (issue #468): a key's press depth on
    /// <see cref="CustomInputState.AnalogKeys"/>, read as a button past the
    /// row's deadzone, as a trigger pull, and as one side of a stick axis.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AnalogKeySourceTests
    {
        private static readonly string W = SourceCoercion.AnalogKeyDescriptor(AnalogKeyCodes.W);

        private static MappingSource Src(string descriptor, int deadZone = 0, bool invert = false) => new()
        {
            Descriptor = descriptor,
            DeadZone = deadZone,
            Invert = invert,
        };

        private static CustomInputState State(params (int code, float depth)[] keys)
        {
            var s = new CustomInputState { AnalogKeys = new AnalogKeyInputState() };
            foreach (var (code, depth) in keys) s.AnalogKeys.Set(code, depth);
            return s;
        }

        private static CustomInputState Depth(float w) => State((AnalogKeyCodes.W, w));

        // ── The sub-state ──

        [Fact]
        public void Set_ReplacesClampsAndRemoves()
        {
            var k = new AnalogKeyInputState();
            Assert.True(k.Set(AnalogKeyCodes.W, 0.4f));
            Assert.True(k.Set(AnalogKeyCodes.W, 0.6f));
            Assert.Equal(1, k.Count);
            Assert.Equal(0.6f, k.Get(AnalogKeyCodes.W));
            k.Set(AnalogKeyCodes.A, 7f);
            Assert.Equal(1f, k.Get(AnalogKeyCodes.A));
            k.Set(AnalogKeyCodes.W, 0f);
            Assert.Equal(0f, k.Get(AnalogKeyCodes.W));
            Assert.Equal(1, k.Count);
            k.Set(AnalogKeyCodes.A, float.NaN);
            Assert.Equal(0, k.Count);
        }

        [Fact]
        public void CodesOutsideTheSpace_AreIgnored()
        {
            var k = new AnalogKeyInputState();
            Assert.True(k.Set(0, 1f));
            Assert.True(k.Set(-5, 1f));
            Assert.True(k.Set(AnalogKeyInputState.CodeCount, 1f));
            Assert.Equal(0, k.Count);
        }

        [Fact]
        public void AFullState_RefusesANewKey_ButStillUpdatesAHeldOne()
        {
            var k = new AnalogKeyInputState();
            for (int i = 0; i < AnalogKeyInputState.MaxKeys; i++) Assert.True(k.Set(i + 1, 0.5f));
            Assert.False(k.Set(AnalogKeyInputState.MaxKeys + 1, 0.5f));
            Assert.True(k.Set(1, 0.9f));
            Assert.Equal(0.9f, k.Get(1));
            Assert.Equal(AnalogKeyInputState.MaxKeys, k.Count);
        }

        [Fact]
        public void AFullState_KeepsTheDeepestKeys()
        {
            // The routes that read a whole matrix report every key above
            // rest, so a full state trades its shallowest key for a deeper
            // one, and a pressed key is never hidden behind resting ones.
            var k = new AnalogKeyInputState();
            for (int i = 0; i < AnalogKeyInputState.MaxKeys; i++) Assert.True(k.Set(i + 1, 0.01f + i * 0.001f));
            Assert.True(k.Set(AnalogKeyCodes.PositionBase, 0.9f));
            Assert.Equal(AnalogKeyInputState.MaxKeys, k.Count);
            Assert.Equal(0.9f, k.Get(AnalogKeyCodes.PositionBase));
            Assert.Equal(0f, k.Get(1));
            Assert.Equal(0.011f, k.Get(2), 5);
            Assert.False(k.Set(AnalogKeyCodes.PositionBase + 1, 0.005f));
            Assert.Equal(0f, k.Get(AnalogKeyCodes.PositionBase + 1));
        }

        [Fact]
        public void AMappingRead_MarksTheKey_OnTheRowAndEveryCopy()
        {
            using var dev = new AnalogKeyboardDevice(Candidate());
            dev.InjectForTest(AnalogKeyCodes.W, 0.75f);
            dev.AttachForTest();
            var s = dev.GetCurrentState();
            Assert.False(dev.IsMappedForTest(AnalogKeyCodes.W));
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, Src(W, 60), 50));
            Assert.True(dev.IsMappedForTest(AnalogKeyCodes.W));
            Assert.False(dev.IsMappedForTest(AnalogKeyCodes.S));

            // A copy of the row's state reads for the same row.
            var copy = s.Clone();
            SourceCoercion.EvaluateForTriggerTarget(copy, Src(SourceCoercion.AnalogKeyDescriptor(AnalogKeyCodes.S)));
            Assert.True(dev.IsMappedForTest(AnalogKeyCodes.S));

            // A state of no row reads without stamping anything.
            Assert.Equal(0.5f, SourceCoercion.EvaluateForTriggerTarget(Depth(0.5f), Src(W)));
        }

        [Fact]
        public void Remove_KeepsTheOthersInOrder()
        {
            var k = new AnalogKeyInputState();
            k.Set(AnalogKeyCodes.Q, 0.1f);
            k.Set(AnalogKeyCodes.W, 0.2f);
            k.Set(AnalogKeyCodes.E, 0.3f);
            k.Remove(AnalogKeyCodes.W);
            Assert.Equal(new[] { AnalogKeyCodes.Q, AnalogKeyCodes.E }, k.Codes.Take(k.Count));
            Assert.Equal(0, k.Codes[2]);
            Assert.Equal(0f, k.Depths[2]);
        }

        [Fact]
        public void SameAs_IgnoresOrder_AndNullIsNoKeyDown()
        {
            var a = new AnalogKeyInputState();
            var b = new AnalogKeyInputState();
            a.Set(AnalogKeyCodes.Q, 0.1f); a.Set(AnalogKeyCodes.W, 0.2f);
            b.Set(AnalogKeyCodes.W, 0.2f); b.Set(AnalogKeyCodes.Q, 0.1f);
            Assert.True(a.SameAs(b));
            b.Set(AnalogKeyCodes.Q, 0.15f);
            Assert.False(a.SameAs(b));
            Assert.False(a.SameAs(null));
            Assert.True(new AnalogKeyInputState().SameAs(null));
        }

        [Fact]
        public void CloneAndCopyInto_AreDeep_AndResetEqualsFresh()
        {
            var s = Depth(0.7f);
            var clone = s.Clone();
            Assert.Equal(0.7f, clone.AnalogKeys.Get(AnalogKeyCodes.W));
            clone.AnalogKeys.Set(AnalogKeyCodes.W, 0.1f);
            Assert.Equal(0.7f, s.AnalogKeys.Get(AnalogKeyCodes.W));

            // A device without the family copies as null, dropping a stale set.
            var dst = Depth(0.5f);
            new CustomInputState().CopyInto(dst);
            Assert.Null(dst.AnalogKeys);

            s.ResetForReuse();
            Assert.NotNull(s.AnalogKeys);
            Assert.Equal(0, s.AnalogKeys.Count);
        }

        // ── The descriptor ──

        [Fact]
        public void TheDescriptor_ClassifiesAsItsOwnFamily()
        {
            Assert.Equal("Analog Key 26", W);
            Assert.Equal(SourceCoercion.SourceType.AnalogKey, SourceCoercion.ClassifyDescriptor(W));
            Assert.True(SourceCoercion.IsThresholdedButtonFamily(W));
            Assert.False(SourceCoercion.IsThresholdedButtonFamily("Button 3"));
        }

        [Theory]
        [InlineData("Analog Key 26", true, 26)]
        [InlineData(" Analog Key 26 ", true, 26)]
        [InlineData("Analog Key 1", true, 1)]
        [InlineData("Analog Key 1791", true, 1791)]
        [InlineData("Analog Key 1792", false, 0)]
        [InlineData("Analog Key 0", false, 0)]
        [InlineData("Analog Key -1", false, 0)]
        [InlineData("Analog Key +5", false, 0)]
        [InlineData("Analog Key 2 6", false, 0)]
        [InlineData("Analog Key ", false, 0)]
        [InlineData("analog key 26", false, 0)]
        [InlineData("Analog Keys 26", false, 0)]
        [InlineData(null, false, 0)]
        public void TryParse_TakesOnlyAWholeCodeInTheSpace(string descriptor, bool ok, int code)
        {
            Assert.Equal(ok, SourceCoercion.TryParseAnalogKey(descriptor, out int parsed));
            Assert.Equal(code, ok ? parsed : 0);
        }

        // ── Button reads ──

        /// <summary>The row's deadzone is the actuation point.</summary>
        [Theory]
        [InlineData(0.30f, 25, true)]
        [InlineData(0.30f, 40, false)]
        [InlineData(0.50f, 0, true)]      // unset deadzone: the 50 percent global
        [InlineData(0.49f, 0, false)]
        [InlineData(1.00f, 100, true)]    // the bottom of the press reaches 100
        [InlineData(0.99f, 100, false)]
        [InlineData(0.00f, 1, false)]
        public void AsAButton_FiresAtTheDeadzone(float depth, int deadZone, bool pressed)
        {
            Assert.Equal(pressed, SourceCoercion.EvaluateForButtonTarget(Depth(depth), Src(W, deadZone), 50));
        }

        /// <summary>A trigger-click target reads at a 0 threshold, and a key
        /// at rest still never fires.</summary>
        [Fact]
        public void AZeroThreshold_NeedsTheKeyToMove()
        {
            Assert.False(SourceCoercion.EvaluateForButtonTarget(Depth(0f), Src(W), 0));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(new CustomInputState(), Src(W), 0));
            Assert.True(SourceCoercion.EvaluateForButtonTarget(Depth(0.01f), Src(W), 0));
        }

        /// <summary>Soft press and full press are two rows on one key, and
        /// the Only One combine fires between them.</summary>
        [Theory]
        [InlineData(0.10f, false, false, false)]
        [InlineData(0.40f, true, false, true)]
        [InlineData(0.95f, true, true, false)]
        public void SoftAndFullPress_AreTwoThresholdsOnOneKey(float depth, bool soft, bool full, bool between)
        {
            var state = Depth(depth);
            bool s = SourceCoercion.EvaluateForButtonTarget(state, Src(W, 20), 50);
            bool f = SourceCoercion.EvaluateForButtonTarget(state, Src(W, 90), 50);
            Assert.Equal(soft, s);
            Assert.Equal(full, f);
            Assert.Equal(between, CombineHelper.CombineButton("XOR", new[] { s, f }));
        }

        [Fact]
        public void AnotherKey_OrAnotherDevice_ReadsReleased()
        {
            Assert.False(SourceCoercion.EvaluateForButtonTarget(State((AnalogKeyCodes.S, 1f)), Src(W), 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(new CustomInputState(), Src(W), 50));
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(new CustomInputState(), Src(W)));
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(new CustomInputState(), Src(W)));
        }

        [Fact]
        public void TheParamAndGateRead_UsesTheHalfwayPoint()
        {
            Assert.True(SourceCoercion.ReadHardwareBoolDescriptor(Depth(0.5f), W));
            Assert.False(SourceCoercion.ReadHardwareBoolDescriptor(Depth(0.49f), W));
        }

        // ── Analog reads ──

        [Fact]
        public void AsATrigger_TheDepthIsThePull()
        {
            Assert.Equal(0.37f, SourceCoercion.EvaluateForTriggerTarget(Depth(0.37f), Src(W)), precision: 5);
            Assert.Equal(1f, SourceCoercion.EvaluateForTriggerTarget(Depth(1f), Src(W)));
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(Depth(0f), Src(W)));
        }

        /// <summary>A key on a stick axis rests at center and pushes one
        /// side. Invert pushes the other, so two keys drive one axis the way
        /// two buttons do.</summary>
        [Fact]
        public void OnAStickAxis_AKeyPushesOneSide_AndInvertTheOther()
        {
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(Depth(0f), Src(W)));
            Assert.Equal(0.6f, SourceCoercion.EvaluateForBipolarAxisTarget(Depth(0.6f), Src(W)), precision: 5);
            var s = SourceCoercion.AnalogKeyDescriptor(AnalogKeyCodes.S);
            Assert.Equal(-0.8f, SourceCoercion.EvaluateForBipolarAxisTarget(
                State((AnalogKeyCodes.S, 0.8f)), Src(s, invert: true)), precision: 5);
        }

        /// <summary>Shift activators and the recorders treat the key as a
        /// trigger: it rests at 0 and travels one way (#443's rule).</summary>
        [Fact]
        public void AKey_RestsAtZero_ForTheActivatorsAndRecorders()
        {
            Assert.True(InputManager.AxisRestsAtZero(W, null));
            Assert.False(InputManager.AxisRestsAtZero("Axis 0", null));
        }

        // ── Device rows ──

        [Fact]
        public void TheDeviceType_StaysOutOfTheAnyDeviceWildcard()
        {
            Assert.Equal(38, InputDeviceType.AnalogKeyboard);
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.AnalogKeyboard));
        }

        private static AnalogKeyboardCandidate Candidate(ushort vid = 0x31E3, ushort pid = 0x1232, string serial = "A1B2C3")
        {
            var info = new AnalogKeyboardDeviceInfo
            {
                Path = @"\\?\hid#vid_31e3&pid_1232&mi_03#test",
                VendorId = vid,
                ProductId = pid,
                UsagePage = 0xFF53,
                InputReportLength = 65,
                ProductString = "Wooting 60HE",
                SerialNumber = serial,
            };
            return new AnalogKeyboardCandidate
            {
                Info = info,
                Routes = AnalogKeyboardRoutes.Candidates(info),
                Name = "Wooting 60HE",
                IdentityKey = AnalogKeyboardHidRuntime.IdentityKeyFor(info),
            };
        }

        [Fact]
        public void TheWootingV2Interface_MatchesItsRoute()
        {
            var routes = Candidate().Routes;
            Assert.Equal("soup-wooting-v2", Assert.Single(routes).Id);
            Assert.False(routes[0].Writable);
        }

        [Fact]
        public void TheRow_IsAKeyboardWithNoNumberedInputs()
        {
            using var dev = new AnalogKeyboardDevice(Candidate());
            Assert.Equal(InputDeviceType.AnalogKeyboard, dev.GetInputDeviceType());
            Assert.Equal(0, dev.NumButtons);
            Assert.Equal(0, dev.NumAxes);
            Assert.Empty(dev.GetDeviceObjects());
            Assert.StartsWith("analogkb://", dev.DevicePath);
            Assert.Equal("Wooting 60HE", dev.Name);
        }

        /// <summary>A Wooting keyboard keeps its row across its gamepad
        /// modes, whose product IDs differ in the low nibble.</summary>
        [Fact]
        public void TheIdentity_SurvivesAWootingModeChange_AndSeparatesTwoKeyboards()
        {
            using var mode1 = new AnalogKeyboardDevice(Candidate(pid: 0x1232));
            using var mode2 = new AnalogKeyboardDevice(Candidate(pid: 0x1231));
            using var other = new AnalogKeyboardDevice(Candidate(serial: "Z9"));
            Assert.Equal(mode1.InstanceGuid, mode2.InstanceGuid);
            Assert.Equal(mode1.ProductGuid, mode2.ProductGuid);
            Assert.NotEqual(mode1.InstanceGuid, other.InstanceGuid);
        }

        [Fact]
        public void TheState_CarriesTheLiveKeys_OnlyWhileAttached()
        {
            using var dev = new AnalogKeyboardDevice(Candidate());
            dev.InjectForTest(AnalogKeyCodes.W, 0.75f);
            Assert.Null(dev.GetCurrentState());
            dev.AttachForTest();
            var s = dev.GetCurrentState();
            Assert.Equal(0.75f, s.AnalogKeys.Get(AnalogKeyCodes.W));
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, Src(W, 60), 50));
            dev.Dispose();
            Assert.Null(dev.GetCurrentState());
        }

        [Fact]
        public void ThePicker_ListsTheKeysTheKeyboardReports()
        {
            var wooting = new UserDevice { CapType = InputDeviceType.AnalogKeyboard, VendorId = 0x31E3, ProdId = 0x1232 };
            var choices = MappingDisplayResolver.BuildInputChoices(wooting);
            Assert.Equal(AnalogKeyCodes.FullKeyboard.Select(SourceCoercion.AnalogKeyDescriptor),
                choices.Select(c => c.Descriptor));
            Assert.Equal(MacroAction.VirtualKeyDisplayName(VirtualKey.W),
                choices.Single(c => c.Descriptor == W).DisplayName);

            var tartarus = new UserDevice { CapType = InputDeviceType.AnalogKeyboard, VendorId = 0x1532, ProdId = 0x0244 };
            Assert.Equal(20, MappingDisplayResolver.BuildInputChoices(tartarus).Length);
        }

        [Fact]
        public void KeysWithoutAVirtualKey_HaveTheirOwnNames()
        {
            var s = Strings.Instance;
            Assert.Equal("Fn", MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.Fn));
            Assert.Equal(s.AnalogKey_IntlHash, MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.IntlHash));
            Assert.Equal(s.AnalogKey_IntlBackslash, MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.IntlBackslash));
            Assert.Equal(string.Format(s.AnalogKey_Extra_Format, 4), MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.Oem4));
            Assert.Equal(string.Format(s.AnalogKey_Code_Format, "0x3FF"), MappingDisplayResolver.AnalogKeyDisplayName(0x3FF));
            Assert.Equal(string.Format(s.Key_Numpad, s.Key_Enter), MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.NumpadEnter));
            Assert.Equal(string.Format(s.Key_Numpad, "="), MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.NumpadEqual));
            Assert.Equal(s.AnalogKey_IntlYen, MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.IntlYen));
            Assert.Equal(s.AnalogKey_IntlRo, MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.IntlRo));
            Assert.Equal(s.AnalogKey_Henkan, MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.Henkan));
            Assert.Equal(s.AnalogKey_Hangul, MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.Hangul));
            Assert.Equal(s.AnalogKey_LeftSpace, MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.LeftSpace));
            Assert.Equal(s.AnalogKey_RightFn, MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.RightFn));
            // The SayoDevice O3C's unlabeled keys, and keys known by position.
            Assert.Equal(string.Format(s.AnalogKey_Code_Format, 1), MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.PadKey1));
            Assert.Equal(string.Format(s.AnalogKey_Code_Format, 3), MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.PadKey3));
            Assert.Equal(string.Format(s.AnalogKey_Position_Format, 37),
                MappingDisplayResolver.AnalogKeyDisplayName(AnalogKeyCodes.PositionBase + 37));
        }

        [Fact]
        public void ARouteWithAStalenessWindow_DropsTheKeys_WhenPassesStall()
        {
            // The HallJoy routes whose depths expire by wall clock.
            using var dev = new AnalogKeyboardDevice(Candidate());
            dev.UseRouteForTest(new AnalogKeyboardRoute { Id = "test", StaleAfterMs = 150 });
            dev.AttachForTest();
            dev.InjectForTest(AnalogKeyCodes.W, 0.5f);
            dev.SetLastReportTickForTest(Environment.TickCount64);
            Assert.Equal(0.5f, dev.GetCurrentState().AnalogKeys.Get(AnalogKeyCodes.W));
            dev.SetLastReportTickForTest(Environment.TickCount64 - 1000);
            Assert.Equal(0f, dev.GetCurrentState().AnalogKeys.Get(AnalogKeyCodes.W));

            // A route without one keeps a held key however long it is quiet.
            using var quiet = new AnalogKeyboardDevice(Candidate());
            quiet.UseRouteForTest(new AnalogKeyboardRoute { Id = "test" });
            quiet.AttachForTest();
            quiet.InjectForTest(AnalogKeyCodes.W, 0.5f);
            quiet.SetLastReportTickForTest(Environment.TickCount64 - 60000);
            Assert.Equal(0.5f, quiet.GetCurrentState().AnalogKeys.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void TheAzothRoute_DropsAHeldKey_WhenAPassStallsPastTheReleaseTime()
        {
            // The session renews its lease before its release check, so a
            // renewal write that stalls to its timeout would hold the last key.
            // The route's window is the session's own release time.
            var azoth = OtherRoutes.All
                .Single(r => r.Id == "halljoy-rog-azoth-96-he");
            Assert.Equal(RogAzoth96HeSession.ReleaseMs, azoth.StaleAfterMs);

            using var dev = new AnalogKeyboardDevice(Candidate());
            dev.UseRouteForTest(azoth);
            dev.AttachForTest();
            dev.InjectForTest(AnalogKeyCodes.W, 0.5f);
            dev.SetLastReportTickForTest(Environment.TickCount64);
            Assert.Equal(0.5f, dev.GetCurrentState().AnalogKeys.Get(AnalogKeyCodes.W));
            dev.SetLastReportTickForTest(Environment.TickCount64 - 1000);
            Assert.Equal(0f, dev.GetCurrentState().AnalogKeys.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void KeysAKeyboardReports_JoinItsPickerList()
        {
            // Keys known only by position, and any code a table missed.
            using var dev = new AnalogKeyboardDevice(Candidate());
            dev.UseRouteForTest(new AnalogKeyboardRoute { Id = "test" });
            int before = dev.KeyOrder.Length;
            var pass = new AnalogKeyInputState();
            pass.Set(AnalogKeyCodes.PositionBase + 12, 0.4f);
            pass.Set(AnalogKeyCodes.W, 0.4f);
            dev.NoteKeysForTest(pass);
            Assert.Equal(before + 1, dev.KeyOrder.Length);
            Assert.Equal(AnalogKeyCodes.PositionBase + 12, dev.KeyOrder[^1]);
            var ud = new UserDevice { CapType = InputDeviceType.AnalogKeyboard, VendorId = 0x31E3, ProdId = 0x1232 };
            ud.InstanceGuid = dev.InstanceGuid;
            Assert.Contains(AnalogKeyCodes.PositionBase + 12, AnalogKeyboardRuntime.KeysFor(ud));
            dev.NoteKeysForTest(pass);
            Assert.Equal(before + 1, dev.KeyOrder.Length);
        }

        // ── Macro triggers ──

        [Fact]
        public void AKey_BecomesADescriptorTriggerEntry()
        {
            var choice = new InputChoice { Descriptor = W, DeviceGuid = Guid.NewGuid().ToString() };
            Assert.True(MacroItem.TryBuildTriggerEntry(choice, out var entry));
            Assert.Equal(W, entry.SourceDescriptor);
            Assert.Equal(Guid.Parse(choice.DeviceGuid), entry.DeviceGuid);
        }

        /// <summary>A key's trigger row shows a Deadzone control, and the
        /// value it stamps is the depth the entry fires at, so two entries
        /// on one key are a soft press and a full press.</summary>
        [Fact]
        public void ATriggerEntry_TakesItsOwnThreshold()
        {
            var macro = new MacroItem();
            var soft = new MacroItem.TriggerInputEntry { DeviceGuid = Guid.NewGuid(), SourceDescriptor = W };
            macro.SetTriggerInputEntries(new() { soft });
            var row = Assert.Single(macro.TriggerInputItems);
            Assert.True(row.HasThreshold);
            Assert.False(row.IsAxis);
            Assert.Same(soft, row.ThresholdEntry);

            Assert.Equal(50, soft.DescriptorThresholdPercent);
            soft.DescriptorThresholdPercent = 20;
            Assert.Equal(20, soft.DescriptorDeadZone);
            Assert.Equal(20, soft.DescriptorSource.DeadZone);
            Assert.True(SourceCoercion.EvaluateForButtonTarget(Depth(0.3f), soft.DescriptorSource, 50));

            var full = new MacroItem.TriggerInputEntry { DeviceGuid = soft.DeviceGuid, SourceDescriptor = W, DescriptorDeadZone = 95 };
            Assert.False(SourceCoercion.EvaluateForButtonTarget(Depth(0.3f), full.DescriptorSource, 50));
            Assert.True(SourceCoercion.EvaluateForButtonTarget(Depth(0.97f), full.DescriptorSource, 50));

            // The stamp survives the saved spec, and reset returns to 50.
            var back = MacroItem.TriggerInputEntry.Parse(soft.Spec);
            Assert.Equal(20, back.DescriptorDeadZone);
            soft.ResetDescriptorDeadZoneCommand.Execute(null);
            Assert.Equal(0, soft.DescriptorDeadZone);
            Assert.Equal(50, soft.DescriptorThresholdPercent);
        }

        [Fact]
        public void AnUnthresholdedDescriptor_ShowsNoDeadzone()
        {
            var macro = new MacroItem();
            macro.SetTriggerInputEntries(new()
            {
                new MacroItem.TriggerInputEntry { DeviceGuid = Guid.NewGuid(), SourceDescriptor = "Touchpad 0 Click" },
            });
            Assert.False(Assert.Single(macro.TriggerInputItems).HasThreshold);
        }

        // ── Idle and Remote Link ──

        [Fact]
        public void AMovingKey_IsNotUnchanged()
        {
            Assert.False(IdleInputDetector.IsUnchanged(Depth(0.6f), Depth(0.5f)));
            Assert.True(IdleInputDetector.IsUnchanged(Depth(0.6f), Depth(0.6f)));
            Assert.False(IdleInputDetector.IsUnchanged(Depth(0.6f), new CustomInputState()));
            Assert.True(IdleInputDetector.IsUnchanged(State(), new CustomInputState()));
        }

        private static readonly CustomInputStateCodec.Caps NoSensors = new(gyro: false, accel: false);

        [Fact]
        public void TheKeys_SurviveTheWire()
        {
            var s = State((AnalogKeyCodes.W, 0.25f), (AnalogKeyCodes.PlayPause, 1f), (AnalogKeyCodes.Fn, 0.5f));
            var back = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(s, NoSensors));
            Assert.Equal(3, back.AnalogKeys.Count);
            Assert.Equal(0.25f, back.AnalogKeys.Get(AnalogKeyCodes.W), precision: 4);
            Assert.Equal(1f, back.AnalogKeys.Get(AnalogKeyCodes.PlayPause));
            Assert.Equal(0.5f, back.AnalogKeys.Get(AnalogKeyCodes.Fn), precision: 4);
            Assert.True(CustomInputStateCodec.Encode(s, NoSensors).Length <= CustomInputStateCodec.MaxEncodedSize(s, NoSensors));
        }

        /// <summary>No key down is the neutral, so it costs no bytes, and a
        /// frame without the block clears a stale set.</summary>
        [Fact]
        public void NoKeyDown_AddsNoBytes_AndClearsAStaleSet()
        {
            int empty = CustomInputStateCodec.Encode(new CustomInputState(), NoSensors).Length;
            Assert.Equal(empty, CustomInputStateCodec.Encode(State(), NoSensors).Length);
            // Magic, the tail mask, the count, then a code and a depth.
            Assert.Equal(empty + 3 + 1 + 4, CustomInputStateCodec.Encode(Depth(0.5f), NoSensors).Length);

            var target = Depth(1f);
            Assert.True(CustomInputStateCodec.DecodeInto(CustomInputStateCodec.Encode(new CustomInputState(), NoSensors), target));
            Assert.Equal(0, target.AnalogKeys.Count);
        }

        [Fact]
        public void AHostileCount_FailsClosed()
        {
            byte[] frame = CustomInputStateCodec.Encode(Depth(0.5f), NoSensors);
            // The count byte sits before the frame's last four bytes.
            frame[^5] = AnalogKeyInputState.MaxKeys + 1;
            var target = Depth(0.9f);
            Assert.False(CustomInputStateCodec.DecodeInto(frame, target));
            Assert.Equal(0, target.AnalogKeys.Count);

            byte[] cut = CustomInputStateCodec.Encode(Depth(0.5f), NoSensors)[..^2];
            Assert.False(CustomInputStateCodec.DecodeInto(cut, target));
            Assert.Equal(0, target.AnalogKeys.Count);
        }

        // ── Settings ──

        [Fact]
        public void TheSwitch_IsOffByDefault_ResetsOff_AndPersists()
        {
            var vm = new SettingsViewModel();
            Assert.False(vm.AnalogKeyboardsEnabled);
            vm.AnalogKeyboardsEnabled = true;
            vm.ResetSettingCommand.Execute(nameof(SettingsViewModel.AnalogKeyboardsEnabled));
            Assert.False(vm.AnalogKeyboardsEnabled);

            string mainWindow = RepoFile("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("nameof(SettingsViewModel.AnalogKeyboardsEnabled)", mainWindow);
            string settings = RepoFile("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("vm.AnalogKeyboardsEnabled = appSettings.AnalogKeyboardsEnabled", settings);
            Assert.Contains("AnalogKeyboardsEnabled = vm.AnalogKeyboardsEnabled", settings);
        }

        /// <summary>The switch sits in the Settings page's Input Engine card
        /// beside the G-keys switch, the vendor input paths the engine can
        /// read, and not on the Dashboard.</summary>
        [Fact]
        public void TheSwitch_SitsInTheInputEngineCard()
        {
            string page = RepoFile("PadForge.App", "Views", "SettingsPage.xaml");
            int engine = page.IndexOf("Binding Settings_InputEngine,", StringComparison.Ordinal);
            int analog = page.IndexOf("Binding Settings_AnalogKeyboards,", StringComparison.Ordinal);
            Assert.True(engine > 0 && analog > engine, "the analog keyboards row is not inside the Input Engine card");
            Assert.Contains("CommandParameter=\"AnalogKeyboardsEnabled\"", page);
            Assert.DoesNotContain("AnalogKeyboards", RepoFile("PadForge.App", "Views", "DashboardPage.xaml"));
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
