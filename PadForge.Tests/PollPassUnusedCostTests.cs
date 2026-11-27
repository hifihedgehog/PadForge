using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// What the poll pass pays every pass for a feature nobody uses: a
    /// foreground pass takes no lock to learn it is not suspended, and a
    /// device read with nothing held writes no note into a shared
    /// dictionary on every pass.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PollPassUnusedCostTests
    {
        private readonly ITestOutputHelper _output;
        public PollPassUnusedCostTests(ITestOutputHelper output) => _output = output;

        /// <summary>A foreground pass returns while another thread holds the
        /// menu publication gate: it does not wait on it.</summary>
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public async Task AForegroundPass_TakesNoPublicationLock(bool suspendWhenBackground, bool foreground)
        {
            var im = new InputManager { SuspendWhenBackground = suspendWhenBackground, HostIsForeground = foreground };
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                lock (InputManager.MenuPublicationSync)
                {
                    held.Set();
                    release.Wait();
                }
            });
            holder.Start();
            try
            {
                Assert.True(held.Wait(5000));
                var pass = Task.Run(() => im.ApplyFocusSuspension(() => { }, () => { }));
                Assert.True(await Task.WhenAny(pass, Task.Delay(2000)) == pass, "the pass waited on the gate another thread held");
                Assert.False(await pass);
            }
            finally
            {
                release.Set();
                holder.Join();
            }
        }

        /// <summary>A suspension still takes the gate, and it ends once the
        /// host is back in the foreground.</summary>
        [Fact]
        public void ASuspension_StillTakesTheGate_AndEnds()
        {
            var im = new InputManager { SuspendWhenBackground = true, HostIsForeground = false };
            bool entered = false;
            Assert.True(im.ApplyFocusSuspension(() => entered |= Monitor.IsEntered(InputManager.MenuPublicationSync), () => { }));
            Assert.True(entered);
            im.HostIsForeground = true;
            Assert.False(im.ApplyFocusSuspension(() => { }, () => { }));
            // Ended: the next background pass takes the neutral edge again,
            // two submissions instead of one.
            im.HostIsForeground = false;
            int submits = 0;
            Assert.True(im.ApplyFocusSuspension(() => submits++, () => { }));
            Assert.Equal(2, submits);
        }

        private static ConcurrentDictionary<Guid, long> Seen()
            => (ConcurrentDictionary<Guid, long>)typeof(ControlHolds)
                .GetField("_seen", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        /// <summary>A read with nothing held rewrites the device's note only
        /// once it is a tenth of the not-polled span old.</summary>
        [Fact]
        public void ADeviceRead_RewritesItsNoteOnlyWhenItAges()
        {
            long now = 1_000_000_000;
            long tick = Stopwatch.Frequency / 1000;
            ControlHolds.Publish(null);
            ControlHolds.ClockForTest = () => now;
            try
            {
                var device = Guid.NewGuid();
                var state = new CustomInputState();
                ControlHolds.BeginPass();
                Assert.Same(state, ControlHolds.Apply(device, state));
                long first = Seen()[device];
                Assert.Equal(now, first);
                long refresh = ProgramDevice.NotPolledTicks / 10;
                int passes = 0;
                while (now + tick - first < refresh)
                {
                    now += tick;
                    ControlHolds.BeginPass();
                    ControlHolds.Apply(device, state);
                    Assert.Equal(first, Seen()[device]);
                    passes++;
                }
                Assert.True(passes > 0);
                now += tick;
                ControlHolds.BeginPass();
                ControlHolds.Apply(device, state);
                Assert.Equal(now, Seen()[device]);
            }
            finally
            {
                ControlHolds.ClockForTest = null;
                ControlHolds.Publish(null);
            }
        }

        /// <summary>The holds and the travel readers start their pass on the
        /// clock reading Step 2 takes once, and take none of their
        /// own.</summary>
        [Fact]
        public void TheHoldsAndTravelReaders_TakeThePassClock()
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            ControlHolds.Publish(null);
            try
            {
                long passNow = 123_456_789;
                ControlHolds.BeginPass(passNow);
                Assert.Equal(passNow, (long)typeof(ControlHolds).GetField("_lastPass", flags).GetValue(null));
                InputManager.BeginTravelReadPass(passNow);
                Assert.Equal(passNow, (long)typeof(InputManager).GetField("s_travelReadTicks", flags).GetValue(null));
                // A test's clock still stands in for the pass's.
                ControlHolds.ClockForTest = () => 42;
                ControlHolds.BeginPass(passNow);
                Assert.Equal(42L, (long)typeof(ControlHolds).GetField("_lastPass", flags).GetValue(null));
            }
            finally
            {
                ControlHolds.ClockForTest = null;
                ControlHolds.Publish(null);
            }
        }

        /// <summary>Times the mouse steer checks a stick row's source pays
        /// on every read, for a gamepad's Axis 0 with nothing of mouse steer
        /// set.</summary>
        [Fact]
        public void TimeTheMouseSteerChecksOnAGamepadAxis()
        {
            var axis = new PadForge.Engine.Data.MappingSource { Kind = "Direct", DeviceGuid = Guid.NewGuid().ToString(), Descriptor = "Axis 0" };
            var button = new PadForge.Engine.Data.MappingSource { Kind = "Direct", DeviceGuid = axis.DeviceGuid, Descriptor = "Button 0" };
            string guid = axis.DeviceGuid;
            Time("IsMouseTravel Axis 0", () => PadForge.Engine.Common.Mapping.SourceEvaluator.IsMouseTravel(axis));
            Time("MouseSteerOf Axis 0", () => PadForge.Engine.Common.Mapping.SourceEvaluator.MouseSteerOf(axis, guid, out _));
            Time("ReadsOwnGate Axis 0", () => PadForge.Engine.Common.Mapping.SourceEvaluator.ReadsOwnGate(axis, guid));
            Time("ReadsOwnGate Button 0", () => PadForge.Engine.Common.Mapping.SourceEvaluator.ReadsOwnGate(button, guid));
        }

        [System.Runtime.InteropServices.DllImport("SDL3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern uint SDL_CreateProperties();
        [System.Runtime.InteropServices.DllImport("SDL3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void SDL_DestroyProperties(uint props);
        [System.Runtime.InteropServices.DllImport("SDL3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.U1)]
        private static extern bool SDL_SetNumberProperty(uint props, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string name, long value);

        /// <summary>Times what a DualSense's status bytes cost a pass to
        /// read from SDL: its property read by name, and the two handle
        /// calls before it (here on no handle, which SDL checks and
        /// refuses).</summary>
        [Fact]
        public void TimeTheStatusBytesRead()
        {
            uint props = SDL_CreateProperties();
            Assert.NotEqual(0u, props);
            try
            {
                Assert.True(SDL_SetNumberProperty(props, DualSenseStatusBytes.Property, 0x0102));
                Time("SDL_GetNumberProperty status bytes", () => SDL3.SDL.SDL_GetNumberProperty(props, DualSenseStatusBytes.Property, 0));
                Time("SDL_GetGamepadJoystick none", () => SDL3.SDL.SDL_GetGamepadJoystick(IntPtr.Zero));
                Time("SDL_GetJoystickProperties none", () => SDL3.SDL.SDL_GetJoystickProperties(IntPtr.Zero));
            }
            finally { SDL_DestroyProperties(props); }
        }

        private void Time(string name, Action call)
        {
            for (int i = 0; i < 20_000; i++) call();
            const int Calls = 200_000;
            double best = double.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < Calls; i++) call();
                watch.Stop();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds * 1_000_000.0 / Calls);
            }
            _output.WriteLine($"HOOK {name} ns={best:F1}");
        }

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

        /// <summary>Times 200,000 passes of the focus check and the Read
        /// step with one assigned pad and two more, and nothing a program,
        /// a script or a recording uses. Reports nanoseconds a pass.</summary>
        [Fact]
        public void TimeTheUnusedPass()
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            var created = SettingsManager.SlotCreated;
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
                var read = (Action)Delegate.CreateDelegate(typeof(Action), im,
                    typeof(InputManager).GetMethod("UpdateInputStates", BindingFlags.NonPublic | BindingFlags.Instance));
                void Pass()
                {
                    im.ApplyFocusSuspension();
                    using (InputManager.EnterMenuPublication()) read();
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
                _output.WriteLine($"PASS ns={best:F0} (focus check + Read step, 3 pads, best of 3 x {Passes})");

                // Each hook the pass gained, alone, with nothing using it.
                var flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var beginMarks = (Action)Delegate.CreateDelegate(typeof(Action), im, typeof(InputManager).GetMethod("BeginProgramMarks", flags));
                var recordInput = (Action<UserDevice, CustomInputState>)Delegate.CreateDelegate(typeof(Action<UserDevice, CustomInputState>),
                    typeof(InputManager).GetMethod("RecordInput", flags));
                var drain = typeof(InputManager).Assembly.GetType("PadForge.Common.Input.ControllerDatabaseEngine")
                    .GetMethod("Drain", flags);
                var drainCall = (Action<InputManager>)Delegate.CreateDelegate(typeof(Action<InputManager>), drain);
                var state = new CustomInputState();
                Time("ApplyFocusSuspension", () => im.ApplyFocusSuspension());
                Time("EnterMenuPublication scope", () => { using (InputManager.EnterMenuPublication()) { } });
                Time("ControlHolds.BeginPass", ControlHolds.BeginPass);
                Time("ControlHolds.Apply", () => ControlHolds.Apply(assigned.InstanceGuid, state));
                Time("BeginProgramMarks", beginMarks);
                Time("NoteProgramMarks", () => im.NoteProgramMarks(assigned, state));
                Time("WorkOutOutputCauses", im.WorkOutOutputCauses);
                Time("ControllerDatabaseEngine.Drain", () => drainCall(im));
                Time("BeginTravelReadPass", InputManager.BeginTravelReadPass);
                Time("RecordInput", () => recordInput(assigned, state));
            }
            finally
            {
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
                SettingsManager.SlotCreated = created;
            }
        }
    }
}
