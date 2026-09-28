using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #461: Toggle as a Primary Mode. The row reads its own input, as
    /// Direct does, and latches it: one press holds the output on and the
    /// next press releases it.
    /// </summary>
    public class ToggleSourceKindTests
    {
        private static MappingSource Toggle(string descriptor = "Button 0", bool invert = false)
            => new() { Kind = "Toggle", Descriptor = descriptor, Invert = invert };

        private static CustomInputState Buttons(bool down)
        {
            var s = new CustomInputState();
            s.Buttons[0] = down;
            return s;
        }

        private static bool ReadButton(SourceKindRuntime rt, MappingSource src, bool down,
            string target = "ButtonA", int slot = 0)
            => SourceEvaluator.EvaluateForButtonTarget(Buttons(down), src, 50, slot, target, 0, rt, 0.001);

        [Fact]
        public void PressLatchesOnAndTheNextPressReleases()
        {
            var rt = new SourceKindRuntime();
            var src = Toggle();
            bool[] input =    { false, true, true, false, false, true,  false };
            bool[] expected = { false, true, true, true,  true,  false, false };
            for (int f = 0; f < input.Length; f++)
            {
                rt.FrameSeq++;
                Assert.Equal(expected[f], ReadButton(rt, src, input[f]));
            }
        }

        [Fact]
        public void PressAlreadyDownOnTheFirstReadDoesNotLatch()
        {
            var rt = new SourceKindRuntime();
            var src = Toggle();
            rt.FrameSeq++;
            Assert.False(ReadButton(rt, src, true));
            rt.FrameSeq++;
            Assert.False(ReadButton(rt, src, true));
            rt.FrameSeq++;
            Assert.False(ReadButton(rt, src, false));
            rt.FrameSeq++;
            Assert.True(ReadButton(rt, src, true));
        }

        [Fact]
        public void TwoDeviceReadsInOneFrameFlipOnce()
        {
            // An any-device source is read once per device on the slot.
            var rt = new SourceKindRuntime();
            var src = Toggle();
            rt.FrameSeq++;
            ReadButton(rt, src, false);
            ReadButton(rt, src, false);

            rt.FrameSeq++;
            Assert.False(ReadButton(rt, src, false)); // first device idle
            Assert.True(ReadButton(rt, src, true));   // second device presses

            rt.FrameSeq++;
            Assert.True(ReadButton(rt, src, false));  // latched on the first device's read
            Assert.True(ReadButton(rt, src, true));   // same press, no second flip

            rt.FrameSeq++;
            ReadButton(rt, src, false);
            ReadButton(rt, src, false);

            rt.FrameSeq++;
            Assert.False(ReadButton(rt, src, true));  // both press together: one flip, off
            Assert.False(ReadButton(rt, src, true));
        }

        [Fact]
        public void FrameWithoutAReadReleasesAndAHeldPressDoesNotRelatch()
        {
            var rt = new SourceKindRuntime();
            var src = Toggle();
            rt.FrameSeq++;
            ReadButton(rt, src, false);
            rt.FrameSeq++;
            Assert.True(ReadButton(rt, src, true));
            rt.FrameSeq++;
            Assert.True(ReadButton(rt, src, false));

            // The row goes unread for a frame (its shift layer closed).
            rt.FrameSeq += 2;
            Assert.False(ReadButton(rt, src, false));

            // Latch again, then skip a frame with the button held down.
            rt.FrameSeq++;
            Assert.True(ReadButton(rt, src, true));
            rt.FrameSeq += 2;
            Assert.False(ReadButton(rt, src, true));
            rt.FrameSeq++;
            Assert.False(ReadButton(rt, src, true));
            rt.FrameSeq++;
            Assert.False(ReadButton(rt, src, false));
            rt.FrameSeq++;
            Assert.True(ReadButton(rt, src, true));
        }

        [Fact]
        public void TriggerTargetLatchesFullPullOnlyPastTheThreshold()
        {
            var rt = new SourceKindRuntime();
            var direct = new MappingSource { Descriptor = "Axis 2" };
            var src = new MappingSource { Kind = "Toggle", Descriptor = "Axis 2" };
            var light = new CustomInputState();
            light.Axis[2] = 16000;
            var deep = new CustomInputState();
            deep.Axis[2] = 60000;
            var rest = new CustomInputState();
            float lightPull = SourceCoercion.EvaluateForTriggerTarget(light, direct);
            float deepPull = SourceCoercion.EvaluateForTriggerTarget(deep, direct);
            Assert.InRange(lightPull, 0.01f, 0.49f);
            Assert.InRange(deepPull, 0.51f, 1f);

            float Read(CustomInputState s)
                => SourceEvaluator.EvaluateForTriggerTarget(s, src, 0, "LeftTrigger", 0, rt, 0.001);

            rt.FrameSeq++;
            Assert.Equal(0f, Read(rest));
            rt.FrameSeq++;
            Assert.Equal(0f, Read(light));
            rt.FrameSeq++;
            Assert.Equal(0f, Read(rest));
            rt.FrameSeq++;
            Assert.Equal(1f, Read(deep));
            rt.FrameSeq++;
            Assert.Equal(1f, Read(rest));
            rt.FrameSeq++;
            Assert.Equal(0f, Read(deep));
        }

        [Fact]
        public void AxisTargetHoldsFullScaleInThePressedDirection()
        {
            var rt = new SourceKindRuntime();
            float Read(MappingSource s, bool down)
                => SourceEvaluator.EvaluateForBipolarAxisTarget(Buttons(down), s, 0, "LeftThumbAxisX", 0, rt, 0.001);

            var plain = Toggle();
            rt.FrameSeq++;
            Assert.Equal(0f, Read(plain, false));
            rt.FrameSeq++;
            Assert.Equal(1f, Read(plain, true));
            rt.FrameSeq++;
            Assert.Equal(1f, Read(plain, false));

            // An inverted button is the negative side of a stick, so its
            // toggle holds the stick at the negative end.
            rt.Clear();
            var inverted = Toggle(invert: true);
            rt.FrameSeq++;
            Assert.Equal(0f, Read(inverted, false));
            rt.FrameSeq++;
            Assert.Equal(-1f, Read(inverted, true));
            rt.FrameSeq++;
            Assert.Equal(-1f, Read(inverted, false));
        }

        [Fact]
        public void AnalogStickPressLatchesTheSideItWasPushedTo()
        {
            var rt = new SourceKindRuntime();
            var src = new MappingSource { Kind = "Toggle", Descriptor = "Axis 0" };
            CustomInputState Stick(int raw)
            {
                var s = new CustomInputState();
                Array.Fill(s.Axis, 32768);
                s.Axis[0] = raw;
                return s;
            }
            float Read(int raw)
                => SourceEvaluator.EvaluateForBipolarAxisTarget(Stick(raw), src, 0, "LeftThumbAxisX", 0, rt, 0.001);

            rt.FrameSeq++;
            Assert.Equal(0f, Read(32768));
            rt.FrameSeq++;
            Assert.Equal(-1f, Read(0));       // pushed left: held full left
            rt.FrameSeq++;
            Assert.Equal(-1f, Read(32768));   // released to center: still held
            rt.FrameSeq++;
            Assert.Equal(0f, Read(65535));    // pushed again, either side: released
            rt.FrameSeq++;
            Assert.Equal(0f, Read(32768));
        }

        [Fact]
        public void NoRuntimeReadsRestAndLatchesNothing()
        {
            // The UI previews pass no runtime so they can never advance a
            // latch the polling thread owns.
            var src = Toggle();
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(Buttons(true), src, 50, 0, "ButtonA", 0, null, 0));
            Assert.Equal(0f, SourceEvaluator.EvaluateForTriggerTarget(Buttons(true), src, 0, "LeftTrigger", 0, null, 0));
            Assert.Equal(0f, SourceEvaluator.EvaluateForBipolarAxisTarget(Buttons(true), src, 0, "LeftThumbAxisX", 0, null, 0));
        }

        [Fact]
        public void ResetsDropOnlyTheirOwnLatches()
        {
            var rt = new SourceKindRuntime();
            var src = Toggle();

            void LatchBoth()
            {
                rt.Clear();
                rt.FrameSeq++;
                ReadButton(rt, src, false, "ButtonA", 0);
                ReadButton(rt, src, false, "ButtonB", 0);
                rt.FrameSeq++;
                Assert.True(ReadButton(rt, src, true, "ButtonA", 0));
                Assert.True(ReadButton(rt, src, true, "ButtonB", 0));
            }

            LatchBoth();
            rt.ResetForSlot(1);
            rt.FrameSeq++;
            Assert.True(ReadButton(rt, src, false, "ButtonA", 0));

            LatchBoth();
            rt.ResetForSlot(0);
            rt.FrameSeq++;
            Assert.False(ReadButton(rt, src, false, "ButtonA", 0));
            Assert.False(ReadButton(rt, src, false, "ButtonB", 0));

            LatchBoth();
            rt.ResetForRow(0, "ButtonA");
            rt.FrameSeq++;
            Assert.False(ReadButton(rt, src, false, "ButtonA", 0));
            Assert.True(ReadButton(rt, src, false, "ButtonB", 0));

            LatchBoth();
            rt.Clear();
            rt.FrameSeq++;
            Assert.False(ReadButton(rt, src, false, "ButtonA", 0));
        }

        [Fact]
        public void KindHelpersSortToggleWithTheDescriptorKinds()
        {
            Assert.True(SourceEvaluator.IsToggleKind(Toggle()));
            Assert.False(SourceEvaluator.IsToggleKind(new MappingSource()));
            Assert.False(SourceEvaluator.IsToggleKind(null));

            Assert.True(SourceEvaluator.IsDescriptorKind(null));
            Assert.True(SourceEvaluator.IsDescriptorKind(""));
            Assert.True(SourceEvaluator.IsDescriptorKind("Direct"));
            Assert.True(SourceEvaluator.IsDescriptorKind("Toggle"));
            Assert.False(SourceEvaluator.IsDescriptorKind("Incremental"));
            Assert.False(SourceEvaluator.IsDescriptorKind("Ramped"));
            Assert.False(SourceEvaluator.IsDescriptorKind("InvertOnHold"));

            // A Toggle with no input is a blank position, like a blank Direct.
            Assert.True(SourceEvaluator.IsUnmappedDirect(new MappingSource { Kind = "Toggle" }));
            Assert.True(SourceEvaluator.IsUnmappedDirect(new MappingSource()));
            Assert.False(SourceEvaluator.IsUnmappedDirect(Toggle()));
            Assert.False(SourceEvaluator.IsUnmappedDirect(new MappingSource { Kind = "Incremental" }));
        }

        [Fact]
        public void KindPickerOffersToggleRightAfterDirect()
        {
            var options = MappingSourceItem.KindOptions;
            Assert.Equal("Direct", options[0].Value);
            Assert.Equal("Toggle", options[1].Value);
            Assert.Equal(Strings.Instance.Pad_Mapping_Kind_Toggle, options[1].Name);
        }

        [Fact]
        public void ToggleRowKeepsTheSourcePickerAndTheFullEditingRow()
        {
            var item = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            item.LoadDescriptor("Button 0");
            Assert.True(item.IsTrivialDirect);

            item.PrimaryKindSource.Kind = "Toggle";
            Assert.True(item.IsPrimaryDescriptor);
            Assert.False(item.IsPrimaryDirect);
            Assert.False(item.IsMultiSource);
            Assert.False(item.IsTrivialDirect);
            Assert.True(item.HasAnySource);

            item.PrimaryKindSource.Kind = "Incremental";
            Assert.False(item.IsPrimaryDescriptor);
        }

        [Fact]
        public void LoadingAToggleSourceKeepsOnlyTheKindOnTheHolder()
        {
            var item = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            item.PrimaryKindSource.ParamUp = "Button 4";
            item.LoadPrimaryKind(new MappingSource { Kind = "Toggle", Descriptor = "Button 0", ParamUp = "Button 7" });
            Assert.Equal("Toggle", item.PrimaryKindSource.Kind);
            Assert.Equal("", item.PrimaryKindSource.ParamUp);

            item.LoadPrimaryKind(null);
            Assert.Equal("Direct", item.PrimaryKindSource.Kind);
        }
    }

    [Collection("CultureSwitching")]
    public class ToggleSourceKindCultureTests
    {
        [Theory]
        [InlineData("en", "Toggle")]
        [InlineData("de", "Umschalten")]
        [InlineData("es", "Alternar")]
        [InlineData("fr", "Bascule")]
        [InlineData("it", "Alterna")]
        [InlineData("ja", "トグル")]
        [InlineData("ko", "토글")]
        [InlineData("nl", "Schakelen")]
        [InlineData("pt-BR", "Alternar")]
        [InlineData("zh-Hans", "切换")]
        public void ToggleChoiceIsNamedInEveryLanguage(string culture, string expected)
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo(culture));
                Assert.Equal(expected, MappingSourceItem.KindOptions[1].Name);
                Assert.Contains(expected, Strings.Instance.Pad_Mapping_Kind_Tooltip);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }
    }

    [Collection("SettingsManagerStatics")]
    public class ToggleSourceKindSlotTests : IDisposable
    {
        private const int Slot = 0;
        private static readonly Guid PadA = new("d4610001-0000-4000-8000-000000000001");
        private static readonly Guid PadB = new("d4610002-0000-4000-8000-000000000002");

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

        public ToggleSourceKindSlotTests()
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

        /// <summary>One polling frame the way UpdateOutputStates runs it:
        /// the frame begins, then every device on the slot takes its pass,
        /// and Step 4 ORs the passes' buttons.</summary>
        private bool Frame(MappingSet set, bool aDown, bool bDown)
        {
            _stateA.Buttons[0] = aDown;
            _stateB.Buttons[0] = bDown;
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
        public void AnyDeviceToggleInAMultiSourceRowHearsTheSecondDevice()
        {
            // The multi-source walk used to stop at the first device that
            // answered true. A latched toggle always answers true on the
            // first device, so the second device's press never reached it.
            var set = OneRow(
                new MappingSource { Kind = "Toggle", DeviceGuid = "", Descriptor = "Button 0" },
                new MappingSource { DeviceGuid = "", Descriptor = "Button 12" });

            Assert.False(Frame(set, false, false));
            Assert.True(Frame(set, false, true));    // B presses: latched
            Assert.True(Frame(set, false, false));
            Assert.True(Frame(set, false, true));    // B presses again: releases
            Assert.False(Frame(set, false, false));
            Assert.True(Frame(set, true, false));    // A latches it
            Assert.True(Frame(set, false, false));
        }

        [Fact]
        public void AnyDeviceToggleInASingleSourceRowFollowsEitherDevice()
        {
            var set = OneRow(new MappingSource { Kind = "Toggle", DeviceGuid = "", Descriptor = "Button 0" });

            Assert.False(Frame(set, false, false));
            Assert.True(Frame(set, false, true));
            Assert.True(Frame(set, false, false));
            Frame(set, false, true);                 // the release lands within one frame
            Assert.False(Frame(set, false, false));
            Assert.True(Frame(set, true, false));
            Assert.True(Frame(set, false, false));
        }

        [Fact]
        public void PinnedToggleIgnoresTheOtherDevice()
        {
            var set = OneRow(new MappingSource { Kind = "Toggle", DeviceGuid = PadA.ToString(), Descriptor = "Button 0" });

            Assert.False(Frame(set, false, false));
            Assert.False(Frame(set, false, true));
            Assert.True(Frame(set, true, false));
            Assert.True(Frame(set, false, true));
            Assert.True(Frame(set, false, false));
        }

        [Fact]
        public void LegacyMergeKeepsAnAnyDeviceToggleRowAsAuthored()
        {
            // An any-device Direct row already covers a newly assigned
            // device, so the merge adds no per-device default beside it. An
            // any-device Toggle covers them the same way.
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var setting = SettingsManager.FindSettingByInstanceGuidAndSlot(PadA, Slot);
                setting.GetPadSetting().ButtonA = "Button 0";
                var toggle = new MappingSource { Kind = "Toggle", DeviceGuid = "", Descriptor = "Button 0" };
                SettingsManager.SlotMappingSets[Slot] = new MappingSet
                {
                    Rows = new() { new MappingRow { Target = "ButtonA", LayerMask = "Base", Sources = new() { toggle } } },
                };
                SettingsService.RefreshMappingSetsFromLegacy();
                var row = SettingsManager.SlotMappingSets[Slot].Rows.Single(r => r.Target == "ButtonA");
                Assert.Same(toggle, Assert.Single(row.Sources));
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
        public void ToggleRowSurvivesTheEditorAndASaveReload()
        {
            var service = new SettingsService(_vm);
            OneRow(new MappingSource { Kind = "Toggle", DeviceGuid = PadA.ToString(), Descriptor = "Button 3" });

            var item = Hydrate();
            Assert.Equal("Button 3", item.SourceDescriptor);
            Assert.Equal("Toggle", item.PrimaryKindSource.Kind);
            Assert.True(item.IsPrimaryDescriptor);

            item = SaveReload(service);
            var saved = Assert.Single(SettingsManager.SlotMappingSets[Slot].Rows.Single(r => r.Target == "ButtonA").Sources);
            Assert.Equal("Toggle", saved.Kind);
            Assert.Equal("Button 3", saved.Descriptor);
            Assert.Equal(PadA.ToString(), saved.DeviceGuid);
            Assert.Equal("Button 3", item.SourceDescriptor);
            Assert.Equal("Toggle", item.PrimaryKindSource.Kind);

            // Back to Direct in the picker saves a plain Direct source.
            item.PrimaryKindSource.Kind = "Direct";
            item = SaveReload(service);
            saved = Assert.Single(SettingsManager.SlotMappingSets[Slot].Rows.Single(r => r.Target == "ButtonA").Sources);
            Assert.Equal("Direct", saved.Kind);
            Assert.Equal("Button 3", saved.Descriptor);
            Assert.Equal("Direct", item.PrimaryKindSource.Kind);

            // And Direct to Toggle keeps the input the row already had.
            item.PrimaryKindSource.Kind = "Toggle";
            SaveReload(service);
            saved = Assert.Single(SettingsManager.SlotMappingSets[Slot].Rows.Single(r => r.Target == "ButtonA").Sources);
            Assert.Equal("Toggle", saved.Kind);
            Assert.Equal("Button 3", saved.Descriptor);
        }
    }
}
