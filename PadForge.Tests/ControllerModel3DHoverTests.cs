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
        /// TrackPadCoverDirectional against the right one's Smooth.
        ///
        /// <para>Each face is quartered in the mesh, so every direction has
        /// its OWN mesh and highlights as itself, the way a D-pad key does
        /// everywhere else in this tree. The left pad's quarters are D-pad
        /// keys; the right pad's carry the right stick's axis directions.
        /// Each pad keeps its middle as its click.</para></summary>
        [Fact]
        public void SteamController2015_LeftPadIsTheDPad_RightPadIsTheStick()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);

            foreach (var key in new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" })
                Assert.True(m.ButtonMap.ContainsKey(key), $"the left pad has no {key} mesh");

            foreach (var axis in new[] { "RightThumbAxisYNeg", "RightThumbAxisY",
                                         "RightThumbAxisXNeg", "RightThumbAxisX" })
                Assert.Contains(axis, m.ClickMap.Values);

            // And each pad still clicks, through the middle left behind.
            Assert.True(m.ButtonMap.ContainsKey("LeftTouchpadClick"));
            Assert.True(m.ButtonMap.ContainsKey("RightTouchpadClick"));

            // Nothing on this pad goes through the quadrant-wedge path any
            // more: a bowl 42 mm across cannot carry one.
            var pads = m.ButtonMap["LeftTouchpadClick"].Concat(m.ButtonMap["RightTouchpadClick"]);
            foreach (var pad in pads)
                Assert.False(m.QuadrantMap.ContainsKey(pad));
        }

        /// <summary>Every direction mesh sits where its name says, measured
        /// from its own pad's centre.</summary>
        [Theory]
        [InlineData("DPadUp", "LeftTouchpadClick", 0, 1)]
        [InlineData("DPadDown", "LeftTouchpadClick", 0, -1)]
        [InlineData("DPadLeft", "LeftTouchpadClick", -1, 0)]
        [InlineData("DPadRight", "LeftTouchpadClick", 1, 0)]
        public void SteamController2015_DirectionMeshesSitWhereTheyClaim(
            string role, string padRole, int wantX, int wantZ)
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            var pad = m.ButtonMap[padRole][0].Bounds;
            double cx = pad.X + pad.SizeX / 2, cz = pad.Z + pad.SizeZ / 2;
            var b = m.ButtonMap[role][0].Bounds;
            double dx = b.X + b.SizeX / 2 - cx, dz = b.Z + b.SizeZ / 2 - cz;

            if (wantX != 0)
            {
                Assert.True(Math.Abs(dx) > Math.Abs(dz), $"{role} is not mainly across the pad");
                Assert.True(Math.Sign(dx) == wantX, $"{role} sits on the wrong side");
            }
            else
            {
                Assert.True(Math.Abs(dz) > Math.Abs(dx), $"{role} is not mainly up or down the pad");
                Assert.True(Math.Sign(dz) == wantZ, $"{role} sits on the wrong side");
            }
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

        /// <summary>THE PROPERTY that makes a direction read as a direction:
        /// the wedge is an ARC OF A RING and surrounds the stick, never a
        /// filled slice bearing in on its middle.
        ///
        /// <para>The mesh is what gives it: a cap ships hollow, and the arc
        /// is what the wedge builder cuts from it. The Xbox 360's is hollow
        /// to 0.49 of its radius, the 2026 Steam Controller's to 0.66, the
        /// Steam Deck's to 0.74, and the 2015 Steam Controller's to 0.55
        /// since its solid knurled dome is split into a ring and a middle
        /// when the mesh is built.</para>
        ///
        /// <para>The Switch 2 Pro is NOT here. Its cap mesh is a solid disc
        /// and its wedge is still a filled slice, a gap in that asset rather
        /// than in this code.</para></summary>
        [Theory]
        [InlineData("Xbox360")]
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
