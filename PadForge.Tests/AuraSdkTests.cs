using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The ASUS Aura SDK route (#557 item 11): the key table against
    /// RGB.NET's mapping and the Logitech LED SDK's scan code table, the
    /// paths each Aura type is lit through, the COM interfaces against the
    /// type library import, and the worker against a scripted SDK.
    /// </summary>
    public class AuraLightsTests
    {
        // ── The key table ──

        /// <summary>Codes and usages written out from RGB.NET.Devices.Asus
        /// (Enum/AsusLedId.cs, Keyboard/AsusKeyboardLedMapping.cs) and the
        /// HID Usage Tables 1.6 Keyboard/Keypad page, never from the
        /// generator.</summary>
        [Theory]
        [InlineData(0x01, 0x29)] // KEY_ESCAPE, Keyboard_Escape
        [InlineData(0x02, 0x1E)] // KEY_1
        [InlineData(0x0B, 0x27)] // KEY_0
        [InlineData(0x1E, 0x04)] // KEY_A
        [InlineData(0x2C, 0x1D)] // KEY_Z
        [InlineData(0x1C, 0x28)] // KEY_RETURN, Enter
        [InlineData(0x2B, 0x31)] // KEY_BACKSLASH
        [InlineData(0x29, 0x35)] // KEY_GRAVE
        [InlineData(0x3A, 0x39)] // KEY_CAPITAL
        [InlineData(0x57, 0x44)] // KEY_F11
        [InlineData(0x58, 0x45)] // KEY_F12
        [InlineData(0x9C, 0x58)] // KEY_NUMPADENTER, E0 1C
        [InlineData(0x9D, 0xE4)] // KEY_RCONTROL, E0 1D
        [InlineData(0xB5, 0x54)] // KEY_DIVIDE, E0 35
        [InlineData(0xB7, 0x46)] // KEY_SYSRQ, Print Screen
        [InlineData(0xB8, 0xE6)] // KEY_RMENU, right Alt
        [InlineData(0xC5, 0x48)] // KEY_PAUSE
        [InlineData(0xC8, 0x52)] // KEY_UP, E0 48
        [InlineData(0xCB, 0x50)] // KEY_LEFT
        [InlineData(0xCD, 0x4F)] // KEY_RIGHT
        [InlineData(0xD0, 0x51)] // KEY_DOWN
        [InlineData(0xD3, 0x4C)] // KEY_DELETE
        [InlineData(0xDB, 0xE3)] // KEY_LWIN
        [InlineData(0xDD, 0x65)] // KEY_APPS
        [InlineData(0x59, 0x64)] // UNDOCUMENTED_1, RGB.NET's Keyboard_NonUsBackslash
        [InlineData(0x56, 0x32)] // UNDOCUMENTED_2, RGB.NET's Keyboard_NonUsTilde
        [InlineData(0xB3, 0x85)] // KEY_NUMPADCOMMA, Keyboard_NumComma
        [InlineData(0x64, 0x68)] // KEY_F13
        [InlineData(0x7D, 0x89)] // KEY_YEN, International3
        [InlineData(0x70, 0x88)] // KEY_KANA, International2
        public void AKeyCode_LightsTheKeyRgbNetMapsItTo(int code, int usage)
        {
            var key = AuraLights.KeyboardKeys.Single(k => k.Code == code);
            Assert.Equal(LightKind.Key, key.Kind);
            Assert.Equal(usage, key.Usage);
            var light = AuraLights.KeyboardMap.Lights.Single(l => l.Address == code);
            Assert.Equal((ushort)usage, light.Usage);
            Assert.Equal(LightKeySource.Vendor, light.KeySource);
        }

        /// <summary>The lights RGB.NET maps to an LED that is no key:
        /// Keyboard_Function at 0x100, Logo at 0x101, the media keys.</summary>
        [Fact]
        public void TheLightsThatAreNoKey_KeepTheirKindAndLabel()
        {
            var fn = AuraLights.KeyboardMap.Lights.Single(l => l.Address == 0x100);
            Assert.Equal(LightKind.Other, fn.Kind);
            Assert.Equal(LightLabel.Fn, fn.Label);
            Assert.Equal(0, fn.Usage);
            Assert.Equal(LightKind.Logo, AuraLights.KeyboardMap.Lights.Single(l => l.Address == 0x101).Kind);
            Assert.Equal(LightLabel.Mute, AuraLights.KeyboardMap.Lights.Single(l => l.Address == 0xA0).Label);
            Assert.Equal(LightLabel.PlayPause, AuraLights.KeyboardMap.Lights.Single(l => l.Address == 0xA2).Label);
            Assert.Equal(LightLabel.Stop, AuraLights.KeyboardMap.Lights.Single(l => l.Address == 0xA4).Label);
            Assert.Equal(LightLabel.Next, AuraLights.KeyboardMap.Lights.Single(l => l.Address == 0x99).Label);
            // Keyboard_MediaVolumeDown and Keyboard_MediaVolumeUp (AsusKeyboardLedMapping.cs:127-128).
            Assert.Equal(LightLabel.VolumeDown, AuraLights.KeyboardMap.Lights.Single(l => l.Address == 0xAE).Label);
            Assert.Equal(LightLabel.VolumeUp, AuraLights.KeyboardMap.Lights.Single(l => l.Address == 0xB0).Label);
            // RGB.NET maps 150 LEDs, one per code, every usage once.
            Assert.Equal(150, AuraLights.KeyboardKeys.Length);
            Assert.Equal(150, AuraLights.KeyboardKeys.Select(k => k.Code).Distinct().Count());
            var usages = AuraLights.KeyboardKeys.Where(k => k.Usage != 0).Select(k => k.Usage).ToList();
            Assert.Equal(usages.Count, usages.Distinct().Count());
        }

        /// <summary>The Aura code is a DirectInput scan code, 0x80 plus the
        /// second byte for an E0-prefixed set 1 key. The Logitech LED SDK's
        /// KeyName is the same set 1 code with 0x100 for the prefix
        /// (LogitechLEDLib.h:31-149, in SdkKeyTables.g.cs), so every key both
        /// tables hold must land on one usage.</summary>
        [Fact]
        public void EveryKeyTheLogitechTableHoldsToo_HasTheSameUsage()
        {
            var logitech = LogiLedKeys.Keys.Where(k => k.Usage != 0).ToDictionary(k => k.Code, k => k.Usage);
            int compared = 0;
            foreach (var key in AuraLights.KeyboardKeys.Where(k => k.Usage != 0))
            {
                int keyName = key.Code < 0x80 ? key.Code : 0x100 | (key.Code & 0x7F);
                if (!logitech.TryGetValue(keyName, out ushort usage)) continue;
                Assert.True(usage == key.Usage, $"{key.Name} 0x{key.Code:X}: Aura 0x{key.Usage:X2}, Logitech 0x{usage:X2}");
                compared++;
            }
            Assert.True(compared >= 104, "compared " + compared);
        }

        [Fact]
        public void TheGeneratedTable_IsReadAtThePinnedRgbNetCommit()
            => Assert.Equal("3c5af72b25c77971c21ccf96c13c41c62c8212f6", AuraLights.RgbNetCommit);

        // ── Types and paths ──

        /// <summary>The device classes RGB.NET's provider gives each
        /// AsusDeviceType (AsusDeviceProvider.cs:78-92, Enum/AsusDeviceType.cs).</summary>
        [Theory]
        [InlineData(0x80000u, "keyboard", true)]   // KEYBOARD_RGB
        [InlineData(0x80001u, "keyboard", false)]  // KEYBOARD_5ZONE_RGB
        [InlineData(0x81000u, "keyboard", true)]   // NB_KB_RGB
        [InlineData(0x81001u, "keyboard", false)]  // NB_KB_4ZONE_RGB
        [InlineData(0x90000u, "mouse", false)]     // MOUSE_RGB
        [InlineData(0x40000u, "headset", false)]   // HEADSET_RGB
        [InlineData(0x10000u, "mainboard", false)] // MB_RGB
        [InlineData(0x11000u, "strip", false)]     // MB_ADDRESABLE
        [InlineData(0x20000u, "vga", false)]       // VGA_RGB
        [InlineData(0x70000u, "dram", false)]      // DRAM_RGB
        [InlineData(0xE0000u, "other", false)]     // TERMINAL_RGB
        [InlineData(0xD1000u, "other", false)]     // WATERCOOLER_RGB
        [InlineData(0x12000u, "other", false)]     // DESKTOP_RGB
        [InlineData(0u, "other", false)]
        public void AnAuraType_IsLitThroughItsPath(uint type, string path, bool perKey)
        {
            Assert.Equal(path, AuraLights.CategoryOf(type));
            Assert.Equal(perKey, AuraLights.PerKey(type));
            Assert.Contains(path, AuraLights.Categories);
        }

        [Fact]
        public void APathsLights_AreTheLongestLightsListAmongItsDevices()
        {
            var devices = new[]
            {
                new AuraDevice(0, AuraLights.DramRgb, "DRAM 1", 8, Array.Empty<ushort>()),
                new AuraDevice(1, AuraLights.DramRgb, "DRAM 2", 5, Array.Empty<ushort>()),
                new AuraDevice(2, AuraLights.MbRgb, "Mainboard", 1, Array.Empty<ushort>()),
                new AuraDevice(3, AuraLights.KeyboardRgb, "Claymore", 6, new ushort[] { 0x1E }),
                new AuraDevice(4, AuraLights.NotebookKeyboard4ZoneRgb, "TUF", 4, Array.Empty<ushort>()),
            };
            var dram = AuraLights.MapFor("dram", devices);
            Assert.Equal(8, dram.Lights.Count);
            Assert.All(dram.Lights, l => Assert.Equal(LightKind.Zone, l.Kind));
            Assert.Equal(Enumerable.Range(0, 8), dram.Lights.Select(l => l.Address));
            Assert.Equal(Enumerable.Range(1, 8), dram.Lights.Select(l => l.Zone));
            // One light lights one color.
            Assert.Null(AuraLights.MapFor("mainboard", devices));
            Assert.Null(AuraLights.MapFor("vga", devices));
            // The keyboard path: every key, and the zone keyboard's four
            // lights. The per-key board's own Lights list adds no zone.
            var keyboard = AuraLights.MapFor("keyboard", devices);
            Assert.Equal(AuraLights.KeyboardKeys.Length + 4, keyboard.Lights.Count);
            Assert.Equal(4, keyboard.ZoneCount);
            Assert.Equal(AuraLights.ZoneBase, keyboard.Lights.First(l => l.Kind == LightKind.Zone).Address);
            Assert.Same(AuraLights.KeyboardMap, AuraLights.MapFor("keyboard", devices.Take(3).ToArray()));
        }

        /// <summary>The lights RGB.NET adds to two ROG laptops' per-key boards
        /// by the device's name, written out from
        /// RGB.NET.Devices.Asus Keyboard/AsusKeyboardLedMapping.cs:177-189 and
        /// 203-235 at the pinned commit: the bars under wide keys, which light
        /// with their key, and on the Strix its other lights. A name that
        /// matches neither table adds nothing.</summary>
        [Theory]
        [InlineData("ROG Zephyrus Duo 15 GX551", new[] { 39, 40, 55, 57, 97, 99, 118, 120, 130, 131, 133 })]
        [InlineData("ROG Strix G513QM", new[]
        {
            2, 3, 4, 5, 6, 55, 57, 58, 79, 97, 99, 100, 118, 120, 121, 130, 131, 133, 169, 170, 171, 172, 173, 174,
        })]
        public void TwoRogLaptops_CarryTheirExtraLights(string name, int[] indexes)
        {
            var devices = new[] { new AuraDevice(0, AuraLights.NotebookKeyboardRgb, name, 200, new ushort[] { 0x1E }) };
            var map = AuraLights.MapFor("keyboard", devices);
            var extra = map.Lights.Where(l => l.Address >= AuraLights.ExtraBase).ToList();
            Assert.Equal(indexes, extra.Select(l => l.Address - AuraLights.ExtraBase));
            Assert.Equal(AuraLights.KeyboardKeys.Length + indexes.Length, map.Lights.Count);
            ushort UsageAt(int index) => extra.Single(l => l.Address == AuraLights.ExtraBase + index).Usage;
            Assert.Equal((ushort)0x2A, UsageAt(55));
            Assert.Equal((ushort)0x2A, UsageAt(57));
            Assert.Equal((ushort)0x28, UsageAt(97));
            Assert.Equal((ushort)0xE5, UsageAt(118));
            Assert.Equal((ushort)0x2C, UsageAt(133));
            Assert.Same(AuraLights.KeyboardMap, AuraLights.MapFor("keyboard",
                new[] { new AuraDevice(0, AuraLights.NotebookKeyboardRgb, "NotebookKeyboard", 200, new ushort[] { 0x1E }) }));
        }

        [Fact]
        public void TheStrixsOtherLights_KeepWhatRgbNetNamesThem()
        {
            var map = AuraLights.MapFor("keyboard",
                new[] { new AuraDevice(0, AuraLights.NotebookKeyboardRgb, "ROG Strix G513QM", 200, new ushort[] { 0x1E }) });
            LightInfo At(int index) => map.Lights.Single(l => l.Address == AuraLights.ExtraBase + index);
            Assert.Equal(LightKind.Logo, At(6).Kind);
            Assert.Equal(LightKind.Edge, At(174).Kind);
            Assert.Equal((LightKind.Other, LightLabel.Fan), (At(5).Kind, At(5).Label));
            Assert.Equal((LightKind.Other, LightLabel.VolumeDown), (At(2).Kind, At(2).Label));
            Assert.Equal((LightKind.Other, LightLabel.VolumeUp), (At(3).Kind, At(3).Label));
            Assert.Equal((LightKind.Other, LightLabel.PlayPause), (At(58).Kind, At(58).Label));
            Assert.Equal((LightKind.Other, LightLabel.Stop), (At(79).Kind, At(79).Label));
            Assert.Equal((LightKind.Other, LightLabel.Previous), (At(100).Kind, At(100).Label));
            Assert.Equal((LightKind.Other, LightLabel.Next), (At(121).Kind, At(121).Label));
        }

        // ── The COM interfaces ──

        /// <summary>The IIDs, the class ID and the method order of
        /// Interop.AuraServiceLib.dll (SHA-256 B82FC09C363FF94B...), read
        /// from its metadata and written out here.</summary>
        [Theory]
        [InlineData("IAuraSdk2", "EE69DBAE-33FF-4E45-B378-01797A59852D", "Enumerate,SwitchMode,ReleaseControl")]
        [InlineData("IAuraSyncDeviceCollection", "87FC56AB-99CA-4FD3-B561-2EEDD719DA57", "GetEnumerator,get_Count,get_Item")]
        [InlineData("IAuraSyncDevice", "6A30D789-F5DA-4F26-BF09-6DFB9DEDF91E", "get_Lights,get_Type,get_Name,get_Width,get_Height,Apply")]
        [InlineData("IAuraSyncKeyboard", "87B03DD6-59E6-44F5-8772-56A7494F9900", "get_Lights,get_Type,get_Name,get_Width,get_Height,Apply,get_Key,get_Keys")]
        [InlineData("IAuraRgbLightCollection", "003065BC-B562-454E-ADCB-A9B70042D486", "GetEnumerator,get_Count,get_Item")]
        [InlineData("IAuraRgbLight", "9AF6260E-4311-417D-B3EF-B85A34CF3244", "get_Red,set_Red,get_Green,set_Green,get_Blue,set_Blue,get_Name,get_Color,set_Color")]
        [InlineData("IAuraRgbKeyCollection", "81C61F82-4DA0-4081-BDDE-108E1FCDDA7F", "GetEnumerator,get_Count,get_Item")]
        [InlineData("IAuraRgbKey", "95E6C828-6DE4-441E-873A-C44AE850F3A2", "get_Red,set_Red,get_Green,set_Green,get_Blue,set_Blue,get_Name,get_Color,set_Color,get_Code,get_X,get_Y")]
        public void EachInterface_HasTheImportsIidAndMethodOrder(string name, string iid, string methods)
        {
            var type = typeof(AuraSdkNative).GetNestedType(name, BindingFlags.NonPublic);
            Assert.NotNull(type);
            Assert.True(type.IsInterface && type.IsImport);
            Assert.Equal(new Guid(iid), type.GUID);
            Assert.Equal(ComInterfaceType.InterfaceIsDual, type.GetCustomAttribute<InterfaceTypeAttribute>().Value);
            var order = type.GetMethods().OrderBy(m => m.MetadataToken).Select(m => m.Name);
            Assert.Equal(methods.Split(','), order);
        }

        [Fact]
        public void TheClass_IsAuraServiceLibsAuraSdk()
            => Assert.Equal(new Guid("05921124-5057-483E-A037-E9497B523590"), AuraSdkNative.AuraSdkClsid);

        /// <summary>The test host opens no vendor SDK: the production seam
        /// answers as on a PC without Armoury Crate.</summary>
        [Fact]
        public void TheProductionSeam_OpensNothingInTheTestHost()
        {
            var native = new AuraSdkNative();
            Assert.False(native.Open(out string detail));
            Assert.Contains("test host", detail);
            Assert.Empty(native.Devices);
            native.Close();
        }
    }

    /// <summary>The Aura SDK worker against a scripted SDK.</summary>
    [Collection("PeripheralOutputStatics")]
    public class AuraSdkBackendTests : IDisposable
    {
        private static readonly Guid Keyboard = new("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid Ram = new("00000000-0000-0000-0000-0000000000c3");

        public AuraSdkBackendTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        internal sealed class FakeAura : IAuraSdkNative
        {
            private readonly object _lock = new();
            private readonly List<string> _calls = new();
            public volatile bool OpenOk = true;
            public volatile bool FailWrites;
            public IReadOnlyList<AuraDevice> Script = new[]
            {
                new AuraDevice(0, AuraLights.KeyboardRgb, "ROG Strix Scope", 0, new ushort[] { 0x1E, 0x11, 0xC8, 0x101, 0x0777 }),
                new AuraDevice(1, AuraLights.DramRgb, "DRAM", 3, Array.Empty<ushort>()),
            };
            private IReadOnlyList<AuraDevice> _devices = Array.Empty<AuraDevice>();

            private readonly List<long> _times = new();
            private void Log(string s) { lock (_lock) { _calls.Add(s); _times.Add(System.Diagnostics.Stopwatch.GetTimestamp()); } }
            public string[] Calls { get { lock (_lock) return _calls.ToArray(); } }
            public int Count(string call) => Calls.Count(c => c == call);
            /// <summary>When each call of that name arrived, in order, as
            /// Stopwatch timestamps.</summary>
            public long[] TimesOf(string call)
            {
                lock (_lock)
                {
                    var hits = new List<long>();
                    for (int i = 0; i < _calls.Count; i++)
                        if (_calls[i] == call) hits.Add(_times[i]);
                    return hits.ToArray();
                }
            }
            public void Clear() { lock (_lock) { _calls.Clear(); _times.Clear(); } }

            /// <summary>Set: the next light holds until it is set, and
            /// <see cref="Held"/> says it is there, on <see cref="HeldThread"/>.</summary>
            public ManualResetEventSlim Hold;
            public readonly ManualResetEventSlim Held = new(false);
            public Thread HeldThread;

            public IReadOnlyList<AuraDevice> Devices => _devices;
            /// <summary>Set: the next open holds until it is set, and
            /// <see cref="OpenHeld"/> says it is there, on <see cref="HeldThread"/>.</summary>
            public ManualResetEventSlim HoldOpen;
            public readonly ManualResetEventSlim OpenHeld = new(false);

            public bool Open(out string detail)
            {
                Log("open");
                detail = "fake";
                var hold = Interlocked.Exchange(ref HoldOpen, null);
                if (hold != null)
                {
                    HeldThread = Thread.CurrentThread;
                    OpenHeld.Set();
                    hold.Wait(30000);
                }
                if (!OpenOk) return false;
                _devices = Script;
                return true;
            }
            public volatile bool HeldCallFails;

            private void HoldHere()
            {
                var hold = Interlocked.Exchange(ref Hold, null);
                if (hold == null) return;
                HeldThread = Thread.CurrentThread;
                Held.Set();
                hold.Wait(30000);
                if (HeldCallFails) throw new COMException("server gone", unchecked((int)0x800706BA));
            }
            public void SetLight(int device, int index, int rgb)
            {
                HoldHere();
                Log($"light:{device}:{index}:{rgb:X6}");
                if (FailWrites) throw new COMException("server gone", unchecked((int)0x800706BA));
            }
            public void SetKey(int device, ushort code, int rgb)
            {
                HoldHere();
                Log($"key:{device}:{code:X}:{rgb:X6}");
                if (FailWrites) throw new COMException("server gone", unchecked((int)0x800706BA));
            }
            public void Apply(int device) => Log("apply:" + device);
            public void Close() { Log("close"); _devices = Array.Empty<AuraDevice>(); }
        }

        private static AuraSdkBackend Backend(FakeAura native) => new(native, pollMs: 10, retryMs: 100, stopWaitMs: 2000);

        private static OutputPath Path(string key) => new(OutputFamily.AuraSdkType, key);

        private static void Link(params DeviceLinks[] rows) => PeripheralOutputs.PublishLinks(new LinkTable(rows));

        private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000) => SpinWait.SpinUntil(condition, timeoutMs);

        private static PeripheralPresence Presence(bool installed = true, bool running = true)
            => new PeripheralPresence(false, false, false, false, false)
            {
                AsusAuraSdk = installed,
                Programs = RunningPrograms.Of(running ? new[] { "LightingService" } : new[] { "explorer" }, VendorPrograms.All),
            };

        [Fact]
        public void NothingClaimed_TheSdkIsNeverOpened()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            PeripheralOutputs.SetBackendState(OutputFamily.AuraSdkType, BackendState.Waiting);
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var backend = Backend(native);
            backend.Start();
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.AuraSdkType) == BackendState.Idle));
            Thread.Sleep(80);
            Assert.Empty(native.Calls);
            // The keyboard path's keys are published from the start.
            Assert.Same(AuraLights.KeyboardMap, PeripheralLightMaps.For(Path("keyboard")));
        }

        /// <summary>Claimed, but Armoury Crate does not run, or the SDK is
        /// not installed: the worker waits and opens nothing, as Aurora
        /// refuses without LightingService.exe (AsusDevice.cs:17-21).</summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void WithoutTheProgramOrTheSdk_NothingIsOpened(bool installed, bool running)
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence(installed, running);
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            using var backend = Backend(native);
            backend.Start();
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.AuraSdkType) == BackendState.Waiting));
            Thread.Sleep(80);
            Assert.Empty(native.Calls);
        }

        /// <summary>A claim opens the session and lights every key of the
        /// keyboard in its color through Key[code], a code the table lacks
        /// included, then one Apply. The DRAM path, unclaimed, is not
        /// touched. The same color again sends nothing.</summary>
        [Fact]
        public void AClaim_LightsEveryKeyOfItsKind_AndAppliesOnce()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1));
            Thread.Sleep(60);
            Assert.Equal(new[]
            {
                "open",
                "key:0:1E:112233", "key:0:11:112233", "key:0:C8:112233", "key:0:101:112233", "key:0:777:112233",
                "apply:0",
            }, native.Calls);
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.AuraSdkType) == BackendState.Connected));
        }

        /// <summary>Key colors light the keys they name, by HID usage, over
        /// the background. A later change sends only the key that changed,
        /// then one Apply.</summary>
        [Fact]
        public void KeyColors_LightTheirKeys_AndOnlyChangedKeysGoOutAgain()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var backend = Backend(native);
            backend.Start();
            // A (usage 0x04) red, W (0x1A) green, the logo group blue.
            var frame = new KeyFrame(new Dictionary<ushort, int> { [0x04] = 0xFF0000, [0x1A] = 0x00FF00 },
                new Dictionary<string, int> { [LightGroups.Logo] = 0x0000FF }, null, null);
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x10, 0x10, 0x10, keys: frame);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1));
            var calls = native.Calls;
            Assert.Contains("key:0:1E:FF0000", calls);
            Assert.Contains("key:0:11:00FF00", calls);
            Assert.Contains("key:0:101:0000FF", calls);
            Assert.Contains("key:0:C8:101010", calls);
            Assert.Contains("key:0:777:101010", calls);

            native.Clear();
            var next = new KeyFrame(new Dictionary<ushort, int> { [0x04] = 0xFF0000, [0x1A] = 0xFFFF00 },
                new Dictionary<string, int> { [LightGroups.Logo] = 0x0000FF }, null, null);
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x10, 0x10, 0x10, keys: next);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1));
            Thread.Sleep(40);
            Assert.Equal(new[] { "key:0:11:FFFF00", "apply:0" }, native.Calls);
        }

        /// <summary>A path lit light by light: the session's map numbers the
        /// DRAM's three lights, a zone color reaches its light, and each
        /// light is set through the Lights list before one Apply.</summary>
        [Fact]
        public void AnotherKind_IsLitLightByLight_OnTheMapTheSessionRead()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Ram, Lighting = new[] { Path("dram") } });
            using var backend = Backend(native);
            backend.Start();
            Assert.Null(PeripheralLightMaps.For(Path("dram")));
            var frame = new KeyFrame(null, new Dictionary<string, int> { [LightGroups.Zone(2)] = 0x00FF00 }, null, null);
            PeripheralOutputs.SetLighting(Ram, slot: 0, player: 1, 0xFF, 0x00, 0x00, keys: frame);
            // The map comes with the session, ahead of its first picture.
            Assert.True(WaitFor(() => native.Count("apply:1") == 1));
            Assert.Equal(3, PeripheralLightMaps.For(Path("dram"))?.Lights.Count);
            var calls = native.Calls;
            Assert.Contains("light:1:1:00FF00", calls);
            Assert.Contains("light:1:0:FF0000", calls);
            Assert.Contains("light:1:2:FF0000", calls);
            Assert.DoesNotContain(calls, c => c.StartsWith("key:"));
        }

        /// <summary>Two devices on one path: a light only the longer one has
        /// changes, so only that device is set and sent with Apply.</summary>
        [Fact]
        public void OnlyTheDeviceWhoseLightsChanged_IsApplied()
        {
            var native = new FakeAura();
            native.Script = new[]
            {
                new AuraDevice(0, AuraLights.DramRgb, "DRAM 1", 3, Array.Empty<ushort>()),
                new AuraDevice(1, AuraLights.DramRgb, "DRAM 2", 1, Array.Empty<ushort>()),
            };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Ram, Lighting = new[] { Path("dram") } });
            using var backend = Backend(native);
            backend.Start();
            var frame = new KeyFrame(null, new Dictionary<string, int> { [LightGroups.Zone(3)] = 0x00FF00 }, null, null);
            PeripheralOutputs.SetLighting(Ram, slot: 0, player: 1, 0xFF, 0x00, 0x00, keys: frame);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1 && native.Count("apply:1") == 1));
            native.Clear();
            var next = new KeyFrame(null, new Dictionary<string, int> { [LightGroups.Zone(3)] = 0x0000FF }, null, null);
            PeripheralOutputs.SetLighting(Ram, slot: 0, player: 1, 0xFF, 0x00, 0x00, keys: next);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1));
            Thread.Sleep(40);
            Assert.Equal(new[] { "light:0:2:0000FF", "apply:0" }, native.Calls);
        }

        /// <summary>A keyboard lit in zones (NB_KB_4ZONE_RGB) is lit through
        /// its Lights list, as RGB.NET lights it (AsusKeyboardRGBDevice.cs:93-98
        /// and AsusUpdateQueue.cs:76-87): its four lights are the keyboard
        /// path's four zones, beside the keys of a per-key board.</summary>
        [Fact]
        public void AZoneKeyboard_IsLitThroughItsLights_AsTheKeyboardPathsZones()
        {
            var native = new FakeAura();
            native.Script = new[]
            {
                new AuraDevice(0, AuraLights.NotebookKeyboard4ZoneRgb, "TUF", 4, Array.Empty<ushort>()),
                new AuraDevice(1, AuraLights.KeyboardRgb, "Strix", 0, new ushort[] { 0x1E }),
            };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var backend = Backend(native);
            backend.Start();
            var frame = new KeyFrame(new Dictionary<ushort, int> { [0x04] = 0xFF0000 },
                new Dictionary<string, int> { [LightGroups.Zone(3)] = 0x00FF00 }, null, null);
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0x00, 0xFF, keys: frame);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1 && native.Count("apply:1") == 1));
            var calls = native.Calls;
            Assert.Equal(new[] { "light:0:0:0000FF", "light:0:1:0000FF", "light:0:2:00FF00", "light:0:3:0000FF" },
                calls.Where(c => c.StartsWith("light:")));
            Assert.Equal(new[] { "key:1:1E:FF0000" }, calls.Where(c => c.StartsWith("key:")));
            var map = PeripheralLightMaps.For(Path("keyboard"));
            Assert.Equal(4, map.ZoneCount);
            Assert.Equal(AuraLights.KeyboardKeys.Length + 4, map.Lights.Count);
        }

        /// <summary>The last claim going hands every device back with
        /// ReleaseControl (the seam's Close), and the worker goes idle.</summary>
        [Fact]
        public void TheLastReleaseClosesTheSession()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1));
            PeripheralOutputs.ReleaseLighting(Keyboard, 0);
            Assert.True(WaitFor(() => native.Count("close") == 1));
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.AuraSdkType) == BackendState.Idle));
            Assert.Equal(1, native.Count("open"));
        }

        /// <summary>A board with extra lights takes them through its Lights
        /// list, beside its keys, as RGB.NET's update queue writes
        /// AsusLedType.Light on a per-key board (AsusUpdateQueue.cs:50-73). A
        /// key's color reaches the bars under that key.</summary>
        [Fact]
        public void AnExtraLight_IsSetThroughTheLightsList_WithItsKey()
        {
            var native = new FakeAura();
            native.Script = new[]
            {
                new AuraDevice(0, AuraLights.NotebookKeyboardRgb, "ROG Zephyrus Duo 15", 134, new ushort[] { 0x1E }),
            };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var backend = Backend(native);
            backend.Start();
            var frame = new KeyFrame(new Dictionary<ushort, int> { [0x2C] = 0xFF0000 }, null, null, null);
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0x00, 0xFF, keys: frame);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1));
            var calls = native.Calls;
            Assert.Contains("key:0:1E:0000FF", calls);
            Assert.Contains("light:0:39:0000FF", calls);
            Assert.Contains("light:0:130:FF0000", calls);
            Assert.Contains("light:0:133:FF0000", calls);
            Assert.Equal(11, calls.Count(c => c.StartsWith("light:")));
        }

        /// <summary>A list shorter than a table's index leaves that light out,
        /// since the SDK has no such light to set.</summary>
        [Fact]
        public void AnExtraLightPastTheLightsList_IsLeftOut()
        {
            var native = new FakeAura();
            native.Script = new[]
            {
                new AuraDevice(0, AuraLights.NotebookKeyboardRgb, "ROG Zephyrus Duo 15", 56, new ushort[] { 0x1E }),
            };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0x00, 0xFF);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1));
            Assert.Equal(new[] { "light:0:39:0000FF", "light:0:40:0000FF", "light:0:55:0000FF" },
                native.Calls.Where(c => c.StartsWith("light:")));
        }

        /// <summary>Armoury Crate runs and the SDK will not open: the tab says
        /// so and not "Waiting for". Armoury Crate not running reads as ever.
        /// A session that opens clears it.</summary>
        [Fact]
        public void AnOpenRefused_WhileTheProgramRuns_IsARefusal_AndASessionClearsIt()
        {
            var native = new FakeAura { OpenOk = false };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var backend = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 2000);
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => PeripheralOutputs.RefusalOn(Path("keyboard")) != null), "the refusal was recorded");
            Assert.Equal(BackendState.Waiting, PeripheralOutputs.StateOf(OutputFamily.AuraSdkType));
            Assert.NotNull(PeripheralOutputs.RefusalOn(Path("dram")));
            Assert.False(PeripheralOutputs.RefusalOn(Path("keyboard")).SettingOff);

            PeripheralOutputs.Presence = Presence(running: false);
            Assert.True(WaitFor(() => PeripheralOutputs.RefusalOn(Path("keyboard")) == null), "the program is gone, so it refused nothing");

            PeripheralOutputs.Presence = Presence();
            Assert.True(WaitFor(() => PeripheralOutputs.RefusalOn(Path("keyboard")) != null));
            native.OpenOk = true;
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.AuraSdkType) == BackendState.Connected));
            Assert.Null(PeripheralOutputs.RefusalOn(Path("keyboard")));
        }

        /// <summary>A worker a stop left inside the SDK is waited for by the next
        /// engine start's worker, on a backend of its own, so two never call the
        /// SDK at once.</summary>
        [Fact]
        public void TheNextBackend_WaitsForAWorkerLeftInsideTheSdk()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var first = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 50);
            first.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => native.Calls.Any(c => c.StartsWith("key:", StringComparison.Ordinal))), "the first worker painted");
            using var gate = new ManualResetEventSlim(false);
            native.Hold = gate;
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x44, 0x55, 0x66);
            Assert.True(native.Held.Wait(30000), "the worker is inside the SDK");
            first.Dispose();

            using var next = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 50);
            try
            {
                next.Start();
                Thread.Sleep(300);
                Assert.Equal(1, native.Count("open"));
            }
            finally
            {
                gate.Set();
            }
            Assert.True(WaitFor(() => native.Count("open") == 2), "the next worker opened the SDK");
            var calls = native.Calls;
            Assert.True(Array.IndexOf(calls, "close") < Array.LastIndexOf(calls, "open"), string.Join(", ", calls));
        }

        /// <summary>A worker the next one gave up waiting for still ends its own
        /// session when that one opens none: nothing else would give the
        /// devices back.</summary>
        [Fact]
        public void AWorkerTheNextOneGaveUpOn_EndsItsSession_WhenThatOneOpensNone()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var first = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 50, predecessorWaitMs: 50);
            first.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => native.Calls.Any(c => c.StartsWith("key:", StringComparison.Ordinal))), "the first worker painted");
            using var gate = new ManualResetEventSlim(false);
            native.Hold = gate;
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x44, 0x55, 0x66);
            Assert.True(native.Held.Wait(30000), "the worker is inside the SDK");
            first.Dispose();

            Link(new DeviceLinks { Device = Keyboard, Lighting = Array.Empty<OutputPath>() });
            using var next = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 50, predecessorWaitMs: 50);
            try
            {
                next.Start();
                Thread.Sleep(200);
            }
            finally
            {
                gate.Set();
            }
            Assert.True(native.HeldThread.Join(30000), "the first worker ended");
            Assert.Equal(1, native.Count("open"));
            Assert.Equal(1, native.Count("close"));
        }

        /// <summary>The same when the next one tried and the SDK refused it:
        /// that worker has no session to hand back.</summary>
        [Fact]
        public void AWorkerTheNextOneGaveUpOn_EndsItsSession_WhenThatOnesOpenFails()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var first = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 50, predecessorWaitMs: 50);
            first.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => native.Calls.Any(c => c.StartsWith("key:", StringComparison.Ordinal))), "the first worker painted");
            using var gate = new ManualResetEventSlim(false);
            native.Hold = gate;
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x44, 0x55, 0x66);
            Assert.True(native.Held.Wait(30000), "the worker is inside the SDK");
            first.Dispose();

            native.OpenOk = false;
            using var next = new AuraSdkBackend(native, pollMs: 10, retryMs: 60000, stopWaitMs: 50, predecessorWaitMs: 50);
            try
            {
                next.Start();
                Assert.True(WaitFor(() => native.Count("open") == 2), "the next worker tried the SDK");
            }
            finally
            {
                gate.Set();
            }
            Assert.True(native.HeldThread.Join(30000), "the first worker ended");
            Assert.Equal(1, native.Count("close"));
        }

        /// <summary>A worker the next one gave up waiting for comes out of the
        /// SDK and closes nothing: the SDK is the process's, and the next
        /// worker's session is on it. The same when the call it comes out of
        /// failed, which sends it down its path for a program that stopped
        /// answering.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AWorkerTheNextOneGaveUpOn_ClosesNothing(bool itsCallFails)
        {
            var native = new FakeAura { HeldCallFails = itsCallFails };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var first = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 50, predecessorWaitMs: 50);
            first.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => native.Calls.Any(c => c.StartsWith("key:", StringComparison.Ordinal))), "the first worker painted");
            using var gate = new ManualResetEventSlim(false);
            native.Hold = gate;
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x44, 0x55, 0x66);
            Assert.True(native.Held.Wait(30000), "the worker is inside the SDK");
            first.Dispose();

            using var next = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 50, predecessorWaitMs: 50);
            try
            {
                next.Start();
                Assert.True(WaitFor(() => native.Count("open") == 2), "the next worker opened the SDK");
            }
            finally
            {
                gate.Set();
            }
            Assert.True(native.HeldThread.Join(30000), "the first worker ended");
            Assert.Equal(0, native.Count("close"));
        }

        /// <summary>A worker a stop gave up on inside the SDK's open, with only an earlier
        /// run's session in the books, ends the session it comes back with when the next run
        /// opens none: nothing else would give the devices back.</summary>
        [Fact]
        public void AWorkerTheNextOneGaveUpOn_InItsOpen_EndsItsSession_WhenThatOneOpensNone()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            using (var earlier = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 2000, predecessorWaitMs: 50))
            {
                earlier.Start();
                Assert.True(WaitFor(() => native.Calls.Any(c => c.StartsWith("key:", StringComparison.Ordinal))), "the earlier run painted");
            }
            native.Clear();

            using var gate = new ManualResetEventSlim(false);
            native.HoldOpen = gate;
            using var first = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 50, predecessorWaitMs: 50);
            first.Start();
            Assert.True(native.OpenHeld.Wait(30000), "the worker is inside the SDK's open");
            first.Dispose();

            Link(new DeviceLinks { Device = Keyboard, Lighting = Array.Empty<OutputPath>() });
            using var next = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 50, predecessorWaitMs: 50);
            try
            {
                next.Start();
                Thread.Sleep(200);
            }
            finally
            {
                gate.Set();
            }
            Assert.True(native.HeldThread.Join(30000), "the first worker ended");
            Assert.Equal(1, native.Count("open"));
            Assert.Equal(1, native.Count("close"));
        }

        /// <summary>A worker a stop gave up on inside the SDK sets no more lights when it
        /// comes back, in each of the three loops of a paint. The laptop's one key leads to
        /// its extra lights, and the DRAM has zones.</summary>
        [Theory]
        [InlineData("keys")]
        [InlineData("extras")]
        [InlineData("zones")]
        public void AWorkerThatComesBackFromTheSdk_SetsNoMoreLights(string loop)
        {
            var native = new FakeAura();
            if (loop == "extras")
                native.Script = new[] { new AuraDevice(0, AuraLights.NotebookKeyboardRgb, "ROG Zephyrus Duo 15 GX551", 134, new ushort[] { 0x1E }) };
            bool zones = loop == "zones";
            var row = zones ? Ram : Keyboard;
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = row, Lighting = new[] { Path(zones ? "dram" : "keyboard") } });
            using var first = new AuraSdkBackend(native, pollMs: 10, retryMs: 50, stopWaitMs: 50, predecessorWaitMs: 50);
            first.Start();
            PeripheralOutputs.SetLighting(row, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => native.Calls.Any(c => c.EndsWith(":112233", StringComparison.Ordinal))), "the first worker painted");
            using var gate = new ManualResetEventSlim(false);
            native.Hold = gate;
            PeripheralOutputs.SetLighting(row, slot: 0, player: 1, 0x44, 0x55, 0x66);
            Assert.True(native.Held.Wait(30000), "the worker is inside the SDK");
            first.Dispose();
            native.Clear();

            gate.Set();
            Assert.True(native.HeldThread.Join(30000), "the first worker ended");
            Assert.True(native.Calls.Count(c => c.EndsWith(":445566", StringComparison.Ordinal)) <= 1, string.Join(" | ", native.Calls));
        }

        /// <summary>A write that fails with the program running reads the same
        /// until the next session opens, and a stop clears it.</summary>
        [Fact]
        public void AFailedWrite_IsARefusal_AndAStopClearsIt()
        {
            var native = new FakeAura { FailWrites = true };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            var backend = new AuraSdkBackend(native, pollMs: 10, retryMs: 60000, stopWaitMs: 2000);
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => PeripheralOutputs.RefusalOn(Path("keyboard")) != null), "the refusal was recorded");
            backend.Dispose();
            Assert.Null(PeripheralOutputs.RefusalOn(Path("keyboard")));
        }

        /// <summary>A kind nobody claims any more goes back to Armoury Crate
        /// though another stays claimed. The SDK hands every device back at
        /// once, so the session closes and opens again in the same pass, as
        /// ChromaBackend reopens on a change of categories, and only what
        /// stays claimed is painted.</summary>
        [Fact]
        public void AKindLetGo_WhileAnotherStays_ReopensTheSession_AndRepaintsWhatStays()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } },
                 new DeviceLinks { Device = Ram, Lighting = new[] { Path("dram") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            PeripheralOutputs.SetLighting(Ram, slot: 1, player: 2, 0x44, 0x55, 0x66);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1 && native.Count("apply:1") == 1));
            native.Clear();

            PeripheralOutputs.ReleaseLighting(Keyboard, 0);
            Assert.True(WaitFor(() => native.Count("apply:1") == 1));
            Thread.Sleep(40);
            Assert.Equal(new[] { "close", "open", "light:1:0:445566", "light:1:1:445566", "light:1:2:445566", "apply:1" }, native.Calls);
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.AuraSdkType) == BackendState.Connected));
        }

        /// <summary>Armoury Crate stopping ends the session, and its start
        /// again opens a new one that paints the whole picture.</summary>
        [Fact]
        public void TheProgramStopping_EndsTheSession_AndItsReturnOpensANewOne()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1));
            PeripheralOutputs.Presence = Presence(running: false);
            Assert.True(WaitFor(() => native.Count("close") == 1));
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.AuraSdkType) == BackendState.Waiting));
            PeripheralOutputs.Presence = Presence();
            Assert.True(WaitFor(() => native.Count("apply:0") == 2));
            Assert.Equal(2, native.Count("open"));
        }

        /// <summary>A write that fails ends the session, and a new one is
        /// opened only after the retry interval.</summary>
        [Fact]
        public void AFailedWrite_EndsTheSession_AndRetriesLater()
        {
            var native = new FakeAura { FailWrites = true };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => native.Count("close") >= 1));
            Assert.Equal(0, native.Count("apply:0"));
            native.FailWrites = false;
            Assert.True(WaitFor(() => native.Count("apply:0") == 1));
            Assert.True(native.Count("open") >= 2);
        }

        /// <summary>An SDK that cannot be opened is tried again after the
        /// retry interval, not on every poll.</summary>
        [Fact]
        public void AnOpenThatFails_IsRetriedOnItsInterval()
        {
            var native = new FakeAura { OpenOk = false };
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            using var backend = new AuraSdkBackend(native, pollMs: 10, retryMs: 400, stopWaitMs: 2000);
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => native.Count("open") >= 2, 30000), "the open was never tried again");
            // The interval is read off the worker's own two calls. The test
            // used to sleep 200 ms and then count one open, which fails
            // whenever this thread sleeps past the 400 ms interval. The
            // backend times the retry on a 16 ms clock, so the floor sits a
            // little under 400.
            long[] opens = native.TimesOf("open");
            double gapMs = System.Diagnostics.Stopwatch.GetElapsedTime(opens[0], opens[1]).TotalMilliseconds;
            Assert.True(gapMs >= 350, $"the second open came {gapMs:F0} ms after the first, and the retry interval is 400 ms");
        }

        /// <summary>Stop hands the devices back, and the crash path's two
        /// steps do too, on the worker's thread.</summary>
        [Fact]
        public void StopAndTheCrashPath_HandBack()
        {
            var native = new FakeAura();
            PeripheralOutputs.Presence = Presence();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Path("keyboard") } });
            var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(WaitFor(() => native.Count("apply:0") == 1));
            backend.SignalStop();
            backend.ReleaseClaimedNow(Environment.TickCount64 + 1000);
            Assert.Equal(1, native.Count("close"));
            backend.Stop();
            Assert.Equal(1, native.Count("close"));
            Assert.Null(PeripheralLightMaps.For(Path("keyboard")));
            backend.Dispose();

            var again = new FakeAura();
            var second = Backend(again);
            second.Start();
            Assert.True(WaitFor(() => again.Count("apply:0") == 1));
            second.Stop();
            Assert.Equal(1, again.Count("close"));
            second.Dispose();
        }

        /// <summary>The backend finds no units and needs no walk: its
        /// snapshot is an empty scan from the start.</summary>
        [Fact]
        public void TheSnapshotIsAnEmptyScan()
        {
            using var backend = Backend(new FakeAura());
            Assert.True(backend.Snapshot.Scanned);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Equal(OutputFamily.AuraSdkType, backend.Family);
            backend.Rescan(Array.Empty<PadForge.Common.Input.HidCollectionInfo>());
        }
    }
}
