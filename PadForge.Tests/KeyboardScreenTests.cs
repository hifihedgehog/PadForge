using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Logitech;
using PadForge.Engine.Common.MadCatz;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The keyboard screens (issue #519): the wire formats against the
    /// implementations that drive the hardware, the key decodes per device,
    /// the hand-off between LGS and the direct writers, the macro ordinals,
    /// the vendor gate and the settings.
    /// </summary>
    public class KeyboardScreenTests
    {
        private const int MonoW = KeyboardScreenFormats.MonoWidth, MonoH = KeyboardScreenFormats.MonoHeight;

        // ── The monochrome frame, against libg15 ──

        /// <summary>libg15's dumpPixmapIntoLCDFormat (netfab/libg15
        /// src/libg15.c), ported line for line, and writePixmapToLCD's
        /// report ID. Its input is a one-bit pixmap, 20 bytes a row, the
        /// leftmost pixel in the top bit, read past row 42 for the last band,
        /// so the pixmap here runs to 48 rows of zeros.</summary>
        private static byte[] Libg15Frame(bool[] lit)
        {
            const int width = 160, rowBytes = width / 8;
            var data = new byte[rowBytes * 48];
            for (int y = 0; y < MonoH; y++)
                for (int x = 0; x < width; x++)
                    if (lit[y * width + x]) data[y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));
            var lcd = new byte[0x3E0];
            int outputOffset = 32, baseOffset = 0;
            for (int row = 0; row < 6; ++row)
            {
                for (int col = 0; col < width; ++col)
                {
                    int bit = col % 8;
                    lcd[outputOffset] = (byte)(
                        (((data[baseOffset] << bit) & 0x80) >> 7) |
                        (((data[baseOffset + rowBytes] << bit) & 0x80) >> 6) |
                        (((data[baseOffset + rowBytes * 2] << bit) & 0x80) >> 5) |
                        (((data[baseOffset + rowBytes * 3] << bit) & 0x80) >> 4) |
                        (((data[baseOffset + rowBytes * 4] << bit) & 0x80) >> 3) |
                        (((data[baseOffset + rowBytes * 5] << bit) & 0x80) >> 2) |
                        (((data[baseOffset + rowBytes * 6] << bit) & 0x80) >> 1) |
                        (((data[baseOffset + rowBytes * 7] << bit) & 0x80) >> 0));
                    ++outputOffset;
                    if (bit == 7) baseOffset++;
                }
                baseOffset += width - rowBytes;
            }
            lcd[0] = 0x03;
            return lcd;
        }

        /// <summary>A picture with the lit pixels dark, as the renderer draws
        /// the monochrome screen.</summary>
        private static byte[] MonoPicture(bool[] lit)
        {
            var bgra = new byte[MonoW * MonoH * 4];
            for (int p = 0; p < lit.Length; p++)
            {
                byte v = lit[p] ? (byte)0 : (byte)255;
                bgra[p * 4] = bgra[p * 4 + 1] = bgra[p * 4 + 2] = v;
                bgra[p * 4 + 3] = 255;
            }
            return bgra;
        }

        [Fact]
        public void TheMonochromeReport_IsLibg15sFrameByteForByte()
        {
            var random = new Random(519);
            var lit = new bool[MonoW * MonoH];
            for (int i = 0; i < lit.Length; i++) lit[i] = random.Next(2) == 0;
            var report = KeyboardScreenFormats.ToG15Report(KeyboardScreenFormats.ToLgsMono(MonoPicture(lit)));
            Assert.Equal(992, report.Length);
            Assert.Equal(Libg15Frame(lit), report);
        }

        [Fact]
        public void TheMonochromeReport_PutsABandsTopRowInBitZero()
        {
            var lit = new bool[MonoW * MonoH];
            lit[0] = true;                         // x 0, y 0: band 0, bit 0
            lit[9 * MonoW + 5] = true;             // x 5, y 9: band 1, bit 1
            lit[42 * MonoW + 159] = true;          // x 159, y 42: band 5, bit 2
            var report = KeyboardScreenFormats.ToG15Report(KeyboardScreenFormats.ToLgsMono(MonoPicture(lit)));
            Assert.Equal(0x03, report[0]);
            Assert.All(Enumerable.Range(1, 31), i => Assert.Equal(0, report[i]));
            Assert.Equal(0x01, report[32]);
            Assert.Equal(0x02, report[32 + 160 + 5]);
            Assert.Equal(0x04, report[32 + 5 * 160 + 159]);
            Assert.Equal(3, report.Skip(32).Count(b => b != 0));
        }

        [Fact]
        public void LgsMonochrome_LightsADarkPixelAt255()
        {
            var lit = new bool[MonoW * MonoH];
            lit[7] = true;
            var mono = KeyboardScreenFormats.ToLgsMono(MonoPicture(lit));
            Assert.Equal(160 * 43, mono.Length);
            Assert.Equal(0xFF, mono[7]);
            Assert.Equal(1, mono.Count(b => b != 0));
        }

        // ── The G19 frame, against G19USB ──

        [Fact]
        public void TheG19Frame_IsG19UsbsHeaderAndPixels()
        {
            var random = new Random(0x519);
            var bgra = new byte[320 * 240 * 4];
            random.NextBytes(bgra);
            var frame = KeyboardScreenFormats.ToG19Frame(bgra);
            Assert.Equal(154112, frame.Length);

            // G19USB G19Constants.LcdHeader: 16 fixed bytes, then each byte
            // its own index.
            byte[] start = { 0x10, 0x0F, 0x00, 0x58, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3F, 0x01, 0xEF, 0x00, 0x0F };
            Assert.Equal(start, frame.Take(16).ToArray());
            Assert.All(Enumerable.Range(16, 512 - 16), i => Assert.Equal((byte)i, frame[i]));

            // G19USB G19Helpers.ConvertBitmapToRGB565, JoinGreenAndBlue and
            // JoinRedAndGreen, x outer and y inner.
            var expected = new byte[320 * 240 * 2];
            for (int x = 0, pos = 0; x < 320; x++)
            {
                for (int y = 0; y < 240; y++)
                {
                    int i = (y * 320 + x) * 4;
                    int b = bgra[i], g = bgra[i + 1] >> 2, r = bgra[i + 2];
                    expected[pos++] = (byte)((g << 5) | (b >> 3));
                    expected[pos++] = (byte)((r & 0xF8) | (g >> 3));
                }
            }
            Assert.Equal(expected, frame.Skip(512).ToArray());
        }

        // ── The keys ──

        [Theory]
        [InlineData(KeyboardScreenDevices.G15)]
        [InlineData(KeyboardScreenDevices.Z10)]
        [InlineData(KeyboardScreenDevices.GamePanel)]
        public void TheG15sNineByteReport_DecodesItsKeys(ushort pid)
        {
            // hid-lg-g15 lg_g15_handle_lcd_menu_keys, libg15 processKeyEvent9Byte.
            byte[] report = { 0x02, 0, 0x80, 0, 0x80, 0, 0, 0, 0x80 };
            int bits = KeyboardScreenFormats.DecodeMonoKeys(pid, report);
            Assert.Equal((1 << 0) | (1 << 2) | (1 << KeyboardScreenFormats.MonoAppletKey), bits);
            Assert.Equal(-1, KeyboardScreenFormats.DecodeMonoKeys(pid, new byte[] { 0x01, 0, 0x80, 0, 0, 0, 0, 0, 0 }));
            Assert.Equal(-1, KeyboardScreenFormats.DecodeMonoKeys(pid, new byte[] { 0x02, 0, 0x80 }));
        }

        [Fact]
        public void TheG15v2G510AndG13Reports_DecodeTheirKeys()
        {
            // hid-lg-g15 lg_g15_v2_event: byte 2, the applet key in bit 7.
            Assert.Equal((1 << 1) | (1 << KeyboardScreenFormats.MonoAppletKey),
                KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G15V2, new byte[] { 0x02, 0, 0x84, 0, 0 }));
            // lg_g510_event: report 3, byte 4, the applet key in bit 0.
            Assert.Equal((1 << 3) | (1 << KeyboardScreenFormats.MonoAppletKey),
                KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G510, new byte[] { 0x03, 0, 0, 0, 0x11 }));
            Assert.Equal(1 << 0, KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G510Audio, new byte[] { 0x03, 0, 0, 0, 0x02 }));
            // The G13: report 1, byte 6.
            Assert.Equal(1 << 2, KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G13, new byte[] { 0x01, 0, 0, 0, 0, 0, 0x08, 0 }));
            Assert.Equal(-1, KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G19, new byte[] { 0x02, 0, 0, 0, 0 }));
        }

        /// <summary>Windows pads every read to the longest input report of
        /// the collection, so a key report arrives with zeros after it and is
        /// still told by its ID.</summary>
        [Fact]
        public void APaddedWindowsRead_StillDecodes()
        {
            var padded = new byte[64];
            padded[0] = 0x03;
            padded[4] = 0x01;
            Assert.Equal(1 << KeyboardScreenFormats.MonoAppletKey,
                KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G510, padded));
        }

        [Fact]
        public void TheG19sDisplayKeys_Decode()
        {
            // gnome15 keys.py, G19USB G19Keys.cs: byte 1 is 0x80.
            Assert.Equal((1 << KeyboardScreenFormats.G19Up) | (1 << KeyboardScreenFormats.G19Ok),
                KeyboardScreenFormats.DecodeG19Keys(new byte[] { 0x88, 0x80 }));
            Assert.Equal(1 << KeyboardScreenFormats.G19Settings, KeyboardScreenFormats.DecodeG19Keys(new byte[] { 0x01, 0x80 }));
            Assert.Equal(-1, KeyboardScreenFormats.DecodeG19Keys(new byte[] { 0x01, 0x00 }));
            Assert.Equal(-1, KeyboardScreenFormats.DecodeG19Keys(new byte[] { 0x01 }));
        }

        [Fact]
        public void LgsButtonFlags_MapToTheRowsButtons()
        {
            Assert.Equal(11, KeyboardScreenFormats.LgsButtons.Length);
            Assert.Contains((0x0001, KeyboardScreenKind.Monochrome, KeyboardScreenFormats.MonoKey1), KeyboardScreenFormats.LgsButtons);
            Assert.Contains((0x4000, KeyboardScreenKind.Color, KeyboardScreenFormats.G19Menu), KeyboardScreenFormats.LgsButtons);
            Assert.DoesNotContain(KeyboardScreenFormats.LgsButtons,
                b => b.Kind == KeyboardScreenKind.Monochrome && b.Button == KeyboardScreenFormats.MonoAppletKey);
            Assert.DoesNotContain(KeyboardScreenFormats.LgsButtons,
                b => b.Kind == KeyboardScreenKind.Color && b.Button == KeyboardScreenFormats.G19Settings);
            Assert.All(KeyboardScreenFormats.LgsButtons,
                b => Assert.InRange(b.Button, 0, KeyboardScreenFormats.KeyCountOf(b.Kind) - 1));
        }

        /// <summary>A tap that starts and ends between two polls still reads
        /// pressed once, the G-key row's rule. Two engines on one screen both
        /// reach its row, and two screens stay apart.</summary>
        [Fact]
        public void ATapBetweenPolls_IsHeldForThePulse_AndTwoScreensStayApart()
        {
            string one = "test-" + Guid.NewGuid().ToString("N"), two = "test-" + Guid.NewGuid().ToString("N");
            try
            {
                KeyboardScreenKeys.Report(one, "hid", 1 << 2);
                KeyboardScreenKeys.Report(one, "hid", 0);
                var buttons = new bool[KeyboardScreenFormats.MaxKeys];
                long now = Environment.TickCount64;
                KeyboardScreenKeys.Read(one, buttons, now);
                Assert.True(buttons[2]);
                KeyboardScreenKeys.Read(one, buttons, now + KeyboardScreenKeys.PulseMs + 1);
                Assert.False(buttons[2]);

                // Two engines hold keys of one screen at once, and its row reads both.
                KeyboardScreenKeys.Report(one, "lgs", 1 << 0);
                KeyboardScreenKeys.Report(one, "hid", 1 << 4);
                KeyboardScreenKeys.Read(one, buttons, now + 10_000);
                Assert.True(buttons[0]);
                Assert.True(buttons[4]);
                KeyboardScreenKeys.Clear(one, "lgs");
                KeyboardScreenKeys.Read(one, buttons, now + 20_000);
                Assert.False(buttons[0]);
                Assert.True(buttons[4]);

                // A key on the other keyboard never reaches this one's row.
                KeyboardScreenKeys.Report(two, "hid", 1 << 1);
                KeyboardScreenKeys.Read(one, buttons, now + 30_000);
                Assert.False(buttons[1]);
                KeyboardScreenKeys.Read(two, buttons, now + 30_000);
                Assert.True(buttons[1]);
                Assert.False(buttons[4]);

                // A screen that left reads nothing.
                KeyboardScreenKeys.ClearScreen(two);
                KeyboardScreenKeys.Read(two, buttons, now + 40_000);
                Assert.DoesNotContain(true, buttons);
            }
            finally
            {
                KeyboardScreenKeys.ClearScreen(one);
                KeyboardScreenKeys.ClearScreen(two);
            }
        }

        // ── Which engine draws ──

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, false)]
        [InlineData(true, false, true)]
        public void TheMonochromeDirectWriter_StandsDownWhileLgsDraws(bool lgs, bool lgsSees, bool draws)
            => Assert.Equal(draws, KeyboardScreenHost.MonoDrawsDirectly(lgs, lgsSees));

        /// <summary>The readers keep only the round applet key while LGS
        /// draws the monochrome screens and hands their other keys over. With
        /// LGS up and no monochrome screen it sees, PadForge draws the
        /// screens itself and the readers pass every key on.</summary>
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        public void TheReaders_KeepOnlyTheAppletKey_OnlyWhileLgsDrawsTheMonochromeScreens(bool lgs, bool lgsSees, bool appletOnly)
        {
            Assert.Equal(appletOnly, KeyboardScreenHost.AppletKeyOnly(lgs, lgsSees));
            Assert.Equal(!KeyboardScreenHost.MonoDrawsDirectly(lgs, lgsSees), KeyboardScreenHost.AppletKeyOnly(lgs, lgsSees));
            // The worker hands the readers this rule, not LGS's session alone.
            string host = File.ReadAllText(RepoFile("PadForge.App", "Common", "Input", "KeyboardScreens", "KeyboardScreenHost.cs"));
            Assert.Contains("appletKeyOnly: AppletKeyOnly(lgs, _lgs.MonoConnected)", host);
        }

        /// <summary>LGS takes one picture per kind, so it shows the first
        /// screen of each kind in id order, and the other screens' rows name
        /// that screen.</summary>
        [Fact]
        public void LgsShowsTheFirstScreenOfEachKind()
        {
            var g510 = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G510, "{bbbb0000-0000-0000-0000-000000000000}", "p1");
            var g15 = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G15, "{aaaa0000-0000-0000-0000-000000000000}", "p2");
            var g19 = KeyboardScreenInfo.G19(@"USB\VID_046D&PID_C229&MI_00\7&1&0&0000");
            var screens = new[] { g510, g19, g15, KeyboardScreenInfo.Arx() };
            // hid:046d:c222:... (the G15) sorts before hid:046d:c22d:... (the G510).
            Assert.Equal(g15, KeyboardScreenHost.Leader(screens, KeyboardScreenKind.Monochrome));
            Assert.Equal(g19, KeyboardScreenHost.Leader(screens, KeyboardScreenKind.Color));
            Assert.Null(KeyboardScreenHost.Leader(new[] { g510 }, KeyboardScreenKind.Color));
        }

        /// <summary>A keyboard's collections share its USB IDs and Windows'
        /// container, so they are one screen. Two keyboards are two, and the
        /// same keyboard found again is the same row.</summary>
        [Fact]
        public void EachKeyboardIsItsOwnScreen_ByItsContainer()
        {
            const string container = "{12345678-1234-1234-1234-123456789ABC}";
            var a = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G15, container, @"\\?\hid#vid_046d&pid_c222&mi_01&col01#x");
            var b = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G15, container, @"\\?\hid#vid_046d&pid_c222&mi_01&col02#x");
            var other = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G15, "{87654321-1234-1234-1234-123456789ABC}", "p");
            Assert.Equal(a, b);
            Assert.Equal(a.RowGuid, b.RowGuid);
            Assert.NotEqual(a.Id, other.Id);
            Assert.NotEqual(a.RowGuid, other.RowGuid);
            Assert.Equal("Logitech G15 Screen", a.Name);
            Assert.Equal("G15", a.Model);
            // No container named: the collection's own path keeps it apart.
            var lone = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G13, "", @"\\?\HID#VID_046D&PID_C21C#Y");
            Assert.Equal(@"hid:\\?\hid#vid_046d&pid_c21c#y", lone.Id);
            Assert.Equal(KeyboardScreenInfo.G19("X").RowGuid, KeyboardScreenInfo.G19("x").RowGuid);
        }

        /// <summary>A keyboard screen macro plays on the screens whose rows
        /// feed its slot, the Dreamcast macro's rule, and on no other.</summary>
        [Fact]
        public void AMacro_ReachesOnlyTheScreensThatFeedItsSlot()
        {
            var first = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G15, "{aaaa0000-0000-0000-0000-000000000000}", "p1");
            var second = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G510, "{bbbb0000-0000-0000-0000-000000000000}", "p2");
            var arx = KeyboardScreenInfo.Arx();
            var slots = new Dictionary<Guid, HashSet<int>>
            {
                [first.RowGuid] = new() { 0 },
                [second.RowGuid] = new() { 1, 2 },
            };
            HashSet<int> SlotsOf(Guid row) => slots.TryGetValue(row, out var s) ? s : new HashSet<int>();
            var screens = new[] { first, second, arx };
            Assert.Equal(new[] { first }, KeyboardScreenService.Feeding(screens, 0, SlotsOf));
            Assert.Equal(new[] { second }, KeyboardScreenService.Feeding(screens, 2, SlotsOf));
            Assert.Empty(KeyboardScreenService.Feeding(screens, 5, SlotsOf));
        }

        /// <summary>LGS keeps the version it last wrote for a kind, so a new
        /// leading screen's first frame must carry a version no other
        /// screen's frame had. Two screens with one frame each differ.</summary>
        [Fact]
        public void EveryFrame_TakesAVersionNoOtherScreensFrameHas()
        {
            using var host = new KeyboardScreenHost(new byte[4], 1, 1, null);
            host.Publish("test-a", new byte[] { 1 });
            host.Publish("test-b", new byte[] { 2 });
            var a = host.FrameOf("test-a");
            var b = host.FrameOf("test-b");
            Assert.NotNull(a.Frame);
            Assert.NotNull(b.Frame);
            Assert.NotEqual(a.Version, b.Version);
            // Above the sentinels the writers keep: -1 (nothing written) and
            // -2 (cleared to black).
            Assert.True(a.Version > 0 && b.Version > 0);
        }

        /// <summary>Arx's guide (page 3) asks for init and shutdown on one
        /// thread, and the LCD library gets every call from one thread: the
        /// sessions close on the worker that opened them, never on the
        /// thread that disposes the host. A pass longer than Dispose's
        /// wait still ends in the shutdown, on the worker, when it ends.</summary>
        [Fact]
        public void TheEngines_ShutDownOnTheWorker_EvenAfterDisposesWaitRanOut()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var host = new KeyboardScreenHost(new byte[4], 1, 1, null) { JoinMs = 100 };
            int worker = 0;
            host.PassForTest = () =>
            {
                worker = Environment.CurrentManagedThreadId;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            };
            host.Start();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                int disposer = 0;
                var dispose = new Thread(() =>
                {
                    disposer = Environment.CurrentManagedThreadId;
                    host.Dispose();
                });
                dispose.Start();
                Assert.True(dispose.Join(TimeSpan.FromSeconds(5)), "Dispose waited past its join");
                Assert.Equal(0, host.ClosedOnThread);
                release.Set();
                Assert.True(SpinWait.SpinUntil(() => host.ClosedOnThread != 0, TimeSpan.FromSeconds(5)));
                Assert.Equal(worker, host.ClosedOnThread);
                Assert.NotEqual(disposer, host.ClosedOnThread);
            }
            finally
            {
                release.Set();
            }
        }

        /// <summary>A pass that ends after Dispose emptied the screen list
        /// puts nothing back in it.</summary>
        [Fact]
        public void AStoppingHost_PublishesNoScreens()
        {
            var host = new KeyboardScreenHost(new byte[4], 1, 1, null);
            host.Dispose();
            host.PublishScreens(new[] { KeyboardScreenInfo.Arx() });
            Assert.Empty(KeyboardScreensRuntime.Screens);
        }

        /// <summary>A host made while the last one's worker is still inside
        /// a pass runs no pass of its own until that worker has closed its
        /// sessions.</summary>
        [Fact]
        public void TheNextHost_WaitsForTheLastWorker()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var first = new KeyboardScreenHost(new byte[4], 1, 1, null) { JoinMs = 50 };
            first.PassForTest = () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            };
            var second = new KeyboardScreenHost(new byte[4], 1, 1, null);
            int passes = 0;
            second.PassForTest = () => Interlocked.Increment(ref passes);
            try
            {
                first.Start();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                first.Dispose();
                second.Start();
                Thread.Sleep(300);
                Assert.Equal(0, Volatile.Read(ref passes));
                release.Set();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref passes) > 0, TimeSpan.FromSeconds(5)));
                Assert.NotEqual(0, first.ClosedOnThread);
            }
            finally
            {
                release.Set();
                second.Dispose();
            }
        }

        /// <summary>A last worker that outlives the next host's wait ends no
        /// session of that host's on the libraries the process shares, and its
        /// own while that host has none there.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ALastWorkerThatOutlivesTheNextHostsWait_EndsNoSessionOfThatHost(bool theNextHostHasOne)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var firstLcd = new FakeLcd();
            var firstArx = new FakeArx();
            var nextLcd = new FakeLcd();
            var nextArx = new FakeArx();
            KeyboardScreenHost Host(FakeLcd lcdLib, FakeArx arxLib, bool active, int waitMs, int joinMs)
            {
                var lgs = new LgsLcdScreens(_ => lcdLib, () => Array.Empty<uint>());
                var arx = new ArxScreen(_ => arxLib);
                if (active)
                {
                    lgs.Step(new byte[MonoW * MonoH * 4], 1, null, 0, 1_000);
                    arx.Step(new byte[KeyboardScreenFormats.ArxWidth * KeyboardScreenFormats.ArxHeight * 4], 1, 1_000);
                }
                return new KeyboardScreenHost(new byte[4], 1, 1, null) { Lgs = lgs, Arx = arx, PredecessorWaitMs = waitMs, JoinMs = joinMs };
            }
            var first = Host(firstLcd, firstArx, true, KeyboardScreenHost.PredecessorJoinMs, 50);
            first.PassForTest = () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            var second = Host(nextLcd, nextArx, theNextHostHasOne, 50, 5000);
            int passes = 0;
            second.PassForTest = () => Interlocked.Increment(ref passes);
            try
            {
                first.Start();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
                first.Dispose();
                second.Start();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref passes) > 0, TimeSpan.FromSeconds(30)));
                release.Set();
                Assert.True(SpinWait.SpinUntil(() => first.ClosedOnThread != 0, TimeSpan.FromSeconds(30)));
                Assert.Equal(theNextHostHasOne ? 0 : 1, firstLcd.Shutdowns);
                Assert.Equal(theNextHostHasOne ? 0 : 1, firstArx.Shutdowns);
            }
            finally
            {
                release.Set();
                second.Dispose();
            }
            Assert.Equal(theNextHostHasOne ? 1 : 0, nextLcd.Shutdowns);
            Assert.Equal(theNextHostHasOne ? 1 : 0, nextArx.Shutdowns);
        }

        private sealed class FakeLcd : ILogitechLcd
        {
            public int Inits, Shutdowns;
            public bool Init(string friendlyName, int lcdTypes) { Inits++; return true; }
            public bool IsConnected(int lcdType) => true;
            public bool IsButtonPressed(int button) => false;
            public void Update() { }
            public void Shutdown() => Shutdowns++;
            public bool MonoSetBackground(byte[] monoBitmap) => true;
            public bool ColorSetBackground(byte[] colorBitmap) => true;
        }

        /// <summary>LGS restarting under an open LCD session: the session
        /// that saw LGS's process see it replaced is shut down and opened
        /// again at once, and one that sees it gone is shut down, the direct
        /// writers draw, and the init is tried again on its timer (Arx's
        /// guide, pages 8 and 15, the sibling SDK's rule).</summary>
        [Fact]
        public void AnLcdSession_OpensAgain_WhenLgsRestarts()
        {
            var lib = new FakeLcd();
            uint[] lgs = { 100 };
            var lcd = new LgsLcdScreens(_ => lib, () => lgs?.ToArray());
            var mono = new byte[MonoW * MonoH * 4];
            long t = 1_000;
            lcd.Step(mono, 1, null, 0, t);
            Assert.True(lcd.Active);
            Assert.Equal(1, lib.Inits);
            lcd.Step(mono, 1, null, 0, t += 20);           // the session sees LGS 100
            lcd.Step(mono, 1, null, 0, t += LgsLcdScreens.LgsCheckMs);
            Assert.Equal(0, lib.Shutdowns);

            // LGS restarted inside one look: shut down and opened again.
            lgs = new uint[] { 200 };
            lcd.Step(mono, 1, null, 0, t += LgsLcdScreens.LgsCheckMs);
            Assert.Equal(1, lib.Shutdowns);
            Assert.Equal(2, lib.Inits);
            Assert.True(lcd.Active);

            // LGS gone: shut down, and no init until its timer.
            lcd.Step(mono, 1, null, 0, t += 20);           // sees LGS 200
            lgs = Array.Empty<uint>();
            lcd.Step(mono, 1, null, 0, t += LgsLcdScreens.LgsCheckMs);
            Assert.Equal(2, lib.Shutdowns);
            Assert.False(lcd.Active);
            lcd.Step(mono, 1, null, 0, t += 20);
            Assert.Equal(2, lib.Inits);
            lgs = new uint[] { 300 };
            lcd.Step(mono, 1, null, 0, t += LgsLcdScreens.InitRetryMs);
            Assert.Equal(3, lib.Inits);
            Assert.True(lcd.Active);

            // An unreadable list decides nothing.
            lcd.Step(mono, 1, null, 0, t += 20);           // sees LGS 300
            lgs = null;
            lcd.Step(mono, 1, null, 0, t += LgsLcdScreens.LgsCheckMs);
            Assert.Equal(2, lib.Shutdowns);
            Assert.True(lcd.Active);
        }

        /// <summary>A session that never saw an LGS process (a host under
        /// another name) is never taken down by the look.</summary>
        [Fact]
        public void AnLcdSession_ThatNeverSawLgs_IsLeftAlone()
        {
            var lib = new FakeLcd();
            var lcd = new LgsLcdScreens(_ => lib, () => Array.Empty<uint>());
            var mono = new byte[MonoW * MonoH * 4];
            long t = 1_000;
            for (int i = 0; i < 5; i++) lcd.Step(mono, 1, null, 0, t += LgsLcdScreens.LgsCheckMs);
            Assert.Equal(1, lib.Inits);
            Assert.Equal(0, lib.Shutdowns);
            Assert.True(lcd.Active);
        }

        private sealed class FakeArx : ILogitechArx
        {
            public int Inits, Shutdowns, Calls;
            public bool Answers = true;
            public int Error;
            public Action<uint, uint> OnEvent;
            public bool Init(string identifier, string friendlyName, Action<uint, uint> onEvent)
            {
                Inits++;
                OnEvent = onEvent;
                return true;
            }
            private bool Call() { Calls++; return Answers; }
            public bool AddContent(byte[] content, string fileName, string mimeType) => Call();
            public bool AddImage(byte[] bgra, int width, int height, string fileName) => Call();
            public bool SetIndex(string fileName) => Call();
            public bool SetTagProperty(string tagId, string property, string value) => Call();
            public int LastError => Answers ? 0 : Error;
            public void Shutdown() => Shutdowns++;
        }

        /// <summary>The Arx guide, page 15: a call that fails with error 6,
        /// the connection with LGS broken, means LGS takes no further call,
        /// so the session is shut down and the init tried again on its timer
        /// (page 8). A still picture makes no call of its own, so a slow
        /// liveness call finds the loss.</summary>
        [Fact]
        public void AnArxSession_ThatLostLgs_IsShutDownAndOpenedAgain()
        {
            var lib = new FakeArx();
            var arx = new ArxScreen(_ => lib);
            var frame = new byte[KeyboardScreenFormats.ArxWidth * KeyboardScreenFormats.ArxHeight * 4];
            long t = 1_000;
            arx.Step(frame, 1, t);
            Assert.True(arx.Active);
            lib.OnEvent(LogitechArxLibrary.EventDeviceArrival, 0);
            arx.Step(frame, 1, t += 20);
            Assert.True(arx.OnPhone);

            // The picture stands. LGS exits: the next liveness call fails.
            lib.Answers = false;
            lib.Error = LogitechArxLibrary.ErrorConnectionBroken;
            int calls = lib.Calls;
            arx.Step(frame, 1, t += 20);
            Assert.Equal(calls, lib.Calls);                       // not before its time
            arx.Step(frame, 1, t += ArxScreen.LivenessMs);
            Assert.Equal(calls + 1, lib.Calls);
            Assert.Equal(1, lib.Shutdowns);
            Assert.False(arx.Active);
            Assert.False(arx.OnPhone);

            // LGS is back: the init waits for its timer, then the page goes
            // out again.
            lib.Answers = true;
            arx.Step(frame, 1, t += 20);
            Assert.Equal(1, lib.Inits);
            arx.Step(frame, 1, t += ArxScreen.InitRetryMs);
            Assert.Equal(2, lib.Inits);
            Assert.True(arx.Active);
        }

        /// <summary>A new picture that fails with error 4 (SDK not
        /// initialized) ends the session too. Any other error leaves it up
        /// and the picture goes out again.</summary>
        [Fact]
        public void AnArxSession_StaysUp_OnAnyOtherError()
        {
            var lib = new FakeArx();
            var arx = new ArxScreen(_ => lib);
            var frame = new byte[KeyboardScreenFormats.ArxWidth * KeyboardScreenFormats.ArxHeight * 4];
            long t = 1_000;
            arx.Step(frame, 1, t);
            lib.Answers = false;
            lib.Error = 8;                                         // error copying memory
            arx.Step(frame, 2, t += ArxScreen.MinImageMs);
            Assert.True(arx.Active);
            Assert.Equal(0, lib.Shutdowns);
            lib.Error = LogitechArxLibrary.ErrorNotInitialized;
            arx.Step(frame, 2, t += ArxScreen.MinImageMs);
            Assert.False(arx.Active);
            Assert.Equal(1, lib.Shutdowns);
        }

        private sealed class FakeKeyReader : G15HidScreens.IKeyReader
        {
            public volatile bool Attached = true;
            public bool IsAttached => Attached;
            public void Dispose() { }
        }

        /// <summary>A keyboard unplugged and plugged in again inside one
        /// scan's wait keeps its path, and its screen comes back blank. Its
        /// reader going quiet is the sign: the scan opens the reader again,
        /// and the picture goes out again though it did not change.</summary>
        [Fact]
        public void AMonochromeKeyboard_PluggedInAgain_GetsItsPictureAgain()
        {
            const string path = @"\\?\hid#vid_046d&pid_c222&mi_01#test";
            var readers = new List<FakeKeyReader>();
            var writes = new List<string>();
            var resets = new List<string>();
            var hid = new G15HidScreens(new G15HidScreens.Hid
            {
                Paths = () => new() { (path, KeyboardScreenDevices.G15, "{12345678-1234-1234-1234-123456789ABC}") },
                ReadCaps = _ => new G15HidScreens.Caps(KeyboardScreenFormats.G15ReportLength, 9, 0xFF00, 0x0001),
                OpenReader = (_, _, _, _) => { var r = new FakeKeyReader(); readers.Add(r); return r; },
                Write = (p, _) => { writes.Add(p); return true; },
                Reset = p => resets.Add(p),
            });
            try
            {
                var frame = MonoPicture(new bool[MonoW * MonoH]);
                (byte[], long) FrameOf(string _) => (frame, 7);
                hid.Step(FrameOf, true, false, 0);
                Assert.Single(hid.Screens);
                Assert.Single(writes);
                hid.Step(FrameOf, true, false, 20);
                Assert.Single(writes);

                readers[0].Attached = false;
                hid.Step(FrameOf, true, false, G15HidScreens.ScanMs);
                Assert.Equal(2, readers.Count);
                Assert.Contains(path, resets);
                Assert.Equal(2, writes.Count);
            }
            finally
            {
                hid.Dispose();
            }
        }

        private sealed class FakeG19Link : IG19Link
        {
            public int Writes;
            public volatile bool Fail;
            public volatile bool Disposed;
            public bool HasKeys => true;
            public int KeyPacket => 2;
            public bool WriteFrame(byte[] data, out int error) { error = 0; Writes++; return true; }
            public G19Read ReadKeys(byte[] buffer, out int got)
            {
                got = 0;
                if (Fail) return G19Read.Error;
                Thread.Sleep(5);
                return G19Read.Timeout;
            }
            public void Dispose() => Disposed = true;
        }

        private const string G19Node = @"USB\VID_046D&PID_C229&MI_00\7&TEST&0&0000";

        private static List<VendorUsbDriverInstaller.UsbNode> G19Nodes(string service)
            => new() { new VendorUsbDriverInstaller.UsbNode(G19Node, G19UsbScreen.Vid, G19UsbScreen.Pid, 0, service, new[] { @"USB\VID_046D&PID_C229&MI_00" }, Array.Empty<string>()) };

        /// <summary>A G19's key reader that stops on an error (unplugged,
        /// maybe plugged in again inside one look's wait) closes the screen,
        /// and the next look opens it again and draws the picture again
        /// though it did not change.</summary>
        [Fact]
        public void AG19_PluggedInAgain_GetsItsPictureAgain()
        {
            var links = new List<FakeG19Link>();
            var screen = new G19UsbScreen(G19Node, null, new G19Io
            {
                FindPath = _ => "path",
                Open = _ => { var l = new FakeG19Link(); links.Add(l); return l; },
                Bind = (_, _, _, _, _) => throw new InvalidOperationException("no bind on WinUSB"),
            });
            try
            {
                var frame = new byte[KeyboardScreenFormats.ColorWidth * KeyboardScreenFormats.ColorHeight * 4];
                var nodes = G19Nodes("WINUSB");
                screen.Step(frame, 3, true, 0, nodes);
                Assert.Equal(G19UsbScreen.State.Open, screen.Current);
                screen.Step(frame, 3, true, 20, nodes);
                Assert.Equal(1, links[0].Writes);

                links[0].Fail = true;
                Thread.Sleep(200);
                screen.Step(frame, 3, true, 40, nodes);
                Assert.True(links[0].Disposed);
                Assert.Equal(2, links.Count);
                Assert.Equal(1, links[1].Writes);
            }
            finally
            {
                screen.Dispose();
            }
        }

        private sealed class StuckG19Link : IG19Link
        {
            public readonly ManualResetEventSlim Release = new();
            /// <summary>Set once the reader is inside its read.</summary>
            public readonly ManualResetEventSlim Reading = new();
            public int Writes;
            public bool FailWrites;
            public volatile bool Disposed;
            public bool HasKeys => true;
            public int KeyPacket => 2;
            public bool WriteFrame(byte[] data, out int error)
            {
                error = FailWrites ? 31 : 0;
                Writes++;
                return !FailWrites;
            }
            public G19Read ReadKeys(byte[] buffer, out int got)
            {
                got = 0;
                Reading.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
                return G19Read.Error;
            }
            public void Dispose() => Disposed = true;
        }

        /// <summary>A key reader stuck in a read past Close's wait is left
        /// with its handles (the headset reader's rule). When its read ends
        /// in an error after the screen opened again on a new link, that
        /// error is the old link's, and the new session stays open.</summary>
        [Fact]
        public void AStaleG19Reader_NeverClosesTheNextSession()
        {
            var links = new List<StuckG19Link>();
            var screen = new G19UsbScreen(G19Node, null, new G19Io
            {
                FindPath = _ => "path",
                Open = _ => { var l = new StuckG19Link(); links.Add(l); return l; },
                Bind = (_, _, _, _, _) => throw new InvalidOperationException("no bind on WinUSB"),
            });
            try
            {
                var frame = new byte[KeyboardScreenFormats.ColorWidth * KeyboardScreenFormats.ColorHeight * 4];
                var nodes = G19Nodes("WINUSB");
                screen.Step(frame, 1, true, 0, nodes);
                Assert.Single(links);
                // The reader is inside its read before the write fails. A
                // reader thread that starts late under a loaded run sees the
                // screen closed, leaves without reading, and its link is
                // freed, which is the close this test does not mean.
                Assert.True(links[0].Reading.Wait(TimeSpan.FromSeconds(10)), "the reader never read");

                // A write fails: the screen closes, and its reader stays
                // stuck past the wait.
                links[0].FailWrites = true;
                screen.Step(frame, 2, true, 20, nodes);
                Assert.False(links[0].Disposed);

                // The next look opens the screen on a new link.
                screen.Step(frame, 2, true, G19UsbScreen.LookMs + 20, nodes);
                Assert.Equal(2, links.Count);
                Assert.Equal(1, links[1].Writes);

                // The old reader's read ends in an error now.
                links[0].Release.Set();
                Thread.Sleep(200);
                screen.Step(frame, 2, true, G19UsbScreen.LookMs + 40, nodes);
                // The new session still draws: a new picture goes out on it.
                screen.Step(frame, 3, true, G19UsbScreen.LookMs + 60, nodes);
                Assert.Equal(2, links[1].Writes);
                Assert.False(links[1].Disposed);
                Assert.Equal(2, links.Count);
            }
            finally
            {
                foreach (var l in links) l.Release.Set();
                screen.Dispose();
            }
        }

        /// <summary>The switch goes off while a G19's bind is in flight:
        /// Restore waits for the bind and then finds the node on PadForge's
        /// WinUSB and gives it back, where without the wait it read the node
        /// before the bind landed, skipped it, and left the G19 on
        /// WinUSB.</summary>
        [Fact]
        public void Restore_WaitsForABindInFlight_AndGivesTheNodeBack()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            string service = "HidUsb";
            var restored = new List<string>();
            var screen = new G19UsbScreen(G19Node, null, new G19Io
            {
                FindPath = _ => null,
                Open = _ => null,
                Bind = (_, _, _, _, _) =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                    service = "WINUSB";
                    return true;
                },
            });
            Thread restore = null;
            try
            {
                screen.Step(null, 0, true, 0, G19Nodes("HidUsb"));
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                restore = new Thread(() => G19UsbScreen.Restore(null, () => G19Nodes(service), _ => true,
                    (id, _) => { lock (restored) restored.Add(id); return true; }));
                restore.Start();
                Assert.False(restore.Join(300), "Restore ran while the bind was in flight");
                release.Set();
                Assert.True(restore.Join(TimeSpan.FromSeconds(5)));
                Assert.Equal(new[] { G19Node }, restored);
            }
            finally
            {
                release.Set();
                restore?.Join(TimeSpan.FromSeconds(5));
                screen.Dispose();
            }
        }

        /// <summary>A bind that reaches the driver gate after the host
        /// stopped (the switch went off, the engine stopped) binds
        /// nothing.</summary>
        [Fact]
        public async System.Threading.Tasks.Task ABind_ThatStartsAfterTheHostStopped_BindsNothing()
        {
            using var restoring = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var stop = new CancellationTokenSource();
            int binds = 0;
            var screen = new G19UsbScreen(G19Node, null, new G19Io
            {
                FindPath = _ => null,
                Open = _ => null,
                Bind = (_, _, _, _, _) => { Interlocked.Increment(ref binds); return true; },
            }, stop.Token);
            // A Restore holds the gate while the bind is started.
            var restore = new Thread(() => G19UsbScreen.Restore(null, () =>
            {
                restoring.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return null;
            }, _ => true, (_, _) => true));
            try
            {
                restore.Start();
                Assert.True(restoring.Wait(TimeSpan.FromSeconds(5)));
                screen.Step(null, 0, true, 0, G19Nodes("HidUsb"));
                Assert.NotNull(screen.BindTask);
                stop.Cancel();
                release.Set();
                await screen.BindTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, Volatile.Read(ref binds));
            }
            finally
            {
                release.Set();
                restore.Join(TimeSpan.FromSeconds(5));
                screen.Dispose();
            }
        }

        /// <summary>Each bind rewrites the package and signs it again, so a
        /// G19 whose bind keeps failing is tried three times and no more,
        /// VendorUsbBindingService's MaxFailures. The G19 plugged in again is
        /// a new screen and tries again.</summary>
        [Fact]
        public async System.Threading.Tasks.Task AG19Bind_StopsAfterThreeFailures()
        {
            int binds = 0;
            var io = new G19Io
            {
                FindPath = _ => null,
                Open = _ => null,
                Bind = (_, _, _, _, _) => { Interlocked.Increment(ref binds); return false; },
            };
            var screen = new G19UsbScreen(G19Node, null, io);
            var nodes = G19Nodes("HidUsb");
            long t = 0;
            for (int i = 0; i < 6; i++)
            {
                screen.Step(null, 0, true, t, nodes);
                if (screen.BindTask != null) await screen.BindTask.WaitAsync(TimeSpan.FromSeconds(5));
                t += G19UsbScreen.BindRetryMs;
            }
            Assert.Equal(G19UsbScreen.MaxBindFailures, Volatile.Read(ref binds));
            Assert.Equal(3, G19UsbScreen.MaxBindFailures);

            var again = new G19UsbScreen(G19Node, null, io);
            again.Step(null, 0, true, t, nodes);
            await again.BindTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(G19UsbScreen.MaxBindFailures + 1, Volatile.Read(ref binds));
        }

        /// <summary>A G19 on a driver from Logitech gets the line that says
        /// to start LGS. One on another program's driver (libusbK from Zadig,
        /// someone else's WinUSB binding) gets a line that names the driver
        /// and says PadForge leaves it alone, since LGS cannot use it
        /// either.</summary>
        [Theory]
        [InlineData("LGBulk", "Logitech", "OtherDriver")]
        [InlineData("libusbK", "libusbK", "ForeignDriver")]
        [InlineData("WINUSB", "libwdi", "ForeignDriver")]
        [InlineData("LGBulk", null, "OtherDriver")]
        public void AG19OnAnotherDriver_IsToldApartByWhoseDriverItIs(string service, string provider, string expectedName)
        {
            var expected = Enum.Parse<G19UsbScreen.State>(expectedName);
            var screen = new G19UsbScreen(G19Node, null, new G19Io
            {
                FindPath = _ => null,
                Open = _ => null,
                Bind = (_, _, _, _, _) => throw new InvalidOperationException("no bind from another driver"),
                ProviderOf = _ => provider,
            });
            screen.Step(null, 0, true, 0, G19Nodes(service));
            Assert.Equal(expected, screen.Current);
            Assert.Equal(service, screen.OtherDriver);

            var status = KeyboardScreenHost.ColorStatus(screen.Current, screen.OtherDriver, lgs: true, lgsSeesColor: true);
            Assert.Equal(expected == G19UsbScreen.State.ForeignDriver ? KeyboardScreenRoute.G19ForeignDriver : KeyboardScreenRoute.Lgs, status.Route);
            status = KeyboardScreenHost.ColorStatus(screen.Current, screen.OtherDriver, lgs: false, lgsSeesColor: false);
            Assert.Equal(expected == G19UsbScreen.State.ForeignDriver ? KeyboardScreenRoute.G19ForeignDriver : KeyboardScreenRoute.G19OtherDriver, status.Route);
            Assert.Equal(service, status.Detail);
        }

        /// <summary>Every route a screen's status can take has its own line
        /// (lens 1y: an enum that grows reaches every switch over it).</summary>
        [Fact]
        public void EveryKeyboardScreenRoute_HasALine()
        {
            string service = File.ReadAllText(RepoFile("PadForge.App", "Services", "KeyboardScreenService.cs"));
            foreach (var route in Enum.GetValues<KeyboardScreenRoute>())
                if (route != KeyboardScreenRoute.NotFound)
                    Assert.Contains("KeyboardScreenRoute." + route + " =>", service);
            string resx = File.ReadAllText(RepoFile("PadForge.App", "Resources", "Strings", "Strings.resx"));
            Assert.Contains("<data name=\"KeyboardScreens_Status_G19ForeignDriver_Format\" xml:space=\"preserve\"><value>the G19's screen is on another program's driver ({0}), which PadForge leaves alone</value></data>", resx);
        }

        /// <summary>A S.T.R.I.K.E. 7 library that will not load for want of
        /// the Visual C++ 2008 runtime is not loaded again every ten seconds.
        /// The screen looks for the runtime once a minute, and loads at once
        /// after the install button's own check found it.</summary>
        [Fact]
        public void AStrike7Library_MissingItsRuntime_WaitsForTheRuntime()
        {
            string path = Path.Combine(Path.GetTempPath(), "PadForge.Tests-" + Guid.NewGuid().ToString("N") + ".dll");
            File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A });
            try
            {
                int loads = 0, checks = 0;
                bool installed = false;
                long found = 0;
                var screen = new Strike7Screen(new byte[4], 1, 1,
                    _ => { loads++; return (null, "runtime"); },
                    () => { checks++; if (installed) found++; return installed; },
                    () => found);
                long t = 0;
                screen.Step(null, 0, path, string.Empty, t);
                Assert.Equal(1, loads);
                Assert.Equal("runtime", screen.LoadReason);
                for (int i = 0; i < 5; i++) screen.Step(null, 0, path, string.Empty, t += Strike7Screen.OpenRetryMs);
                Assert.Equal(1, loads);

                // The minute's check finds no runtime: still no load.
                screen.Step(null, 0, path, string.Empty, t = Strike7Screen.RuntimeCheckMs);
                Assert.Equal(1, checks);
                Assert.Equal(1, loads);

                // The install button's check finds it: the load is tried at once.
                found++;
                screen.Step(null, 0, path, string.Empty, t += 20);
                Assert.Equal(2, loads);

                // And the minute's check finding it does the same.
                installed = true;
                screen.Step(null, 0, path, string.Empty, t += Strike7Screen.RuntimeCheckMs);
                Assert.Equal(3, loads);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TheG19_IsBoundOnlyWhileLgsIsAway()
        {
            Assert.True(KeyboardScreenHost.G19MayBind(false));
            Assert.False(KeyboardScreenHost.G19MayBind(true));
        }

        // ── The test host loads no vendor library ──

        [Fact]
        public void EveryVendorLibrary_IsRefusedInTheTestHost()
        {
            Assert.True(VendorProgramGate.Refuses);
            Assert.Null(LogitechLcdLibrary.TryLoad(out string lcd));
            Assert.Equal("gate", lcd);
            Assert.Null(LogitechArxLibrary.TryLoad(out string arx));
            Assert.Equal("gate", arx);
            Assert.Null(Strike7Library.TryLoad(@"C:\nowhere\Strike7API.dll", out string strike7));
            Assert.Equal("gate", strike7);
        }

        // ── The keys row and the macros keep their numbers ──

        /// <summary>Each screen is a row of type 42 with its own kind's keys:
        /// five on a monochrome screen, eight on a G19, none on Arx Control
        /// or the S.T.R.I.K.E. 7, whose rows carry the screen alone.</summary>
        [Fact]
        public void EachScreenRow_IsDeviceType42_WithItsKindsKeys()
        {
            Assert.Equal(42, InputDeviceType.KeyboardScreenKeys);
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.KeyboardScreenKeys));
            var cases = new (KeyboardScreenInfo Screen, int Keys, ushort Pid)[]
            {
                (KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G510, "{aaaa0000-0000-0000-0000-000000000000}", "p"), 5, 0x4B4D),
                (KeyboardScreenInfo.G19(@"USB\VID_046D&PID_C229&MI_00\7&1&0&0000"), 8, 0x4B43),
                (KeyboardScreenInfo.Arx(), 0, 0x4B41),
                (KeyboardScreenInfo.Strike7(), 0, 0x4B37),
            };
            foreach (var (screen, keys, pid) in cases)
            {
                using var row = new KeyboardScreenDevice(screen);
                Assert.Equal(InputDeviceType.KeyboardScreenKeys, row.GetInputDeviceType());
                Assert.Equal(keys, row.NumButtons);
                Assert.Equal(keys, row.GetDeviceObjects().Length);
                Assert.Equal(0x4B53, row.VendorId);
                Assert.Equal(pid, row.ProductId);
                Assert.Equal(screen.RowGuid, row.InstanceGuid);
                Assert.Equal(screen.Name, row.Name);
            }
        }

        /// <summary>The owner's rule (2026-10-25): the Settings page never
        /// sets anything for one device. The keyboard screens' dialogs and a
        /// Falcon's Calibrate Reach are on the device's panel on the Devices
        /// page, and the pad page holds only what belongs to a slot.</summary>
        [Fact]
        public void DeviceActions_LiveOnTheDevicesPage_NeverInSettings()
        {
            string settings = File.ReadAllText(RepoFile("PadForge.App", "Views", "SettingsPage.xaml"));
            string devices = File.ReadAllText(RepoFile("PadForge.App", "Views", "DevicesPage.xaml"));
            string pad = File.ReadAllText(RepoFile("PadForge.App", "Views", "PadPage.xaml"));
            foreach (string kind in new[] { "Monochrome", "Color", "Arx", "Strike7" })
                Assert.DoesNotContain("KeyboardScreens_Configure_" + kind, settings);
            Assert.DoesNotContain("Calibrate", settings);
            Assert.DoesNotContain("Falcon_Calibrate", pad);
            Assert.Contains("Click=\"KeyboardScreen_Click\"", devices);
            Assert.Contains("Click=\"FalconCalibrate_Click\"", devices);
        }

        [Fact]
        public void TheKeyboardScreenMacros_AppendAtTheTail()
        {
            Assert.Equal(58, (int)MacroActionType.ShowKeyboardScreen);
            Assert.Equal(59, (int)MacroActionType.KeyboardScreenMode);
            Assert.Equal(60, (int)MacroActionType.Stopwatch);
            Assert.Equal(61, (int)MacroActionType.PanKeyboardScreen);
            // Adaptive Trigger Effect and its Stop follow at 62 and 63 (#531),
            // Set Key Colors and Clear Key Colors at 64 and 65 (#557), and
            // Toggle Screen Overlay at 66 (#560).
            Assert.Equal(MacroActionType.PanKeyboardScreen, Enum.GetValues<MacroActionType>()[^7]);
            Assert.Equal(0, (int)KeyboardScreenMode.Off);
            Assert.Equal(8, (int)KeyboardScreenMode.Lightbar);
            Assert.Equal(KeyboardScreenMode.Lightbar, KeyboardScreenData.DefaultMode);
            Assert.Equal(3, (int)KeyboardScreenKind.Strike7);
        }

        /// <summary>Both dispatch loops send each action to its sink, the
        /// Show Dreamcast Screen seam's shape.</summary>
        [Fact]
        public void EachKeyboardScreenAction_SitsInBothEvaluationSwitches()
        {
            string source = File.ReadAllText(RepoFile("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs"));
            foreach (string call in new[]
            {
                "MacroKeyboardScreenShowSink(macro.PadIndex, action.ScreenKind, action.ScreenOverlayNumber, action.ScreenFrames,",
                "MacroKeyboardScreenModeSink(macro.PadIndex, action.ScreenKind, action.ScreenOverlayNumber, action.ScreenMode);",
                "MacroStopwatchSink(action.StopwatchAction);",
                "MacroKeyboardScreenPanSink(macro.PadIndex, action.ScreenOverlayNumber, action.PanDirection, action.PanStep);",
            })
                Assert.Equal(2, CountOf(source, call));
        }

        [Fact]
        public void TheKeyboardScreenActions_RoundTripThroughTheSettingsFile()
        {
            var action = new MacroAction
            {
                Type = MacroActionType.ShowKeyboardScreen,
                ScreenKind = KeyboardScreenKind.Arx,
                ScreenFrames = "AAAA,BBBB",
                ScreenFrameMs = 250,
                ScreenRepeat = 3,
                ScreenMode = KeyboardScreenMode.Stopwatch,
                StopwatchAction = KeyboardScreenStopwatchAction.Reset,
                PanDirection = KeyboardScreenPanDirection.Up,
                PanStep = 40,
            };
            var item = new MacroItem { Name = "screens" };
            item.Actions.Add(action);
            var data = SettingsService.BuildMacroDataForMacro(item, 0);
            var back = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null).Actions[0];
            Assert.Equal(KeyboardScreenKind.Arx, back.ScreenKind);
            Assert.Equal("AAAA,BBBB", back.ScreenFrames);
            Assert.Equal(250, back.ScreenFrameMs);
            Assert.Equal(3, back.ScreenRepeat);
            Assert.Equal(KeyboardScreenMode.Stopwatch, back.ScreenMode);
            Assert.Equal(KeyboardScreenStopwatchAction.Reset, back.StopwatchAction);
            Assert.Equal(KeyboardScreenPanDirection.Up, back.PanDirection);
            Assert.Equal(40, back.PanStep);

            // An action from a file written before #519 takes the defaults.
            var fresh = new MacroAction();
            Assert.Equal(KeyboardScreenData.DefaultMode, fresh.ScreenMode);
            Assert.Equal(1000, fresh.ScreenFrameMs);
            Assert.Equal(MacroAction.DefaultPanStep, fresh.PanStep);
            Assert.Equal(KeyboardScreenService.MinFrameMs, new MacroAction { ScreenFrameMs = 1 }.ScreenFrameMs);
        }

        // ── Settings ──

        /// <summary>Each screen's choices are kept by its row, BlissBoxPortData's
        /// cleanup: an entry with no readable row, one left at every default
        /// and a second entry for one row go, and an unknown mode is put back
        /// to the default.</summary>
        [Fact]
        public void TheScreensList_IsCleanedOnLoad()
        {
            Guid g15 = Guid.NewGuid(), g19 = Guid.NewGuid(), arx = Guid.NewGuid();
            var list = KeyboardScreenData.Normalize(new[]
            {
                null,
                new KeyboardScreenData { Device = g19.ToString("N"), Mode = KeyboardScreenMode.Clock, PanX = -5 },
                new KeyboardScreenData { Device = g19.ToString("D"), Mode = KeyboardScreenMode.Picture },
                new KeyboardScreenData { Device = "not a device", Mode = KeyboardScreenMode.Clock },
                new KeyboardScreenData { Device = arx.ToString("D"), Mode = (KeyboardScreenMode)42, Picture = "map.png" },
                new KeyboardScreenData { Device = g15.ToString("D") },
            });
            Assert.Equal(2, list.Count);
            Assert.Equal(g19.ToString("D"), list[0].Device);
            Assert.Equal(KeyboardScreenMode.Clock, list[0].Mode);
            Assert.Equal(0, list[0].PanX);
            Assert.Equal(arx.ToString("D"), list[1].Device);
            Assert.Equal(KeyboardScreenData.DefaultMode, list[1].Mode);
            Assert.Empty(KeyboardScreenData.Normalize(null));
        }

        [Fact]
        public void TheScreens_RoundTripThroughTheSettingsXml()
        {
            Guid g510 = Guid.NewGuid(), strike7 = Guid.NewGuid();
            var data = new AppSettingsData
            {
                KeyboardScreensEnabled = true,
                KeyboardScreens = new[]
                {
                    new KeyboardScreenData { Device = g510.ToString("D"), Mode = KeyboardScreenMode.PlayTime },
                    new KeyboardScreenData { Device = strike7.ToString("D"), Mode = KeyboardScreenMode.Picture, Picture = "maps\\world.png", PanX = 300, PanY = 120 },
                },
                DefaultProfileKeyboardScreenPicture = "default.png",
                Strike7LibraryPath = "C:\\Tools\\Strike7API.dll",
            };
            var serializer = new XmlSerializer(typeof(AppSettingsData));
            using var writer = new StringWriter();
            serializer.Serialize(writer, data);
            string xml = writer.ToString();
            Assert.Contains("<KeyboardScreens>", xml);
            Assert.Contains("Device=\"" + strike7.ToString("D") + "\"", xml);
            var back = (AppSettingsData)serializer.Deserialize(new StringReader(xml));
            Assert.True(back.KeyboardScreensEnabled);
            Assert.Equal(2, back.KeyboardScreens.Length);
            Assert.Equal(KeyboardScreenMode.PlayTime, back.KeyboardScreens[0].Mode);
            Assert.Equal("maps\\world.png", back.KeyboardScreens[1].Picture);
            Assert.Equal(300, back.KeyboardScreens[1].PanX);
            Assert.Equal(120, back.KeyboardScreens[1].PanY);
            Assert.Equal("default.png", back.DefaultProfileKeyboardScreenPicture);
            Assert.Equal("C:\\Tools\\Strike7API.dll", back.Strike7LibraryPath);
        }

        [Fact]
        public void TheSwitch_IsOffByDefault_AndFeedsTheEngine()
        {
            var vm = new SettingsViewModel();
            Assert.False(vm.KeyboardScreensEnabled);
            vm.KeyboardScreensEnabled = true;
            try
            {
                Assert.True(KeyboardScreensRuntime.Enabled);
                vm.ResetSettingCommand.Execute(nameof(SettingsViewModel.KeyboardScreensEnabled));
                Assert.False(vm.KeyboardScreensEnabled);
                Assert.False(KeyboardScreensRuntime.Enabled);
            }
            finally
            {
                vm.KeyboardScreensEnabled = false;
            }
            string settings = File.ReadAllText(RepoFile("PadForge.App", "Services", "SettingsService.cs"));
            Assert.Contains("vm.KeyboardScreensEnabled = appSettings.KeyboardScreensEnabled;", settings);
            Assert.Contains("KeyboardScreensEnabled = vm.KeyboardScreensEnabled,", settings);
            Assert.Contains("vm.KeyboardScreens = KeyboardScreenData.Normalize(appSettings.KeyboardScreens);", settings);
        }

        // ── The stopwatch, a show and the pictures ──

        [Fact]
        public void TheStopwatch_StartsPausesAndResets()
        {
            KeyboardScreenService.Stopwatch(KeyboardScreenStopwatchAction.Reset, 0);
            KeyboardScreenService.Stopwatch(KeyboardScreenStopwatchAction.StartPause, 1_000);
            Assert.Equal(TimeSpan.FromSeconds(4), KeyboardScreenService.StopwatchTime(5_000));
            KeyboardScreenService.Stopwatch(KeyboardScreenStopwatchAction.StartPause, 5_000);
            Assert.Equal(TimeSpan.FromSeconds(4), KeyboardScreenService.StopwatchTime(60_000));
            KeyboardScreenService.Stopwatch(KeyboardScreenStopwatchAction.StartPause, 60_000);
            Assert.Equal(TimeSpan.FromSeconds(6), KeyboardScreenService.StopwatchTime(62_000));
            KeyboardScreenService.Stopwatch(KeyboardScreenStopwatchAction.Reset, 62_000);
            Assert.Equal(TimeSpan.Zero, KeyboardScreenService.StopwatchTime(90_000));
            Assert.Equal("1:05", KeyboardScreenService.FormatStopwatch(TimeSpan.FromSeconds(65)));
            Assert.Equal("1:00:05", KeyboardScreenService.FormatStopwatch(TimeSpan.FromSeconds(3605)));
        }

        [Fact]
        public void AShow_PlaysEachFrameForItsTime_ThenEnds()
        {
            byte[] a = { 1 }, b = { 2 };
            var show = new KeyboardScreenShow(new() { a, b }, 200, 2, 1_000);
            Assert.Same(a, show.Frame(1_000));
            Assert.Same(b, show.Frame(1_250));
            Assert.Same(a, show.Frame(1_450));
            Assert.Same(b, show.Frame(1_799));
            Assert.Null(show.Frame(1_800));
            // A frame time under the floor is held to it.
            var fast = new KeyboardScreenShow(new() { a }, 1, 1, 0);
            Assert.Same(a, fast.Frame(KeyboardScreenService.MinFrameMs - 1));
            Assert.Null(fast.Frame(KeyboardScreenService.MinFrameMs));
        }

        [Fact]
        public void TheRenderer_DrawsEachKindAtItsSize()
            => RunSta(() =>
            {
                foreach (var kind in KeyboardScreenFormats.Kinds)
                {
                    var (w, h) = KeyboardScreenFormats.SizeOf(kind);
                    var text = KeyboardScreenRenderer.RenderText("12:34", kind);
                    Assert.Equal(w * h * 4, text.Length);
                    if (KeyboardScreenFormats.IsMonochrome(kind))
                    {
                        // Pure black and white, some of each.
                        Assert.All(Enumerable.Range(0, w * h), p => Assert.True(text[p * 4] is 0 or 255));
                        Assert.Contains(Enumerable.Range(0, w * h), p => text[p * 4] == 0);
                        Assert.Contains(Enumerable.Range(0, w * h), p => text[p * 4] == 255);
                    }
                    else
                    {
                        // Light text on black.
                        Assert.Equal(0, text[0]);
                        Assert.Contains(Enumerable.Range(0, w * h), p => text[p * 4] > 200);
                    }
                }
            });

        [Fact]
        public void TheLightbarColor_FillsAColorScreen_AndDithersAMonochromeOne()
        {
            var color = KeyboardScreenRenderer.RenderColor(0x10, 0x80, 0xF0, KeyboardScreenKind.Color);
            Assert.Equal(0xF0, color[0]);
            Assert.Equal(0x80, color[1]);
            Assert.Equal(0x10, color[2]);
            Assert.Equal(255, color[3]);

            int Dark(byte r, byte g, byte b)
            {
                var mono = KeyboardScreenRenderer.RenderColor(r, g, b, KeyboardScreenKind.Monochrome);
                return Enumerable.Range(0, MonoW * MonoH).Count(p => mono[p * 4] == 0);
            }
            Assert.Equal(0, Dark(255, 255, 255));
            Assert.Equal(MonoW * MonoH, Dark(0, 0, 0));
            int half = Dark(128, 128, 128);
            Assert.InRange(half, MonoW * MonoH * 4 / 10, MonoW * MonoH * 6 / 10);
        }

        /// <summary>The Lightbar mode shows the lowest slot's color among the
        /// pads still on the slot their color came from, and forgets a pad
        /// that left. The pads here are this test's own, so a color the
        /// effects path records meanwhile is never one it reads.</summary>
        [Fact]
        public void TheLightbarColor_IsTheLowestSlotsStillOnIt_AndAPadThatLeftIsForgotten()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
            var where = new Dictionary<Guid, int> { [a] = 2, [b] = 1, [c] = 3 };
            bool OnItsSlot(Guid device, int slot) => where.TryGetValue(device, out int now) && now == slot;

            LightbarColors.Record(2, a, 1, 2, 3);
            LightbarColors.Record(1, b, 4, 5, 6);
            LightbarColors.Record(3, c, 7, 8, 9);
            Assert.True(LightbarColors.TryGetLowest(OnItsSlot, out var color));
            Assert.Equal(((byte)4, (byte)5, (byte)6), color);

            // b moves to slot 0: its color came from slot 1, so it is gone.
            where[b] = 0;
            Assert.True(LightbarColors.TryGetLowest(OnItsSlot, out color));
            Assert.Equal(((byte)1, (byte)2, (byte)3), color);
            where[b] = 1;
            Assert.True(LightbarColors.TryGetLowest(OnItsSlot, out color));
            Assert.Equal(((byte)1, (byte)2, (byte)3), color);

            // A new color on the slot takes over again.
            LightbarColors.Record(1, b, 10, 20, 30);
            Assert.True(LightbarColors.TryGetLowest(OnItsSlot, out color));
            Assert.Equal(((byte)10, (byte)20, (byte)30), color);

            where.Clear();
            Assert.False(LightbarColors.TryGetLowest(OnItsSlot, out _));
        }

        /// <summary>A screen whose row feeds slots follows the lowest of
        /// those slots, so player two's keyboard shows player two's color.
        /// A screen that feeds none follows the lowest slot of all.</summary>
        [Fact]
        public void TheLightbarColor_FollowsTheSlotsTheScreenFeeds()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            var where = new Dictionary<Guid, int> { [a] = 1, [b] = 2 };
            bool OnItsSlot(Guid device, int slot) => where.TryGetValue(device, out int now) && now == slot;
            LightbarColors.Record(1, a, 1, 2, 3);
            LightbarColors.Record(2, b, 4, 5, 6);
            try
            {
                Assert.True(LightbarColors.TryGetLowest(OnItsSlot, slot => slot == 2, out var color));
                Assert.Equal(((byte)4, (byte)5, (byte)6), color);
                Assert.True(LightbarColors.TryGetLowest(OnItsSlot, slot => slot == 1 || slot == 2, out color));
                Assert.Equal(((byte)1, (byte)2, (byte)3), color);
                Assert.False(LightbarColors.TryGetLowest(OnItsSlot, slot => slot == 7, out _));
                Assert.True(LightbarColors.TryGetLowest(OnItsSlot, null, out color));
                Assert.Equal(((byte)1, (byte)2, (byte)3), color);
            }
            finally
            {
                where.Clear();
                LightbarColors.TryGetLowest(OnItsSlot, out _);
            }
        }

        /// <summary>The screen service's check takes the device lock, and the
        /// effects dispatcher records a color while it holds that lock. The
        /// check runs outside the color list's lock, so the two never wait
        /// on each other. Here the check waits for a lock that a second
        /// thread holds while it records. The waits are bounded, so the old
        /// order fails the test and unwinds instead of hanging the
        /// run.</summary>
        [Fact]
        public void TheLightbarCheck_RunsOutsideTheLock_SoARecordUnderTheDeviceLockGoesThrough()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            var deviceLock = new object();
            using var checking = new ManualResetEventSlim();
            using var deviceLockHeld = new ManualResetEventSlim();
            bool tookDeviceLock = false, recorded = false;
            LightbarColors.Record(1, a, 1, 2, 3);
            bool OnItsSlot(Guid device, int slot)
            {
                if (device != a) return false;
                checking.Set();
                if (!Monitor.TryEnter(deviceLock, 2000)) return true;
                tookDeviceLock = true;
                Monitor.Exit(deviceLock);
                return true;
            }
            var dispatcher = new Thread(() =>
            {
                lock (deviceLock)
                {
                    deviceLockHeld.Set();
                    if (!checking.Wait(2000)) return;
                    LightbarColors.Record(2, b, 4, 5, 6);
                    recorded = true;
                }
            }) { IsBackground = true };
            try
            {
                dispatcher.Start();
                Assert.True(deviceLockHeld.Wait(2000));
                Assert.True(LightbarColors.TryGetLowest(OnItsSlot, slot => slot == 1, out var color));
                Assert.True(dispatcher.Join(4000));
                Assert.True(recorded);
                Assert.True(tookDeviceLock);
                Assert.Equal(((byte)1, (byte)2, (byte)3), color);
            }
            finally
            {
                dispatcher.Join(4000);
                LightbarColors.TryGetLowest((device, _) => device != a && device != b, out _);
            }
        }

        /// <summary>A pad judged gone is removed only while its entry is the
        /// one judged: a color recorded for it while the list was read, from
        /// the slot it moved to, stays.</summary>
        [Fact]
        public void ALightbarColor_RecordedWhileTheListIsRead_IsKept()
        {
            Guid a = Guid.NewGuid();
            LightbarColors.Record(1, a, 1, 2, 3);
            bool moved = false;
            bool OnItsSlot(Guid device, int slot)
            {
                if (device != a) return false;
                if (slot == 2) return true;
                // a moved to slot 2 and its new color lands before the
                // stale slot 1 entry is dropped.
                if (!moved) { moved = true; LightbarColors.Record(2, a, 7, 8, 9); }
                return false;
            }
            try
            {
                Assert.False(LightbarColors.TryGetLowest(OnItsSlot, out _));
                Assert.True(LightbarColors.TryGetLowest(OnItsSlot, out var color));
                Assert.Equal(((byte)7, (byte)8, (byte)9), color);
            }
            finally
            {
                LightbarColors.TryGetLowest((device, _) => device != a, out _);
            }
        }

        [Fact]
        public void APicture_LargerThanTheScreen_PansToItsEdgesAndNoFurther()
            => RunSta(() =>
            {
                var pixels = new byte[600 * 400 * 4];
                for (int p = 0; p < 600 * 400; p++) pixels[p * 4 + 3] = 255;
                // A white column at x 500 marks where the window has moved.
                for (int y = 0; y < 400; y++) { int i = (y * 600 + 500) * 4; pixels[i] = pixels[i + 1] = pixels[i + 2] = 255; }
                var picture = System.Windows.Media.Imaging.BitmapSource.Create(600, 400, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, pixels, 600 * 4);
                Assert.Equal((120, 80), KeyboardScreenRenderer.PanLimits(picture, KeyboardScreenKind.Strike7));
                Assert.Equal((280, 160), KeyboardScreenRenderer.PanLimits(picture, KeyboardScreenKind.Color));
                Assert.Equal((0, 0), KeyboardScreenRenderer.PanLimits(null, KeyboardScreenKind.Color));

                // At the far edge the column sits at x 500 - 120 = 380.
                var frame = KeyboardScreenRenderer.RenderWindow(picture, KeyboardScreenKind.Strike7, 999, 999);
                Assert.True(frame[(10 * 480 + 380) * 4] > 200);
                Assert.True(frame[(10 * 480 + 379) * 4] < 50);
            });

        /// <summary>A picture that will not load at first, a file on a drive
        /// that is not ready yet, is read again after the retry time and
        /// then shows. Until then the screen says no picture, and the file is
        /// not read four times a second.</summary>
        [Fact]
        public void APictureThatFailedToLoad_IsReadAgainAfterTheRetryTime()
            => RunSta(() =>
            {
                string path = Path.Combine(Path.GetTempPath(), "PadForge.Tests.KeyboardScreen." + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    var service = new KeyboardScreenService(new SettingsViewModel(), null);
                    var screen = new KeyboardScreenInfo("test-retry", KeyboardScreenKind.Color, "Test Screen", "Test");
                    var data = new KeyboardScreenData { Device = screen.RowGuid.ToString("D"), Mode = KeyboardScreenMode.Picture, Picture = path };

                    Assert.Null(service.Compose(screen, data, 1000, out var blank));
                    Assert.Equal(KeyboardScreenService.Blank.NoPicture, blank);

                    WriteTestPng(path);
                    Assert.Null(service.Compose(screen, data, 1000 + KeyboardScreenService.PictureRetryMs - 1, out blank));
                    Assert.Equal(KeyboardScreenService.Blank.NoPicture, blank);
                    Assert.NotNull(service.Compose(screen, data, 1000 + KeyboardScreenService.PictureRetryMs, out blank));
                    Assert.Equal(KeyboardScreenService.Blank.None, blank);
                }
                finally
                {
                    File.Delete(path);
                }
            });

        /// <summary>The engine's stop forgets the loaded pictures, so the next
        /// start reads each file again at once.</summary>
        [Fact]
        public void Reset_ForgetsAPictureThatFailedToLoad()
            => RunSta(() =>
            {
                string path = Path.Combine(Path.GetTempPath(), "PadForge.Tests.KeyboardScreen." + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    var service = new KeyboardScreenService(new SettingsViewModel(), null);
                    var screen = new KeyboardScreenInfo("test-reset", KeyboardScreenKind.Color, "Test Screen", "Test");
                    var data = new KeyboardScreenData { Device = screen.RowGuid.ToString("D"), Mode = KeyboardScreenMode.Picture, Picture = path };

                    Assert.Null(service.Compose(screen, data, 1000, out _));
                    WriteTestPng(path);
                    service.Reset();
                    Assert.NotNull(service.Compose(screen, data, 1001, out var blank));
                    Assert.Equal(KeyboardScreenService.Blank.None, blank);
                }
                finally
                {
                    File.Delete(path);
                }
            });

        /// <summary>The editor lists only the entries that hold a picture, so
        /// Remove counts those. An entry that holds none, from a file edited
        /// by hand, does not shift the picture the click removes, and it goes
        /// with the edit.</summary>
        [Fact]
        public void RemovingAScreenFrame_RemovesThePictureTheEditorShowsAtThatPlace()
            => RunSta(() =>
            {
                var kind = KeyboardScreenKind.Monochrome;
                var (width, height) = KeyboardScreenFormats.SizeOf(kind);
                var dark = new byte[width * height * 4];
                var light = new byte[width * height * 4];
                for (int p = 0; p < width * height; p++) { dark[p * 4 + 3] = 255; light[p * 4] = light[p * 4 + 1] = light[p * 4 + 2] = light[p * 4 + 3] = 255; }
                string first = KeyboardScreenService.EncodeFrame(dark, kind), second = KeyboardScreenService.EncodeFrame(light, kind);
                var action = new MacroAction { ScreenKind = kind, ScreenFrames = "not-a-picture," + first + "," + second };
                Assert.Equal(2, action.ScreenFrameImages.Count);

                action.RemoveScreenFrame(0);
                Assert.Equal(second, action.ScreenFrames);
                Assert.Single(action.ScreenFrameImages);
            });

        /// <summary>Download Strike7API.dll drops the copy the user picked
        /// only when this click produced the library. The driver operation
        /// reports a failure and returns, and a failed download leaves an
        /// earlier one on disk, so a test of the file's presence dropped the
        /// pick after a failure too.</summary>
        [Fact]
        public async System.Threading.Tasks.Task AFailedStrike7Download_KeepsThePickedLibrary()
        {
            static System.Threading.Tasks.Task Run(Action operation) => System.Threading.Tasks.Task.Run(() =>
            {
                try { operation(); }
                catch (InvalidOperationException) { }
            });

            var settings = new SettingsViewModel { Strike7LibraryPath = @"C:\Picked\Strike7API.dll" };
            int dirty = 0;
            await KeyboardScreenService.DownloadStrike7Library(Run, () => throw new InvalidOperationException("offline"),
                settings, () => dirty++);
            Assert.Equal(@"C:\Picked\Strike7API.dll", settings.Strike7LibraryPath);
            Assert.Equal(0, dirty);

            await KeyboardScreenService.DownloadStrike7Library(Run, () => { }, settings, () => dirty++);
            Assert.Null(settings.Strike7LibraryPath);
            Assert.Equal(1, dirty);

            await KeyboardScreenService.DownloadStrike7Library(Run, () => { }, settings, () => dirty++);
            Assert.Equal(1, dirty);

            string window = File.ReadAllText(RepoFile("PadForge.App", "MainWindow.KeyboardScreens.cs"));
            Assert.Contains("KeyboardScreenService.DownloadStrike7Library(", window);
            Assert.DoesNotContain("File.Exists(KeyboardScreenService.DownloadedStrike7Library", window);
        }

        // ── Helpers ──

        private static void WriteTestPng(string path)
        {
            var pixels = new byte[8 * 8 * 4];
            for (int p = 0; p < 8 * 8; p++) pixels[p * 4 + 3] = 255;
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(8, 8, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, 8 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        private static int CountOf(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "render run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// The rows whose objects PadForge names itself keep those objects in the
    /// settings file and are not opened again when the language changes. A
    /// row that published translated names kept the language it was found
    /// in. These rows publish English names, the same in every language, and
    /// the picker and the Devices page translate them, as they do the NFC
    /// row's "Any NFC Tag". Strings are read at run time, so the culture
    /// tests run apart.
    /// </summary>
    [Collection("CultureSwitching")]
    public class PublishedObjectNameTests
    {
        private static readonly System.Globalization.CultureInfo English = System.Globalization.CultureInfo.GetCultureInfo("en");
        private static readonly System.Globalization.CultureInfo German = System.Globalization.CultureInfo.GetCultureInfo("de");

        /// <summary>Each object's name with the language at
        /// <paramref name="culture"/> when the row publishes it.</summary>
        private static string[] NamesIn(System.Globalization.CultureInfo culture, Func<DeviceObjectItem[]> objects)
        {
            Strings.ChangeCulture(culture);
            return objects().Select(o => o.Name).ToArray();
        }

        private static void InEachLanguage(Action body)
        {
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try { body(); }
            finally { Strings.ChangeCulture(before); }
        }

        [Fact]
        public void AKeyboardScreensKeys_AreNamedInEnglish_AndShownInTheLanguage() => InEachLanguage(() =>
        {
            foreach (var kind in new[] { KeyboardScreenKind.Monochrome, KeyboardScreenKind.Color })
            {
                var row = new KeyboardScreenDevice(new KeyboardScreenInfo("test-names-" + kind, kind, "Test Screen", "Test"));
                string[] english = NamesIn(English, row.GetDeviceObjects);
                string[] german = NamesIn(German, row.GetDeviceObjects);
                Assert.Equal(english, german);
                for (int i = 0; i < german.Length; i++)
                {
                    string shown = PadForge.Common.MappingDisplayResolver.LocalizeObjectName(german[i]);
                    Assert.Equal(TranslatedKeyName(kind, i), shown);
                    Assert.NotEqual(german[i], shown);
                }
            }
            Assert.Equal("Screen Key 1", KeyboardScreenDevice.KeyName(KeyboardScreenKind.Monochrome, KeyboardScreenFormats.MonoKey1));
            Assert.Equal("Screen OK", KeyboardScreenDevice.KeyName(KeyboardScreenKind.Color, KeyboardScreenFormats.G19Ok));
        });

        /// <summary>The name each key showed when its row was found in the
        /// current language, before the names were published in
        /// English.</summary>
        private static string TranslatedKeyName(KeyboardScreenKind kind, int button)
        {
            var s = Strings.Instance;
            if (kind == KeyboardScreenKind.Monochrome)
                return button == KeyboardScreenFormats.MonoAppletKey
                    ? s.KeyboardScreenKey_Applet
                    : string.Format(s.KeyboardScreenKey_Mono_Format, button - KeyboardScreenFormats.MonoKey1 + 1);
            return button switch
            {
                KeyboardScreenFormats.G19Left => s.KeyboardScreenKey_G19Left,
                KeyboardScreenFormats.G19Right => s.KeyboardScreenKey_G19Right,
                KeyboardScreenFormats.G19Ok => s.KeyboardScreenKey_G19Ok,
                KeyboardScreenFormats.G19Cancel => s.KeyboardScreenKey_G19Cancel,
                KeyboardScreenFormats.G19Up => s.KeyboardScreenKey_G19Up,
                KeyboardScreenFormats.G19Down => s.KeyboardScreenKey_G19Down,
                KeyboardScreenFormats.G19Menu => s.KeyboardScreenKey_G19Menu,
                _ => s.KeyboardScreenKey_G19Settings,
            };
        }

        [Fact]
        public void AFalconsAxesAndButtons_AreNamedInEnglish_AndShownInTheLanguage() => InEachLanguage(() =>
        {
            string[] english = NamesIn(English, PadForge.Common.Input.Falcon.FalconDevice.DeviceObjects);
            string[] german = NamesIn(German, PadForge.Common.Input.Falcon.FalconDevice.DeviceObjects);
            Assert.Equal(english, german);
            var s = Strings.Instance;
            Assert.Equal(new[] { s.Falcon_Axis_LeftRight, s.Falcon_Axis_UpDown, s.Falcon_Axis_InOut,
                    string.Format(s.DevObj_Button, 1), string.Format(s.DevObj_Button, 2),
                    string.Format(s.DevObj_Button, 3), string.Format(s.DevObj_Button, 4) },
                german.Select(PadForge.Common.MappingDisplayResolver.LocalizeObjectName).ToArray());
            Assert.Equal(new[] { "Left/Right", "Up/Down", "In/Out", "Button 1", "Button 2", "Button 3", "Button 4" }, german);
        });

        [Fact]
        public void AMicrophonesAnyPhrase_IsNamedInEnglish_AndShownInTheLanguage() => InEachLanguage(() =>
        {
            var row = new MicrophoneInputDevice("test-names-endpoint", "Test Microphone");
            string english = NamesIn(English, row.GetDeviceObjects)[0];
            string german = NamesIn(German, row.GetDeviceObjects)[0];
            Assert.Equal("Any Phrase", german);
            Assert.Equal(english, german);
            Assert.Equal(Strings.Instance.Voice_AnyPhrase, PadForge.Common.MappingDisplayResolver.LocalizeObjectName(german));
            Assert.NotEqual(german, Strings.Instance.Voice_AnyPhrase);
        });

        /// <summary>The Devices page's named chips show each name the way the
        /// mapping picker does.</summary>
        [Fact]
        public void TheDevicesPagesNamedChips_ShowTheNamesInTheLanguage() => InEachLanguage(() =>
        {
            Strings.ChangeCulture(German);
            var row = new KeyboardScreenDevice(new KeyboardScreenInfo("test-chips", KeyboardScreenKind.Color, "Test Screen", "Test"));
            var vm = new DevicesViewModel();
            vm.RebuildNamedButtons(row.GetDeviceObjects());
            Assert.Equal(KeyboardScreenFormats.KeyCountOf(KeyboardScreenKind.Color), vm.HandheldButtons.Count);
            Assert.Equal(Strings.Instance.KeyboardScreenKey_G19Ok, vm.HandheldButtons[KeyboardScreenFormats.G19Ok].Name);
            Assert.NotEqual("Screen OK", vm.HandheldButtons[KeyboardScreenFormats.G19Ok].Name);
        });

        /// <summary>A macro's trigger text names a raw button the way the
        /// mapping picker does, so a key a row publishes in English reads in
        /// the language there too, and a button the device names none of
        /// reads "Button N".</summary>
        [Fact]
        public void AMacroTriggersButtonLabel_IsTheNameThePickerShows() => InEachLanguage(() =>
        {
            Strings.ChangeCulture(German);
            var key = new DeviceObjectItem { Name = "Screen Key 1", ObjectTypeGuid = ObjectGuid.Button, InputIndex = 0 };
            Assert.Equal(PadForge.Common.MappingDisplayResolver.LocalizeObjectName("Screen Key 1"), MacroItem.ButtonLabel(key, 0));
            Assert.NotEqual("Screen Key 1", MacroItem.ButtonLabel(key, 0));
            Assert.Equal(string.Format(Strings.Instance.Macro_Button_Format, 4), MacroItem.ButtonLabel(null, 4));
            Assert.Equal(string.Format(Strings.Instance.Macro_Button_Format, 4),
                MacroItem.ButtonLabel(new DeviceObjectItem { Name = "", InputIndex = 4 }, 4));
        });

        /// <summary>Every trigger text builder labels a raw button through
        /// <see cref="MacroItem.ButtonLabel"/>: the summary, the chips, the
        /// recorded combination and the single-button label in MacroItem, and
        /// the recorder's line in InputService. A builder that printed the
        /// object's name as it is stored would show a published English name
        /// untranslated.</summary>
        [Fact]
        public void EveryTriggerTextBuilder_LabelsButtonsThroughButtonLabel()
        {
            string macro = File.ReadAllText(RepoPath("PadForge.App", "ViewModels", "MacroItem.cs"));
            string service = File.ReadAllText(RepoPath("PadForge.App", "Services", "InputService.cs"));
            Assert.DoesNotContain("? obj.Name", macro);
            Assert.DoesNotContain("? obj.Name", service);
            Assert.Equal(5, CountOf(macro, "ButtonLabel(obj, "));
            Assert.Equal(1, CountOf(service, "MacroItem.ButtonLabel(obj, "));
        }

        private static int CountOf(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static string RepoPath(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }
    }

    /// <summary>
    /// Remove Device takes the settings kept by the row's GUID with the row.
    /// A keyboard screen's choices go. A Bliss-Box port's choices go back to
    /// the defaults and its copy of the adapter's own picture stays, as Reset
    /// to Defaults keeps it. Another row's entries stay as they are.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class RemovedDeviceScreenDataTests
    {
        [Fact]
        public void RemoveDevice_DropsTheRowsScreenChoices_AndKeepsTheAdaptersPicture()
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var vm = new MainViewModel();
                var service = new DeviceService(vm, new SettingsService(vm));
                service.WireEvents();

                Guid screen = KeyboardScreenInfo.RowGuidOf("test-remove-screen"), otherScreen = KeyboardScreenInfo.RowGuidOf("test-keep-screen");
                Guid port = Guid.NewGuid(), otherPort = Guid.NewGuid();
                vm.Settings.KeyboardScreens.Add(new KeyboardScreenData { Device = screen.ToString("D"), Mode = KeyboardScreenMode.Clock });
                vm.Settings.KeyboardScreens.Add(new KeyboardScreenData { Device = otherScreen.ToString("D"), Mode = KeyboardScreenMode.Clock });
                string picture = Convert.ToBase64String(new byte[192]);
                vm.Settings.BlissBoxPorts.Add(new BlissBoxPortData
                {
                    Device = port.ToString("D"), ScreenMode = DreamcastScreenMode.Clock, Picture = picture,
                    NativeArrows = true, AdapterPicture = picture,
                });
                vm.Settings.BlissBoxPorts.Add(new BlissBoxPortData { Device = otherPort.ToString("D"), ScreenMode = DreamcastScreenMode.Clock });

                void Remove(Guid row)
                {
                    var item = new DeviceRowViewModel { InstanceGuid = row };
                    vm.Devices.Devices.Add(item);
                    vm.Devices.SelectedDevice = item;
                    vm.Devices.RemoveDeviceCommand.Execute(null);
                }

                Remove(screen);
                Assert.DoesNotContain(vm.Settings.KeyboardScreens, d => d.Device == screen.ToString("D"));
                Assert.Contains(vm.Settings.KeyboardScreens, d => d.Device == otherScreen.ToString("D") && d.Mode == KeyboardScreenMode.Clock);

                Remove(port);
                var kept = Assert.Single(vm.Settings.BlissBoxPorts, p => p.Device == port.ToString("D"));
                Assert.Equal(DreamcastScreenMode.Adapter, kept.ScreenMode);
                Assert.Null(kept.Picture);
                Assert.False(kept.NativeArrows);
                Assert.Equal(picture, kept.AdapterPicture);
                Assert.Contains(vm.Settings.BlissBoxPorts, p => p.Device == otherPort.ToString("D") && p.ScreenMode == DreamcastScreenMode.Clock);

                // With no copy of the adapter's picture, nothing is left to keep.
                kept.AdapterPicture = null;
                kept.ScreenMode = DreamcastScreenMode.Clock;
                Remove(port);
                Assert.DoesNotContain(vm.Settings.BlissBoxPorts, p => p.Device == port.ToString("D"));
            }
            finally
            {
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
            }
        }
    }
}
