using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The Map step (UpdateOutputStates, CombineOutputStates and, where a
    /// build has it, WorkOutOutputCauses) over a real slot's rows, timed.
    /// PADFORGE_BENCH_SLOT names a file holding one MappingSet element as
    /// PadForge.xml stores it. Every device its rows name is online, and
    /// the first one's sticks, triggers and buttons move on every pass.
    /// Without the variable the test returns at once.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MapStepBenchTests
    {
        private readonly ITestOutputHelper _output;
        public MapStepBenchTests(ITestOutputHelper output) => _output = output;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint SetThreadIdealProcessor(IntPtr thread, uint processor);
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint ms);

        /// <summary>The paired experiment: a pass, a 1 ms sleep on a
        /// high-resolution waitable timer, a pass, on a thread of its own,
        /// with the thread left free, given an ideal processor, or held to
        /// one core, the three flipped every window of 200 passes. Times
        /// only the passes.</summary>
        private void SleepPair(Action pass)
        {
            const int Windows = 60, PerWindow = 200;
            var sums = new double[3];
            var counts = new long[3];
            var mins = new double[] { double.MaxValue, double.MaxValue, double.MaxValue };
            Exception failed = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    timeBeginPeriod(1);
                    IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 2, 0x1F0003);
                    IntPtr self = GetCurrentThread();
                    int core = Environment.ProcessorCount - 2;
                    UIntPtr all = SetThreadAffinityMask(self, (UIntPtr)(1UL << core));
                    SetThreadAffinityMask(self, all);
                    try
                    {
                        for (int w = 0; w < Windows; w++)
                        {
                            int mode = w % 3;
                            if (mode == 0) { SetThreadAffinityMask(self, all); SetThreadIdealProcessor(self, 0xFFFFFFFF); }
                            else if (mode == 1) { SetThreadAffinityMask(self, all); SetThreadIdealProcessor(self, (uint)core); }
                            else SetThreadAffinityMask(self, (UIntPtr)(1UL << core));
                            double windowSum = 0;
                            for (int i = 0; i < PerWindow; i++)
                            {
                                long due = -10_000;
                                SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                                WaitForSingleObject(timer, 100);
                                long t0 = Stopwatch.GetTimestamp();
                                pass();
                                windowSum += (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
                            }
                            if (w >= 3)
                            {
                                sums[mode] += windowSum;
                                counts[mode] += PerWindow;
                                mins[mode] = Math.Min(mins[mode], windowSum / PerWindow);
                            }
                        }
                    }
                    finally
                    {
                        SetThreadAffinityMask(self, all);
                        CloseHandle(timer);
                        timeEndPeriod(1);
                    }
                }
                catch (Exception ex) { failed = ex; }
            });
            thread.Start();
            thread.Join();
            if (failed != null) throw failed;
            string[] names = { "free", "ideal core", "one-core affinity" };
            for (int m = 0; m < 3; m++)
                _output.WriteLine($"SLEEPPAIR {names[m]} mean_us={sums[m] / counts[m]:F2} best_window_us={mins[m]:F2} passes={counts[m]}");
        }

        [Fact]
        public void TimeTheMapStepOverARealSlot()
        {
            string file = Environment.GetEnvironmentVariable("PADFORGE_BENCH_SLOT");
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
            MappingSet set;
            using (var stream = File.OpenRead(file))
                set = (MappingSet)new XmlSerializer(typeof(MappingSet), new XmlRootAttribute("MappingSet")).Deserialize(stream);

            // PADFORGE_BENCH_TARGETS: comma-separated target prefixes; only
            // rows whose target starts with one of them are kept.
            string only = Environment.GetEnvironmentVariable("PADFORGE_BENCH_TARGETS");
            if (!string.IsNullOrEmpty(only))
            {
                var prefixes = only.Split(',');
                set.Rows.RemoveAll(row => !Array.Exists(prefixes, x => (row.Target ?? "").StartsWith(x, StringComparison.Ordinal)));
            }
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            var created = SettingsManager.SlotCreated;
            var setsBefore = SettingsManager.SlotMappingSets;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.SlotMappingSets[0] = set;
                SettingsManager.SlotCreated[0] = true;
                var guids = new List<Guid>();
                foreach (var row in set.Rows)
                    foreach (var src in row.Sources ?? new List<MappingSource>())
                        if (Guid.TryParse(src.DeviceGuid, out var g) && !guids.Contains(g)) guids.Add(g);
                // PADFORGE_BENCH_ONLINE: how many of the devices the rows
                // name are online (all by default). PADFORGE_BENCH_FOREIGN=1
                // adds an online device on the slot that no row names, the
                // state of the 2026-11-16 count (rows walked, nothing read).
                int online = int.TryParse(Environment.GetEnvironmentVariable("PADFORGE_BENCH_ONLINE"), out int on) ? on : int.MaxValue;
                if (Environment.GetEnvironmentVariable("PADFORGE_BENCH_FOREIGN") == "1") guids.Add(Guid.NewGuid());
                var pads = new List<UserDevice>();
                int index = 0;
                foreach (var g in guids)
                {
                    bool isOnline = index++ < online || index == guids.Count && Environment.GetEnvironmentVariable("PADFORGE_BENCH_FOREIGN") == "1";
                    var wrapper = new WebControllerDevice(g.ToString(), "Bench pad");
                    wrapper.SetConnected(true);
                    var ud = new UserDevice
                    {
                        InstanceGuid = g, Device = wrapper, InputState = new CustomInputState(),
                        IsOnline = isOnline, CapType = InputDeviceType.Gamepad, VendorId = 0x054C, ProdId = 0x0CE6,
                    };
                    wrapper.SetConnected(isOnline);
                    SettingsManager.UserDevices.Items.Add(ud);
                    var us = new UserSetting { InstanceGuid = g, MapTo = 0 };
                    us.SetPadSetting(new PadSetting());
                    SettingsManager.UserSettings.Items.Add(us);
                    pads.Add(ud);
                }
                var im = new InputManager();
                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var steps = new List<Action>();
                foreach (string name in new[] { "UpdateOutputStates", "CombineOutputStates", "WorkOutOutputCauses" })
                {
                    var m = typeof(InputManager).GetMethod(name, flags, Type.EmptyTypes);
                    if (m != null) steps.Add((Action)Delegate.CreateDelegate(typeof(Action), im, m));
                }
                // Two states for the first pad, swapped every pass, so the
                // rows see input that moves.
                var a = new CustomInputState();
                var b = new CustomInputState();
                for (int i = 0; i < 6 && i < a.Axis.Length; i++) { a.Axis[i] = 1000 * i; b.Axis[i] = 60000 - 1000 * i; }
                for (int i = 0; i < 4 && i < a.Buttons.Length; i++) b.Buttons[i] = true;
                int pass = 0;
                void Pass()
                {
                    pads[0].InputState = (pass++ & 1) == 0 ? a : b;
                    PadForge.Engine.Common.Mapping.SourceCoercion.BeginPollFrame(1, 0);
                    using (InputManager.EnterMenuPublication())
                        foreach (var s in steps) s();
                }
                int Passes = int.TryParse(Environment.GetEnvironmentVariable("PADFORGE_BENCH_PASSES"), out int asked) && asked > 0 ? asked : 200_000;
                for (int i = 0; i < Math.Min(20_000, Passes); i++) Pass();
                double best = double.MaxValue;
                int rounds = int.TryParse(Environment.GetEnvironmentVariable("PADFORGE_BENCH_ROUNDS"), out int r) && r > 0 ? r : 5;
                for (int round = 0; round < rounds; round++)
                {
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < Passes; i++) Pass();
                    watch.Stop();
                    best = Math.Min(best, watch.Elapsed.TotalMilliseconds * 1_000_000.0 / Passes);
                }
                _output.WriteLine($"MAP ns={best:F0} rows={set.Rows.Count} devices={pads.Count} online={Math.Min(online, pads.Count)} foreign={Environment.GetEnvironmentVariable("PADFORGE_BENCH_FOREIGN")} steps={steps.Count} (best of {rounds} x {Passes})");
                if (Environment.GetEnvironmentVariable("PADFORGE_BENCH_SLEEPPAIR") == "1")
                    SleepPair(Pass);
            }
            finally
            {
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
                SettingsManager.SlotCreated = created;
                SettingsManager.SlotMappingSets = setsBefore;
            }
        }
    }
}
