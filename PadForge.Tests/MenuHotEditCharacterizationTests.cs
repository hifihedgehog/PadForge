using System;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    // These characterize the pending-release behavior before adjudication.
    // A passing repro records the behavior being questioned, not a fixed contract.
    [Collection("SettingsManagerStatics")]
    [Trait("Audit", "MenuHotEditSupplement")]
    public class MenuHotEditCharacterizationTests
    {
        private readonly ITestOutputHelper _output;
        public MenuHotEditCharacterizationTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(MenuFireType.TouchRelease)]
        [InlineData(MenuFireType.ClickRelease)]
        public void Repro_KindIndexEditCommitsTheOldPendingIndex(MenuFireType fire)
        {
            using var f = new Rig(fire);
            var pending = f.Arm();
            int frames = f.Frames;
            f.Editor.KindIndex = 1;
            Assert.Equal(MenuKind.Grid, f.Menu.Kind);
            Assert.Equal(1, f.Edits);
            Assert.Equal(1, f.StructuralEdits);
            Assert.Equal(frames, f.Frames);
            Assert.Same(pending, f.Context);
            Assert.True(pending.State.PhysicalEngaged);
            Assert.Equal(-1, pending.State.PulsedIndex);

            // No active input sample occurs between the edit and this release.
            f.ReleaseWithoutSampling();
            f.Frame();
            Report("KindIndex", f, pending);
            Assert.Same(pending, f.Context);
            Assert.Equal(2, f.Context.State.PulsedIndex);
            Assert.False(f.Context.State.PhysicalEngaged);
            Assert.Single(f.Keys);
            Assert.Contains(Rig.OldKey, f.Keys);
        }

        [Theory]
        [InlineData(MenuFireType.TouchRelease)]
        [InlineData(MenuFireType.ClickRelease)]
        public void Repro_SelectedKeyVkEditSendsTheNewKeyFromTheOldRelease(MenuFireType fire)
        {
            using var f = new Rig(fire);
            var pending = f.Arm();
            int frames = f.Frames;
            f.Cell.SelectedKeyVk = Rig.NewKey;
            Assert.Equal((int)Rig.NewKey, f.Cell.SelectedKeyVk);
            Assert.Equal(1, f.Edits);
            Assert.Equal(0, f.StructuralEdits);
            Assert.Equal(frames, f.Frames);
            Assert.Same(pending, f.Context);
            Assert.Equal(-1, pending.State.PulsedIndex);

            f.ReleaseWithoutSampling();
            f.Frame();
            Report("SelectedKeyVk", f, pending);
            Assert.Same(pending, f.Context);
            Assert.Equal(2, f.Context.State.PulsedIndex);
            Assert.Single(f.Keys);
            Assert.Contains(Rig.NewKey, f.Keys);
            Assert.DoesNotContain(Rig.OldKey, f.Keys);
        }

        [Theory]
        [InlineData(MenuFireType.TouchRelease, false)]
        [InlineData(MenuFireType.TouchRelease, true)]
        [InlineData(MenuFireType.ClickRelease, false)]
        [InlineData(MenuFireType.ClickRelease, true)]
        public void Control_WithoutAnOldInteractionTheSameEditDoesNotCommit(MenuFireType fire, bool kindEdit)
        {
            using var f = new Rig(fire);
            Assert.True(f.Context.State.Engaged, "the layer-held menu must already be open");
            Assert.False(f.Context.State.PhysicalEngaged);
            Assert.False(f.Context.State.Clicked);
            Assert.Empty(f.Keys);
            f.ApplyEdit(kindEdit);
            f.ReleaseWithoutSampling();
            f.Frame();
            Assert.Equal(-1, f.Context.State.PulsedIndex);
            Assert.Empty(f.Keys);
        }

        [Theory]
        [InlineData(MenuFireType.TouchRelease, false)]
        [InlineData(MenuFireType.TouchRelease, true)]
        [InlineData(MenuFireType.ClickRelease, false)]
        [InlineData(MenuFireType.ClickRelease, true)]
        public void Control_ExistingCancellationRemovesOldIntentAndAllowsNewInput(MenuFireType fire, bool kindEdit)
        {
            using var f = new Rig(fire);
            var pending = f.Arm();
            f.ApplyEdit(kindEdit);
            f.Manager.ClearMenuRuntimeForSlot(0);
            f.ReleaseWithoutSampling();
            f.Frame();
            Assert.NotSame(pending, f.Context);
            Assert.Equal(-1, f.Context.State.PulsedIndex);
            Assert.Empty(f.Keys);

            // The edited menu still works. A 2x2 grid selects its lower-right
            // cell from this vector. The radial selects its right-hand cell.
            f.PressAndDeflect();
            f.Frame();
            int freshIndex = kindEdit ? 3 : 2;
            Assert.Equal(freshIndex, f.Context.State.HoveredIndex);
            Assert.True(f.Context.State.PhysicalEngaged);
            f.ReleaseWithoutSampling();
            f.Frame();
            Assert.Equal(freshIndex, f.Context.State.PulsedIndex);
            Assert.Single(f.Keys);
            Assert.Contains(kindEdit ? (ushort)0x44 : Rig.NewKey, f.Keys);
        }

        [Theory]
        [InlineData(MenuFireType.TouchRelease, false)]
        [InlineData(MenuFireType.TouchRelease, true)]
        [InlineData(MenuFireType.ClickRelease, false)]
        [InlineData(MenuFireType.ClickRelease, true)]
        public void Control_LabelAndIconEditsPreserveThePendingGesture(MenuFireType fire, bool iconEdit)
        {
            using var f = new Rig(fire);
            var pending = f.Arm();
            if (iconEdit)
            {
                f.Cell.SetIcon("ghost_050_menu_0030.png");
                Assert.Equal("ghost_050_menu_0030.png", f.Cell.IconName);
            }
            else
            {
                f.Cell.Label = "Edited caption";
                Assert.Equal("Edited caption", f.Cell.Label);
            }
            Assert.Equal(1, f.Edits);
            Assert.Equal(0, f.StructuralEdits);
            Assert.Equal((int)Rig.OldKey, f.Cell.SelectedKeyVk);
            Assert.Same(pending, f.Context);
            f.ReleaseWithoutSampling();
            f.Frame();
            Report(iconEdit ? "Icon control" : "Label control", f, pending);
            Assert.Same(pending, f.Context);
            Assert.Equal(2, f.Context.State.PulsedIndex);
            Assert.Single(f.Keys);
            Assert.Contains(Rig.OldKey, f.Keys);
        }

        [Theory]
        [InlineData(MenuFireType.TouchRelease)]
        [InlineData(MenuFireType.ClickRelease)]
        public void Control_CancellationDoesNotSuppressAFreshHeldSample(MenuFireType fire)
        {
            using var f = new Rig(fire);
            var pending = f.Arm();
            f.Manager.ClearMenuRuntimeForSlot(0);
            f.Frame();
            Assert.NotSame(pending, f.Context);
            Assert.True(f.Context.State.PhysicalEngaged);
            Assert.Equal(-1, f.Context.State.PulsedIndex);
            f.ReleaseWithoutSampling();
            f.Frame();
            Assert.Equal(2, f.Context.State.PulsedIndex);
            Assert.Single(f.Keys);
            Assert.Contains(Rig.OldKey, f.Keys);
        }

        private void Report(string edit, Rig f, InputManager.MenuTickContext pending)
        {
            _output.WriteLine("edit={0} fire={1} kind={2} sameContext={3} pulsedIndex={4} physical={5} clicked={6} keys=[{7}] frameMs={8}",
                edit, f.Menu.FireType, f.Menu.Kind, ReferenceEquals(pending, f.Context),
                f.Context.State.PulsedIndex, f.Context.State.PhysicalEngaged, f.Context.State.Clicked,
                string.Join(",", f.Keys.Select(k => k.ToString("X2"))), f.FrameElapsedMs);
        }

        private sealed class Rig : IDisposable
        {
            public const ushort OldKey = 0x43;
            public const ushort NewKey = 0x5A;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
            private readonly bool[] _created = SettingsManager.SlotCreated;
            private readonly bool[] _enabled = SettingsManager.SlotEnabled;
            private readonly bool _stale = InputService.VmMappingsStale;
            private readonly bool _suppress = InputService.SuppressMappingEditPush;
            private static readonly FieldInfo EngineField = typeof(InputService).GetField("_inputManagerStatic", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("InputService engine field is unavailable");
            private readonly object _engine = EngineField.GetValue(null);
            private readonly Action _collect;
            private PadViewModel _pad;
            public InputManager Manager { get; } = new();
            public MenuDefinitionEntry Menu { get; }
            public UserDevice Device { get; }
            public CustomInputState State { get; } = new();
            public MenuEditorItem Editor { get; }
            public MenuCellItem Cell => Editor.Cells.Single(c => c.Index == 2);
            public InputManager.MenuTickContext Context => Manager.MenuContexts[(0, Device.InstanceGuid, 1)];
            public ushort[] Keys { get; private set; } = Array.Empty<ushort>();
            public int Edits { get; private set; }
            public int StructuralEdits { get; private set; }
            public int Frames { get; private set; }
            public long FrameElapsedMs { get; private set; }

            public Rig(MenuFireType fire)
            {
                try
                {
                    SettingsManager.UserSettings = new SettingsCollection();
                    SettingsManager.UserDevices = new DeviceCollection();
                    SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                    SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                    SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
                    SettingsManager.SlotCreated[0] = SettingsManager.SlotEnabled[0] = true;
                    InputService.VmMappingsStale = false;
                    InputService.SuppressMappingEditPush = false;
                    EngineField.SetValue(null, Manager);
                    InputManager.ClearAllShiftRuntime();
                    var set = new MappingSet();
                    set.ShiftActivators.Add(new ShiftActivator
                    {
                        LayerMask = "L1", LayerName = "Layer", Mode = "Hold", Descriptor = "Button 0",
                    });
                    Menu = new MenuDefinitionEntry
                    {
                        MenuId = 1, Kind = MenuKind.Radial, CellCount = 4, HasCenter = true,
                        HostDescriptor = "Gamepad RightStick", ClickDescriptor = "Button 1",
                        LayerMask = "L1", LayerHoldsOpen = true, FireType = fire, EngageDeadzonePercent = 25,
                    };
                    for (int i = 0; i <= 4; i++)
                        Menu.Items.Add(new MenuItemDefinition { Index = i, VirtualKey = 0x41 + i, Label = "Cell " + i });
                    set.Menus.Add(Menu);
                    SettingsManager.SlotMappingSets[0] = set;
                    Device = new UserDevice
                    {
                        InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.Gamepad,
                        CapButtonCount = 16, IsOnline = true, InputState = State,
                    };
                    SettingsManager.UserDevices.Items.Add(Device);
                    SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = Device.InstanceGuid, MapTo = 0 });
                    _pad = new PadViewModel(0);
                    _pad.ConfigItemDirtyCallback = () => Edits++;
                    _pad.MenusStructureChanged = () =>
                    {
                        StructuralEdits++;
                        _pad.RefreshMenuInputChoices();
                    };
                    _pad.RebuildLayerTabs(set.ShiftActivators);
                    _pad.ReloadMenus();
                    Editor = Assert.Single(_pad.Menus);
                    Assert.True(Cell.ShowKeyPicker);
                    _collect = (typeof(InputManager).GetMethod("CollectMenuDirectOutputs", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("Menu output collector is unavailable")).CreateDelegate<Action>(Manager);
                    InputManager.ApplyMacroLayerSwitch(0, "L1");
                    ReleaseWithoutSampling();
                    Frame();
                    Assert.True(Context.State.Engaged);
                    Assert.Empty(Keys);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public InputManager.MenuTickContext Arm()
            {
                PressAndDeflect();
                Frame();
                Assert.True(Context.State.Engaged);
                Assert.True(Context.State.PhysicalEngaged);
                Assert.Equal(Menu.FireType == MenuFireType.ClickRelease, Context.State.Clicked);
                Assert.Equal(2, Context.State.HoveredIndex);
                Assert.Equal(-1, Context.State.PulsedIndex);
                Assert.Empty(Keys);
                return Context;
            }

            public void ApplyEdit(bool kindEdit)
            {
                if (kindEdit) Editor.KindIndex = 1;
                else Cell.SelectedKeyVk = NewKey;
            }

            public void PressAndDeflect()
            {
                State.Axis[3] = 65535;
                State.Axis[4] = 32768;
                State.Buttons[1] = Menu.FireType == MenuFireType.ClickRelease;
            }

            public void ReleaseWithoutSampling()
            {
                State.Axis[3] = State.Axis[4] = 32768;
                State.Buttons[1] = false;
            }

            public void Frame()
            {
                long start = Environment.TickCount64;
                SourceCoercion.BeginPollFrame();
                Device.InputStateSeq++;
                Manager.UpdateMenuContexts(Device, State);
                Manager._desiredLatchedKeys.Clear();
                _collect();
                Keys = Manager._desiredLatchedKeys.ToArray();
                Frames++;
                FrameElapsedMs = Environment.TickCount64 - start;
            }

            public void Dispose()
            {
                // The application keeps pad view models for its lifetime.
                // This fixture removes only its own two strong subscriptions.
                if (_pad != null)
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    HMaestroProfileCatalog.CatalogReloaded -= typeof(PadViewModel)
                        .GetMethod("OnCatalogReloaded", flags).CreateDelegate<EventHandler>(_pad);
                    MacroItem.Renamed -= typeof(PadViewModel)
                        .GetMethod("OnMacroRenamed", flags).CreateDelegate<Action<MacroItem, string, string>>(_pad);
                    _pad.ConfigItemDirtyCallback = null;
                    _pad.MenusStructureChanged = null;
                    _pad = null;
                }
                EngineField.SetValue(null, _engine);
                InputManager.ClearAllShiftRuntime();
                InputService.VmMappingsStale = _stale;
                InputService.SuppressMappingEditPush = _suppress;
                SettingsManager.UserSettings = _settings;
                SettingsManager.UserDevices = _devices;
                SettingsManager.SlotMappingSets = _sets;
                SettingsManager.SlotCreated = _created;
                SettingsManager.SlotEnabled = _enabled;
            }
        }
    }
}
