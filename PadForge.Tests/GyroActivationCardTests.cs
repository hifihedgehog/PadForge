using System;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The Gyro On rows and the Re-Center switch of the Gyro group's
    /// Engage card, and the import stamps they replace: Steam's ratchet, its
    /// gyro activation by layer and its re-center each land on the assigned
    /// device's card and leave the slot.</summary>
    [Collection("SettingsManagerStatics")]
    public sealed class GyroActivationCardTests
    {
        private static PadSetting Assign(MappingSet set)
        {
            var saved = SettingsManager.SlotMappingSets;
            try
            {
                SettingsManager.SlotMappingSets = new MappingSet[16];
                SettingsManager.SlotMappingSets[0] = set;
                var ps = new PadSetting();
                WorkshopTuningApplier.ApplyToAssignedDevice(0, ps, Guid.NewGuid().ToString());
                return ps;
            }
            finally { SettingsManager.SlotMappingSets = saved; }
        }

        [Fact]
        public void TheRatchet_LandsAsBasesWhileNotHeld_AndLeavesTheSlot()
        {
            var set = new MappingSet { WorkshopGyroRatchetDescriptors = "Gamepad Paddle4|Touchpad 1 Finger 0 Down" };
            var ps = Assign(set);
            Assert.Equal("Base=Release,Any,Gamepad Paddle4|Touchpad 1 Finger 0 Down", ps.GyroActivation);
            Assert.Equal("", set.WorkshopGyroRatchetDescriptors);
        }

        [Fact]
        public void TheActivation_AndTheRecenter_LandOnTheCard_AndLeaveTheSlot()
        {
            var set = new MappingSet
            {
                WorkshopGyroActivation = "Base=Hold,All,Gamepad ButtonA|Gamepad ButtonB;Layer_1=Toggle,Any,Gamepad ButtonX",
                WorkshopGyroRecenterOnEngage = true,
            };
            var ps = Assign(set);
            Assert.Equal("Base=Hold,All,Gamepad ButtonA|Gamepad ButtonB;Layer_1=Toggle,Any,Gamepad ButtonX", ps.GyroActivation);
            Assert.Equal("1", ps.GyroRecenterOnEngage);
            Assert.Equal("", set.WorkshopGyroActivation);
            Assert.False(set.WorkshopGyroRecenterOnEngage);
        }

        [Fact]
        public void TheUsersOwnActivation_Stays()
        {
            var set = new MappingSet { WorkshopGyroActivation = "Base=Hold,Any,Gamepad ButtonA" };
            var saved = SettingsManager.SlotMappingSets;
            try
            {
                SettingsManager.SlotMappingSets = new MappingSet[16];
                SettingsManager.SlotMappingSets[0] = set;
                var ps = new PadSetting { GyroActivation = "Base=Toggle,Any,Gamepad ButtonY" };
                WorkshopTuningApplier.ApplyToAssignedDevice(0, ps, Guid.NewGuid().ToString());
                Assert.Equal("Base=Toggle,Any,Gamepad ButtonY", ps.GyroActivation);
                Assert.Equal("", set.WorkshopGyroActivation);
            }
            finally { SettingsManager.SlotMappingSets = saved; }
        }

        [Fact]
        public void ParseAndEncode_RoundTrip()
        {
            const string text = "Base=Release,Any,Gamepad ButtonA;Layer_2=Hold,All,Gamepad ButtonB|Gamepad ButtonX;Layer_3=Hold,Any,";
            Assert.Equal(text, GyroActivation.Encode(GyroActivation.Parse(text)));
            Assert.Null(GyroActivation.Parse(""));
            Assert.Empty(GyroActivation.Parse("Layer_3=Hold,Any,")["Layer_3"].Buttons);
        }

        [Fact]
        public void TheCard_ShowsAndEditsEachLayer()
        {
            var vm = new PadViewModel(0) { GyroActivation = "Base=Hold,Any,Gamepad ButtonA" };
            Assert.Equal("Hold", vm.GyroActivationMode);
            Assert.True(vm.GyroActivationUsesButtons);
            Assert.Single(vm.GyroActivationButtons, b => b.IsOn);
            Assert.True(vm.GyroActivationButtons.Single(b => b.IsOn).Descriptor == "Gamepad ButtonA");

            vm.GyroActivationButtons.First(b => b.Descriptor == "Gamepad ButtonB").IsOn = true;
            vm.GyroActivationNeeds = "All";
            Assert.Equal("Base=Hold,All,Gamepad ButtonA|Gamepad ButtonB", vm.GyroActivation);

            // A shift layer starts on Same as Base and adds an entry of its own.
            vm.GyroActivationLayer = "Layer_1";
            Assert.Equal(PadViewModel.GyroActivationSameAsBase, vm.GyroActivationMode);
            vm.GyroActivationMode = PadViewModel.GyroActivationAlways;
            Assert.Equal("Base=Hold,All,Gamepad ButtonA|Gamepad ButtonB;Layer_1=Hold,Any,", vm.GyroActivation);

            // Base back to Always On drops its entry and keeps the layer's.
            vm.GyroActivationLayer = "Base";
            vm.GyroActivationMode = PadViewModel.GyroActivationSameAsBase;
            Assert.Equal("Layer_1=Hold,Any,", vm.GyroActivation);
        }

        /// <summary>A controller that never had a Gyro On entry still lists
        /// its buttons. The list was built only when the activation's text
        /// or the shown layer changed, so on a new controller While Held
        /// showed the Gyro On Buttons row with nothing in it to tick, until
        /// the Layer picker moved. The list is now built with the
        /// controller.</summary>
        [Fact]
        public void ANewControllersButtons_AreListed_BeforeAnyEntryExists()
        {
            var vm = new PadViewModel(0);
            Assert.Equal("", vm.GyroActivation);
            Assert.NotEmpty(vm.GyroActivationButtons);
            Assert.DoesNotContain(vm.GyroActivationButtons, b => b.IsOn);

            vm.GyroActivationMode = "Hold";
            Assert.True(vm.GyroActivationUsesButtons);
            Assert.Single(vm.GyroActivationButtons, b => b.Descriptor == "Gamepad ButtonA").IsOn = true;
            Assert.Equal("Base=Hold,Any,Gamepad ButtonA", vm.GyroActivation);
        }

        [Fact]
        public void AnImportedButtonTheListLacks_StillShowsChecked()
        {
            var vm = new PadViewModel(0) { GyroActivation = "Base=Release,Any,Button 17" };
            var b = Assert.Single(vm.GyroActivationButtons, x => x.Descriptor == "Button 17");
            Assert.True(b.IsOn);
            b.IsOn = false;
            Assert.Equal("Base=Release,Any,", vm.GyroActivation);
        }

        /// <summary>Each persisted value has a control on the card, a reset
        /// button beside it, and both save paths and both load paths carry
        /// it.</summary>
        [Fact]
        public void EachValue_HasARowAResetAndItsSaveAndLoad()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "PadForge.sln")))
                root = root.Parent;
            Assert.NotNull(root);
            string Read(params string[] p) => File.ReadAllText(Path.Combine(new[] { root!.FullName }.Concat(p).ToArray()));
            string xaml = Read("PadForge.App", "Views", "PadPage.xaml");
            string input = Read("PadForge.App", "Services", "InputService.cs");
            string settings = Read("PadForge.App", "Services", "SettingsService.cs");

            foreach (var (bind, reset) in new[]
            {
                ("Binding GyroActivationMode,", "ResetGyroActivationLayerCommand"),
                ("Binding GyroActivationButtons}", "ResetGyroActivationButtonsCommand"),
                ("Binding GyroActivationNeeds,", "ResetGyroActivationNeedsCommand"),
                ("Binding GyroRecenterOnEngage,", "ResetGyroRecenterOnEngageCommand"),
            })
            {
                Assert.Contains(bind, xaml);
                Assert.Contains(reset, xaml);
            }
            foreach (var src in new[] { input, settings })
            {
                Assert.Contains("ps.GyroActivation = padVm.GyroActivation", src);
                Assert.Contains("padVm.GyroActivation = ps.GyroActivation", src);
                Assert.Contains("ps.GyroRecenterOnEngage = padVm.GyroRecenterOnEngage", src);
                Assert.Contains("padVm.GyroRecenterOnEngage = ps.GyroRecenterOnEngage", src);
            }
        }
    }
}
