using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Serialization;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Keyboard + Mouse slot's virtual HID keyboards and mice (HIDMaestro
    /// 1.12.0, HIDMaestro#67): the key map, what each profile can send, the
    /// two senders, the hooks' account of PadForge's own HID output, the slot
    /// setting that picks the devices, and the drawings the preview shows.
    /// Every test here that touches OwnHidOutput, a process-wide static the
    /// low-level hooks read, sits in the statics collection and resets it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class KbmHidDeviceTests
    {
        private static readonly Assembly Sdk = typeof(HMContext).Assembly;

        private static HMProfile Profile(string id)
        {
            var p = HMaestroProfileCatalog.GetProfileById(id);
            Assert.NotNull(p);
            return p;
        }

        private static KbmHidKeyMap.HidKey Kb(int usage) => new(0x07, (ushort)usage);
        private static KbmHidKeyMap.HidKey Cc(int usage) => new(0x0C, (ushort)usage);

        // ── The key map ─────────────────────────────────────────────────

        [Fact]
        public void FixedKeys_SendTheirOwnUsages()
        {
            Assert.Equal(Kb(0x29), KbmHidKeyMap.Nominal(0x1B));   // Escape
            Assert.Equal(Kb(0x68), KbmHidKeyMap.Nominal(0x7C));   // F13
            Assert.Equal(Kb(0x73), KbmHidKeyMap.Nominal(0x87));   // F24
            Assert.Equal(Kb(0x58), KbmHidKeyMap.Nominal(0x88));   // Numpad Enter
            Assert.Equal(Kb(0x88), KbmHidKeyMap.Nominal(0x15));   // Kana
            Assert.Equal(Kb(0x8A), KbmHidKeyMap.Nominal(0x1C));   // Convert
            Assert.Equal(Kb(0x8B), KbmHidKeyMap.Nominal(0x1D));   // No Convert
            Assert.Equal(Kb(0x35), KbmHidKeyMap.Nominal(0xF3));   // Hankaku/Zenkaku at the Grave position
            Assert.Equal(Kb(0x64), KbmHidKeyMap.Nominal(0xE2));   // the non-US \ | key
            Assert.Equal(Kb(0xE1), KbmHidKeyMap.Nominal(0x10));   // Shift as the left one
            Assert.Equal(Kb(0xE5), KbmHidKeyMap.Nominal(0xA1));
            Assert.True(KbmHidKeyMap.Nominal(0x07).IsNone);
        }

        [Fact]
        public void MediaKeys_SendConsumerUsages()
        {
            var expected = new Dictionary<int, int>
            {
                [0xA6] = 0x224, [0xA7] = 0x225, [0xA8] = 0x227, [0xA9] = 0x226, [0xAA] = 0x221,
                [0xAB] = 0x22A, [0xAC] = 0x223, [0xAD] = 0xE2, [0xAE] = 0xEA, [0xAF] = 0xE9,
                [0xB0] = 0xB5, [0xB1] = 0xB6, [0xB2] = 0xB7, [0xB3] = 0xCD, [0xB4] = 0x18A,
                [0xB5] = 0x183, [0xB6] = 0x194, [0xB7] = 0x192,
            };
            foreach (var (vk, usage) in expected)
                Assert.Equal(Cc(usage), KbmHidKeyMap.Nominal(vk));
        }

        /// <summary>The scan code the hooks see for each usage is the one
        /// SDL's table reads back as that usage, for every key a character
        /// position can take.</summary>
        [Fact]
        public void ScanCodes_RoundTripThroughSdlsTable()
        {
            for (int u = 0; u <= 0xFF; u++)
            {
                if (!KbmHidKeyMap.IsCharacterUsage((ushort)u)) continue;
                if (u == 0x32) continue;   // shares scan 0x2B with 0x31, as SDL's table has it
                int scan = KbmHidKeyMap.ScanKeyOf(Kb(u));
                Assert.True(scan >= 0, $"usage {u:X2} has no scan code");
                Assert.Equal(Kb(u), KbmHidKeyMap.UsageOfScanCode((uint)scan));
            }
            Assert.Equal(-1, KbmHidKeyMap.ScanKeyOf(Cc(0xE9)));
        }

        [Fact]
        public void DrawnKeys_StandForTheirFixedTargets()
        {
            IntPtr layout = KbmHidKeyMap.UiLayout();
            Assert.Equal(0x1B, KbmHidKeyMap.TargetOf(Kb(0x29), layout));
            Assert.Equal(0xA0, KbmHidKeyMap.TargetOf(Kb(0xE1), layout));   // the left key, never generic Shift
            Assert.Equal(0xA3, KbmHidKeyMap.TargetOf(Kb(0xE4), layout));
            Assert.Equal(0x88, KbmHidKeyMap.TargetOf(Kb(0x58), layout));
            Assert.Equal(0x7C, KbmHidKeyMap.TargetOf(Kb(0x68), layout));
            Assert.Equal(0x1C, KbmHidKeyMap.TargetOf(Kb(0x8A), layout));
            Assert.Equal(0xB3, KbmHidKeyMap.TargetOf(Cc(0xCD), layout));
            Assert.Equal(0xAF, KbmHidKeyMap.TargetOf(Cc(0xE9), layout));
            Assert.Equal(-1, KbmHidKeyMap.TargetOf(Cc(0x30), layout));
        }

        /// <summary>A character key the input layout reaches resolves back to
        /// the key it was drawn on, whatever layout the test machine has.</summary>
        [Fact]
        public void CharacterKeys_RoundTripThroughTheInputLayout()
        {
            IntPtr layout = KbmHidKeyMap.UiLayout();
            int reached = 0;
            for (int u = 0x04; u <= 0x1D; u++)
            {
                int vk = KbmHidKeyMap.TargetOf(Kb(u), layout);
                Assert.True(vk >= 0, $"letter usage {u:X2} reaches no row");
                if (KbmHidKeyMap.Resolve(vk, layout).Equals(Kb(u))) reached++;
            }
            Assert.True(reached >= 20, $"only {reached} of 26 letters round-trip");
        }

        // ── What a profile can send ─────────────────────────────────────

        private static object Packer(byte[] descriptor)
        {
            var type = Sdk.GetType("HIDMaestro.Internal.KeyboardMousePacker", true);
            return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { descriptor }, null);
        }

        private static T Get<T>(object o, string name)
        {
            var t = o.GetType();
            var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null) return (T)prop.GetValue(o);
            return (T)t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
        }

        /// <summary>KbmDeviceCaps reads each profile's descriptor the way
        /// HIDMaestro's packer does: the same collections, the same mouse
        /// buttons, wheel and tilt, for all 103 keyboard and mouse profiles.</summary>
        [Fact]
        public void Caps_AgreeWithHidMaestrosPacker_ForEveryKeyboardAndMouse()
        {
            var profiles = HMaestroProfileCatalog.KeyboardProfiles.Concat(HMaestroProfileCatalog.MouseProfiles).ToList();
            Assert.Equal(103, profiles.Count);
            foreach (var p in profiles)
            {
                byte[] d = p.GetDescriptorBytes();
                var caps = KbmDeviceCaps.FromDescriptor(d);
                var packer = Packer(d);
                Assert.True(Get<bool>(packer, "HasKeyboard") == caps.HasKeyboard, p.Id + " keyboard");
                Assert.True(Get<bool>(packer, "HasMouse") == caps.HasMouse, p.Id + " mouse");
                Assert.True(Get<bool>(packer, "HasConsumer") == caps.HasConsumer, p.Id + " consumer");
                Assert.True(Get<bool>(packer, "HasKeyboardLeds") == caps.HasKeyboardLeds, p.Id + " leds");
                var buttons = (System.Collections.IDictionary)Get<object>(packer, "_mouseButtons");
                for (int b = 1; b <= KbmDeviceCaps.WindowsMouseButtons; b++)
                    Assert.True(buttons.Contains(b) == caps.CanSendMouseButton(b - 1), $"{p.Id} button {b}");
                Assert.True((Get<object>(packer, "_wheel") != null) == caps.HasWheel, p.Id + " wheel");
                Assert.True((Get<object>(packer, "_pan") != null) == caps.HasPan, p.Id + " pan");
                if (string.Equals(p.Type, "keyboard", StringComparison.OrdinalIgnoreCase))
                    Assert.True(caps.HasKeyboard, p.Id + " is a keyboard with no keys");
                else
                    Assert.True(caps.HasMouse, p.Id + " is a mouse with no mouse");
            }
        }

        /// <summary>A boot keyboard holds six keys in its array and lists every
        /// letter. The positive control for the rollover below.</summary>
        [Fact]
        public void Caps_ReadABootKeyboardsArray()
        {
            var caps = KbmDeviceCaps.For(Profile("cherry-g83-rs6000"));
            Assert.True(caps.HasKeyboard);
            Assert.Equal(6, caps.KeyArraySlots);
            Assert.False(caps.NKeyRollover);
            for (int u = 0x04; u <= 0x1D; u++) Assert.True(caps.CanSendKey((byte)u));
            for (int m = 0xE0; m <= 0xE7; m++) Assert.True(caps.IsKeyBit((byte)m));
            Assert.False(caps.IsKeyBit(0x04));
            Assert.False(caps.HasConsumer);
        }

        // ── The hooks' account ──────────────────────────────────────────

        [Fact]
        public void OwnKeys_StayPadForgesUntilWindowsDeliversTheRelease()
        {
            OwnHidOutput.ResetForTests();
            try
            {
                Assert.False(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, true));
                OwnHidOutput.NoteKeyDown(0x41, 0x1E);
                Assert.True(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, true));
                Assert.True(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, true));   // a repeat
                OwnHidOutput.NoteKeyUp(0x41, 0x1E);
                // A repeat already on its way when the release was sent.
                Assert.True(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, true));
                Assert.True(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, false));
                // The release is spent, and the key is the user's again.
                Assert.False(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, false));
                Assert.False(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, true));
            }
            finally { OwnHidOutput.ResetForTests(); }
        }

        [Fact]
        public void OwnKeys_MatchByScanCodeWhenWindowsReportsAnotherCode()
        {
            OwnHidOutput.ResetForTests();
            try
            {
                // Shift sent for the generic index reaches the hook as the
                // left key's code.
                OwnHidOutput.NoteKeyDown(0x10, OwnHidOutput.ScanKey(0x2A, false));
                Assert.True(OwnHidOutput.IsOwnKey(0xA0, 0x2A, false, true));
                // An E0 key keys on its extended form only.
                OwnHidOutput.NoteKeyDown(0x2D, OwnHidOutput.ScanKey(0x52, true));
                Assert.True(OwnHidOutput.IsOwnKey(0x2D, 0x52, true, true));
                Assert.False(OwnHidOutput.IsOwnKey(0x60, 0x52, false, true));
            }
            finally { OwnHidOutput.ResetForTests(); }
        }

        [Fact]
        public void AnUndeliveredRelease_ExpiresWithThePressItEnds()
        {
            OwnHidOutput.ResetForTests();
            try
            {
                long now = 1_000_000;
                OwnHidOutput.Clock = () => now;
                OwnHidOutput.NoteKeyDown(0x41, 0x1E);
                OwnHidOutput.NoteKeyUp(0x41, 0x1E);
                now += OwnHidOutput.PendingWindowMs + 1;
                Assert.False(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, true));
                Assert.False(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, false));

                OwnHidOutput.NoteButton(0, down: true);
                Assert.True(OwnHidOutput.IsOwnButton(0, isDown: true));
                OwnHidOutput.NoteButton(0, down: false);
                Assert.True(OwnHidOutput.IsOwnButton(0, isDown: true));
                now += OwnHidOutput.PendingWindowMs + 1;
                Assert.False(OwnHidOutput.IsOwnButton(0, isDown: true));
                Assert.False(OwnHidOutput.IsOwnButton(0, isDown: false));
            }
            finally { OwnHidOutput.ResetForTests(); }
        }

        [Fact]
        public void AReleaseWithNoPress_IsNotRecorded()
        {
            OwnHidOutput.ResetForTests();
            try
            {
                OwnHidOutput.NoteKeyUp(0x41, 0x1E);
                Assert.False(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, false));
                OwnHidOutput.NoteButton(2, down: false);
                Assert.False(OwnHidOutput.IsOwnButton(2, isDown: false));
            }
            finally { OwnHidOutput.ResetForTests(); }
        }

        // ── The senders ─────────────────────────────────────────────────

        private sealed class FakeSink : IKbmHidSink
        {
            private readonly object _gate = new();
            private readonly List<string> _log = new();
            public int Disposes;

            public void SubmitKeyboard(HMKeyModifiers modifiers, HMKey[] keys)
            {
                lock (_gate) _log.Add($"K {(byte)modifiers:X2}:" + string.Join(",", keys.Select(k => ((byte)k).ToString("X2"))));
            }

            public void SubmitConsumer(ushort[] usages)
            {
                lock (_gate) _log.Add("C " + string.Join(",", usages.Select(u => u.ToString("X"))));
            }

            public void SubmitMouse(in HMMouseState s)
            {
                lock (_gate) _log.Add($"M {(uint)s.Buttons:X} {s.X} {s.Y} {s.Wheel} {s.Pan}");
            }

            public HMKeyboardLeds? KeyboardLeds { get; set; }
            public event Action<HMKeyboardLeds> KeyboardLedsChanged;
            public void Raise(HMKeyboardLeds leds) => KeyboardLedsChanged?.Invoke(leds);
            public void Dispose() => Interlocked.Increment(ref Disposes);

            public string[] Log { get { lock (_gate) return _log.ToArray(); } }

            public string[] WaitFor(int count)
            {
                Assert.True(SpinWait.SpinUntil(() => Log.Length >= count, 5000),
                    $"expected {count} reports, got: {string.Join(" | ", Log)}");
                Thread.Sleep(50);   // nothing more arrives
                return Log;
            }
        }

        private static ulong[] Keys(params int[] vks)
        {
            var w = new ulong[4];
            foreach (int vk in vks) w[vk / 64] |= 1UL << (vk % 64);
            return w;
        }

        private static void Press(KbmHidKeyboard kb, params int[] vks)
        {
            var w = Keys(vks);
            kb.Submit(w[0], w[1], w[2], w[3]);
        }

        [Fact]
        public void Keyboard_SendsTheWholeStateAndTellsTheHooks()
        {
            OwnHidOutput.ResetForTests();
            var sink = new FakeSink();
            var kb = new KbmHidKeyboard(sink, KbmDeviceCaps.For(Profile("corsair-k55-rgb-pro-xt")), "k55", 0,
                KbmHidKeyMap.Nominal);
            try
            {
                Press(kb, 0x41, 0xA0);           // A and Left Shift
                Assert.Equal(new[] { "K 02:04" }, sink.WaitFor(1));
                Assert.True(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, true));
                Assert.True(OwnHidOutput.IsOwnKey(0xA0, 0x2A, false, true));
                Press(kb, 0x41, 0xA0, 0xB3);     // and Play/Pause
                Assert.Equal("C CD", sink.WaitFor(2)[1]);
                Press(kb);
                var log = sink.WaitFor(4);
                Assert.Contains("K 00:", log);
                Assert.Contains("C ", log);
            }
            finally
            {
                kb.Dispose();
                OwnHidOutput.ResetForTests();
            }
            Assert.Equal(1, sink.Disposes);
        }

        /// <summary>Two rows on one usage (generic and left Shift) press it
        /// once and let go of it once, after the second lets go.</summary>
        [Fact]
        public void Keyboard_SharesAUsageBetweenTwoKeys()
        {
            OwnHidOutput.ResetForTests();
            var sink = new FakeSink();
            var kb = new KbmHidKeyboard(sink, KbmDeviceCaps.For(Profile("cherry-g83-rs6000")), "g83", 0,
                KbmHidKeyMap.Nominal);
            try
            {
                Press(kb, 0x10, 0xA0);
                Assert.Equal(new[] { "K 02:" }, sink.WaitFor(1));
                Press(kb, 0xA0);
                Thread.Sleep(100);
                Assert.Single(sink.Log);
                Assert.True(OwnHidOutput.IsOwnKey(0xA0, 0x2A, false, true));
                Press(kb);
                Assert.Equal("K 00:", sink.WaitFor(2)[1]);
            }
            finally
            {
                kb.Dispose();
                OwnHidOutput.ResetForTests();
            }
        }

        /// <summary>Past the array's six slots a key waits for one, as Windows
        /// would show it after a boot keyboard's ErrorRollOver.</summary>
        [Fact]
        public void Keyboard_HoldsTheSeventhKeyUntilASlotFrees()
        {
            OwnHidOutput.ResetForTests();
            var sink = new FakeSink();
            var kb = new KbmHidKeyboard(sink, KbmDeviceCaps.For(Profile("cherry-g83-rs6000")), "g83", 0,
                KbmHidKeyMap.Nominal);
            try
            {
                Press(kb, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47);      // A to G
                Assert.Equal("K 00:04,05,06,07,08,09", sink.WaitFor(1)[0]);
                Assert.False(OwnHidOutput.IsOwnKey(0x47, 0x22, false, true));
                Press(kb, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47);
                Assert.Equal("K 00:05,06,07,08,09,0A", sink.WaitFor(2)[1]);
                Assert.True(OwnHidOutput.IsOwnKey(0x47, 0x22, false, true));
            }
            finally
            {
                kb.Dispose();
                OwnHidOutput.ResetForTests();
            }
        }

        [Fact]
        public void Keyboard_PressesNothingTheDeviceCannotSend()
        {
            OwnHidOutput.ResetForTests();
            var sink = new FakeSink();
            // The G83 has no consumer collection, so no media keys.
            var kb = new KbmHidKeyboard(sink, KbmDeviceCaps.For(Profile("cherry-g83-rs6000")), "g83", 0,
                KbmHidKeyMap.Nominal);
            try
            {
                Press(kb, 0xAF);
                Thread.Sleep(150);
                Assert.Empty(sink.Log);
                Press(kb, 0xAF, 0x41);   // a positive control in the same window
                Assert.Equal(new[] { "K 00:04" }, sink.WaitFor(1));
            }
            finally
            {
                kb.Dispose();
                OwnHidOutput.ResetForTests();
            }
        }

        [Fact]
        public void Keyboard_DisposeHandsTheHeldKeysToTheHooksBeforeTheDeviceGoes()
        {
            OwnHidOutput.ResetForTests();
            var sink = new FakeSink();
            var kb = new KbmHidKeyboard(sink, KbmDeviceCaps.For(Profile("cherry-g83-rs6000")), "g83", 0,
                KbmHidKeyMap.Nominal);
            try
            {
                Press(kb, 0x41);
                sink.WaitFor(1);
                kb.Dispose();
                Assert.Equal(1, sink.Disposes);
                // The device's release frame reaches the hook as PadForge's.
                Assert.True(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, false));
                Assert.False(OwnHidOutput.IsOwnKey(0x41, 0x1E, false, true));
            }
            finally { OwnHidOutput.ResetForTests(); }
        }

        [Fact]
        public void Keyboard_KeepsTheLatestLedState()
        {
            var sink = new FakeSink { KeyboardLeds = HMKeyboardLeds.NumLock };
            var kb = new KbmHidKeyboard(sink, KbmDeviceCaps.For(Profile("cherry-g83-rs6000")), "g83", 0,
                KbmHidKeyMap.Nominal);
            try
            {
                Assert.Equal(HMKeyboardLeds.NumLock, kb.Leds);
                sink.Raise(HMKeyboardLeds.NumLock | HMKeyboardLeds.CapsLock);
                Assert.Equal(HMKeyboardLeds.NumLock | HMKeyboardLeds.CapsLock, kb.Leds);
            }
            finally { kb.Dispose(); }
        }

        [Fact]
        public void Mouse_SendsOnlyWhatItHas()
        {
            OwnHidOutput.ResetForTests();
            var sink = new FakeSink();
            // One button, no wheel.
            var mouse = new KbmHidMouse(sink, KbmDeviceCaps.For(Profile("apple-pro-mouse")), "pro", 0);
            try
            {
                mouse.SetButtons(0b11);          // left and right
                mouse.AddWheel(2);
                mouse.AddMotion(5, -3);
                // The sender may take the buttons and the motion in one report
                // or two, so the totals are what the device was told.
                string[] log = null;
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    log = sink.Log;
                    var r = log.Select(l => l.Split(' ')).ToList();
                    return r.Sum(x => int.Parse(x[2])) == 5 && r.Sum(x => int.Parse(x[3])) == -3;
                }, 5000), string.Join(" | ", log ?? Array.Empty<string>()));
                Assert.All(log, l => Assert.StartsWith("M 1 ", l));
                Assert.All(log, l => Assert.EndsWith(" 0 0", l));
                Assert.True(OwnHidOutput.IsOwnButton(0, isDown: true));
                Assert.False(OwnHidOutput.IsOwnButton(2, isDown: true));
            }
            finally
            {
                mouse.Dispose();
                OwnHidOutput.ResetForTests();
            }
            Assert.Equal(1, sink.Disposes);
        }

        [Fact]
        public void Mouse_KeepsEveryClickShorterThanAReport()
        {
            OwnHidOutput.ResetForTests();
            var sink = new FakeSink();
            var mouse = new KbmHidMouse(sink, KbmDeviceCaps.For(Profile("logitech-g502-hero")), "g502", 0);
            try
            {
                mouse.SetButtons(0b1);
                mouse.SetButtons(0b0);
                mouse.SetButtons(0b1);
                mouse.SetButtons(0b0);
                var log = sink.WaitFor(4);
                Assert.Equal(new[] { "M 1 0 0 0 0", "M 0 0 0 0 0", "M 1 0 0 0 0", "M 0 0 0 0 0" }, log);
            }
            finally
            {
                mouse.Dispose();
                OwnHidOutput.ResetForTests();
            }
        }

        // ── The controller ──────────────────────────────────────────────

        [Fact]
        public void Controller_MakesEachHalfUnderItsOwnKey_AndRoutesToIt()
        {
            OwnHidOutput.ResetForTests();
            var sinks = new Dictionary<string, FakeSink>();
            var vc = new KeyboardMouseVirtualController(3, null,
                Profile("corsair-k55-rgb-pro-xt"), Profile("logitech-g502-hero"))
            {
                CreateSinkSeam = (p, key) => { var s = new FakeSink(); sinks[key] = s; return s; },
                KeyResolverSeam = KbmHidKeyMap.Nominal,
            };
            try
            {
                vc.Connect();
                Assert.True(vc.HasHidDevices);
                Assert.Equal(new[] { "padforge:pad3:KeyboardMouse:keyboard", "padforge:pad3:KeyboardMouse:mouse" },
                    sinks.Keys.OrderBy(k => k, StringComparer.Ordinal));
                var raw = new KbmRawState();
                raw.SetKey(0x41, true);
                raw.SetMouseButton(0, true);
                vc.SubmitKbmState(raw);
                Assert.Equal(new[] { "K 00:04" }, sinks["padforge:pad3:KeyboardMouse:keyboard"].WaitFor(1));
                Assert.Equal(new[] { "M 1 0 0 0 0" }, sinks["padforge:pad3:KeyboardMouse:mouse"].WaitFor(1));
                vc.Disconnect();
                Assert.All(sinks.Values, s => Assert.Equal(1, s.Disposes));
            }
            finally
            {
                vc.Dispose();
                OwnHidOutput.ResetForTests();
            }
        }

        /// <summary>A keyboard with a TrackPoint picked for both halves is one
        /// device, as the hardware is, and goes when both halves let go.</summary>
        [Fact]
        public void Controller_MakesOneDeviceForAComboPickedTwice()
        {
            OwnHidOutput.ResetForTests();
            var made = new List<FakeSink>();
            var combo = Profile("lenovo-thinkpad-compact-bluetooth-keyboard");
            var vc = new KeyboardMouseVirtualController(1, null, combo, combo)
            {
                CreateSinkSeam = (p, key) => { var s = new FakeSink(); made.Add(s); return s; },
                KeyResolverSeam = KbmHidKeyMap.Nominal,
            };
            try
            {
                vc.Connect();
                Assert.Single(made);
                vc.Disconnect();
                Assert.Equal(1, made[0].Disposes);
            }
            finally
            {
                vc.Dispose();
                OwnHidOutput.ResetForTests();
            }
        }

        [Fact]
        public void Controller_MakesNoDeviceInTheTestHost()
        {
            var vc = new KeyboardMouseVirtualController(0, null, Profile("cherry-g83-rs6000"), null);
            Assert.Throws<InvalidOperationException>(() => vc.Connect());
            Assert.False(vc.IsConnected);
            vc.Dispose();
        }

        [Fact]
        public void Controller_OnWindowsInput_HasNoDevices()
        {
            var vc = new KeyboardMouseVirtualController(0);
            Assert.False(vc.HasHidDevices);
            Assert.Equal("", vc.KeyboardDeviceId);
            Assert.Equal("", vc.MouseDeviceId);
            Assert.Null(vc.KeyboardLeds);
        }

        // ── The slot setting ────────────────────────────────────────────

        [Fact]
        public void Devices_DefaultToWindowsInput()
        {
            var cfg = new KbmSlotConfig();
            Assert.Equal(KbmSlotConfig.WindowsInput, cfg.KeyboardDevice);
            Assert.Equal(KbmSlotConfig.WindowsInput, cfg.MouseDevice);
            Assert.False(cfg.UsesDevices);
        }

        [Fact]
        public void AMouseRidingTheKeyboard_FollowsIt()
        {
            var cfg = new KbmSlotConfig { KeyboardDevice = "lenovo-thinkpad-compact-bluetooth-keyboard" };
            cfg.MouseDevice = cfg.KeyboardDevice;
            cfg.KeyboardDevice = "hhkb-studio";              // has a pointing stick
            Assert.Equal("hhkb-studio", cfg.MouseDevice);
            cfg.KeyboardDevice = "cherry-g83-rs6000";        // has none
            Assert.Equal(KbmSlotConfig.WindowsInput, cfg.MouseDevice);
            cfg.MouseDevice = "logitech-g502-hero";
            cfg.KeyboardDevice = "hhkb-studio";              // a mouse of its own stays
            Assert.Equal("logitech-g502-hero", cfg.MouseDevice);
        }

        [Fact]
        public void AKeyboardRidingTheMouse_FollowsIt()
        {
            var cfg = new KbmSlotConfig { MouseDevice = "corsair-scimitar-rgb-elite" };
            cfg.KeyboardDevice = cfg.MouseDevice;
            cfg.MouseDevice = "logitech-g502-hero";
            Assert.Equal(KbmSlotConfig.WindowsInput, cfg.KeyboardDevice);
        }

        [Fact]
        public void APickerNullWrite_ChangesNothing()
        {
            var cfg = new KbmSlotConfig { KeyboardDevice = "cherry-g83-rs6000", MouseDevice = "logitech-g502-hero" };
            cfg.KeyboardDevice = null;
            cfg.MouseDevice = null;
            Assert.Equal("cherry-g83-rs6000", cfg.KeyboardDevice);
            Assert.Equal("logitech-g502-hero", cfg.MouseDevice);
        }

        [Fact]
        public void SetDevices_TakesThePairAsGiven()
        {
            var cfg = new KbmSlotConfig { KeyboardDevice = "hhkb-studio" };
            cfg.MouseDevice = "hhkb-studio";
            var raised = new List<string>();
            cfg.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            cfg.SetDevices("cherry-g83-rs6000", "hhkb-studio");
            Assert.Equal("cherry-g83-rs6000", cfg.KeyboardDevice);
            Assert.Equal("hhkb-studio", cfg.MouseDevice);
            Assert.Equal(new[] { nameof(KbmSlotConfig.AvailableMouseDevices), nameof(KbmSlotConfig.KeyboardDevice) }, raised);
            cfg.ResetToDefaults();
            Assert.False(cfg.UsesDevices);
        }

        [Fact]
        public void SlotData_RoundTripsTheDevices_AndOldFilesReadAsWindowsInput()
        {
            var ser = new XmlSerializer(typeof(KbmSlotConfigData));
            var plain = new StringWriter();
            ser.Serialize(plain, new KbmSlotConfigData { SlotIndex = 2 });
            Assert.DoesNotContain("KeyboardDevice", plain.ToString());
            Assert.DoesNotContain("MouseDevice", plain.ToString());

            var picked = new StringWriter();
            ser.Serialize(picked, new KbmSlotConfigData { SlotIndex = 2, KeyboardDevice = "keychron-q6", MouseDevice = "razer-cobra" });
            var back = (KbmSlotConfigData)ser.Deserialize(new StringReader(picked.ToString()));
            Assert.Equal("keychron-q6", back.KeyboardDevice);
            Assert.Equal("razer-cobra", back.MouseDevice);

            var old = (KbmSlotConfigData)ser.Deserialize(new StringReader(
                "<KbmSlotConfigData SlotIndex=\"1\" Surfaces=\"Both\" SocdMode=\"Off\" SocdPairs=\"87:83\" />"));
            Assert.Equal(KbmSlotConfig.WindowsInput, old.KeyboardDevice);
            Assert.Equal(KbmSlotConfig.WindowsInput, old.MouseDevice);
        }

        [Fact]
        public void Rows_HideWhatTheDeviceCannotSend()
        {
            var cfg = new KbmSlotConfig();
            Assert.True(cfg.DeviceCanSend("KbmKeyAF"));
            Assert.True(cfg.DeviceCanSend("KbmMBtn4"));

            cfg.SetDevices("cherry-g83-rs6000", "apple-pro-mouse");
            Assert.True(cfg.DeviceCanSend("KbmKey41"));
            Assert.False(cfg.DeviceCanSend("KbmKeyAF"));     // no media keys
            Assert.True(cfg.DeviceCanSend("KbmMBtn0"));
            Assert.False(cfg.DeviceCanSend("KbmMBtn1"));     // one button
            Assert.False(cfg.DeviceCanSend("KbmScroll"));    // no wheel
            Assert.False(cfg.DeviceCanSend("KbmScrollNeg"));
            Assert.True(cfg.DeviceCanSend("KbmMouseX"));
            Assert.True(cfg.DeviceCanSend("VrLTrigger"));

            cfg.SetDevices("corsair-k55-rgb-pro-xt", "logitech-g502-hero");
            Assert.True(cfg.DeviceCanSend("KbmKeyAF"));
            Assert.True(cfg.DeviceCanSend("KbmMBtn4"));
            Assert.True(cfg.DeviceCanSend("KbmScroll"));
        }

        // ── The dropdowns ───────────────────────────────────────────────

        [Fact]
        public void Dropdowns_StartWithWindowsInput_ThenTheCatalog()
        {
            var kb = KbmDeviceCatalog.KeyboardOptions(KbmSlotConfig.WindowsInput);
            Assert.Equal(KbmSlotConfig.WindowsInput, kb[0].Value);
            Assert.Equal(Strings.Instance.Pad_Kbm_Device_WindowsInput, kb[0].Name);
            Assert.Equal(1 + HMaestroProfileCatalog.KeyboardProfiles.Count, kb.Count);
            var mice = KbmDeviceCatalog.MouseOptions(KbmSlotConfig.WindowsInput);
            Assert.Equal(1 + HMaestroProfileCatalog.MouseProfiles.Count, mice.Count);
        }

        [Fact]
        public void Dropdowns_OfferTheOtherHalfsDeviceOnlyWhenItCarriesThisHalf()
        {
            string trackPoint = "lenovo-thinkpad-compact-bluetooth-keyboard";
            var mice = KbmDeviceCatalog.MouseOptions(trackPoint);
            Assert.Equal(trackPoint, mice[1].Value);
            Assert.Equal(Strings.Instance.Pad_Kbm_Device_SameAsKeyboard, mice[1].Name);
            Assert.DoesNotContain(KbmDeviceCatalog.MouseOptions("cherry-g83-rs6000"), o => o.Value == "cherry-g83-rs6000");

            string scimitar = "corsair-scimitar-rgb-elite";
            var keyboards = KbmDeviceCatalog.KeyboardOptions(scimitar);
            Assert.Equal(scimitar, keyboards[1].Value);
            Assert.Equal(Strings.Instance.Pad_Kbm_Device_SameAsMouse, keyboards[1].Name);
            // A keyboard riding the mouse never offers itself back.
            Assert.DoesNotContain(KbmDeviceCatalog.MouseOptions(scimitar), o => o.Name == Strings.Instance.Pad_Kbm_Device_SameAsKeyboard);
        }

        [Fact]
        public void DropdownEntries_AreEqualByValue()
        {
            var a = new KbmDeviceOption { Value = "razer-cobra", Name = "Razer Cobra" };
            var b = new KbmDeviceOption { Value = "RAZER-COBRA", Name = "another name" };
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, new KbmDeviceOption { Value = "razer-viper-mini" });
        }

        // ── The drawings and the key lists ──────────────────────────────

        [Fact]
        public void EveryKeyboardProfile_HasADrawing()
        {
            var catalog = HMaestroProfileCatalog.KeyboardProfiles.Select(p => p.Id)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            var drawn = KeyboardFamilies.ProfileIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(catalog, drawn, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void EveryDrawnKey_IsAKeyOrInert()
        {
            foreach (var id in KeyboardFamilies.ProfileIds)
            {
                var family = KeyboardFamilies.For(id);
                Assert.NotEmpty(family.Keys);
                foreach (var k in family.Keys)
                {
                    if (k.Usage == 0) Assert.NotNull(k.Token);
                    else Assert.True(k.Page == 0x07 || k.Page == 0x0C, $"{family.Name} {k.Usage:X6}");
                    Assert.True(k.W > 0 && k.H > 0 && k.X >= 0 && k.Y >= 0);
                }
            }
        }

        [Fact]
        public void TheJapaneseBoard_DrawsItsOwnKeys()
        {
            var jp = KeyboardFamilies.For("hhkb-professional-jp");
            foreach (int u in new[] { 0x87, 0x88, 0x89, 0x8A, 0x8B })
                Assert.Contains(jp.Keys, k => k.Page == 0x07 && k.Id == u);
            Assert.Equal("変換", KeyboardFamilies.FixedLegend(jp.Keys.First(k => k.Id == 0x8A), KeyboardFamilies.Legends.Pc));
            var mac = KeyboardFamilies.For("apple-magic-keyboard-a1644");
            Assert.Equal(KeyboardFamilies.Legends.Mac, mac.Legends);
            Assert.True(mac.Approximate);
            Assert.Equal("⌘", KeyboardFamilies.FixedLegend(mac.Keys.First(k => k.Id == 0xE3), mac.Legends));
        }

        [Fact]
        public void TheExtraKeys_AreInTheEngineTheGridAndTheSocdList()
        {
            var codes = (byte[])typeof(InputManager)
                .GetField("KbmKeyVkCodes", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);
            var vm = RestoredPad.Build(0, VirtualControllerType.KeyboardMouse);
            var socd = KbmSlotConfig.GetKeyOptions();
            foreach (byte vk in KbmExtraKeys.Vks)
            {
                Assert.Contains(vk, codes);
                Assert.True(InputManager.IsKbmKeyTarget(vk));
                Assert.Contains(vm.Mappings, m => m.TargetSettingName == $"KbmKey{vk:X2}");
                Assert.Contains(socd, o => o.Vk == vk);
                Assert.False(string.IsNullOrWhiteSpace(KbmExtraKeys.Label(vk)));
                Assert.False(KbmHidKeyMap.Nominal(vk).IsNone, $"{vk:X2} sends nothing on a device");
            }
            Assert.Equal(KbmExtraKeys.Vks.Length, KbmExtraKeys.Vks.Distinct().Count());
            Assert.Equal(codes.Length, codes.Distinct().Count());
        }

        // ── The hooks and Raw Input leave PadForge's own devices alone ──

        private static bool[] HookedKeys()
        {
            var keys = new bool[256];
            InputHookManager.MergeHookedKeyState(keys, keys.Length);
            return keys;
        }

        private static bool[] HookedButtons()
        {
            var buttons = new bool[5];
            InputHookManager.MergeHookedMouseState(buttons, buttons.Length);
            return buttons;
        }

        /// <summary>A key PadForge's virtual keyboard holds passes the hook
        /// untouched, untagged as a HID key arrives, while the same key from
        /// a real keyboard is consumed: the SendInput test
        /// (AuditDelta20261003Tests.TheKeyboardHook_PassesPadForgesOwnKeys)
        /// for the HID path.</summary>
        [Fact]
        public void TheKeyboardHook_PassesTheVirtualKeyboardsKeys()
        {
            const int A = 0x41;
            const uint ScanA = 0x1E;
            var hooks = new InputHookManager();
            hooks.SetSuppressedKeys(new HashSet<int> { A });
            bool paused = InputHookManager.ConsumePaused;
            InputHookManager.ConsumePaused = false; // as while the engine evaluates
            OwnHidOutput.ResetForTests();
            try
            {
                OwnHidOutput.NoteKeyDown(A, OwnHidOutput.ScanKey((int)ScanA, false));
                Assert.False(InputHookManager.HandleKeyboardEvent(A, ScanA, 0, IntPtr.Zero, isDown: true));
                Assert.False(HookedKeys()[A]);
                OwnHidOutput.NoteKeyUp(A, OwnHidOutput.ScanKey((int)ScanA, false));
                Assert.False(InputHookManager.HandleKeyboardEvent(A, ScanA, 0, IntPtr.Zero, isDown: false));

                // The same key from a real keyboard, same window.
                Assert.True(InputHookManager.HandleKeyboardEvent(A, ScanA, 0, IntPtr.Zero, isDown: true));
                Assert.True(HookedKeys()[A]);
                Assert.True(InputHookManager.HandleKeyboardEvent(A, ScanA, 0, IntPtr.Zero, isDown: false));
            }
            finally
            {
                hooks.SetSuppressedKeys(new HashSet<int>());
                InputHookManager.ConsumePaused = paused;
                OwnHidOutput.ResetForTests();
            }
        }

        [Fact]
        public void TheMouseHook_PassesTheVirtualMousesClicks()
        {
            var hooks = new InputHookManager();
            hooks.SetSuppressedMouseButtons(new HashSet<int> { 0 });
            bool paused = InputHookManager.ConsumePaused;
            InputHookManager.ConsumePaused = false; // as while the engine evaluates
            OwnHidOutput.ResetForTests();
            try
            {
                OwnHidOutput.NoteButton(0, down: true);
                Assert.False(InputHookManager.HandleMouseButton(0, true, IntPtr.Zero, 0));
                Assert.False(HookedButtons()[0]);
                OwnHidOutput.NoteButton(0, down: false);
                Assert.False(InputHookManager.HandleMouseButton(0, false, IntPtr.Zero, 0));

                Assert.True(InputHookManager.HandleMouseButton(0, true, IntPtr.Zero, 0));
                Assert.True(HookedButtons()[0]);
                Assert.True(InputHookManager.HandleMouseButton(0, false, IntPtr.Zero, 0));
            }
            finally
            {
                hooks.SetSuppressedMouseButtons(new HashSet<int>());
                InputHookManager.ConsumePaused = paused;
                OwnHidOutput.ResetForTests();
            }
        }

        /// <summary>Raw Input records of a device PadForge made reach no
        /// keyboard or mouse state, and a real device's reach its own, in the
        /// same window.</summary>
        [Fact]
        public void RawInput_LeavesTheVirtualDevicesOut()
        {
            IntPtr own = (IntPtr)0x7A110, real = (IntPtr)0x7A120;
            var savedTest = RawInputListener.IsOwnVirtualDevicePath;
            var savedFeed = RawInputListener.ICadeFeed;
            RawInputListener.IsOwnVirtualDevicePath = _ => false;
            RawInputListener.ICadeFeed = (h, make, flags) => false;
            RawInputListener.SetOwnVirtualDeviceForTests(own, true);
            RawInputListener.SetOwnVirtualDeviceForTests(real, false);
            try
            {
                RawInputListener.ApplyKeyboardRecord(own, 0x1E, 0, 0x41);
                RawInputListener.ApplyKeyboardRecord(real, 0x1E, 0, 0x41);
                var keys = new bool[256];
                RawInputListener.GetKeyboardState(own, keys, keys.Length);
                Assert.False(keys[0x41]);
                RawInputListener.GetKeyboardState(real, keys, keys.Length);
                Assert.True(keys[0x41]);

                RawInputListener.ApplyMouseRecord(own, 0, 0x0001, 0, 0, 0, 0);
                RawInputListener.ApplyMouseRecord(real, 0, 0x0001, 0, 0, 0, 0);
                var buttons = new bool[5];
                RawInputListener.GetMouseButtons(own, buttons);
                Assert.False(buttons[0]);
                RawInputListener.GetMouseButtons(real, buttons);
                Assert.True(buttons[0]);
            }
            finally
            {
                RawInputListener.ApplyKeyboardRecord(real, 0x1E, 0x01, 0x41);
                RawInputListener.ApplyMouseRecord(real, 0, 0x0002, 0, 0, 0, 0);
                RawInputListener.SetOwnVirtualDeviceForTests(own, null);
                RawInputListener.SetOwnVirtualDeviceForTests(real, null);
                RawInputListener.IsOwnVirtualDevicePath = savedTest;
                RawInputListener.ICadeFeed = savedFeed;
            }
        }

        // ── The engine's capture ────────────────────────────────────────

        [Fact]
        public void TheBuild_LeavesOutAHalfTheSlotDoesNotDrive()
        {
            var im = new InputManager();
            var cfg = new KbmSlotConfig();
            cfg.SetDevices("cherry-g83-rs6000", "logitech-g502-hero");
            im._kbmConfigs[4] = cfg;
            Assert.True(im.DesiredKbmBuild(4).Matches("cherry-g83-rs6000", "logitech-g502-hero"));
            cfg.Surfaces = "KeyboardOnly";
            Assert.True(im.DesiredKbmBuild(4).Matches("cherry-g83-rs6000", ""));
            cfg.Surfaces = "MouseOnly";
            Assert.True(im.DesiredKbmBuild(4).Matches("", "LOGITECH-G502-HERO"));
            Assert.False(default(InputManager.KbmBuild).UsesDevices);
            Assert.Equal(-1, im.KbmKeyboardLedsOf(4));
        }
    }
}
