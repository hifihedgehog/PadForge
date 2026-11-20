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

        /// <summary>Two models built at the same moment both find no file
        /// kept and both read it. The copy kept first is the one every model
        /// shares. Kept over it, the model that had read first held meshes
        /// no later model got. In a full run on 2026-11-19 another class
        /// built this model while the test above built its first, and its
        /// two models shared 9 meshes where 10 were asked for. Here four
        /// threads build the model at once with nothing kept, and all four
        /// end with the same meshes and the same art.</summary>
        [Fact]
        public void ModelsBuiltAtTheSameMomentShareOneCopyOfEachFile()
        {
            const int builders = 4;
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            foreach (var (table, gate) in new[] { ("s_parsed", "s_parsedGate"), ("s_textures", "s_texturesGate") })
            {
                var kept = (System.Collections.IDictionary)typeof(ControllerModelBase).GetField(table, flags).GetValue(null);
                lock (typeof(ControllerModelBase).GetField(gate, flags).GetValue(null)) kept.Clear();
            }

            using var start = new System.Threading.Barrier(builders);
            var meshes = new HashSet<object>[builders];
            var art = new HashSet<object>[builders];
            var faults = new Exception[builders];
            var threads = Enumerable.Range(0, builders).Select(i => new System.Threading.Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    // A model belongs to the thread that built it, so it is
                    // read and let go of there. Its meshes and its art are
                    // frozen and outlive it in these sets.
                    using var model = ControllerModelBase.Create("XboxSeries", "Carbon", true);
                    var parts = Parts(model.model3DGroup).ToList();
                    meshes[i] = new HashSet<object>(parts.Select(p => p.Geometry), ReferenceEqualityComparer.Instance);
                    art[i] = new HashSet<object>(parts.SelectMany(p => Brushes(p.Material)).OfType<ImageBrush>(),
                        ReferenceEqualityComparer.Instance);
                }
                catch (Exception ex)
                {
                    faults[i] = ex;
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.All(faults, Assert.Null);
            Assert.True(meshes[0].Count >= 10, $"the model has only {meshes[0].Count} meshes");
            Assert.NotEmpty(art[0]);
            for (int i = 1; i < builders; i++)
            {
                Assert.True(meshes[0].SetEquals(meshes[i]), $"builder {i} kept meshes of its own");
                Assert.True(art[0].SetEquals(art[i]), $"builder {i} kept art of its own");
            }
        }

        /// <summary>The rule both caches keep by: the first copy kept under
        /// a key is the one handed to every caller, a second read of the
        /// same key is dropped for it, and a copy nothing holds any more
        /// gives way to the next one read.</summary>
        [Fact]
        public void TheFirstCopyKeptIsTheOneEveryCallerGets()
        {
            var kept = new Dictionary<string, WeakReference<object>>();
            var gate = new object();
            object first = new object(), second = new object(), other = new object();

            Assert.Same(first, ControllerModelBase.KeepFirst(kept, gate, "Body.obj", first));
            Assert.Same(first, ControllerModelBase.KeepFirst(kept, gate, "Body.obj", second));
            Assert.Same(other, ControllerModelBase.KeepFirst(kept, gate, "Grip.obj", other));

            kept["Gone.obj"] = new WeakReference<object>(null);
            Assert.Same(second, ControllerModelBase.KeepFirst(kept, gate, "Gone.obj", second));
            Assert.Same(second, ControllerModelBase.KeepFirst(kept, gate, "Gone.obj", first));
            GC.KeepAlive(first);
            GC.KeepAlive(other);
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
