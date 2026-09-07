using System;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using PadForge.Engine.Common.Mapping;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public class MotionCombinePickerTests
    {
        [Theory]
        [InlineData("MotionGyro")]
        [InlineData("MotionAccel")]
        public void AddingAMotionSourceSelectsTheAxisDefault(string target)
        {
            var mapping = new MappingItem("Motion", target, MappingCategory.Motion);
            Assert.Equal("", mapping.CombineMode);

            mapping.AddExtraSourceCommand.Execute(null);

            Assert.Equal("MaxAbs", mapping.CombineMode);
            Assert.Equal(new[] { "MaxAbs", "Sum", "Average", "Custom" },
                mapping.AvailableCombineModes.Select(option => option.Value));
            var button = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            button.AddExtraSourceCommand.Execute(null);
            Assert.Equal("OR", button.CombineMode);
            Assert.Contains(button.AvailableCombineModes, option => option.Value == "AND");
            var trigger = new MappingItem("Trigger", "LeftTrigger", MappingCategory.Triggers);
            Assert.Contains(trigger.AvailableCombineModes, option => option.Value == "StickTrim");
        }

        [Theory]
        [InlineData("OR")]
        [InlineData("AND")]
        [InlineData("XOR")]
        [InlineData("StickTrim")]
        public void LegacyMotionModeDisplaysItsCurrentNumericBehavior(string mode)
        {
            var mapping = new MappingItem("Gyro", "MotionGyro", MappingCategory.Motion)
            {
                CombineMode = mode,
            };
            Assert.Equal("MaxAbs", mapping.CombineMode);
            Assert.Equal(mapping.AvailableCombineModes.Single(option => option.Value == "MaxAbs").Name,
                mapping.CombineModeDisplayName);
            float[] values = { -0.25f, 0.8f };
            Assert.Equal(0.8f, CombineHelper.CombineAxis(mode, values));
            Assert.Equal(0.8f, CombineHelper.CombineAxis(mapping.CombineMode, values));
        }

        [Fact]
        public void MotionModeBindingResolvesTheLegacyModeAndAcceptsARealPick()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var mapping = new MappingItem("Gyro", "MotionGyro", MappingCategory.Motion)
                    {
                        CombineMode = "AND",
                    };
                    var combo = new ComboBox { DataContext = mapping, SelectedValuePath = "Value" };
                    combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MappingItem.AvailableCombineModes)));
                    combo.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(MappingItem.CombineMode))
                    {
                        Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                    });
                    combo.GetBindingExpression(ComboBox.SelectedValueProperty).UpdateTarget();
                    Assert.Equal("MaxAbs", combo.SelectedValue);
                    Assert.DoesNotContain(combo.Items.Cast<MappingItem.CombineModeOption>(), option => option.Value == "AND");
                    combo.SelectedValue = "Sum";
                    Assert.Equal("Sum", mapping.CombineMode);
                    Assert.Equal(0.55f, CombineHelper.CombineAxis(mapping.CombineMode, new[] { -0.25f, 0.8f }), 5);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(15000), "Motion picker binding timed out");
            if (failure != null) throw failure;
        }
    }
}
