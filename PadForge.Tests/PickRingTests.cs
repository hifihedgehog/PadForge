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
        /// row. Nothing is ringed with nothing picked.</summary>
        [Fact]
        public void ThePickRingsEveryRowOfAStickOrADPad()
        {
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            Assert.Empty(vm.PickedTargets());

            vm.PickedTarget = "ButtonA";
            Assert.Equal(new[] { "ButtonA" }, vm.PickedTargets());

            vm.PickedTarget = "DPadUp";
            Assert.Equal(new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" }.OrderBy(t => t),
                vm.PickedTargets().OrderBy(t => t));

            vm.PickedTarget = "LeftThumbAxisXNeg";
            Assert.Equal(vm.Mappings.Where(m => m.Category == MappingCategory.LeftStick).Select(m => m.TargetSettingName),
                vm.PickedTargets());
            Assert.Contains("LeftThumbAxisX", vm.PickedTargets());
            Assert.Contains("LeftThumbAxisY", vm.PickedTargets());
            Assert.DoesNotContain("RightThumbAxisX", vm.PickedTargets());

            vm.PickedTarget = "LeftTrigger";
            Assert.Equal(new[] { "LeftTrigger" }, vm.PickedTargets());

            vm.PickedTarget = "NoSuchControl";
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
        /// mirrored in a right-to-left language. Both parts start
        /// hidden.</summary>
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

            Assert.Equal(new[] { "PickRingBand", "PickRing" }, layer.Elements().Select(Name));
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
        }

        /// <summary>The ring is placed again whenever the control may be
        /// drawn elsewhere: another pick, view or set of rows, another
        /// slot, another picture, a picture of another size, and a picture
        /// that says its controls moved. It is placed after the layout has
        /// settled, once for all the requests made before that. It shows
        /// in the picture's view only, a ring around several parts is
        /// round, and with no control to ring both its parts are
        /// hidden.</summary>
        [Fact]
        public void TheRingFollowsThePick_TheViewAndThePicture()
        {
            string code = CodeBehind();
            Assert.Matches(@"else if \(e\.PropertyName == nameof\(PadViewModel\.PickedRow\)\)\s*\{[^}]*QueuePickRingSync\(\);\s*\}", code);
            Assert.Matches(@"ResubscribeSelectedDeviceInfo\(\);\s*// Another slot has another pick, or none\.\s*QueuePickRingSync\(\);", code);
            Assert.Matches(@"SyncTabVisibility\(\);\s*BindActiveModelView\(\);[^}]*QueuePickRingSync\(\);\s*\}", code);
            Assert.Contains("private void PictureHost_SizeChanged(object sender, SizeChangedEventArgs e) => QueuePickRingSync();", code);
            Assert.Contains("private void OnPickBoundsChanged(object sender, EventArgs e) => QueuePickRingSync();", code);
            Assert.Matches(@"foreach \(var picture in PicturesWithPick\(\)\)\s*picture\.PickBoundsChanged \+= OnPickBoundsChanged;", code);

            Assert.Matches(@"private void QueuePickRingSync\(\)\s*\{\s*if \(_pickRingQueued\) return;\s*_pickRingQueued = true;\s*Dispatcher\.BeginInvoke\(System\.Windows\.Threading\.DispatcherPriority\.Loaded, new Action\(\(\) =>\s*\{\s*_pickRingQueued = false;\s*SyncPickRing\(\);\s*\}\)\);\s*\}", code);

            Assert.Contains("if (DataContext is PadViewModel vm && vm.IsButtonsPictureView)", code);
            Assert.Contains("foreach (string target in vm.PickedTargets())", code);
            Assert.Contains("if (parts > 1) round = true;", code);
            Assert.Matches(@"var place = at is Rect control\s*\? PicturePick\.Place\(control, round, new Size\(PickRingLayer\.ActualWidth, PickRingLayer\.ActualHeight\)\)\s*: default;\s*if \(!\(place\.Line\.Width > 0\)\)\s*\{\s*PickRing\.Visibility = Visibility\.Collapsed;\s*PickRingBand\.Visibility = Visibility\.Collapsed;\s*return;\s*\}\s*PutPickRing\(PickRing, place\.Line, place\.LineRadius\);\s*PutPickRing\(PickRingBand, place\.Band, place\.BandRadius\);", code);
            // A picture that is not set to show is not asked, and the
            // answer is mapped to where the ring is drawn.
            Assert.Contains("if (view.Visibility != Visibility.Visible) continue;", code);
            Assert.Contains("drawn = view.TransformToVisual(PickRingLayer).TransformBounds(own);", code);

            // The view model says the pick changed with the row.
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.PickedTarget = "ButtonA";
            Assert.Contains(nameof(PadViewModel.PickedRow), raised);
            raised.Clear();
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.Contains(nameof(PadViewModel.PickedRow), raised);
        }
    }
}
