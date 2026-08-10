using System;
using System.Linq;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The DualSense Edge's four extra buttons in the 3D view. Their
    /// geometry was always in the purchased mesh, welded into larger
    /// parts: the back-button levers were shells inside MainBody, the
    /// ribbed Fn buttons shells inside the stick housings, and the Fn
    /// labels shells inside the static decal overlay. They are re-filed
    /// into their own part files and registered under the PadSetting
    /// names, so hover, click-to-record and press-highlight all work.
    ///
    /// Gating is by asset presence: only the Edge folder carries the
    /// part files, so the plain colorways cannot grow controls they
    /// have no wire for (the same mechanism as the Edge stick
    /// housings).
    /// </summary>
    public class DualSenseEdge3DExtrasTests
    {
        private static readonly string[] PartFiles =
        {
            "LeftBackButton.obj", "RightBackButton.obj",
            "LeftFnButton.obj", "RightFnButton.obj",
            "Decal-Fn-Left.obj", "Decal-Fn-Right.obj",
        };

        private static string[] Resources()
            => typeof(ControllerModelBase).Assembly.GetManifestResourceNames();

        [Fact]
        public void EdgeAssetSet_CarriesAllSixPartFiles()
        {
            var names = Resources();
            foreach (var f in PartFiles)
                Assert.Single(names, n => n.EndsWith(
                    $".DualSenseEdge.Edge.{f}", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>No plain colorway may carry them: TryLoadModel gates
        /// the registration on these files existing per-family, and a
        /// stray copy in a DualSense folder would hand a plain pad four
        /// controls it has no wire for.</summary>
        [Fact]
        public void PlainColorways_CarryNone()
        {
            var names = Resources();
            foreach (var colorway in ControllerModelDualSense.AppearanceIds)
                foreach (var f in PartFiles)
                    Assert.DoesNotContain(names, n => n.EndsWith(
                        $".DualSense.{colorway}.{f}", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void EdgeModel_RegistersTheFourExtras()
        {
            using var model = new ControllerModelDualSenseEdge();
            foreach (var target in new[]
                     { "LeftPaddle", "RightPaddle", "LeftFunction", "RightFunction" })
            {
                Assert.True(model.ButtonMap.ContainsKey(target),
                    $"{target} is not registered on the Edge model");
                var group = Assert.Single(model.ButtonMap[target]);
                Assert.Equal(target, model.ClickMap[group]);
                Assert.Contains(group, model.model3DGroup.Children.OfType<object>());
            }

            // The mesh's own frame pins which side is which: the body
            // atlas labels the x<0 lever "L B", and the left stick sits
            // at x=-25.6. A swapped pair would light the wrong button.
            static double CenterX(System.Windows.Media.Media3D.Model3DGroup g)
                => g.Bounds.X + g.Bounds.SizeX / 2;
            Assert.True(CenterX(model.ButtonMap["LeftPaddle"][0]) < 0);
            Assert.True(CenterX(model.ButtonMap["RightPaddle"][0]) > 0);
            Assert.True(CenterX(model.ButtonMap["LeftFunction"][0]) < 0);
            Assert.True(CenterX(model.ButtonMap["RightFunction"][0]) > 0);

            // The Fn label rides its button (a static-overlay label
            // would stay grey while the button lights), so the Fn
            // groups carry two geometries: cap + rider decal.
            Assert.Equal(2, model.ButtonMap["LeftFunction"][0].Children.Count);
            Assert.Equal(2, model.ButtonMap["RightFunction"][0].Children.Count);
        }

        [Fact]
        public void PlainModel_DoesNotGrowThem()
        {
            using var model = new ControllerModelDualSense("White");
            foreach (var target in new[]
                     { "LeftPaddle", "RightPaddle", "LeftFunction", "RightFunction" })
                Assert.False(model.ButtonMap.ContainsKey(target),
                    $"plain DualSense grew a {target} it has no wire for");
        }
    }
}
