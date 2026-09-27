using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pads in iCade mode (hifihedgehog/SDL#33 Part 16). The fork decodes such
    /// a pad only when SDL_JOYSTICK_ICADE_DEVICES lists the IDs of the
    /// keyboard it pairs as, in the form "0xVVVV/0xPPPP" with "0x" and one to
    /// four hex digits on each side, vendor 0 refused, at most 32 pairs
    /// (SDL_ICade_ParseDevices, docs/README-icade.md). The ION iCade cabinet,
    /// 15E4:0132, needs no entry.
    /// </summary>
    public class ICadePadTests
    {
        [Fact]
        public void AnEntry_IsTheHintsOwnForm()
        {
            Assert.Equal("0x1234/0xABCD", ICadePads.Entry(0x1234, 0xABCD));
            Assert.Equal("0x0005/0x0001", ICadePads.Entry(0x0005, 0x0001));
            Assert.True(ICadePads.TryParse(ICadePads.Entry(0x1234, 0xABCD), out ushort v, out ushort p));
            Assert.Equal((0x1234, 0xABCD), (v, p));
        }

        [Theory]
        [InlineData("0x1/0x2", 0x1, 0x2)]
        [InlineData(" 0X1a2B / 0xc3D4 ", 0x1A2B, 0xC3D4)]
        [InlineData("0x0457/0x0000", 0x0457, 0x0000)]
        public void TheParser_TakesWhatTheForksParserTakes(string entry, int vendor, int product)
        {
            Assert.True(ICadePads.TryParse(entry, out ushort v, out ushort p));
            Assert.Equal((vendor, product), (v, p));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("1234/5678")]
        [InlineData("0x/0x12")]
        [InlineData("0x12345/0x1")]
        [InlineData("0x12/0xZZ")]
        [InlineData("0x0000/0x1234")]
        [InlineData("0x12:0x34")]
        [InlineData("0x12/0x34/0x56")]
        public void TheParser_RefusesWhatTheForksParserRefuses(string entry)
            => Assert.False(ICadePads.TryParse(entry, out _, out _));

        [Fact]
        public void TheHint_ListsEachPairOnceAndDropsTheUnreadable()
        {
            string value = ICadePads.HintValue(new[] { "0x1234/0x5678", "junk", "0X1234/0X5678", "0x0000/0x1", "0x9/0xA" });
            Assert.Equal("0x1234/0x5678,0x0009/0x000A", value);
            Assert.Equal(string.Empty, ICadePads.HintValue(null));
            Assert.Equal(string.Empty, ICadePads.HintValue(Array.Empty<string>()));
        }

        [Fact]
        public void TheHint_StopsAtTheForksThirtyTwoPairs()
        {
            var many = Enumerable.Range(1, 40).Select(i => ICadePads.Entry(0x1000, (ushort)i));
            Assert.Equal(ICadePads.MaxPairs, ICadePads.HintValue(many).Split(',').Length);
        }

        [Fact]
        public void Lists_MatchesThePair()
        {
            var pads = new[] { "0x1234/0x5678" };
            Assert.True(ICadePads.Lists(pads, 0x1234, 0x5678));
            Assert.False(ICadePads.Lists(pads, 0x1234, 0x5679));
            Assert.False(ICadePads.Lists(null, 0x1234, 0x5678));
        }

        /// <summary>Only a Bluetooth keyboard with IDs is offered, never the
        /// cabinet, which the fork decodes without an entry.</summary>
        [Theory]
        [InlineData(true, true, 0x1234, 0x5678, true)]
        [InlineData(true, false, 0x1234, 0x5678, false)]
        [InlineData(false, true, 0x1234, 0x5678, false)]
        [InlineData(true, true, 0x0000, 0x5678, false)]
        [InlineData(true, true, 0x15E4, 0x0132, false)]
        public void OnlyABluetoothKeyboardCanBeMarked(bool keyboard, bool bluetooth, int vendor, int product, bool expected)
            => Assert.Equal(expected, ICadePads.CanMark(keyboard, bluetooth, (ushort)vendor, (ushort)product));

        /// <summary>What SDL receives. Hints need no SDL_Init, and the
        /// joystick lock is a no-op before it, so this runs against the
        /// shipped SDL3.dll without starting a joystick driver.</summary>
        [Fact]
        public void Applying_WritesTheDevicesHint()
        {
            try
            {
                InputManager.ApplyICadePads(new[] { "0x1234/0x5678", "0x9/0xA" });
                Assert.Equal("0x1234/0x5678,0x0009/0x000A", SDL3.SDL.SDL_GetHint(SDL3.SDL.SDL_HINT_JOYSTICK_ICADE_DEVICES));
            }
            finally
            {
                InputManager.ApplyICadePads(Array.Empty<string>());
            }
            Assert.Equal(string.Empty, SDL3.SDL.SDL_GetHint(SDL3.SDL.SDL_HINT_JOYSTICK_ICADE_DEVICES) ?? string.Empty);
        }

        [Fact]
        public void TheList_PersistsInTheSettingsFile()
        {
            var data = new AppSettingsData { ICadePads = new[] { "0x1234/0x5678" } };
            var ser = new XmlSerializer(typeof(AppSettingsData));
            using var w = new StringWriter();
            ser.Serialize(w, data);
            Assert.Contains("<Pair>0x1234/0x5678</Pair>", w.ToString());
            var back = (AppSettingsData)ser.Deserialize(new StringReader(w.ToString()));
            Assert.Equal(new[] { "0x1234/0x5678" }, back.ICadePads);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "PadForge.App")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        /// <summary>The sibling set: load applies the list before SDL_Init,
        /// the save writes it, InitializeSdl replays it, the row fill offers
        /// the two buttons, and the page carries them.</summary>
        [Fact]
        public void SiblingLegs_LoadSaveReplayFillAndPage()
        {
            string settings = RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("PadForge.Common.Input.InputManager.ApplyICadePads(vm.ICadePads);", settings);
            Assert.Contains("ICadePads = vm.ICadePads.Count > 0", settings);

            string manager = RepoText("PadForge.App", "Common", "Input", "InputManager.cs");
            Assert.Contains("SDL_SetHint(SDL_HINT_JOYSTICK_ICADE_DEVICES, _iCadeHintValue);", manager);

            string fill = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("row.ShowReadAsICade = !iCadeListed && ICadePads.CanMark(", fill);
            Assert.Contains("row.ShowReadAsKeyboard = iCadeListed", fill);

            string page = RepoText("PadForge.App", "Views", "DevicesPage.xaml");
            Assert.Contains("Binding SelectedDevice.ShowReadAsICade, Converter", page);
            Assert.Contains("Binding SelectedDevice.ShowReadAsKeyboard, Converter", page);
            Assert.Contains("Click=\"ReadAsICade_Click\"", page);
            Assert.Contains("Click=\"ReadAsKeyboard_Click\"", page);

            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("_viewModel.Devices.ICadeModeRequested += (s, asPad) => SetICadeMode(asPad);", window);
        }
    }
}
