using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Recording;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Plugins;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>A problem recording made for a test by the real recorder,
    /// on a folder of the test's own: each device's state for each frame,
    /// one frame every <c>step</c> seconds.</summary>
    internal sealed class TestRecording : IDisposable
    {
        private readonly RecorderTestHost _host = new();
        public string Path { get; }
        public List<UserDevice> Devices { get; } = new();

        public TestRecording(int devices, int frames, double step, Func<int, int, CustomInputState> state)
        {
            for (int d = 0; d < devices; d++)
            {
                var pad = RecorderTestKit.Pad("Test Pad " + (d + 1), 0x054C, (ushort)(0x0CE6 + d));
                Devices.Add(pad);
                _host.DeviceList.Add(pad);
            }
            var recorder = new ProblemRecorder(_host) { FrozenClock = Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            for (int f = 0; f < frames; f++)
            {
                for (int d = 0; d < devices; d++) capture.Input(Devices[d], state(d, f));
                capture.Advance(step);
            }
            Path = recorder.Stop(wait: true).Path;
            Assert.True(File.Exists(Path));
        }

        public void Dispose() => _host.Dispose();
    }

    /// <summary>
    /// The two plugins PadForge carries (#597). The Test Controller (#600
    /// item 19) moves its sticks and triggers together and then rests while
    /// its buttons take turns, works the motion out at each read, holds what
    /// By Hand sets across a restart, and is a gamepad that auto-maps one to
    /// one. Replay plays a recording's frames at the file's timing, loops
    /// with Repeat, and makes one device for each of the file's devices. A
    /// recording kept with the tests runs through the mapping pipeline as a
    /// test.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class PluginBuiltInTests
    {
        private readonly ITestOutputHelper _output;

        public PluginBuiltInTests(ITestOutputHelper output) => _output = output;

        // ── Test Controller (#600 item 19) ──

        private static TestControllerConfig Config(Action<PluginSettingSet> set = null)
        {
            var settings = new PluginSettingSet();
            TestControllerPattern.Declare(settings);
            set?.Invoke(settings);
            return TestControllerPattern.Read(settings);
        }

        private static CustomInputState At(TestControllerConfig c, long ms)
        {
            var s = new CustomInputState();
            TestControllerPattern.Fill(s, c, ms);
            return s;
        }

        [Fact]
        public void TheTestController_DeclaresEverySettingTheIssueLists_WithItsRangeAndDefault()
        {
            var set = new PluginSettingSet();
            TestControllerPattern.Declare(set);
            var d = set.Declarations().ToDictionary(x => x.Key);
            Assert.Equal(PluginSettingKind.Choice, d[TestControllerPattern.KeyMoves].Kind);
            Assert.Equal(new[] { "both", "sticks", "triggers" }, d[TestControllerPattern.KeyMoves].Choices.Select(c => c.Value));
            Assert.Equal(new[] { "sweep", "circle" }, d[TestControllerPattern.KeyPattern].Choices.Select(c => c.Value));
            Assert.Equal("sweep", d[TestControllerPattern.KeyPattern].Default);
            var move = d[TestControllerPattern.KeyMove];
            Assert.Equal((0.5, 20.0, "4", "s"), (move.Min, move.Max, move.Default, move.Unit));
            var rest = d[TestControllerPattern.KeyRest];
            Assert.Equal((0.0, 20.0, "2"), (rest.Min, rest.Max, rest.Default));
            var press = d[TestControllerPattern.KeyPress];
            Assert.Equal((50.0, 5000.0, "250", "ms"), (press.Min, press.Max, press.Default, press.Unit));
            Assert.Equal("true", d[TestControllerPattern.KeyButtons].Default);
            // The navigation buttons and the D-pad start off, with the reason
            // beside them.
            Assert.Equal("false", d[TestControllerPattern.KeyNavigation].Default);
            Assert.Equal("false", d[TestControllerPattern.KeyDpad].Default);
            Assert.Contains("Xbox Game Bar", d[TestControllerPattern.KeyNavigation].Help);
            Assert.Equal(new[] { "1", "2", "3", "4" }, d[TestControllerPattern.KeyControllers].Choices.Select(c => c.Value));
            Assert.Equal("false", d[TestControllerPattern.KeyHand].Default);
            Assert.Equal(PluginSettingKind.Pad, d[TestControllerPattern.KeyLeftStick].Kind);
            Assert.Equal(PluginSettingKind.Pad, d[TestControllerPattern.KeyRightStick].Kind);
            Assert.Equal(PluginSettingKind.Number, d[TestControllerPattern.KeyLeftTrigger].Kind);
            Assert.All(TestControllerPattern.HandButtons, b => Assert.Equal(PluginSettingKind.Switch, d[b.Key].Kind));
            Assert.All(d.Values.Where(x => x.Key.StartsWith("hand-", StringComparison.Ordinal)), x =>
            {
                Assert.Equal(TestControllerPattern.KeyHand, x.When);
                Assert.True(x.Compact);
            });
            Assert.Equal(PluginSettingKind.Status, d[TestControllerPattern.KeyRate].Kind);
        }

        [Fact]
        public void TheTestControllersNote_SaysHowItMoves()
        {
            // The motion: the sticks and the triggers move together, then
            // rest, while one button at a time is held.
            var c = Config();
            var moving = At(c, 1000);
            var resting = At(c, 5000);
            Assert.NotEqual(TestControllerPattern.Center, moving.Axis[0]);
            Assert.Equal(moving.Axis[0], moving.Axis[3]);
            Assert.Equal(moving.Axis[1], moving.Axis[4]);
            Assert.True(moving.Axis[2] > 0);
            Assert.Equal(moving.Axis[2], moving.Axis[5]);
            Assert.Equal(1, moving.Buttons.Count(b => b));
            Assert.Equal(TestControllerPattern.Center, resting.Axis[0]);
            Assert.Equal(0, resting.Axis[2]);

            // The card's note, in the English Strings.resx holds, says so,
            // and no longer that it moves one control at a time.
            var resx = System.Xml.Linq.XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"));
            string note = resx.Root.Elements("data").Single(e => (string)e.Attribute("name") == "Plugins_TestController_Note").Element("value").Value;
            Assert.DoesNotContain("one control at a time", note);
            Assert.Contains("together", note);
            Assert.Contains("rest", note);
            Assert.Contains("take turns", note);
        }

        [Fact]
        public void ByDefault_ItPressesNoNavigationButton_TurnsNoDPad_AndMovesThenRests()
        {
            var c = Config();
            var pressed = new HashSet<int>();
            bool moved = false, rested = false;
            for (long ms = 0; ms < 60_000; ms += 5)
            {
                var s = At(c, ms);
                for (int b = 0; b < 11; b++) if (s.Buttons[b]) pressed.Add(b);
                Assert.Equal(-1, s.Povs[0]);
                long into = ms % 6000;
                bool sticksCentered = s.Axis[0] == TestControllerPattern.Center && s.Axis[1] == TestControllerPattern.Center
                                      && s.Axis[3] == TestControllerPattern.Center && s.Axis[4] == TestControllerPattern.Center;
                if (into >= 4000)
                {
                    // The Rest Time: sticks centered, triggers out.
                    Assert.True(sticksCentered, "a stick moves while resting at " + ms);
                    Assert.Equal(0, s.Axis[2]);
                    rested = true;
                }
                else if (!sticksCentered) moved = true;
            }
            Assert.True(moved && rested);
            Assert.Equal(TestControllerPattern.PlainButtons.OrderBy(b => b), pressed.OrderBy(b => b));
            Assert.DoesNotContain(0, pressed);   // A
            Assert.DoesNotContain(1, pressed);   // B
            Assert.DoesNotContain(6, pressed);   // Back
            Assert.DoesNotContain(7, pressed);   // Start
            Assert.DoesNotContain(10, pressed);  // Guide
        }

        [Fact]
        public void TheSettings_ChangeTheMotion_AsTheyAreSet()
        {
            // One button at a time, held for the Press Time.
            var c = Config(s => s.Set(null, TestControllerPattern.KeyPress, "1000"));
            Assert.Equal(1, At(c, 0).Buttons.Count(b => b));
            Assert.Equal(At(c, 100).Buttons, At(c, 900).Buttons);
            Assert.NotEqual(Array.IndexOf(At(c, 900).Buttons, true), Array.IndexOf(At(c, 1100).Buttons, true));

            // Navigation on: every button takes its turn. D-pad on: it turns.
            c = Config(s =>
            {
                s.Set(null, TestControllerPattern.KeyNavigation, "true");
                s.Set(null, TestControllerPattern.KeyDpad, "true");
            });
            var pressed = new HashSet<int>();
            var hats = new HashSet<int>();
            for (long ms = 0; ms < 20_000; ms += 10)
            {
                var s = At(c, ms);
                for (int b = 0; b < 11; b++) if (s.Buttons[b]) pressed.Add(b);
                if (s.Povs[0] >= 0) hats.Add(s.Povs[0]);
            }
            Assert.Equal(Enumerable.Range(0, 11), pressed.OrderBy(b => b));
            Assert.Equal(Enumerable.Range(0, 8).Select(i => i * 4500), hats.OrderBy(h => h));

            // Press Buttons off: none.
            c = Config(s => s.Set(null, TestControllerPattern.KeyButtons, "false"));
            for (long ms = 0; ms < 10_000; ms += 50) Assert.DoesNotContain(true, At(c, ms).Buttons);

            // Triggers only: the sticks stay centered, the triggers go to full.
            c = Config(s => s.Set(null, TestControllerPattern.KeyMoves, "triggers"));
            int high = 0;
            for (long ms = 0; ms < 6000; ms += 5)
            {
                var s = At(c, ms);
                Assert.Equal(TestControllerPattern.Center, s.Axis[0]);
                high = Math.Max(high, s.Axis[5]);
            }
            Assert.InRange(high, 65000, 65535);

            // Circle: the left stick goes round, each point a full radius out.
            c = Config(s => s.Set(null, TestControllerPattern.KeyPattern, "circle"));
            for (long ms = 0; ms < 4000; ms += 100)
            {
                var s = At(c, ms);
                double x = (s.Axis[0] - 32768) / 32767.0, y = (s.Axis[1] - 32768) / 32767.0;
                Assert.InRange(Math.Sqrt(x * x + y * y), 0.99, 1.01);
            }

            // Move Time 10 s and Rest Time 0: still moving at 9 s.
            c = Config(s =>
            {
                s.Set(null, TestControllerPattern.KeyMove, "10");
                s.Set(null, TestControllerPattern.KeyRest, "0");
            });
            Assert.NotEqual(TestControllerPattern.Center, At(c, 9100).Axis[1]);
            // A value outside a range is refused with the range in words.
            var set = new PluginSettingSet();
            TestControllerPattern.Declare(set);
            Assert.Contains("0.5 to 20", set.Set(null, TestControllerPattern.KeyMove, "30"));
        }

        [Fact]
        public void ByHand_StopsTheMotion_AndSetsTheControllersOwnInputs()
        {
            var c = Config(s =>
            {
                s.Set(null, TestControllerPattern.KeyHand, "true");
                s.Set(null, TestControllerPattern.KeyLeftStick, "100,-100");
                s.Set(null, TestControllerPattern.KeyRightTrigger, "50");
                s.Set(null, "hand-a", "true");
            });
            for (long ms = 0; ms < 8000; ms += 250)
            {
                var s = At(c, ms);
                Assert.Equal(65535, s.Axis[0]);
                Assert.Equal(1, s.Axis[1]);
                Assert.Equal(TestControllerPattern.Center, s.Axis[3]);
                Assert.Equal(32768, s.Axis[5]);
                Assert.True(s.Buttons[0]);
                Assert.Equal(1, s.Buttons.Count(b => b));
                Assert.Equal(-1, s.Povs[0]);
            }
        }

        [Fact]
        public void TheMotion_IsWorkedOutAtEachRead_SoItIsAsFineAsThePollingRate()
        {
            long now = 0;
            var c = Config(s => s.Set(null, TestControllerPattern.KeyPattern, "circle"));
            var motion = new TestControllerMotion(null, () => c, () => now);
            var seen = new HashSet<int>();
            // A read every millisecond, as at 1,000 Hz: each read is a new
            // place on the circle.
            for (now = 0; now < 50; now++) seen.Add(motion.Read().Axis[0]);
            Assert.True(seen.Count >= 45, "only " + seen.Count + " places in 50 reads");
            Assert.Equal(50, motion.TakeReads());
            Assert.Equal(0, motion.TakeReads());
        }

        [Fact]
        public void TheTestController_IsAGamepadOnTheDeviceList_ThatMovesAndAutoMapsOneToOne()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            using var builtIns = new BuiltInPlugins(rig.Host.Sessions.Devices, () => "Test Controller", n => n + " (Replay)");
            builtIns.TestSettings.Set(null, TestControllerPattern.KeyPress, "50");
            builtIns.TestControllerOn = true;
            Assert.Null(builtIns.TestControllerDevice);
            builtIns.SetPluginsOn(true);
            var device = builtIns.TestControllerDevice;
            Assert.NotNull(device);
            Assert.Equal(BuiltInPlugins.TestControllerGuid, device.InstanceGuid);
            Assert.Contains("register Test Controller", rig.Engine.Calls);
            Assert.Equal(InputDeviceType.Gamepad, device.GetInputDeviceType());
            Assert.Equal(6, device.NumAxes);
            Assert.Equal(11, device.NumButtons);
            Assert.Equal(1, device.NumHats);

            // It moves by itself, read by read, and counts its reads.
            var seen = new HashSet<string>();
            var clock = Stopwatch.StartNew();
            while (seen.Count < 3 && clock.ElapsedMilliseconds < 5000)
            {
                var s = device.GetCurrentState();
                seen.Add(string.Join(",", Enumerable.Range(0, 11).Select(i => s.Buttons[i] ? 1 : 0)));
                Thread.Sleep(20);
            }
            Assert.True(seen.Count >= 3, "the Test Controller did not move");
            builtIns.CountReads();
            Assert.False(string.IsNullOrEmpty(builtIns.TestSettings.ValueOf(null, TestControllerPattern.KeyRate)));

            // A gamepad's auto-map, one to one.
            var ud = new UserDevice();
            ud.LoadFromExternalDevice(device);
            var ps = SettingsManager.CreateDefaultPadSetting(ud, VirtualControllerType.Xbox);
            Assert.Equal("Button 0", ps.ButtonA);
            Assert.Equal("Axis 0", ps.LeftThumbAxisX);
            Assert.Equal("Axis 2", ps.LeftTrigger);
            Assert.Equal("POV 0 Up", ps.DPadUp);

            // Controllers 3: two more, each its own device. Back to 1: the
            // first stays.
            builtIns.TestSettings.Set(null, TestControllerPattern.KeyControllers, "3");
            Assert.Equal(3, builtIns.TestControllerCount);
            Assert.Contains("register Test Controller 3", rig.Engine.Calls);
            Assert.Equal(BuiltInPlugins.TestControllerGuidOf(2), builtIns.TestControllerDevices[2].InstanceGuid);
            builtIns.TestSettings.Set(null, TestControllerPattern.KeyControllers, "1");
            Assert.Equal(1, builtIns.TestControllerCount);
            Assert.Contains("unregister Test Controller 3", rig.Engine.Calls);
            Assert.Same(device, builtIns.TestControllerDevice);

            // Off takes it off the list, at rest.
            builtIns.TestControllerOn = false;
            Assert.Contains("unregister Test Controller", rig.Engine.Calls);
            Assert.Null(builtIns.TestControllerDevice);
        }

        [Fact]
        public void TheTestControllersValues_AreKept_AndRestore_AcrossARestart()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            PluginValueData[] saved;
            using (var first = new BuiltInPlugins(rig.Host.Sessions.Devices, () => "Test Controller", n => n))
            {
                first.TestSettings.Set(null, TestControllerPattern.KeyHand, "true");
                first.TestSettings.Set(null, "hand-x", "true");
                first.TestSettings.Set(null, TestControllerPattern.KeyMove, "4");
                saved = first.TestSettings.Save();
            }
            // Only what differs from its default is kept.
            Assert.Equal(new[] { TestControllerPattern.KeyHand, "hand-x" }, saved.Select(v => v.Key).OrderBy(k => k, StringComparer.Ordinal));
            using var second = new BuiltInPlugins(rig.Host.Sessions.Devices, () => "Test Controller", n => n);
            second.LoadTestValues(saved);
            Assert.True(second.TestConfig.ByHand);
            Assert.True(second.TestConfig.Buttons[2]);
        }

        [Fact]
        public void ByHand_ASticksPosition_HoldsWhereItWasLeft_AndReadsTheSameAfterARestart()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            CustomInputState before;
            AppSettingsData file;
            using (var first = new BuiltInPlugins(rig.Host.Sessions.Devices, () => "Test Controller", n => n))
            {
                // The Plugins page's own settings host, whose pads read Stay:
                // PluginPad puts a pad back to center on release only without it.
                var page = new PadForge.ViewModels.PluginSettingsHost(first.TestSettings, null, "Plugins_TestController",
                    System.Windows.Threading.Dispatcher.CurrentDispatcher);
                page.Item(TestControllerPattern.KeyHand).IsOn = true;
                foreach (string key in new[] { TestControllerPattern.KeyLeftStick, TestControllerPattern.KeyRightStick })
                {
                    Assert.True(first.TestSettings.Find(null, key).Stay, key + " goes back to center when it is let go");
                    Assert.True(page.Item(key).Stay);
                }
                // Each stick set by hand, as a drag of its pad sets it.
                page.Item(TestControllerPattern.KeyLeftStick).Pad = new System.Windows.Point(40, -60);
                page.Item(TestControllerPattern.KeyRightStick).Pad = new System.Windows.Point(-25.5, 80);
                before = At(first.TestConfig, 0);
                // PadForge.xml keeps the sticks with the triggers and the
                // buttons.
                file = new AppSettingsData { TestControllerValues = first.TestSettings.Save() };
            }

            // Written and read again by the file's own serializer.
            var xml = new System.Xml.Serialization.XmlSerializer(typeof(AppSettingsData));
            var text = new StringWriter();
            xml.Serialize(text, file);
            var read = (AppSettingsData)xml.Deserialize(new StringReader(text.ToString()));

            using var second = new BuiltInPlugins(rig.Host.Sessions.Devices, () => "Test Controller", n => n);
            second.LoadTestValues(read.TestControllerValues);
            Assert.True(second.TestConfig.ByHand);
            Assert.Equal((40.0, -60.0), second.TestConfig.LeftStick);
            Assert.Equal((-25.5, 80.0), second.TestConfig.RightStick);
            // The controller reads the same as before the restart, at any time:
            // By Hand stops the motion.
            foreach (long ms in new long[] { 0, 1500, 4500 })
            {
                var after = At(second.TestConfig, ms);
                Assert.Equal(before.Axis, after.Axis);
                Assert.Equal(before.Buttons, after.Buttons);
            }
            Assert.Equal(TestControllerPattern.Stick(40), before.Axis[0]);
            Assert.Equal(TestControllerPattern.Stick(-60), before.Axis[1]);
            Assert.Equal(TestControllerPattern.Stick(-25.5), before.Axis[3]);
            Assert.Equal(TestControllerPattern.Stick(80), before.Axis[4]);
        }

        [Fact]
        public void TheTestControllersValuesFromTheFile_AreCheckedAsAPluginsAreWhenItDeclares()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            using var builtIns = new BuiltInPlugins(rig.Host.Sessions.Devices, () => "Test Controller", n => n);
            // A file can hold a value at its default (a default a later
            // build moved to it, or a hand's edit) or one out of its range.
            builtIns.LoadTestValues(new[]
            {
                new PluginValueData { Key = TestControllerPattern.KeyMove, Value = "4" },
                new PluginValueData { Key = TestControllerPattern.KeyPress, Value = "10" },
                new PluginValueData { Key = TestControllerPattern.KeyRest, Value = "3" },
            });
            // One at its default is not kept, so PadForge.xml loses it. One
            // its declaration does not take gives way to the default. The
            // rest stand, and only they are kept.
            Assert.Equal("4", builtIns.TestSettings.ValueOf(null, TestControllerPattern.KeyMove));
            Assert.Equal("250", builtIns.TestSettings.ValueOf(null, TestControllerPattern.KeyPress));
            Assert.Equal(250, builtIns.TestConfig.PressMs);
            Assert.Equal(3000, builtIns.TestConfig.RestMs);
            Assert.Equal(new[] { TestControllerPattern.KeyRest }, builtIns.TestSettings.Save().Select(v => v.Key));
        }

        // ── Replay ──

        private static CustomInputState Moving(int device, int frame)
        {
            var s = RecorderTestKit.Rest();
            s.Axis[0] = 1000 + frame * 100 + device;
            s.Buttons[device] = frame % 2 == 0;
            return s;
        }

        [Fact]
        public void AFileWithTwoDevices_MakesTwoDevices_WithTheShapesItGives()
        {
            using var recording = new TestRecording(2, 5, 0.02, Moving);
            var file = ReplayFile.Read(recording.Path, out string error);
            Assert.Null(error);
            Assert.Equal(new[] { 1, 2 }, file.Devices.Select(d => d.Number));
            Assert.Equal(new[] { "Test Pad 1", "Test Pad 2" }, file.Devices.Select(d => d.Name));
            Assert.All(file.Devices, d => Assert.Equal(InputDeviceType.Gamepad, d.Shape.DeviceType));
            Assert.All(file.Devices, d => Assert.Equal(17, d.Shape.Buttons.Count));
            Assert.All(file.Devices, d => Assert.Equal(6, d.Shape.Axes.Count));
            Assert.NotEqual(file.Devices[0].Key, file.Devices[1].Key);

            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            using var builtIns = new BuiltInPlugins(rig.Host.Sessions.Devices, () => "Test Controller", n => n + " (Replay)");
            builtIns.SetPluginsOn(true);
            builtIns.ReplayOn = true;
            Assert.Null(builtIns.Open(recording.Path));
            Assert.Equal(2, builtIns.ReplayDeviceCount);
            Assert.Contains("register Test Pad 1 (Replay)", rig.Engine.Calls);
            Assert.Contains("register Test Pad 2 (Replay)", rig.Engine.Calls);
            builtIns.ReplayOn = false;
            Assert.Contains("unregister Test Pad 1 (Replay)", rig.Engine.Calls);
            Assert.Contains("unregister Test Pad 2 (Replay)", rig.Engine.Calls);
            Assert.Equal(0, builtIns.ReplayDeviceCount);
        }

        /// <summary>The built-ins' devices are named by PadForge, so their
        /// names follow the interface language as the merged keyboards' and
        /// the Touchpad Overlay's do: a live device is renamed with its
        /// shape and state kept, and a row read after the device went offline
        /// asks for the name in the language in use, unless the user named
        /// the device.</summary>
        [Fact]
        public void TheBuiltInsDeviceNames_FollowTheInterfaceLanguage()
        {
            string language = "en";
            string TestName() => language == "en" ? "Test Controller" : "Testcontroller";
            string ReplayName(string recorded) => language == "en" ? recorded + " (Replay)" : recorded + " (Wiedergabe)";
            using var recording = new TestRecording(2, 5, 0.02, Moving);
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            var builtIns = new BuiltInPlugins(rig.Host.Sessions.Devices, TestName, ReplayName);
            try
            {
                // Named before it is first switched on, by its fixed identity.
                Assert.True(BuiltInDeviceNames.TryName(BuiltInPlugins.TestControllerGuid, out string early));
                Assert.Equal("Test Controller", early);

                builtIns.SetPluginsOn(true);
                builtIns.TestControllerOn = true;
                builtIns.ReplayOn = true;
                Assert.Null(builtIns.Open(recording.Path));
                var test = builtIns.TestControllerDevice;
                Assert.Equal(BuiltInPlugins.TestControllerGuid, test.InstanceGuid);
                var replayed = rig.Host.Sessions.Devices.All().Where(e => e.Program == BuiltInPlugins.ReplayProgram).OrderBy(e => e.Device.Name).ToList();
                Assert.Equal(new[] { "Test Pad 1 (Replay)", "Test Pad 2 (Replay)" }, replayed.Select(e => e.Device.Name));
                var shape = test.Shape;
                int reshapes = 0;
                test.CapabilitiesChanged += () => reshapes++;

                language = "de";
                builtIns.RefreshNames();
                Assert.Equal("Testcontroller", test.Name);
                Assert.Same(shape, test.Shape);
                Assert.Equal(1, reshapes);
                Assert.Equal(new[] { "Test Pad 1 (Wiedergabe)", "Test Pad 2 (Wiedergabe)" }, replayed.Select(e => e.Device.Name));
                // A name that did not change raises nothing.
                builtIns.RefreshNames();
                Assert.Equal(1, reshapes);

                // The row as the Devices page and the mappings name it, live
                // or offline.
                var row = new UserDevice();
                row.LoadFromExternalDevice(replayed[0].Device);
                builtIns.ReplayOn = false;
                language = "en";
                Assert.Equal("Test Pad 1 (Replay)", InputService.LocalizedDeviceName(row));
                var offlineTest = new UserDevice { InstanceGuid = BuiltInPlugins.TestControllerGuid, ProductName = "Testcontroller", InstanceName = "Testcontroller" };
                Assert.Equal("Test Controller", InputService.LocalizedDeviceName(offlineTest));
                // A name the user gave stands.
                offlineTest.DisplayName = "My Bench Pad";
                Assert.Equal("My Bench Pad", InputService.LocalizedDeviceName(offlineTest));

                // The interface's language change reaches the built-ins.
                string controller = ExtrasPageTests.RepoText("PadForge.App", "Services", "Plugins", "PluginsController.cs");
                Assert.Contains("Strings.CultureChanged += BuiltIns.RefreshNames;", controller);
                Assert.Contains("Strings.CultureChanged -= BuiltIns.RefreshNames;", controller);
                string service = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
                Assert.Equal(2, service.Split("BuiltInName(ud) ?? ud.ResolvedName").Length - 1);
            }
            finally
            {
                builtIns.Dispose();
            }
            // A disposed set names nothing any more.
            Assert.False(BuiltInDeviceNames.TryName(BuiltInPlugins.TestControllerGuid, out _));
        }

        /// <summary>A Replay device's row keeps the recorded name, in no
        /// language, as its product's, and its path says it is Replay's. A row
        /// an earlier run left offline, which nothing in this run named, is
        /// named in the language in use from it, and follows a language
        /// change. A name the user gave stands.</summary>
        [Fact]
        public void AReplayRowLeftOfflineByAnEarlierRun_IsNamedInTheLanguageInUse()
        {
            using var recording = new TestRecording(1, 5, 0.02, Moving);
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            var builtIns = new BuiltInPlugins(rig.Host.Sessions.Devices, () => "Test Controller", BuiltInPlugins.ReplayShownName);
            UserDevice row;
            try
            {
                builtIns.SetPluginsOn(true);
                builtIns.ReplayOn = true;
                Assert.Null(builtIns.Open(recording.Path));
                var entry = Assert.Single(rig.Host.Sessions.Devices.All(), e => e.Program == BuiltInPlugins.ReplayProgram);
                Assert.Equal("Test Pad 1 (Replay)", entry.Device.Name);
                Assert.Equal("Test Pad 1", entry.Device.ProductName);
                Assert.StartsWith(BuiltInPlugins.ReplayPathPrefix, entry.Device.DevicePath, StringComparison.Ordinal);
                // The engine's row, as PadForge.xml saves it.
                lock (SettingsManager.UserDevices.SyncRoot)
                    row = SettingsManager.UserDevices.Items.Single(d => d.InstanceGuid == entry.Guid);
                Assert.Equal("Test Pad 1", row.ProductName);
                Assert.Equal(entry.Device.DevicePath, row.DevicePath);
            }
            finally
            {
                builtIns.Dispose();
            }

            // The next run: nothing names the device until its recording is
            // opened, and the saved row is read back.
            Assert.False(BuiltInDeviceNames.TryName(row.InstanceGuid, out _));
            var saved = new UserDevice
            {
                InstanceGuid = row.InstanceGuid,
                DevicePath = row.DevicePath,
                ProductName = row.ProductName,
                InstanceName = row.InstanceName,
            };
            // The name is made when it is shown, from the format of the
            // language in use: the row itself holds no word of any language
            // around the recorded name.
            Assert.DoesNotContain("Replay", saved.ProductName);
            Assert.Equal("Test Pad 1 (Replay)", InputService.LocalizedDeviceName(saved));
            Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.Plugins_Replay_DeviceName_Format, "Test Pad 1"),
                InputService.LocalizedDeviceName(saved));
            string service = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("return Plugins.BuiltInPlugins.ReplayShownName(ud.ProductName);", service);
            string plugins = ExtrasPageTests.RepoText("PadForge.App", "Services", "Plugins", "BuiltInPlugins.cs");
            Assert.Contains("PadForge.Resources.Strings.Strings.Instance.Plugins_Replay_DeviceName_Format, recorded);", plugins);
            saved.DisplayName = "Bench Recording";
            Assert.Equal("Bench Recording", InputService.LocalizedDeviceName(saved));
            // A program's own device keeps the name it gave: its path is not
            // Replay's.
            var program = new UserDevice { InstanceGuid = Guid.NewGuid(), DevicePath = "program://abcd2345", ProductName = "Pad" };
            Assert.Equal("Pad", InputService.LocalizedDeviceName(program));
        }

        [Fact]
        public void AFileThatIsNoRecording_IsNotOpened()
        {
            string path = Path.Combine(Path.GetTempPath(), "PadForge.Tests.NotARecording." + Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllText(path, "hello\nworld\n");
            try
            {
                Assert.Null(ReplayFile.Read(path, out string error));
                Assert.Equal("not-a-recording", error);
                Assert.Null(ReplayFile.Read(path + ".missing", out error));
                Assert.Equal("unreadable", error);
            }
            finally { File.Delete(path); }
        }

        /// <summary>A recording is a file from anyone: one past a limit, or
        /// one whose bytes are no recording, is refused with a word for the
        /// plain line the page shows, and nothing it holds is shown
        /// unclean.</summary>
        [Fact]
        public void AMalformedRecording_IsRefused_AndItsNamesAreCleaned()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.BadRecordings." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                const string head = "PadForge problem recording, format 1\n== Devices ==\n";
                string longLine = Path.Combine(dir, "long.log");
                File.WriteAllText(longLine, head + "D1 name=\"" + new string('x', ReplayFile.LongestLine + 10) + "\"\n== Events ==\n0.000000 I D1 full online a0=1\n");
                Assert.Null(ReplayFile.Read(longLine, out string error));
                Assert.Equal("malformed", error);

                var many = new System.Text.StringBuilder(head);
                for (int d = 1; d <= ReplayFile.MostDevices + 1; d++) many.Append("D").Append(d).Append(" name=\"Pad\" type=Gamepad axes=6 buttons=11 hats=1\n");
                many.Append("== Events ==\n");
                for (int d = 1; d <= ReplayFile.MostDevices + 1; d++) many.Append("0.000000 I D").Append(d).Append(" full online a0=1\n");
                string tooMany = Path.Combine(dir, "many.log");
                File.WriteAllText(tooMany, many.ToString());
                Assert.Null(ReplayFile.Read(tooMany, out error));
                Assert.Equal("too-many-devices", error);

                string twoEntries = Path.Combine(dir, "two.zip");
                using (var zip = System.IO.Compression.ZipFile.Open(twoEntries, System.IO.Compression.ZipArchiveMode.Create))
                {
                    zip.CreateEntry("a.log");
                    zip.CreateEntry("b.log");
                }
                Assert.Null(ReplayFile.Read(twoEntries, out error));
                Assert.Equal("malformed", error);

                string garbage = Path.Combine(dir, "garbage.zip");
                File.WriteAllBytes(garbage, new byte[] { (byte)'P', (byte)'K', 3, 4, 0xFF, 0xFF, 0x00, 0x13 });
                Assert.Null(ReplayFile.Read(garbage, out error));
                Assert.NotNull(error);

                string big = Path.Combine(dir, "big.log");
                using (var stream = File.Create(big)) stream.SetLength(ReplayFile.MostFileBytes + 1);
                Assert.Null(ReplayFile.Read(big, out error));
                Assert.Equal("malformed", error);

                // A name that would reverse the text after it comes out clean.
                string bidi = Path.Combine(dir, "bidi.log");
                File.WriteAllText(bidi, head + "D1 name=\"Pad‮gnp.exe\" type=Gamepad axes=6 buttons=11 hats=1\n== Events ==\n0.000000 I D1 full online a0=1\n");
                var file = ReplayFile.Read(bidi, out error);
                Assert.Null(error);
                Assert.Equal("Padgnp.exe", file.Devices.Single().Name);
            }
            finally { Directory.Delete(dir, true); }
        }

        /// <summary>The tolerance Replay keeps: no line leaves more than a
        /// millisecond before its time, half leave within 10 ms of it and
        /// nineteen in twenty within 30 ms.</summary>
        [Fact]
        public void Frames_LeaveAtTheFilesTiming_WithinTheStatedTolerance()
        {
            using var recording = new TestRecording(2, 40, 0.02, Moving);
            var file = ReplayFile.Read(recording.Path, out _);
            var devices = file.Devices.ToDictionary(d => d.Number, d => new ProgramDevice(Guid.NewGuid(), Guid.NewGuid(), "program://t" + d.Number, d.Name, d.Shape));
            using var player = new ReplayPlayer(file, devices);
            var sent = new List<(int Device, long At, long Left)>();
            var ended = new ManualResetEventSlim();
            player.Sent += (d, at, left) => { lock (sent) sent.Add((d, at, left)); };
            player.Ended += ended.Set;
            player.Play();
            Assert.True(ended.Wait(10000), "playback did not end");

            Assert.Equal(file.Frames, sent.Count);
            var lags = sent.Select(s => s.Left - s.At).OrderBy(l => l).ToList();
            _output.WriteLine($"{sent.Count} lines over {file.LengthMicros / 1000} ms, lag min {lags[0]} us, median {lags[lags.Count / 2]} us, p95 {lags[lags.Count * 95 / 100]} us, max {lags[^1]} us");
            Assert.True(lags[0] >= -1000, "a line left early: " + lags[0] + " us");
            Assert.True(lags[lags.Count / 2] <= 10_000, "median lag " + lags[lags.Count / 2] + " us");
            Assert.True(lags[lags.Count * 95 / 100] <= 30_000, "95th percentile lag " + lags[lags.Count * 95 / 100] + " us");
            // The file's times run 20 ms apart, and so did the lines.
            var firstDevice = sent.Where(s => s.Device == 1).Select(s => s.At).Distinct().ToList();
            Assert.InRange(firstDevice[^1] - firstDevice[0], 770_000, 790_000);
            Assert.False(player.Playing);
            // Each device rests at the end, as at a Stop.
            Assert.False(devices[1].PeekState().Buttons[0]);
        }

        [Fact]
        public void Repeat_StartsTheFileAgainAtItsEnd()
        {
            using var recording = new TestRecording(1, 10, 0.01, Moving);
            var file = ReplayFile.Read(recording.Path, out _);
            var devices = file.Devices.ToDictionary(d => d.Number, d => new ProgramDevice(Guid.NewGuid(), Guid.NewGuid(), "program://r", d.Name, d.Shape));
            using var player = new ReplayPlayer(file, devices) { Repeat = true };
            var passes = 0;
            long last = -1;
            var twice = new ManualResetEventSlim();
            int axisAtSecondPass = -1;
            player.Sent += (d, at, left) =>
            {
                if (at < last)
                {
                    if (++passes == 2) twice.Set();
                    if (passes == 1) axisAtSecondPass = devices[1].PeekState().Axis[0];
                }
                last = at;
            };
            bool ended = false;
            player.Ended += () => ended = true;
            player.Play();
            Assert.True(twice.Wait(10000), "Repeat did not start the file again");
            Assert.True(player.Playing);
            Assert.False(ended);
            // The second pass starts with the file's first frame.
            Assert.Equal(1000, axisAtSecondPass);
            player.Stop();
            Assert.False(player.Playing);
            Assert.Equal(0, player.PositionMicros);
        }

        // ── A recording as a test ──

        /// <summary>The recording kept with the tests: two pads, the first
        /// pressing button 0 on every other frame and pushing its stick
        /// right, made by the real recorder.</summary>
        internal static string KeptRecording => Path.Combine(AppContext.BaseDirectory, "TestData", "Recordings", "two-pads.zip");

        [Fact]
        public void ARecordingKeptWithTheTests_RunsThroughTheMappingPipeline()
        {
            Assert.True(File.Exists(KeptRecording), KeptRecording);
            var set = new PadForge.Engine.Data.MappingSet();
            using var run = new RecordingAsTest(KeptRecording);
            var pad = run.DeviceRow(1);
            set.Rows.Add(new PadForge.Engine.Data.MappingRow { Target = "ButtonA", LayerMask = "Base", Sources = { new() { DeviceGuid = pad.InstanceGuidString, Descriptor = "Button 0" } } });
            set.Rows.Add(new PadForge.Engine.Data.MappingRow { Target = "LeftThumbAxisX", LayerMask = "Base", Sources = { new() { DeviceGuid = pad.InstanceGuidString, Descriptor = "Axis 0" } } });

            var outputs = run.Outputs(1, set);
            Assert.Equal(12, outputs.Count);
            for (int f = 0; f < outputs.Count; f++)
            {
                Assert.Equal(f % 2 == 0, outputs[f].IsButtonPressed(Gamepad.A));
                // Axis 0 ran 1000 + 5000 f, so the stick moves right frame by
                // frame from its left end.
                if (f > 0) Assert.True(outputs[f].ThumbLX > outputs[f - 1].ThumbLX, "frame " + f);
            }
            Assert.True(outputs[0].ThumbLX < -28000);
        }
    }

    /// <summary>
    /// A problem recording run as a test (#597): each input frame of one of
    /// its devices goes through the mapping pipeline's own pass
    /// (InputManager.ApplyMappingSetToGamepad, as UpdateOutputStates runs
    /// it) with the mapping set a test gives, and the output of each frame
    /// comes back. The device is the one Replay would make, on a device
    /// list of the test's own.
    /// </summary>
    internal sealed class RecordingAsTest : IDisposable
    {
        private const int Slot = 6;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly PadForge.Engine.Data.MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly ReplayFile _file;
        private readonly Dictionary<int, UserDevice> _rows = new();

        private static readonly System.Reflection.MethodInfo BeginFrame = typeof(InputManager)
            .GetMethod("BeginFrameMultiSourceTracking", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        private static readonly System.Reflection.MethodInfo Apply = typeof(InputManager)
            .GetMethod("ApplyMappingSetToGamepad", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        public RecordingAsTest(string path)
        {
            _file = ReplayFile.Read(path, out string error);
            Assert.True(_file != null, "the recording did not open: " + error);
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new PadForge.Engine.Data.MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated);
            Array.Clear(SettingsManager.SlotEnabled);
            SettingsManager.SlotCreated[Slot] = true;
            SettingsManager.SlotEnabled[Slot] = true;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
            foreach (var d in _file.Devices)
            {
                var device = new ProgramDevice(ProgramDeviceDeclaration.InstanceGuid("builtin", BuiltInPlugins.ReplayProgram, d.Key),
                    Guid.NewGuid(), "program://test" + d.Number, d.Name, d.Shape);
                var ud = new UserDevice();
                ud.LoadFromExternalDevice(device);
                ud.Device = device;
                ud.IsOnline = true;
                SettingsManager.UserDevices.Items.Add(ud);
                var setting = new UserSetting { InstanceGuid = ud.InstanceGuid, ProductGuid = ud.ProductGuid, MapTo = Slot };
                setting.SetPadSetting(new PadSetting());
                SettingsManager.UserSettings.Items.Add(setting);
                _rows[d.Number] = ud;
            }
        }

        /// <summary>The device row Replay's device for D<paramref name="number"/> has.</summary>
        public UserDevice DeviceRow(int number) => _rows[number];

        /// <summary>The output of every input frame of
        /// D<paramref name="number"/>, one for each time the file gives, in
        /// the file's order.</summary>
        public List<Gamepad> Outputs(int number, PadForge.Engine.Data.MappingSet set)
        {
            var outputs = new List<Gamepad>();
            var ud = _rows[number];
            SettingsManager.SlotMappingSets[Slot] = set;
            long lastTime = long.MinValue;
            using var reader = RecordingReader.Open(_file.Path);
            while (reader.Read())
            {
                if (reader.Layer != RecordingCodec.LayerInput || reader.Device?.Number != number) continue;
                if (reader.Payload.StartsWith("device ")) continue;
                var state = reader.Device.State.Clone();
                ud.InputState = state;
                BeginFrame.Invoke(null, null);
                object[] args = { state, set, ud.InstanceGuidString, 50, Slot, new Gamepad() };
                Apply.Invoke(null, args);
                // Lines of one pass share a time: the pass's output is the
                // last one's.
                if (reader.Microseconds == lastTime) outputs[^1] = (Gamepad)args[5];
                else outputs.Add((Gamepad)args[5]);
                lastTime = reader.Microseconds;
            }
            return outputs;
        }

        public void Dispose()
        {
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
        }
    }

    internal static class TestHostAssert
    {
        /// <summary>The fields a Test Controller frame sets, equal.</summary>
        internal static void SameState(CustomInputState a, CustomInputState b)
        {
            Assert.Equal(a.Axis, b.Axis);
            Assert.Equal(a.Buttons, b.Buttons);
            Assert.Equal(a.Povs, b.Povs);
        }
    }
}
