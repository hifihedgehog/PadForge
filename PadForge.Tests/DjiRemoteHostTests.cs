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
    /// DJI RC and RC 2 remotes read over the network (hifihedgehog/SDL#33
    /// Part 6). The fork connects only to the hosts
    /// SDL_JOYSTICK_DJI_REMOTE_TCP_HOSTS names, as four dotted decimal octets
    /// with no leading zero and an optional port from 1 to 65535, 40007 when
    /// absent, at most 8 hosts, and an address and port named twice kept once
    /// (SDL_dji_tcp_proto.c, SDL_DJITCP_ParseHosts).
    /// </summary>
    public class DjiRemoteHostTests
    {
        [Theory]
        [InlineData("192.168.7.251", "192.168.7.251:40007")]
        [InlineData("127.0.0.1:40007", "127.0.0.1:40007")]
        [InlineData("  10.0.0.2:5000  ", "10.0.0.2:5000")]
        [InlineData("0.0.0.0", "0.0.0.0:40007")]
        [InlineData("255.255.255.255:65535", "255.255.255.255:65535")]
        public void TheParser_TakesWhatTheForksParserTakes(string text, string key)
        {
            Assert.True(DjiRemoteHosts.TryNormalize(text, out string normal));
            Assert.Equal(key, normal);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("192.168.1")]
        [InlineData("192.168.1.256")]
        [InlineData("192.168.01.1")]
        [InlineData("1.2.3.4:0")]
        [InlineData("1.2.3.4:65536")]
        [InlineData("1.2.3.4:040007")]
        [InlineData("1.2.3.4:")]
        [InlineData("1.2.3.4:5:6")]
        [InlineData("1.2.3.4.5")]
        [InlineData("remote.local")]
        [InlineData("::1")]
        [InlineData("1.2.3.-4")]
        public void TheParser_RefusesWhatTheForksParserRefuses(string text)
            => Assert.False(DjiRemoteHosts.TryNormalize(text, out _));

        [Fact]
        public void TheHint_ListsEachHostOnceAndDropsTheUnreadable()
        {
            string value = DjiRemoteHosts.HintValue(new[] { "1.2.3.4", "junk", "1.2.3.4:40007", "5.6.7.8:9000" });
            Assert.Equal("1.2.3.4:40007,5.6.7.8:9000", value);
            Assert.Equal(string.Empty, DjiRemoteHosts.HintValue(null));
        }

        [Fact]
        public void TheHint_StopsAtTheForksEightHosts()
        {
            var many = Enumerable.Range(1, 12).Select(i => "10.0.0." + i);
            Assert.Equal(DjiRemoteHosts.MaxHosts, DjiRemoteHosts.HintValue(many).Split(',').Length);
        }

        /// <summary>What SDL receives. Hints need no SDL_Init, and the
        /// joystick lock is a no-op before it, so this runs against the
        /// shipped SDL3.dll without opening a connection.</summary>
        [Fact]
        public void Applying_WritesTheHostsHint()
        {
            try
            {
                InputManager.ApplyDjiRemoteHosts(new[] { "192.168.7.251", "10.0.0.2:5000" });
                Assert.Equal("192.168.7.251:40007,10.0.0.2:5000",
                    SDL3.SDL.SDL_GetHint(SDL3.SDL.SDL_HINT_JOYSTICK_DJI_REMOTE_TCP_HOSTS));
            }
            finally
            {
                InputManager.ApplyDjiRemoteHosts(Array.Empty<string>());
            }
            Assert.Equal(string.Empty, SDL3.SDL.SDL_GetHint(SDL3.SDL.SDL_HINT_JOYSTICK_DJI_REMOTE_TCP_HOSTS) ?? string.Empty);
        }

        [Fact]
        public void TheList_PersistsInTheSettingsFile()
        {
            var data = new AppSettingsData { DjiRemoteHosts = new[] { "192.168.7.251:40007" } };
            var ser = new XmlSerializer(typeof(AppSettingsData));
            using var w = new StringWriter();
            ser.Serialize(w, data);
            Assert.Contains("<Host>192.168.7.251:40007</Host>", w.ToString());
            var back = (AppSettingsData)ser.Deserialize(new StringReader(w.ToString()));
            Assert.Equal(new[] { "192.168.7.251:40007" }, back.DjiRemoteHosts);
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
        /// the save writes it, InitializeSdl replays it, and the dialog's
        /// changes reach SDL and the file.</summary>
        [Fact]
        public void SiblingLegs_LoadSaveReplayAndChange()
        {
            string settings = RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("PadForge.Common.Input.InputManager.ApplyDjiRemoteHosts(vm.DjiRemoteHosts);", settings);
            Assert.Contains("DjiRemoteHosts = vm.DjiRemoteHosts.Count > 0", settings);

            string manager = RepoText("PadForge.App", "Common", "Input", "InputManager.cs");
            Assert.Contains("SDL_SetHint(SDL_HINT_JOYSTICK_DJI_REMOTE_TCP_HOSTS, _djiHostsHintValue);", manager);

            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("Common.Input.InputManager.ApplyDjiRemoteHosts(_viewModel.Settings.DjiRemoteHosts);", window);
        }
    }
}
