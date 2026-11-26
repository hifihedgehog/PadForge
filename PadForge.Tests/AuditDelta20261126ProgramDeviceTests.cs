using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The devices programs add, as the audit of 2026-11-26 found them: a
    /// stopped engine's handler on a device, whose device a second program of
    /// the same claimed name takes, the player a program's device shows once
    /// PadForge lets go of it, and a Logitech library PadForge refuses.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261126ProgramDeviceTests
    {
        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        private static ProgramDevice Device(ProgramDeviceShape shape = null)
            => new ProgramDevice(Guid.NewGuid(), Guid.NewGuid(), "program://test", "Pad", shape ?? new ProgramDeviceShape());

        private static int Handlers(ProgramDevice device)
        {
            var field = typeof(ProgramDevice).GetField(nameof(ProgramDevice.CapabilitiesChanged), BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (field.GetValue(device) as Delegate)?.GetInvocationList().Length ?? 0;
        }

        // ── A stopped engine lets go of the devices programs added ──

        /// <summary>The user stops the engine and starts it again: the
        /// device a script declared is registered with the new input manager,
        /// and the stopped one's handler is no longer on it.</summary>
        [Fact]
        public void AStoppedEngine_TakesItsHandlerOffEveryProgramDevice()
        {
            var devices = SettingsManager.UserDevices;
            SettingsManager.UserDevices = new DeviceCollection();
            var first = new InputManager();
            var second = new InputManager();
            try
            {
                var device = Device();
                first.RegisterProgramDevice(device);
                Assert.Equal(1, Handlers(device));
                first.Dispose();
                second.RegisterProgramDevice(device);
                Assert.Equal(1, Handlers(device));
                device.Reshape("Pad", new ProgramDeviceShape());
            }
            finally
            {
                second.Dispose();
                SettingsManager.UserDevices = devices;
            }
        }

        // ── A device is its program's, not its claimed name's ──

        private static AutomationSession Session(int number, string key)
            => new AutomationSession(null, new MemoryStream(), null, number, CancellationToken.None, () => 0)
            {
                Who = key == null ? null : new AllowedProgram(key, "Pad Tool", "script", null, null, null, AutomationLevel.ChangeSettings),
            };

        /// <summary>Two scripts that say hello with the same name are two
        /// programs: the second's declaration of the same key is a device of
        /// its own, and the first's assigned device stays the first's.</summary>
        [Fact]
        public void TwoScriptsOfOneClaimedName_DeclareTwoDevices()
        {
            var registry = new ProgramDevices(() => 0) { IsAssigned = _ => true, ForgetRow = _ => { } };
            try
            {
                var a = Session(1, @"script|C:\TOOLS\A.EXE");
                var b = Session(2, @"script|C:\TOOLS\B.EXE");
                Assert.Null(registry.Declare(a, "pad", "Pad", new ProgramDeviceShape(), out var first, out _));
                registry.SessionEnded(a, now: true);
                Assert.Null(registry.Declare(b, "pad", "Pad", new ProgramDeviceShape(), out var second, out bool known));
                Assert.False(known);
                Assert.NotEqual(first.Guid, second.Guid);
            }
            finally
            {
                registry.Dispose();
            }
        }

        /// <summary>A script whose path Windows would not give is known only
        /// by its process number, which the next start does not keep, so it
        /// gets no device to come back to.</summary>
        [Fact]
        public void AScriptKnownOnlyByItsProcessNumber_DeclaresNothing()
        {
            var registry = new ProgramDevices(() => 0) { IsAssigned = _ => true, ForgetRow = _ => { } };
            try
            {
                Assert.Equal("unnamed-program", registry.Declare(Session(1, "script|?4242"), "pad", "Pad", new ProgramDeviceShape(), out _, out _));
                Assert.Equal("unnamed-program", registry.Declare(Session(2, null), "pad", "Pad", new ProgramDeviceShape(), out _, out _));
                Assert.Equal(0, registry.Held);
            }
            finally
            {
                registry.Dispose();
            }
        }

        /// <summary>The refusal says what happened: no device for a program
        /// whose file Windows did not name, apart from a connection PadForge
        /// did not take, in the pipe's words and in Activity's.</summary>
        [Fact]
        public void ADeclareForAnUnnamedProgram_SaysSoInItsOwnWords()
        {
            string words = AutomationLine.WordsFor("unnamed-program");
            Assert.NotEqual(AutomationLine.WordsFor("refused"), words);
            Assert.NotEqual(AutomationLine.WordsFor("never-said"), words);
            Assert.Contains("device", words, StringComparison.Ordinal);
            var s = PadForge.Resources.Strings.Strings.Instance;
            string phrase = PadForge.ViewModels.AutomationActivityItem.RefusalWords("err unnamed-program " + words);
            Assert.Equal(s.Automation_Result_UnnamedProgram, phrase);
            Assert.NotEqual(s.Automation_Result_Refused, phrase);
        }

        // ── The player a program's device shows ──

        /// <summary>A device that showed player 2 hears player 0 once
        /// PadForge lets go of it, and one that never showed a number hears
        /// nothing.</summary>
        [Fact]
        public void AProgramDevice_HearsPlayerZeroWhenLetGo()
        {
            var heard = new List<int>();
            var device = Device(new ProgramDeviceShape { Player = true });
            device.PlayerChanged += n => heard.Add(n);
            device.SetPlayerNumber(0);
            Assert.Empty(heard);
            device.SetPlayerNumber(2);
            device.SetPlayerNumber(0);
            device.SetPlayerNumber(0);
            Assert.Equal(new[] { 2, 0 }, heard);
            Assert.Null(device.LastPlayer);
        }

        /// <summary>The user unassigns a program's device that showed player
        /// 2 and a light: the pass that seeds the players tells it player 0
        /// and the light off, and leaves a device a slot holds alone.</summary>
        [Fact]
        public void AnUnassignedProgramDevice_IsToldPlayerZeroAndLightOff()
        {
            var shape = new ProgramDeviceShape { Player = true, Light = true };
            var loose = Device(shape);
            var held = Device(shape);
            var players = new List<int>();
            var lights = new List<(byte, byte, byte)>();
            loose.PlayerChanged += n => players.Add(n);
            loose.LedChanged += (r, g, b) => lights.Add((r, g, b));
            int heldHeard = 0;
            held.PlayerChanged += _ => heldHeard++;
            loose.SetPlayerNumber(2);
            loose.SetLed(255, 0, 0);
            held.SetPlayerNumber(1);
            var devices = new DeviceCollection();
            var looseRow = new UserDevice { InstanceGuid = loose.InstanceGuid, Device = loose, IsOnline = true };
            var heldRow = new UserDevice { InstanceGuid = held.InstanceGuid, Device = held, IsOnline = true };
            devices.Items.Add(looseRow);
            devices.Items.Add(heldRow);
            PadForge.Services.InputService.LetGoOfUnheldRelayedDevices(devices, new List<Guid> { held.InstanceGuid });
            PadForge.Services.InputService.LetGoOfUnheldRelayedDevices(devices, new List<Guid> { held.InstanceGuid });
            Assert.Equal(new[] { 2, 0 }, players);
            Assert.Equal(new[] { ((byte)255, (byte)0, (byte)0), ((byte)0, (byte)0, (byte)0) }, lights);
            Assert.Equal(1, heldHeard);
        }

        /// <summary>A web pad the user unassigns stops showing its player
        /// pips and light, and one that never showed either hears
        /// nothing.</summary>
        [Fact]
        public void AnUnassignedWebPad_IsToldPlayerZeroAndLightOff()
        {
            var shown = new WebControllerDevice("audit-shown", "Web 1");
            var fresh = new WebControllerDevice("audit-fresh", "Web 2");
            var players = new List<int>();
            var lights = new List<(byte, byte, byte)>();
            int freshHeard = 0;
            shown.PlayerIndexChanged += n => players.Add(n);
            shown.LedChanged += (r, g, b) => lights.Add((r, g, b));
            fresh.PlayerIndexChanged += _ => freshHeard++;
            fresh.LedChanged += (_, _, _) => freshHeard++;
            shown.SetPlayerNumber(3);
            shown.SetLed(0, 0, 255);
            var devices = new DeviceCollection();
            devices.Items.Add(new UserDevice { InstanceGuid = shown.InstanceGuid, Device = shown, IsOnline = true });
            devices.Items.Add(new UserDevice { InstanceGuid = fresh.InstanceGuid, Device = fresh, IsOnline = true });
            PadForge.Services.InputService.LetGoOfUnheldRelayedDevices(devices, new List<Guid>());
            PadForge.Services.InputService.LetGoOfUnheldRelayedDevices(devices, new List<Guid>());
            Assert.Equal(new[] { 3, 0 }, players);
            Assert.Equal(new[] { ((byte)0, (byte)0, (byte)255), ((byte)0, (byte)0, (byte)0) }, lights);
            Assert.Equal(0, freshHeard);
        }

        // ── A Logitech library PadForge refuses ──

        /// <summary>A library PadForge did not load because others can change
        /// it or Logitech did not sign it gets its own reason in each of the
        /// three loaders, apart from one that would not load.</summary>
        [Fact]
        public void EachLogitechLoader_ReportsARefusalApart()
        {
            foreach (string file in new[] { "LogitechArxLibrary.cs", "LogitechLcdLibrary.cs", "LogitechGKeySource.cs" })
            {
                string source = RepoFile("PadForge.Engine", "Common", "Logitech", file);
                Assert.Contains("TrustedLibrary.Outcome(", source, StringComparison.Ordinal);
            }
            string gkeys = RepoFile("PadForge.Engine", "Common", "Logitech", "LogitechGKeySource.cs");
            Assert.Contains("LogitechGKeyState.Refused", gkeys, StringComparison.Ordinal);
            string host = RepoFile("PadForge.App", "Common", "Input", "KeyboardScreens", "KeyboardScreenHost.cs");
            Assert.Contains("KeyboardScreenRoute.ArxRefused", host, StringComparison.Ordinal);
        }

        /// <summary>Every candidate that did not load is written to the
        /// diagnostics log, which the Arx card's load-failure line points at:
        /// one that could not be opened ("missing", though every candidate is
        /// a file that exists) as that.</summary>
        [Fact]
        public void EveryLogitechCandidateThatDidNotLoad_IsLogged()
        {
            Assert.Equal(@"LOGITECH ARX C:\L\x.dll not loaded: could not be opened",
                PadForge.Engine.Common.TrustedLibrary.NotLoadedLine("LOGITECH ARX", @"C:\L\x.dll", "missing"));
            Assert.Equal(@"LOGITECH ARX C:\L\x.dll not loaded: would not load",
                PadForge.Engine.Common.TrustedLibrary.NotLoadedLine("LOGITECH ARX", @"C:\L\x.dll", "load"));
            Assert.Equal(@"LOGITECH LCD C:\L\x.dll not loaded: not signed",
                PadForge.Engine.Common.TrustedLibrary.NotLoadedLine("LOGITECH LCD", @"C:\L\x.dll", "not signed"));
            foreach (string file in new[] { "LogitechArxLibrary.cs", "LogitechLcdLibrary.cs", "LogitechGKeySource.cs" })
            {
                string source = RepoFile("PadForge.Engine", "Common", "Logitech", file);
                Assert.Contains("SdlDiagLog.WriteLine(TrustedLibrary.NotLoadedLine(", source, StringComparison.Ordinal);
                Assert.DoesNotContain("if (why != ", source, StringComparison.Ordinal);
            }
        }

        /// <summary>A candidate the checks refused (TrustedLibrary.Check's
        /// reasons) reads as refused, a load failure outranks it, and a
        /// library that is not the one wanted outranks both.</summary>
        [Fact]
        public void ARefusedLibrary_IsNotALoadFailure()
        {
            Assert.Equal("refused", PadForge.Engine.Common.TrustedLibrary.Outcome("missing", "not signed"));
            Assert.Equal("refused", PadForge.Engine.Common.TrustedLibrary.Outcome("missing", @"others may change C:\Logitech"));
            Assert.Equal("refused", PadForge.Engine.Common.TrustedLibrary.Outcome("refused", "signed by Someone Else"));
            Assert.Equal("load", PadForge.Engine.Common.TrustedLibrary.Outcome("refused", "load"));
            Assert.Equal("load", PadForge.Engine.Common.TrustedLibrary.Outcome("load", "not signed"));
            Assert.Equal("load", PadForge.Engine.Common.TrustedLibrary.Outcome("missing", "missing"));
            Assert.Equal("exports", PadForge.Engine.Common.TrustedLibrary.Outcome("load", "exports"));
            Assert.Equal("exports", PadForge.Engine.Common.TrustedLibrary.Outcome("exports", "not signed"));

            Assert.Equal(PadForge.Engine.Common.Logitech.LogitechGKeyState.Refused,
                PadForge.Engine.Common.Logitech.LogitechGKeySource.StateWhenNoneLoaded("refused"));
            Assert.Equal(PadForge.Engine.Common.Logitech.LogitechGKeyState.LoadFailed,
                PadForge.Engine.Common.Logitech.LogitechGKeySource.StateWhenNoneLoaded("load"));

            Assert.Equal(PadForge.Common.Input.KeyboardScreens.KeyboardScreenRoute.ArxRefused,
                PadForge.Common.Input.KeyboardScreens.KeyboardScreenHost.ArxRouteFor("refused"));
            Assert.Equal(PadForge.Common.Input.KeyboardScreens.KeyboardScreenRoute.ArxLoadFailed,
                PadForge.Common.Input.KeyboardScreens.KeyboardScreenHost.ArxRouteFor("load"));
            Assert.Equal(PadForge.Common.Input.KeyboardScreens.KeyboardScreenRoute.ArxNoLibrary,
                PadForge.Common.Input.KeyboardScreens.KeyboardScreenHost.ArxRouteFor("missing"));
        }
    }
}
