using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A mouse source's line (#600): its Steer input on any on/off input of
    /// the slot, both gates under Steer, the pickers' words after a language
    /// change, and the Source picker's Preview on the line's own slot with
    /// nothing allocated per step.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class MouseSteeringEditorAuditTests : IDisposable
    {
        private const string Target = "LeftThumbAxisX";
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly Func<string, string, int, bool> _savedKeyHeld = SourceCoercion.KeyHeldProvider;
        private readonly Func<int, double> _savedForce = SourceCoercion.MouseTravelForceProvider;
        private readonly Guid _mouse = Guid.NewGuid();
        private readonly Guid _keyboard = Guid.NewGuid();

        public MouseSteeringEditorAuditTests()
        {
            var devices = new DeviceCollection();
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = _mouse, ProductGuid = _mouse, InstanceName = "Test mouse", ProductName = "Test mouse",
                CapType = InputDeviceType.Mouse, IsOnline = true, IsEnabled = true,
            });
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = _keyboard, ProductGuid = _keyboard, InstanceName = "Test keyboard", ProductName = "Test keyboard",
                CapType = InputDeviceType.Keyboard, IsOnline = true, IsEnabled = true,
            });
            SettingsManager.UserDevices = devices;
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _savedDevices;
            SourceCoercion.KeyHeldProvider = _savedKeyHeld;
            SourceCoercion.MouseTravelForceProvider = _savedForce;
            MouseFamilyPreview.Instance.Stop();
            MouseFamilyPreview.ReadSeam = null;
            MouseFamilyPreview.ClockSeam = null;
        }

        private MappingItem Row(string descriptor, Guid device)
        {
            var row = new MappingItem("Left Stick X", Target, MappingCategory.LeftStick);
            row.LoadDescriptor(descriptor);
            row.PrimarySourceDeviceGuid = device.ToString();
            return row;
        }

        private static CustomInputState Mouse(int dx = 0)
            => new() { MouseRawDX = dx };

        // ── The Steer input is any on/off input of the slot ──

        [Fact]
        public void TheSteerInput_CanBeAKeyboardKey_AndTheWheelSteersOnlyWhileItIsHeld()
        {
            var row = Row("Mouse Travel X", _mouse);
            row.UseSharedKeyInputs(new ObservableCollection<InputChoice>
            {
                new() { Descriptor = "Button 2", DisplayName = "Right Button", DeviceGuid = _mouse.ToString(), DeviceLabel = "Test mouse" },
                new() { Descriptor = "Button 65", DisplayName = "A", DeviceGuid = _keyboard.ToString(), DeviceLabel = "Test keyboard" },
            });
            row.ParamTravelSteer = MappingSource.TravelSteerHeldStay;
            var key = row.TravelSteerInputs.FirstOrDefault(c => c.DeviceGuid == _keyboard.ToString());
            Assert.NotNull(key);
            row.TravelSteerInputChoice = key;
            Assert.Same(key, row.TravelSteerInputChoice);

            var src = new MappingSource { Kind = "Direct", Descriptor = "Mouse Travel X", DeviceGuid = _mouse.ToString() };
            row.StoreShapingAndGates(src);
            row.StoreMouseTravel(src);

            bool down = false;
            string keyboard = _keyboard.ToString();
            SourceCoercion.KeyHeldProvider = (device, descriptor, slot) =>
                down && string.Equals(device, keyboard, StringComparison.OrdinalIgnoreCase) && descriptor == "Button 65";
            var rt = new SourceKindRuntime();
            float Read(int dx)
            {
                rt.FrameSeq++;
                return SourceEvaluator.EvaluateForBipolarAxisTarget(Mouse(dx), src, 0, Target, 0, rt, 0.001, _mouse.ToString());
            }
            Assert.Equal(0f, Read(500));                 // the key is up: no steering
            down = true;
            Assert.Equal(0.25f, Read(500), 4);           // held: 500 of 2,000 counts
            down = false;
            Assert.Equal(0.25f, Read(500), 4);           // let go under While Held and Stay: it stays
        }

        // ── Steer reads both gates ──

        [Fact]
        public void ASourceWithTheSecondGateOnly_ShowsAWhileHeldMode()
        {
            var src = new MappingSource { Kind = "Direct", Descriptor = "Mouse Travel X", DeviceGuid = _mouse.ToString(), Gate2Descriptor = "Button 3" };
            var row = Row("Mouse Travel X", _mouse);
            row.LoadShapingAndGates(src);
            row.LoadMouseTravel(src);
            Assert.Equal(MappingSource.TravelSteerHeldStay, row.ParamTravelSteer);

            var added = MappingSourceItem.FromDomain(src);
            Assert.Equal(MappingSource.TravelSteerHeldStay, added.ParamTravelSteer);
        }

        [Fact]
        public void PickingAlways_OnASourceWithBothGates_StoresNeither()
        {
            var src = new MappingSource
            {
                Kind = "Direct", Descriptor = "Mouse Travel X", DeviceGuid = _mouse.ToString(),
                GateDescriptor = "Button 2", Gate2Descriptor = "Button 3", ParamTravelSteer = MappingSource.TravelSteerHeldStay,
            };
            var row = Row("Mouse Travel X", _mouse);
            row.LoadShapingAndGates(src);
            row.LoadMouseTravel(src);
            row.ParamTravelSteer = MappingSource.TravelSteerAlways;
            var stored = new MappingSource { Kind = "Direct", Descriptor = "Mouse Travel X" };
            row.StoreShapingAndGates(stored);
            Assert.Equal(string.Empty, stored.GateDescriptor ?? string.Empty);
            Assert.Equal(string.Empty, stored.Gate2Descriptor ?? string.Empty);

            var added = MappingSourceItem.FromDomain(src);
            added.ParamTravelSteer = MappingSource.TravelSteerAlways;
            var back = added.ToDomain();
            Assert.Equal(string.Empty, back.GateDescriptor ?? string.Empty);
            Assert.Equal(string.Empty, back.Gate2Descriptor ?? string.Empty);
        }

        // ── The mouse pickers follow a language change ──

        [Fact]
        public void ALanguageChange_BuildsTheMousePickersAndTheUnitAgain()
        {
            var row = Row("Mouse Travel X", _mouse);
            var added = new MappingSourceItem { Kind = "Direct", Descriptor = "Mouse Travel X", DeviceGuid = _mouse.ToString() };
            row.ExtraSources.Add(added);
            var rowRaised = new HashSet<string>();
            var addedRaised = new HashSet<string>();
            PropertyChangedEventHandler onRow = (_, e) => rowRaised.Add(e.PropertyName);
            PropertyChangedEventHandler onAdded = (_, e) => addedRaised.Add(e.PropertyName);
            row.PropertyChanged += onRow;
            added.PropertyChanged += onAdded;
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("es"));
                foreach (var raised in new[] { rowRaised, addedRaised })
                {
                    Assert.Contains(nameof(MappingItem.ParamTravelCenteringChoices), raised);
                    Assert.Contains(nameof(MappingItem.ParamTravelSteerChoices), raised);
                    Assert.Contains(nameof(MappingItem.TravelCountsUnit), raised);
                }
                Assert.Equal(Strings.Instance.Mapping_MouseTravelCentering_Off, row.ParamTravelCenteringChoices[0].Display);
            }
            finally
            {
                Strings.ChangeCulture(before);
                row.PropertyChanged -= onRow;
                added.PropertyChanged -= onAdded;
            }
        }

        // ── The Preview ──

        private sealed class StillMouse
        {
            public MouseFamilyPreview.Sample Sample = new(0, 0, 0, 0, new bool[5], null);
            public long X;
            public long Clock = 1;
            public MouseFamilyPreview.Sample Read() => Sample with { X = X };
        }

        private static StillMouse Seam()
        {
            var mouse = new StillMouse();
            MouseFamilyPreview.ReadSeam = mouse.Read;
            MouseFamilyPreview.ClockSeam = () => mouse.Clock;
            return mouse;
        }

        private static void Frame(StillMouse mouse)
        {
            mouse.Clock += System.Diagnostics.Stopwatch.Frequency / 60;
            MouseFamilyPreview.Instance.Step();
        }

        [Fact]
        public void ThePreview_ReadsTheGamesForceOnTheLinesOwnSlot()
        {
            SourceCoercion.MouseTravelForceProvider = slot => slot == 0 ? 1.0 : 0.0;
            var mouse = Seam();
            var row = Row("Mouse Travel X", _mouse);
            row.ParamTravelCentering = MappingSource.TravelCenteringGameForce;
            var choice = new InputChoice { Descriptor = "Mouse Travel X", DeviceGuid = _mouse.ToString() };
            MappingDisplayResolver.DescribeMouseFamilies(new List<InputChoice> { choice });
            row.SelectedInput = choice;
            MouseFamilyPreview.Instance.PinCommand.Execute(new object[] { choice, row, 3 });

            mouse.X += 1000;
            Frame(mouse);
            double moved = MouseFamilyPreview.Instance.Value;
            for (int i = 0; i < 10; i++) Frame(mouse);
            // Slot 0's game pushes slot 0's wheel. This line is slot 3's.
            Assert.Equal(moved, MouseFamilyPreview.Instance.Value, 6);
        }

        [Fact]
        public void ThePreviewsStep_AllocatesNothing_OnceItIsPinned()
        {
            var mouse = Seam();
            var row = Row("Mouse Travel X", _mouse);
            row.ParamTravelSteer = MappingSource.TravelSteerHeldStay;
            row.GateDescriptor = "Button 2";
            var choice = new InputChoice { Descriptor = "Mouse Travel X", DeviceGuid = _mouse.ToString() };
            MappingDisplayResolver.DescribeMouseFamilies(new List<InputChoice> { choice });
            row.SelectedInput = choice;
            MouseFamilyPreview.Instance.PinCommand.Execute(new object[] { choice, row, 0 });
            for (int i = 0; i < 8; i++) Frame(mouse);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) Frame(mouse);
            long grown = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, grown);
        }
    }
}
