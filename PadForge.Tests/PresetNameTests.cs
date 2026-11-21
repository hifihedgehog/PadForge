using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Extended slot's synthetic Custom preset is a word of PadForge's,
    /// not a product name, and is shown in the interface's language: in the
    /// sidebar pill, on the Home card and in the Preset picker. The 6.0.0
    /// prep's Arabic frame showed it as the English "Custom" among
    /// translated slot names. Its profile keeps "Custom" as its Name, which
    /// HIDMaestro reads for the virtual device.
    /// </summary>
    [Collection("CultureSwitching")]
    public class PresetNameTests
    {
        private static void OnSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(30000), "Preset picker binding timed out");
            if (failure != null) throw failure;
        }

        private static PadViewModel CustomSlot()
        {
            var vm = new PadViewModel(0) { OutputType = VirtualControllerType.Extended };
            // The field, not the setter: a live profile change moves the
            // slot's bindings through the settings service, which the test
            // host has none of (AuditDelta20261022Tests does the same).
            typeof(PadViewModel).GetField("_profileId", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(vm, HMaestroProfileCatalog.CustomProfileId);
            return vm;
        }

        /// <summary>The Preset picker, bound as PadPage.xaml binds it.</summary>
        private static ComboBox PresetPicker(PadViewModel vm)
        {
            var combo = new ComboBox { DataContext = vm, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
            combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(PadViewModel.PresetChoices)));
            combo.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(PadViewModel.ProfileId))
            {
                Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            return combo;
        }

        [Fact]
        public void TheCustomPresetIsShownByItsWordInThePillAndThePicker()
        {
            OnSta(() =>
            {
                var before = CultureInfo.CurrentUICulture;
                try
                {
                    Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                    var vm = CustomSlot();
                    Assert.Equal(Strings.Instance.Pad_Preset_Custom, vm.PresetDisplayName);

                    var picker = PresetPicker(vm);
                    var picked = Assert.IsType<PadViewModel.PresetChoice>(picker.SelectedItem);
                    Assert.Equal(HMaestroProfileCatalog.CustomProfileId, picked.Id);
                    Assert.Equal(Strings.Instance.Pad_Preset_Custom, picked.Name);
                    // Every entry is spoken by the name it shows, and a
                    // product name is the catalog's own.
                    foreach (var choice in vm.PresetChoices)
                        Assert.Equal(choice.Name, choice.ToString());
                    var profiles = vm.AvailableProfiles;
                    Assert.Equal(profiles.Count, vm.PresetChoices.Count);
                    for (int i = 0; i < profiles.Count; i++)
                    {
                        if (profiles[i].Id != HMaestroProfileCatalog.CustomProfileId)
                            Assert.Equal(profiles[i].Name, vm.PresetChoices[i].Name);
                    }
                    // The profile itself keeps its name for HIDMaestro.
                    Assert.Equal("Custom", profiles.Single(p => p.Id == HMaestroProfileCatalog.CustomProfileId).Name);

                    // A language change keeps the pick in a list made again.
                    var first = vm.PresetChoices;
                    Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                    Assert.NotSame(first, vm.PresetChoices);
                    Assert.Equal(HMaestroProfileCatalog.CustomProfileId, picker.SelectedValue);
                    Assert.NotNull(picker.SelectedItem);
                }
                finally { Strings.ChangeCulture(before); }
            });
        }

        /// <summary>Waits on the translation of Pad_Preset_Custom: the
        /// Arabic name is written in Arabic letters, as the frame's other
        /// slot names are.</summary>
        [Fact]
        public void TheCustomPresetIsNamedInArabic()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("ar"));
                string name = CustomSlot().PresetDisplayName;
                Assert.True(name.Any(c => c >= '؀' && c <= 'ۿ'), $"the Custom preset reads \"{name}\" in Arabic");
            }
            finally { Strings.ChangeCulture(before); }
        }
    }
}
