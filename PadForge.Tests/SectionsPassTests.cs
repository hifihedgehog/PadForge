using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Xml.Linq;
using PadForge.Common.Input;
using PadForge.Controls;
using PadForge.Converters;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The parts the sections pass of #574 added to the controller page: a
    /// part card's body, its live views, the tray at a card's foot, a feature
    /// line that has nothing to open, Motion Passthrough's two choices, the
    /// resets of the cards it made, and the bindings that light a line or
    /// feed a live view.
    /// </summary>
    [Collection("CardFoldStateStatics")]
    public class SectionsPassTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static void Sta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(30000), "the test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static XDocument Page() => XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"));

        // ── a part card's body ─────────────────────────────────────────

        /// <summary>A part card's body stands the live view beside the
        /// settings while the card is at least 900 wide: the settings take at
        /// most 560 and the live view the rest, 22 after them. In a narrower
        /// card, two to a row of the page, the live view stands under the
        /// settings across the card. A body with no bound stacks.</summary>
        [Fact]
        public void APartBody_StandsTheLiveViewBesideTheSettingsInAWideCard_AndUnderThemInANarrowOne()
        {
            var (side, settings, live) = PartBodyPanel.Widths(1000, 900, 560, 22);
            Assert.True(side);
            Assert.Equal(560, settings);
            Assert.Equal(418, live);
            (side, settings, live) = PartBodyPanel.Widths(900, 900, 560, 22);
            Assert.True(side);
            Assert.Equal(318, live);
            (side, settings, live) = PartBodyPanel.Widths(899, 900, 560, 22);
            Assert.False(side);
            Assert.Equal(899, settings);
            Assert.Equal(899, live);
            Assert.False(PartBodyPanel.Widths(double.PositiveInfinity, 900, 560, 22).SideBySide);
            Assert.False(PartBodyPanel.Widths(double.NaN, 900, 560, 22).SideBySide);
            // The settings never take more than the body leaves them.
            (side, settings, live) = PartBodyPanel.Widths(500, 400, 560, 22);
            Assert.True(side);
            Assert.Equal(478, settings);
            Assert.Equal(0, live);

            Sta(() =>
            {
                var rows = new Border { Height = 100 };
                var view = new Border { Height = 60 };
                var body = new PartBodyPanel();
                body.Children.Add(rows);
                body.Children.Add(view);
                void Lay(double width)
                {
                    body.Measure(new Size(width, double.PositiveInfinity));
                    body.Arrange(new Rect(0, 0, width, body.DesiredSize.Height));
                }

                Lay(1000);
                Assert.True(body.IsSideBySide);
                Assert.Equal(100, body.DesiredSize.Height);
                Assert.Equal(new Rect(0, 0, 560, 100), LayoutInformation.GetLayoutSlot(rows));
                Assert.Equal(new Rect(582, 0, 418, 60), LayoutInformation.GetLayoutSlot(view));

                Lay(700);
                Assert.False(body.IsSideBySide);
                Assert.Equal(170, body.DesiredSize.Height);
                Assert.Equal(new Rect(0, 0, 700, 100), LayoutInformation.GetLayoutSlot(rows));
                Assert.Equal(new Rect(0, 110, 700, 60), LayoutInformation.GetLayoutSlot(view));

                // A live view that does not show leaves the settings the
                // whole width.
                view.Visibility = Visibility.Collapsed;
                Lay(1000);
                Assert.False(body.IsSideBySide);
                Assert.Equal(new Rect(0, 0, 1000, 100), LayoutInformation.GetLayoutSlot(rows));
            });
        }

        // ── the live views ─────────────────────────────────────────────

        /// <summary>The Gyro card's horizon is drawn from the accelerometer.
        /// The reading points up and gravity is its negative, as the
        /// engine's space projections take it (SourceCoercion.GravityDown,
        /// after GamepadMotion.hpp:635), with X to the right, Y up and Z
        /// toward the player. A roll to the right and a nose that rises read
        /// positive, the reading's length does not count, and a reading too
        /// weak to be gravity draws no horizon.</summary>
        [Fact]
        public void TheHorizon_ReadsRollAndPitchFromTheAccelerometer()
        {
            var level = GyroHorizon.Attitude(0, 1, 0).Value;
            Assert.Equal(0, level.Roll, 9);
            Assert.Equal(0, level.Pitch, 9);
            double s30 = Math.Sin(Math.PI / 6), c30 = Math.Cos(Math.PI / 6);
            var right = GyroHorizon.Attitude(-s30, c30, 0).Value;
            Assert.Equal(30, right.Roll, 9);
            Assert.Equal(0, right.Pitch, 9);
            Assert.Equal(-30, GyroHorizon.Attitude(s30, c30, 0).Value.Roll, 9);
            double s20 = Math.Sin(Math.PI / 9), c20 = Math.Cos(Math.PI / 9);
            var noseUp = GyroHorizon.Attitude(0, c20, -s20).Value;
            Assert.Equal(20, noseUp.Pitch, 9);
            Assert.Equal(0, noseUp.Roll, 9);
            Assert.Equal(-20, GyroHorizon.Attitude(0, c20, s20).Value.Pitch, 9);
            // A reading in m/s² reads as the same reading in g.
            Assert.Equal(30, GyroHorizon.Attitude(-s30 * 9.81, c30 * 9.81, 0).Value.Roll, 9);
            Assert.Null(GyroHorizon.Attitude(0.1, 0.05, 0));
            Assert.Null(GyroHorizon.Attitude(0, 0, 0));
            Assert.Null(GyroHorizon.Attitude(double.NaN, 1, 0));
            Assert.Null(GyroHorizon.Attitude(double.PositiveInfinity, 1, 0));
        }

        /// <summary>A turn rate's bar fills from the middle by the square
        /// root of the rate over the full scale, so a slow turn still shows,
        /// and stops at the end past the full scale. No rate, or no scale,
        /// fills nothing. The scale is 720 degrees a second when nothing sets
        /// it.</summary>
        [Fact]
        public void ARateBar_FillsByTheSquareRootOfTheRate()
        {
            Assert.Equal(0, CenterBar.Reach(0, 720));
            Assert.Equal(1, CenterBar.Reach(720, 720), 9);
            Assert.Equal(-1, CenterBar.Reach(-720, 720), 9);
            Assert.Equal(0.5, CenterBar.Reach(180, 720), 9);
            Assert.Equal(-0.5, CenterBar.Reach(-180, 720), 9);
            Assert.Equal(1, CenterBar.Reach(2000, 720), 9);
            Assert.Equal(0, CenterBar.Reach(double.NaN, 720));
            Assert.Equal(0, CenterBar.Reach(100, 0));
            Sta(() => Assert.Equal(720, new CenterBar().FullScale));
        }

        /// <summary>A finger on the Touchpad card's surface leaves a short
        /// trail: each new place it touches, held to the pad, the last eight
        /// at most. A place the trail already ends on adds nothing, and the
        /// trail goes when the finger lifts.</summary>
        [Fact]
        public void AFingersTrail_FollowsItAndGoesWhenItLifts()
        {
            var trail = new List<Point>();
            TouchSurface.Follow(trail, true, 0.2, 0.3);
            Assert.Equal(new[] { new Point(0.2, 0.3) }, trail);
            TouchSurface.Follow(trail, true, 0.2, 0.3);
            Assert.Single(trail);
            TouchSurface.Follow(trail, true, 1.5, -0.5);
            Assert.Equal(new Point(1, 0), trail[^1]);
            for (int i = 0; i < 20; i++) TouchSurface.Follow(trail, true, i / 20.0, 0.5);
            Assert.Equal(TouchSurface.TrailLength, trail.Count);
            Assert.Equal(8, TouchSurface.TrailLength);
            Assert.Equal(new Point(19 / 20.0, 0.5), trail[^1]);
            TouchSurface.Follow(trail, false, 0.5, 0.5);
            Assert.Empty(trail);
        }

        /// <summary>A PlayStation pad is drawn as the Motion board draws it,
        /// its sides closing in toward its foot, and any other pad, or none,
        /// as a rectangle with rounded corners. The device picked under Driven
        /// by decides, by the app's own test for a Sony pad.</summary>
        [Fact]
        public void APlayStationPad_IsDrawnTapered_AndAnyOtherPadAsARectangle()
        {
            Sta(() =>
            {
                double w = TouchSurface.ViewWidth, h = TouchSurface.ViewHeight;
                var plain = TouchSurface.Outline(w, h, false);
                var tapered = TouchSurface.Outline(w, h, true);
                Assert.Equal(new Rect(6, 6, w - 12, h - 12), plain.Bounds);
                // Near the top both pads run almost to the view's edges.
                Assert.True(plain.FillContains(new Point(10, 14)));
                Assert.True(tapered.FillContains(new Point(10, 14)));
                Assert.True(tapered.FillContains(new Point(w - 10, 14)));
                // Lower down the tapered pad's sides have closed in.
                Assert.True(plain.FillContains(new Point(10, 88)));
                Assert.False(tapered.FillContains(new Point(10, 88)));
                Assert.False(tapered.FillContains(new Point(w - 10, 88)));
                Assert.True(tapered.FillContains(new Point(w / 2, 100)));
                // At another size the tapered outline scales with the view.
                var wide = TouchSurface.Outline(2 * w, 2 * h, true);
                Assert.True(wide.FillContains(new Point(20, 28)));
                Assert.False(wide.FillContains(new Point(20, 176)));
                Assert.False(new TouchSurface().Tapered);
            });

            string service = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("padVm.DeviceTouchTapered = ud != null && GameModEffects.IsSonyPad(ud.VendorId, ud.ProdId);", service);
            Assert.Contains("padVm.DeviceTouchTapered = false;", service);
            Assert.True(GameModEffects.IsSonyPad(0x054C, 0x0CE6));
            Assert.True(GameModEffects.IsSonyPad(0x054C, 0x09CC));
            Assert.False(GameModEffects.IsSonyPad(0x045E, 0x0B13));
        }

        // ── the tray ───────────────────────────────────────────────────

        /// <summary>A part card's tray draws the hairline over its lines and
        /// none under them, since the card draws its own edge there, and its
        /// outline is square at the top, where the card's rows go on, and
        /// round at the lower corners, the card's inner ones.</summary>
        [Fact]
        public void ATray_HasOneHairlineAndRoundsOnlyItsLowerCorners()
        {
            Assert.Equal(1 + 40 + 1 + 50, FoldList.HeightFor(new double[] { 40, 50 }, 0, tray: true));
            Assert.Equal(2 + 40 + 1 + 50, FoldList.HeightFor(new double[] { 40, 50 }, 0));
            Assert.Equal(0, FoldList.HeightFor(Array.Empty<double>(), 0, tray: true));
            Sta(() =>
            {
                var shape = FoldList.TrayShape(new Rect(0, 0, 200, 100), 11);
                Assert.Equal(new Rect(0, 0, 200, 100), shape.Bounds);
                Assert.True(shape.FillContains(new Point(0.5, 0.5)));
                Assert.True(shape.FillContains(new Point(199.5, 0.5)));
                Assert.False(shape.FillContains(new Point(0.5, 99.5)));
                Assert.False(shape.FillContains(new Point(199.5, 99.5)));
                Assert.True(shape.FillContains(new Point(100, 99.5)));
                // A corner larger than half the tray is held to half of it.
                Assert.Equal(new Rect(0, 0, 20, 10), FoldList.TrayShape(new Rect(0, 0, 20, 10), 11).Bounds);
                Assert.False(new FoldList().IsTray);
            });
        }

        // ── a feature line ─────────────────────────────────────────────

        /// <summary>A feature line with nothing to open (the EQ or the
        /// Limiter while off, Lighting Control for a device that offers no
        /// hold) is its switch alone: no chevron, nothing under it, its name
        /// takes no click and no Tab stop, and a screen reader is offered no
        /// way to open it. The rule is the template's last, so it outranks an
        /// open state kept from when the line could open.</summary>
        [Fact]
        public void ALineWithNothingToOpen_DoesNotOpen()
        {
            Sta(() =>
            {
                var line = new FoldCard();
                Assert.True(line.CanOpen);
                var peer = UIElementAutomationPeer.CreatePeerForElement(line);
                Assert.NotNull(peer.GetPattern(PatternInterface.ExpandCollapse));
                line.CanOpen = false;
                Assert.Null(peer.GetPattern(PatternInterface.ExpandCollapse));
                // A More row always has its card's settings to open.
                Assert.NotNull(UIElementAutomationPeer.CreatePeerForElement(new MoreRow()).GetPattern(PatternInterface.ExpandCollapse));
            });

            var styles = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml"));
            var partLine = styles.Descendants().Single(e => (string)e.Attribute(X + "Key") == "PartLine");
            var last = partLine.Descendants().Single(e => e.Name.LocalName == "ControlTemplate.Triggers").Elements().Last();
            Assert.Equal(("CanOpen", "False"), ((string)last.Attribute("Property"), (string)last.Attribute("Value")));
            Assert.Equal(new[] { "Chevron Visibility=Hidden", "ExpandSite Visibility=Collapsed", "HeaderSite IsHitTestVisible=False", "HeaderSite Focusable=False" },
                last.Elements().Select(s => (string)s.Attribute("TargetName") + " " + (string)s.Attribute("Property") + "=" + (string)s.Attribute("Value")));

            // The EQ and the Limiter open while they are on, which is when
            // their settings show.
            var page = Page();
            foreach (var (name, flag) in new[] { ("ParametricEqFoldCard", "DeviceConfig.AudioEqEnabled"), ("LimiterFoldCard", "DeviceConfig.AudioLimiterEnabled") })
            {
                var fold = page.Descendants().Single(e => (string)e.Attribute(X + "Name") == name);
                Assert.Equal("{Binding " + flag + "}", (string)fold.Attribute("CanOpen"));
                var settings = Assert.Single(Assert.Single(fold.Elements(), e => e.Name.LocalName == "StackPanel").Elements());
                Assert.Contains(flag, (string)settings.Attribute("Visibility"));
            }
            // Every other line with a switch has settings that always show.
            foreach (var fold in page.Descendants().Where(e => e.Name.LocalName == "FoldCard"
                && e.Elements().Any(c => c.Name.LocalName == "FoldCard.Switch") && e.Attribute("CanOpen") == null
                && (string)e.Attribute(X + "Name") != "PeripheralLightingCard"))
            {
                var settings = Assert.Single(fold.Elements(), e => e.Name.LocalName == "StackPanel").Elements().ToList();
                Assert.Contains(settings, e => e.Attribute("Visibility") == null);
            }
        }

        /// <summary>Every binding that lights a feature line, lets it open or
        /// feeds a part's live view names a property the page's view model
        /// has, of the type the target takes. A misspelled path fails
        /// without a word in WPF, and the icon would stay steel or the view
        /// would stay still.</summary>
        [Fact]
        public void EveryLinesStateAndEveryLiveReading_NamesAPropertyOfThePage()
        {
            static Type PathType(string path)
            {
                Type type = typeof(PadViewModel);
                foreach (string part in path.Split('.'))
                {
                    var property = type.GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
                    Assert.True(property != null, path + ": " + type.Name + " has no " + part);
                    type = property.PropertyType;
                }
                return type;
            }
            string Path(string binding)
            {
                var m = Regex.Match(binding ?? "", @"^\{Binding ([\w.]+)\}$");
                Assert.True(m.Success, binding);
                return m.Groups[1].Value;
            }

            var page = Page();
            int checkedPaths = 0;
            foreach (var line in page.Descendants().Where(e => e.Name.LocalName == "FoldCard"))
            {
                foreach (string attribute in new[] { "IsOn", "CanOpen" })
                {
                    if (line.Attribute(attribute) == null) continue;
                    Assert.Equal(typeof(bool), PathType(Path((string)line.Attribute(attribute))));
                    checkedPaths++;
                }
                var any = line.Elements().SingleOrDefault(e => e.Name.LocalName == "FoldCard.IsOn");
                if (any == null) continue;
                Assert.Contains("AnyOnConverter", any.ToString());
                foreach (var b in any.Descendants().Where(e => e.Name.LocalName == "Binding"))
                {
                    Assert.Equal(typeof(bool), PathType((string)b.Attribute("Path")));
                    checkedPaths++;
                }
            }
            // Thirteen lines light by one flag, Lock Feedback by any of its
            // four cues, and the EQ and the Limiter open by their flag.
            Assert.Equal(19, checkedPaths);

            var horizon = Assert.Single(page.Descendants(), e => e.Name.LocalName == "GyroHorizon");
            foreach (string axis in new[] { "AccelX", "AccelY", "AccelZ" })
                Assert.Equal(typeof(double), PathType(Path((string)horizon.Attribute(axis))));
            var bars = page.Descendants().Where(e => e.Name.LocalName == "CenterBar").ToList();
            Assert.Equal(3, bars.Count);
            Assert.All(bars, bar => Assert.Equal(typeof(double), PathType(Path((string)bar.Attribute("Value")))));
            var surface = Assert.Single(page.Descendants(), e => e.Name.LocalName == "TouchSurface");
            foreach (string finger in new[] { "X0", "Y0", "X1", "Y1" })
                Assert.Equal(typeof(float), PathType(Path((string)surface.Attribute(finger))));
            foreach (string finger in new[] { "Down0", "Down1", "Tapered" })
                Assert.Equal(typeof(bool), PathType(Path((string)surface.Attribute(finger))));

            // The surface reads the touchpad of the device picked under
            // Driven by, its first pad's first two fingers, which the 30 Hz
            // lane writes, a position before its finger's contact. With no
            // device picked no finger is down.
            string service = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("if (down0) { padVm.DeviceTouchX0 = pad.FingerX[0]; padVm.DeviceTouchY0 = pad.FingerY[0]; }", service);
            Assert.Contains("if (down1) { padVm.DeviceTouchX1 = pad.FingerX[1]; padVm.DeviceTouchY1 = pad.FingerY[1]; }", service);
            Assert.True(service.IndexOf("padVm.DeviceTouchY0 = pad.FingerY[0]; }", StringComparison.Ordinal)
                < service.IndexOf("padVm.DeviceTouchDown0 = down0;", StringComparison.Ordinal));
            Assert.Equal(2, Regex.Matches(service, @"padVm\.DeviceTouchDown0 = (down0|false);").Count);
        }

        /// <summary>The Lock Feedback line lights while any of its cues is
        /// on: a stick at full lock can rumble, buzz the triggers, flash the
        /// lightbar or stiffen the adaptive triggers.</summary>
        [Fact]
        public void AnyOn_IsOnWhileAnyFlagIs()
        {
            var c = new AnyOnConverter();
            object Any(params object[] values) => c.Convert(values, typeof(bool), null, CultureInfo.InvariantCulture);
            Assert.Equal(true, Any(false, false, true, false));
            Assert.Equal(false, Any(false, false, false, false));
            // Read as the line's On or Off reads a flag: a number or an enum
            // is on when it is not zero, and a word or no value yet is off.
            Assert.Equal(true, Any(0, DayOfWeek.Monday));
            Assert.Equal(false, Any(0, DayOfWeek.Sunday, "On", DependencyProperty.UnsetValue, null));
            Assert.Equal(false, Any());
            Assert.Equal(false, c.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture));
            Assert.Throws<NotSupportedException>(() => c.ConvertBack(true, new[] { typeof(bool) }, null, CultureInfo.InvariantCulture));
        }

        // ── Motion Passthrough and the cards' resets ───────────────────

        /// <summary>A part body with no least width set stands its two parts
        /// side by side whenever both fit: the settings at their width, the
        /// gap, and the live view at the width it asks for. One short of
        /// that, the live view goes under the settings. A stick's card is
        /// such a body: its live view is an instrument of a fixed size and
        /// its settings column is sized for the language, so no one number
        /// is right for both.</summary>
        [Fact]
        public void APartBodyWithNoLeastWidth_StandsItsPartsSideBySideWheneverBothFit()
        {
            Assert.Equal(708, PartBodyPanel.LeastSideBySide(double.NaN, 360, 22, 326));
            Assert.Equal(900, PartBodyPanel.LeastSideBySide(900, 360, 22, 326));
            Sta(() =>
            {
                var settings = new Border { Height = 132 };
                var live = new Border { Width = 326, Height = 300, HorizontalAlignment = HorizontalAlignment.Left };
                var body = new PartBodyPanel { SideBySideWidth = double.NaN, SettingsWidth = 360, Gap = 22, StackGap = 16 };
                body.Children.Add(settings);
                body.Children.Add(live);
                body.Measure(new Size(708, double.PositiveInfinity));
                body.Arrange(new Rect(0, 0, 708, body.DesiredSize.Height));
                Assert.True(body.IsSideBySide);
                Assert.Equal(300, body.DesiredSize.Height);
                Assert.Equal(new Rect(0, 0, 360, 300), LayoutInformation.GetLayoutSlot(settings));
                Assert.Equal(382, LayoutInformation.GetLayoutSlot(live).X);

                body.Measure(new Size(707, double.PositiveInfinity));
                body.Arrange(new Rect(0, 0, 707, body.DesiredSize.Height));
                Assert.False(body.IsSideBySide);
                Assert.Equal(132 + 16 + 300, body.DesiredSize.Height);
                Assert.Equal(new Rect(0, 148, 707, 300), LayoutInformation.GetLayoutSlot(live));
            });
        }

        /// <summary>A legend tag of the 3D and 2D pictures lights while its
        /// control's output is on, an ember edge and name, in the frame the
        /// picture lights the control. It is the tag's one light: a press
        /// used to flash the tag blue for 250 ms while a dot beside it
        /// showed the output, and the flash went out with the button still
        /// held (owner, 2026-11-14). The 3D view reads a Nintendo row's raw
        /// name as its anchor does, so that slot's tags light too. The 2D
        /// view's timer did the flash and the dot and nothing else, and is
        /// gone.</summary>
        [Fact]
        public void ALegendTag_LightsWhileItsControlsOutputIsOn()
        {
            foreach (string view in new[] { "ControllerModelView", "ControllerModel2DView" })
            {
                string tags = ExtrasPageTests.RepoText("PadForge.App", "Views", view + ".Annotations.cs");
                Assert.DoesNotContain("FlashUntil", tags);
                Assert.DoesNotContain("EmberDot", tags);
                Assert.DoesNotContain("IsInputActive", tags);
                Assert.Contains("LightChip(chip, ChipOutputOn(chip.Row.TargetSettingName));", tags);
                // A tag made while its control is held is lit at once.
                Assert.Contains("LightChip(chip, ChipOutputOn(row.TargetSettingName));", tags);
                Assert.Contains("chip.Border.SetResourceReference(Border.BorderBrushProperty, on ? \"EmberBrush\" : \"SteelLineSoftBrush\");", tags);
                Assert.Contains("chip.Text.SetResourceReference(TextBlock.ForegroundProperty, on ? \"EmberBrush\" : \"TextFillColorSecondaryBrush\");", tags);
                string code = ExtrasPageTests.RepoText("PadForge.App", "Views", view + ".xaml.cs");
                Assert.Matches(@"private void OnRendering\(object sender, EventArgs e\)\s*\{[\s\S]*?UpdateAnnotationChipLights\(\);\s*\}", code);
            }
            string model = ExtrasPageTests.RepoText("PadForge.App", "Views", "ControllerModelView.Annotations.cs");
            Assert.Matches(@"private bool ChipOutputOn\(string target\)\s*\{[\s\S]*?Models2D\.NintendoPreviewMap\.ToPreview\(target, _vm\?\.ProfileId\);[\s\S]*?return GetButtonState\(target\);", model);
            Assert.DoesNotContain("DispatcherTimer", ExtrasPageTests.RepoText("PadForge.App", "Views", "ControllerModel2DView.Annotations.cs"));
        }

        /// <summary>A line of gray text under a row starts where the row's
        /// control does, as the boards draw it: past its section's label,
        /// the label's width and the room after it. Those are what the game
        /// reads under Motion Passthrough, the last calibration under
        /// Calibration, and Screen Color's line and status under the
        /// lighting Mode.</summary>
        [Fact]
        public void ALineUnderARow_StartsWhereTheRowsControlDoes()
        {
            var page = Page();
            var passthrough = page.Descendants().Single(e => (string)e.Attribute(X + "Name") == "GyroPassthroughCard");
            var lines = new[]
            {
                passthrough.Elements().Single(e => e.Name.LocalName == "TextBlock"),
                page.Descendants().Single(e => e.Name.LocalName == "TextBlock" && (string)e.Attribute("Text") == "{Binding GyroCalibrationLabel}"),
                page.Descendants().Single(e => e.Name.LocalName == "TextBlock"
                    && ((string)e.Attribute("Text") ?? "").StartsWith("{Binding Pad_Lighting_ScreenHint,", StringComparison.Ordinal)),
                page.Descendants().Single(e => (string)e.Attribute(X + "Name") == "ScreenColorStatus"),
            };
            foreach (var line in lines)
            {
                var label = line.Ancestors()
                    .SelectMany(a => a.Elements().Where(r => r.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)))
                    .SelectMany(r => r.Elements())
                    .First(s => s.Name.LocalName == "Style" && (string)s.Attribute(X + "Key") == "DzLabel");
                string Setter(string property) => (string)label.Elements()
                    .Single(s => (string)s.Attribute("Property") == property).Attribute("Value");
                double column = double.Parse(Setter("Width"), CultureInfo.InvariantCulture)
                    + double.Parse(Setter("Margin").Split(',')[2], CultureInfo.InvariantCulture);
                var margin = ((string)line.Attribute("Margin")).Split(',');
                Assert.Equal(column, double.Parse(margin[0], CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Motion Passthrough is Raw or Tuned. Raw is the other
        /// side of the one stored flag: choosing it turns the tuning off,
        /// choosing Tuned turns it on, and the radio group clearing the
        /// choice that was not picked writes nothing. A change of the flag
        /// is told for both choices.</summary>
        [Fact]
        public void MotionPassthrough_IsRawOrTuned()
        {
            var vm = new PadViewModel(0);
            Assert.False(vm.GyroApplyTuningToPassthrough);
            Assert.True(vm.GyroPassthroughRaw);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.GyroApplyTuningToPassthrough = true;
            Assert.False(vm.GyroPassthroughRaw);
            Assert.Contains(nameof(PadViewModel.GyroPassthroughRaw), raised);
            vm.GyroPassthroughRaw = false;
            Assert.True(vm.GyroApplyTuningToPassthrough);
            raised.Clear();
            vm.GyroPassthroughRaw = true;
            Assert.False(vm.GyroApplyTuningToPassthrough);
            Assert.Contains(nameof(PadViewModel.GyroApplyTuningToPassthrough), raised);
            Assert.Contains(nameof(PadViewModel.GyroPassthroughRaw), raised);
        }

        /// <summary>The Reset All of each card the pass made resets what the
        /// card shows and its More: the Gyro card its passthrough and its
        /// sensitivity with the units, the inverts and Real-World
        /// Calibration, and none of its lines' settings, which have their own
        /// resets, nor the calibration, a measurement that Reset Calibration
        /// clears. Both Sticks resets Steering Lock Feedback and Flick Stick.
        /// The IR Pointer card resets its mode with the mode's speed and its
        /// tuning under More.</summary>
        [Fact]
        public void EachNewCardsResetAll_ResetsWhatTheCardHolds()
        {
            var vm = new PadViewModel(0);
            vm.GyroApplyTuningToPassthrough = true;
            vm.GyroSensitivityH = 3;
            vm.GyroSensitivityV = 2;
            vm.GyroSensitivityUnits = "DegPerScreenTurn";
            vm.GyroInvertPitch = true;
            vm.GyroInvertYaw = true;
            vm.GyroInvertRoll = true;
            vm.GyroRealWorldCalibration = 1.5;
            vm.GyroSpace = "World";
            vm.ResetGyroCardCommand.Execute(null);
            Assert.True(vm.GyroPassthroughRaw);
            Assert.Equal(1.0, vm.GyroSensitivityH);
            Assert.Equal(1.0, vm.GyroSensitivityV);
            Assert.Equal("Multiplier", vm.GyroSensitivityUnits);
            Assert.False(vm.GyroInvertPitch || vm.GyroInvertYaw || vm.GyroInvertRoll);
            Assert.Equal(0, vm.GyroRealWorldCalibration);
            Assert.Equal("World", vm.GyroSpace);

            vm.SteeringLockRumbleEnabled = true;
            vm.SteeringLockLightbarEnabled = true;
            vm.FlickTime = 0.3;
            vm.FlickOnEngage = true;
            vm.ResetBothSticksCommand.Execute(null);
            Assert.False(vm.SteeringLockRumbleEnabled);
            Assert.False(vm.SteeringLockLightbarEnabled);
            Assert.Equal(0.1, vm.FlickTime);
            Assert.False(vm.FlickOnEngage);

            vm.PointerMode = "FpsMouse";
            vm.PointerFpsSpeed = 50;
            vm.IrSensorBarPos = 1;
            vm.IrSensorBarCompPercent = 30;
            vm.IrSmoothingPercent = 40;
            vm.ResetIrPointerCardCommand.Execute(null);
            Assert.Equal("Mouse", vm.PointerMode);
            Assert.Equal(35, vm.PointerFpsSpeed);
            Assert.Equal(0, vm.IrSensorBarPos);
            Assert.Equal(0, vm.IrSensorBarCompPercent);
            Assert.Equal(0, vm.IrSmoothingPercent);
        }
    }
}
