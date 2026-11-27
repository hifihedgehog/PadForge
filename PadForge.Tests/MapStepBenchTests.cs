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
