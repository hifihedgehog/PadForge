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
    /// The Mode picker of a mapping row must show the mode the row has. A
    /// frame of the 6.0.0 prep showed a Right Trigger row in Rapid Trigger
    /// (its Distance slider showing) with the picker blank. Two ways led
    /// there: a load into a row whose list had been narrowed for its earlier
    /// input, and a saved mode the list never offers (the Sticks card's
    /// steering, stamped onto a stick row as it saves).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ModePickerShowsTheSavedModeTests
    {
        private const string PadGuid = "e7799b35-32b9-ebca-f114-3c65c0d74c78";
        private const string OtherGuid = "acac1111-2222-3333-4444-555566667777";

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
            Assert.True(thread.Join(15000), "Mode picker binding timed out");
            if (failure != null) throw failure;
        }

        /// <summary>The first source's Mode picker, bound as PadPage.xaml's
        /// SourceLine template binds it.</summary>
        private static ComboBox ModePicker(MappingItem row)
        {
            var combo = new ComboBox { DataContext = row.ModeHolder, SelectedValuePath = "Value", DisplayMemberPath = "Name" };
            combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MappingSourceItem.ModeChoices)));
            combo.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(MappingSourceItem.Kind))
            {
                Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            return combo;
        }

        private static string Shown(ComboBox combo) => (combo.SelectedItem as MappingSourceItem.KindChoice)?.Value;

        /// <summary>Loads a stored row the way InputService.RefreshMappingsToViewModel
        /// does: the first source, then the added ones, inside the load bracket.</summary>
        private static void Load(MappingItem row, MappingSource first, params MappingSource[] added)
        {
            row.BeginLoadRow();
            try
            {
                row.LoadFirstSource(first, _ => "Pad");
                row.ExtraSources.Clear();
                row.CombineMode = added.Length > 0 ? "MaxAbs" : "";
                foreach (var source in added)
                    row.ExtraSources.Add(MappingSourceItem.FromDomain(source));
            }
            finally { row.EndLoadRow(); }
        }

        private static MappingSource Source(string kind, string descriptor, string guid = PadGuid)
            => new MappingSource { Kind = kind, Descriptor = descriptor, DeviceGuid = guid, DeadZone = 50 };

        [Fact]
        public void ALoadShowsRapidTriggerAfterAnInputWithoutDepth()
        {
            OnSta(() =>
            {
                // A profile whose Right Trigger reads a button, shown, then a
                // profile whose Right Trigger is Rapid Trigger on the trigger.
                var row = new MappingItem("RT", "RightTrigger", MappingCategory.Triggers);
                Load(row, Source("Direct", "Button 7"));
                var picker = ModePicker(row);
                Assert.Equal("Direct", Shown(picker));
                Assert.DoesNotContain(row.ModeHolder.ModeChoices, k => k.Value == "RapidTrigger");

                Load(row, Source("RapidTrigger", "Axis 5"), Source("Direct", "Axis 5", OtherGuid));

                Assert.Equal("RapidTrigger", row.ModeHolder.Kind);
                Assert.Equal("RapidTrigger", Shown(picker));
            });
        }

        [Fact]
        public void ALoadShowsInvertOnHoldAfterARowWithOneSource()
        {
            OnSta(() =>
            {
                // Invert On Hold is offered first only beside another source,
                // which a load adds after the first source's mode.
                var row = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
                Load(row, Source("Direct", "Button 0"));
                var picker = ModePicker(row);
                Assert.DoesNotContain(row.ModeHolder.ModeChoices, k => k.Value == "InvertOnHold");

                Load(row, new MappingSource { Kind = "InvertOnHold", ParamModifier = "Button 4", DeviceGuid = PadGuid },
                    Source("Direct", "Button 1"));

                Assert.Equal("InvertOnHold", row.ModeHolder.Kind);
                Assert.Equal("InvertOnHold", Shown(picker));
            });
        }

        [Theory]
        [InlineData("LeftThumbAxisX", MappingCategory.LeftStick, "WindingStick", "Axis 0")]
        [InlineData("LeftThumbAxisX", MappingCategory.LeftStick, "AngleToAxisX", "Axis 0")]
        [InlineData("LeftThumbAxisY", MappingCategory.LeftStick, "AngleToAxisY", "Axis 1")]
        [InlineData("LeftThumbAxisX", MappingCategory.LeftStick, "MotionLeanX", "Axis 0")]
        public void AStickSteeringModeLoadsAsTheRowsOwnAxis(string target, MappingCategory category,
            string steering, string ownAxis)
        {
            OnSta(() =>
            {
                // SettingsService.ApplySteeringKindToRow keeps the stick's X
                // axis in Descriptor and its Y axis in ParamYDescriptor.
                MappingSource Steered(string guid) => new MappingSource
                {
                    Kind = steering, DeviceGuid = guid, DeadZone = 50,
                    Descriptor = "Axis 0", ParamYDescriptor = "Axis 1",
                };
                var row = new MappingItem(target, target, category);
                var picker = ModePicker(row);

                Load(row, Steered(PadGuid), Steered(OtherGuid));

                Assert.Equal("Direct", row.ModeHolder.Kind);
                Assert.Equal("Direct", Shown(picker));
                Assert.Equal(ownAxis, row.SourceDescriptor);
                Assert.True(row.IsPrimaryDescriptor);
                var added = row.ExtraSources.Single();
                Assert.Equal("Direct", added.Kind);
                Assert.Equal(ownAxis, added.Descriptor);
                Assert.Contains(added.ModeChoices, k => k.Value == added.Kind);
            });
        }

        [Fact]
        public void AnAddedSourceWithAnEmptyModeLoadsAsDirect()
        {
            // The engine reads an empty or unknown mode as Direct
            // (SourceEvaluator's default case), so the editor shows Direct.
            foreach (string kind in new[] { "", "Chord" })
            {
                var added = MappingSourceItem.FromDomain(Source(kind, "Button 2"));
                Assert.Equal("Direct", added.Kind);
                Assert.Equal("Button 2", added.Descriptor);
            }
        }
    }
}
