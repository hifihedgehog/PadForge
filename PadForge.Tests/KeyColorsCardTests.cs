using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #557: the Lighting tab's Key Colors card. Where a color goes
    /// (a key by usage, a whole group by name, the rest by light), how a
    /// shift layer shows the Base layer under its own colors, the picker
    /// and the selection, and the picture's placement and key lookup.
    /// </summary>
    public class KeyColorsCardTests
    {
        private static LightMap Map() => new(new[]
        {
            new LightInfo(0, LightKind.Key, 0x29, LightKeySource.Device),   // Esc
            new LightInfo(1, LightKind.Key, 0x04, LightKeySource.Device),   // A
            new LightInfo(2, LightKind.Key, 0x1A, LightKeySource.Device),   // W
            new LightInfo(3, LightKind.Key, 0, LightKeySource.None),        // a key no source named
            new LightInfo(4, LightKind.Edge, 0, LightKeySource.None),
            new LightInfo(5, LightKind.Edge, 0, LightKeySource.None),
            new LightInfo(6, LightKind.Logo, 0, LightKeySource.None),
            new LightInfo(7, LightKind.Key, 0x04, LightKeySource.Device),   // a second light under A
        }, "test");

        private static ShiftLayerInfo Layer(string mask, string name = null)
            => new() { LayerMask = mask, LayerName = name ?? mask };

        private static (KeyColorsCardViewModel Card, DeviceSlotConfig Config) Attached(string text = "",
            params ShiftLayerInfo[] layers)
        {
            var cfg = new DeviceSlotConfig { KeyLighting = text };
            var card = new KeyColorsCardViewModel();
            card.Attach(cfg, Map(), layers.Length > 0 ? layers : new[] { Layer("Base") });
            return (card, cfg);
        }

        [Fact]
        public void AKeyGoesByUsage_AWholeGroupByName_AndPartOfAGroupByLight()
        {
            var (card, cfg) = Attached();
            Assert.True(card.IsVisible);
            card.SetSelection(new[] { 1, 3, 4, 5 });
            card.ColorSelection(0xFF0000);
            Assert.Equal("Base>k04=FF0000,gedge=FF0000,l3=FF0000", cfg.KeyLighting);
            // The second light under A takes the key's color.
            Assert.Equal(0xFF0000, card.OwnColorOf(7));

            card.SetSelection(new[] { 4 });
            card.ColorSelection(0x00FF00);
            Assert.Equal("Base>k04=FF0000,gedge=FF0000,l3=FF0000,l4=00FF00", cfg.KeyLighting);
            Assert.Equal(0x00FF00, card.OwnColorOf(4));
            Assert.Equal(0xFF0000, card.OwnColorOf(5));
            Assert.Null(card.OwnColorOf(6));
        }

        [Fact]
        public void TakingTheColorOffPartOfAColoredGroup_LeavesItOnTheRestByLight()
        {
            var (card, cfg) = Attached("Base>gedge=0000FF,k04=FF0000");
            card.SetSelection(new[] { 4, 1 });
            card.ClearSelectedColors();
            Assert.Equal("Base>l5=0000FF", cfg.KeyLighting);
            Assert.Null(card.OwnColorOf(4));
            Assert.Equal(0x0000FF, card.OwnColorOf(5));
        }

        [Fact]
        public void AShiftLayerShowsTheBaseLayerUnderItsColors_UntilItsRuleSaysOtherwise()
        {
            var (card, cfg) = Attached("Base>k04=FF0000,k29=00FF00", Layer("Base"), Layer("Shift1"));
            card.SelectedLayerMask = "Shift1";
            Assert.False(card.IsBaseLayer);
            Assert.Equal(KeyBackgroundRule.BaseLayer, card.BackgroundRule);
            Assert.Null(card.OwnColorOf(1));
            Assert.Equal(0xFF0000, card.BaseColorOf(1));

            card.SetSelection(new[] { 1 });
            card.ColorSelection(0x0000FF);
            Assert.Equal("Base>k04=FF0000,k29=00FF00|Shift1>k04=0000FF", cfg.KeyLighting);
            Assert.Equal(0x0000FF, card.OwnColorOf(1));
            Assert.Null(card.BaseColorOf(1));
            Assert.Equal(0x00FF00, card.BaseColorOf(0));

            card.BackgroundRule = KeyBackgroundRule.Same;
            Assert.Equal("Base>k04=FF0000,k29=00FF00|Shift1>bg=same,k04=0000FF", cfg.KeyLighting);
            Assert.Null(card.BaseColorOf(0));

            // Removing every color sets the layer back to how it starts.
            card.ClearLayer();
            Assert.Equal("Base>k04=FF0000,k29=00FF00", cfg.KeyLighting);
            Assert.Equal(KeyBackgroundRule.BaseLayer, card.BackgroundRule);
        }

        [Fact]
        public void OnlyAShiftLayerCanShowTheBaseLayer()
        {
            var (card, cfg) = Attached("", Layer("Base", "Normal"), Layer("Shift1", "Fn"));
            Assert.Equal(new[] { KeyBackgroundRule.Same, KeyBackgroundRule.Off, KeyBackgroundRule.Color },
                card.BackgroundChoices.Select(c => c.Rule));
            card.BackgroundRule = KeyBackgroundRule.BaseLayer;
            Assert.Equal(string.Empty, cfg.KeyLighting);

            card.SelectedLayerMask = "Shift1";
            var choices = card.BackgroundChoices;
            Assert.Equal(KeyBackgroundRule.BaseLayer, choices[0].Rule);
            // The choice names the Base layer the way the Mappings tab does.
            Assert.Contains("Normal", choices[0].Label);
            Assert.Equal(4, choices.Count);
        }

        [Fact]
        public void ThePickerShowsOneLightsColor_AndKeepsItsOwnForSeveral()
        {
            var (card, cfg) = Attached("Base>k04=123456,k29=654321");
            // No selection: choosing a color changes nothing saved.
            card.PaintHex = "#FF0000";
            Assert.Equal("Base>k04=123456,k29=654321", cfg.KeyLighting);

            card.Toggle(1);
            Assert.Equal("123456", card.PaintHex);
            card.Toggle(0);
            Assert.Equal("123456", card.PaintHex);

            card.PaintHex = "abcdef";
            Assert.Equal("Base>k04=ABCDEF,k29=ABCDEF", cfg.KeyLighting);

            card.Toggle(1);
            card.PaintHex = "FF0000";
            card.SetSelection(new[] { 1, 2 });
            Assert.Equal("FF0000", card.PaintHex);
            card.ApplyColorCommand.Execute(null);
            Assert.Equal("Base>k04=FF0000,k1A=FF0000,k29=FF0000", cfg.KeyLighting);
        }

        [Fact]
        public void AChannelColorsTheSelectionAtOnce()
        {
            var (card, cfg) = Attached();
            card.PaintHex = "000000";
            card.SetSelection(new[] { 2 });
            card.PaintRed = 0x10;
            Assert.Equal("Base>k1A=100000", cfg.KeyLighting);
            card.PaintBlue = 0x20;
            Assert.Equal("Base>k1A=100020", cfg.KeyLighting);
        }

        [Fact]
        public void TheHexBoxesTakeSixDigits_WithOrWithoutTheHash()
        {
            var (card, cfg) = Attached();
            card.PaintHex = "#00ff00";
            Assert.Equal(0xFF, card.PaintGreen);
            card.PaintHex = "bogus";
            Assert.Equal("00FF00", card.PaintHex);
            card.PaintHex = "12345";
            Assert.Equal("00FF00", card.PaintHex);

            card.BackgroundHex = "0a0b0c";
            Assert.Equal(KeyBackgroundRule.Color, card.BackgroundRule);
            Assert.Equal("Base>bg=0A0B0C", cfg.KeyLighting);
            card.BackgroundGreen = 0xFF;
            Assert.Equal("Base>bg=0AFF0C", cfg.KeyLighting);
        }

        [Fact]
        public void PressingAKeyTakesEveryLightOfIt_InAndOut()
        {
            var (card, _) = Attached();
            Assert.True(card.TogglePressed(0x04));
            Assert.Equal(new[] { 1, 7 }, card.Selection.OrderBy(i => i));
            Assert.True(card.TogglePressed(0x04));
            Assert.Empty(card.Selection);
            Assert.False(card.TogglePressed(0x05));
        }

        [Fact]
        public void TheSelectionButtonsPickByKind()
        {
            var (card, _) = Attached();
            card.SelectAllKeysCommand.Execute(null);
            Assert.Equal(new[] { 0, 1, 2, 3, 7 }, card.Selection.OrderBy(i => i));
            card.SelectEdgeCommand.Execute(null);
            Assert.Equal(new[] { 4, 5 }, card.Selection.OrderBy(i => i));
            card.SelectWasdCommand.Execute(null);
            Assert.Equal(new[] { 1, 2, 7 }, card.Selection.OrderBy(i => i));
            card.ClearSelectionCommand.Execute(null);
            Assert.False(card.HasSelection);
            card.SelectAllLightsCommand.Execute(null);
            Assert.Equal(8, card.Selection.Count);
        }

        [Fact]
        public void ResetAllRemovesEveryLayer()
        {
            var (_, cfg) = Attached("Base>k04=FF0000|Shift1>bg=off");
            cfg.ResetKeyLightingCommand.Execute(null);
            Assert.Equal(string.Empty, cfg.KeyLighting);
        }

        [Fact]
        public void ANewDeviceClearsTheSelection_AndTheLayerPickStaysWhileTheLayerDoes()
        {
            var (card, cfg) = Attached("", Layer("Base"), Layer("Shift1"));
            card.SelectedLayerMask = "Shift1";
            card.Toggle(1);
            card.UpdateLayers(new[] { Layer("Base"), Layer("Shift1", "Fn") });
            Assert.Equal("Shift1", card.SelectedLayerMask);
            Assert.Equal("Fn", card.Layers[1].LayerName);
            Assert.Single(card.Selection);

            card.UpdateLayers(new[] { Layer("Base") });
            Assert.Equal("Base", card.SelectedLayerMask);
            Assert.False(card.HasLayers);

            card.Attach(new DeviceSlotConfig(), Map(), new[] { Layer("Base") });
            Assert.Empty(card.Selection);

            card.Detach();
            Assert.False(card.IsVisible);
            card.ColorSelection(0xFF0000);
            Assert.Equal(string.Empty, cfg.KeyLighting);
        }

        [Fact]
        public void AnOutsideChangeToTheConfigRedrawsTheCard()
        {
            var (card, cfg) = Attached();
            int redraws = 0;
            card.LightsChanged += () => redraws++;
            cfg.KeyLighting = "Base>k04=FF0000";
            Assert.Equal(1, redraws);
            Assert.Equal(0xFF0000, card.OwnColorOf(1));
        }

        [Fact]
        public void TheScanCodeOfAKeyMessageFindsItsUsage()
        {
            // lParam: the scan code in bits 16 to 23, the E0 form in bit 24.
            static MSG Key(int scan, bool extended)
                => new() { message = 0x0100, lParam = (IntPtr)((scan << 16) | (extended ? 1 << 24 : 0) | 1) };
            Assert.Equal(0x11C, KeyLightsView.ScanKey(Key(0x1C, true)));
            Assert.Equal(0x01E, KeyLightsView.ScanKey(Key(0x1E, false)));
            Assert.Equal(0, KeyLightsView.ScanKey(Key(0, false)));

            Assert.Equal(new ushort[] { 0x28 }, KbmHidKeyMap.UsagesOfScanKey(0x1C));
            Assert.Equal(new ushort[] { 0x58 }, KbmHidKeyMap.UsagesOfScanKey(0x11C));
            Assert.Equal(new ushort[] { 0x04 }, KbmHidKeyMap.UsagesOfScanKey(0x1E));
            // One scan code for the ANSI backslash and the ISO key left of Enter.
            Assert.Equal(new ushort[] { 0x31, 0x32 }, KbmHidKeyMap.UsagesOfScanKey(0x2B));
            // Pause and Num Lock, as the hook and the window message report them.
            Assert.Equal(new ushort[] { 0x48 }, KbmHidKeyMap.UsagesOfScanKey(0x45));
            Assert.Equal(new ushort[] { 0x53 }, KbmHidKeyMap.UsagesOfScanKey(0x145));
            Assert.Empty(KbmHidKeyMap.UsagesOfScanKey(0x7F));

            // Every usage a scan key names reads back to that scan key.
            for (int u = 1; u <= 0xFF; u++)
            {
                int scanKey = KbmHidKeyMap.ScanKeyOf(new KbmHidKeyMap.HidKey(0x07, (ushort)u));
                if (scanKey >= 0) Assert.Contains((ushort)u, KbmHidKeyMap.UsagesOfScanKey(scanKey));
            }
        }

        [Fact]
        public void KeysNamedByUsageGoOnTheLayoutThatHoldsTheMost_AndTheRestBecomeChips()
        {
            var map = Map();
            var placed = KeyLightsView.PlaceByLayout(map);
            // Esc, A twice and W have places. The unnamed key, the edge and
            // the logo do not.
            Assert.Equal(new[] { 0, 1, 2, 7 }, placed.Select(p => p.Index).OrderBy(i => i));
            var a = placed.Where(p => p.Index is 1 or 7).Select(p => p.Box).Distinct().ToList();
            Assert.Single(a);

            var edgeOnly = new LightMap(new[]
            {
                new LightInfo(0, LightKind.Edge, 0, LightKeySource.None),
                new LightInfo(1, LightKind.Edge, 0, LightKeySource.None),
            }, "test");
            Assert.Empty(KeyLightsView.PlaceByLayout(edgeOnly));
        }

        [Fact]
        public void LightsAtReportedPositions_KeysAtTheirPitch_SmallLightsSmaller()
        {
            var map = new LightMap(new[]
            {
                new LightInfo(0, LightKind.Key, 0x04, LightKeySource.Device) { X = 0, Y = 0, HasPosition = true },
                new LightInfo(1, LightKind.Key, 0x16, LightKeySource.Device) { X = 19050, Y = 0, HasPosition = true },
                new LightInfo(2, LightKind.Key, 0x07, LightKeySource.Device) { X = 38100, Y = 0, HasPosition = true },
                new LightInfo(3, LightKind.Edge, 0, LightKeySource.None) { X = 19050, Y = 30000, HasPosition = true },
            }, "test");
            Assert.True(map.Positioned);
            Assert.Equal(19050, KeyLightsView.KeyPitch(map.Lights), 3);
            var placed = KeyLightsView.PlaceByPosition(map);
            var key = placed[0].Box;
            var edge = placed[3].Box;
            Assert.Equal(key.Width, placed[2].Box.Width, 6);
            Assert.True(edge.Width < key.Width);
            // Left to right in the device's order, the edge light under the
            // middle key.
            Assert.True(placed[0].Box.X < placed[1].Box.X && placed[1].Box.X < placed[2].Box.X);
            Assert.Equal(placed[1].Box.X + placed[1].Box.Width / 2, edge.X + edge.Width / 2, 6);
            Assert.True(edge.Y > key.Bottom);
        }
    }

    /// <summary>The pad lets the card go when the device config it shows
    /// is swapped, before the page attaches it to the new one.</summary>
    [Collection("SettingsManagerStatics")]
    public class KeyColorsCardPadTests
    {
        [Fact]
        public void ASwappedDeviceConfigLetsTheCardGo()
        {
            var vm = new PadViewModel(0);
            var cfg = vm.GetOrCreateDeviceConfig(Guid.NewGuid());
            vm.DeviceConfig = cfg;
            vm.KeyColors.Attach(cfg, new LightMap(new[]
            {
                new LightInfo(0, LightKind.Key, 0x04, LightKeySource.Device),
                new LightInfo(1, LightKind.Key, 0x05, LightKeySource.Device),
            }, "test"), vm.LayerTabs);
            Assert.True(vm.KeyColors.IsVisible);

            vm.DeviceConfig = vm.GetOrCreateDeviceConfig(Guid.NewGuid());
            Assert.False(vm.KeyColors.IsVisible);
            vm.KeyColors.SetSelection(new[] { 0 });
            vm.KeyColors.ColorSelection(0xFF0000);
            Assert.Equal(string.Empty, cfg.KeyLighting);
        }
    }
}
