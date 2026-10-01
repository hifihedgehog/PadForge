using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Media3D;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Models2D;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The DualShock 3 has a body of its own in both previews. Until it
    /// did, the dualshock-3 profile fell through the generic DualShock
    /// route and drew a DualShock 4: a touchpad, a light bar, SHARE and
    /// OPTIONS, none of which the pad has.
    ///
    /// <para>The mesh is a bought model split by tools/dualshock3_mesh.py
    /// and the 2D art is drawn by tools/dualshock3_art.py, so these tests
    /// pin the chain from the profile id to files on disk, every control
    /// the pad has in both views, and the constants the converter derived
    /// from the mesh.</para>
    /// </summary>
    public class DualShock3ArtworkTests
    {
        private static string AppDir()
        {
            var d = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && d != null; i++)
            {
                var c = Path.Combine(d, "PadForge.App");
                if (Directory.Exists(Path.Combine(c, "2DModels"))) return c;
                d = Path.GetDirectoryName(d);
            }
            return null;
        }

        /// <summary>The controls a DualShock 3 has: four face buttons, the
        /// d-pad, L1 and R1, the two analog triggers, SELECT, START, the PS
        /// button, and two sticks that click.</summary>
        public static TheoryData<string> Controls => new()
        {
            "ButtonA", "ButtonB", "ButtonX", "ButtonY",
            "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
            "LeftShoulder", "RightShoulder", "LeftTrigger", "RightTrigger",
            "ButtonBack", "ButtonStart", "ButtonGuide",
            "LeftThumbRing", "RightThumbRing", "LeftThumbButton", "RightThumbButton",
        };

        [Fact]
        public void TheProfileIsOfferedAsAPlayStationPad()
        {
            Assert.Contains(HMaestroProfileCatalog.PlayStationProfiles, p => p.Id == "dualshock-3");
        }

        /// <summary>Its own art in both views, matched before the generic
        /// DualShock line that hands every other DualShock the DS4.</summary>
        [Fact]
        public void TheProfileResolvesToItsOwnArt()
        {
            var (name2D, name3D) = HMaestroProfileCatalog.ResolveAssetFolders(
                "dualshock-3", VirtualControllerType.PlayStation, out bool dedicated);
            Assert.Equal("DS3", name2D);
            Assert.Equal("DS3", name3D);
            Assert.True(dedicated);

            Assert.Equal(("DS4", "DS4"), HMaestroProfileCatalog.ResolveAssetFolders(
                "dualshock-4-v2", VirtualControllerType.PlayStation));
        }

        [Fact]
        public void TheFoldersShipTheirFiles()
        {
            var app = AppDir();
            Assert.NotNull(app);
            Assert.True(File.Exists(Path.Combine(app, "3DModels", "DS3", "MainBody.obj")));
            Assert.True(File.Exists(Path.Combine(app, "3DModels", "DS3", "Body.jpg")));
            Assert.True(File.Exists(Path.Combine(app, DualShock3Layout.BasePath)));
        }

        [Theory]
        [MemberData(nameof(Controls))]
        public void The2DLayoutCarriesTheControl(string target)
        {
            Assert.Contains(DualShock3Layout.Overlays, o => o.TargetName == target);
        }

        /// <summary>The pad has no touchpad, so the layout must not offer
        /// one to hover or bind.</summary>
        [Fact]
        public void The2DLayoutHasNoTouchpad()
        {
            Assert.DoesNotContain(DualShock3Layout.Overlays,
                o => o.ElementType == OverlayElementType.Touchpad || o.TargetName.Contains("Touchpad"));
        }

        /// <summary>Every sprite exists and is the size its entry declares.
        /// The view stretches an image into its rect, so a mismatch draws a
        /// distorted control rather than failing.</summary>
        [Fact]
        public void EverySpriteIsTheSizeItsEntryDeclares()
        {
            var app = AppDir();
            Assert.NotNull(app);
            foreach (var ov in DualShock3Layout.Overlays)
            {
                if (string.IsNullOrEmpty(ov.ImageFile)) continue;
                var path = Path.Combine(app, "2DModels", "DS3", ov.ImageFile);
                Assert.True(File.Exists(path), $"{ov.TargetName}: {ov.ImageFile} is missing");
                using var fs = File.OpenRead(path);
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                    fs, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                Assert.Equal((int)ov.Width, frame.PixelWidth);
                Assert.Equal((int)ov.Height, frame.PixelHeight);
            }
        }

        /// <summary>Every control is a target in the 3D view too: buttons in
        /// the press map, triggers in the click map, stick directions on
        /// the two caps.</summary>
        [Theory]
        [MemberData(nameof(Controls))]
        public void The3DModelCarriesTheControl(string target)
        {
            using var m = ControllerModelBase.Create("DS3", null, false);
            if (target.EndsWith("ThumbRing", StringComparison.Ordinal))
            {
                var ring = target.StartsWith("Left", StringComparison.Ordinal)
                    ? m.LeftThumbRing : m.RightThumbRing;
                Assert.NotNull(ring);
                Assert.True(m.QuadrantMap.ContainsKey(ring));
                return;
            }
            Assert.Contains(target, m.ClickMap.Values);
        }

        /// <summary>The face buttons and d-pad keys sit where their names
        /// say: cross (ButtonA) at the bottom, circle (ButtonB) right,
        /// square (ButtonX) left, triangle (ButtonY) at the top. A swapped
        /// pair passes every other test and lights the wrong button.</summary>
        [Fact]
        public void EachButtonSitsWhereItsNameSaysIn3D()
        {
            using var m = ControllerModelBase.Create("DS3", null, false);
            Point3D C(string t)
            {
                var b = m.ButtonMap[t][0].Bounds;
                return new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            }
            // +X is the controller's right, +Z its top edge.
            foreach (var (top, bottom, left, right) in new[]
                     {
                         ("ButtonY", "ButtonA", "ButtonX", "ButtonB"),
                         ("DPadUp", "DPadDown", "DPadLeft", "DPadRight"),
                     })
            {
                Assert.True(C(top).Z > C(left).Z && C(top).Z > C(right).Z && C(left).Z > C(bottom).Z,
                    $"{top} must be highest and {bottom} lowest");
                Assert.True(C(left).X < C(top).X && C(top).X < C(right).X,
                    $"{left} must be left of {top} and {right} right of it");
            }
            Assert.True(C("ButtonBack").X < C("ButtonGuide").X && C("ButtonGuide").X < C("ButtonStart").X,
                "SELECT, the PS button and START run left to right");
        }

        [Fact]
        public void EachButtonSitsWhereItsNameSaysIn2D()
        {
            (double X, double Y) C(string t)
            {
                var o = DualShock3Layout.Overlays.First(e => e.TargetName == t);
                return (o.X + o.Width / 2, o.Y + o.Height / 2);
            }
            // Canvas y grows downward.
            foreach (var (top, bottom, left, right) in new[]
                     {
                         ("ButtonY", "ButtonA", "ButtonX", "ButtonB"),
                         ("DPadUp", "DPadDown", "DPadLeft", "DPadRight"),
                     })
            {
                Assert.True(C(top).Y < C(left).Y && C(top).Y < C(right).Y && C(left).Y < C(bottom).Y,
                    $"{top} must be highest and {bottom} lowest");
                Assert.True(C(left).X < C(top).X && C(top).X < C(right).X,
                    $"{left} must be left of {top} and {right} right of it");
            }
            Assert.True(C("ButtonBack").X < C("ButtonGuide").X && C("ButtonGuide").X < C("ButtonStart").X,
                "SELECT, the PS button and START run left to right");
            Assert.True(C("LeftShoulder").X < C("RightShoulder").X && C("LeftTrigger").X < C("RightTrigger").X);
        }

        [Fact]
        public void The3DModelHasNoTouchpad()
        {
            using var m = ControllerModelBase.Create("DS3", null, false);
            Assert.Null(m.Touchpad);
            Assert.DoesNotContain("TouchpadClick", m.ClickMap.Values);
        }

        /// <summary>Each stick pivots on its own axis, behind its cap. The
        /// converter measured the pivot as the center of the stick's dome,
        /// a sphere, so it lies on the line through the cap's center, well
        /// behind the cap (the face is -Y).</summary>
        [Fact]
        public void EachStickPivotsOnItsAxisBehindTheCap()
        {
            using var m = ControllerModelBase.Create("DS3", null, false);
            foreach (var (ring, pivot) in new[]
                     {
                         (m.LeftThumbRing, m.JoystickRotationPointCenterLeftMillimeter),
                         (m.RightThumbRing, m.JoystickRotationPointCenterRightMillimeter),
                     })
            {
                Rect3D b = ring.Bounds;
                Assert.InRange(pivot.X, b.X + b.SizeX / 2 - 0.1, b.X + b.SizeX / 2 + 0.1);
                Assert.InRange(pivot.Z, b.Z + b.SizeZ / 2 - 0.1, b.Z + b.SizeZ / 2 + 0.1);
                Assert.True(pivot.Y > b.Y + b.SizeY + 15,
                    $"pivot Y {pivot.Y:F1} sits within 15 mm of the cap's back at {b.Y + b.SizeY:F1}");
            }
        }

        /// <summary>The ring is the cap's head, in front of the click
        /// (stem and dome), and the two do not overlap in depth.</summary>
        [Fact]
        public void TheRingIsTheHeadInFrontOfTheClick()
        {
            using var m = ControllerModelBase.Create("DS3", null, false);
            foreach (var (ring, click) in new[] { (m.LeftThumbRing, m.LeftThumb), (m.RightThumbRing, m.RightThumb) })
            {
                Rect3D r = ring.Bounds, c = click.Bounds;
                Assert.True(r.Y < c.Y, "the ring must be the frontmost part of the stick");
                Assert.True(r.Y + r.SizeY <= c.Y + 0.01, "ring and click overlap in depth");
            }
        }

        /// <summary>The hinge sits a third of the way up each trigger by the
        /// Xbox One model's fraction of the part's own bounds.</summary>
        [Fact]
        public void EachTriggerHingesAThirdOfTheWayUp()
        {
            using var m = ControllerModelBase.Create("DS3", null, false);
            foreach (var (trigger, hinge) in new[]
                     {
                         (m.LeftShoulderTrigger, m.ShoulderTriggerRotationPointCenterLeftMillimeter),
                         (m.RightShoulderTrigger, m.ShoulderTriggerRotationPointCenterRightMillimeter),
                     })
            {
                Rect3D b = trigger.Bounds;
                Assert.InRange((hinge.Y - b.Y) / b.SizeY, 0.317, 0.337);
                Assert.InRange((hinge.Z - b.Z) / b.SizeZ, 0.361, 0.381);
            }
        }
    }
}
