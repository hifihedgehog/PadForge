using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Connection as a value, #600 item 43: Device Connection as a Custom
    /// Expression variable, 1 while the chosen device is connected, and
    /// Offline Devices as what a keyboard screen or a Screen Overlay shows.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class ConnectionValueTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly bool[] _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();

        private const string PadPath = @"\\?\HID#VID_054C&PID_0CE6#8&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string StickPath = @"\\?\HID#VID_044F&PID_B10A#8&2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        public ConnectionValueTests()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotCreated[0] = SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotCreated[1] = true;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
        }

        private static UserDevice Device(string name, string path, bool online, int type = InputDeviceType.Gamepad, params int[] slots)
        {
            var ud = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = name,
                ProductName = name,
                DevicePath = path,
                CapType = type,
                IsOnline = online,
            };
            SettingsManager.UserDevices.Items.Add(ud);
            foreach (int slot in slots)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = ud.InstanceGuid, MapTo = slot });
            return ud;
        }

        // ── The tail pins ──

        [Fact]
        public void BothSourcesAreAppended_AndTheNumbersBeforeThemStay()
        {
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Enum.GetValues<MacroTriggerSource>().Select(v => (int)v));
            Assert.Equal(4, (int)MacroTriggerSource.DeviceConnection);
            Assert.Equal(Enumerable.Range(0, 10), Enum.GetValues<KeyboardScreenMode>().Select(v => (int)v));
            Assert.Equal(9, (int)KeyboardScreenMode.OfflineDevices);
            // The default a new screen starts with did not move.
            Assert.Equal(KeyboardScreenMode.Lightbar, KeyboardScreenData.DefaultMode);
        }

        // ── Device Connection ──

        [Fact]
        public void TheVariable_IsWrittenAsTheDeviceItReads_AndReadBack()
        {
            var pad = Device("DualSense Wireless Controller", PadPath, online: true, slots: 0);
            var v = new MacroExpressionVariable { Source = MacroTriggerSource.DeviceConnection };
            Assert.False(v.IsBound);
            Assert.Equal("", v.Spec);
            v.DeviceGuid = pad.InstanceGuid;
            Assert.True(v.IsBound);
            Assert.Equal("con:" + pad.InstanceGuid, v.Spec);
            Assert.Equal("DualSense Wireless Controller", v.DisplaySummary);

            var back = new MacroExpressionVariable { Spec = v.Spec };
            Assert.Equal(MacroTriggerSource.DeviceConnection, back.Source);
            Assert.Equal(pad.InstanceGuid, back.DeviceGuid);
            Assert.True(back.IsBound);

            // A broken one reads as no device.
            var broken = new MacroExpressionVariable { Spec = "con:not-a-guid" };
            Assert.Equal(MacroTriggerSource.DeviceConnection, broken.Source);
            Assert.False(broken.IsBound);

            // Its own reset, back to no device.
            Assert.True(MacroExpressionVariable.CanResetSetting(nameof(MacroExpressionVariable.DeviceGuid)));
            v.ResetSettingCommand.Execute(nameof(MacroExpressionVariable.DeviceGuid));
            Assert.Equal(Guid.Empty, v.DeviceGuid);
            Assert.False(v.IsBound);
        }

        [Fact]
        public void ThePicker_ListsEveryDeviceAPersonPlugsIn_AndKeepsTheChosenOne()
        {
            var pad = Device("DualSense Wireless Controller", PadPath, online: false);
            var stick = Device("T.16000M", StickPath, online: true, InputDeviceType.Joystick);
            var merged = Device("All Keyboards", "aggregate://keyboards", online: true, InputDeviceType.Keyboard);
            var v = new MacroExpressionVariable { Source = MacroTriggerSource.DeviceConnection };
            var options = v.ConnectionDeviceOptions;
            Assert.Contains(options, o => o.Guid == pad.InstanceGuid);
            Assert.Contains(options, o => o.Guid == stick.InstanceGuid && o.Name == "T.16000M");
            // A merged row is no device a person plugs in.
            Assert.DoesNotContain(options, o => o.Guid == merged.InstanceGuid);

            // A device PadForge has forgotten stays chosen, by its ID.
            var gone = Guid.NewGuid();
            v.DeviceGuid = gone;
            Assert.Contains(v.ConnectionDeviceOptions, o => o.Guid == gone && o.Name == gone.ToString());
        }

        /// <summary>The board's own example: <c>!a</c> with a reading the
        /// DualSense, through the engine's own evaluator, while the pad is
        /// the only device of its controller. Nothing fires before the run's
        /// first enumeration has finished, when every row reads
        /// offline.</summary>
        [Fact]
        public void NotA_RunsTheMacro_WhenTheDeviceDisconnects_AndNotBeforeTheFirstEnumeration()
        {
            var pad = Device("DualSense Wireless Controller", PadPath, online: false, slots: 0);
            var macro = new MacroItem
            {
                Name = "Pad gone",
                IsEnabled = true,
                PadIndex = 0,
                TriggerMode = MacroTriggerMode.CustomExpression,
                RepeatMode = MacroRepeatMode.UntilRelease,
                TriggerExpression = "!a",
            };
            macro.TriggerExpressionVariables.Add(new MacroExpressionVariable { Spec = "con:" + pad.InstanceGuid });
            macro.Actions.Add(new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = Gamepad.A, DurationMs = 60000 });

            var im = new InputManager();
            var running = typeof(InputManager).GetField("_running", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            try
            {
                int run = im.StampRun();
                running.SetValue(im, true);
                Assert.False(im.DeviceConnected(pad.InstanceGuid));

                // Engine start: the pad reads offline, and nothing is known.
                var gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, new[] { macro });
                Assert.Equal(0, gp.Buttons & Gamepad.A);

                // The first enumeration finds the pad.
                pad.IsOnline = true;
                im.NoteEnumerationBegan(run, 1000);
                im.NoteDevicesEnumerated(run);
                Assert.True(im.DeviceConnected(pad.InstanceGuid));
                gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, new[] { macro });
                Assert.Equal(0, gp.Buttons & Gamepad.A);

                // It disconnects: the letter is 0, and !a runs the macro.
                pad.IsOnline = false;
                gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, new[] { macro });
                Assert.Equal(Gamepad.A, gp.Buttons & Gamepad.A);
            }
            finally
            {
                running.SetValue(im, false);
            }
        }

        /// <summary>The engine idles once no controller has a connected
        /// device, and the idle pass evaluated the global macros alone. It
        /// now evaluates a slot's macros that read a connection too, and
        /// only those, so the drop of the last device runs !a while Home and
        /// the state still say Idle.</summary>
        [Fact]
        public void TheIdlePass_RunsTheMacrosThatReadAConnection_AndNoOthers()
        {
            var pad = Device("DualSense Wireless Controller", PadPath, online: true, slots: 0);
            MacroItem Macro(string expression, string spec, ushort button, bool enabled = true)
            {
                var m = new MacroItem
                {
                    IsEnabled = enabled, PadIndex = 0, TriggerMode = MacroTriggerMode.CustomExpression,
                    TriggerExpression = expression, RepeatMode = MacroRepeatMode.UntilRelease,
                };
                if (spec != null) m.TriggerExpressionVariables.Add(new MacroExpressionVariable { Spec = spec });
                m.Actions.Add(new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = button, DurationMs = 60000 });
                return m;
            }
            var gone = Macro("!a", "con:" + pad.InstanceGuid, Gamepad.A);
            var always = Macro("1", null, Gamepad.B);
            var off = Macro("!a", "con:" + pad.InstanceGuid, Gamepad.X, enabled: false);
            Assert.True(InputManager.ReadsAConnection(gone));
            Assert.False(InputManager.ReadsAConnection(always));
            Assert.False(InputManager.ReadsAConnection(off));
            Assert.False(InputManager.ReadsAConnection(Macro("!a", "con:", Gamepad.Y)));
            Assert.False(InputManager.ReadsAConnection(Macro("!a", "val:boost", Gamepad.Y)));

            var im = new InputManager();
            var running = typeof(InputManager).GetField("_running", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            try
            {
                int run = im.StampRun();
                running.SetValue(im, true);
                im.NoteEnumerationBegan(run, 1000);
                im.NoteDevicesEnumerated(run);
                im.MacroSnapshots[0] = new[] { gone, always, off };

                im.EvaluateConnectionMacros();
                Assert.Equal(0, im.CombinedOutputStates[0].Buttons & (Gamepad.A | Gamepad.B | Gamepad.X));

                // The last device drops, and the engine idles.
                pad.IsOnline = false;
                im.CombinedOutputStates[0] = new Gamepad();
                im.EvaluateConnectionMacros();
                Assert.Equal(Gamepad.A, im.CombinedOutputStates[0].Buttons & Gamepad.A);
                // A macro that reads no connection is the full pass's alone.
                Assert.Equal(0, im.CombinedOutputStates[0].Buttons & (Gamepad.B | Gamepad.X));
            }
            finally
            {
                running.SetValue(im, false);
                im.MacroSnapshots[0] = null;
            }

            // The idle pass calls it, and the idle rule is as it was: Home
            // and the state keep saying Idle.
            string manager = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.cs");
            int idle = manager.IndexOf("if (BeginIdlePoll())", StringComparison.Ordinal);
            int global = manager.IndexOf("EvaluateGlobalMacros();", idle, StringComparison.Ordinal);
            int connection = manager.IndexOf("EvaluateConnectionMacros();", idle, StringComparison.Ordinal);
            int release = manager.IndexOf("ReleaseAllLatchedMacroKeys();", idle, StringComparison.Ordinal);
            Assert.True(idle > 0 && global > idle && connection > global && release > connection);
            string service = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("_inputManager.IsIdle = !s_benchStayAwake && !anyActive && !remoteSharing && !teardownPending;", service, StringComparison.Ordinal);
        }

        /// <summary>A connection macro the engine evaluates while it idles,
        /// with the device connected so the macro rests. The interface swaps
        /// the slot's snapshot for a new array of the same macros at 30 Hz,
        /// so <paramref name="body"/> gets the engine and two such
        /// arrays.</summary>
        private static void WithIdleConnectionMacro(Action<InputManager, MacroItem, MacroItem[], MacroItem[]> body)
        {
            var pad = Device("DualSense Wireless Controller", PadPath, online: true, slots: 0);
            var macro = new MacroItem
            {
                IsEnabled = true, PadIndex = 0, TriggerMode = MacroTriggerMode.CustomExpression,
                TriggerExpression = "!a", RepeatMode = MacroRepeatMode.UntilRelease,
            };
            macro.TriggerExpressionVariables.Add(new MacroExpressionVariable { Spec = "con:" + pad.InstanceGuid });
            macro.Actions.Add(new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = Gamepad.A, DurationMs = 60000 });
            var other = new MacroItem { IsEnabled = true, PadIndex = 0, TriggerMode = MacroTriggerMode.OnPress, TriggerButtons = Gamepad.B };
            var im = new InputManager();
            var running = typeof(InputManager).GetField("_running", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            try
            {
                int run = im.StampRun();
                running.SetValue(im, true);
                im.NoteEnumerationBegan(run, 1000);
                im.NoteDevicesEnumerated(run);
                body(im, macro, new[] { other, macro }, new[] { other, macro });
            }
            finally
            {
                running.SetValue(im, false);
                im.MacroSnapshots[0] = null;
            }
        }

        /// <summary>The idle pass picks a slot's connection macros out of a
        /// snapshot the interface replaces 30 times a second, and allocates
        /// nothing while the macros it picks stay the same.</summary>
        [Fact]
        public void TheIdlePass_AllocatesNothing_WhileTheSlotsConnectionMacrosStayTheSame()
        {
            WithIdleConnectionMacro((im, macro, first, second) =>
            {
                bool flip = false;
                void Pass()
                {
                    flip = !flip;
                    im.MacroSnapshots[0] = flip ? first : second;
                    im.EvaluateConnectionMacros();
                }
                Pass();
                Assert.Equal(new long[] { 0, 0, 0 }, AllocationWindow.Clean(Pass, 200));
                Assert.False(macro.IsExecuting);
            });
        }

        /// <summary>The active pass lets go of the idle pass's arrays of
        /// connection macros, so none holds a macro the person deletes while
        /// the engine is active.</summary>
        [Fact]
        public void TheActivePass_LetsGoOfTheIdlePassesArrays()
        {
            WithIdleConnectionMacro((im, macro, first, _) =>
            {
                var arrays = (MacroItem[][])typeof(InputManager)
                    .GetField("_connectionMacros", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .GetValue(im);
                im.MacroSnapshots[0] = first;
                im.EvaluateConnectionMacros();
                Assert.NotNull(arrays[0]);

                typeof(InputManager)
                    .GetMethod("EvaluateMacros", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(im, null);
                Assert.All(arrays, a => Assert.Null(a));
            });
        }

        /// <summary>A menu cell that names a connection macro and was held on
        /// the last active pass is not held in the idle pass, which runs no
        /// menu: the macro gets its release on the first idle pass.</summary>
        [Fact]
        public void AMenuCellsStamp_FromTheLastActivePass_IsNotCurrentInTheIdlePass()
        {
            WithIdleConnectionMacro((im, macro, first, _) =>
            {
                im.MacroSnapshots[0] = first;
                macro.MenuTriggerTick = im.MacroPassTick;
                im.EvaluateConnectionMacros();
                im.EvaluateConnectionMacros();
                Assert.False(macro.WasTriggerActive);
            });
        }

        /// <summary>The variable and the mode go through the settings file
        /// and the clipboard, which carry a macro as its data, and the
        /// screens list's cleaning on load keeps mode 9.</summary>
        [Fact]
        public void BothValues_SurviveTheSettingsFileTheClipboardAndTheCleaning()
        {
            var pad = Device("DualSense Wireless Controller", PadPath, online: true, slots: 0);
            var macro = new MacroItem { Name = "Pad gone", TriggerMode = MacroTriggerMode.CustomExpression, TriggerExpression = "!a" };
            macro.TriggerExpressionVariables.Add(new MacroExpressionVariable { Source = MacroTriggerSource.DeviceConnection, DeviceGuid = pad.InstanceGuid });
            macro.Actions.Add(new MacroAction { Type = MacroActionType.KeyboardScreenMode, ScreenMode = KeyboardScreenMode.OfflineDevices });

            var data = SettingsService.BuildMacroDataForMacro(macro, 0);
            string json = SettingsService.SerializeMacrosToClipboard(new[] { data });
            var pasted = SettingsService.TryParseMacroClipboard(json);
            Assert.NotNull(pasted);
            var back = SettingsService.LoadMacroFromData(Assert.Single(pasted.Macros), VirtualControllerType.Xbox, null);
            var variable = Assert.Single(back.TriggerExpressionVariables);
            Assert.Equal(MacroTriggerSource.DeviceConnection, variable.Source);
            Assert.Equal(pad.InstanceGuid, variable.DeviceGuid);
            Assert.Equal(KeyboardScreenMode.OfflineDevices, Assert.Single(back.Actions).ScreenMode);

            var xml = new System.Xml.Serialization.XmlSerializer(typeof(MacroData));
            var written = new System.IO.StringWriter();
            xml.Serialize(written, data);
            var reread = (MacroData)xml.Deserialize(new System.IO.StringReader(written.ToString()));
            Assert.Equal("con:" + pad.InstanceGuid, reread.TriggerExpressionVariables);

            var screen = Guid.NewGuid().ToString("D");
            var list = KeyboardScreenData.Normalize(new[] { new KeyboardScreenData { Device = screen, Mode = KeyboardScreenMode.OfflineDevices } });
            Assert.Equal(KeyboardScreenMode.OfflineDevices, Assert.Single(list).Mode);
        }

        [Fact]
        public void BothLanes_ReadTheConnection_AndNeitherRecordsIt()
        {
            string evaluate = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Equal(2, CountOf(evaluate, "if (v.Source == MacroTriggerSource.DeviceConnection)"));
            Assert.Equal(2, CountOf(evaluate, "if (!ConnectionsKnown(vars, n)) return false;"));
            string input = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("MacroTriggerSource.GameTelemetry or MacroTriggerSource.ScriptValue or MacroTriggerSource.DeviceConnection", input, StringComparison.Ordinal);
            string page = ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml");
            Assert.Contains("Tag=\"{x:Static vm:MacroTriggerSource.DeviceConnection}\"", page, StringComparison.Ordinal);
            Assert.Contains("ItemsSource=\"{Binding ConnectionDeviceOptions}\"", page, StringComparison.Ordinal);
        }

        private static int CountOf(string text, string what)
        {
            int count = 0;
            for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) count++;
            return count;
        }

        // ── Offline Devices ──

        [Fact]
        public void OfflineDevices_NamesTheMappedDevicesThatAreAway()
        {
            var pad = Device("DualSense Wireless Controller", PadPath, online: false, slots: 0);
            var stick = Device("T.16000M", StickPath, online: true, InputDeviceType.Joystick, 0);
            var unmapped = Device("Wheel", @"\\?\HID#VID_046D&PID_C266#1#{x}", online: false, InputDeviceType.Driving);
            var offController = Device("Arcade Stick", @"\\?\HID#VID_0F0D&PID_0092#1#{x}", online: false, InputDeviceType.Gamepad, 1);
            var merged = Device("All Keyboards", "aggregate://keyboards", online: false, InputDeviceType.Keyboard, 0);

            var service = new KeyboardScreenService(new SettingsViewModel(), null);
            Assert.Equal("DualSense Wireless Controller", service.OfflineDevicesText());

            stick.IsOnline = false;
            Assert.Equal("DualSense Wireless Controller, T.16000M", service.OfflineDevicesText());

            pad.IsOnline = stick.IsOnline = true;
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.KeyboardScreen_NoDeviceOffline, service.OfflineDevicesText());
            Assert.Equal("All connected", PadForge.Resources.Strings.Strings.Instance.KeyboardScreen_NoDeviceOffline);

            // The Screen Overlay shows the same line, as text.
            pad.IsOnline = false;
            var content = service.OverlayContent(new KeyboardScreenData { Mode = KeyboardScreenMode.OfflineDevices }, Environment.TickCount64);
            Assert.Equal("DualSense Wireless Controller", content.Text);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.KeyboardScreens_Mode_OfflineDevices, KeyboardScreenService.ModeName(KeyboardScreenMode.OfflineDevices));
            Assert.Equal("Offline Devices", KeyboardScreenService.ModeName(KeyboardScreenMode.OfflineDevices));
            _ = unmapped; _ = offController; _ = merged;
        }

        /// <summary>The screen draws the line on every kind of device
        /// screen, the smallest included, where a long list wraps and ends
        /// in an ellipsis.</summary>
        [Fact]
        public void OfflineDevices_DrawsOnEveryScreen()
        {
            Device("DualSense Wireless Controller", PadPath, online: false, slots: 0);
            Device("T.16000M", StickPath, online: false, InputDeviceType.Joystick, 0);
            Exception failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var service = new KeyboardScreenService(new SettingsViewModel(), null);
                    foreach (var kind in new[] { KeyboardScreenKind.Monochrome, KeyboardScreenKind.Color, KeyboardScreenKind.Arx, KeyboardScreenKind.Strike7 })
                    {
                        var screen = new KeyboardScreenInfo("test-offline-" + kind, kind, "Test Screen", "Test");
                        byte[] frame = service.Compose(screen, new KeyboardScreenData { Mode = KeyboardScreenMode.OfflineDevices }, Environment.TickCount64, out var blank);
                        Assert.NotNull(frame);
                        Assert.Equal(KeyboardScreenService.Blank.None, blank);
                        // Something is drawn: not every byte is the ground.
                        Assert.Contains(frame, b => b != frame[0]);
                    }
                }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "render run timed out");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
