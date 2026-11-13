using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Map Key Lights (#557): the names learned per device model, how a map
    /// takes them, the probe that lights one light ahead of every claim,
    /// and the dialog's walk over the key lights.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public sealed class MapKeyLightsTests : IDisposable
    {
        private const string Product = "lamparray:1234:0001:1:5";
        private static readonly Guid Row = new("00000000-0000-0000-0000-00000000557a");
        private static readonly OutputPath Path = new(OutputFamily.LampArrayUnit, "board");

        public MapKeyLightsTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            LearnedKeyMaps.ResetForTest();
        }

        public void Dispose()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            LearnedKeyMaps.ResetForTest();
        }

        private static LightMap Board(string product = Product) => new(new[]
        {
            new LightInfo(0, LightKind.Key, 0x29, LightKeySource.Device),   // Esc
            new LightInfo(1, LightKind.Key, 0, LightKeySource.None),
            new LightInfo(2, LightKind.Key, 0, LightKeySource.None),
            new LightInfo(3, LightKind.Edge, 0, LightKeySource.None),
            new LightInfo(4, LightKind.Key, 0, LightKeySource.None),
        }, "test") { Product = product };

        // ── The names ──

        [Fact]
        public void ALearnedNameNamesAKeyLight_AndOnlyAKeyLight()
        {
            var map = Board();
            Assert.Same(map, LearnedKeyMaps.Apply(map));

            LearnedKeyMaps.Set(Product, 1, 0x04);
            LearnedKeyMaps.Set(Product, 3, 0x05);
            var named = LearnedKeyMaps.Apply(map);
            Assert.NotSame(map, named);
            Assert.Equal(0x04, named.Lights[1].Usage);
            Assert.Equal(LightKeySource.Learned, named.Lights[1].KeySource);
            // The edge light has no key to name.
            Assert.Equal(LightKind.Edge, named.Lights[3].Kind);
            Assert.Equal(0, named.Lights[3].Usage);
            Assert.Equal(Product, named.Product);
            Assert.Equal(new[] { 1 }, named.IndexesOf(0x04));

            // Another model takes none of them.
            Assert.Equal(0, LearnedKeyMaps.Apply(Board("lamparray:1234:0002:1:5")).Lights[1].Usage);
        }

        [Fact]
        public void NamingAKeyAgainMovesIt_AndForgettingTakesItAway()
        {
            LearnedKeyMaps.Set(Product, 1, 0x04);
            LearnedKeyMaps.Set(Product, 2, 0x04, moveFromOthers: true);
            Assert.Equal(new Dictionary<int, ushort> { [2] = 0x04 }, LearnedKeyMaps.For(Product));
            LearnedKeyMaps.Set(Product, 2, 0);
            Assert.Empty(LearnedKeyMaps.For(Product));
            Assert.Empty(LearnedKeyMaps.SaveRegistry());
        }

        [Fact]
        public void TheNamesSaveAsText_AndLoadBack()
        {
            int changes = 0;
            void Count(object s, EventArgs e) => changes++;
            LearnedKeyMaps.Changed += Count;
            try
            {
                LearnedKeyMaps.Replace(Product, new Dictionary<int, ushort> { [12] = 0x1A, [3] = 0x04, [7] = 0 });
                Assert.Equal(1, changes);
                var saved = LearnedKeyMaps.SaveRegistry();
                Assert.Equal(new[] { (Product, "3=04,12=1A") }, saved);

                LearnedKeyMaps.LoadRegistry(new[] { ("other", "1=05, x=1, 2=, 3=0, 4=ZZ"), (Product, "5=06") });
                Assert.Equal(new Dictionary<int, ushort> { [1] = 0x05 }, LearnedKeyMaps.For("other"));
                Assert.Equal(new Dictionary<int, ushort> { [5] = 0x06 }, LearnedKeyMaps.For(Product));
                Assert.Equal(2, changes);
            }
            finally
            {
                LearnedKeyMaps.Changed -= Count;
            }
        }

        // ── The probe ──

        [Fact]
        public void TheProbeComesBeforeEveryClaim_OnALinkedPathOnly()
        {
            var frame = new KeyFrame(null, null, new Dictionary<int, int> { [2] = 0xFFFFFF }, 0);
            PeripheralOutputs.SetProbe(Path, frame);
            // No row links the path: nothing lights it.
            Assert.False(PeripheralOutputs.TryResolveColor(Path, out _));

            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Row, Lighting = new[] { Path } } }));
            PeripheralOutputs.SetRunning(true);
            PeripheralOutputs.SetLighting(Row, slot: 0, player: 1, 1, 2, 3);
            Assert.True(PeripheralOutputs.TryResolveColor(Path, out int rgb, out var ruler));
            Assert.Equal(0, rgb);
            Assert.Same(frame, ruler.Keys);

            int version = PeripheralOutputs.LightingVersion;
            PeripheralOutputs.SetProbe(Path, new KeyFrame(null, null, new Dictionary<int, int> { [2] = 0xFFFFFF }, 0));
            Assert.Equal(version, PeripheralOutputs.LightingVersion);

            PeripheralOutputs.SetProbe(Path, null);
            Assert.True(PeripheralOutputs.LightingVersion > version);
            Assert.True(PeripheralOutputs.TryResolveColor(Path, out rgb, out ruler));
            Assert.Equal(0x010203, rgb);
            Assert.Null(ruler.Keys);
        }

        // ── The dialog's walk ──

        private MapKeyLightsViewModel Dialog()
        {
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Row, Lighting = new[] { Path } } }));
            PeripheralLightMaps.Publish(Path, Board());
            return new MapKeyLightsViewModel(Path, "Bench");
        }

        private static int Lit()
        {
            Assert.True(PeripheralOutputs.TryResolveColor(Path, out _, out var ruler));
            return Assert.Single(ruler.Keys.Lights).Key;
        }

        /// <summary>The dialog draws the progress line's two numbers in a
        /// face of their own, so the line comes in five parts that make the
        /// sentence, and the parts follow each name learned (#574).</summary>
        [Fact]
        public void TheProgressLineComesInPartsAroundItsNumbers()
        {
            using var vm = Dialog();
            // One of the four key lights has a name, Esc.
            Assert.Equal("1", vm.ProgressFirst);
            Assert.Equal("4", vm.ProgressSecond);
            Assert.Equal(vm.Progress, vm.ProgressLead + vm.ProgressFirst + vm.ProgressMiddle + vm.ProgressSecond + vm.ProgressTail);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.Start();
            vm.Name(0x04);
            Assert.Equal("2", vm.ProgressFirst);
            Assert.Equal(vm.Progress, vm.ProgressLead + vm.ProgressFirst + vm.ProgressMiddle + vm.ProgressSecond + vm.ProgressTail);
            foreach (string part in new[] { nameof(vm.Progress), nameof(vm.ProgressLead), nameof(vm.ProgressFirst), nameof(vm.ProgressMiddle), nameof(vm.ProgressSecond), nameof(vm.ProgressTail) })
                Assert.Contains(part, raised);
        }

        [Fact]
        public void TheWalkLightsEachUnnamedKey_AndAPressNamesIt()
        {
            using var vm = Dialog();
            Assert.True(vm.CanLearn);
            Assert.False(vm.IsListening);

            vm.Start();
            Assert.True(vm.IsListening);
            Assert.Equal(1, Lit());

            vm.Name(0x04);
            Assert.Equal(new Dictionary<int, ushort> { [1] = 0x04 }, LearnedKeyMaps.For(Product));
            Assert.Equal(2, Lit());

            vm.Skip();
            Assert.Equal(4, Lit());
            vm.Back();
            Assert.Equal(2, Lit());

            vm.Name(0x05);
            vm.Name(0x06);
            // Every key light named: the walk ends and the probe with it.
            Assert.False(vm.IsListening);
            Assert.False(PeripheralOutputs.TryResolveColor(Path, out _));
            Assert.Equal(new Dictionary<int, ushort> { [1] = 0x04, [2] = 0x05, [4] = 0x06 }, LearnedKeyMaps.For(Product));
        }

        /// <summary>The picture hands over what is selected, the light lit now,
        /// then what a drag covered. A drag asks about the first key it covered,
        /// and one that covered no key asks about nothing.</summary>
        [Fact]
        public void ADragAsksAboutTheFirstKeyItCovered_AndADragOverNoKeyAsksNothing()
        {
            using var vm = Dialog();
            IKeyLightsSource source = vm;
            source.SetSelection(Array.Empty<int>());
            Assert.False(vm.IsListening);

            vm.Start();
            Assert.Equal(1, Lit());
            source.SetSelection(source.Selection.Concat(new[] { 4 }));
            Assert.Equal(4, Lit());
            source.SetSelection(source.Selection);
            Assert.Equal(4, Lit());
        }

        [Fact]
        public void ClosingTheDialogEndsTheProbe()
        {
            var vm = Dialog();
            vm.Start();
            Assert.True(PeripheralOutputs.TryResolveColor(Path, out _));
            vm.Dispose();
            Assert.False(PeripheralOutputs.TryResolveColor(Path, out _));
        }

        [Fact]
        public void OneScanCodeForTwoKeys_TakesTheOneTheDeviceDoesNotNameElsewhere()
        {
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Row, Lighting = new[] { Path } } }));
            var iso = new LightMap(new[]
            {
                new LightInfo(0, LightKind.Key, 0x31, LightKeySource.Device),
                new LightInfo(1, LightKind.Key, 0, LightKeySource.None),
            }, "test") { Product = Product };
            PeripheralLightMaps.Publish(Path, iso);
            using var vm = new MapKeyLightsViewModel(Path, "Bench");
            Assert.Equal(0x32, vm.Choose(new ushort[] { 0x31, 0x32 }));
            Assert.Equal(0x28, vm.Choose(new ushort[] { 0x28 }));
            Assert.Equal(0, vm.Choose(Array.Empty<ushort>()));
        }

        [Fact]
        public void AMapWithNoModelOrNoKeys_CannotBeLearned()
        {
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Row, Lighting = new[] { Path } } }));
            PeripheralLightMaps.Publish(Path, Board(product: null));
            using (var vm = new MapKeyLightsViewModel(Path, "Bench"))
            {
                Assert.False(vm.CanLearn);
                vm.Start();
                vm.Name(0x04);
                Assert.Empty(LearnedKeyMaps.SaveRegistry());
            }

            PeripheralLightMaps.Publish(Path, new LightMap(new[]
            {
                new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
                new LightInfo(1, LightKind.Zone, 0, LightKeySource.None) { Zone = 2 },
            }, "test") { Product = Product });
            using var zones = new MapKeyLightsViewModel(Path, "Bench");
            Assert.False(zones.CanLearn);
        }
    }
}
