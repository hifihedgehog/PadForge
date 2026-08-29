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
        /// Valve molds a D-pad cross into the left cover, naming that solid
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

            // Neither PAD goes through the quadrant-wedge path any more: a
            // bowl 42 mm across cannot carry one, which is why each face is
            // quartered in the mesh instead.
            //
            // The synthesized right stick head does, and belongs there: it
            // is a stick head, the shape the wedge path was written for, and
            // it registers as the right pad's click because pressing this
            // pad IS the right stick button. So it appears in that click's
            // list beside the pad without being the pad.
            foreach (var pad in new[] { m.Touchpad, m.TouchpadRight })
                Assert.False(m.QuadrantMap.ContainsKey(pad));
        }

        /// <summary>Every direction mesh sits where its name says, measured
        /// from its own pad's center.</summary>
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

        /// <summary>THE PROPERTY for a stick: it is continuous. Whatever
        /// the button lights, plus whatever rides the stick, reaches the
        /// cap's underside, so no gap opens in the middle of the stick when
        /// it leans. Model space puts the face along -Y, so the topmost
        /// geometry has the smallest Y.
        ///
        /// <para>This asked for the BUTTON alone until 2026-08-27, which was
        /// wrong for a stick built from three solids. The Steam Deck's stem
        /// is the shaft between its cap and its base, and it rides without
        /// lighting: the button lights the base, the same part of the stick
        /// every other pad here lights.</para></summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void TheStickIsContinuousUpToItsCap(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);
            void Check(string button, Model3DGroup ring)
            {
                if (ring == null || !m.ButtonMap.TryGetValue(button, out var groups)) return;
                double capUnderside = ring.Bounds.Y + ring.Bounds.SizeY;
                double top = groups.Min(g => g.Bounds.Y);
                if (m.StickRiders.TryGetValue(ring, out var riders) && riders.Count > 0)
                    top = Math.Min(top, riders.Min(g => g.Bounds.Y));
                Assert.True(top <= capUnderside + 0.5,
                    $"{family}: {button} and its riders stop {top - capUnderside:F2} mm short of "
                    + "the cap, so the stick has a gap in it");
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
            var center = new Vector3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            double R = Math.Max(b.SizeX, b.SizeZ) / 2.0;

            var method = typeof(ControllerModelView).GetMethod("BuildClippedQuadrantMesh",
                BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var (isX, isNeg) in new[] { (false, true), (false, false), (true, true), (true, false) })
            {
                var mesh = (MeshGeometry3D)method.Invoke(null, new object[] { cap, center, isX, isNeg });
                Assert.NotEmpty(mesh.Positions);
                double nearest = mesh.Positions.Min(p =>
                    Math.Sqrt((p.X - center.X) * (p.X - center.X) + (p.Z - center.Z) * (p.Z - center.Z)));
                Assert.True(nearest >= 0.4 * R,
                    $"{family}: the wedge reaches r={nearest:F2} on a {R:F2} mm cap, so it is a filled slice, not a ring");
            }
        }

        /// <summary>A labeled key registers as the KEY, with its label
        /// riding it, and the label keeps its own color so it can be read.
        ///
        /// <para>The Steam Deck had this inside out on both sides at once.
        /// Its Quick Access control was the 9.32 mm ThreeDots glyph while the
        /// 16.25 mm key under it was scenery, so hovering lit three dots and
        /// nothing else. Its Steam key was the reverse: the key registered
        /// and the wordmark on it was scenery, so everything lit EXCEPT the
        /// text.</para>
        ///
        /// <para>The color half is its own bug. A rider joins its host's
        /// ButtonMap list, so a paint pass that runs AFTER the riders are
        /// added repaints every label in its own cap's color, and the same
        /// dictionary entry proves it: both groups end up sharing one
        /// material instance.</para></summary>
        [Theory]
        [InlineData("SteamDeck", "ButtonGuide")]
        [InlineData("SteamDeck", "ButtonQuickAccess")]
        [InlineData("SteamDeck", "ButtonA")]
        [InlineData("SteamDeck", "ButtonB")]
        [InlineData("SteamDeck", "ButtonX")]
        [InlineData("SteamDeck", "ButtonY")]
        [InlineData("SteamDeck", "ButtonBack")]
        [InlineData("SteamDeck", "ButtonStart")]
        [InlineData("SteamController", "ButtonA")]
        [InlineData("SteamController", "ButtonGuide")]
        public void LabelRidesItsKeyAndKeepsItsColor(string family, string role)
        {
            using var m = ControllerModelBase.Create(family, null, false);
            var groups = m.ButtonMap[role];
            Assert.True(groups.Count >= 2, $"{family}: {role} carries no label rider");

            var key = groups[0].Bounds;
            var label = groups[1].Bounds;
            Assert.True(label.SizeX < key.SizeX,
                $"{family}: {role}'s label is {label.SizeX:F2} mm across against a {key.SizeX:F2} mm key, "
                + "so the label is registered as the control");
            Assert.True(label.X >= key.X - 0.05 && label.X + label.SizeX <= key.X + key.SizeX + 0.05,
                $"{family}: {role}'s label does not sit within its key");

            Assert.True(m.DefaultMaterials.TryGetValue(groups[0], out var keyMat));
            Assert.True(m.DefaultMaterials.TryGetValue(groups[1], out var labelMat));
            Assert.False(ReferenceEquals(keyMat, labelMat),
                $"{family}: {role}'s label was repainted in its key's own material and cannot be read");
        }

        /// <summary>A stick that clicks is a stick that MOVES, and a trigger
        /// that maps is a trigger that pulls. Both need a pivot and a
        /// non-zero travel, and a zero angle rotates nothing at all.
        ///
        /// <para>The Steam Deck set neither. It was the only model in the
        /// tree with no rotation points, so its sticks and triggers were
        /// frozen while every other pad animated.</para></summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void SticksAndTriggersCanActuallyMove(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);

            if (m.ButtonMap.ContainsKey("LeftThumbButton") || m.LeftThumbRing != null)
            {
                Assert.True(m.JoystickMaxAngleDeg > 0,
                    $"{family}: the sticks have no travel, so nothing deflects");
                Assert.NotEqual(default, m.JoystickRotationPointCenterLeftMillimeter);
            }
            if (m.LeftShoulderTrigger != null)
            {
                Assert.True(m.TriggerMaxAngleDeg > 0,
                    $"{family}: the triggers have no travel, so nothing pulls");
                Assert.NotEqual(default, m.ShoulderTriggerRotationPointCenterLeftMillimeter);
                Assert.NotEqual(default, m.ShoulderTriggerRotationPointCenterRightMillimeter);
            }
        }

        /// <summary>A stick pivot sits at its click mesh's own center in X
        /// and Z, which is what keeps a deflected stick in its recess rather
        /// than swinging about some other point on the pad.</summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void StickPivotSitsOnItsOwnStick(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);
            void Check(string button, Vector3D pivot)
            {
                if (!m.ButtonMap.TryGetValue(button, out var groups)) return;
                var b = groups[0].Bounds;
                double cx = b.X + b.SizeX / 2, cz = b.Z + b.SizeZ / 2;
                Assert.True(Math.Abs(pivot.X - cx) < 3.5,
                    $"{family}: {button}'s pivot is {Math.Abs(pivot.X - cx):F1} mm off its own stick in X");
                Assert.True(Math.Abs(pivot.Z - cz) < 3.5,
                    $"{family}: {button}'s pivot is {Math.Abs(pivot.Z - cz):F1} mm off its own stick in Z");
            }
            Check("LeftThumbButton", m.JoystickRotationPointCenterLeftMillimeter);
            if (m.RightThumbRing != null)
                Check("RightThumbButton", m.JoystickRotationPointCenterRightMillimeter);
        }

        /// <summary>THE WHOLE STICK moves. Every piece of geometry sitting
        /// on a stick has to be reachable from that stick's ring plus its
        /// button's groups, because that is the exact set the view tilts and
        /// grades. A piece registered anywhere else stands still while the
        /// rest of the stick leans.
        ///
        /// <para>The Steam Deck's capacitive barrel was scenery, so its
        /// stick moved as a cap and a collar with a frozen middle.</para></summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void TheWholeStickMovesWithIt(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);
            void Check(string button, Model3DGroup ring, Model3DGroup thumb, double pivotY)
            {
                if (ring == null || !m.ButtonMap.TryGetValue(button, out var groups)) return;

                // The same set ControllerModelView tilts: the cap, the
                // thumb, and everything the button lights. They are not one
                // list, because a base the cap hides tilts without lighting.
                var moving = new HashSet<Model3DGroup>(groups) { ring };
                if (thumb != null) moving.Add(thumb);
                if (m.StickRiders.TryGetValue(ring, out var riders))
                    foreach (var r in riders) moving.Add(r);
                var rb = ring.Bounds;
                double cx = rb.X + rb.SizeX / 2, cz = rb.Z + rb.SizeZ / 2;

                // A part OF the stick is CONCENTRIC with it, which is what
                // separates a barrel from a paddle that merely sits nearby:
                // the DualSense Edge's left paddle is 9.8 mm off its stick's
                // axis, a whole ring radius, while the Steam Deck's barrel is
                // on it to a tenth of a millimetre.
                double onAxis = rb.SizeX * 0.15;

                foreach (var child in m.model3DGroup.Children)
                {
                    if (child is not Model3DGroup g || moving.Contains(g)) continue;
                    var b = g.Bounds;
                    if (b.IsEmpty) continue;
                    if (Math.Max(b.SizeX, b.SizeZ) > rb.SizeX * 1.6) continue;
                    double gx = b.X + b.SizeX / 2, gz = b.Z + b.SizeZ / 2;
                    if (Math.Abs(gx - cx) > onAxis || Math.Abs(gz - cz) > onAxis) continue;
                    // Nothing that reaches past the stick's own pivot is
                    // part of the stick: the pivot is inside the case by
                    // construction. -Y is out of the face, so deeper is a
                    // larger Y. This is what the Steam Deck's well liner is,
                    // and its base bulb, which stops well short, is not.
                    if (b.Y + b.SizeY > pivotY) continue;

                    Assert.Fail($"{family}: a {b.SizeX:F1} mm part sits on {button}'s own axis but is "
                        + "not in the moving set, so it stays put while the stick tilts");
                }
            }
            Check("LeftThumbButton", m.LeftThumbRing, m.LeftThumb,
                m.JoystickRotationPointCenterLeftMillimeter.Y);
            Check("RightThumbButton", m.RightThumbRing, m.RightThumb,
                m.JoystickRotationPointCenterRightMillimeter.Y);
        }

        /// <summary>A stick stands in an opening, not on a plate.
        ///
        /// <para>The Steam Deck's shell shipped with both wells capped by a
        /// flat disc of plastic at the plane the base bulb's back sits on,
        /// so the stick stood on a solid surface and appeared to slide
        /// across it when it leaned. Vertex radius does not find that disc:
        /// the triangle over the left stick's axis spans 17 mm and has every
        /// corner outside the well, which is why the shell measured as open
        /// while rendering closed. Cast a ray instead.</para></summary>
        [Fact]
        public void TheSteamDeckStandsItsSticksInOpenWells()
        {
            using var m = ControllerModelBase.Create("SteamDeck", null, false);
            foreach (var (ring, side) in new[] { (m.LeftThumbRing, "left"), (m.RightThumbRing, "right") })
            {
                Assert.NotNull(ring);
                var rb = ring.Bounds;
                double ax = rb.X + rb.SizeX / 2, az = rb.Z + rb.SizeZ / 2;
                double capRear = rb.Y + rb.SizeY;

                double nearest = double.MaxValue;
                foreach (var child in m.MainBody.Children)
                {
                    if (child is not GeometryModel3D geo || geo.Geometry is not MeshGeometry3D mesh)
                        continue;
                    var p = mesh.Positions;
                    var idx = mesh.TriangleIndices;
                    for (int i = 0; i + 2 < idx.Count; i += 3)
                    {
                        var a = p[idx[i]]; var b = p[idx[i + 1]]; var c = p[idx[i + 2]];
                        double det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                        if (Math.Abs(det) < 1e-12) continue;
                        double w0 = ((b.Z - c.Z) * (ax - c.X) + (c.X - b.X) * (az - c.Z)) / det;
                        double w1 = ((c.Z - a.Z) * (ax - c.X) + (a.X - c.X) * (az - c.Z)) / det;
                        double w2 = 1 - w0 - w1;
                        if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
                        double y = w0 * a.Y + w1 * b.Y + w2 * c.Y;
                        if (y < nearest) nearest = y;
                    }
                }

                // The back wall is 22 mm in. Anything nearer than 10 mm
                // behind the cap is a floor the stick would stand on.
                Assert.True(nearest > capRear + 10.0,
                    $"the {side} stick well is capped {nearest - capRear:F1} mm behind the cap, "
                    + "so the stick stands on a plate instead of in an opening");
            }
        }

        /// <summary>The Steam Deck's stick button lights its BASE, the
        /// same part of the stick every other pad here lights.
        ///
        /// <para>The family is consistent: the DualSense, the DS4, the Xbox
        /// Series, the Xbox 360 and the Switch 2 Pro all light a base wider
        /// than their cap and leave the cap dark, so the glow reads as a
        /// collar around the stick. The Deck's three solids make it possible
        /// to get this exactly inside out, and an earlier pass did: it lit
        /// the 12.24 mm stem, whose face IS the top of the stick, and left
        /// the 15.22 mm base dark. Rendered through the app's own viewport
        /// the difference is unmistakable, a lit stick face against every
        /// other pad's lit collar.</para></summary>
        [Fact]
        public void TheSteamDeckLightsItsStickBaseAndNotItsFace()
        {
            using var m = ControllerModelBase.Create("SteamDeck", null, false);
            foreach (var (ring, button, side) in new[]
                     { (m.LeftThumbRing, "LeftThumbButton", "left"),
                       (m.RightThumbRing, "RightThumbButton", "right") })
            {
                var lit = m.ButtonMap[button];
                Assert.Single(lit);

                // The base is the widest solid below the cap. The stem is
                // the narrow one, and it rides without lighting.
                double capWidth = ring.Bounds.SizeX;
                Assert.True(lit[0].Bounds.SizeX > capWidth * 0.9,
                    $"the {side} stick button lights a {lit[0].Bounds.SizeX:F1} mm part under a "
                    + $"{capWidth:F1} mm cap, which is the stem, not the base");
                Assert.True(m.StickRiders.TryGetValue(ring, out var riders) && riders.Count == 1,
                    $"the {side} stem must ride the stick so it leans with the cap and the base");
                Assert.DoesNotContain(riders[0], lit);
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
        [InlineData(0.0, 0.0, false)]    // dead center stays the click
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
