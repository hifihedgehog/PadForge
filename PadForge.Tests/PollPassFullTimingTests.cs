using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// Times the poll pass's managed steps, in the loop's order, with one
    /// pad on slot 0 and two more on the list, and nothing a program, a
    /// script or a recording uses. SDL and the virtual controller's
    /// hand-over are left out: the test host has neither. Steps a build
    /// does not have are skipped, so the same file times an older build.
    /// Runs only under PADFORGE_BENCH_TIMING=1.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PollPassFullTimingTests
    {
        private readonly ITestOutputHelper _output;
        public PollPassFullTimingTests(ITestOutputHelper output) => _output = output;

        private static UserDevice Pad(ushort vid, ushort pid)
        {
            var wrapper = new WebControllerDevice(Guid.NewGuid().ToString(), "Pass probe");
            wrapper.SetConnected(true);
            return new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), Device = wrapper, InputState = new CustomInputState(),
                IsOnline = true, CapType = InputDeviceType.Gamepad, VendorId = vid, ProdId = pid,
            };
        }

        [Fact]
        public void TimeTheManagedPass()
        {
            if (Environment.GetEnvironmentVariable("PADFORGE_BENCH_TIMING") != "1") return;
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            var created = SettingsManager.SlotCreated;
            var setsBefore = SettingsManager.SlotMappingSets;
            var sets = (MappingSet[])setsBefore?.Clone();
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                var im = new InputManager();
                var assigned = Pad(0x045E, 0x028E);
                SettingsManager.UserDevices.Items.Add(assigned);
                SettingsManager.UserDevices.Items.Add(Pad(0x054C, 0x0CE6));
                SettingsManager.UserDevices.Items.Add(Pad(0x1234, 0x5678));
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = assigned.InstanceGuid, MapTo = 0 });
                SettingsManager.SlotCreated[0] = true;
                string g = assigned.InstanceGuid.ToString();
                var rows = new List<MappingRow>();
                string[] buttons = { "ButtonA", "ButtonB", "ButtonX", "ButtonY", "LeftShoulder", "RightShoulder", "ButtonBack", "ButtonStart" };
                for (int i = 0; i < buttons.Length; i++)
                    rows.Add(new MappingRow { Target = buttons[i], Sources = new() { new MappingSource { DeviceGuid = g, Descriptor = "Button " + i } } });
                string[] axes = { "LeftThumbAxisX", "LeftThumbAxisY", "RightThumbAxisX", "RightThumbAxisY", "LeftTrigger", "RightTrigger" };
                for (int i = 0; i < axes.Length; i++)
                    rows.Add(new MappingRow { Target = axes[i], Sources = new() { new MappingSource { DeviceGuid = g, Descriptor = "Axis " + i } } });
                SettingsManager.SlotMappingSets ??= new MappingSet[InputManager.MaxPads];
                SettingsManager.SlotMappingSets[0] = new MappingSet { Rows = rows };

                var steps = new List<(string Name, Action Run)>();
                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                void Add(string name)
                {
                    var m = typeof(InputManager).GetMethod(name, flags, Type.EmptyTypes);
                    if (m == null) { _output.WriteLine($"STEP {name} absent"); return; }
                    steps.Add((name, (Action)Delegate.CreateDelegate(typeof(Action), im, m)));
                }
                void AddBool(string name, bool arg)
                {
                    var m = typeof(InputManager).GetMethod(name, flags, new[] { typeof(bool) });
                    if (m == null) { _output.WriteLine($"STEP {name} absent"); return; }
                    var call = (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), im, m);
                    steps.Add((name, () => call(arg)));
                }
                steps.Add(("ApplyFocusSuspension", () => im.ApplyFocusSuspension()));
                Add("RunControllerDatabaseWork");
                Add("UpdateInputStates");
                Add("UpdateGyroEngageStates");
                Add("UpdateTriggerRouteEngageStates");
                Add("UpdateHapticMirrorEngageStates");
                Add("UpdateMotionSnapshots");
                Add("UpdateOutputStates");
                Add("CombineOutputStates");
                Add("WorkOutOutputCauses");
                Add("EvaluateMacros");
                AddBool("TickOnScreenKeyboard", true);
                Add("ApplyMotionRows");
                Add("PublishSteeringAngles");
                Add("UpdateRumbleAudioLane");

                int pollRun = 1;
                void Pass()
                {
                    PadForge.Engine.Common.Mapping.SourceCoercion.BeginPollFrame(pollRun, 0);
                    using (InputManager.EnterMenuPublication())
                        foreach (var s in steps) s.Run();
                }
                for (int i = 0; i < 20_000; i++) Pass();
                const int Passes = 200_000;
                double best = double.MaxValue;
                for (int round = 0; round < 3; round++)
                {
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < Passes; i++) Pass();
                    watch.Stop();
                    best = Math.Min(best, watch.Elapsed.TotalMilliseconds * 1_000_000.0 / Passes);
                }
                _output.WriteLine($"FULLPASS ns={best:F0} ({steps.Count} steps, best of 3 x {Passes})");
                foreach (var s in steps)
                {
                    var one = s.Run;
                    double stepBest = double.MaxValue;
                    for (int round = 0; round < 3; round++)
                    {
                        var watch = Stopwatch.StartNew();
                        for (int i = 0; i < 50_000; i++)
                        {
                            PadForge.Engine.Common.Mapping.SourceCoercion.BeginPollFrame(pollRun, 0);
                            using (InputManager.EnterMenuPublication()) one();
                        }
                        watch.Stop();
                        stepBest = Math.Min(stepBest, watch.Elapsed.TotalMilliseconds * 1_000_000.0 / 50_000);
                    }
                    _output.WriteLine($"STEP {s.Name} ns={stepBest:F0}");
                }
            }
            finally
            {
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
                SettingsManager.SlotCreated = created;
                if (sets != null) Array.Copy(sets, setsBefore, sets.Length);
                SettingsManager.SlotMappingSets = setsBefore;
            }
        }
    }
}
