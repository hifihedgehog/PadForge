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
    /// The Namco USIO's layout (hifihedgehog/SDL#33 Part 14). One board ID
    /// serves Taiko no Tatsujin and Tekken cabinets. The fork reads
    /// SDL_JOYSTICK_HIDAPI_USIO_LAYOUT when the board opens: "tekken" in any
    /// case selects Tekken's four sticks, and any other value Taiko's two
    /// drums, the default (docs/README-arcade-io.md).
    /// </summary>
    public class UsioLayoutTests
    {
        [Theory]
        [InlineData("tekken", "tekken")]
        [InlineData("TEKKEN", "tekken")]
        [InlineData(" Tekken ", "tekken")]
        [InlineData("taiko", "taiko")]
        [InlineData("", "taiko")]
        [InlineData(null, "taiko")]
        [InlineData("ddr", "taiko")]
        public void TheLayout_ReadsAsTheForkReadsIt(string text, string layout)
            => Assert.Equal(layout, InputManager.NormalizeUsioLayout(text));

        /// <summary>What SDL receives. Hints need no SDL_Init, so this runs
        /// against the shipped SDL3.dll without opening a board. The re-open
        /// toggle is left out: it only matters to a running driver.</summary>
        [Fact]
        public void Applying_WritesTheLayoutHint()
        {
            try
            {
                InputManager.ApplyUsioLayout("Tekken", reopen: false);
                Assert.Equal("tekken", SDL3.SDL.SDL_GetHint(SDL3.SDL.SDL_HINT_JOYSTICK_HIDAPI_USIO_LAYOUT));
            }
            finally
            {
                InputManager.ApplyUsioLayout(InputManager.UsioTaiko, reopen: false);
            }
            Assert.Equal("taiko", SDL3.SDL.SDL_GetHint(SDL3.SDL.SDL_HINT_JOYSTICK_HIDAPI_USIO_LAYOUT));
        }

        [Fact]
        public void OnlyTekken_IsWrittenToTheSettingsFile()
        {
            var ser = new XmlSerializer(typeof(AppSettingsData));
            using var w = new StringWriter();
            ser.Serialize(w, new AppSettingsData { UsioLayout = "tekken" });
            Assert.Contains("<UsioLayout>tekken</UsioLayout>", w.ToString());
            using var w2 = new StringWriter();
            ser.Serialize(w2, new AppSettingsData());
            Assert.DoesNotContain("UsioLayout", w2.ToString());
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "PadForge.App")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        /// <summary>The sibling set: load applies the layout without a
        /// re-open, InitializeSdl replays it, the rows offer the other layout
        /// by the joystick names the fork gives each, and the page carries
        /// both buttons.</summary>
        [Fact]
        public void SiblingLegs_LoadReplayFillAndPage()
        {
            string settings = RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("PadForge.Common.Input.InputManager.ApplyUsioLayout(vm.UsioLayout, reopen: false);", settings);

            string manager = RepoText("PadForge.App", "Common", "Input", "InputManager.cs");
            Assert.Contains("SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_USIO_LAYOUT, _usioLayout);", manager);

            string fill = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("row.ShowUsioTekken = boardName.StartsWith(\"Namco USIO Taiko Drum\", StringComparison.Ordinal);", fill);
            Assert.Contains("row.ShowUsioTaiko = boardName.StartsWith(\"Namco USIO Tekken\", StringComparison.Ordinal);", fill);

            string page = RepoText("PadForge.App", "Views", "DevicesPage.xaml");
            Assert.Contains("Binding SelectedDevice.ShowUsioTekken, Converter", page);
            Assert.Contains("Binding SelectedDevice.ShowUsioTaiko, Converter", page);

            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("_viewModel.Devices.UsioLayoutRequested += (s, layout) => SetUsioLayout(layout);", window);
        }
    }
}
