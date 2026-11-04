using System;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The LED counts a device's config holds for its strips (#557):
    /// their text form, the setting, and the Lighting tab's LED Strips
    /// card.</summary>
    public class LedStripsCardTests
    {
        [Theory]
        [InlineData("", new int[0])]
        [InlineData("  ", new int[0])]
        [InlineData("30", new[] { 30 })]
        [InlineData("30,0,12", new[] { 30, 0, 12 })]
        [InlineData("30,0,0", new[] { 30 })]
        [InlineData("0,0", new int[0])]
        [InlineData("x,5", new[] { 0, 5 })]
        [InlineData("-5,5", new[] { 0, 5 })]
        [InlineData("99999999999,5", new[] { 0, 5 })]
        [InlineData(" 7 , 8 ", new[] { 7, 8 })]
        public void TheText_ReadsAsCounts_WithNoTrailingZeros(string text, int[] counts)
            => Assert.Equal(counts, StripCounts.Parse(text));

        [Fact]
        public void TheCounts_WriteBackAsTextThatReadsTheSame()
        {
            Assert.Equal("30,0,12", StripCounts.Format(new[] { 30, 0, 12, 0 }));
            Assert.Equal(string.Empty, StripCounts.Format(new[] { 0, 0 }));
            Assert.Equal(string.Empty, StripCounts.Format(null));
            Assert.Equal("30,0,12", StripCounts.Format(StripCounts.Parse("30,0,12")));
        }

        [Fact]
        public void AStripsCount_IsZeroWhereNoneIsSet()
        {
            var counts = new[] { 30, 0, 12 };
            Assert.Equal(30, StripCounts.At(counts, 1));
            Assert.Equal(12, StripCounts.At(counts, 3));
            Assert.Equal(0, StripCounts.At(counts, 0));
            Assert.Equal(0, StripCounts.At(counts, 4));
            Assert.Equal(0, StripCounts.At(null, 1));
        }

        [Fact]
        public void SettingACount_ChangesThatStripAlone()
        {
            Assert.Equal(new[] { 30, 5, 12 }, StripCounts.With(new[] { 30, 0, 12 }, 2, 5));
            Assert.Equal(new[] { 0, 0, 7 }, StripCounts.With(null, 3, 7));
            Assert.Equal(new[] { 30 }, StripCounts.With(new[] { 30, 12 }, 2, 0));
            Assert.Empty(StripCounts.With(new[] { 30 }, 1, 0));
            Assert.Equal(new[] { 30 }, StripCounts.With(new[] { 30 }, 0, 9));
        }

        [Fact]
        public void TwoLists_AreTheSameWhereEveryStripIs()
        {
            Assert.True(StripCounts.Same(null, StripCounts.None));
            Assert.True(StripCounts.Same(new[] { 0, 0 }, null));
            Assert.True(StripCounts.Same(new[] { 3, 0 }, new[] { 3 }));
            Assert.False(StripCounts.Same(new[] { 3 }, new[] { 4 }));
            Assert.False(StripCounts.Same(new[] { 3 }, new[] { 3, 1 }));
            Assert.False(StripCounts.Same(null, new[] { 1 }));
        }

        [Fact]
        public void TheSetting_SavesAsItsText_AndTellsTheDispatcher()
        {
            var config = new DeviceSlotConfig();
            Assert.Equal(string.Empty, config.LedStripCounts);
            Assert.Empty(config.LedStripCountsParsed);
            string changed = null;
            config.PropertyChanged += (_, e) => changed = e.PropertyName;
            config.LedStripCounts = "30,0,12,0";
            Assert.Equal("30,0,12", config.LedStripCounts);
            Assert.Equal(new[] { 30, 0, 12 }, config.LedStripCountsParsed);
            Assert.Equal(nameof(DeviceSlotConfig.LedStripCounts), changed);
            config.SetLedStripCount(2, 8);
            Assert.Equal("30,8,12", config.LedStripCounts);
            config.ResetLedStripCountsCommand.Execute(null);
            Assert.Equal(string.Empty, config.LedStripCounts);
            Assert.Empty(config.LedStripCountsParsed);
        }

        private static LightMap Map(params LightStrip[] strips)
            => new(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) }, "test") { Strips = strips };

        [Fact]
        public void TheCard_ListsEachStripOfTheDevice_WithItsCountAndMost()
        {
            var config = new DeviceSlotConfig { LedStripCounts = "0,12" };
            var card = new LedStripsCardViewModel();
            card.Attach(config, Map(new LightStrip(1, 48, 0), new LightStrip(2, 48, 12), new LightStrip(4, 60, 0)));
            Assert.Equal(new[] { 1, 2, 4 }, card.Rows.Select(r => r.Number));
            Assert.Equal(new[] { 48, 48, 60 }, card.Rows.Select(r => r.Maximum));
            Assert.Equal(new double?[] { 0, 12, 0 }, card.Rows.Select(r => r.Leds));
        }

        /// <summary>A card that moved to another device stops listening to the
        /// last one, or that device's counts would reach this one's rows.</summary>
        [Fact]
        public void TheCard_LetsGoOfTheDeviceItLeft()
        {
            var left = new DeviceSlotConfig();
            var card = new LedStripsCardViewModel();
            card.Attach(left, Map(new LightStrip(1, 48, 0)));
            card.Attach(new DeviceSlotConfig(), Map(new LightStrip(1, 48, 0)));
            int raised = 0;
            card.Rows[0].PropertyChanged += (_, _) => raised++;
            left.LedStripCounts = "5";
            Assert.Equal(0, raised);
            card.Detach();
            Assert.Empty(card.Rows);
        }

        /// <summary>The counts go to disk and come back with the rest of a
        /// device's settings.</summary>
        [Fact]
        public void TheCounts_SaveAndLoad()
        {
            var build = typeof(PadForge.Services.SettingsService).GetMethod("BuildDeviceSlotConfigData",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(build);
            var saved = (DeviceSlotConfigData)build.Invoke(null, new object[]
            {
                new DeviceSlotConfig { LedStripCounts = "30,0,12" }, 3, Guid.NewGuid(),
            });
            Assert.Equal("30,0,12", saved.LedStripCounts);
            var loaded = new DeviceSlotConfig { LedStripCounts = "9" };
            PadForge.Services.SettingsService.ApplyDeviceSlotConfigData(loaded, saved);
            Assert.Equal("30,0,12", loaded.LedStripCounts);
            saved.LedStripCounts = null;
            PadForge.Services.SettingsService.ApplyDeviceSlotConfigData(loaded, saved);
            Assert.Equal(string.Empty, loaded.LedStripCounts);
        }

        /// <summary>A copy onto a device with no settings of its own leaves that
        /// device's counts alone: they describe what is plugged into it. The
        /// copy's data is left as it came.</summary>
        [Fact]
        public void AStandIn_KeepsItsOwnCounts()
        {
            var build = typeof(PadForge.Services.SettingsService).GetMethod("BuildDeviceSlotConfigData",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var apply = typeof(PadForge.Services.SettingsService).GetMethod("ApplyStandInDeviceSlotConfigData",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(apply);
            var source = (DeviceSlotConfigData)build.Invoke(null, new object[]
            {
                new DeviceSlotConfig { LedStripCounts = "30" }, 3, Guid.NewGuid(),
            });
            var standIn = new DeviceSlotConfig { LedStripCounts = "12,4" };
            apply.Invoke(null, new object[] { standIn, source });
            Assert.Equal("12,4", standIn.LedStripCounts);
            Assert.Equal("30", source.LedStripCounts);
        }

        /// <summary>A count comes back as a new map, and the rows of the same
        /// strips stay, so a box being typed in keeps its focus.</summary>
        [Fact]
        public void TheSameStripsInANewMap_KeepTheirRows()
        {
            var config = new DeviceSlotConfig();
            var card = new LedStripsCardViewModel();
            card.Attach(config, Map(new LightStrip(1, 48, 0)));
            var row = card.Rows[0];
            card.Attach(config, Map(new LightStrip(1, 48, 30)));
            Assert.Same(row, card.Rows[0]);
            card.Attach(config, Map(new LightStrip(1, 60, 30)));
            Assert.NotSame(row, card.Rows[0]);
            var other = new DeviceSlotConfig();
            row = card.Rows[0];
            card.Attach(other, Map(new LightStrip(1, 60, 30)));
            Assert.NotSame(row, card.Rows[0]);
        }

        [Theory]
        [InlineData(30d, 30)]
        [InlineData(30.4d, 30)]
        [InlineData(500d, 48)]
        [InlineData(-2d, 0)]
        [InlineData(null, 0)]
        public void ARowsBox_WritesItsCountWithinTheMost(double? typed, int saved)
        {
            var config = new DeviceSlotConfig { LedStripCounts = "5" };
            var card = new LedStripsCardViewModel();
            card.Attach(config, Map(new LightStrip(1, 48, 5)));
            card.Rows[0].Leds = typed;
            Assert.Equal(saved, StripCounts.At(config.LedStripCountsParsed, 1));
            Assert.Equal((double?)saved, card.Rows[0].Leds);
        }

        [Fact]
        public void ARowsReset_ClearsItsCountAlone_AndTheCardsResetEveryCount()
        {
            var config = new DeviceSlotConfig { LedStripCounts = "5,6" };
            var card = new LedStripsCardViewModel();
            card.Attach(config, Map(new LightStrip(1, 48, 5), new LightStrip(2, 48, 6)));
            card.Rows[0].ResetCountCommand.Execute(null);
            Assert.Equal("0,6", config.LedStripCounts);
            string raised = null;
            card.Rows[1].PropertyChanged += (_, e) => raised = e.PropertyName;
            config.ResetLedStripCountsCommand.Execute(null);
            Assert.Equal(nameof(LedStripRow.Leds), raised);
            Assert.Equal((double?)0, card.Rows[1].Leds);
        }

        [Fact]
        public void TheCard_LetsGoOfTheConfigWhenDetached()
        {
            var config = new DeviceSlotConfig { LedStripCounts = "5" };
            var card = new LedStripsCardViewModel();
            card.Attach(config, Map(new LightStrip(1, 48, 5)));
            card.Detach();
            Assert.Empty(card.Rows);
            config.LedStripCounts = "7";
            Assert.Empty(card.Rows);
            card.Attach(config, Map());
            Assert.Empty(card.Rows);
        }

        /// <summary>Map Key Lights' names keep a map's strips.</summary>
        [Fact]
        public void ANamedMap_KeepsItsStrips()
        {
            string product = "test:strips:" + Guid.NewGuid();
            var strips = new[] { new LightStrip(1, 48, 0) };
            var map = new LightMap(new[] { new LightInfo(7, LightKind.Key, 0, LightKeySource.None) }, "test")
            {
                Product = product,
                Strips = strips,
            };
            LearnedKeyMaps.Set(product, 7, 0x29);
            try
            {
                var named = LearnedKeyMaps.Apply(map);
                Assert.NotSame(map, named);
                Assert.Equal(strips, named.Strips);
            }
            finally { LearnedKeyMaps.Set(product, 7, 0); }
        }
    }
}
