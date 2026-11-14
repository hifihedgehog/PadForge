using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Xml.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Models3D;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The ring on the picked control (#574). Every board with a picture
    /// draws an ember ring on the control the bar under the picture is
    /// about, and the pictures marked nothing. The page draws one ring over
    /// whichever picture shows, and each picture says where its controls
    /// are drawn. These hold the ring's shape, which rows a pick rings,
    /// where the VR hands and the 3D model say a control is, and the page's
    /// markup and wiring.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PickRingTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static XDocument Page() => XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"));

        private static string CodeBehind() => ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs");

        private static string Name(XElement e) => (string)e.Attribute(X + "Name");

        private static XElement Named(XDocument page, string name)
            => Assert.Single(page.Descendants(), e => Name(e) == name);

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the layout run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Lay(FrameworkElement element, double width, double height)
        {
            var size = new Size(width, height);
            element.Measure(size);
            element.Arrange(new Rect(size));
            element.UpdateLayout();
        }

        private static void Near(Rect expected, Rect actual, double within = 0.01)
        {
            Assert.True(Math.Abs(expected.X - actual.X) <= within && Math.Abs(expected.Y - actual.Y) <= within
                && Math.Abs(expected.Width - actual.Width) <= within && Math.Abs(expected.Height - actual.Height) <= within,
                $"expected {expected}, got {actual}");
        }

        // ── the ring's shape ───────────────────────────────────────────

        /// <summary>The ring is a line 2 wide that stands 3 off the control,
        /// with a band 4 wide outside the line, as the boards draw it. A
        /// round control's ring is as round as its box allows. A control
        /// that lies on part pixels is ringed on whole ones, outward.</summary>
        [Fact]
        public void TheRingStandsOffTheControl_WithABandOutsideItsLine()
        {
            Assert.Equal(3, PicturePick.Gap);
            Assert.Equal(2, PicturePick.LineWidth);
            Assert.Equal(4, PicturePick.BandWidth);

            var round = PicturePick.Place(new Rect(100, 200, 40, 40), round: true);
            Assert.Equal(new Rect(95, 195, 50, 50), round.Line);
            Assert.Equal(25, round.LineRadius);
            Assert.Equal(new Rect(91, 191, 58, 58), round.Band);
            Assert.Equal(29, round.BandRadius);

            // A round control wider than tall: the ends are round.
            var pill = PicturePick.Place(new Rect(0, 0, 80, 20), round: true);
            Assert.Equal(new Rect(-5, -5, 90, 30), pill.Line);
            Assert.Equal(15, pill.LineRadius);

            var part = PicturePick.Place(new Rect(10.4, 20.6, 30.2, 10.1), round: false);
            Assert.Equal(new Rect(5, 15, 41, 21), part.Line);
            Assert.Equal(new Rect(1, 11, 49, 29), part.Band);
        }

        /// <summary>The ring's line stays inside the picture. A trigger at
        /// the top of the 2D model had its ring cut open there. A control
        /// well inside the picture is ringed as in a picture of no stated
        /// size, and one that lies outside it has no ring.</summary>
        [Fact]
        public void TheRingsLineStaysInsideThePicture()
        {
            var picture = new Size(1407, 597);
            var top = PicturePick.Place(new Rect(390, 0, 86, 90), round: false, picture);
            Assert.Equal(new Rect(385, 0, 96, 95), top.Line);
            Assert.Equal(14, top.LineRadius);
            Assert.Equal(new Rect(381, -4, 104, 103), top.Band);

            var corner = PicturePick.Place(new Rect(1380, 570, 27, 27), round: true, picture);
            Assert.Equal(new Rect(1375, 565, 32, 32), corner.Line);
            Assert.Equal(16, corner.LineRadius);

            var inside = new Rect(100, 200, 40, 40);
            Assert.Equal(PicturePick.Place(inside, round: true), PicturePick.Place(inside, round: true, picture));

            Assert.False(PicturePick.Place(new Rect(2000, 100, 40, 40), round: true, picture).Line.Width > 0);
            Assert.False(PicturePick.Place(new Rect(100, -90, 40, 40), round: true, picture).Line.Width > 0);
            // A picture with no size yet: the ring is as the control asks.
            Assert.Equal(PicturePick.Place(inside, round: true), PicturePick.Place(inside, round: true, new Size(0, 0)));
        }

        /// <summary>A control that is not round gets a box with corners a
        /// fifth of the ring's shorter side, no less than 6 and no more
        /// than 14: a key's 7, a VR trigger's 14.</summary>
        [Theory]
        [InlineData(20, 30, 6)]
        [InlineData(10, 10, 6)]
        [InlineData(25, 40, 7)]
        [InlineData(40, 90, 10)]
        [InlineData(60, 100, 14)]
        [InlineData(200, 300, 14)]
        public void ABoxedControlsCorners_AreAFifthOfTheRingsShorterSide(double width, double height, double radius)
        {
            var place = PicturePick.Place(new Rect(0, 0, width, height), round: false);
            Assert.Equal(radius, place.LineRadius, 6);
            Assert.Equal(radius + PicturePick.BandWidth, place.BandRadius, 6);
        }

        // ── which rows a pick rings ────────────────────────────────────

        /// <summary>The pick rings the control its bar is about: one row's
        /// control, or with a D-pad direction or a stick's axis picked,
        /// every row's of that control. A direction's own name picks its
        /// row. Nothing is ringed with nothing picked, as the picture
        /// opens, and a pick that names no control rings nothing.</summary>
        [Fact]
        public void ThePickRingsEveryRowOfAStickOrADPad()
        {
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            Assert.Empty(vm.PickedTargets());

            vm.PickedTarget = "ButtonB";
            Assert.Equal(new[] { "ButtonB" }, vm.PickedTargets());

            vm.PickedTarget = "DPadUp";
            Assert.Equal(new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" }.OrderBy(t => t),
                vm.PickedTargets().OrderBy(t => t));

            // A stick is its two axes and its click.
            vm.PickedTarget = "LeftThumbAxisXNeg";
            Assert.Equal(new[] { "LeftThumbAxisX", "LeftThumbAxisY", "LeftThumbButton" },
                vm.PickedTargets().OrderBy(t => t, StringComparer.Ordinal));
            Assert.DoesNotContain("RightThumbAxisX", vm.PickedTargets());
            Assert.DoesNotContain("RightThumbButton", vm.PickedTargets());
            vm.PickedTarget = "RightThumbButton";
            Assert.Equal(new[] { "RightThumbAxisX", "RightThumbAxisY", "RightThumbButton" },
                vm.PickedTargets().OrderBy(t => t, StringComparer.Ordinal));

            vm.PickedTarget = "LeftTrigger";
            Assert.Equal(new[] { "LeftTrigger" }, vm.PickedTargets());

            vm.PickedTarget = "NoSuchControl";
            Assert.Empty(vm.PickedTargets());
            vm.PickedTarget = "LeftTrigger";
            vm.PickedTarget = null;
            Assert.Empty(vm.PickedTargets());
        }

        /// <summary>A custom Extended slot's sticks past the second share
        /// one category, and so do its POVs. A pick there is one control's
        /// rows: the bar under the picture and the ring are on the stick or
        /// the POV that was picked, and a pick of the next one builds the
        /// rows again.</summary>
        [Fact]
        public void OnASlotWithSeveralSticksOrPovs_APickIsOneControls()
        {
            var vm = new PadViewModel(0) { OutputType = VirtualControllerType.Extended };
            vm.ExtendedConfig.TriggerCount = 0;
            vm.ExtendedConfig.ThumbstickCount = 3;
            vm.ExtendedConfig.PovCount = 2;
            vm.RebuildMappings();
            vm.ApplyMappingPickerFilter();
            List<MappingItem> Shown() => System.Windows.Data.CollectionViewSource.GetDefaultView(vm.Mappings)
                .Cast<MappingItem>().ToList();

            var sticks = vm.Mappings.Where(m => m.Category is MappingCategory.LeftStick or MappingCategory.RightStick).ToList();
            Assert.Equal(new[] { "Stick0", "Stick0", "Stick1", "Stick1", "Stick2", "Stick2" }, sticks.Select(m => m.Control));
            Assert.Equal(new[] { MappingCategory.LeftStick, MappingCategory.RightStick, MappingCategory.RightStick },
                new[] { sticks[0].Category, sticks[2].Category, sticks[4].Category });

            vm.PickedTarget = sticks[2].TargetSettingName;
            Assert.Equal(new[] { sticks[2].TargetSettingName, sticks[3].TargetSettingName }, vm.PickedTargets());
            Assert.Equal(new[] { sticks[2], sticks[3] }, Shown());
            // The third stick's own direction name picks its row, and the
            // rows under the picture are the third stick's.
            vm.PickedTarget = sticks[5].NegSettingName;
            Assert.Equal(new[] { sticks[4].TargetSettingName, sticks[5].TargetSettingName }, vm.PickedTargets());
            Assert.Equal(new[] { sticks[4], sticks[5] }, Shown());

            var povs = vm.Mappings.Where(m => m.Category == MappingCategory.DPad).ToList();
            Assert.Equal(new[] { "Pov0", "Pov0", "Pov0", "Pov0", "Pov1", "Pov1", "Pov1", "Pov1" }, povs.Select(m => m.Control));
            vm.PickedTarget = povs[6].TargetSettingName;
            Assert.Equal(povs.Skip(4).Select(m => m.TargetSettingName), vm.PickedTargets());
            Assert.Equal(povs.Skip(4), Shown());
            vm.PickedTarget = povs[1].TargetSettingName;
            Assert.Equal(povs.Take(4), Shown());

            // A gamepad's rows name no control: a category is one there.
            var pad = new PadViewModel(1);
            Assert.All(pad.Mappings, m => Assert.Null(m.Control));
            Assert.True(PadViewModel.SameControl(pad.Mappings.First(m => m.TargetSettingName == "DPadUp"),
                pad.Mappings.First(m => m.TargetSettingName == "DPadLeft")));
            Assert.False(PadViewModel.SameControl(pad.Mappings.First(m => m.TargetSettingName == "LeftThumbAxisX"),
                pad.Mappings.First(m => m.TargetSettingName == "RightThumbAxisX")));
            Assert.False(PadViewModel.SameControl(pad.Mappings.First(m => m.TargetSettingName == "ButtonA"),
                pad.Mappings.First(m => m.TargetSettingName == "ButtonA")));
        }

        // ── where a picture says a control is ──────────────────────────

        /// <summary>Every picture of the Buttons section says where its
        /// controls are, and the page asks the one that shows: the touch
        /// sensors' capsules first, since they lie over the model.</summary>
        [Fact]
        public void EveryPictureOfTheButtonsSectionSaysWhereItsControlsAre()
        {
            foreach (var picture in new[]
                     {
                         typeof(ControllerModelView), typeof(ControllerModel2DView), typeof(ControllerSchematicView),
                         typeof(MidiPreviewView), typeof(KBMPreviewView), typeof(VRPreviewView), typeof(TouchSensorStrip),
                     })
                Assert.True(typeof(IPictureWithPick).IsAssignableFrom(picture), picture.Name);

            string code = CodeBehind();
            Assert.Matches(@"private IEnumerable<IPictureWithPick> PicturesWithPick\(\)\s*\{\s*yield return TouchSensors;\s*yield return ControllerModel3D;\s*yield return ControllerModel2D;\s*yield return ControllerSchematic;\s*yield return MidiPreview;\s*yield return KBMPreview;\s*yield return VRPreview;\s*\}", code);
            // Each of them is a picture in the picture's host.
            var page = Page();
            var host = Named(page, "PictureHost");
            foreach (string name in new[] { "TouchSensors", "ControllerModel3D", "ControllerModel2D", "ControllerSchematic", "MidiPreview", "KBMPreview", "VRPreview" })
                Assert.Same(host, Named(page, name).Parent);
        }

        /// <summary>The VR hands: a control is the element that flashes
        /// while its target records. A touch ring is its button's, a click
        /// its trigger's or grip's, and a stick's directions and click are
        /// the stick. Buttons and sticks are round, a trigger and a grip
        /// are not. Nothing is answered before the hands are drawn, and the
        /// picture says when they are.</summary>
        [Fact]
        public void TheVrHandsSayWhereEachControlIsDrawn()
        {
            RunSta(() =>
            {
                var hands = new VRPreviewView();
                var picture = (IPictureWithPick)hands;
                Assert.False(picture.TryGetPickBounds("VrLA", out _, out _));

                int said = 0;
                picture.PickBoundsChanged += (s, e) => said++;
                typeof(VRPreviewView).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(hands, null);
                Assert.Equal(1, said);

                // At the art's own size a control is where the art has it.
                Lay(hands, 975, 726);
                (string Target, Rect At, bool Round)[] controls =
                {
                    ("VrLA", new Rect(119, 181, 61, 61), true),
                    ("VrLATouch", new Rect(119, 181, 61, 61), true),
                    ("VrRB", new Rect(814, 77, 62, 62), true),
                    ("VrLSystem", new Rect(200, 246, 43, 44), true),
                    ("VrLStickX", new Rect(231, 107, 64, 64), true),
                    ("VrLStickXNeg", new Rect(231, 107, 64, 64), true),
                    ("VrLStickYNeg", new Rect(231, 107, 64, 64), true),
                    ("VrLStickClick", new Rect(231, 107, 64, 64), true),
                    ("VrRTrigger", new Rect(592, 360, 89, 99), false),
                    ("VrRTriggerClick", new Rect(592, 360, 89, 99), false),
                    ("VrRGrip", new Rect(510, 396, 50, 230), false),
                    ("VrRGripClick", new Rect(510, 396, 50, 230), false),
                };
                foreach (var (target, at, round) in controls)
                {
                    Assert.True(picture.TryGetPickBounds(target, out Rect bounds, out bool isRound), target);
                    Near(at, bounds);
                    Assert.True(round == isRound, target);
                }
                Assert.False(picture.TryGetPickBounds("ButtonA", out _, out _));
                Assert.False(picture.TryGetPickBounds(null, out _, out _));
                Assert.False(picture.TryGetPickBounds("", out _, out _));

                // In a view half the size the hands are half the size.
                Lay(hands, 487.5, 363);
                Assert.True(picture.TryGetPickBounds("VrRTrigger", out Rect half, out _));
                Near(new Rect(296, 180, 44.5, 49.5), half);

                // In a view of another shape the hands are centered.
                Lay(hands, 975, 1452);
                Assert.True(picture.TryGetPickBounds("VrRTrigger", out Rect tall, out _));
                Near(new Rect(592, 363 + 360, 89, 99), tall);
            });
        }

        /// <summary>The helpers the pictures answer with: a rect of a part's
        /// own space mapped to the picture's, the rect a drawn part covers,
        /// and whether a part is set to show.</summary>
        [Fact]
        public void APartsRectIsMappedThroughWhateverScalesIt()
        {
            RunSta(() =>
            {
                var key = new Rectangle { Width = 30, Height = 40 };
                Canvas.SetLeft(key, 10);
                Canvas.SetTop(key, 20);
                var canvas = new Canvas { Width = 100, Height = 100 };
                canvas.Children.Add(key);
                var box = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
                var picture = new Grid();
                picture.Children.Add(box);
                Lay(picture, 200, 200);

                Assert.True(PicturePick.TryMap(canvas, picture, new Rect(10, 20, 30, 40), out Rect fromCanvas));
                Near(new Rect(20, 40, 60, 80), fromCanvas);
                Assert.Equal(new Rect(0, 0, 30, 40), PicturePick.OwnRect(key));
                Assert.True(PicturePick.TryMap(key, picture, PicturePick.OwnRect(key), out Rect fromKey));
                Near(new Rect(20, 40, 60, 80), fromKey);

                // A path covers its outline, wherever its box is.
                var outline = new Path { Data = new RectangleGeometry(new Rect(5, 6, 7, 8)) };
                Assert.Equal(new Rect(5, 6, 7, 8), PicturePick.OwnRect(outline));
                Assert.True(PicturePick.OwnRect(new Border()).IsEmpty);
                Assert.True(PicturePick.OwnRect(null).IsEmpty);

                // Nothing is mapped for a part outside the picture, or for
                // a rect that covers nothing.
                Assert.False(PicturePick.TryMap(new Canvas(), picture, new Rect(0, 0, 5, 5), out _));
                Assert.False(PicturePick.TryMap(canvas, picture, Rect.Empty, out _));
                Assert.False(PicturePick.TryMap(canvas, picture, new Rect(0, 0, 0, 5), out _));
                Assert.False(PicturePick.TryMap(null, picture, new Rect(0, 0, 5, 5), out _));

                // A part shows while nothing from it up to the picture is
                // collapsed or hidden.
                Assert.True(PicturePick.Shows(key, picture));
                box.Visibility = Visibility.Collapsed;
                Assert.False(PicturePick.Shows(key, picture));
                box.Visibility = Visibility.Visible;
                key.Visibility = Visibility.Hidden;
                Assert.False(PicturePick.Shows(key, picture));
                Assert.False(PicturePick.Shows(new Canvas(), picture));
            });
        }

        // ── the 3D model ───────────────────────────────────────────────

        /// <summary>The matrix that puts a point of the model on a view of
        /// a size, from the default camera at a distance and a lift, as
        /// the view's own does.</summary>
        private static Matrix3D ToScreen(Matrix3D placement, double distance, double lift, double width, double height)
        {
            Vector3D look = ControllerModelFraming.Look;
            Vector3D right = Vector3D.CrossProduct(look, ControllerModelFraming.Up);
            right.Normalize();
            Vector3D up = Vector3D.CrossProduct(right, look);
            double halfWidth = Math.Tan(ControllerModelFraming.FieldOfView * Math.PI / 360.0);
            double halfHeight = halfWidth * height / width;
            double sx = width / (2 * halfWidth), sy = height / (2 * halfHeight);
            var camera = new Matrix3D(
                right.X * sx + look.X * width / 2, -up.X * sy + look.X * height / 2, look.X, look.X,
                right.Y * sx + look.Y * width / 2, -up.Y * sy + look.Y * height / 2, look.Y, look.Y,
                right.Z * sx + look.Z * width / 2, -up.Z * sy + look.Z * height / 2, look.Z, look.Z,
                distance * width / 2, lift * sy + distance * height / 2, 0, distance);
            return placement * camera;
        }

        private static GeometryModel3D Mesh(params Point3D[] points)
            => new GeometryModel3D
            {
                Geometry = new MeshGeometry3D { Positions = new Point3DCollection(points) },
                Material = new DiffuseMaterial(Brushes.Gray),
            };

        private static Model3DGroup Group(params Model3D[] children)
        {
            var group = new Model3DGroup();
            foreach (var child in children) group.Children.Add(child);
            return group;
        }

        private static Rect BoxOf(IEnumerable<Point3D> points, Matrix3D toScreen)
        {
            Rect box = Rect.Empty;
            foreach (Point3D point in points)
            {
                Point4D at = toScreen.Transform(new Point4D(point.X, point.Y, point.Z, 1));
                var drawn = new Point(at.X / at.W, at.Y / at.W);
                if (box.IsEmpty) box = new Rect(drawn, drawn);
                else box.Union(drawn);
            }
            return box;
        }

        /// <summary>A peg through a plate, seen from above and in front:
        /// the box is the cap's, since the plate stands in front of the
        /// stem. With no plate it is the whole peg's. A plate over the
        /// whole peg leaves nothing in view, and the box is the whole
        /// peg's again, so a control on the far side is still marked. A
        /// point behind the eye gives no box.</summary>
        [Fact]
        public void TheBoxOfA3DControl_IsThePartOfItInView()
        {
            Point3D[] cap = { new(-2, -2, 0), new(2, -2, 0), new(2, 2, 0), new(-2, 2, 0) };
            Point3D[] stem = { new(-2, -2, -12), new(2, -2, -12), new(2, 2, -12), new(-2, 2, -12) };
            Model3DGroup Peg() => Group(Mesh(cap.Concat(stem).ToArray()));
            // The plate: two triangles, 3 under the cap.
            GeometryModel3D Plate(double z) => Mesh(
                new(-40, -40, z), new(40, -40, z), new(40, 40, z),
                new(-40, -40, z), new(40, 40, z), new(-40, 40, z));

            Matrix3D toScreen = ToScreen(Matrix3D.Identity, 200, 0, 800, 600);
            Rect capBox = BoxOf(cap, toScreen), whole = BoxOf(cap.Concat(stem), toScreen);
            Assert.True(whole.Height > capBox.Height * 2, "the stem adds nothing to the peg's box in this view");

            var peg = Peg();
            var shape = ControllerModelPickBox.Read(Group(Plate(-3), peg), new[] { peg });
            Assert.Equal(8, shape.Points.Length);
            Assert.Equal(6, shape.Cover.Length);
            Assert.True(ControllerModelPickBox.TryGetBox(shape, toScreen, out Rect seen));
            Near(capBox, seen, 1e-6);
            // The box is taken again from the same shape, as it is for
            // every frame that turns the model.
            Assert.True(ControllerModelPickBox.TryGetBox(shape, toScreen, out Rect again));
            Assert.Equal(seen, again);

            var alone = Peg();
            Assert.True(ControllerModelPickBox.TryGetBox(
                ControllerModelPickBox.Read(Group(alone), new[] { alone }), toScreen, out Rect bare));
            Near(whole, bare, 1e-6);

            var under = Peg();
            Assert.True(ControllerModelPickBox.TryGetBox(
                ControllerModelPickBox.Read(Group(Plate(5), under), new[] { under }), toScreen, out Rect covered));
            Near(whole, covered, 1e-6);

            // The eye stands inside the peg: no box.
            Assert.False(ControllerModelPickBox.TryGetBox(shape, ToScreen(Matrix3D.Identity, 1, 0, 800, 600), out _));
            // A control with no points has none either.
            var empty = Group();
            Assert.False(ControllerModelPickBox.TryGetBox(
                ControllerModelPickBox.Read(Group(empty), new[] { empty }), toScreen, out _));
        }

        /// <summary>What stands in front of a control is read near it only,
        /// the control's own parts hide nothing of it, a triangle that only
        /// touches the control's edge does not hide the edge, and a mesh of
        /// many points is thinned.</summary>
        [Fact]
        public void AControlsShape_IsItsPointsAndTheTrianglesNearIt()
        {
            Assert.Equal(6, ControllerModelPickBox.Reach(new Rect3D(0, 0, 0, 1, 2, 3)));
            Assert.Equal(12, ControllerModelPickBox.Reach(new Rect3D(0, 0, 0, 12, 2, 3)));
            Assert.Equal(20, ControllerModelPickBox.Reach(new Rect3D(0, 0, 0, 12, 50, 3)));

            var button = Group(Mesh(new(0, 0, 0), new(4, 0, 0), new(4, 4, 0), new(0, 4, 0)));
            // One triangle within reach of the button, one far from it, and
            // one given by corner numbers.
            var near = Mesh(new(-3, -3, -1), new(8, -3, -1), new(8, 8, -1));
            var far = Mesh(new(100, 100, 0), new(110, 100, 0), new(110, 110, 0));
            var numbered = new GeometryModel3D
            {
                Geometry = new MeshGeometry3D
                {
                    Positions = new Point3DCollection(new Point3D[] { new(0, 0, -2), new(5, 0, -2), new(5, 5, -2), new(200, 200, 0) }),
                    TriangleIndices = new Int32Collection(new[] { 0, 1, 2, 1, 2, 3, 0, 1, 9 }),
                },
                Material = new DiffuseMaterial(Brushes.Gray),
            };
            // The button's own second part is no cover for it.
            var rider = Group(Mesh(new(1, 1, 1), new(2, 1, 1), new(2, 2, 1)));
            var shape = ControllerModelPickBox.Read(Group(near, far, numbered, Group(button, rider)), new[] { button, rider });
            Assert.Equal(7, shape.Points.Length);
            // The near triangle, and the two numbered ones that have their
            // corners: the far one is out of reach, and the one that names
            // a corner the mesh lacks is left out.
            Assert.Equal(9, shape.Cover.Length);
            Assert.Equal(new Point3D(-3, -3, -1), shape.Cover[0]);
            Assert.Equal(new Point3D(0, 0, -2), shape.Cover[3]);

            // A triangle in the button's own plane, sharing its edge, does
            // not hide the edge: it is no nearer than the edge is.
            var rim = Mesh(new(4, 0, 0), new(9, 0, 0), new(9, 4, 0), new(4, 0, 0), new(9, 4, 0), new(4, 4, 0));
            var flat = Group(Mesh(new(0, 0, 0), new(4, 0, 0), new(4, 4, 0), new(0, 4, 0)));
            Matrix3D toScreen = ToScreen(Matrix3D.Identity, 200, 0, 800, 600);
            Assert.True(ControllerModelPickBox.TryGetBox(
                ControllerModelPickBox.Read(Group(rim, flat), new[] { flat }), toScreen, out Rect box));
            Near(BoxOf(new Point3D[] { new(0, 0, 0), new(4, 0, 0), new(4, 4, 0), new(0, 4, 0) }, toScreen), box, 1e-6);

            // 5,000 points become 800 at most, spread over the whole mesh.
            var many = Enumerable.Range(0, 5000).Select(i => new Point3D(i, 0, 0)).ToArray();
            var dense = Group(Mesh(many));
            var thinned = ControllerModelPickBox.Read(Group(dense), new[] { dense });
            Assert.InRange(thinned.Points.Length, 700, ControllerModelPickBox.MostPoints);
            Assert.Equal(new Point3D(0, 0, 0), thinned.Points[0]);
            Assert.True(thinned.Points[^1].X > 4990);
        }

        public static IEnumerable<object[]> Families()
        {
            foreach (string family in new[]
                     {
                         "XBOX360", "XboxSeries", "DS3", "DS4", "DualSense", "DualSenseEdge",
                         "Switch2Pro", "SteamDeck", "SteamController", "SteamController2",
                     })
                yield return new object[] { family };
        }

        /// <summary>On every model, from the default camera: a face button's
        /// box lies within the box of its whole mesh, is about as wide as
        /// it is tall, and is of a size a button has in the picture. No part
        /// a click names carries a transform of its own in the model's
        /// file, which is why the box can leave a part's transform out: the
        /// view gives a part one only to move it.</summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void AFaceButtonsBoxOnAModel_IsItsCap(string family)
        {
            const double width = 1393, height = 581;
            using var model = ControllerModelBase.Create(family, null, false);
            Matrix3D placement = ControllerModelFraming.Placement(model.model3DGroup.Bounds, model.ModelScale);
            var outline = ControllerModelFraming.Measure(model.model3DGroup, placement);
            var (distance, lift) = ControllerModelFraming.Fit(outline, width, height);
            Matrix3D toScreen = ToScreen(placement, distance, lift, width, height);

            foreach (var part in model.ButtonMap.Values.SelectMany(parts => parts).Concat(model.ClickMap.Keys))
                Assert.True(part.Transform == null || part.Transform.Value.IsIdentity, $"{family}: a part carries a transform");

            Assert.True(model.ButtonMap.TryGetValue("ButtonA", out var button), $"{family} has no A button");
            var shape = ControllerModelPickBox.Read(model.model3DGroup, button, model.DefaultMaterials);
            Assert.NotEmpty(shape.Points);
            Assert.NotEmpty(shape.Cover);
            Assert.True(ControllerModelPickBox.TryGetBox(shape, toScreen, out Rect box), family);

            Rect whole = BoxOf(shape.Points, toScreen);
            Assert.True(whole.Contains(box) || Contains(whole, box, 0.01), $"{family}: {box} is not within {whole}");
            Assert.True(ControllerModelPickBox.IsRound(box), $"{family}: the A button's box is {box.Width:F1} by {box.Height:F1}");
            Assert.InRange(box.Width, 12, 140);
            Assert.InRange(box.Height, 12, 140);
        }

        /// <summary>A box about as wide as it is tall is a round control's:
        /// a cap seen at a slant, a trigger. A bumper's is not.</summary>
        [Theory]
        [InlineData(72, 55, true)]
        [InlineData(81, 103, true)]
        [InlineData(60, 60, true)]
        [InlineData(163, 79, false)]
        [InlineData(40, 90, false)]
        [InlineData(0, 10, false)]
        public void ABoxAboutAsWideAsItIsTall_IsARoundControls(double width, double height, bool round)
            => Assert.Equal(round, ControllerModelPickBox.IsRound(new Rect(0, 0, width, height)));

        private static bool Contains(Rect outer, Rect inner, double slack)
            => inner.Left >= outer.Left - slack && inner.Top >= outer.Top - slack
            && inner.Right <= outer.Right + slack && inner.Bottom <= outer.Bottom + slack;

        /// <summary>The Xbox 360 model's A button has a stem inside the
        /// shell, half again as long as its cap is tall in the picture. The
        /// box leaves it out.</summary>
        [Fact]
        public void TheStemOfAButtonInsideTheShell_IsNotInItsBox()
        {
            const double width = 1393, height = 581;
            using var model = ControllerModelBase.Create("XBOX360", null, false);
            Matrix3D placement = ControllerModelFraming.Placement(model.model3DGroup.Bounds, model.ModelScale);
            var (distance, lift) = ControllerModelFraming.Fit(
                ControllerModelFraming.Measure(model.model3DGroup, placement), width, height);
            Matrix3D toScreen = ToScreen(placement, distance, lift, width, height);

            var shape = ControllerModelPickBox.Read(model.model3DGroup, model.ButtonMap["ButtonA"], model.DefaultMaterials);
            Assert.True(ControllerModelPickBox.TryGetBox(shape, toScreen, out Rect box));
            Rect whole = BoxOf(shape.Points, toScreen);
            Assert.True(box.Height < whole.Height * 0.75, $"the box is {box.Height:F1} tall of the mesh's {whole.Height:F1}");
        }

        /// <summary>The box of a control as it was taken before 2026-11-13:
        /// all three corners of every triangle put on the screen, and every
        /// point held against every triangle that reaches into the
        /// control's box. The faster way is held to this one.</summary>
        private static bool PlainBox(ControllerModelPickBox.Shape shape, Matrix3D toScreen, out Rect box)
        {
            box = Rect.Empty;
            Point3D[] points = shape.Points;
            if (points.Length == 0) return false;

            var at = new double[points.Length * 3];
            Rect whole = Rect.Empty;
            for (int i = 0; i < points.Length; i++)
            {
                Point4D p = toScreen.Transform(new Point4D(points[i].X, points[i].Y, points[i].Z, 1));
                if (!(p.W > 0)) return false;
                double x = p.X / p.W, y = p.Y / p.W;
                at[i * 3] = x;
                at[i * 3 + 1] = y;
                at[i * 3 + 2] = p.W;
                if (whole.IsEmpty) whole = new Rect(x, y, 0, 0);
                else whole.Union(new Point(x, y));
            }

            Point3D[] cover = shape.Cover;
            var coverAt = new double[cover.Length * 3];
            int kept = 0;
            for (int i = 0; i + 2 < cover.Length; i += 3)
            {
                Point4D a = toScreen.Transform(new Point4D(cover[i].X, cover[i].Y, cover[i].Z, 1));
                Point4D b = toScreen.Transform(new Point4D(cover[i + 1].X, cover[i + 1].Y, cover[i + 1].Z, 1));
                Point4D c = toScreen.Transform(new Point4D(cover[i + 2].X, cover[i + 2].Y, cover[i + 2].Z, 1));
                if (!(a.W > 0) || !(b.W > 0) || !(c.W > 0)) continue;
                double ax = a.X / a.W, ay = a.Y / a.W, bx = b.X / b.W, by = b.Y / b.W, cx = c.X / c.W, cy = c.Y / c.W;
                if (Math.Max(ax, Math.Max(bx, cx)) < whole.Left || Math.Min(ax, Math.Min(bx, cx)) > whole.Right
                    || Math.Max(ay, Math.Max(by, cy)) < whole.Top || Math.Min(ay, Math.Min(by, cy)) > whole.Bottom)
                    continue;
                coverAt[kept] = ax; coverAt[kept + 1] = ay; coverAt[kept + 2] = a.W;
                coverAt[kept + 3] = bx; coverAt[kept + 4] = by; coverAt[kept + 5] = b.W;
                coverAt[kept + 6] = cx; coverAt[kept + 7] = cy; coverAt[kept + 8] = c.W;
                kept += 9;
            }

            Rect seen = Rect.Empty;
            int inView = 0;
            for (int i = 0; i < points.Length; i++)
            {
                double x = at[i * 3], y = at[i * 3 + 1], far = at[i * 3 + 2];
                if (PlainIsHidden(coverAt, kept, x, y, far * (1 - ControllerModelPickBox.DepthSlack))) continue;
                inView++;
                if (seen.IsEmpty) seen = new Rect(x, y, 0, 0);
                else seen.Union(new Point(x, y));
            }

            box = inView >= 3 && seen.Width > 0 && seen.Height > 0 ? seen : whole;
            return box.Width > 0 && box.Height > 0;
        }

        /// <summary>The triangles near a control as they were gathered
        /// before 2026-11-13: three points for each, read from the meshes'
        /// own lists.</summary>
        private static Point3D[] PlainCover(Model3D model, IReadOnlyList<Model3DGroup> parts,
            IReadOnlyDictionary<Model3DGroup, Material> atRest)
        {
            var own = new List<Point3D>();
            void Own(Model3D part)
            {
                if (part is Model3DGroup group)
                    foreach (Model3D child in group.Children) Own(child);
                else if (part is GeometryModel3D { Geometry: MeshGeometry3D mesh })
                    own.AddRange(mesh.Positions);
            }
            foreach (var part in parts) Own(part);
            var cover = new List<Point3D>();
            if (own.Count == 0) return cover.ToArray();
            Rect3D near = new Rect3D(own[0], new Size3D(0, 0, 0));
            foreach (Point3D point in own) near.Union(point);
            double reach = ControllerModelPickBox.Reach(near);
            near = new Rect3D(near.X - reach, near.Y - reach, near.Z - reach,
                near.SizeX + 2 * reach, near.SizeY + 2 * reach, near.SizeZ + 2 * reach);

            void Gather(Model3D part, Material worn)
            {
                if (part is Model3DGroup group)
                {
                    for (int i = 0; i < parts.Count; i++)
                        if (ReferenceEquals(parts[i], group)) return;
                    if (atRest != null && atRest.TryGetValue(group, out Material rest) && rest != null) worn = rest;
                    foreach (Model3D child in group.Children) Gather(child, worn);
                    return;
                }
                if (part is not GeometryModel3D { Geometry: MeshGeometry3D mesh } drawn) return;
                Material wears = worn ?? drawn.Material;
                if (wears == null || ControllerModelPickBox.IsSeeThrough(wears)) return;
                Point3DCollection positions = mesh.Positions;
                Int32Collection corners = mesh.TriangleIndices;
                int count = corners.Count > 0 ? corners.Count : positions.Count;
                for (int i = 0; i + 2 < count; i += 3)
                {
                    int ia = i, ib = i + 1, ic = i + 2;
                    if (corners.Count > 0)
                    {
                        ia = corners[i]; ib = corners[i + 1]; ic = corners[i + 2];
                        if ((uint)ia >= (uint)positions.Count || (uint)ib >= (uint)positions.Count
                            || (uint)ic >= (uint)positions.Count)
                            continue;
                    }
                    Point3D a = positions[ia], b = positions[ib], c = positions[ic];
                    if (Math.Max(a.X, Math.Max(b.X, c.X)) < near.X || Math.Min(a.X, Math.Min(b.X, c.X)) > near.X + near.SizeX
                        || Math.Max(a.Y, Math.Max(b.Y, c.Y)) < near.Y || Math.Min(a.Y, Math.Min(b.Y, c.Y)) > near.Y + near.SizeY
                        || Math.Max(a.Z, Math.Max(b.Z, c.Z)) < near.Z || Math.Min(a.Z, Math.Min(b.Z, c.Z)) > near.Z + near.SizeZ)
                        continue;
                    cover.Add(a);
                    cover.Add(b);
                    cover.Add(c);
                }
            }
            Gather(model, null);
            return cover.ToArray();
        }

        private static bool PlainIsHidden(double[] cover, int length, double x, double y, double nearest)
        {
            for (int i = 0; i < length; i += 9)
            {
                double ax = cover[i], ay = cover[i + 1], bx = cover[i + 3], by = cover[i + 4], cx = cover[i + 6], cy = cover[i + 7];
                if ((x < ax && x < bx && x < cx) || (x > ax && x > bx && x > cx)
                    || (y < ay && y < by && y < cy) || (y > ay && y > by && y > cy))
                    continue;
                double span = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (Math.Abs(span) < 1e-9) continue;
                double wa = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / span;
                double wb = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / span;
                double wc = 1 - wa - wb;
                if (wa < 0 || wb < 0 || wc < 0) continue;
                double inverse = wa / cover[i + 2] + wb / cover[i + 5] + wc / cover[i + 8];
                if (inverse > 0 && 1 / inverse < nearest) return true;
            }
            return false;
        }

        /// <summary>A point that several triangles of its mesh name is kept
        /// once, a mesh whose triangles are not numbered keeps each of its
        /// points, and the control's box is cut into cells only for enough
        /// triangles, in a box that has a width and a height.</summary>
        [Fact]
        public void TheCoverKeepsAMeshsPointOnce_AndCellsAreForManyTriangles()
        {
            var button = Group(Mesh(new(0, 0, 0), new(4, 0, 0), new(4, 4, 0), new(0, 4, 0)));
            // Two triangles of one plate that name two of its points twice,
            // one that names a point the mesh lacks, and a point that is
            // far from the button and named by none that is kept.
            var plate = new GeometryModel3D
            {
                Geometry = new MeshGeometry3D
                {
                    Positions = new Point3DCollection(new Point3D[] { new(500, 500, 0), new(-3, -3, -1), new(8, -3, -1), new(8, 8, -1), new(-3, 8, -1) }),
                    TriangleIndices = new Int32Collection(new[] { 1, 2, 3, 1, 2, 9, 1, 3, 4 }),
                },
                Material = new DiffuseMaterial(Brushes.Gray),
            };
            var shape = ControllerModelPickBox.Read(Group(plate, button), new[] { button });
            Assert.Equal(new Point3D[] { new(-3, -3, -1), new(8, -3, -1), new(8, 8, -1), new(-3, 8, -1) }, shape.CoverPoints);
            Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, shape.CoverCorners);
            Assert.Equal(6, shape.Cover.Length);
            Assert.Equal(new Point3D(-3, 8, -1), shape.Cover[5]);
            Assert.Equal(shape.CoverPoints.Length * 3, shape.CoverAt.Length);
            Assert.Equal(2, shape.Kept.Length);

            // The same plate as six points with no numbers: each is kept.
            var loose = Mesh(new(-3, -3, -1), new(8, -3, -1), new(8, 8, -1), new(-3, -3, -1), new(8, 8, -1), new(-3, 8, -1));
            var unnumbered = ControllerModelPickBox.Read(Group(loose, button), new[] { button });
            Assert.Equal(6, unnumbered.CoverPoints.Length);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, unnumbered.CoverCorners);
            Assert.Equal(shape.Cover, unnumbered.Cover);

            var box = new Rect(0, 0, 100, 50);
            Assert.Equal(0, ControllerModelPickBox.CellsFor(63, box));
            Assert.Equal(4, ControllerModelPickBox.CellsFor(64, box));
            Assert.Equal(16, ControllerModelPickBox.CellsFor(1024, box));
            Assert.Equal(32, ControllerModelPickBox.CellsFor(4096, box));
            Assert.Equal(32, ControllerModelPickBox.CellsFor(500000, box));
            Assert.Equal(0, ControllerModelPickBox.CellsFor(5000, new Rect(0, 0, 0, 50)));
            Assert.Equal(0, ControllerModelPickBox.CellsFor(5000, new Rect(0, 0, 100, 0)));
            Assert.Equal(0, ControllerModelPickBox.CellsFor(5000, Rect.Empty));
        }

        /// <summary>The faster box is the plain one, to the last bit, over
        /// soups of triangles around a control: few and many, small and
        /// large, flat ones, ones behind the eye, and ones with a corner so
        /// near the eye's plane that it lands far off the screen. Many of
        /// the boxes are a part of the control and many come from cells,
        /// so the runs are not all one easy case.</summary>
        [Fact]
        public void TheFasterBox_IsThePlainBox_OverSoupsOfTriangles()
        {
            int partly = 0, gridded = 0, runs = 0;
            foreach (int triangles in new[] { 0, 40, 90, 400, 3000, 20000 })
            foreach (int seed in new[] { 1, 2, 3 })
            {
                var random = new Random(seed * 100000 + triangles);
                double Spread(double size) => (random.NextDouble() - 0.5) * size;
                // The control: a slab of points about the middle.
                var own = new Point3D[300];
                for (int i = 0; i < own.Length; i++) own[i] = new Point3D(Spread(10), Spread(10), Spread(2));
                var control = Group(Mesh(own));
                // The more triangles, the smaller, as a finer mesh's are,
                // so that about half the control is covered at every count.
                double unit = triangles == 0 ? 0 : Math.Sqrt(3000.0 / triangles);
                var soup = new Point3D[triangles * 3];
                for (int t = 0; t < triangles; t++)
                {
                    double size = unit * ((t % 3) switch { 0 => 0.5, 1 => 1, _ => 2.5 });
                    var middle = new Point3D(Spread(28), Spread(28), Spread(20));
                    for (int c = 0; c < 3; c++)
                        soup[t * 3 + c] = t % 50 == 49 && c == 2
                            ? soup[t * 3]
                            : new Point3D(middle.X + Spread(size), middle.Y + Spread(size), middle.Z + Spread(size));
                }
                var shape = ControllerModelPickBox.Read(Group(Mesh(soup), control), new[] { control });
                foreach (double distance in new[] { 9.0, 14, 60, 200 })
                foreach (double turn in new[] { 0.0, 37, 90 })
                {
                    Matrix3D placement = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), turn)).Value;
                    Matrix3D toScreen = ToScreen(placement, distance, 0, 800, 600);
                    bool plain = PlainBox(shape, toScreen, out Rect expected);
                    bool fast = ControllerModelPickBox.TryGetBox(shape, toScreen, out Rect box);
                    Assert.True(plain == fast && expected == box,
                        $"{triangles} triangles, seed {seed}, from {distance} at {turn}: {box} for {expected}");
                    Assert.True(fast, $"{triangles} triangles, seed {seed}, from {distance} at {turn}: no box");
                    runs++;
                    if (box != BoxOf(shape.Points, toScreen)) partly++;
                }
                if (shape.CellItems.Length > 0) gridded++;
            }
            Assert.Equal(216, runs);
            Assert.True(partly >= 100, $"only {partly} of {runs} boxes were a part of the control");
            Assert.True(gridded >= 9, $"only {gridded} of 18 shapes had their box cut into cells");
        }

        /// <summary>On every model, for every control a recording or a click
        /// names and from several sides, the faster box is the plain one to
        /// the last bit, and the larger controls have their box cut into
        /// cells.</summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void TheFasterBox_IsThePlainBox_ForEveryControlOfAModel(string family)
        {
            const double width = 1393, height = 581;
            using var model = ControllerModelBase.Create(family, null, false);
            Matrix3D placement = ControllerModelFraming.Placement(model.model3DGroup.Bounds, model.ModelScale);
            var (distance, lift) = ControllerModelFraming.Fit(
                ControllerModelFraming.Measure(model.model3DGroup, placement), width, height);

            var controls = new List<(string Name, IReadOnlyList<Model3DGroup> Parts)>();
            foreach (var entry in model.ButtonMap) controls.Add((entry.Key, entry.Value));
            foreach (var entry in model.ClickMap) controls.Add((entry.Value + ", one part", new[] { entry.Key }));
            Assert.True(controls.Count >= 20, $"{family} names only {controls.Count} controls");

            (double Yaw, double Pitch)[] turns = { (0, 0), (30, 0), (-55, 15), (90, 0), (180, -20) };
            int gridded = 0, partly = 0;
            foreach (var (name, parts) in controls)
            {
                var shape = ControllerModelPickBox.Read(model.model3DGroup, parts, model.DefaultMaterials);
                // The triangles are the ones the plain read gathers, in its
                // order, and fewer corners are kept than triangles name.
                Point3D[] cover = shape.Cover, plainCover = PlainCover(model.model3DGroup, parts, model.DefaultMaterials);
                Assert.True(cover.AsSpan().SequenceEqual(plainCover), $"{family} {name}: the cover is not the plain read's");
                Assert.True(cover.Length == 0 || shape.CoverPoints.Length < cover.Length, $"{family} {name}: no corner is shared");
                foreach (var (yaw, pitch) in turns)
                {
                    var turn = new Transform3DGroup();
                    turn.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), yaw)));
                    turn.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), pitch)));
                    Matrix3D toScreen = ToScreen(placement * turn.Value, distance, lift, width, height);
                    bool plain = PlainBox(shape, toScreen, out Rect expected);
                    bool fast = ControllerModelPickBox.TryGetBox(shape, toScreen, out Rect box);
                    Assert.True(plain == fast && expected == box, $"{family} {name} at {yaw}, {pitch}: {box} for {expected}");
                    if (fast && box != BoxOf(shape.Points, toScreen)) partly++;
                }
                if (shape.CellItems.Length > 0) gridded++;
            }
            Assert.True(gridded >= controls.Count / 2, $"{family}: only {gridded} of {controls.Count} controls had cells");
            Assert.True(partly >= controls.Count, $"{family}: only {partly} boxes were a part of their control");
        }

        /// <summary>What light passes through hides nothing of a control:
        /// a brush drawn at less than full strength, a color that is not
        /// solid, a texture that carries transparency, and any of those
        /// under a gloss. A part that the model says is clear at rest is
        /// clear while a highlight makes it solid, and a solid part is
        /// solid while something lights it thin.</summary>
        [Fact]
        public void WhatLightPassesThrough_HidesNothingOfAControl()
        {
            var solid = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)));
            var tinted = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0x96, 0x6A, 0xC4, 0xF6)));
            var faint = new DiffuseMaterial(new SolidColorBrush(Colors.White) { Opacity = 0.30 });
            var gloss = new MaterialGroup();
            gloss.Children.Add(new SpecularMaterial(Brushes.White, 40));
            gloss.Children.Add(tinted);
            var solidGloss = new MaterialGroup();
            solidGloss.Children.Add(solid);
            solidGloss.Children.Add(new SpecularMaterial(new SolidColorBrush(Colors.White) { Opacity = 0.6 }, 40));
            ImageBrush Texture(PixelFormat format) => new ImageBrush(
                new System.Windows.Media.Imaging.WriteableBitmap(4, 4, 96, 96, format, null));
            var clearAtlas = new DiffuseMaterial(Texture(PixelFormats.Bgra32));
            var bodyAtlas = new DiffuseMaterial(Texture(PixelFormats.Bgr24));

            Assert.False(ControllerModelPickBox.IsSeeThrough(solid));
            Assert.False(ControllerModelPickBox.IsSeeThrough(solidGloss));
            Assert.False(ControllerModelPickBox.IsSeeThrough(bodyAtlas));
            Assert.False(ControllerModelPickBox.IsSeeThrough(null));
            Assert.False(ControllerModelPickBox.IsSeeThrough(new SpecularMaterial(Brushes.White, 40)));
            Assert.True(ControllerModelPickBox.IsSeeThrough(tinted));
            Assert.True(ControllerModelPickBox.IsSeeThrough(faint));
            Assert.True(ControllerModelPickBox.IsSeeThrough(gloss));
            Assert.True(ControllerModelPickBox.IsSeeThrough(clearAtlas));

            // A dome over a button: no cover while it is clear.
            Point3D[] plate = { new(-9, -9, 2), new(9, -9, 2), new(9, 9, 2) };
            Model3DGroup Button() => Group(Mesh(new(0, 0, 0), new(4, 0, 0), new(4, 4, 0), new(0, 4, 0)));
            int Cover(Material worn, Dictionary<Model3DGroup, Material> atRest = null, Material rest = null)
            {
                var button = Button();
                var dome = Group(new GeometryModel3D { Geometry = new MeshGeometry3D { Positions = new Point3DCollection(plate) }, Material = worn });
                if (atRest != null) atRest[dome] = rest;
                return ControllerModelPickBox.Read(Group(dome, button), new[] { button }, atRest).Cover.Length;
            }
            Assert.Equal(3, Cover(solid));
            // A mesh that wears nothing is not drawn, and hides nothing.
            Assert.Equal(0, Cover(null));
            Assert.Equal(0, Cover(tinted));
            Assert.Equal(0, Cover(gloss));
            Assert.Equal(0, Cover(clearAtlas));
            // Lit solid, clear at rest: clear. Lit thin, solid at rest: solid.
            Assert.Equal(0, Cover(solid, new Dictionary<Model3DGroup, Material>(), tinted));
            Assert.Equal(3, Cover(tinted, new Dictionary<Model3DGroup, Material>(), solid));
            // A part the model has no record of wears what it wears.
            Assert.Equal(0, Cover(tinted, new Dictionary<Model3DGroup, Material>(), null));
        }

        // ── the page ───────────────────────────────────────────────────

        /// <summary>The page draws one ring over the picture: a line and a
        /// band of the widths the ring's shape is worked out for, in the
        /// ember color, over the pictures and under the view switch. It
        /// takes no clicks, never draws past the picture, and is not
        /// mirrored in a right-to-left language. Under it lies the hover
        /// ring, the line alone in the Buttons board's fainter ember. All
        /// three parts start hidden.</summary>
        [Fact]
        public void ThePageDrawsOneRingOverThePicture()
        {
            var page = Page();
            var host = Named(page, "PictureHost");
            Assert.Equal("ControllerModelHost", (string)host.Attribute("AutomationProperties.AutomationId"));
            Assert.Equal("PictureHost_SizeChanged", (string)host.Attribute("SizeChanged"));

            var layer = Named(page, "PickRingLayer");
            Assert.Equal("Canvas", layer.Name.LocalName);
            Assert.Same(host, layer.Parent);
            Assert.Equal("False", (string)layer.Attribute("IsHitTestVisible"));
            Assert.Equal("True", (string)layer.Attribute("ClipToBounds"));
            Assert.Equal("LeftToRight", (string)layer.Attribute("FlowDirection"));
            // Over every picture, under the button that switches the view.
            var order = host.Elements().Select(Name).ToList();
            Assert.True(order.IndexOf("VRPreview") < order.IndexOf("PickRingLayer"));
            Assert.True(order.IndexOf("TouchSensors") < order.IndexOf("PickRingLayer"));
            Assert.True(order.IndexOf("PickRingLayer") < order.IndexOf("ViewModeToggle"));

            Assert.Equal(new[] { "HoverRing", "PickRingBand", "PickRing" }, layer.Elements().Select(Name));
            var band = Named(page, "PickRingBand");
            var ring = Named(page, "PickRing");
            foreach (var part in new[] { band, ring })
            {
                Assert.Equal("Border", part.Name.LocalName);
                Assert.Equal("Collapsed", (string)part.Attribute("Visibility"));
                Assert.Equal("{DynamicResource EmberBrush}", (string)part.Attribute("BorderBrush"));
                Assert.Null(part.Attribute("Background"));
            }
            Assert.Equal(PicturePick.LineWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), (string)ring.Attribute("BorderThickness"));
            Assert.Equal(PicturePick.BandWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), (string)band.Attribute("BorderThickness"));
            Assert.Equal("0.28", (string)band.Attribute("Opacity"));
            Assert.Null(ring.Attribute("Opacity"));

            var hover = Named(page, "HoverRing");
            Assert.Equal("Border", hover.Name.LocalName);
            Assert.Equal("Collapsed", (string)hover.Attribute("Visibility"));
            Assert.Equal("{DynamicResource EmberHoverRingBrush}", (string)hover.Attribute("BorderBrush"));
            Assert.Equal(PicturePick.LineWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), (string)hover.Attribute("BorderThickness"));
            Assert.Null(hover.Attribute("Background"));
            Assert.Null(hover.Attribute("Opacity"));
            // The board's rgba(255, 138, 87, 0.6) on dark, and the deeper
            // ember at the same strength on light.
            Assert.Contains("<SolidColorBrush x:Key=\"EmberHoverRingBrush\" Color=\"#99FF8A57\"/>",
                ExtrasPageTests.RepoText("PadForge.App", "App.xaml"));
            Assert.Contains("SetBrush(\"EmberHoverRingBrush\", dark ? Color.FromArgb(0x99, 0xFF, 0x8A, 0x57) : Color.FromArgb(0x99, 0xC2, 0x4A, 0x12));",
                ExtrasPageTests.RepoText("PadForge.App", "Common", "EmberTheme.cs"));
        }

        /// <summary>The ring is placed again whenever the control may be
        /// drawn elsewhere: another pick, view or set of rows, another
        /// slot, another picture, a picture of another size, and a picture
        /// that says its controls moved. It is placed after the layout has
        /// settled, once for all the requests made before that. It shows
        /// in the picture's view only, a ring around several parts is
        /// round, and with no control to ring both its parts are hidden.
        /// The hover ring is placed in the same pass, on the control a
        /// picture says is under the pointer, by the row a click there
        /// picks.</summary>
        [Fact]
        public void TheRingFollowsThePick_TheViewAndThePicture()
        {
            string code = CodeBehind();
            Assert.Matches(@"else if \(e\.PropertyName == nameof\(PadViewModel\.PickedRow\)\)\s*\{[^}]*QueuePickRingSync\(\);\s*\}", code);
            Assert.Matches(@"ResubscribeSelectedDeviceInfo\(\);\s*// Another slot has another pick, or none\.\s*QueuePickRingSync\(\);", code);
            Assert.Matches(@"SyncTabVisibility\(\);\s*BindActiveModelView\(\);[^}]*QueuePickRingSync\(\);\s*\}", code);
            Assert.Contains("private void PictureHost_SizeChanged(object sender, SizeChangedEventArgs e) => QueuePickRingSync();", code);
            Assert.Contains("private void OnPickBoundsChanged(object sender, EventArgs e) => QueuePickRingSync();", code);
            Assert.Matches(@"foreach \(var picture in PicturesWithPick\(\)\)\s*\{\s*picture\.PickBoundsChanged \+= OnPickBoundsChanged;\s*picture\.HoverTargetChanged \+= OnHoverTargetChanged;\s*\}", code);

            Assert.Matches(@"private void QueuePickRingSync\(\)\s*\{\s*if \(_pickRingQueued\) return;\s*_pickRingQueued = true;\s*Dispatcher\.BeginInvoke\(System\.Windows\.Threading\.DispatcherPriority\.Loaded, new Action\(\(\) =>\s*\{\s*_pickRingQueued = false;\s*SyncPickRing\(\);\s*\}\)\);\s*\}", code);

            Assert.Contains("if (DataContext is PadViewModel vm && vm.IsButtonsPictureView)", code);
            Assert.Contains("place = RingAround(vm.PickedTargets());", code);
            Assert.Contains("foreach (string target in targets)", code);
            Assert.Contains("if (parts > 1) round = true;", code);
            Assert.Matches(@"return at is Rect control\s*\? PicturePick\.Place\(control, round, new Size\(PickRingLayer\.ActualWidth, PickRingLayer\.ActualHeight\)\)\s*: default;", code);
            Assert.Matches(@"if \(place\.Line\.Width > 0\)\s*\{\s*PutPickRing\(PickRing, place\.Line, place\.LineRadius\);\s*PutPickRing\(PickRingBand, place\.Band, place\.BandRadius\);\s*\}\s*else\s*\{\s*PickRing\.Visibility = Visibility\.Collapsed;\s*PickRingBand\.Visibility = Visibility\.Collapsed;\s*\}", code);
            // The hover ring: the row a click there picks, ringed as a pick
            // is, and hidden on the picked control or where that is drawn.
            Assert.Matches(@"string under = _hoverTarget == null \? null : MappingTargetOf\(vm, _hoverTarget\);\s*if \(under != null\) hover = RingAround\(vm\.HoveredTargets\(under\)\);", code);
            Assert.Matches(@"if \(hover\.Line\.Width > 0 && hover\.Line != place\.Line\) PutPickRing\(HoverRing, hover\.Line, hover\.LineRadius\);\s*else HoverRing\.Visibility = Visibility\.Collapsed;", code);
            Assert.Contains("targetName = MappingTargetOf(vm, targetName);", code);
            // A leave from a picture the pointer has already left for another
            // changes nothing, and a hover that changed nothing is not placed
            // again.
            Assert.Matches(@"if \(_hoverSource == null \|\| !ReferenceEquals\(sender, _hoverSource\)\) return;", code);
            Assert.Matches(@"if \(ReferenceEquals\(sender, _hoverSource\) && string\.Equals\(target, _hoverTarget, StringComparison\.Ordinal\)\) return;", code);
            // A picture that is not set to show is not asked, and the
            // answer is mapped to where the ring is drawn.
            Assert.Contains("if (view.Visibility != Visibility.Visible) continue;", code);
            Assert.Contains("drawn = view.TransformToVisual(PickRingLayer).TransformBounds(own);", code);

            // The view model says the pick changed with the row.
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.PickedTarget = "ButtonB";
            Assert.Contains(nameof(PadViewModel.PickedRow), raised);
            raised.Clear();
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.Contains(nameof(PadViewModel.PickedRow), raised);
        }

        // ── the ring under the pointer ─────────────────────────────────

        /// <summary>A control under the pointer is ringed as a pick is: a
        /// stick or a D-pad as the whole control, from any of its rows or
        /// directions. The picked control takes no hover ring, and a name
        /// with no row takes none. With nothing picked, as the picture
        /// opens, every control takes the hover ring.</summary>
        [Fact]
        public void AHoverRingsTheControlUnderThePointer_UnlessItIsThePickedOne()
        {
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            Assert.Empty(vm.PickedTargets());
            Assert.Equal(new[] { "ButtonA" }, vm.HoveredTargets("ButtonA"));
            vm.PickedTarget = "ButtonA";
            Assert.Equal(new[] { "ButtonA" }, vm.PickedTargets());
            Assert.Equal(new[] { "ButtonB" }, vm.HoveredTargets("ButtonB"));
            Assert.Empty(vm.HoveredTargets("ButtonA"));
            Assert.Equal(new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" }.OrderBy(t => t, StringComparer.Ordinal),
                vm.HoveredTargets("DPadLeft").OrderBy(t => t, StringComparer.Ordinal));
            Assert.Equal(new[] { "LeftThumbAxisX", "LeftThumbAxisY", "LeftThumbButton" },
                vm.HoveredTargets("LeftThumbAxisYNeg").OrderBy(t => t, StringComparer.Ordinal));
            Assert.Empty(vm.HoveredTargets("NoSuchControl"));
            Assert.Empty(vm.HoveredTargets(null));

            // A stick picked: none of its rows takes the hover ring, and the
            // other stick does.
            vm.PickedTarget = "LeftThumbButton";
            Assert.Empty(vm.HoveredTargets("LeftThumbAxisXNeg"));
            Assert.Empty(vm.HoveredTargets("LeftThumbButton"));
            Assert.Equal(new[] { "RightThumbAxisX", "RightThumbAxisY", "RightThumbButton" },
                vm.HoveredTargets("RightThumbAxisX").OrderBy(t => t, StringComparer.Ordinal));

            // With nothing picked, which only the list allows, any control
            // takes it.
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            vm.PickedTarget = null;
            Assert.Equal(new[] { "ButtonA" }, vm.HoveredTargets("ButtonA"));
        }

        private static void Pointer(UIElement part, RoutedEvent what)
            => part.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = what });

        private static List<UIElement> PartsOf(DependencyObject root)
        {
            var parts = new List<UIElement>();
            void Walk(DependencyObject at)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(at); i++)
                {
                    var child = VisualTreeHelper.GetChild(at, i);
                    if (child is UIElement part) parts.Add(part);
                    Walk(child);
                }
            }
            Walk(root);
            return parts;
        }

        /// <summary>Points at each part of a picture, clicks it and leaves
        /// it, as the pointer would, and checks that a part a click records
        /// said that control while the pointer was on it, and none after.
        /// The events carry no position: a part reads the pointer's, which
        /// is the same for the three. Returns how many parts a click
        /// records.</summary>
        private static int PointAtEachPart(UserControl view)
        {
            var picture = (IPictureWithPick)view;
            var said = new List<string>();
            string clicked = null;
            picture.HoverTargetChanged += (s, target) => said.Add(target);
            view.GetType().GetEvent("ControllerElementRecordRequested")!
                .AddEventHandler(view, new EventHandler<string>((s, target) => clicked = target));
            int recorded = 0;
            foreach (var part in PartsOf(view))
            {
                said.Clear();
                clicked = null;
                Pointer(part, UIElement.MouseEnterEvent);
                Pointer(part, UIElement.MouseMoveEvent);
                string hovered = said.LastOrDefault();
                part.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                    System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Pointer(part, UIElement.MouseLeaveEvent);
                if (clicked == null) continue;
                recorded++;
                Assert.Equal(clicked, hovered);
                Assert.Null(said.Last());
            }
            return recorded;
        }

        /// <summary>A picture says which control is under the pointer by the
        /// name a click there records, and says none when the pointer leaves
        /// (#574). Every part of the keyboard and mouse, the touch sensors'
        /// capsules and the VR hands that a click records does. The 3D and
        /// 2D models, the schematic and the MIDI board, which the test host
        /// cannot draw, say it from the hover code each has, by the name
        /// their click uses.</summary>
        [Fact]
        public void APictureSaysTheControlUnderThePointer_AsAClickNamesIt()
        {
            RunSta(() =>
            {
                var kbm = new KBMPreviewView();
                kbm.Resources["TelemetryFontFamily"] = new FontFamily("Consolas");
                kbm.Bind(new PadViewModel(0) { OutputType = VirtualControllerType.KeyboardMouse });
                Lay(kbm, 975, 726);
                Assert.True(PointAtEachPart(kbm) > 100, "the keys and the mouse's controls");
                kbm.Unbind();

                var vm = new PadViewModel(0) { OutputType = VirtualControllerType.Extended };
                typeof(PadViewModel).GetField("_profileId", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(vm, "steam-controller-2");
                var strip = new TouchSensorStrip();
                var attach = typeof(TouchSensorStrip).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)!;
                attach.Invoke(strip, new object[] { vm });
                Lay(strip, 600, 40);
                int capsules = ((StackPanel)strip.Content).Children.Count;
                Assert.True(capsules > 0);
                Assert.Equal(capsules, PointAtEachPart(strip));
                attach.Invoke(strip, new object[] { null });

                var hands = new VRPreviewView();
                typeof(VRPreviewView).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(hands, null);
                Lay(hands, 975, 726);
                // One hit area for each element the hands draw.
                var elements = (Array)typeof(VRPreviewView).GetField("Elements", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                Assert.Equal(elements.Length, PointAtEachPart(hands));
            });

            static string Code(string file) => ExtrasPageTests.RepoText("PadForge.App", "Views", file);
            int Leaves(string code) => System.Text.RegularExpressions.Regex.Matches(code, @"HoverTargetChanged\?\.Invoke\(this, null\);").Count;

            string model = Code("ControllerModelView.xaml.cs");
            Assert.Matches(@"ShowHoverQuadrant\(quadrant\);\s*ModelViewPort\.Cursor = Cursors\.Hand;\s*HoverTargetChanged\?\.Invoke\(this, quadrant\);\s*return;", model);
            Assert.Matches(@"ApplyHoverHighlight\(kv\.Key\);\s*ModelViewPort\.Cursor = Cursors\.Hand;\s*HoverTargetChanged\?\.Invoke\(this, kv\.Value\);\s*return;", model);
            Assert.Matches(@"ModelViewPort\.Cursor = Cursors\.Arrow;\s*HoverTargetChanged\?\.Invoke\(this, null\);\s*\}", model);
            Assert.Contains("ControllerElementRecordRequested?.Invoke(this, stickAxis);", model);
            Assert.Contains("ControllerElementRecordRequested?.Invoke(this, kv.Value);", model);

            string flat = Code("ControllerModel2DView.xaml.cs");
            Assert.Contains("HoverTargetChanged?.Invoke(this, ClickTargetAt(rect, target, e.GetPosition(rect)));", flat);
            Assert.Contains("ControllerElementRecordRequested?.Invoke(this, ClickTargetAt(rect, target, e.GetPosition(rect)));", flat);
            Assert.Matches(@"_dirty = true; // Next render frame restores proper state\s*HoverTargetChanged\?\.Invoke\(this, null\);", flat);
            Assert.Equal(1, Leaves(flat));

            string schematic = Code("ControllerSchematicView.xaml.cs");
            foreach (string named in new[] { "StickTarget(e.GetPosition(outer))", "$\"RawAxis{axisIdx}\"", "PovTarget(e.GetPosition(outer))", "$\"RawBtn{index}\"" })
            {
                Assert.Contains("HoverTargetChanged?.Invoke(this, " + named + ");", schematic);
                Assert.Contains("ControllerElementRecordRequested?.Invoke(this, " + named + ");", schematic);
            }
            Assert.Equal(4, Leaves(schematic));

            string midi = Code("MidiPreviewView.xaml.cs");
            foreach (string named in new[] { "$\"MidiCC{index}\"", "$\"MidiNote{noteIndex}\"" })
            {
                Assert.Contains("HoverTargetChanged?.Invoke(this, " + named + ");", midi);
                Assert.Contains("ControllerElementRecordRequested?.Invoke(this, " + named + ");", midi);
            }
            Assert.Equal(2, Leaves(midi));
        }
    }
}
