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

                    // A language change renames the entries in place, so the
                    // picked entry, which the closed picker shows, reads the
                    // new word. A list made again left the picker holding the
                    // old entry and its old word.
                    var first = vm.PresetChoices;
                    Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                    Assert.Equal(HMaestroProfileCatalog.CustomProfileId, picker.SelectedValue);
                    var shown = Assert.IsType<PadViewModel.PresetChoice>(picker.SelectedItem);
                    Assert.Equal("Benutzerdefiniert", Strings.Instance.Pad_Preset_Custom);
                    Assert.Equal(Strings.Instance.Pad_Preset_Custom, shown.Name);
                    Assert.Same(first, vm.PresetChoices);
                }
                finally { Strings.ChangeCulture(before); }
            });
        }

        /// <summary>A profile the user imported is named in the picker with
        /// the interface language's "(User Generated)" mark around its own
        /// name, and follows a language change. The import wrote the English
        /// mark into the stored name, so every language showed it.</summary>
        [Fact]
        public void AnImportedPresetIsMarkedInTheInterfaceLanguage()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                string json = UserProfileProblemTests.ProfileJson("padforge-test-shown" + HMaestroProfileCatalog.UserProfileIdSuffix,
                    edit: r => r["name"] = "Test Pad");
                using var ctx = HMaestroContexts.ForProfiles();
                ctx.LoadDefaultProfiles();
                Assert.Empty(HMaestroProfileCatalog.LoadUserProfiles(ctx, new[] { json }));
                var imported = ctx.GetProfile("padforge-test-shown" + HMaestroProfileCatalog.UserProfileIdSuffix);
                Assert.NotNull(imported);
                var builtIn = ctx.AllProfiles.First(p => p.IsDeployable && p.Id != imported.Id);

                foreach (string culture in new[] { "de-DE", "en" })
                {
                    Strings.ChangeCulture(CultureInfo.GetCultureInfo(culture));
                    string format = Strings.Instance.Pad_Preset_UserGenerated_Format;
                    Assert.Contains("{0}", format);
                    Assert.Equal(string.Format(format, "Test Pad"), HMaestroProfileCatalog.DisplayName(imported));
                    Assert.Same(HMaestroProfileCatalog.DisplayName(imported), HMaestroProfileCatalog.DisplayName(imported));
                    Assert.Equal(builtIn.Name, HMaestroProfileCatalog.DisplayName(builtIn));
                }
                // The profile keeps its own name for HIDMaestro.
                Assert.Equal("Test Pad", imported.Name);
            }
            finally { Strings.ChangeCulture(before); }
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
