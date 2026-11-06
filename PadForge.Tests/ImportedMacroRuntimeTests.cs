using System;
using System.Linq;
using System.Text;
using System.Threading;
using PadForge.Common.ConfigImport;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>#561: runs what the importers write through the macro engine.
    /// A macro that reads right can still hold a key forever: each action keeps
    /// its own latch, so a release written as a later action, or a virtual
    /// button latch that only flips, has to be seen working.</summary>
    [Collection("SettingsManagerStatics")]
    public class ImportedMacroRuntimeTests
    {
        // VK_NONAME, "reserved, no effect": a real SendInput with an inert key,
        // so a test never types into the host.
        private const int InertKey = 0xFC;

        private static MacroItem Item(MacroTriggerMode mode, MacroRepeatMode repeat, int pairId, params MacroAction[] actions)
        {
            var m = new MacroItem
            {
                Name = "T",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = repeat,
                ConsumeTriggerButtons = false,
                PairId = pairId,
            };
            foreach (var a in actions) m.Actions.Add(a);
            return m;
        }

        private static MacroAction Latch(MacroActionType type, MacroLatchDirection direction, ushort flags = 0, int key = 0, MacroMouseButton mouse = MacroMouseButton.Left)
            => new() { Type = type, LatchDirection = direction, ButtonFlags = flags, KeyCode = key, MouseButton = mouse };

        private static MacroAction Wait(int ms) => new() { Type = MacroActionType.Delay, DurationMs = ms };

        private static ushort Tick(InputManager im, MacroItem[] macros, bool held)
        {
            im._desiredLatchedKeys.Clear();
            var gp = new Gamepad { Buttons = held ? Gamepad.A : (ushort)0 };
            im.EvaluateSlotMacros(ref gp, macros);
            // The engine's frame settle: what the frame wants held against what
            // is held. Both keys here are inert, so nothing reaches the host.
            im.ReconcileLatchedKeys();
            return gp.Buttons;
        }

        private static InputManager Engine(MacroItem[] macros)
        {
            var im = new InputManager();
            im.MacroSnapshots[0] = macros;
            return im;
        }

        private static MacroItem[] SingleBesideHold(ushort holdTrigger, string holdLayer = "")
        {
            var single = Item(MacroTriggerMode.SinglePress, MacroRepeatMode.Once, 0,
                Latch(MacroActionType.ToggleVcButton, MacroLatchDirection.On, flags: Gamepad.X));
            var hold = Item(MacroTriggerMode.HoldForMs, MacroRepeatMode.Once, 0,
                Latch(MacroActionType.ToggleVcButton, MacroLatchDirection.On, flags: Gamepad.Y));
            single.TriggerDoublePressMs = 100;
            hold.TriggerHoldMs = 300;
            hold.TriggerButtons = holdTrigger;
            hold.LayerMask = holdLayer;
            return new[] { single, hold };
        }

        [Fact]
        public void SingleBesideALongPress_APressHeldPastItIsOnlyTheLongPress()
        {
            var macros = SingleBesideHold(Gamepad.A);
            var now = DateTime.UtcNow;
            var im = Engine(macros);
            im.SinglePressUtcNow = () => now;

            Tick(im, macros, true);
            now = now.AddMilliseconds(150);
            Assert.Equal(0, Tick(im, macros, true) & Gamepad.X);

            // The hold's own clock crosses.
            macros[1].TriggerHoldStartUtc = DateTime.UtcNow.AddMilliseconds(-1000);
            Assert.NotEqual(0, Tick(im, macros, true) & Gamepad.Y);
            now = now.AddMilliseconds(400);
            Assert.Equal(0, Tick(im, macros, true) & Gamepad.X);
            Tick(im, macros, false);
            Assert.Equal(0, Tick(im, macros, false) & Gamepad.X);
        }

        [Fact]
        public void SingleBesideALongPress_APressLetGoBeforeItIsTheSingle()
        {
            var macros = SingleBesideHold(Gamepad.A);
            var now = DateTime.UtcNow;
            var im = Engine(macros);
            im.SinglePressUtcNow = () => now;

            Tick(im, macros, true);
            now = now.AddMilliseconds(200);
            Assert.Equal(0, Tick(im, macros, true) & Gamepad.X);
            Assert.NotEqual(0, Tick(im, macros, false) & Gamepad.X);
            Assert.Equal(0, Tick(im, macros, false) & Gamepad.Y);
        }

        [Fact]
        public void SingleBesideALongPress_ShorterThanTheWindow_ReleaseAfterItFiresNoSingle()
        {
            var macros = SingleBesideHold(Gamepad.A);
            macros[0].TriggerDoublePressMs = 400;
            var now = DateTime.UtcNow;
            var im = Engine(macros);
            im.SinglePressUtcNow = () => now;

            Tick(im, macros, true);
            macros[1].TriggerHoldStartUtc = DateTime.UtcNow.AddMilliseconds(-1000);
            Assert.NotEqual(0, Tick(im, macros, true) & Gamepad.Y);
            Tick(im, macros, false);
            now = now.AddMilliseconds(500);
            Assert.Equal(0, Tick(im, macros, false) & Gamepad.X);
        }

        [Theory]
        [InlineData(Gamepad.B, "")]
        [InlineData(Gamepad.A, "Shift1")]
        public void SingleBesideAnotherTriggersOrLayersLongPress_StillFiresHeld(ushort holdTrigger, string holdLayer)
        {
            var macros = SingleBesideHold(holdTrigger, holdLayer);
            var now = DateTime.UtcNow;
            var im = Engine(macros);
            im.SinglePressUtcNow = () => now;

            Tick(im, macros, true);
            now = now.AddMilliseconds(150);
            Assert.NotEqual(0, Tick(im, macros, true) & Gamepad.X);
        }

        /// <summary>reWASD's single, double and long on one button, from the
        /// file through the importer and the settings load to the engine.</summary>
        private static MacroItem[] ImportedThreeGestures()
        {
            string Map(string type, int scan) => "{\"condition\":{\"mask\":{\"id\":1,\"activator\":{\"type\":\"" + type + "\",\"mode\":\"onetime\"}}},\"macros\":[{\"keyboard\":{\"buttonId\":" + scan + "}}]}";
            string json = "{\"schemaVersion\":\"3.0\",\"devices\":{\"hardware\":[{\"id\":1,\"name\":\"gamepad\"}]},\"masks\":[{\"id\":1,\"set\":[{\"deviceId\":1,\"buttonId\":1}]}],\"mappings\":["
                + Map("single", 30) + "," + Map("double", 31) + "," + Map("long", 32) + "]}";
            var data = ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(json), "x.rewasd").Profile.Macros;
            Assert.Equal(3, data.Length);
            var macros = data.Select(d => SettingsService.LoadMacroFromData(d, VirtualControllerType.Xbox, null)).ToArray();
            foreach (var m in macros)
            {
                // The file's trigger reads a real device. The engine test drives
                // the same trigger on all three through Gamepad.A, and each
                // gesture's output becomes a virtual button it can see.
                m.TriggerInputs = null;
                m.TriggerSource = MacroTriggerSource.OutputController;
                m.TriggerButtons = Gamepad.A;
                m.LayerMask = "";
                m.PadIndex = 0;
                ushort flag = m.TriggerMode == MacroTriggerMode.SinglePress ? Gamepad.X
                    : m.TriggerMode == MacroTriggerMode.DoublePress ? Gamepad.Y : Gamepad.B;
                m.Actions.Clear();
                m.Actions.Add(Latch(MacroActionType.ToggleVcButton, MacroLatchDirection.On, flags: flag));
            }
            return macros;
        }

        [Fact]
        public void ImportedSingleDoubleLong_AHoldIsOnlyTheLong()
        {
            var macros = ImportedThreeGestures();
            var single = macros.Single(m => m.TriggerMode == MacroTriggerMode.SinglePress);
            var hold = macros.Single(m => m.TriggerMode == MacroTriggerMode.HoldForMs);
            var now = DateTime.UtcNow;
            var im = Engine(macros);
            im.SinglePressUtcNow = () => now;

            Tick(im, macros, true);
            now = now.AddMilliseconds(single.TriggerDoublePressMs + 50);
            Assert.Equal(0, Tick(im, macros, true) & Gamepad.X);
            hold.TriggerHoldStartUtc = DateTime.UtcNow.AddMilliseconds(-hold.TriggerHoldMs - 50);
            Assert.NotEqual(0, Tick(im, macros, true) & Gamepad.B);
            Tick(im, macros, false);
            now = now.AddMilliseconds(100);
            Assert.Equal(0, Tick(im, macros, false) & Gamepad.X);
        }

        [Fact]
        public void ImportedSingleDoubleLong_ATapIsOnlyTheSingle()
        {
            var macros = ImportedThreeGestures();
            var single = macros.Single(m => m.TriggerMode == MacroTriggerMode.SinglePress);
            var now = DateTime.UtcNow;
            var im = Engine(macros);
            im.SinglePressUtcNow = () => now;

            Tick(im, macros, true);
            Tick(im, macros, false);
            now = now.AddMilliseconds(single.TriggerDoublePressMs + 50);
            ushort buttons = Tick(im, macros, false);
            Assert.NotEqual(0, buttons & Gamepad.X);
            Assert.Equal(0, buttons & Gamepad.Y);
            Assert.Equal(0, buttons & Gamepad.B);
        }

        [Fact]
        public void VirtualButton_SetInOneAction_ClearedInALater_OneInTheSameMacro()
        {
            var macros = new[]
            {
                Item(MacroTriggerMode.OnPress, MacroRepeatMode.Once, 0,
                    Latch(MacroActionType.ToggleVcButton, MacroLatchDirection.On, flags: Gamepad.Y),
                    Wait(40),
                    Latch(MacroActionType.ToggleVcButton, MacroLatchDirection.Off, flags: Gamepad.Y)),
            };
            var im = Engine(macros);

            Assert.NotEqual(0, Tick(im, macros, true) & Gamepad.Y);
            Assert.NotEqual(0, Tick(im, macros, true) & Gamepad.Y);
            Thread.Sleep(60);
            Tick(im, macros, true);
            Tick(im, macros, true);
            Assert.Equal(0, Tick(im, macros, false) & Gamepad.Y);
        }

        [Fact]
        public void VirtualButton_OnIsIdempotent_WhereAFlipWouldAlternate()
        {
            var macros = new[]
            {
                Item(MacroTriggerMode.OnPress, MacroRepeatMode.UntilRelease, 7,
                    Latch(MacroActionType.ToggleVcButton, MacroLatchDirection.On, flags: Gamepad.Y)),
                Item(MacroTriggerMode.OnRelease, MacroRepeatMode.Once, 7,
                    Latch(MacroActionType.ToggleVcButton, MacroLatchDirection.Off, flags: Gamepad.Y)),
            };
            macros[0].RepeatDelayMs = 0;
            var im = Engine(macros);

            for (int i = 0; i < 12; i++)
            {
                Assert.NotEqual(0, Tick(im, macros, true) & Gamepad.Y);
                Thread.Sleep(2);
            }
            // The release twin clears the press leg's latch through the pair.
            Tick(im, macros, false);
            Assert.Equal(0, Tick(im, macros, false) & Gamepad.Y);
            Assert.False(macros[0].Actions[0].VcToggleLatched);
        }

        [Fact]
        public void VirtualButton_DefaultDirection_StillFlips()
        {
            var macros = new[]
            {
                Item(MacroTriggerMode.OnPress, MacroRepeatMode.Once, 0,
                    new MacroAction { Type = MacroActionType.ToggleVcButton, ButtonFlags = Gamepad.Y }),
            };
            var im = Engine(macros);
            Assert.NotEqual(0, Tick(im, macros, true) & Gamepad.Y);
            Tick(im, macros, false);
            Assert.NotEqual(0, Tick(im, macros, false) & Gamepad.Y);
            Tick(im, macros, true);
            Tick(im, macros, false);
            Assert.Equal(0, Tick(im, macros, false) & Gamepad.Y);
        }

        [Fact]
        public void Key_DownAndUpInOneMacro_LeaveNothingHeld()
        {
            var macros = new[]
            {
                Item(MacroTriggerMode.OnPress, MacroRepeatMode.Once, 0,
                    Latch(MacroActionType.ToggleKey, MacroLatchDirection.On, key: InertKey),
                    Wait(30),
                    Latch(MacroActionType.ToggleKey, MacroLatchDirection.Off, key: InertKey)),
            };
            var im = Engine(macros);
            Tick(im, macros, true);
            Assert.Contains((ushort)InertKey, im._latchedKeysDown);
            Thread.Sleep(50);
            Tick(im, macros, true);
            Tick(im, macros, true);
            Tick(im, macros, false);
            Assert.DoesNotContain((ushort)InertKey, im._latchedKeysDown);
            Assert.False(macros[0].Actions[0].KeyToggleLatched);
        }

        [Fact]
        public void Key_OverlappingSequence_HoldsTheFirstAcrossTheSecond()
        {
            // Alt held, Tab tapped, Alt released: the shape DS4Windows' own
            // alt-tab macro has.
            const int Other = 0xE8; // unassigned
            var macros = new[]
            {
                Item(MacroTriggerMode.OnPress, MacroRepeatMode.Once, 0,
                    Latch(MacroActionType.ToggleKey, MacroLatchDirection.On, key: InertKey),
                    Latch(MacroActionType.ToggleKey, MacroLatchDirection.On, key: Other),
                    Latch(MacroActionType.ToggleKey, MacroLatchDirection.Off, key: Other),
                    Wait(30),
                    Latch(MacroActionType.ToggleKey, MacroLatchDirection.Off, key: InertKey)),
            };
            var im = Engine(macros);
            for (int i = 0; i < 5; i++) Tick(im, macros, true);
            Assert.Contains((ushort)InertKey, im._latchedKeysDown);
            Assert.DoesNotContain((ushort)Other, im._latchedKeysDown);
            Thread.Sleep(50);
            Tick(im, macros, true);
            Tick(im, macros, false);
            Assert.DoesNotContain((ushort)InertKey, im._latchedKeysDown);
        }

        [Fact]
        public void Key_ReleaseWrittenAsItsOwnMacro_ReachesThePress()
        {
            // A file can press on one mapping and release on another.
            var macros = new[]
            {
                Item(MacroTriggerMode.OnPress, MacroRepeatMode.Once, 0,
                    Latch(MacroActionType.ToggleKey, MacroLatchDirection.On, key: InertKey)),
                Item(MacroTriggerMode.OnRelease, MacroRepeatMode.Once, 0,
                    Latch(MacroActionType.ToggleKey, MacroLatchDirection.Off, key: InertKey)),
            };
            var im = Engine(macros);
            Tick(im, macros, true);
            Assert.Contains((ushort)InertKey, im._latchedKeysDown);
            Tick(im, macros, false);
            Tick(im, macros, false);
            Assert.DoesNotContain((ushort)InertKey, im._latchedKeysDown);
        }

        [Fact]
        public void TwoHoldPairs_OnOneKey_DoNotReleaseEachOther()
        {
            // The rule that keeps a pair's reach to its own macros: two buttons
            // can hold one key, and letting go of one must not drop it.
            var first = Item(MacroTriggerMode.OnPress, MacroRepeatMode.UntilRelease, 1,
                Latch(MacroActionType.ToggleKey, MacroLatchDirection.On, key: InertKey));
            var firstUp = Item(MacroTriggerMode.OnRelease, MacroRepeatMode.Once, 1,
                Latch(MacroActionType.ToggleKey, MacroLatchDirection.Off, key: InertKey));
            var second = Item(MacroTriggerMode.OnPress, MacroRepeatMode.UntilRelease, 2,
                Latch(MacroActionType.ToggleKey, MacroLatchDirection.On, key: InertKey));
            second.TriggerButtons = Gamepad.B;
            var macros = new[] { first, firstUp, second };
            var im = Engine(macros);

            var gp = new Gamepad { Buttons = (ushort)(Gamepad.A | Gamepad.B) };
            im.EvaluateSlotMacros(ref gp, macros);
            im.ReconcileLatchedKeys();
            gp = new Gamepad { Buttons = Gamepad.B };
            im.EvaluateSlotMacros(ref gp, macros);
            im.ReconcileLatchedKeys();
            gp = new Gamepad { Buttons = Gamepad.B };
            im.EvaluateSlotMacros(ref gp, macros);
            im.ReconcileLatchedKeys();
            Assert.True(second.Actions[0].KeyToggleLatched);
            Assert.Contains((ushort)InertKey, im._latchedKeysDown);
            im.ReleaseAllLatchedMacroKeys();
        }

        [Fact]
        public void AxisLatch_SetThenReleased_LeavesTheAxisAlone()
        {
            var macros = new[]
            {
                Item(MacroTriggerMode.OnPress, MacroRepeatMode.Once, 0,
                    new MacroAction { Type = MacroActionType.AxisSetLatched, AxisTarget = MacroAxisTarget.LeftStickX, AxisValue = 16000 },
                    Wait(30),
                    new MacroAction { Type = MacroActionType.AxisLatchRelease, AxisTarget = MacroAxisTarget.LeftStickX }),
            };
            var im = Engine(macros);
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(16000, gp.ThumbLX);
            Thread.Sleep(50);
            for (int i = 0; i < 3; i++)
            {
                gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
            }
            Assert.Equal(0, gp.ThumbLX);
        }

        // ── What the importers actually write, loaded and run ───────────

        /// <summary>A macro as the app loads it, its trigger swapped for the
        /// virtual A bit, so the test drives it without a device.</summary>
        private static MacroItem[] Loaded(ProfileData p)
            => p.Macros.Select(md =>
            {
                md.TriggerSource = MacroTriggerSource.OutputController;
                md.TriggerInputs = null;
                md.TriggerButtons = Gamepad.A;
                var item = SettingsService.LoadMacroFromData(md, VirtualControllerType.Xbox, null);
                item.ConsumeTriggerButtons = false;
                return item;
            }).ToArray();

        private static ForeignImportResult Ds4(string body)
            => ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(
                "<DS4Windows config_version=\"5\"><LightbarMode>Passthru</LightbarMode>" + body + "</DS4Windows>"), "C:/x/T.xml");

        [Fact]
        public void Ds4WindowsMacro_HoldsThenReleases()
        {
            // 252 down, wait 40 ms, 252 up.
            var r = Ds4("<Control><Macro><Cross>252/340/252</Cross></Macro></Control>");
            var macros = Loaded(r.Profile);
            var im = Engine(macros);

            Tick(im, macros, true);
            Assert.Contains((ushort)InertKey, im._latchedKeysDown);
            Thread.Sleep(60);
            Tick(im, macros, true);
            Tick(im, macros, false);
            Assert.DoesNotContain((ushort)InertKey, im._latchedKeysDown);
        }

        [Fact]
        public void Ds4WindowsPadButtonMacro_WithAWait_HoldsTheBitForTheWait()
        {
            // Y down, wait 40 ms, Y up.
            var r = Ds4("<Control><Macro><Cross>264/340/264</Cross></Macro></Control>");
            var macros = Loaded(r.Profile);
            var im = Engine(macros);

            Assert.NotEqual(0, Tick(im, macros, true) & Gamepad.Y);
            Assert.NotEqual(0, Tick(im, macros, false) & Gamepad.Y);
            Thread.Sleep(60);
            Tick(im, macros, false);
            Assert.Equal(0, Tick(im, macros, false) & Gamepad.Y);
        }

        [Fact]
        public void RepeatingMacro_StoppedBetweenItsDownAndUp_LeavesNothingHeld()
        {
            var r = Ds4("<Control><Macro><Cross>252/1300/252</Cross></Macro><KeyType><Cross>MacroHoldMacro</Cross></KeyType></Control>");
            Assert.Equal(2, r.Profile.Macros.Length);
            var macros = Loaded(r.Profile);
            var im = Engine(macros);

            Tick(im, macros, true);
            Assert.Contains((ushort)InertKey, im._latchedKeysDown);
            // Let go during the second's wait, before the up.
            Tick(im, macros, false);
            Tick(im, macros, false);
            Assert.DoesNotContain((ushort)InertKey, im._latchedKeysDown);
        }

        [Fact]
        public void RewasdHeldLongPress_OnAVirtualButton_HoldsUntilRelease()
        {
            string json = """
                {"schemaVersion":"3.0","config":{"appName":"x"},
                 "devices":{"hardware":[{"id":11,"name":"keyboard"}],"virtual":{"gamepads":[{"id":1,"type":"360"}]}},
                 "masks":[{"id":1,"set":[{"deviceId":11,"buttonId":30}]}],
                 "mappings":[{"condition":{"mask":{"id":1,"activator":{"type":"start","mode":"hold_until_release","params":{"macro":true}}}},
                              "macros":[{"gamepad":{"id":1,"buttonId":4}}]}]}
                """;
            var r = ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(json), "C:/x/T.rewasd");
            Assert.Equal(2, r.Profile.Macros.Length);
            var macros = Loaded(r.Profile);
            macros[0].RepeatDelayMs = 0;
            var im = Engine(macros);

            for (int i = 0; i < 12; i++)
            {
                Assert.NotEqual(0, Tick(im, macros, true) & Gamepad.Y);
                Thread.Sleep(2);
            }
            Tick(im, macros, false);
            Assert.Equal(0, Tick(im, macros, false) & Gamepad.Y);
        }
    }
}
