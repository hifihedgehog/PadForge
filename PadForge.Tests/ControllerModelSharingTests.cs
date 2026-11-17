using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Models built from the same files share what never changes and own
    /// what does.
    ///
    /// <para>The controller's page and a Home card's still each build the
    /// pad's model at launch, and each used to read every mesh file and
    /// decode every atlas again: some 250 MB of parsed meshes and 90 MB of
    /// image buffers a model. The collections that followed held the poll
    /// loop up to 40 ms at a time in its first half minute (measured
    /// 2026-11-16). A file is now read once and kept, frozen, while any model
    /// built from it lives. A model still paints its own parts and moves its
    /// own groups, so those stay its own.</para>
    /// </summary>
    public class ControllerModelSharingTests
    {
        [Fact]
        public void TwoModelsShareTheirMeshesAndOwnTheirParts()
        {
            using var first = ControllerModelBase.Create("XboxSeries", "Carbon", true);
            using var second = ControllerModelBase.Create("XboxSeries", "Carbon", true);
            var a = Parts(first.model3DGroup).ToList();
            var b = Parts(second.model3DGroup).ToList();

            var meshesOfB = new HashSet<Geometry3D>(b.Select(p => p.Geometry), ReferenceEqualityComparer.Instance);
            var shared = a.Select(p => p.Geometry).Where(meshesOfB.Contains).Distinct(ReferenceEqualityComparer.Instance).ToList();
            Assert.True(shared.Count >= 10, $"only {shared.Count} meshes are shared, so the second model read its files again");
            Assert.All(shared, mesh => Assert.True(((Geometry3D)mesh).IsFrozen, "a shared mesh must be frozen"));

            var partsOfB = new HashSet<GeometryModel3D>(b, ReferenceEqualityComparer.Instance);
            Assert.DoesNotContain(a, partsOfB.Contains);
            Assert.All(a, part => Assert.False(part.IsFrozen, "a model paints its parts, so they cannot be frozen"));

            var groupsOfB = new HashSet<Model3DGroup>(Groups(second.model3DGroup), ReferenceEqualityComparer.Instance);
            Assert.DoesNotContain(Groups(first.model3DGroup), groupsOfB.Contains);
        }

        [Fact]
        public void PaintingOneModelLeavesTheOtherAlone()
        {
            using var first = ControllerModelBase.Create("XboxSeries", "Carbon", true);
            using var second = ControllerModelBase.Create("XboxSeries", "Carbon", true);
            var before = Parts(second.model3DGroup).Select(p => p.Material).ToList();

            var paint = new DiffuseMaterial(new SolidColorBrush(Colors.Lime));
            foreach (var part in Parts(first.model3DGroup))
                part.Material = paint;

            Assert.Equal(before, Parts(second.model3DGroup).Select(p => p.Material).ToList());
        }

        [Fact]
        public void TwoModelsShareTheirDecodedArt()
        {
            using var first = ControllerModelBase.Create("XboxSeries", "Carbon", true);
            using var second = ControllerModelBase.Create("XboxSeries", "Carbon", true);
            var artOfB = new HashSet<ImageBrush>(Parts(second.model3DGroup).SelectMany(p => Brushes(p.Material)).OfType<ImageBrush>(),
                ReferenceEqualityComparer.Instance);
            var art = Parts(first.model3DGroup).SelectMany(p => Brushes(p.Material)).OfType<ImageBrush>()
                .Distinct(ReferenceEqualityComparer.Instance).Cast<ImageBrush>().ToList();

            Assert.NotEmpty(art);
            Assert.All(art, brush => Assert.True(artOfB.Contains(brush), "the second model decoded an atlas again"));
            Assert.All(art, brush => Assert.True(brush.IsFrozen));
        }

        /// <summary>An atlas at another opacity is another material: the
        /// opacity is on the brush, and a frozen brush cannot change.</summary>
        [Fact]
        public void OneAtlasAtTwoOpacitiesIsTwoMaterials()
        {
            using var model = ControllerModelBase.Create("XboxSeries", "Carbon", true);
            var load = typeof(ControllerModelBase).GetMethod("TryLoadTexturedMaterial", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(load);
            var whole = (Material)load.Invoke(model, new object[] { "Body.png", 1.0, "XboxSeries.Carbon" });
            var again = (Material)load.Invoke(model, new object[] { "Body.png", 1.0, "XboxSeries.Carbon" });
            var half = (Material)load.Invoke(model, new object[] { "Body.png", 0.5, "XboxSeries.Carbon" });

            Assert.NotNull(whole);
            Assert.Same(whole, again);
            Assert.NotSame(whole, half);
            Assert.Equal(0.5, ((ImageBrush)((DiffuseMaterial)half).Brush).Opacity);
        }

        /// <summary>A model has no finalizer. It held nothing a finalizer
        /// frees, and a finalizer kept every dropped model's whole tree alive
        /// through one more collection, meshes and all, so each collection
        /// after a model was dropped copied it again.</summary>
        [Fact]
        public void NoModelHasAFinalizer()
        {
            var models = typeof(ControllerModelBase).Assembly.GetTypes()
                .Where(t => typeof(ControllerModelBase).IsAssignableFrom(t));
            foreach (var type in models)
                Assert.Null(type.GetMethod("Finalize", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        }

        private static IEnumerable<GeometryModel3D> Parts(Model3D model)
        {
            if (model is GeometryModel3D part)
                yield return part;
            else if (model is Model3DGroup group)
                foreach (var child in group.Children)
                    foreach (var inner in Parts(child))
                        yield return inner;
        }

        private static IEnumerable<Model3DGroup> Groups(Model3D model)
        {
            if (model is not Model3DGroup group) yield break;
            yield return group;
            foreach (var child in group.Children)
                foreach (var inner in Groups(child))
                    yield return inner;
        }

        private static IEnumerable<Brush> Brushes(Material material)
        {
            switch (material)
            {
                case DiffuseMaterial diffuse when diffuse.Brush != null:
                    yield return diffuse.Brush;
                    break;
                case MaterialGroup group:
                    foreach (var child in group.Children)
                        foreach (var brush in Brushes(child))
                            yield return brush;
                    break;
            }
        }
    }
}
