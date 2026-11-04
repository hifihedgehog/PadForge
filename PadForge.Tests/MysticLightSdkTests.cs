using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The MSI Mystic Light SDK route (#557 item 11) against a scripted
    /// library: the calls in the order MSI's documentation and RGB.NET make
    /// them, each LED set to the "Steady" style before its first color, and
    /// every LED's own style and color put back at its hand-back.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class MysticLightBackendTests : IDisposable
    {
        private static readonly Guid Mouse = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid Board = new("00000000-0000-0000-0000-0000000000d4");
        private static readonly Guid Keyboard = new("00000000-0000-0000-0000-0000000000b2");

        public MysticLightBackendTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        internal sealed class FakeMysticLight : IMysticLightNative
        {
            private readonly object _lock = new();
            private readonly List<string> _calls = new();
            public volatile bool LoadOk = true;
            public volatile int InitStatus;
            public volatile int SetStatus;
            public string[] Types = { "MSI_MB", "MSI_MOUSE", "MSI_KEYBOARD", "MSI_HEADSET" };
            public string[] Counts = { "2", "1", "1", "x" };

            private void Log(string s) { lock (_lock) _calls.Add(s); }
            public string[] Calls { get { lock (_lock) return _calls.ToArray(); } }
            public int Count(string call) => Calls.Count(c => c == call);
            public void Clear() { lock (_lock) _calls.Clear(); }

            public bool TryLoad(out string detail) { Log("load"); detail = "fake"; return LoadOk; }
            public int Initialize() { Log("init"); return InitStatus; }
            public int GetDeviceInfo(out string[] types, out string[] ledCounts)
            {
                Log("info");
                types = Types;
                ledCounts = Counts;
                return 0;
            }
            public int GetLedStyle(string type, int index, out string style)
            {
                Log($"getstyle:{type}:{index}");
                style = "Rainbow";
                return 0;
            }
            public int GetLedColor(string type, int index, out int r, out int g, out int b)
            {
                Log($"getcolor:{type}:{index}");
                r = 1; g = 2; b = 3;
                return 0;
            }
            public int SetLedStyle(string type, int index, string style)
            {
                Log($"style:{type}:{index}:{style}");
                return 0;
            }
            public int SetLedColor(string type, int index, int r, int g, int b)
            {
                Log($"color:{type}:{index}:{r},{g},{b}");
                return SetStatus;
            }
            public int Release() { Log("release"); return 0; }
            public void Unload() => Log("unload");
        }

        private static MysticLightBackend Backend(FakeMysticLight native) => new(native, pollMs: 10, retryMs: 100, stopWaitMs: 2000);

        private static OutputPath Path(string key) => new(OutputFamily.MysticLightType, key);

        private static void Link(params DeviceLinks[] rows) => PeripheralOutputs.PublishLinks(new LinkTable(rows));

        private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000) => SpinWait.SpinUntil(condition, timeoutMs);

        private static PeripheralPresence Presence(bool installed = true, bool running = true)
            => new PeripheralPresence(false, false, false, false, false)
            {
                MsiMysticLightSdk = installed,
                Programs = RunningPrograms.Of(running ? new[] { "Mystic_Light_Service" } : new[] { "explorer" }, VendorPrograms.All),
            };

        [Fact]
        public void ThePathsAreRgbNetsThreeTypes_AndOneForEveryOther()
        {
            Assert.Equal(new[] { "MSI_MB", "MSI_VGA", "MSI_MOUSE", "other" }, MysticLightBackend.PathKeys);
            Assert.Equal("MSI_MB", MysticLightBackend.PathOfType("MSI_MB"));
            Assert.Equal("MSI_VGA", MysticLightBackend.PathOfType("MSI_VGA"));
            Assert.Equal("MSI_MOUSE", MysticLightBackend.PathOfType("MSI_MOUSE"));
            Assert.Equal("other", MysticLightBackend.PathOfType("MSI_KEYBOARD"));
            Assert.Equal("other", MysticLightBackend.PathOfType("other"));
            // RGB.NET's style string (MsiMainboardRGBDevice.cs:38) and
            // MLAPI_OK (Mystic Light SDK 1.0.0.08 p.9).
            Assert.Equal("Steady", MysticLightBackend.SteadyStyle);
            Assert.Equal(0, MysticLightBackend.MlapiOk);
        }

        [Fact]
        public void NothingClaimed_TheLibraryIsNeverLoaded()
        {
            var native = new FakeMysticLight();
            PeripheralOutputs.Presence = Presence();
            PeripheralOutputs.SetBackendState(OutputFamily.MysticLightType, BackendState.Waiting);
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Path("MSI_MOUSE") } });
            using var backend = Backend(native);
            backend.Start();
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.MysticLightType) == BackendState.Idle));
            Thread.Sleep(60);
            Assert.Empty(native.Calls);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void WithoutTheProgramOrTheLibrary_NothingIsLoaded(bool installed, bool running)
        {
            var native = new FakeMysticLight();
            PeripheralOutputs.Presence = Presence(installed, running);
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Path("MSI_MOUSE") } });
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 0x10, 0x20, 0x30);
            using var backend = Backend(native);
            backend.Start();
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.MysticLightType) == BackendState.Waiting));
            Thread.Sleep(60);
            Assert.Empty(native.Calls);
        }

        /// <summary>The session: load, MLAPI_Initialize, MLAPI_GetDeviceInfo
        /// (p.3). Then each LED of the claimed path: its style and color
        /// read, the "Steady" style set, its color set. A second color sends
        /// the color alone.</summary>
        [Fact]
        public void AClaim_ReadsEachLedsOwnState_SetsSteady_ThenItsColor()
        {
            var native = new FakeMysticLight();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Board, Lighting = new[] { Path("MSI_MB") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Board, slot: 0, player: 1, 0x10, 0x20, 0x30);
            Assert.True(WaitFor(() => native.Count("color:MSI_MB:1:16,32,48") == 1));
            Assert.Equal(new[]
            {
                "load", "init", "info",
                "getstyle:MSI_MB:0", "getcolor:MSI_MB:0", "style:MSI_MB:0:Steady", "color:MSI_MB:0:16,32,48",
                "getstyle:MSI_MB:1", "getcolor:MSI_MB:1", "style:MSI_MB:1:Steady", "color:MSI_MB:1:16,32,48",
            }, native.Calls);
            Assert.Equal(2, PeripheralLightMaps.For(Path("MSI_MB")).Lights.Count);

            native.Clear();
            PeripheralOutputs.SetLighting(Board, slot: 0, player: 1, 0xFF, 0x00, 0x00);
            Assert.True(WaitFor(() => native.Count("color:MSI_MB:1:255,0,0") == 1));
            Thread.Sleep(40);
            Assert.Equal(new[] { "color:MSI_MB:0:255,0,0", "color:MSI_MB:1:255,0,0" }, native.Calls);
        }

        /// <summary>A zone color reaches its LED alone, and only an LED whose
        /// color changed goes out again.</summary>
        [Fact]
        public void LightColors_ReachTheirLed_AndOnlyChangedLedsGoOut()
        {
            var native = new FakeMysticLight();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Board, Lighting = new[] { Path("MSI_MB") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Board, slot: 0, player: 1, 0x00, 0x00, 0x00);
            Assert.True(WaitFor(() => native.Count("color:MSI_MB:1:0,0,0") == 1));
            native.Clear();
            var frame = new KeyFrame(null, new Dictionary<string, int> { [LightGroups.Zone(2)] = 0x0000FF }, null, null);
            PeripheralOutputs.SetLighting(Board, slot: 0, player: 1, 0x00, 0x00, 0x00, keys: frame);
            Assert.True(WaitFor(() => native.Count("color:MSI_MB:1:0,0,255") == 1));
            Thread.Sleep(40);
            Assert.Equal(new[] { "color:MSI_MB:1:0,0,255" }, native.Calls);
        }

        /// <summary>Every type RGB.NET does not name goes to the "other"
        /// path, its LEDs in the SDK's order. A count that is no number
        /// leaves its type out.</summary>
        [Fact]
        public void TheOtherPath_TakesEveryUnnamedType()
        {
            var native = new FakeMysticLight { Counts = new[] { "2", "1", "3", "x" } };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("other") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x01, 0x02, 0x03);
            Assert.True(WaitFor(() => native.Count("color:MSI_KEYBOARD:2:1,2,3") == 1));
            var colored = native.Calls.Where(c => c.StartsWith("color:")).ToArray();
            Assert.Equal(new[] { "color:MSI_KEYBOARD:0:1,2,3", "color:MSI_KEYBOARD:1:1,2,3", "color:MSI_KEYBOARD:2:1,2,3" }, colored);
            Assert.Equal(3, PeripheralLightMaps.For(Path("other")).Lights.Count);
        }

        /// <summary>A path that leaves while another stays gives its LEDs
        /// their own color and style back, color first. The last release
        /// does the same for the rest, then MLAPI_Release and the library
        /// freed.</summary>
        [Fact]
        public void HandBack_PutsEachLedsOwnColorAndStyleBack_ThenReleases()
        {
            var native = new FakeMysticLight();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Board, Lighting = new[] { Path("MSI_MB") } },
                 new DeviceLinks { Device = Mouse, Lighting = new[] { Path("MSI_MOUSE") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Board, slot: 0, player: 1, 0x10, 0x20, 0x30);
            PeripheralOutputs.SetLighting(Mouse, slot: 1, player: 2, 0x40, 0x50, 0x60);
            Assert.True(WaitFor(() => native.Count("color:MSI_MOUSE:0:64,80,96") == 1));

            native.Clear();
            PeripheralOutputs.ReleaseLighting(Mouse, 1);
            Assert.True(WaitFor(() => native.Count("style:MSI_MOUSE:0:Rainbow") == 1));
            Assert.Equal(new[] { "color:MSI_MOUSE:0:1,2,3", "style:MSI_MOUSE:0:Rainbow" }, native.Calls);

            native.Clear();
            PeripheralOutputs.ReleaseLighting(Board, 0);
            Assert.True(WaitFor(() => native.Count("unload") == 1));
            Assert.Equal(new[]
            {
                "color:MSI_MB:0:1,2,3", "style:MSI_MB:0:Rainbow",
                "color:MSI_MB:1:1,2,3", "style:MSI_MB:1:Rainbow",
                "release", "unload",
            }, native.Calls);
        }

        /// <summary>An MLAPI_Initialize that does not answer MLAPI_OK frees
        /// the library and tries again after the retry interval.</summary>
        [Fact]
        public void AFailedInitialize_FreesTheLibrary_AndRetries()
        {
            var native = new FakeMysticLight { InitStatus = -3 };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Board, Lighting = new[] { Path("MSI_MB") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Board, slot: 0, player: 1, 0x10, 0x20, 0x30);
            Assert.True(WaitFor(() => native.Count("init") == 1));
            Assert.True(WaitFor(() => native.Count("unload") == 1));
            Assert.Equal(new[] { "load", "init", "unload" }, native.Calls.Take(3));
            Assert.DoesNotContain("info", native.Calls);
            native.InitStatus = 0;
            Assert.True(WaitFor(() => native.Count("color:MSI_MB:1:16,32,48") == 1));
        }

        /// <summary>A color the SDK refuses ends the session with the
        /// hand-back of every LED changed so far.</summary>
        [Fact]
        public void ARefusedColor_EndsTheSession_WithItsHandBack()
        {
            var native = new FakeMysticLight { SetStatus = -2 };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Board, Lighting = new[] { Path("MSI_MB") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Board, slot: 0, player: 1, 0x10, 0x20, 0x30);
            Assert.True(WaitFor(() => native.Count("release") >= 1));
            var calls = native.Calls;
            int failed = Array.IndexOf(calls, "color:MSI_MB:0:16,32,48");
            Assert.Equal(new[] { "color:MSI_MB:0:1,2,3", "style:MSI_MB:0:Rainbow", "release", "unload" },
                calls.Skip(failed + 1).Take(4));
            Assert.DoesNotContain("getstyle:MSI_MB:1", calls.Take(failed + 1));
        }

        // ── Finding the library ──

        [Theory]
        [InlineData("\"C:\\Program Files (x86)\\MSI\\MSI Center\\Mystic Light\\Mystic_Light_Service.exe\"",
            "C:\\Program Files (x86)\\MSI\\MSI Center\\Mystic Light")]
        [InlineData("C:\\Program Files (x86)\\MSI\\One Dragon Center\\Mystic_Light\\Mystic_Light_Service.exe -service",
            "C:\\Program Files (x86)\\MSI\\One Dragon Center\\Mystic_Light")]
        [InlineData("", null)]
        [InlineData("\"unclosed", null)]
        public void TheServiceFolder_IsReadFromItsImagePath(string imagePath, string folder)
            => Assert.Equal(folder, MysticLightNative.FolderOfImagePath(imagePath));

        /// <summary>Only an x64 image is loaded: MSI's download carries an
        /// x86 MysticLight_SDK.dll beside the x64 one.</summary>
        [Fact]
        public void OnlyAnX64Image_IsTaken()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pf-mlsdk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string Image(string name, ushort machine)
                {
                    var bytes = new byte[0x100];
                    bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
                    BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
                    bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
                    BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
                    string path = System.IO.Path.Combine(dir, name);
                    File.WriteAllBytes(path, bytes);
                    return path;
                }
                Assert.True(MysticLightNative.IsX64Library(Image("x64.dll", 0x8664)));
                Assert.False(MysticLightNative.IsX64Library(Image("x86.dll", 0x014C)));
                File.WriteAllBytes(System.IO.Path.Combine(dir, "text.dll"), new byte[] { 1, 2, 3 });
                Assert.False(MysticLightNative.IsX64Library(System.IO.Path.Combine(dir, "text.dll")));
                Assert.False(MysticLightNative.IsX64Library(System.IO.Path.Combine(dir, "missing.dll")));
            }
            finally { Directory.Delete(dir, true); }
        }

        /// <summary>The library's names: MSI's 64-bit download name first,
        /// then RGB.NET's.</summary>
        [Fact]
        public void TheLibraryNames_AreMsisAndRgbNets()
            => Assert.Equal(new[] { "MysticLight_SDK_x64.dll", "MysticLight_SDK.dll" }, MysticLightNative.LibraryNames);

        [Fact]
        public void TheProductionSeam_LoadsNothingInTheTestHost()
        {
            var native = new MysticLightNative();
            Assert.False(native.TryLoad(out string detail));
            Assert.Contains("test host", detail);
            Assert.Equal(0, native.Release());
            native.Unload();
        }
    }
}
