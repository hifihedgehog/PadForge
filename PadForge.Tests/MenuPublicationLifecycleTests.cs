using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public class MenuPublicationLifecycleTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReloadWritebackDoesNotWaitForPublicationWhileHoldingADataLock(bool devicesLock)
        {
            using var f = new MenuAuditFixture();
            f.HoldLayerAndDeflect();
            using var entered = new ManualResetEventSlim();
            object dataLock = devicesLock ? SettingsManager.UserDevices.SyncRoot : SettingsManager.UserSettings.SyncRoot;
            var pad = new PadViewModel(0);
            Task poll = null;
            try
            {
                lock (dataLock)
                {
                    poll = Task.Run(() =>
                    {
                        using (InputManager.EnterMenuPublication())
                        {
                            entered.Set();
                            lock (dataLock) { }
                            f.Frame();
                        }
                    });
                    Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                    pad.ReloadMenus();
                    var editor = pad.Menus.Single();
                    int edits = 0;
                    editor.Changed += () => edits++;
                    editor.Enabled = false;
                    Assert.True(f.Menu.Enabled);
                    Assert.Equal(0, edits);
                }
            }
            finally
            {
                if (poll != null) await poll.WaitAsync(TimeSpan.FromSeconds(5));
            }
            pad.SelectedMenu.Enabled = false;
            Assert.False(f.Menu.Enabled);
            Assert.Empty(f.Manager.MenuContexts);
            Assert.Empty(f.Drivers);
        }

        [Fact]
        public void PublicationRejectsAReverseSettingsLockOrder()
        {
            using var f = new MenuAuditFixture();
            lock (SettingsManager.UserSettings.SyncRoot)
                Assert.Throws<InvalidOperationException>(() =>
                {
                    using var publication = InputManager.EnterMenuPublication();
                });
            using (InputManager.EnterMenuPublication())
            {
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    using var nested = InputManager.EnterMenuPublication();
                    Assert.True(Monitor.IsEntered(InputManager.MenuPublicationSync));
                }
            }
        }

        [Fact]
        public void UiEditsRejectAReverseVirtualControllerLockOrder()
        {
            using var f = new MenuAuditFixture();
            var lifecycle = typeof(InputManager).GetField("_vcLifecycleLock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.Manager);
            bool changed = false;
            lock (lifecycle)
                Assert.Throws<InvalidOperationException>(() => InputService.EditMenuConfiguration(0, () => changed = true));
            Assert.False(changed);
            InputService.EditMenuConfiguration(0, () => changed = true);
            Assert.True(changed);
        }

        [Fact]
        public void FocusSuspensionSubmissionsAndKeyReleaseOwnPublication()
        {
            using var f = new MenuAuditFixture();
            f.Manager.SuspendWhenBackground = true;
            f.Manager.HostIsForeground = false;
            int submits = 0, releases = 0;
            void Submit() { Assert.True(Monitor.IsEntered(InputManager.MenuPublicationSync)); submits++; }
            void Release() { Assert.True(Monitor.IsEntered(InputManager.MenuPublicationSync)); releases++; }
            Assert.True(f.Manager.ApplyFocusSuspension(Submit, Release));
            Assert.Equal(2, submits);
            Assert.Equal(1, releases);
            Assert.True(f.Manager.ApplyFocusSuspension(Submit, Release));
            Assert.Equal(3, submits);
            f.Manager.HostIsForeground = true;
            Assert.False(f.Manager.ApplyFocusSuspension(Submit, Release));
            Assert.Equal(3, submits);
        }

        [Fact]
        public void DescriptorEdit_CancelsARealHoldInteraction()
        {
            using var f = new MenuAuditFixture();
            f.State.Buttons[0] = true;
            f.Frame();
            f.Deflect();
            f.Frame();
            Assert.Equal("L1", InputManager.GetEngagedLayerMask(0, f.Set));
            Assert.True(f.Context.State.PhysicalEngaged);
            Assert.Equal(2, f.Context.State.HoveredIndex);

            PadPage.ApplyShiftActivatorEdit(0, f.Activator, new ShiftActivator
            {
                LayerMask = "L1", LayerName = "Layer", Mode = "Hold", Descriptor = "Button 1",
                DeviceGuid = f.Device.InstanceGuidString,
            });
            f.Frame();
            f.Frame();
            Assert.Equal("Base", InputManager.GetEngagedLayerMask(0, f.Set));
            Assert.False(f.Fired(2));
            Assert.DoesNotContain((ushort)0x43, f.Manager._desiredLatchedKeys);

            // A later physical interaction still commits on its own release.
            f.State.Buttons[1] = true;
            f.Frame();
            f.Frame();
            Assert.True(f.Context.State.PhysicalEngaged);
            f.State.Buttons[1] = false;
            f.Frame();
            f.Frame();
            Assert.True(f.Fired(2));
            Assert.Contains((ushort)0x43, f.Manager._desiredLatchedKeys);
        }

        [Fact]
        public void AppearanceOnlyActivatorEdit_PreservesTheInteraction()
        {
            using var f = new MenuAuditFixture();
            f.HoldLayerAndDeflect();
            var context = f.Context;
            PadPage.ApplyShiftActivatorEdit(0, f.Activator, new ShiftActivator
            {
                LayerMask = f.Activator.LayerMask, LayerName = "Renamed caption",
                Mode = f.Activator.Mode, Descriptor = f.Activator.Descriptor,
                DeviceGuid = f.Activator.DeviceGuid,
                Color = "112233", Icon = "test",
            });
            Assert.Same(context, f.Context);
            f.State.Buttons[0] = false;
            f.Frame();
            f.Frame();
            Assert.True(f.Fired(2));
        }

        [Fact]
        public void LayerDelete_CancelsBeforeTheNextPublication()
        {
            using var f = new MenuAuditFixture();
            f.HoldLayerAndDeflect();
            Assert.True(f.Context.State.PhysicalEngaged);
            PadPage.ExecuteLayerDelete(f.Set, f.Activator, "L1", Array.Empty<PadViewModel>());
            Assert.Empty(f.Manager.MenuContexts);
            Assert.Empty(f.Drivers);
            Assert.Empty(f.Set.ShiftActivators);
            Assert.Same(f.Menu, Assert.Single(f.Set.Menus));
            Assert.Equal("L1", f.Menu.LayerMask);
            f.Frame();
            Assert.False(f.Fired(2));
            Assert.Empty(f.Manager._desiredLatchedKeys);

            f.Set.ShiftActivators.Add(f.Activator);
            f.State.Buttons[0] = true;
            f.Frame();
            f.Frame();
            f.State.Buttons[0] = false;
            f.Frame();
            f.Frame();
            Assert.True(f.Fired(2));
        }

        [Fact]
        public async Task AnEditAndItsCancellation_ExcludeConcurrentPublication()
        {
            using var f = new MenuAuditFixture();
            f.HoldLayerAndDeflect();
            using var changed = new ManualResetEventSlim();
            using var finish = new ManualResetEventSlim();
            var edit = Task.Run(() => InputService.EditMenuConfiguration(0, () =>
            {
                Assert.True(Monitor.IsEntered(InputManager.MenuPublicationSync));
                f.Set.ShiftActivators.Clear();
                InputManager.ClearShiftRuntime(0);
                changed.Set();
                Assert.True(finish.Wait(TimeSpan.FromSeconds(5)));
            }));
            Task publication = null;
            try
            {
                Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
                bool entered = Monitor.TryEnter(InputManager.MenuPublicationSync);
                if (entered) Monitor.Exit(InputManager.MenuPublicationSync);
                Assert.False(entered, "the edited state was visible before cancellation");
                publication = Task.Run(f.Frame);
            }
            finally
            {
                finish.Set();
                await edit.WaitAsync(TimeSpan.FromSeconds(5));
                if (publication != null) await publication.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.False(f.Fired(2));
            Assert.Empty(f.Manager._desiredLatchedKeys);
            Assert.True(Monitor.TryEnter(InputManager.MenuPublicationSync));
            Monitor.Exit(InputManager.MenuPublicationSync);
        }

        [Fact]
        public void EnableToggle_CancelsEvenWhenNoPollSeesTheDisabledState()
        {
            using var f = new MenuAuditFixture();
            f.HoldLayerAndDeflect();
            var other = f.Menu.Clone();
            other.MenuId = 2;
            f.Set.Menus.Add(other);
            f.Frame();
            var otherContext = f.Manager.MenuContexts[(0, f.Device.InstanceGuid, 2)];
            var pad = new PadViewModel(0);
            pad.ReloadMenus();
            var editor = pad.Menus.Single(m => m.Entry.MenuId == 1);

            editor.Enabled = false;
            Assert.DoesNotContain(f.Manager.MenuContexts.Keys, k => k.MenuId == 1);
            Assert.False(f.Drivers.Contains((0, 1)));
            f.Center();
            editor.Enabled = true;
            f.Frame();
            Assert.False(f.Fired(2));
            Assert.Same(otherContext, f.Manager.MenuContexts[(0, f.Device.InstanceGuid, 2)]);
            Assert.True(f.Manager.IsMenuItemFired(0, null, 2, 2));

            f.Deflect();
            f.Frame();
            f.Center();
            f.Frame();
            Assert.True(f.Fired(2));
        }

        [Fact]
        public void DisabledDefinition_RetiresItsStateWhenPublishedWithoutAnEditor()
        {
            using var f = new MenuAuditFixture();
            f.HoldLayerAndDeflect();
            f.Menu.Enabled = false;
            f.Frame();
            Assert.DoesNotContain(f.Manager.MenuContexts.Keys, k => k.MenuId == 1);
            Assert.Null(f.Manager.ActiveMenuOverlay);
            f.Center();
            f.Menu.Enabled = true;
            f.Frame();
            Assert.False(f.Fired(2));
        }

        [Fact]
        public void ReenableWhileHeldStartsAFreshInteraction()
        {
            using var f = new MenuAuditFixture();
            f.HoldLayerAndDeflect();
            var old = f.Context;
            var pad = new PadViewModel(0);
            pad.ReloadMenus();
            pad.SelectedMenu.Enabled = false;
            pad.SelectedMenu.Enabled = true;
            Assert.DoesNotContain(f.Manager.MenuContexts.Keys, k => k.MenuId == 1);
            f.Frame();
            Assert.NotSame(old, f.Context);
            Assert.True(f.Context.State.PhysicalEngaged);
            Assert.False(f.Fired(2));
            f.Center();
            f.Frame();
            Assert.True(f.Fired(2));
        }

        [Fact]
        public void DeleteThenDuplicate_ReusedIdDoesNotInheritARelease()
        {
            using var f = new MenuAuditFixture();
            var removed = f.Menu.Clone();
            removed.MenuId = 2;
            f.Set.Menus.Add(removed);
            f.HoldLayerAndDeflect();
            Assert.True(f.Manager.MenuContexts[(0, f.Device.InstanceGuid, 2)].State.PhysicalEngaged);
            var pad = new PadViewModel(0);
            pad.ReloadMenus();
            pad.SelectedMenu = pad.Menus.Single(m => m.Entry.MenuId == 2);
            pad.RemoveMenuCommand.Execute(null);
            Assert.DoesNotContain(f.Manager.MenuContexts.Keys, k => k.MenuId == 2);
            Assert.False(f.Drivers.Contains((0, 2)));
            f.Center();
            f.Frame();
            Assert.True(f.Fired(2), "the surviving menu must complete its physical release");
            pad.SelectedMenu = pad.Menus.Single();
            pad.DuplicateMenuCommand.Execute(null);
            Assert.Equal(2, pad.SelectedMenu.Entry.MenuId);
            f.Frame();
            Assert.False(f.Manager.IsMenuItemFired(0, null, 2, 2));
            Assert.False(f.Manager.MenuContexts[(0, f.Device.InstanceGuid, 2)].State.PhysicalEngaged);
        }

        [Fact]
        public void ReplacedDefinitionWithTheSameSignature_StartsCold()
        {
            using var f = new MenuAuditFixture();
            f.HoldLayerAndDeflect();
            var old = f.Context;
            f.Set.Menus[0] = f.Menu.Clone();
            f.Center();
            f.Frame();
            Assert.NotSame(old, f.Context);
            Assert.False(f.Fired(2));
        }

        [Fact]
        public void AFailedEditStillCancelsItsPartialPublication()
        {
            using var f = new MenuAuditFixture();
            f.HoldLayerAndDeflect();
            Assert.Throws<InvalidOperationException>(() => InputService.EditMenuConfiguration(0, () =>
            {
                f.Set.ShiftActivators.Clear();
                InputManager.ClearShiftRuntime(0);
                throw new InvalidOperationException("interrupted edit");
            }));
            Assert.Empty(f.Manager.MenuContexts);
            Assert.Empty(f.Drivers);
            f.Frame();
            Assert.False(f.Fired(2));
            Assert.Empty(f.Manager._desiredLatchedKeys);
        }
    }

    internal sealed class MenuAuditFixture : IDisposable
    {
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly bool[] _enabled = SettingsManager.SlotEnabled;
        private static readonly FieldInfo EngineField = typeof(InputService).GetField("_inputManagerStatic", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly object _engine = EngineField.GetValue(null);
        private static readonly MethodInfo Collect = typeof(InputManager).GetMethod("CollectMenuDirectOutputs", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo DriverField = typeof(InputManager).GetField("_menuDrivers", BindingFlags.Instance | BindingFlags.NonPublic);
        public InputManager Manager { get; } = new();
        public MappingSet Set { get; } = new();
        public ShiftActivator Activator { get; } = new()
        {
            LayerMask = "L1", LayerName = "Layer", Descriptor = "Button 0", Mode = "Hold",
        };
        public MenuDefinitionEntry Menu { get; }
        public UserDevice Device { get; }
        public CustomInputState State => Device.InputState;
        public readonly List<UserDevice> Devices = new();
        public IDictionary Drivers => (IDictionary)DriverField.GetValue(Manager);
        public InputManager.MenuTickContext Context => Manager.MenuContexts[(0, Device.InstanceGuid, 1)];

        public MenuAuditFixture(MenuFireType fire = MenuFireType.TouchRelease)
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
            SettingsManager.SlotCreated[0] = SettingsManager.SlotEnabled[0] = true;
            InputManager.ClearAllShiftRuntime();
            EngineField.SetValue(null, Manager);
            Menu = new MenuDefinitionEntry
            {
                MenuId = 1, Kind = MenuKind.Radial, CellCount = 4, HasCenter = true,
                HostDescriptor = "Gamepad RightStick", ClickDescriptor = "Button 1",
                LayerMask = "L1", LayerHoldsOpen = true, FireType = fire, EngageDeadzonePercent = 25,
            };
            for (int i = 0; i <= 4; i++) Menu.Items.Add(new MenuItemDefinition { Index = i, VirtualKey = 0x41 + i });
            Set.ShiftActivators.Add(Activator);
            Set.Menus.Add(Menu);
            SettingsManager.SlotMappingSets[0] = Set;
            Device = AddDevice();
            Activator.DeviceGuid = Device.InstanceGuidString;
        }

        public UserDevice AddDevice()
        {
            var state = new CustomInputState();
            Center(state);
            var device = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.Gamepad,
                CapButtonCount = 16, IsOnline = true, InputState = state,
            };
            SettingsManager.UserDevices.Items.Add(device);
            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = device.InstanceGuid, MapTo = 0 });
            Devices.Add(device);
            return device;
        }

        public static void Center(CustomInputState state) { state.Axis[3] = state.Axis[4] = 32768; }
        public static void Deflect(CustomInputState state) { state.Axis[3] = 65535; state.Axis[4] = 32768; }
        public void Center() => Center(State);
        public void Deflect() => Deflect(State);
        public void HoldLayerAndDeflect()
        {
            State.Buttons[0] = true;
            Frame();
            Deflect();
            Frame();
        }

        public void Frame()
        {
            using (InputManager.EnterMenuPublication())
            {
                foreach (var d in Devices) Manager.UpdateMenuContexts(d, d.InputState);
                foreach (var d in Devices) InputManager.ResolveActiveLayerMask(0, Set, d.InputState, d.InstanceGuidString);
                Manager._desiredLatchedKeys.Clear();
                Collect.Invoke(Manager, null);
            }
        }

        public bool Fired(int item) => Manager.IsMenuItemFired(0, null, 1, item);

        public void Dispose()
        {
            EngineField.SetValue(null, _engine);
            InputManager.ClearAllShiftRuntime();
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            SettingsManager.SlotCreated = _created;
            SettingsManager.SlotEnabled = _enabled;
        }
    }
}
