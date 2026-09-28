using System;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Owner report 2026-09-28: the Combine picker's reset button left the
    /// dropdown blank. Reset wrote the empty "use the default" value, which
    /// the engine reads as Strongest on an axis and Either on a button, but
    /// no picker entry carries it, so nothing was selected. The picker must
    /// land on the row's default instead.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class CombineModeResetTests
    {
        [Theory]
        [InlineData("ButtonA", MappingCategory.Buttons, "AND", "OR")]
        [InlineData("DPadUp", MappingCategory.DPad, "XOR", "OR")]
        [InlineData("LeftThumbAxisX", MappingCategory.LeftStick, "Sum", "MaxAbs")]
        [InlineData("LeftTrigger", MappingCategory.Triggers, "Average", "MaxAbs")]
        [InlineData("TouchpadClick", MappingCategory.Buttons, "AND", "OR")]
        [InlineData("TouchpadX1", MappingCategory.Touchpad, "Sum", "MaxAbs")]
        public void ResetSelectsTheRowDefaultInThePicker(string target, MappingCategory category,
            string picked, string expected)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var mapping = new MappingItem(target, target, category);
                    mapping.AddExtraSourceCommand.Execute(null);
                    Assert.Equal(expected, mapping.CombineMode);
                    mapping.CombineMode = picked;

                    var combo = new ComboBox { DataContext = mapping, SelectedValuePath = "Value" };
                    combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MappingItem.AvailableCombineModes)));
                    combo.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(MappingItem.CombineMode))
                    {
                        Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                    });
                    Assert.Equal(picked, combo.SelectedValue);

                    mapping.ResetSettingCommand.Execute(nameof(MappingItem.CombineMode));

                    Assert.Equal(expected, mapping.CombineMode);
                    Assert.Equal(expected, combo.SelectedValue);
                    Assert.NotNull(combo.SelectedItem);
                    Assert.Equal(mapping.AvailableCombineModes.Single(o => o.Value == expected).Name,
                        mapping.CombineModeDisplayName);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(15000), "Combine picker binding timed out");
            if (failure != null) throw failure;
        }

        [Theory]
        [InlineData("TouchpadClick", MappingCategory.Buttons, "OR")]
        [InlineData("TouchpadContact1", MappingCategory.Touchpad, "OR")]
        [InlineData("TouchpadContact2", MappingCategory.Touchpad, "OR")]
        [InlineData("TouchpadY2", MappingCategory.Touchpad, "MaxAbs")]
        public void TouchpadButtonsDefaultToEitherAndItsAxesToStrongest(string target,
            MappingCategory category, string expected)
        {
            // The engine reads Touchpad Click and the contact rows as
            // buttons (EvalTouchpadButton), so their default is Either.
            var mapping = new MappingItem(target, target, category);
            mapping.AddExtraSourceCommand.Execute(null);
            Assert.Equal(expected, mapping.CombineMode);
        }

        [Theory]
        [InlineData("ButtonA", MappingCategory.Buttons, "OR")]
        [InlineData("LeftThumbAxisX", MappingCategory.LeftStick, "MaxAbs")]
        public void AKindThatOpensThePickerSelectsTheDefault(string target, MappingCategory category,
            string expected)
        {
            // Incremental on a lone source shows the Combine picker, which
            // read blank because only a second source picked a default.
            var mapping = new MappingItem(target, target, category);
            mapping.LoadDescriptor("Button 0");
            Assert.False(mapping.IsMultiSource);
            Assert.Equal("", mapping.CombineMode);

            mapping.PrimaryKindSource.Kind = "Incremental";

            Assert.True(mapping.IsMultiSource);
            Assert.Equal(expected, mapping.CombineMode);
        }

        [Fact]
        public void LoadingAKindRowWithNoStoredModeSelectsTheDefault()
        {
            var mapping = new MappingItem("LT", "LeftTrigger", MappingCategory.Triggers);
            mapping.BeginLoadRow();
            mapping.LoadDescriptor("");
            mapping.LoadPrimaryKind(new MappingSource { Kind = "Incremental", ParamUp = "Button 4" });
            mapping.CombineMode = "";
            Assert.Equal("", mapping.CombineMode);
            mapping.EndLoadRow();
            Assert.Equal("MaxAbs", mapping.CombineMode);
        }

        [Fact]
        public void ALoneDirectOrToggleSourceKeepsTheEmptyMode()
        {
            // No picker shows for one Direct or Toggle source, so nothing
            // needs a default written for it.
            var mapping = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            mapping.LoadDescriptor("Button 0");
            mapping.PrimaryKindSource.Kind = "Toggle";
            Assert.False(mapping.IsMultiSource);
            Assert.Equal("", mapping.CombineMode);
            mapping.ResetSettingCommand.Execute(nameof(MappingItem.CombineMode));
            Assert.Equal("", mapping.CombineMode);
        }
    }
}
