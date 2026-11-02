using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #557: per-key lighting. The saved key colors per shift layer,
    /// the frame a claim carries, the renderer that turns a unit's lights
    /// into colors, and the claim that carries a frame to the backends.
    /// </summary>
    public class KeyLightingSettingsTests
    {
        [Fact]
        public void TheTextReadsBack_InOneOrder_SoEqualColorsSaveAsEqualText()
        {
            var settings = KeyLightingSettings.Parse(
                "Shift1>bg=off,k06=ffffff,k19=FFFFFF|Base>gedge=0000FF,k1A=FF0000,l12=00FF00,k04=00FF00");
            Assert.Equal("Base>k04=00FF00,k1A=FF0000,gedge=0000FF,l12=00FF00|Shift1>bg=off,k06=FFFFFF,k19=FFFFFF",
                settings.Format());
            Assert.Equal(settings.Format(), KeyLightingSettings.Parse(settings.Format()).Format());
        }

        [Fact]
        public void APartTheReaderDoesNotKnow_IsDropped_AndTheRestKept()
        {
            var settings = KeyLightingSettings.Parse(
                "Base>k1A=FF0000,k00=FFFFFF,kZZ=FFFFFF,k1B=FFF,gbogus=FFFFFF,gzone0=FFFFFF,gzone2=00FF00,l-1=FFFFFF,x=1,bg=nonsense|>k04=FFFFFF|Bad|Mask>k04=FFFFFF");
            // "Mask" is a mask the text can hold, so it is kept: which
            // layers a slot has is the Mappings tab's to say, not the text's.
            Assert.Equal("Base>k1A=FF0000,gzone2=00FF00|Mask>k04=FFFFFF", settings.Format());
        }

        [Fact]
        public void TheBaseLayerHasNothingToInherit()
        {
            var settings = KeyLightingSettings.Parse("Base>inherit,k04=FF0000|Shift1>inherit");
            Assert.False(settings.LayerOf("Base").UsesBase);
            Assert.True(settings.LayerOf("Shift1").UsesBase);
            Assert.Equal("Base>k04=FF0000|Shift1>inherit", settings.Format());
        }

        [Fact]
        public void ALayerWithItsOwnColorsShowsThem_AndOneThatInheritsOrHasNoneShowsBase()
        {
            var settings = KeyLightingSettings.Parse("Base>k04=FF0000|Shift1>bg=off,k06=FFFFFF|Shift2>inherit");

            var shift1 = settings.FrameFor("Shift1");
            Assert.Equal(0xFFFFFF, shift1.Keys[0x06]);
            Assert.False(shift1.Keys.ContainsKey(0x04));
            Assert.Equal(0, shift1.Background);

            foreach (var mask in new[] { "Base", "Shift2", "Shift9", null })
            {
                var frame = settings.FrameFor(mask);
                Assert.Equal(0xFF0000, frame.Keys[0x04]);
                Assert.Null(frame.Background);
            }

            Assert.True(KeyLightingSettings.Empty.FrameFor("Base").IsEmpty);
        }

        [Fact]
        public void ABackgroundColorRidesTheFrame()
        {
            var frame = KeyLightingSettings.Parse("Base>bg=102030").FrameFor("Base");
            Assert.Equal(0x102030, frame.Background);
            Assert.False(frame.IsEmpty);
        }

        [Fact]
        public void ACopyOntoAnotherDeviceKeepsKeysAndGroups_ButNotSingleLights()
        {
            var settings = KeyLightingSettings.Parse("Base>k04=FF0000,glogo=00FF00,l3=0000FF|Shift1>l9=FFFFFF");
            Assert.Equal("Base>k04=FF0000,glogo=00FF00", settings.WithoutLights().Format());
        }

        [Fact]
        public void AnEmptyLayerLeavesTheSettings()
        {
            var settings = KeyLightingSettings.Parse("Base>k04=FF0000|Shift1>k06=FFFFFF");
            var cleared = settings.With(new KeyLightingLayer { Mask = "Shift1" });
            Assert.Equal("Base>k04=FF0000", cleared.Format());
            var replaced = settings.With(new KeyLightingLayer
            {
                Mask = "Shift1",
                Background = KeyBackgroundRule.Color,
                BackgroundRgb = 0x123456,
            });
            Assert.Equal("Base>k04=FF0000|Shift1>bg=123456", replaced.Format());
        }

        [Theory]
        [InlineData("edge", true)]
        [InlineData("logo", true)]
        [InlineData("status", true)]
        [InlineData("other", true)]
        [InlineData("zone1", true)]
        [InlineData("zone12", true)]
        [InlineData("zone0", false)]
        [InlineData("zone", false)]
        [InlineData("zone1a", false)]
        [InlineData("Edge", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyTheGroupNamesTheMapMakesAreValid(string group, bool valid)
            => Assert.Equal(valid, LightGroups.IsValid(group));

        [Fact]
        public void TheDeviceConfigSavesTheTextInItsOneOrder_AndHandsTheDispatcherTheParsedForm()
        {
            var cfg = new DeviceSlotConfig();
            Assert.Equal(string.Empty, cfg.KeyLighting);
            Assert.True(cfg.KeyLightingParsed.IsEmpty);

            var changes = new List<string>();
            cfg.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            cfg.KeyLighting = "Base>k1A=ff0000,k04=00ff00";
            Assert.Equal("Base>k04=00FF00,k1A=FF0000", cfg.KeyLighting);
            Assert.Equal(0xFF0000, cfg.KeyLightingParsed.FrameFor("Base").Keys[0x1A]);
            Assert.Equal(new[] { nameof(DeviceSlotConfig.KeyLighting) }, changes);

            // The same colors in another order change nothing.
            cfg.KeyLighting = "Base>k1A=FF0000,k04=00FF00";
            Assert.Single(changes);

            cfg.KeyLighting = null;
            Assert.Equal(string.Empty, cfg.KeyLighting);
            Assert.True(cfg.KeyLightingParsed.IsEmpty);
        }
    }

    public class LightRendererTests
    {
        private static LightMap Map() => new(new[]
        {
            new LightInfo(0, LightKind.Key, 0x29, LightKeySource.Device),      // Esc
            new LightInfo(1, LightKind.Key, 0x04, LightKeySource.Device),      // A
            new LightInfo(2, LightKind.Key, 0, LightKeySource.None),           // a key no source named
            new LightInfo(3, LightKind.Edge, 0, LightKeySource.None),
            new LightInfo(4, LightKind.Logo, 0, LightKeySource.None),
            new LightInfo(5, LightKind.Zone, 0, LightKeySource.None) { Zone = 2 },
            new LightInfo(6, LightKind.Key, 0x04, LightKeySource.Device),      // a second light under A
        }, "test");

        [Fact]
        public void EveryLightShowsTheBackground_WhenNothingIsSet()
        {
            Assert.Equal(Enumerable.Repeat(0x123456, 7), LightRenderer.Render(Map(), 0x123456, null));
            Assert.Equal(Enumerable.Repeat(0x123456, 7), LightRenderer.Render(Map(), 0x123456, KeyFrame.Empty));
        }

        [Fact]
        public void TheMostSpecificColorWins_LightThenKeyThenGroupThenBackground()
        {
            var frame = new KeyFrame(
                keys: new Dictionary<ushort, int> { [0x04] = 0xAA0000, [0x29] = 0xBB0000 },
                groups: new Dictionary<string, int> { ["edge"] = 0x00CC00, ["zone2"] = 0x00DD00, ["logo"] = 0x00EE00 },
                lights: new Dictionary<int, int> { [0] = 0x0000FF },
                background: null);
            Assert.Equal(new[] { 0x0000FF, 0xAA0000, 0x111111, 0x00CC00, 0x00EE00, 0x00DD00, 0xAA0000 },
                LightRenderer.Render(Map(), 0x111111, frame));
        }

        [Fact]
        public void AFrameBackgroundReplacesTheClaimColor_ForEveryLightWithoutOne()
        {
            var frame = new KeyFrame(new Dictionary<ushort, int> { [0x04] = 0xFFFFFF }, null, null, background: 0);
            Assert.Equal(new[] { 0, 0xFFFFFF, 0, 0, 0, 0, 0xFFFFFF }, LightRenderer.Render(Map(), 0x111111, frame));
        }

        [Fact]
        public void TheMapCountsItsKeysZonesAndUnnamedKeys()
        {
            var map = Map();
            Assert.Equal(2, map.KeyCount);
            Assert.Equal(1, map.UnnamedKeyCount);
            Assert.Equal(2, map.ZoneCount);
            Assert.Equal(new[] { 1, 6 }, map.IndexesOf(0x04));
            Assert.Empty(map.IndexesOf(0x05));
            Assert.True(map.PerLight);
            Assert.True(map.HasKeys);
            Assert.False(map.Positioned);
            Assert.Equal("zone2", map.Lights[5].Group);
            Assert.Null(map.Lights[0].Group);
        }

        [Fact]
        public void TwoFramesWithTheSameColorsAreEqual_WhateverOrderTheyWereBuiltIn()
        {
            var a = new KeyFrame(new Dictionary<ushort, int> { [4] = 1, [5] = 2 }, null, null, null);
            var b = new KeyFrame(new Dictionary<ushort, int> { [5] = 2, [4] = 1 }, null, null, null);
            var c = new KeyFrame(new Dictionary<ushort, int> { [5] = 2, [4] = 3 }, null, null, null);
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, c);
            Assert.True(KeyFrame.Same(null, KeyFrame.Empty));
            Assert.False(KeyFrame.Same(null, a));
            Assert.False(KeyFrame.Same(a, new KeyFrame(a.Keys, null, null, background: 0)));
        }
    }

    [Collection("PeripheralOutputStatics")]
    public class KeyFrameClaimTests : IDisposable
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000005a1");
        private static readonly Guid B = new("00000000-0000-0000-0000-0000000005b2");

        public KeyFrameClaimTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralOutputs.SetRunning(true);
        }

        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static KeyFrame Keys(int rgb) => new(new Dictionary<ushort, int> { [0x04] = rgb }, null, null, null);

        [Fact]
        public void TheRulingClaimCarriesItsKeyFrameToThePath()
        {
            var path = new OutputPath(OutputFamily.LampArrayUnit, "kb");
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = A, Lighting = new[] { path } },
                new DeviceLinks { Device = B, Lighting = new[] { path } },
            }));
            PeripheralOutputs.SetLighting(A, slot: 0, player: 2, 1, 2, 3, keys: Keys(0xFF0000));
            PeripheralOutputs.SetLighting(B, slot: 1, player: 1, 4, 5, 6, keys: Keys(0x00FF00));
            Assert.True(PeripheralOutputs.TryResolveColor(path, out int rgb, out var ruler));
            Assert.Equal(B, ruler.Device);
            Assert.Equal(0x040506, rgb);
            Assert.Equal(0x00FF00, ruler.Keys.Keys[0x04]);
        }

        [Fact]
        public void ANewFrameAloneWakesTheBackends_AndAnEqualOneDoesNot()
        {
            PeripheralOutputs.SetLighting(A, 0, 1, 1, 2, 3, keys: Keys(0xFF0000));
            int version = PeripheralOutputs.LightingVersion;
            PeripheralOutputs.SetLighting(A, 0, 1, 1, 2, 3, keys: Keys(0xFF0000));
            Assert.Equal(version, PeripheralOutputs.LightingVersion);
            PeripheralOutputs.SetLighting(A, 0, 1, 1, 2, 3, keys: Keys(0x0000FF));
            Assert.True(PeripheralOutputs.LightingVersion > version);

            // An empty frame is no frame: going from none to empty is no change.
            PeripheralOutputs.SetLighting(B, 0, 1, 1, 2, 3);
            version = PeripheralOutputs.LightingVersion;
            PeripheralOutputs.SetLighting(B, 0, 1, 1, 2, 3, keys: KeyFrame.Empty);
            Assert.Equal(version, PeripheralOutputs.LightingVersion);
        }
    }
}
