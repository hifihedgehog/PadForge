using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PadForge.Models2D;
using PadForge.Models3D;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The 3D preview's interaction contract, pinned per model.
    ///
    /// <para>Written after all three Valve models shipped with an empty
    /// HighlightMaterials (none of them called DrawAccentHighlights), so not
    /// one button on the Steam Deck, the 2015 Steam Controller or the 2026
    /// Steam Controller lit up on hover, on press, or while recording. The
    /// hit test and the click-to-record path worked the whole time: only the
    /// glow was missing, which is exactly the part a person sees.</para>
    /// </summary>
    public class ControllerModel3DHoverTests
    {
        /// <summary>Every model the viewport can build, with the arguments
        /// ControllerModelView.EnsureModel passes for it.</summary>
        public static IEnumerable<object[]> Families() => new[]
        {
            new object[] { "Xbox360", null, false },
            new object[] { "XboxSeries", "Carbon", true },
            new object[] { "XboxSeries", "Carbon", false },
            new object[] { "DS4", "JetBlack", false },
            new object[] { "DualSense", "White", false },
            new object[] { "DualSenseEdge", null, false },
            new object[] { "Switch2Pro", null, true },
            new object[] { "Switch2Pro", null, false },
            new object[] { "SteamDeck", null, false },
            new object[] { "SteamController", null, false },
            new object[] { "SteamController2", null, false },
        };

        private static string[] PressLoopRoles() =>
            (string[])typeof(ControllerModelView)
                .GetField("ButtonProperties", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);

        /// <summary>THE PROPERTY. Every control the user can hover has the
        /// three things the hover path looks up: geometry the hit test can
        /// match, a highlight material to glow with, and a default material
        /// to restore to. A control missing the middle one is invisible to
        /// the user even though every other part of the path runs.</summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void EveryClickableGroupCanGlowAndRestore(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);

            Assert.NotEmpty(m.ClickMap);
            foreach (var kv in m.ClickMap)
            {
                // UpdateHoverHighlight matches with Children.Contains(hitGeo),
                // so a group whose geometry is nested deeper can never hover.
                Assert.True(kv.Key.Children.OfType<GeometryModel3D>().Any(),
                    $"{family}: {kv.Value} has no direct geometry, so the hit test can never match it");
                Assert.True(m.HighlightMaterials.ContainsKey(kv.Key),
                    $"{family}: {kv.Value} has no highlight material, so hovering it shows nothing");
                Assert.True(m.DefaultMaterials.ContainsKey(kv.Key),
                    $"{family}: {kv.Value} has no default material, so it never returns to rest");
            }
        }

        /// <summary>Every role a model maps is a role the per-frame press
        /// loop actually drives. The Valve roles were read by the state
        /// switch and iterated by nothing, so they never lit on press.</summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void EveryMappedRoleIsDrivenByThePressLoop(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);
            var driven = PressLoopRoles();
            foreach (var role in m.ButtonMap.Keys)
                Assert.True(driven.Contains(role),
                    $"{family}: {role} is mapped on the model but absent from the press loop's table");
        }

        /// <summary>The stick-glow material the deflection grade reads is
        /// present on both rings, or the model has no ring to grade.</summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void StickRingsCanGrade(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);
            foreach (var ring in new[] { m.LeftThumbRing, m.RightThumbRing })
            {
                if (ring == null) continue;
                Assert.True(m.HighlightMaterials.ContainsKey(ring), $"{family}: a stick ring has no highlight material");
                Assert.True(m.DefaultMaterials.ContainsKey(ring), $"{family}: a stick ring has no default material");
            }
        }

        /// <summary>A stick the user can click is a stick whose DIRECTIONS
        /// are reachable too, through a registered quadrant surface: the cap
        /// solid, or the click mesh where a pad ships no separate cap.</summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void EveryStickHasADirectionSurface(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);
            void Check(string button, Model3DGroup ring, Model3DGroup cap, string[] want)
            {
                if (!m.ButtonMap.ContainsKey(button)) return;
                var surface = ring ?? cap;
                Assert.True(surface != null, $"{family}: {button} clicks but has no head to take a direction from");
                Assert.True(m.QuadrantMap.TryGetValue(surface, out var got),
                    $"{family}: the head for {button} carries no directions");
                Assert.Equal(want, got);
            }
            Check("LeftThumbButton", m.LeftThumbRing, m.LeftThumb,
                new[] { "LeftThumbAxisYNeg", "LeftThumbAxisY", "LeftThumbAxisXNeg", "LeftThumbAxisX" });
            Check("RightThumbButton", m.RightThumbRing, m.RightThumb,
                new[] { "RightThumbAxisYNeg", "RightThumbAxisY", "RightThumbAxisXNeg", "RightThumbAxisX" });
        }

        /// <summary>Every direction surface is one the user can see and that
        /// can glow: real geometry, a resting material, a highlight. A group
        /// with no resting material renders in the OBJ loader's yellow
        /// default, which is how it announces itself.</summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void EveryQuadrantSurfaceIsPaintedAndCanGlow(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);
            Assert.NotEmpty(m.QuadrantMap);
            foreach (var kv in m.QuadrantMap)
            {
                Assert.Equal(4, kv.Value.Length);
                Assert.All(kv.Value, n => Assert.False(string.IsNullOrWhiteSpace(n)));
                Assert.True(kv.Key.Children.OfType<GeometryModel3D>().Any(),
                    $"{family}: a direction surface has no direct geometry, so the hit test can never match it");
                Assert.True(m.DefaultMaterials.ContainsKey(kv.Key),
                    $"{family}: a direction surface has no resting material and renders in the loader's default");
                Assert.True(m.HighlightMaterials.ContainsKey(kv.Key),
                    $"{family}: a direction surface has no highlight material");
            }
        }

        /// <summary>The stick BUTTON glows its own click mesh, never the cap.
        /// Owning both made pressing or hovering the click light the entire
        /// stick, so the two controls a stick carries looked like one.</summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void StickButtonDoesNotOwnTheCap(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);
            if (m.LeftThumbRing != null && m.ButtonMap.TryGetValue("LeftThumbButton", out var l))
                Assert.DoesNotContain(m.LeftThumbRing, l);
            if (m.RightThumbRing != null && m.ButtonMap.TryGetValue("RightThumbButton", out var r))
                Assert.DoesNotContain(m.RightThumbRing, r);
        }

        /// <summary>The 2015 Steam Controller's two pads are not two of a
        /// kind. SDL drives the LEFT one as the D-pad and the RIGHT one as
        /// the right thumbstick (SDL_hidapi_steam.c 1655 and 1673), and
        /// Valve moulds a D-pad cross into the left cover, naming that solid
        /// TrackPadCoverDirectional against the right one's Smooth. Each pad
        /// keeps its click in the middle.</summary>
        [Fact]
        public void SteamController2015_LeftPadIsTheDPad_RightPadIsTheStick()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            var left = m.ButtonMap["LeftTouchpadClick"][0];
            var right = m.ButtonMap["RightTouchpadClick"][0];

            Assert.Equal(new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" }, m.QuadrantMap[left]);
            Assert.Equal(new[] { "RightThumbAxisYNeg", "RightThumbAxisY", "RightThumbAxisXNeg", "RightThumbAxisX" },
                m.QuadrantMap[right]);
            Assert.True(m.ClickMap.ContainsKey(left));
            Assert.True(m.ClickMap.ContainsKey(right));
        }

        /// <summary>Every direction a Valve model offers resolves to a real
        /// row on that profile's grid, so clicking one records something
        /// instead of falling on the floor.</summary>
        [Theory]
        [InlineData("SteamController", "steam-controller")]
        [InlineData("SteamController", "steam-controller-composite")]
        [InlineData("SteamController2", "steam-controller-2")]
        [InlineData("SteamDeck", "steam-deck-composite")]
        public void QuadrantTargetsReachTheGrid(string family, string profileId)
        {
            using var m = ControllerModelBase.Create(family, null, false);
            foreach (var names in m.QuadrantMap.Values)
                foreach (var n in names)
                    Assert.False(string.IsNullOrEmpty(NintendoPreviewMap.ToRaw(n, profileId)),
                        $"{profileId}: {n} does not translate to any grid target");
        }

        /// <summary>The 2015 stick is a knurled cap on a wider base, two
        /// solids in Valve's CAD (ThumbTopGrip and ThumbTopBase). Folding
        /// both into the click mesh left the pad with no cap group, so its
        /// stick had no direction surface and no visible collar.</summary>
        [Fact]
        public void SteamController2015_StickHasACapInsideItsCollar()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            Assert.NotNull(m.LeftThumbRing);
            double cap = m.LeftThumbRing.Bounds.SizeX;
            double collar = m.ButtonMap["LeftThumbButton"][0].Bounds.SizeX;
            Assert.True(cap < collar,
                $"cap {cap:F1} mm must sit inside collar {collar:F1} mm so the collar reads as a ring");
        }

        private static IEnumerable<Point3D> Vertices(Model3DGroup group)
        {
            foreach (var child in group.Children)
                if (child is GeometryModel3D g && g.Geometry is MeshGeometry3D mesh)
                    foreach (Point3D p in mesh.Positions)
                        yield return p;
        }

        /// <summary>THE PROPERTY for the stick button: its highlight covers
        /// the stick from the case all the way up to the cap. Model space
        /// puts the face along -Y, so the button's topmost geometry has to
        /// reach the cap's underside.
        ///
        /// <para>The Steam Deck failed this. It splits its stick into three
        /// solids where every other model ships two, and its middle one, the
        /// capacitive barrel, was scenery. The button lit only the thin
        /// collar at the case and the stick read as half dead.</para></summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void StickButtonReachesTheCap(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);
            void Check(string button, Model3DGroup ring)
            {
                if (ring == null || !m.ButtonMap.TryGetValue(button, out var groups)) return;
                double capUnderside = ring.Bounds.Y + ring.Bounds.SizeY;
                double buttonTop = groups.Min(g => g.Bounds.Y);
                Assert.True(buttonTop <= capUnderside + 0.5,
                    $"{family}: {button} stops {buttonTop - capUnderside:F2} mm short of the cap, "
                    + "so the stem below it never lights");
            }
            Check("LeftThumbButton", m.LeftThumbRing);
            Check("RightThumbButton", m.RightThumbRing);
        }

        /// <summary>And the other half of the same contract: the button's
        /// geometry stops AT the cap and never reaches inside it. Valve
        /// models the 2015 stick as a press fit, so the base carries a
        /// spigot that runs up through the cap's shell wall, out at
        /// r = 7.65 mm where the cap's inner wall sits at 5.75 and its outer
        /// at 8.81. Left in, the button's accent had geometry inside the cap
        /// to bleed through, and the cap is the one part of a stick that
        /// must never light with its button.</summary>
        [Fact]
        public void SteamController2015_DoughnutStaysOutOfTheCap()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            var cap = m.LeftThumbRing;
            var basePart = m.ButtonMap["LeftThumbButton"][0];

            double capUnderside = cap.Bounds.Y + cap.Bounds.SizeY;
            double axisX = cap.Bounds.X + cap.Bounds.SizeX / 2.0;
            double axisZ = cap.Bounds.Z + cap.Bounds.SizeZ / 2.0;
            double capRadius = cap.Bounds.SizeX / 2.0;

            int intruding = Vertices(basePart).Count(p =>
                p.Y <= capUnderside - 0.05
                && Math.Sqrt((p.X - axisX) * (p.X - axisX) + (p.Z - axisZ) * (p.Z - axisZ)) < capRadius - 0.8);
            Assert.True(intruding == 0,
                $"{intruding} base vertices sit inside the cap, where the button's accent can bleed onto it");

            // And the doughnut itself survives: the base still reaches well
            // outside the cap, which is what makes it read as a ring.
            double baseRadius = basePart.Bounds.SizeX / 2.0;
            Assert.True(baseRadius > capRadius + 2.0,
                $"base radius {baseRadius:F2} mm leaves no ring outside a {capRadius:F2} mm cap");
        }

        /// <summary>The wing splits off the cover ON the plane, so the seam
        /// is the straight line the plane makes. Assigning whole triangles
        /// to a side by their centroid leaves it jagged by a triangle's
        /// width, which was plainly visible on this cover.</summary>
        [Fact]
        public void SteamController2015_GripSeamIsStraight()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            double leftSeam = m.ButtonMap["LeftGrip"][0].Bounds.X + m.ButtonMap["LeftGrip"][0].Bounds.SizeX;
            double rightSeam = m.ButtonMap["RightGrip"][0].Bounds.X;
            Assert.True(Math.Abs(leftSeam + 30.0) < 0.01,
                $"the left wing's inboard edge is at {leftSeam:F3}, not on the cut plane at -30");
            Assert.True(Math.Abs(rightSeam - 30.0) < 0.01,
                $"the right wing's inboard edge is at {rightSeam:F3}, not on the cut plane at 30");

            // Nothing crosses the plane either, which a centroid cut allows.
            Assert.DoesNotContain(Vertices(m.ButtonMap["LeftGrip"][0]), p => p.X > -30.0 + 1e-6);
        }

        /// <summary>The 2015 grip paddle is the FLARED WING of the rear
        /// cover, split off the one solid Valve models the cover as. An
        /// earlier round carved the paddle out of the bottom shell by facing
        /// and position, which handed the whole handle skin to the grip
        /// highlight and ran it right to the handle's tip.</summary>
        [Fact]
        public void SteamController2015_GripIsTheCoverWingNotTheHandle()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            var grip = m.ButtonMap["LeftGrip"][0].Bounds;
            var body = m.MainBody.Bounds;

            // The paddle stops well short of the handle's tip. The carve
            // that took the whole skin came within 0.1 mm of it.
            double clearance = grip.Z - body.Z;
            Assert.True(clearance > 15,
                $"the grip reaches within {clearance:F1} mm of the handle tip: that is the handle, not the paddle");
            Assert.True(grip.SizeZ < body.SizeZ * 0.5,
                $"the grip runs {grip.SizeZ:F1} mm down a {body.SizeZ:F1} mm body: that is the handle, not the paddle");
        }

        /// <summary>A flat surface lying in the face plane, the shape of a
        /// trackpad: two triangles at a constant Y.</summary>
        private static Model3DGroup FlatPad(double y, double half)
        {
            var mesh = new MeshGeometry3D();
            mesh.Positions.Add(new Point3D(-half, y, -half));
            mesh.Positions.Add(new Point3D(half, y, -half));
            mesh.Positions.Add(new Point3D(half, y, half));
            mesh.Positions.Add(new Point3D(-half, y, half));
            foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
                mesh.TriangleIndices.Add(i);
            var g = new Model3DGroup();
            g.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(Brushes.Gray)));
            return g;
        }

        /// <summary>THE PROPERTY for the hover wedge: it comes out IN FRONT
        /// of the surface it was cut from, whatever that surface is shaped
        /// like. The offset used to push each point away from a torus
        /// skeleton circle, which on a flat trackpad is a sideways shove:
        /// the wedge slid across the pad instead of rising off it, so the pad
        /// covered its own direction wedges.</summary>
        [Theory]
        [InlineData(false, true)]     // up
        [InlineData(false, false)]    // down
        [InlineData(true, true)]      // left
        [InlineData(true, false)]     // right
        public void QuadrantWedge_RisesOffAFlatPad(bool isX, bool isNeg)
        {
            const double padY = -5.0;
            var pad = FlatPad(padY, 20.0);
            var method = typeof(ControllerModelView).GetMethod("BuildClippedQuadrantMesh",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var mesh = (MeshGeometry3D)method.Invoke(null,
                new object[] { pad, new Vector3D(0, padY, 0), isX, isNeg });

            Assert.NotEmpty(mesh.Positions);
            foreach (Point3D p in mesh.Positions)
                Assert.True(p.Y < padY - 0.5,
                    $"wedge vertex at Y={p.Y:F2} never rose off a pad sitting at Y={padY:F2}");
        }

        /// <summary>And it lands in the quadrant it was asked for.</summary>
        [Theory]
        [InlineData(false, true, "up")]
        [InlineData(false, false, "down")]
        [InlineData(true, true, "left")]
        [InlineData(true, false, "right")]
        public void QuadrantWedge_CoversOnlyItsOwnQuadrant(bool isX, bool isNeg, string which)
        {
            var pad = FlatPad(-5.0, 20.0);
            var method = typeof(ControllerModelView).GetMethod("BuildClippedQuadrantMesh",
                BindingFlags.NonPublic | BindingFlags.Static);
            var mesh = (MeshGeometry3D)method.Invoke(null,
                new object[] { pad, new Vector3D(0, -5.0, 0), isX, isNeg });

            Assert.NotEmpty(mesh.Positions);
            foreach (Point3D p in mesh.Positions)
            {
                // The clip is exact, so only rounding needs slack.
                const double eps = 1e-6;
                switch (which)
                {
                    case "up": Assert.True(p.Z >= Math.Abs(p.X) - eps, $"({p.X:F2},{p.Z:F2}) is not in the up wedge"); break;
                    case "down": Assert.True(-p.Z >= Math.Abs(p.X) - eps, $"({p.X:F2},{p.Z:F2}) is not in the down wedge"); break;
                    case "left": Assert.True(-p.X >= Math.Abs(p.Z) - eps, $"({p.X:F2},{p.Z:F2}) is not in the left wedge"); break;
                    default: Assert.True(p.X >= Math.Abs(p.Z) - eps, $"({p.X:F2},{p.Z:F2}) is not in the right wedge"); break;
                }
            }
        }

        /// <summary>THE PROPERTY that keeps a hover wedge off its
        /// neighbours: it never moves in X or Z, so it stays inside the
        /// footprint of the surface it was cut from no matter how tightly
        /// the controls are packed.
        ///
        /// <para>A control is a solid and most of it is buried: 52% of a
        /// 2015 trackpad's triangles and 69% of its stick cap's are side
        /// walls, inner shells and undersides. Lifting one of those along
        /// its own normal drives it sideways, out through whatever sits
        /// beside it. That pad sits in a recess with the case wall against
        /// its rim and the stick base 2.04 mm away, and the wedge cut into
        /// both.</para></summary>
        [Theory]
        [InlineData("SteamController", "LeftTouchpadClick")]
        [InlineData("SteamController", "RightTouchpadClick")]
        [InlineData("SteamController2", "LeftTouchpadClick")]
        [InlineData("SteamDeck", "RightTouchpadClick")]
        public void QuadrantWedge_StaysInsideItsOwnFootprint(string family, string role)
        {
            using var m = ControllerModelBase.Create(family, null, false);
            var surface = m.ButtonMap[role][0];
            var b = surface.Bounds;
            var centre = new Vector3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);

            var method = typeof(ControllerModelView).GetMethod("BuildClippedQuadrantMesh",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            foreach (var (isX, isNeg) in new[] { (false, true), (false, false), (true, true), (true, false) })
            {
                var mesh = (MeshGeometry3D)method.Invoke(null, new object[] { surface, centre, isX, isNeg });
                Assert.NotEmpty(mesh.Positions);
                foreach (Point3D p in mesh.Positions)
                {
                    Assert.True(p.X >= b.X - 0.001 && p.X <= b.X + b.SizeX + 0.001,
                        $"{family} {role}: wedge reaches X={p.X:F2}, outside its surface [{b.X:F2},{b.X + b.SizeX:F2}]");
                    Assert.True(p.Z >= b.Z - 0.001 && p.Z <= b.Z + b.SizeZ + 0.001,
                        $"{family} {role}: wedge reaches Z={p.Z:F2}, outside its surface [{b.Z:F2},{b.Z + b.SizeZ:F2}]");
                }
            }
        }

        /// <summary>The wedge stops before the surface rolls away under it.
        /// A control's rim curves off, and a wedge lifted over that roll-off
        /// floats free of it: past 0.9 of the 2015 stick cap's radius the
        /// mean surface normal has tilted to -0.57, and a lip standing
        /// 0.8 mm proud there reads as the highlight spilling off the stick
        /// onto the doughnut whose inner edge is 0.4 mm away.</summary>
        [Theory]
        [InlineData("SteamController", "LeftTouchpadClick")]
        [InlineData("SteamController", "RightTouchpadClick")]
        [InlineData("SteamController2", "LeftTouchpadClick")]
        public void QuadrantWedge_StopsShortOfTheRim(string family, string role)
        {
            using var m = ControllerModelBase.Create(family, null, false);
            CheckInset(m.ButtonMap[role][0]);
        }

        [Theory]
        [InlineData("SteamController")]
        [InlineData("SteamController2")]
        [InlineData("SteamDeck")]
        [InlineData("Xbox360")]
        public void StickCapWedge_StopsShortOfTheRim(string family)
        {
            using var m = ControllerModelBase.Create(family, null, false);
            CheckInset(m.LeftThumbRing);
        }

        /// <summary>THE PROPERTY that makes a direction read as a direction:
        /// the wedge is an ARC OF A RING and surrounds the stick, never a
        /// filled slice bearing in on its middle.
        ///
        /// <para>Three of these caps are hollow in the mesh and give it for
        /// free: the Xbox 360's to 0.49 of its radius, the Steam Deck's to
        /// 0.74, the 2026 Steam Controller's to 0.66. Two are solid discs,
        /// the 2015 Steam Controller's and the Switch 2 Pro's, and those are
        /// given the same hole so every pad's wedge reads alike.</para></summary>
        [Theory]
        [InlineData("Xbox360")]
        [InlineData("Switch2Pro")]
        [InlineData("SteamDeck")]
        [InlineData("SteamController")]
        [InlineData("SteamController2")]
        public void StickCapWedge_IsAnArcNotAPieSlice(string family)
        {
            using var m = ControllerModelBase.Create(family, null, false);
            var cap = m.LeftThumbRing;
            Assert.NotNull(cap);
            var b = cap.Bounds;
            var centre = new Vector3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            double R = Math.Max(b.SizeX, b.SizeZ) / 2.0;

            var method = typeof(ControllerModelView).GetMethod("BuildClippedQuadrantMesh",
                BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var (isX, isNeg) in new[] { (false, true), (false, false), (true, true), (true, false) })
            {
                var mesh = (MeshGeometry3D)method.Invoke(null, new object[] { cap, centre, isX, isNeg });
                Assert.NotEmpty(mesh.Positions);
                double nearest = mesh.Positions.Min(p =>
                    Math.Sqrt((p.X - centre.X) * (p.X - centre.X) + (p.Z - centre.Z) * (p.Z - centre.Z)));
                Assert.True(nearest >= 0.4 * R,
                    $"{family}: the wedge reaches r={nearest:F2} on a {R:F2} mm cap, so it is a filled slice, not a ring");
            }
        }

        private static void CheckInset(Model3DGroup surface)
        {
            var b = surface.Bounds;
            var centre = new Vector3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            double limit = 0.85 * Math.Max(b.SizeX, b.SizeZ) / 2.0;

            var method = typeof(ControllerModelView).GetMethod("BuildClippedQuadrantMesh",
                BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var (isX, isNeg) in new[] { (false, true), (false, false), (true, true), (true, false) })
            {
                var mesh = (MeshGeometry3D)method.Invoke(null, new object[] { surface, centre, isX, isNeg });
                Assert.NotEmpty(mesh.Positions);
                foreach (Point3D p in mesh.Positions)
                {
                    double r = Math.Sqrt((p.X - centre.X) * (p.X - centre.X) + (p.Z - centre.Z) * (p.Z - centre.Z));
                    // The disc is clipped as a 24-gon, so its corners sit a
                    // little past the inradius. 1% covers that exactly.
                    Assert.True(r <= limit * 1.01,
                        $"wedge reaches r={r:F2} on a surface whose wedge must stop at {limit:F2}");
                }
            }
        }

        /// <summary>And it is cut from the surface's VISIBLE face, so a wedge
        /// never draws over the buried half of a control.</summary>
        [Fact]
        public void QuadrantWedge_ComesOnlyFromTheVisibleFace()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            var pad = m.ButtonMap["LeftTouchpadClick"][0];
            var b = pad.Bounds;
            var centre = new Vector3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);

            var method = typeof(ControllerModelView).GetMethod("BuildClippedQuadrantMesh",
                BindingFlags.NonPublic | BindingFlags.Static);
            var mesh = (MeshGeometry3D)method.Invoke(null, new object[] { pad, centre, false, true });

            // The pad runs 17.6 mm deep, most of it skirt inside the case.
            // Every wedge vertex belongs to the face on top of it.
            double deepest = mesh.Positions.Max(p => p.Y);
            Assert.True(deepest < b.Y + b.SizeY * 0.5,
                $"wedge reaches {deepest:F2} mm into a pad spanning [{b.Y:F2},{b.Y + b.SizeY:F2}]: that is its buried skirt");
        }

        /// <summary>Which quadrant a point falls in: 0 up, 1 down, 2 left,
        /// 3 right, in the model's X across / +Z up frame.</summary>
        [Theory]
        [InlineData(0.0, 5.0, 0)]
        [InlineData(0.0, -5.0, 1)]
        [InlineData(-5.0, 0.0, 2)]
        [InlineData(5.0, 0.0, 3)]
        [InlineData(4.0, 3.0, 3)]
        [InlineData(-3.0, 4.0, 0)]
        public void QuadrantSlot_SplitsTheFaceIntoFour(double dx, double dz, int want)
        {
            var method = typeof(ControllerModelView).GetMethod("QuadrantSlot",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var center = new Vector3D(10.0, 0.0, -20.0);
            var hit = new Point3D(center.X + dx, 0, center.Z + dz);
            Assert.Equal(want, (int)method.Invoke(null, new object[] { hit, center }));
        }

        /// <summary>Every Valve model in particular: the roles the owner
        /// reported dead, named one by one so a regression says which.</summary>
        [Theory]
        [InlineData("SteamController2", "ButtonA", "ButtonQuickAccess", "Paddle1", "Paddle4", "LeftTouchpadClick", "RightTouchpadClick")]
        [InlineData("SteamDeck", "ButtonA", "ButtonQuickAccess", "Paddle1", "Paddle4", "LeftTouchpadClick", "RightTouchpadClick")]
        [InlineData("SteamController", "ButtonA", "LeftGrip", "RightGrip", "LeftTouchpadClick", "RightTouchpadClick", "ButtonStart")]
        public void ValveRolesGlow(string family, params string[] roles)
        {
            using var m = ControllerModelBase.Create(family, null, false);
            foreach (var role in roles)
            {
                Assert.True(m.ButtonMap.TryGetValue(role, out var groups), $"{family}: {role} is not on the model");
                foreach (var g in groups)
                    Assert.True(m.HighlightMaterials.ContainsKey(g), $"{family}: {role} has no highlight material");
            }
        }

        /// <summary>A hand-tuned highlight is never overwritten by the
        /// backstop: the DualSense's clear plastic keeps the material its
        /// own constructor chose.</summary>
        [Fact]
        public void BackstopKeepsHandTunedHighlights()
        {
            using var m = ControllerModelBase.Create("DualSense", "White", false);
            var before = m.HighlightMaterials.ToDictionary(kv => kv.Key, kv => kv.Value);
            m.EnsureHighlightMaterials();
            foreach (var kv in before)
                Assert.Same(kv.Value, m.HighlightMaterials[kv.Key]);
        }

        private static Model3DGroup CapAt(double cx, double cz, double r)
        {
            var mesh = new MeshGeometry3D();
            mesh.Positions.Add(new Point3D(cx - r, 0, cz - r));
            mesh.Positions.Add(new Point3D(cx + r, 0, cz - r));
            mesh.Positions.Add(new Point3D(cx + r, 0, cz + r));
            mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(2);
            var g = new Model3DGroup();
            g.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(Brushes.Gray)));
            return g;
        }

        /// <summary>The ring-less split: the middle of the head is the
        /// click, the outer half is a direction.</summary>
        [Theory]
        [InlineData(0.0, 0.0, false)]    // dead centre stays the click
        [InlineData(0.3, 0.0, false)]    // inside half the radius
        [InlineData(0.9, 0.0, true)]     // out at the edge
        [InlineData(0.0, -0.8, true)]
        [InlineData(0.5, 0.5, true)]     // diagonal clears the radius
        public void OuterCapHit_SplitsClickFromDirection(double nx, double nz, bool expectDirection)
        {
            var method = typeof(ControllerModelView).GetMethod("IsOuterCapHit",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            const double cx = -18.45, cz = -14.15, r = 9.0;
            var cap = CapAt(cx, cz, r);
            var hit = new Point3D(cx + nx * r, 0, cz + nz * r);
            bool got = (bool)method.Invoke(null, new object[] { cap, hit });
            Assert.Equal(expectDirection, got);
        }
    }
}
