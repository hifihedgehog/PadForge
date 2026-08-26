using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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

        /// <summary>A stick the user can click is a stick whose DIRECTION is
        /// reachable too: through the ring solid, or through the cap the
        /// ring-less path falls back to. The 2015 Steam Controller has no
        /// ring (its bezel is a hole in the case), which left its only
        /// stick with no axis target at all.</summary>
        [Theory]
        [MemberData(nameof(Families))]
        public void EveryStickHasADirectionTarget(string family, string appearance, bool extra)
        {
            using var m = ControllerModelBase.Create(family, appearance, extra);
            if (m.ButtonMap.ContainsKey("LeftThumbButton"))
                Assert.True(m.LeftThumbRing != null || m.LeftThumb != null,
                    $"{family}: the left stick clicks but has no head to take a direction from");
            if (m.ButtonMap.ContainsKey("RightThumbButton"))
                Assert.True(m.RightThumbRing != null || m.RightThumb != null,
                    $"{family}: the right stick clicks but has no head to take a direction from");
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
