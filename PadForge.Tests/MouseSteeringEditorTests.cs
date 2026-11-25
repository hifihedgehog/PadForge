using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services.Catalog;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #600 items 34 to 40, the line and the picker: what each mouse source's
    /// line shows (Steer on every mouse source, Mouse Speed's and Mouse
    /// Position's own settings, "This axis moves relatively"), the travel in
    /// centimeters from the mouse's DPI, the Source picker's words and
    /// Preview, and the row's mouse settings as catalog entries.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class MouseSteeringEditorTests : IDisposable
    {
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly Guid _mouse = Guid.NewGuid();
        private readonly Guid _spinner = Guid.NewGuid();

        public MouseSteeringEditorTests()
        {
            var devices = new DeviceCollection();
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = _mouse, ProductGuid = _mouse, InstanceName = "Test mouse", ProductName = "Test mouse",
                CapType = InputDeviceType.Mouse, IsOnline = true, IsEnabled = true, MouseDpi = 1600,
            });
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = _spinner, ProductGuid = _spinner, InstanceName = "Spinner", ProductName = "Spinner",
                CapType = InputDeviceType.Joystick, IsOnline = true, IsEnabled = true,
            });
            SettingsManager.UserDevices = devices;
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _savedDevices;
            MouseFamilyPreview.Instance.Stop();
            MouseFamilyPreview.ReadSeam = null;
            MouseFamilyPreview.ClockSeam = null;
        }

        private MappingItem Row(string descriptor, Guid device)
        {
            var row = new MappingItem("Left Stick X", "LeftThumbAxisX", MappingCategory.LeftStick);
            row.LoadDescriptor(descriptor);
            row.PrimarySourceDeviceGuid = device.ToString();
            return row;
        }

        private MappingSourceItem Added(string descriptor, Guid device)
            => new() { Kind = "Direct", Descriptor = descriptor, DeviceGuid = device.ToString() };

        // ── The line ──

        [Fact]
        public void MouseSpeed_ShowsItsOwnSettings_AndSteer_OnAMouseOnly()
        {
            var speed = Row("Axis 0", _mouse);
            Assert.True(speed.IsMouseSpeedSource);
            Assert.True(speed.IsMouseSteerSource);
            Assert.False(speed.IsMouseTravelSource);
            Assert.False(speed.IsAxisRelativeApplicable);
            Assert.False(speed.IsTravelSteerInputShown);
            speed.ParamTravelSteer = MappingSource.TravelSteerHeldStay;
            Assert.True(speed.IsTravelSteerInputShown);

            var padAxis = Row("Axis 0", _spinner);
            Assert.False(padAxis.IsMouseSpeedSource);
            Assert.False(padAxis.IsMouseSteerSource);

            var added = Added("Axis 1", _mouse);
            Assert.True(added.IsMouseSpeedSource);
            added.Kind = "Incremental";
            Assert.False(added.IsMouseSpeedSource);
        }

        [Fact]
        public void MousePosition_ShowsSteer_AndItsAnchorUnderAModeThatReadsAnInput()
        {
            var position = Added("Mouse Position X", _mouse);
            Assert.True(position.IsMouseSteerSource);
            Assert.False(position.IsMousePositionAnchorShown);
            position.ParamTravelSteer = MappingSource.TravelSteerToggle;
            Assert.True(position.IsMousePositionAnchorShown);
            position.ParamMousePositionAnchor = true;
            Assert.True(position.ToDomain().ParamMousePositionAnchor);
        }

        [Fact]
        public void AnAxisOfADeviceThatIsNoMouse_CanBeMarkedAsMovingRelatively_AndThenTakesTheTravelLine()
        {
            var axis = Row("Axis 3", _spinner);
            Assert.True(axis.IsAxisRelativeApplicable);
            Assert.False(axis.IsMouseTravelSource);
            Assert.True(axis.IsParamAccelApplicable);
            axis.ParamAxisRelative = true;
            Assert.True(axis.IsMouseTravelSource);
            Assert.True(axis.IsMouseSteerSource);
            // A held position takes no Acceleration, as Mouse Travel's does not.
            Assert.False(axis.IsParamAccelApplicable);
            var saved = new MappingSource();
            axis.StoreMouseTravel(saved);
            Assert.True(saved.ParamAxisRelative);

            // A mouse's axis and a button are never marked.
            Assert.False(Row("Axis 3", _mouse).IsAxisRelativeApplicable);
            Assert.False(Row("Button 1", _spinner).IsAxisRelativeApplicable);

            // The flag rides the saved form, an added source's too.
            var added = Added("Slider 0", _spinner);
            added.ParamAxisRelative = true;
            var back = MappingSourceItem.FromDomain(added.ToDomain());
            Assert.True(back.ParamAxisRelative);
            Assert.True(back.IsMouseTravelSource);
        }

        [Fact]
        public void TheWheelsTakeTheTravelLine()
        {
            Assert.True(Row(SourceCoercion.WheelTravelDescriptor, _mouse).IsMouseTravelSource);
            Assert.True(Added(SourceCoercion.WheelTravelSidewaysDescriptor, _mouse).IsMouseTravelSource);
        }

        [Fact]
        public void TheNewSettings_GoToTheSavedFormAndBack()
        {
            var added = Added("Axis 0", _mouse);
            added.ParamMouseSpeedSensitivity = 2.25;
            added.ParamMouseBackToCenter = 0.4;
            added.ParamTravelSmoothingMs = 12;
            added.ParamTravelSteer = MappingSource.TravelSteerHeldStay;
            var domain = added.ToDomain();
            Assert.Equal(2.25, domain.ParamMouseSpeedSensitivity);
            Assert.Equal(0.4, domain.ParamMouseBackToCenter);
            Assert.Equal(12, domain.ParamTravelSmoothingMs);
            Assert.Equal(MappingSource.TravelSteerHeldStay, domain.ParamTravelSteer);
            var back = MappingSourceItem.FromDomain(domain);
            Assert.Equal(2.25, back.ParamMouseSpeedSensitivity);
            Assert.Equal(0.4, back.ParamMouseBackToCenter);
            // A typed value lands inside its range.
            back.ParamMouseSpeedSensitivity = 99;
            Assert.Equal(MappingSource.MaxMouseSpeedSensitivity, back.ParamMouseSpeedSensitivity);
            back.ParamTravelReturnTime = 0;
            Assert.Equal(0, back.ParamTravelReturnTime);
            // Each resets to its default.
            back.ResetSettingCommand.Execute(nameof(MappingSourceItem.ParamMouseSpeedSensitivity));
            back.ResetSettingCommand.Execute(nameof(MappingSourceItem.ParamMouseBackToCenter));
            Assert.Equal(MappingSource.DefaultMouseSpeedSensitivity, back.ParamMouseSpeedSensitivity);
            Assert.Equal(0, back.ParamMouseBackToCenter);
        }

        /// <summary>A mouse bound to WinUSB reports its wheel in counts its
        /// Resolution Multiplier divides a notch into. Wheel Travel reads
        /// Windows' units at that precision, the remainder kept for the next
        /// poll, as Raw Input hands a fine-scrolling wheel's units.</summary>
        [Fact]
        public void AWinUsbMousesFineWheel_ReachesWheelTravelAtItsFullPrecision()
        {
            var channel = new FakeKeyboardChannel(WinUsbMouseTests.Path, mouse: true) { WheelMultiplier = 16 };
            using var row = new PadForge.Common.Input.WinUsbKeyboards.WinUsbMouseDevice(
                new PadForge.Common.Input.WinUsbKeyboards.WinUsbKeyboard(WinUsbMouseTests.Path, 1, 2, Guid.NewGuid(), "M", IsMouse: true),
                channel, start: false);
            row.Feed(new byte[] { 0x00, 0x00, 0x00, 0x01, 0 });
            Assert.Equal(7, row.GetCurrentState().MouseRawWheel);          // 7.5 units, half a unit kept
            row.Feed(new byte[] { 0x00, 0x00, 0x00, 0x01, 0 });
            Assert.Equal(8, row.GetCurrentState().MouseRawWheel);          // and given with the next
            for (int i = 0; i < 16; i++) row.Feed(new byte[] { 0x00, 0x00, 0x00, 0xFF, 0 });
            Assert.Equal(-120, row.GetCurrentState().MouseRawWheel);       // sixteen counts back are a notch
        }

        /// <summary>Search finds the new settings of a source's line, as a
        /// row option on the controller page, so a person and a program reach
        /// them alike (#600 item 40).</summary>
        [Fact]
        public void SearchFindsTheNewMouseSettings()
        {
            var markup = PadForge.Search.SearchSources.FileNames.ToDictionary(
                p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
            var index = PadForge.Search.SearchIndexBuilder.Read(markup, PadForge.Views.PadPage.TabOfGroupName);
            foreach (string key in new[]
                     {
                         "Mapping_MouseSpeedSensitivity", "Mapping_MouseBackToCenter", "Mapping_MousePositionAnchor",
                         "Mapping_AxisRelative",
                     })
                Assert.Contains(index.Entries, e => e.Kind == PadForge.Search.SearchKind.Setting
                    && e.Page == PadForge.Search.SearchPage.Pad && e.RowOptions && e.Label?.Key == key);
        }

        // ── Item 38: centimeters from the DPI ──

        [Fact]
        public void TheTravel_ReadsInCentimeters_OnceTheMousesDpiIsSet()
        {
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
                var row = Row("Mouse Travel X", _mouse);
                // The board's line: "2,000 counts, 3.2 cm".
                Assert.Equal("2,000", row.ParamTravelCountsText);
                Assert.Equal("counts, 3.2 cm", row.TravelCountsUnit);
                int heard = 0;
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MappingItem.TravelCountsUnit)) heard++; };
                row.ParamTravelCounts = 4000;
                Assert.Equal(1, heard);
                Assert.Equal("counts, 6.4 cm", row.TravelCountsUnit);

                // A DPI change on the device panel reaches the line.
                SettingsManager.FindDeviceByInstanceGuid(_mouse).MouseDpi = 800;
                MouseDpiNotice.Instance.Raise();
                Assert.Equal(2, heard);
                Assert.Equal("counts, 13 cm", row.TravelCountsUnit);

                // No DPI, no centimeters, and a wheel has none.
                SettingsManager.FindDeviceByInstanceGuid(_mouse).MouseDpi = 0;
                Assert.Equal("counts", row.TravelCountsUnit);
                SettingsManager.FindDeviceByInstanceGuid(_mouse).MouseDpi = 800;
                Assert.Equal("counts", Row(SourceCoercion.WheelTravelDescriptor, _mouse).TravelCountsUnit);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
        }

        /// <summary>The counts' box shows the user's own digit grouping, and
        /// what it shows reads back as the same counts in every culture .NET
        /// knows, as does a number typed plainly or with a plain space where
        /// the culture groups with a no-break one.</summary>
        [Fact]
        public void TheCountsBox_ReadsBackWhatItShows_InEveryCulture()
        {
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                var row = Added("Mouse Travel X", _mouse);
                foreach (var c in System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.AllCultures))
                {
                    System.Globalization.CultureInfo.CurrentCulture = c;
                    foreach (double counts in new[] { 100.0, 2000.0, 12500.0, 50000.0 })
                    {
                        row.ParamTravelCounts = counts;
                        string shown = row.ParamTravelCountsText;
                        row.ParamTravelCounts = 777;
                        row.ParamTravelCountsText = shown;
                        Assert.True(counts == row.ParamTravelCounts, $"{c.Name}: \"{shown}\" read back as {row.ParamTravelCounts}");
                    }
                    row.ParamTravelCountsText = "3500";
                    Assert.True(row.ParamTravelCounts == 3500, $"{c.Name}: a plain 3500");
                    string spaced = "4" + " " + "500";
                    if (c.NumberFormat.NumberGroupSeparator.Trim().Length == 0)
                    {
                        row.ParamTravelCountsText = spaced;
                        Assert.True(row.ParamTravelCounts == 4500, $"{c.Name}: 4 500 with a plain space");
                    }
                }
                // Text that is no number leaves the counts, and the box shows them again.
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                row.ParamTravelCounts = 2000;
                int shownAgain = 0;
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MappingSourceItem.ParamTravelCountsText)) shownAgain++; };
                row.ParamTravelCountsText = "wide";
                Assert.Equal(2000, row.ParamTravelCounts);
                Assert.Equal(1, shownAgain);
                Assert.Equal("2.000", row.ParamTravelCountsText);
                // A typed number past the range lands at its end.
                row.ParamTravelCountsText = "90.000";
                Assert.Equal(MappingSource.MaxTravelCounts, row.ParamTravelCounts);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
        }

        [Fact]
        public void TheDevicePanel_ShowsMouseDpi_OnAMousesOwnRowOnly()
        {
            var row = new DeviceRowViewModel { DeviceTypeKey = "Mouse", DevicePath = @"\\?\HID#VID_046D&PID_C08B#7&1&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}" };
            Assert.True(row.ShowMouseDpi);
            Assert.Contains(nameof(DeviceRowViewModel.MouseDpi), DevicesViewModel.SettingsCardResets(row));
            row.MouseDpi = 999999;
            Assert.Equal(UserDevice.MaxMouseDpi, row.MouseDpi);
            row.ResetSettingCommand.Execute(nameof(DeviceRowViewModel.MouseDpi));
            Assert.Equal(0, row.MouseDpi);
            Assert.False(new DeviceRowViewModel { DeviceTypeKey = "Gamepad" }.ShowMouseDpi);
        }

        // ── Item 38: the picker's words ──

        [Fact]
        public void ThePicker_SaysWhatEachMouseFamilyDoes_AndWhichHasAPreview()
        {
            var s = Strings.Instance;
            var list = new List<InputChoice>
            {
                new() { Descriptor = "Axis 0" }, new() { Descriptor = "Axis 2" }, new() { Descriptor = "Mouse Position Y" },
                new() { Descriptor = "Mouse Travel X" }, new() { Descriptor = SourceCoercion.WheelTravelDescriptor },
                new() { Descriptor = SourceCoercion.WheelTravelSidewaysDescriptor }, new() { Descriptor = "Button 0" },
            };
            MappingDisplayResolver.DescribeMouseFamilies(list);
            Assert.Equal(s.Mapping_MouseSpeed_Description, list[0].Description);
            Assert.Equal(InputChoice.PreviewSpeed, list[0].PreviewFamily);
            Assert.Equal(s.Mapping_MouseScroll_Description, list[1].Description);
            Assert.False(list[1].HasPreview);
            Assert.Equal(s.Mapping_MousePosition_Description, list[2].Description);
            Assert.Equal(s.Mapping_MouseTravel_Description, list[3].Description);
            Assert.Equal(InputChoice.PreviewWheel, list[4].PreviewFamily);
            Assert.Equal(s.Mapping_WheelTravelSideways_Description, list[5].Description);
            Assert.False(list[6].HasDescription);
            Assert.False(list[6].HasPreview);
            Assert.Equal(s.Mapping_WheelTravel, MappingDisplayResolver.ResolveDescriptorText(SourceCoercion.WheelTravelDescriptor, null));
        }

        // ── Item 39: Preview ──

        private sealed class FakeMouse
        {
            public long X, Wheel;
            public bool[] Buttons = new bool[5];
            public (double, double, double, double, double)? Pointer;
            public long Clock = 1;
            public MouseFamilyPreview.Sample Read() => new(X, 0, Wheel, 0, (bool[])Buttons.Clone(), Pointer);
        }

        private static FakeMouse Seam()
        {
            var mouse = new FakeMouse();
            MouseFamilyPreview.ReadSeam = mouse.Read;
            MouseFamilyPreview.ClockSeam = () => mouse.Clock;
            return mouse;
        }

        private static void Frame(FakeMouse mouse, double seconds = 0.016)
        {
            mouse.Clock += (long)(seconds * System.Diagnostics.Stopwatch.Frequency);
            MouseFamilyPreview.Instance.Step();
        }

        [Fact]
        public void Preview_PinsOneFamily_FollowsTheUsersMouse_AndMapsNothing()
        {
            var mouse = Seam();
            var preview = MouseFamilyPreview.Instance;
            var travel = new InputChoice { Descriptor = "Mouse Travel X", DeviceGuid = string.Empty };
            MappingDisplayResolver.DescribeMouseFamilies(new List<InputChoice> { travel });
            var row = Row("Axis 0", _mouse);
            preview.PinCommand.Execute(new object[] { travel, row });
            Assert.True(travel.IsPreviewing);
            Assert.Same(travel, preview.Pinned);

            mouse.X += 760;                        // 760 of the default 2,000 counts
            Frame(mouse);
            Assert.Equal(0.38, preview.Value, 6);
            Assert.Equal(string.Format(Strings.Instance.Mapping_Preview_Held_Format,
                string.Format(Strings.Instance.Mapping_Preview_Right, 38)), preview.Caption);
            Assert.NotEqual(System.Windows.Media.Geometry.Empty, preview.Arc);
            // Held as a wheel is: nothing more moves it.
            Frame(mouse);
            Assert.Equal(0.38, preview.Value, 6);

            // The row was never mapped to it.
            Assert.Equal("Axis 0", row.SelectedInput?.Descriptor ?? "Axis 0");
            Assert.False(row.IsMouseTravelSource);

            // Another family's Preview takes its place, and Stop ends it.
            var wheel = new InputChoice { Descriptor = SourceCoercion.WheelTravelDescriptor };
            MappingDisplayResolver.DescribeMouseFamilies(new List<InputChoice> { wheel });
            preview.PinCommand.Execute(wheel);
            Assert.False(travel.IsPreviewing);
            Assert.True(wheel.IsPreviewing);
            mouse.Wheel -= 240;
            Frame(mouse);
            Assert.Equal(-0.12, preview.Value, 6);
            Assert.Contains(string.Format(Strings.Instance.Mapping_Preview_Down, 12), preview.Caption);
            preview.Stop();
            Assert.False(wheel.IsPreviewing);
            Assert.Null(preview.Pinned);
        }

        [Fact]
        public void Preview_RunsTheLinesOwnSettings_AndSaysWhenItsSteerInputHoldsItStill()
        {
            var mouse = Seam();
            var preview = MouseFamilyPreview.Instance;
            var row = Row("Mouse Travel X", _mouse);
            row.ParamTravelSteer = MappingSource.TravelSteerHeldCenter;
            row.GateDescriptor = "Button 2";
            row.ParamTravelCounts = 1000;
            var choice = new InputChoice { Descriptor = "Mouse Travel X", DeviceGuid = _mouse.ToString() };
            MappingDisplayResolver.DescribeMouseFamilies(new List<InputChoice> { choice });
            row.SelectedInput = choice;
            preview.PinCommand.Execute(new object[] { choice, row });

            mouse.X += 300;
            Frame(mouse);
            Assert.Equal(0.0, preview.Value);
            Assert.StartsWith(Strings.Instance.Mapping_Preview_Waits_Format.Substring(0, 6), preview.Caption);

            mouse.Buttons[2] = true;               // the line's Steer input, the right button
            mouse.X += 300;
            Frame(mouse);
            Assert.Equal(0.3, preview.Value, 6);   // 300 of the line's own 1,000 counts
            mouse.Buttons[2] = false;
            Frame(mouse);
            Assert.Equal(0.0, preview.Value);      // While Held and Center, Centering Off: at center at once
        }

        [Fact]
        public void Preview_OfMouseSpeed_AndMousePosition()
        {
            var mouse = Seam();
            var preview = MouseFamilyPreview.Instance;
            var speed = new InputChoice { Descriptor = "Axis 0" };
            MappingDisplayResolver.DescribeMouseFamilies(new List<InputChoice> { speed });
            preview.PinCommand.Execute(speed);
            for (int i = 0; i < 4; i++)
            {
                mouse.X += 128;                     // 8,000 counts a second
                Frame(mouse);
            }
            Assert.Equal(0.5, preview.Value, 2);
            Assert.StartsWith(Strings.Instance.Mapping_Preview_At_Format.Substring(0, 2), preview.Caption);
            for (int i = 0; i < 4; i++) Frame(mouse);
            Assert.Equal(0.0, preview.Value, 6);   // lets go when the hand stops

            var position = new InputChoice { Descriptor = "Mouse Position Y" };
            MappingDisplayResolver.DescribeMouseFamilies(new List<InputChoice> { position });
            preview.PinCommand.Execute(position);
            mouse.Pointer = (1000, 540 + 96, 1000, 540, 192);
            Frame(mouse);
            Assert.Equal(0.5, preview.Value, 6);
            Assert.Contains(string.Format(Strings.Instance.Mapping_Preview_Down, 50), preview.Caption);
        }

        [Fact]
        public void ThePreviewsArc_RunsFromTheTopTowardTheSideTheValueLeans()
        {
            Assert.Equal(System.Windows.Media.Geometry.Empty, MouseFamilyPreview.ArcOf(0));
            var right = MouseFamilyPreview.ArcOf(1).Bounds;
            var left = MouseFamilyPreview.ArcOf(-1).Bounds;
            Assert.True(right.Left >= 56 && right.Right <= 105, right.ToString());
            Assert.True(left.Right <= 58 && left.Left >= 9, left.ToString());
        }

        // ── Item 40: the catalog ──

        [Fact]
        public void ARowsMouseSettings_AreCatalogEntries_ReadAndSetAsAPersonDoes()
        {
            var pad = new PadViewModel(0);
            var row = pad.Mappings.First(m => m.TargetSettingName == "LeftThumbAxisX");
            row.LoadDescriptor("Mouse Travel X");
            row.PrimarySourceDeviceGuid = _mouse.ToString();
            row.ExtraSources.Add(Added("Axis 1", _mouse));
            int index = pad.Mappings.IndexOf(row);

            var items = new List<LiveItem>();
            MouseSteeringCatalog.AddPad(items, pad, 1);
            string prefix = "row/c1/" + index + "/mouse/";
            var ids = items.Select(i => i.Id).ToList();
            foreach (string key in new[] { "lock", "centering", "steer", "smoothing" })
                Assert.Contains(prefix + key, ids);
            Assert.DoesNotContain(prefix + "return", ids);          // Centering Off shows no Return Time
            Assert.DoesNotContain(prefix + "steerinput", ids);      // nor does Always a Steer Input
            Assert.Contains("row/c1/" + index + "/s1/mouse/sensitivity", ids);
            Assert.Contains("row/c1/" + index + "/s1/mouse/back", ids);
            Assert.All(items, i => Assert.Equal(ReachClass.Free, i.Reach));

            var lockItem = items.Single(i => i.Id == prefix + "lock");
            Assert.Equal("2000", lockItem.Read());
            lockItem.Write("3500");
            Assert.Equal(3500, row.ParamTravelCounts);
            Assert.Throws<ArgumentException>(() => lockItem.Write("wide"));

            var centering = items.Single(i => i.Id == prefix + "centering");
            centering.Write(Strings.Instance.Mapping_MouseTravelCentering_Spring);
            Assert.Equal(MappingSource.TravelCenteringSpring, row.ParamTravelCentering);
            Assert.Equal(Strings.Instance.Mapping_MouseTravelCentering_Spring, centering.Read());
            centering.Write("Off");
            Assert.Equal(MappingSource.TravelCenteringOff, row.ParamTravelCentering);
            Assert.Throws<ArgumentException>(() => centering.Write("Sideways"));

            // A setting the row no longer shows is refused.
            var steer = items.Single(i => i.Id == prefix + "steer");
            row.LoadDescriptor("Axis 3");
            row.PrimarySourceDeviceGuid = _spinner.ToString();
            Assert.Throws<InvalidOperationException>(() => steer.Write("HeldStay"));

            // The live seam carries them.
            var main = new MainViewModel();
            bool created = SettingsManager.SlotCreated[0];
            SettingsManager.SlotCreated[0] = true;
            try
            {
                main.Pads[0].Mappings.First(m => m.TargetSettingName == "LeftThumbAxisX").LoadDescriptor("Mouse Travel Y");
                Assert.Contains(LiveCatalog.Items(), i => i.Id.EndsWith("/mouse/lock", StringComparison.Ordinal)
                    && i.Id.StartsWith("row/c", StringComparison.Ordinal));
            }
            finally
            {
                SettingsManager.SlotCreated[0] = created;
            }
        }
    }
}
