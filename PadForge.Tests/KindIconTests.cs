using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using PadForge.Common;
using PadForge.Controls;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A wheel and a joystick show their own icon wherever a device's kind
    /// shows.
    ///
    /// <para>Neither Segoe MDL2 Assets nor Segoe Fluent Icons has a glyph for
    /// a wheel or a joystick, so both drew the gamepad's, and a wheel in the
    /// Devices list read as a gamepad (owner, 2026-11-17). Each now takes a
    /// line drawing: the steering cards' wheel, and a joystick in the same
    /// line.</para>
    /// </summary>
    public class KindIconTests
    {
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        [Fact]
        public void AWheelAndAJoystickDrawAShapeInPlaceOfTheGamepad()
        {
            var wheel = DeviceTypeGlyph.ShapeFor(InputDeviceType.Driving);
            var joystick = DeviceTypeGlyph.ShapeFor(InputDeviceType.Joystick);
            Assert.NotNull(wheel);
            Assert.NotNull(joystick);
            Assert.NotSame(wheel, joystick);
            // A flight stick is a joystick, as the Devices list files it.
            Assert.Same(joystick, DeviceTypeGlyph.ShapeFor(InputDeviceType.Flight));
            Assert.True(wheel.IsFrozen && joystick.IsFrozen);
            foreach (int kind in new[] { InputDeviceType.Driving, InputDeviceType.Joystick, InputDeviceType.Flight })
                Assert.Equal("", DeviceTypeGlyph.For(kind));

            // Every other kind keeps its glyph and has no shape.
            foreach (int kind in new[] { InputDeviceType.Gamepad, InputDeviceType.FirstPerson, InputDeviceType.Supplemental,
                         InputDeviceType.Keyboard, InputDeviceType.Mouse, InputDeviceType.Touchpad, 0 })
            {
                Assert.Null(DeviceTypeGlyph.ShapeFor(kind));
                Assert.NotEqual("", DeviceTypeGlyph.For(kind));
            }
        }

        /// <summary>A wheel device and the steering cards draw one wheel.</summary>
        [Fact]
        public void TheWheelIsTheSteeringCardsWheel()
        {
            string icons = ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml");
            var card = Regex.Match(icons, "<StreamGeometry x:Key=\"SteeringWheelGeometry\">([^<]*)</StreamGeometry>");
            Assert.True(card.Success, "ControllerIcons.xaml has no SteeringWheelGeometry");
            Assert.Equal(DeviceTypeGlyph.SteeringWheelPath, card.Groups[1].Value);
        }

        [Fact]
        public void TheShapeFillsTheCardsSquareAndHidesTheGlyph()
        {
            RunSta(() =>
            {
                var icon = new KindIcon { Glyph = "", FontSize = 18 };
                icon.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var plain = new GlyphText { Text = "", FontSize = 18, FontFamily = new FontFamily("Segoe MDL2 Assets") };
                plain.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                // With a glyph it takes the room a GlyphText takes.
                Assert.Equal(plain.DesiredSize, icon.DesiredSize);

                icon.Shape = DeviceTypeGlyph.ShapeFor(InputDeviceType.Driving);
                icon.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                // The card's 17 beside a glyph of 18.
                Assert.Equal(new Size(17, 17), icon.DesiredSize);
                Assert.Equal(Visibility.Collapsed, ((UIElement)icon.Child).Visibility);

                icon.Shape = null;
                Assert.Equal(Visibility.Visible, ((UIElement)icon.Child).Visibility);
            });
        }

        [Fact]
        public void TheGlyphTakesTheIconsSizeAndColor()
        {
            RunSta(() =>
            {
                var icon = new KindIcon { Glyph = "", FontSize = 13, Foreground = Brushes.OrangeRed };
                var glyph = (GlyphText)icon.Child;
                Assert.Equal("", glyph.Text);
                Assert.Equal(13, glyph.FontSize);
                Assert.Same(Brushes.OrangeRed, glyph.Foreground);
                Assert.Equal("Segoe MDL2 Assets", glyph.FontFamily.Source);
            });
        }
    }
}
