using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using InputManager = PadForge.Common.Input.InputManager;
using Keyboard = System.Windows.Input.Keyboard;
using MouseButton = System.Windows.Input.MouseButton;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using InputEventArgs = System.Windows.Input.InputEventArgs;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using Xunit;
using PadForge.Engine.Common;

namespace PadForge.Tests
{
    /// <summary>
    /// The boundary between programs and PadForge (#597), which runs with
    /// administrator rights: keyboard and mouse output held back from windows
    /// with rights, the checks of who serves a pipe and who owns a window,
    /// the lock only a copy with rights can take, the limits on sessions,
    /// devices, values, questions, snapshots and recordings, the level read
    /// again where it is applied, the names shown to the user and what reads
    /// carry. Every pipe and window has a name of the test's own, and no
    /// key or click reaches this PC: the gate is driven through its seams
    /// and the output queue's test path.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationHardeningTests
    {
        private static readonly IntPtr Medium = (IntPtr)11, High = (IntPtr)22;

        /// <summary>Stands in the windows the gate reads: the one in front
        /// and the one under the pointer, each by handle, whose process is
        /// the handle's number and runs at the level the test gives.</summary>
        private sealed class Windows : IDisposable
        {
            public IntPtr Front = Medium, UnderPointer = Medium;
            public bool InForce = true;

            public Windows()
            {
                ProgramInputGate.InForceSeam = () => InForce;
                ProgramInputGate.ForegroundSeam = () => Front;
                ProgramInputGate.PointerWindowSeam = () => UnderPointer;
                ProgramInputGate.ProcessOfSeam = w => (uint)w;
                ProgramInputGate.IntegritySeam = pid => pid == (uint)High ? 0x3000 : pid == (uint)Medium ? 0x2000 : null;
                ProgramInputGate.ForgetForTest();
            }

            public void Dispose()
            {
                ProgramInputGate.InForceSeam = null;
                ProgramInputGate.ForegroundSeam = null;
                ProgramInputGate.PointerWindowSeam = null;
                ProgramInputGate.ProcessOfSeam = null;
                ProgramInputGate.IntegritySeam = null;
                ProgramInputGate.ExtraInfoSeam = null;
                ProgramInputGate.ForgetForTest();
            }
        }

        private const uint KeyUp = 0x0002, Move = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, XDown = 0x0080, XUp = 0x0100, Wheel = 0x0800;

        private static InputManager.InjectedEvent Key(ushort vk, bool up) => new(true, vk, 0, up ? KeyUp : 0, 0, 0, 0);
        private static InputManager.InjectedEvent Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0) => new(false, 0, 0, flags, dx, dy, data);

        private static readonly InputManager.InjectedEvent[] AClickAKeyAMoveAndATurn =
        {
            Key(0x41, up: false), Key(0x41, up: true),
            Mouse(Move, 5, 5), Mouse(LeftDown), Mouse(LeftUp), Mouse(Wheel, data: 120), Mouse(XDown, data: 1), Mouse(XUp, data: 1),
        };

        // ── 2b. Output to windows with rights ──

        [Fact]
        public void TheGate_SendsEverything_WhileNoProgramDrivesPadForge()
        {
            using var windows = new Windows { InForce = false, Front = High, UnderPointer = High };
            Assert.Equal(AClickAKeyAMoveAndATurn, InputManager.GateForTest(AClickAKeyAMoveAndATurn));
        }

        [Fact]
        public void TheGate_SendsEverything_ToAWindowAtMediumIntegrity()
        {
            using var windows = new Windows();
            Assert.Equal(AClickAKeyAMoveAndATurn, InputManager.GateForTest(AClickAKeyAMoveAndATurn));
            Assert.False(ProgramInputGate.KeysBlocked);
            Assert.False(ProgramInputGate.ButtonsBlocked);
        }

        [Fact]
        public void TheGate_KeepsOnlyReleases_ForAWindowInFrontWithRights()
        {
            using var windows = new Windows { Front = High };
            var sent = InputManager.GateForTest(AClickAKeyAMoveAndATurn);
            Assert.Equal(new[] { Key(0x41, up: true), Mouse(LeftUp), Mouse(XUp, data: 1) }, sent);
        }

        [Fact]
        public void TheGate_HoldsClicksAndTurns_ForTheWindowUnderThePointer_AndLetsKeysAndMovesGo()
        {
            using var windows = new Windows { UnderPointer = High };
            var sent = InputManager.GateForTest(AClickAKeyAMoveAndATurn);
            Assert.Equal(new[] { Key(0x41, up: false), Key(0x41, up: true), Mouse(Move, 5, 5), Mouse(LeftUp), Mouse(XUp, data: 1) }, sent);
        }

        [Fact]
        public void TheGate_HoldsBack_WhenNoWindowIsInFront_OrItsProcessCannotBeRead()
        {
            using var windows = new Windows { Front = IntPtr.Zero };
            Assert.True(ProgramInputGate.KeysBlocked);
            windows.Front = (IntPtr)33;   // a process whose token is not read
            Assert.True(ProgramInputGate.KeysBlocked);
        }

        [Fact]
        public void TheGate_ReadsAProcessOnce_UntilTheWindowInFrontChanges()
        {
            using var windows = new Windows();
            for (int i = 0; i < 100; i++) Assert.False(ProgramInputGate.KeysBlocked);
            Assert.Equal(1, ProgramInputGate.IntegrityReads);
            windows.Front = High;
            for (int i = 0; i < 100; i++) Assert.True(ProgramInputGate.KeysBlocked);
            Assert.Equal(2, ProgramInputGate.IntegrityReads);
        }

        [Fact]
        public void TheGate_IsInForce_WhileAHoldADeviceOrAValueStands()
        {
            ProgramInputGate.InForceSeam = null;
            ControlHolds.Publish(null);
            ScriptValues.ResetForTest();
            try
            {
                Assert.False(ProgramInputGate.InForce);
                ControlHolds.Publish(new Dictionary<Guid, ControlHolds.DeviceHolds>
                {
                    [Guid.NewGuid()] = new ControlHolds.DeviceHolds(new[] { (0, true) }, null, null),
                });
                Assert.True(ProgramInputGate.InForce);
                ControlHolds.Publish(null);
                Assert.False(ProgramInputGate.InForce);
                Assert.True(ScriptValues.Set("speed", 1, 7));
                Assert.True(ProgramInputGate.InForce);
                ScriptValues.ReleaseOwner(7);
                Assert.False(ProgramInputGate.InForce);
                ProgramInputGate.ProgramDevicesChanged(1);
                Assert.True(ProgramInputGate.InForce);
                ProgramInputGate.ProgramDevicesChanged(-1);
                Assert.False(ProgramInputGate.InForce);
            }
            finally
            {
                ControlHolds.Publish(null);
                ScriptValues.ResetForTest();
            }
        }

        [Fact]
        public void TheGate_CountsAProgramsDevice_FromItsDeclareToItsGoing()
        {
            ProgramInputGate.InForceSeam = null;
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            Assert.False(ProgramInputGate.InForce);
            using var client = rig.Open();
            string id = client.Final("declare key=pad name=\"Pad\" {}").Substring(6);
            Assert.True(ProgramInputGate.InForce);
            Assert.Equal("ok", client.Final("gone " + id));
            Assert.False(ProgramInputGate.InForce);
        }

        [Fact]
        public void EveryOutputThatReachesWindows_AsksTheGate()
        {
            string macros = OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.Step4b.EvaluateMacros.cs");
            string sendQueued = FenceSource.Body(macros, "private static void SendQueued(INPUT[] inputs, int count)");
            Assert.Contains("if (ProgramInputGate.InForce) count = KeepWhatTheGateAllows(inputs, count);", sendQueued);
            Assert.True(sendQueued.IndexOf("KeepWhatTheGateAllows", StringComparison.Ordinal) < sendQueued.IndexOf("SendInput(", StringComparison.Ordinal));
            string hid = OffLimitsSource.Read("PadForge.App/Common/Input/KbmHidDevices.cs");
            Assert.Contains("Send(ProgramInputGate.KeysBlocked ? Nothing(f.Consumer) : f);", hid);
            Assert.Contains("byte buttons = Effective(out bool keysBlocked, out bool buttonsBlocked);", hid);
            Assert.Contains("!ProgramInputGate.KeysBlocked && SetCursorPosNative(x, y)",
                OffLimitsSource.Read("PadForge.App/Common/Input/KeyboardMouseVirtualController.cs"));
            Assert.Contains("!PadForge.Common.Input.ProgramInputGate.KeysBlocked && SetCursorPosNative(X, Y)",
                OffLimitsSource.Read("PadForge.App/Services/CursorControlService.cs"));
        }

        // ── 2b, the second wall: the answer buttons ──

        /// <summary>Runs <paramref name="body"/> on an STA thread, which a
        /// button and the input events need.</summary>
        private static void OnSta(Action body)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { error = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA test did not finish");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }

        [Fact]
        public void TheAnswerButtons_IgnorePadForgesOwnClicksAndKeys_WhileAProgramDrivesIt() => OnSta(() =>
        {
            using var windows = new Windows();
            var button = new Button();
            OwnOutputGuard.SetRefuses(button, true);
            bool Raised(InputEventArgs e)
            {
                button.RaiseEvent(e);
                return e.Handled;
            }
            MouseButtonEventArgs Press() => new(System.Windows.Input.Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = System.Windows.UIElement.PreviewMouseDownEvent };
            MouseButtonEventArgs Release() => new(System.Windows.Input.Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = System.Windows.UIElement.PreviewMouseUpEvent };
            KeyEventArgs Space() => new(Keyboard.PrimaryDevice, new FakePresentationSource(), 0, System.Windows.Input.Key.Space)
            { RoutedEvent = System.Windows.UIElement.PreviewKeyDownEvent };

            ProgramInputGate.ExtraInfoSeam = () => PadForge.Engine.Common.InputHookManager.OutputTag;
            Assert.True(Raised(Press()));
            Assert.True(Raised(Release()));
            Assert.True(Raised(Space()));
            // The user's own click and key go through.
            ProgramInputGate.ExtraInfoSeam = () => IntPtr.Zero;
            Assert.False(Raised(Press()));
            Assert.False(Raised(Space()));
            // With no program driving PadForge its own output is not held.
            windows.InForce = false;
            ProgramInputGate.ExtraInfoSeam = () => PadForge.Engine.Common.InputHookManager.OutputTag;
            Assert.False(Raised(Press()));
        });

        private sealed class FakePresentationSource : System.Windows.PresentationSource
        {
            protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null;
            public override System.Windows.Media.Visual RootVisual { get; set; }
            public override bool IsDisposed => false;
        }

        [Fact]
        public void TheCardAndThePage_PutTheGuardOnEveryAnswerButton()
        {
            string card = OffLimitsSource.Read("PadForge.App/Views/AutomationRequestCard.xaml");
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(card, "OwnOutputGuard.Refuses=\"True\"").Count);
            string page = OffLimitsSource.Read("PadForge.App/Views/ExtrasPage.xaml");
            Assert.Contains("Command=\"{Binding DenyCommand}\" common:OwnOutputGuard.Refuses=\"True\"", page);
            Assert.Contains("Command=\"{Binding AllowCommand}\" Margin=\"10,0,0,0\" common:OwnOutputGuard.Refuses=\"True\"", page);
        }

        // ── 3. Standing in for PadForge ──

        [Fact]
        public async Task APipeThisTestMade_FailsTheOwnerCheckOfPadForgesName_Live()
        {
            string name = "PadForge.Tests.Owner." + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var waiting = server.WaitForConnectionAsync();
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            client.Connect(5000);
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            string owner = PipeOwner.Of(client.SafePipeHandle);
            Assert.Equal(WindowsIdentity.GetCurrent().User.Value, owner);
            // This process runs without rights, so its pipe's owner is its
            // user, which PadForge's name never takes.
            Assert.False(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
            Assert.False(PipeOwner.IsServer(client.SafePipeHandle, AutomationPipe.PipeName));
            Assert.False(PipeOwner.IsServer(client.SafePipeHandle, ExternalControlService.PipeName));
            // Windows compares pipe names without regard to case, and so does
            // the rule.
            Assert.False(PipeOwner.IsServer(client.SafePipeHandle, "padforge.AUTOMATION"));
            // A test's own name takes the test's own user, the plugin kit's rule.
            Assert.True(PipeOwner.IsServer(client.SafePipeHandle, name));
        }

        [Fact]
        public void PadForgesOwnNames_AreThePipesItServes()
        {
            Assert.Equal(new[] { ExternalControlService.PipeName, AutomationPipe.PipeName }, PipeOwner.OwnNames);
            Assert.True(PipeOwner.IsOwnName("PADFORGE.CONTROL"));
            Assert.False(PipeOwner.IsOwnName("PadForge.Tests.Owner"));
            Assert.False(PipeOwner.IsOwnName(null));
            // The plugin kit carries the very file PadForge builds, so the
            // kit's C# plugin keeps the same rule.
            Assert.Equal(ExtrasPageTests.RepoText("PadForge.App", "Services", "Automation", "PipeOwner.cs"),
                System.Text.Encoding.UTF8.GetString(PadForge.Services.Plugins.PluginKit.Read("PipeOwner.cs")));
        }

        [Fact]
        public void EveryLibrary_ChecksWhoServesThePipe_BeforeItSendsAnything()
        {
            var libraries = AutomationLibraries.All.ToDictionary(l => l.Kind,
                l => System.Text.Encoding.UTF8.GetString(AutomationLibraries.Read(l.Kind)));
            foreach (var (kind, text) in libraries)
            {
                Assert.Contains("S-1-5-32-544", text);
                Assert.Contains("S-1-5-18", text);
                Assert.Contains("It is not PadForge, so nothing was sent to it.", text);
                Assert.Contains("0x3000", text);
            }
            // Each opens at the identification level.
            Assert.Contains("_SECURITY_SQOS_PRESENT | _SECURITY_IDENTIFICATION", libraries[AutomationLibraryKind.Python]);
            Assert.Contains("TokenImpersonationLevel.Identification", libraries[AutomationLibraryKind.PowerShell]);
            Assert.Contains("\"UInt\", 0x00110000", libraries[AutomationLibraryKind.AutoHotkey]);
            // A pipe of another name than PadForge's own takes the caller's
            // own user, PadForge's never.
            Assert.Contains("name.lower() not in _OWN_NAMES and found is not None and found == _own_user()", libraries[AutomationLibraryKind.Python]);
            Assert.Contains("_OWN_NAMES = (\"padforge.control\", \"padforge.automation\")", libraries[AutomationLibraryKind.Python]);
            Assert.Contains("|| string.Equals(name, \"PadForge.Control\", StringComparison.OrdinalIgnoreCase);", libraries[AutomationLibraryKind.PowerShell]);
            Assert.Contains("if (isTheAppsOwn || owner == null) return false;", libraries[AutomationLibraryKind.PowerShell]);
            Assert.Contains("pipe != PadForge.DefaultPipe && pipe != \"PadForge.Control\" && owner != \"\" && owner = PadForge.OwnUser()", libraries[AutomationLibraryKind.AutoHotkey]);
            // The owner is read before the first write: the PowerShell
            // library reads only once it is checked.
            string ps = libraries[AutomationLibraryKind.PowerShell];
            Assert.True(ps.IndexOf("ServerIsPadForge($Pipe", StringComparison.Ordinal) < ps.IndexOf("$handle.Start()", StringComparison.Ordinal));
        }

        [Fact]
        public void TheOwnerRule_TakesAdministratorsAndTheSystem_AndNoUser()
        {
            Assert.True(PipeOwner.Taken("S-1-5-32-544"));
            Assert.True(PipeOwner.Taken("S-1-5-18"));
            Assert.False(PipeOwner.Taken("S-1-5-21-1-2-3-1001"));
            Assert.False(PipeOwner.Taken(null));
            Assert.True(PipeOwner.Taken("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001"));
        }

        [Fact]
        public void PadForgesPipes_NameTheirOwner_TheSystemOrAdministrators()
        {
            var user = WindowsIdentity.GetCurrent().User;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            Assert.Equal(admins, ExternalControlService.BuildSecurity(user, user, serverIsAdministrator: true).GetOwner(typeof(SecurityIdentifier)));
            Assert.Equal(system, ExternalControlService.BuildSecurity(user, system, serverIsAdministrator: true).GetOwner(typeof(SecurityIdentifier)));
            Assert.Null(ExternalControlService.BuildSecurity(user, user, serverIsAdministrator: false).GetOwner(typeof(SecurityIdentifier)));
        }

        [Fact]
        public void TheOwnerTheServerNames_IsTheOwnerAPipeCarries_Live()
        {
            string pipe = "PadForge.Tests.OwnerSeam." + Guid.NewGuid().ToString("N");
            var me = WindowsIdentity.GetCurrent().User;
            ExternalControlService.OwnerSeam = () => me;
            try
            {
                using (var service = new ExternalControlService(_ => "ok", pipe))
                {
                    Assert.Equal(ExternalControlService.PipeState.Serving, service.Start());
                    using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                    client.Connect(5000);
                    Assert.Equal(me.Value, PipeOwner.Of(client.SafePipeHandle));
                }
                // A token without rights cannot name Administrators: the
                // server's code is refused its pipe, as a squatter would be.
                ExternalControlService.OwnerSeam = () => new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                using (var refused = new ExternalControlService(_ => "ok", pipe + ".admins"))
                    Assert.NotEqual(ExternalControlService.PipeState.Serving, refused.Start());
            }
            finally
            {
                ExternalControlService.OwnerSeam = null;
            }
        }

        [Fact]
        public void TheControlPipeClient_TakesATestsOwnPipe_AndReadsItsAnswer()
        {
            string pipe = "PadForge.Tests.Client." + Guid.NewGuid().ToString("N");
            using var service = new ExternalControlService(line => "ok " + line, pipe);
            Assert.Equal(ExternalControlService.PipeState.Serving, service.Start());
            Assert.Equal("ok query", ControlPipeClient.Exchange(pipe, "query", 5000));
        }

        [Fact]
        public void APipeServerThatIsNotPadForge_IsNoPadForge_AndTheCallerIsTold()
        {
            var said = new List<string>();
            int shown = 0;
            var seams = new ClientRun.Seams
            {
                CopyRuns = () => true,
                Exchange = _ => ControlPipeClient.NotPadForge,
                PipeStands = () => true,
                Settle = () => RunningCopy.Settled.Ready,
                ShowWindow = () => { shown++; return true; },
                LendForeground = () => { },
                Watch = () => null,
                Say = said.Add,
                SayError = said.Add,
                Tell = said.Add,
                TellError = said.Add,
            };
            Assert.True(ClientRun.TryRun(new[] { "--state" }, out int code, seams));
            Assert.Equal(ClientRun.NotRunning, code);
            Assert.Equal(ClientRun.NotPadForgeText, Assert.Single(said));
            // Showing the window needs no pipe.
            said.Clear();
            Assert.True(ClientRun.TryRun(new[] { "--show" }, out code, seams));
            Assert.Equal(ClientRun.Done, code);
            Assert.Equal(1, shown);
        }

        [Fact]
        public void TheServer_ReadsTheToken_OfAClientThatOpensAtTheIdentificationLevel()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            using var client = new AutomationTestClient(rig.PipeName, TokenImpersonationLevel.Identification);
            Assert.StartsWith("ok padforge=", client.Final("hello name=\"Identified\" kind=script"));
            Assert.StartsWith("ok ", client.Final("version"));
        }

        [Fact]
        public void RunningCopy_PassesOverAWindowOfItsNameWithoutRights_Live() => OnSta(() =>
        {
            string tag = "PadForge.Tests.Squat." + Guid.NewGuid().ToString("N");
            using var squatter = new InstanceSignal(tag + ".Instance", tag + ".Ready", tag + ".Show");
            Assert.True(squatter.Open());
            // This process runs without rights: its window is not the
            // running copy's.
            Assert.Equal(IntPtr.Zero, RunningCopy.FindChecked(tag + ".Instance"));
            RunningCopy.IntegritySeam = pid => pid == (uint)Environment.ProcessId ? 0x3000 : null;
            try
            {
                Assert.Equal(squatter.SignalWindow, RunningCopy.FindChecked(tag + ".Instance"));
            }
            finally
            {
                RunningCopy.IntegritySeam = null;
            }
            Assert.True(RunningCopy.RunsWithRights((uint)Environment.ProcessId) == false);
        });

        [Fact]
        public void RunningCopy_ReadsNoLock_ItHasNoRightsFor()
        {
            string source = OffLimitsSource.Read("PadForge.App/Services/RunningCopy.cs");
            Assert.DoesNotContain("Mutex.TryOpenExisting", source);
            Assert.Contains("private static bool LockIsHeld() => InstanceLock.IsHeld();", source);
            string show = FenceSource.Body(source, "internal static bool Show(int waitMs = 10000)");
            Assert.Contains("WaitForSignalWindow(waitMs)", show);
            Assert.Contains("FindChecked(windowName)", FenceSource.Body(source, "private static IntPtr Find(string windowName)"));
        }

        [Fact]
        public void TheInstanceLock_CannotBeTakenOrReadWithoutRights_Live()
        {
            string ns = "PadForge.Tests.Lock." + Guid.NewGuid().ToString("N");
            Assert.False(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
            using (var taken = InstanceLock.Take(out bool held, ns))
            {
                Assert.Null(taken);
                Assert.False(held);
            }
            Assert.False(InstanceLock.IsHeld(ns));
            // The app's own names are the test host's never.
            Assert.Null(InstanceLock.Take(out _));
            Assert.Contains("TakeSingleInstanceLock(out bool isNewInstance)", OffLimitsSource.Read("PadForge.App/App.xaml.cs"));
        }

        // ── 4. Sessions ──

        [Fact]
        public void WhileAutomationIsOff_AScriptAndAnAssistantAreRefused_AndAPluginIsServed()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            rig.Host.AcceptPluginToken = (token, pid) => token == "right" ? new AutomationPluginGrant("Bridge") : null;
            rig.Host.McpSession = (peer, stream, stopping) => Task.CompletedTask;
            rig.Host.ApplyPlugins(true);
            rig.Host.Apply(false, AutomationLevel.SendInputs);
            Assert.Equal(ExternalControlService.PipeState.Serving, rig.Host.AutomationPipeState);
            using var script = rig.Connect();
            Assert.StartsWith("err not-allowed ", script.Final("hello name=s kind=script"));
            Assert.True(script.ClosedWithin(3000));
            using var assistant = rig.Connect();
            Assert.StartsWith("err not-allowed ", assistant.Final("hello name=a kind=mcp"));
            using var plugin = rig.Connect();
            Assert.StartsWith("ok padforge=", plugin.Final("hello name=p kind=plugin token=right"));
            rig.Host.ApplyPlugins(false);
        }

        [Fact]
        public void OneProcess_HoldsFourSessions()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            for (int i = 0; i < AutomationSessions.MostPerProcess; i++) rig.Open("Same " + i);
            using var fifth = rig.Connect();
            Assert.StartsWith("err too-many ", fifth.Final("hello name=fifth"));
        }

        [Fact]
        public void FourSessions_AreKeptForPlugins()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            rig.EachConnectionItsOwnProcess();
            rig.Host.AcceptPluginToken = (token, pid) => new AutomationPluginGrant("Bridge " + pid);
            for (int i = 0; i < AutomationSessions.MostSessions - AutomationSessions.KeptForPlugins; i++) rig.Open("Script " + i);
            using var script = rig.Connect();
            Assert.StartsWith("err too-many ", script.Final("hello name=more"));
            var plugins = new List<AutomationTestClient>();
            for (int i = 0; i < AutomationSessions.KeptForPlugins; i++)
            {
                var plugin = rig.Connect();
                plugins.Add(plugin);
                Assert.StartsWith("ok padforge=", plugin.Final("hello name=p kind=plugin token=t"));
            }
            using var last = rig.Connect();
            Assert.StartsWith("err too-many ", last.Final("hello name=p kind=plugin token=t"));
            foreach (var plugin in plugins) plugin.Dispose();
        }

        [Fact]
        public async Task AtMost32ConnectionsWaitForTheirHello_TheOldestLetGo()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var silent = new List<AutomationTestClient>();
            for (int i = 0; i < AutomationSessions.MostWaitingForHello; i++) silent.Add(rig.Connect());
            await AutomationPipeRig.Until(() => rig.Host.Sessions.Waiting == AutomationSessions.MostWaitingForHello);
            using var one = rig.Connect();
            Assert.True(silent[0].ClosedWithin(3000), "the oldest silent connection was let go");
            await AutomationPipeRig.Until(() => rig.Host.Sessions.Waiting == AutomationSessions.MostWaitingForHello);
            Assert.StartsWith("ok padforge=", one.Final("hello name=late"));
            foreach (var client in silent) client.Dispose();
        }

        // ── 6. Names shown to the user ──

        [Fact]
        public void ShownText_TakesOutOverridesZeroWidthMarksAndControls()
        {
            Assert.Equal("gpj.exe", ShownText.Clean("\u202Egpj.exe"));
            Assert.Equal("steam.exe", ShownText.Clean("st\u200Bea\u200Dm\uFEFF.exe"));
            Assert.Equal("ab", ShownText.Clean("a\u2066\u2069\u200E\u200F\u061C\u00ADb"));
            Assert.Equal("a b", ShownText.Clean("a\u2028b"));
            Assert.Equal("ab", ShownText.Clean("a\r\n\0b"));
            Assert.Same("plain", ShownText.Clean("plain"));
            // Whole code points are read: tag characters, outside the Basic
            // Multilingual Plane, go, a lone surrogate goes, and a letter
            // outside that plane stays.
            Assert.Equal("ab", ShownText.Clean("a\U000E0041\U000E007Fb"));
            Assert.Equal("ab", ShownText.Clean("a\uD800b\uDC00"));
            Assert.Equal("a\U0001D400b", ShownText.Clean("a\U0001D400​b"));
            Assert.Null(ShownText.Clean(null));
        }

        [Fact]
        public void AProgramsNames_ReachTheUserCleaned_InTheHelloTheDeviceAndActivity()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open("\u202Eexe.maets");
            Assert.Equal("exe.maets", rig.Host.Clients.Snapshot().Single().Name);
            string id = client.Final("declare key=k name=\"Pad\u200B\u202E\" {}").Substring(6);
            Assert.Equal("Pad", rig.Host.Sessions.Devices.Find(id).Device.Name);
            var item = new AutomationActivityItem(new AutomationActivityEntry(DateTimeOffset.Now,
                new AutomationProgram("x\u202Ey", 1, "a\u200Bb.exe", @"C:\Temp\a" + "\u202E" + "b.exe"),
                new AutomationAsk(AutomationAskKind.ActivateProfile, "Rac\u202Eing"), "ok", true), null);
            Assert.Equal("xy", item.Program);
            Assert.DoesNotContain('\u202E', item.ProgramImage);
            Assert.DoesNotContain('\u202E', item.Asked);
            Assert.Contains("Racing", item.Asked);
        }

        [Fact]
        public void ActivityAndTheCard_ShowTheImagesFullPath_AndItsSigner()
        {
            var s = Strings.Instance;
            var signed = new AutomationProgram("steam", 5, "steam.exe", @"C:\Users\me\AppData\Local\Temp\steam.exe", "Valve Corp.");
            var item = new AutomationActivityItem(new AutomationActivityEntry(DateTimeOffset.Now, signed,
                new AutomationAsk(AutomationAskKind.Version), "ok", true), null);
            Assert.Equal(string.Format(s.Automation_Program_Signed_Format, @"C:\Users\me\AppData\Local\Temp\steam.exe", "Valve Corp."), item.ProgramImage);
            Assert.True(item.HasProgramImage);
            Assert.Contains(@"\Temp\steam.exe", AutomationRequestItem.FromLine(signed));
            var unsigned = signed with { Signer = null };
            Assert.Equal(string.Format(s.Automation_Program_NotSigned_Format, @"C:\Users\me\AppData\Local\Temp\steam.exe"),
                AutomationRequestItem.ImageLine(unsigned));
            Assert.False(new AutomationActivityItem(new AutomationActivityEntry(DateTimeOffset.Now, AutomationProgram.Unknown,
                new AutomationAsk(AutomationAskKind.Version), "ok", true), null).HasProgramImage);
        }

        [Fact]
        public void TheSigner_IsReadForAWindowsFile_AndNoneForAFileWithNoSignature_Live()
        {
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            string signer = AuthenticodeSigner.Of(notepad);
            Assert.False(string.IsNullOrEmpty(signer));
            Assert.Contains("Microsoft", signer);
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Signer." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string plain = Path.Combine(dir, "plain.exe");
                File.WriteAllBytes(plain, new byte[] { (byte)'M', (byte)'Z', 0, 0 });
                Assert.Null(AuthenticodeSigner.Of(plain));

                // The test host carries an embedded signature, which reads as
                // Windows' own Get-AuthenticodeSignature reads it.
                string host = Path.Combine(AppContext.BaseDirectory, "testhost.exe");
                Assert.Equal("Microsoft Corporation", AuthenticodeSigner.Of(host));
                // A copy with one byte of its code changed holds no signature.
                byte[] bytes = File.ReadAllBytes(host);
                bytes[bytes.Length / 2] ^= 0xFF;
                string changed = Path.Combine(dir, "changed.exe");
                File.WriteAllBytes(changed, bytes);
                Assert.Null(AuthenticodeSigner.Of(changed));
            }
            finally { Directory.Delete(dir, true); }
            Assert.Null(AuthenticodeSigner.Of(null));
            // A file no signature format covers is still found in a catalog:
            // Windows' own PowerShell modules are catalog-signed scripts.
            string module = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\Modules\Microsoft.PowerShell.Archive\Microsoft.PowerShell.Archive.psm1");
            Assert.True(File.Exists(module), module);
            Assert.Equal("Microsoft Windows", AuthenticodeSigner.Of(module));
            Assert.Equal("Microsoft Windows", AuthenticodeSigner.Of(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")));
        }

        [Fact]
        public void AControlPipeCaller_IsNamedByItsImagesFullPath()
        {
            var me = AutomationProgram.FromProcessId(Environment.ProcessId);
            Assert.Equal(Environment.ProcessPath, me.ImagePath, ignoreCase: true);
        }

        // ── 7. Program devices ──

        [Fact]
        public void AProgramsDevicesMadeInOneRun_AreLimited_AndOneDeclaredAgainCountsOnce()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            rig.EachConnectionItsOwnProcess();
            using var a = rig.Open("Maker A");
            for (int i = 0; i < ProgramDevices.MostMadePerProgram; i++)
            {
                string id = a.Final("declare key=k" + i + " name=\"D\" {}");
                Assert.StartsWith("ok id=", id);
                Assert.Equal("ok", a.Final("gone " + id.Substring(6)));
            }
            Assert.StartsWith("err too-many ", a.Final("declare key=new name=\"D\" {}"));
            // The same key is the same device.
            Assert.StartsWith("ok id=", a.Final("declare key=k0 name=\"D\" {}"));
            // Nothing a program never had assigned piles up.
            Assert.InRange(rig.Host.Sessions.Devices.Held, 0, 1);
            lock (SettingsManager.UserDevices.SyncRoot) Assert.InRange(SettingsManager.UserDevices.Items.Count, 0, 1);

            var others = Enumerable.Range(0, 3).Select(i => rig.Open("Maker " + i)).ToList();
            foreach (var other in others)
                for (int i = 0; i < ProgramDevices.MostMadePerProgram; i++)
                {
                    string id = other.Final("declare key=k" + i + " name=\"D\" {}");
                    Assert.StartsWith("ok id=", id);
                    other.Final("gone " + id.Substring(6));
                }
            Assert.Equal(ProgramDevices.MostMadeInAll, rig.Host.Sessions.Devices.Made);
            using var late = rig.Open("Maker Late");
            Assert.StartsWith("err too-many ", late.Final("declare key=k0 name=\"D\" {}"));
            foreach (var other in others) other.Dispose();
        }

        [Fact]
        public void ADeviceNobodyAssigned_GoesWithItsRow_WhenItsWaitRunsOut()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            int forgotten = 0;
            rig.Host.Sessions.Devices.Forgotten += () => Interlocked.Increment(ref forgotten);
            var client = rig.Open();
            string id = client.Final("declare key=k name=\"D\" {}").Substring(6);
            var guid = rig.Host.Sessions.Devices.Find(id).Guid;
            Assert.NotNull(SettingsManager.FindDeviceByInstanceGuid(guid));
            Assert.Equal("ok bye", client.Final("bye"));
            client.Dispose();
            Assert.True(SpinWait.SpinUntil(() => rig.Host.Sessions.Devices.Find(id) == null, 5000));
            Assert.Null(SettingsManager.FindDeviceByInstanceGuid(guid));
            Assert.Equal(1, forgotten);
        }

        [Fact]
        public void AtTheLoad_ARowOfAProgramsDeviceNobodyAssigned_IsDropped()
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var left = new UserDevice { InstanceGuid = Guid.NewGuid(), DevicePath = "program://abcdefgh" };
                var kept = new UserDevice { InstanceGuid = Guid.NewGuid(), DevicePath = "program://ijklmnop" };
                var pad = new UserDevice { InstanceGuid = Guid.NewGuid(), DevicePath = @"\\?\hid#vid_045e" };
                SettingsManager.UserDevices.Items.AddRange(new[] { left, kept, pad });
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = kept.InstanceGuid, MapTo = 0 });
                Assert.Equal(1, SettingsService.DropUnassignedProgramDevices());
                Assert.Equal(new[] { kept, pad }, SettingsManager.UserDevices.Items);
            }
            finally
            {
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
            }
        }

        [Fact]
        public void TheDeviceListsWalk_IsOneAtATime_HoweverManyChangesCome()
        {
            string body = FenceSource.Body(OffLimitsSource.Read("PadForge.App/Services/InputService.cs"),
                "private void OnDevicesUpdated(object sender, EventArgs e)");
            int queued = body.IndexOf("if (System.Threading.Interlocked.Exchange(ref _devicesWalkQueued, 1) != 0) return;", StringComparison.Ordinal);
            int invoke = body.IndexOf("_dispatcher.BeginInvoke(", StringComparison.Ordinal);
            int cleared = body.IndexOf("System.Threading.Volatile.Write(ref _devicesWalkQueued, 0);", StringComparison.Ordinal);
            Assert.True(queued >= 0 && invoke > queued && cleared > invoke);
        }

        // ── 8. The level, read again where it is applied ──

        [Fact]
        public void ALevelThatDrops_BetweenTheCheckAndTheHold_LeavesNoHold()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            using var client = rig.Open();
            DropOnce(rig);
            Assert.StartsWith("err not-allowed ", client.Final("own " + AutomationPipeRig.IdOf(pad) + " b2=1"));
            Assert.Null(ControlHolds.Current);
            Assert.Equal(0, rig.Host.Sessions.HoldsOf(rig.Host.Sessions.Snapshot().Single()));
        }

        [Fact]
        public void ALevelThatDrops_BetweenTheCheckAndTheValue_LeavesNoValue()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open();
            DropOnce(rig);
            Assert.StartsWith("err not-allowed ", client.Final("var lap=3"));
            Assert.Equal(0f, ScriptValues.Read("lap"));
            Assert.False(ScriptValues.AnySet);
        }

        [Fact]
        public void ALevelThatDrops_BetweenTheCheckAndTheDevice_LeavesNoDevice()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open();
            DropOnce(rig);
            Assert.StartsWith("err not-allowed ", client.Final("declare key=k name=\"D\" {}"));
            Assert.Equal(0, rig.Host.Sessions.Devices.Live);
        }

        [Fact]
        public void ALevelThatDrops_BetweenTheCheckAndTheChange_LeavesTheDeviceAtRest()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open();
            string id = client.Final("declare key=k name=\"D\" {\"buttons\":[\"A\"]}").Substring(6);
            var device = rig.Host.Sessions.Devices.Find(id).Device;
            DropOnce(rig);
            Assert.StartsWith("@1 err not-allowed ", client.Final("@1 s " + id + " b0=1"));
            Assert.False(device.PeekState().Buttons[0]);
        }

        [Fact]
        public void ALevelThatDropsBelowRead_BetweenTheCheckAndTheFollow_LeavesNothingFollowed()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            using var client = rig.Open();
            DropOnce(rig, AutomationLevel.Commands);
            Assert.StartsWith("err not-allowed ", client.Final("sub " + AutomationPipeRig.IdOf(pad) + " buttons"));
            DropOnce(rig, AutomationLevel.Commands);
            Assert.StartsWith("err not-allowed ", client.Final("sub devices"));
            Assert.False(rig.Host.Sessions.Watch.Hooked);
        }

        private static void DropOnce(AutomationPipeRig rig, AutomationLevel to = AutomationLevel.Read)
        {
            int once = 0;
            rig.Host.Sessions.AfterTheLevelCheck = () =>
            {
                if (Interlocked.Exchange(ref once, 1) == 0) rig.Host.Apply(true, to);
            };
        }

        // ── 9. The show flood ──

        [Fact]
        public void AShow_WithinTwoSecondsOfTheLast_IsIgnored()
        {
            long now = 10_000;
            var show = new CallerShow(() => now);
            Assert.True(show.Take());
            now += CallerShow.GapMs - 1;
            Assert.False(show.Take());
            now += 1;
            Assert.True(show.Take());
        }

        [Fact]
        public void AShow_TakesTheFrontWithTheLentRight_OrFlashesTheTaskbarButton()
        {
            IntPtr front = (IntPtr)5, mine = (IntPtr)9;
            int flashed = 0;
            bool lent = false;
            CallerShow.ForegroundSeam = () => front;
            CallerShow.SetForegroundSeam = hwnd => { if (lent) front = hwnd; return lent; };
            CallerShow.FlashSeam = _ => flashed++;
            try
            {
                Assert.False(CallerShow.ComeForward(mine));
                Assert.Equal(1, flashed);
                lent = true;
                Assert.True(CallerShow.ComeForward(mine));
                Assert.Equal(1, flashed);
            }
            finally
            {
                CallerShow.ForegroundSeam = null;
                CallerShow.SetForegroundSeam = null;
                CallerShow.FlashSeam = null;
            }
            string show = FenceSource.Body(OffLimitsSource.Read("PadForge.App/MainWindow.Automation.cs"), "internal void ShowForACaller()");
            Assert.Contains("if (!_callerShow.Take()) return;", show);
            Assert.DoesNotContain("ForceToForeground", show);
            Assert.DoesNotContain("Topmost", show);
        }

        // ── 11. The control pipe's token checks ──

        [Fact]
        public void TheControlPipe_RefusesAClientTheTokenChecksRefuse_AndActivitySaysSo()
        {
            string pipe = "PadForge.Tests.ControlToken." + Guid.NewGuid().ToString("N");
            var target = new RecordingAutomationTarget();
            using var host = new AutomationHost(target, new InlineAutomationUi(), pipe,
                programOf: pid => new AutomationProgram("caller", pid, "caller.exe"));
            var real = host.ControlService.ReadToken;
            host.ControlService.ReadToken = stream =>
                real(stream) is { } t ? t with { Integrity = 0x1000 } : null;
            host.Apply(true, AutomationLevel.Commands);
            Assert.Equal("error refused", ControlPipeClient.Exchange(pipe, "query", 5000));
            Assert.Empty(target.Calls);
            var entry = host.Activity.Snapshot().Last();
            Assert.False(entry.Succeeded);
            Assert.Equal(Strings.Instance.Automation_Result_Refused, AutomationActivityItem.RefusalWords(entry.Result));

            // The same client at its real level is taken.
            host.ControlService.ReadToken = real;
            Assert.Equal("ok default unpinned", ControlPipeClient.Exchange(pipe, "query", 5000));
        }

        [Fact]
        public void TheControlPipesJudge_IsTheAutomationPipes()
        {
            var service = new ExternalControlService(_ => "ok", "PadForge.Tests.Judge." + Guid.NewGuid().ToString("N"));
            service.ReadToken = _ => null;
            Assert.Equal("no-token", service.Judge(null));
            string serve = FenceSource.Body(OffLimitsSource.Read("PadForge.App/Services/ExternalControlService.cs"),
                "private async Task ServeAsync(NamedPipeServerStream server, CancellationToken token)");
            Assert.True(serve.IndexOf("ReadLineAsync", StringComparison.Ordinal) < serve.IndexOf("Judge(server)", StringComparison.Ordinal));
        }

        // ── 12. Script value names ──

        [Fact]
        public void AValueName_IsFreedWhenTheLastSessionThatSetItEnds()
        {
            ScriptValues.ResetForTest();
            var removed = new List<string>();
            Action<string> onRemoved = removed.Add;
            ScriptValues.NameRemoved += onRemoved;
            try
            {
                for (int i = 0; i < ScriptValues.MostNames; i++) Assert.True(ScriptValues.Set("n" + i, i, 1));
                Assert.False(ScriptValues.Set("other", 1, 2));
                Assert.True(ScriptValues.Set("n0", 5, 2));          // a name another session holds too
                ScriptValues.ReleaseOwner(1);
                Assert.Equal(new[] { "n0" }, ScriptValues.Names());
                Assert.Equal(ScriptValues.MostNames - 1, removed.Count);
                Assert.Equal(5f, ScriptValues.Read("n0"));
                Assert.True(ScriptValues.Set("other", 1, 3));
                ScriptValues.ReleaseOwner(2);
                ScriptValues.ReleaseOwner(3);
                Assert.Empty(ScriptValues.Names());
                Assert.False(ScriptValues.AnySet);
            }
            finally
            {
                ScriptValues.NameRemoved -= onRemoved;
                ScriptValues.ResetForTest();
            }
        }

        // ── 13. Snapshots and recordings for programs ──

        [Fact]
        public void AProgramsSnapshots_AreOneAMinute_TwentyARun_AndScrubbed()
        {
            var target = new RecordingAutomationTarget();
            using var host = new AutomationHost(target, new InlineAutomationUi(), "PadForge.Tests.Snap." + Guid.NewGuid().ToString("N"));
            host.Apply(true, AutomationLevel.Commands);
            long now = 1_000_000;
            host.Commands.NowMs = () => now;
            var stop = CancellationToken.None;
            Assert.StartsWith("ok ", host.Commands.Answer("snapshot", stop).Line);
            Assert.True(target.SnapshotScrubbed);
            Assert.Equal("error too-soon", host.Commands.Answer("snapshot", stop).Line);
            for (int i = 1; i < AutomationCommands.MostFromProgramsPerRun; i++)
            {
                now += AutomationCommands.SnapshotEveryMs;
                Assert.StartsWith("ok ", host.Commands.Answer("snapshot", stop).Line);
            }
            now += AutomationCommands.SnapshotEveryMs;
            Assert.Equal("error limit-reached", host.Commands.Answer("snapshot", stop).Line);
            // The user's own start is not a program, and its file is the
            // Settings button's.
            Assert.StartsWith("ok ", host.Commands.RunOwn("snapshot"));
            Assert.False(target.SnapshotScrubbed);
            Assert.Equal(Strings.Instance.Automation_Result_TooSoon, AutomationActivityItem.RefusalWords("error too-soon"));
            Assert.Equal(Strings.Instance.Automation_Result_LimitReached, AutomationActivityItem.RefusalWords("err limit-reached x"));
        }

        [Fact]
        public void AProgramsRecordingStarts_AreOneInTenSeconds_TwentyARun()
        {
            var target = new RecordingAutomationTarget();
            using var host = new AutomationHost(target, new InlineAutomationUi(), "PadForge.Tests.Rec." + Guid.NewGuid().ToString("N"));
            host.Apply(true, AutomationLevel.Commands);
            bool recording = false;
            int made = 0;
            host.RecordStart = () =>
            {
                if (recording) return AutomationRecordAnswer.Busy;
                recording = true;
                return AutomationRecordAnswer.Done("r" + ++made + ".zip");
            };
            host.Recording = () => recording;
            long now = 1_000_000;
            host.Commands.NowMs = () => now;
            var stop = CancellationToken.None;
            Assert.Equal("ok r1.zip", host.Commands.Answer("record start", stop).Line);
            // A start while it records is the recorder's busy.
            Assert.Equal("error busy", host.Commands.Answer("record start", stop).Line);
            recording = false;
            Assert.Equal("error too-soon", host.Commands.Answer("record start", stop).Line);
            for (int i = 1; i < AutomationCommands.MostFromProgramsPerRun; i++)
            {
                now += AutomationCommands.RecordStartEveryMs;
                recording = false;
                Assert.StartsWith("ok r", host.Commands.Answer("record start", stop).Line);
            }
            now += AutomationCommands.RecordStartEveryMs;
            recording = false;
            Assert.Equal("error limit-reached", host.Commands.Answer("record start", stop).Line);
            Assert.Equal(AutomationCommands.MostFromProgramsPerRun, made);
        }

        [Fact]
        public void ASnapshotForAProgram_GoesThroughTheScrubber()
        {
            var scrubber = new PrivacyScrubber(new PrivacyEnvironment { UserName = "plantuser", PcName = "PLANTPC" });
            scrubber.Know(PrivateKind.SerialNumber, "SN-PLANTED-0042");
            AutomationReadScrub.Seam = () => scrubber;
            try
            {
                var scrub = AutomationReadScrub.FileScrub();
                string text = scrub("open pad SN-PLANTED-0042 for plantuser\r\nPC PLANTPC ready");
                Assert.DoesNotContain("SN-PLANTED-0042", text);
                Assert.DoesNotContain("plantuser", text);
                Assert.DoesNotContain("PLANTPC", text);
                Assert.Contains("ready", text);
            }
            finally { AutomationReadScrub.Seam = null; }
            Assert.Contains("SaveSnapshot(scrubbed ? AutomationReadScrub.FileScrub() : null)",
                OffLimitsSource.Read("PadForge.App/MainWindow.Automation.cs"));
        }

        // ── Device names in reads ──

        [Fact]
        public void AReadNamesADeviceByItsProductScrubbed_NeverTheNameTheUserGaveIt()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var scrubber = new PrivacyScrubber(new PrivacyEnvironment { UserName = "plantuser", PcName = "PLANTPC" });
            scrubber.Know(PrivateKind.SerialNumber, "SN-PLANTED-0042");
            AutomationReadScrub.Seam = () => scrubber;
            try
            {
                var pad = rig.AddDevice(InputDeviceType.Gamepad, "Pad SN-PLANTED-0042 of plantuser");
                pad.DisplayName = "Kitchen Pad";
                pad.InstanceName = "Bluetooth Pad of plantuser";
                using var client = rig.Open();
                var rows = client.Ask("devices");
                string row = rows.Single(r => r.Contains(AutomationPipeRig.IdOf(pad)));
                Assert.DoesNotContain("SN-PLANTED-0042", row);
                Assert.DoesNotContain("plantuser", row);
                Assert.DoesNotContain("Kitchen", row);
                Assert.DoesNotContain("Bluetooth", row);
                Assert.Contains("name=\"Pad ", row);
                var nameless = rig.AddDevice(InputDeviceType.Gamepad, "");
                nameless.InstanceName = "Bluetooth name";
                Assert.Contains("name=\"Device\"", client.Ask("devices").Single(r => r.Contains(AutomationPipeRig.IdOf(nameless))));
            }
            finally { AutomationReadScrub.Seam = null; }
        }

        [Fact]
        public void TheReadScrubber_KnowsEveryDevicesSerialAndTheNameTheUserGaveIt()
        {
            var env = new PrivacyEnvironment { UserName = "u", PcName = "p" };
            var device = new UserDevice { SerialNumber = "AA:BB:CC:DD:EE:FF", DisplayName = "Kitchen Pad", DevicePath = @"\\?\HID#VID_054C&PID_0CE6#7&1a2b3c4d&0&0000#{4d1e55b2}" };
            var live = AutomationReadScrub.LiveValues(env, new List<UserDevice> { device });
            Assert.Contains((PrivateKind.BluetoothAddress, "AA:BB:CC:DD:EE:FF"), live);
            Assert.Contains((PrivateKind.DeviceName, "Kitchen Pad"), live);
            Assert.Contains(live, v => v.Kind == PrivateKind.DeviceInstance);
        }

        // ── Approvals ──

        [Fact]
        public async Task AProgram_HasThreeQuestionsWaiting_AndAllOfThemTen()
        {
            var approvals = new AutomationApprovals(TimeSpan.FromMinutes(5));
            AutomationProgram P(int n) => new AutomationProgram("p" + n, n, "p" + n + ".exe", @"C:\p" + n + ".exe");
            ApprovalRequest Ask(int n) => new ApprovalRequest(P(n), "Change", Array.Empty<string>());
            var waiting = new List<Task<ApprovalAnswer>>();
            for (int i = 0; i < AutomationApprovals.MostPerProgram; i++) waiting.Add(approvals.AskAsync(Ask(1)));
            Assert.Equal(ApprovalAnswer.TooMany, await approvals.AskAsync(Ask(1)));
            for (int p = 2; approvals.Pending.Count < AutomationApprovals.MostInAll; p++) waiting.Add(approvals.AskAsync(Ask(p)));
            Assert.Equal(AutomationApprovals.MostInAll, approvals.Pending.Count);
            Assert.Equal(ApprovalAnswer.TooMany, await approvals.AskAsync(Ask(99)));
            // An answered question frees its place.
            Assert.True(approvals.Answer(approvals.Pending[0].Id, false));
            Assert.Equal(ApprovalAnswer.Denied, await waiting[0]);
            var again = approvals.AskAsync(Ask(1));
            Assert.Equal(AutomationApprovals.MostInAll, approvals.Pending.Count);
            foreach (var p in approvals.Pending) approvals.Answer(p.Id, false);
            Assert.Equal(ApprovalAnswer.Denied, await again);
        }

        // ── The Read level's line ──

        [Fact]
        public void TheReadLevelsLine_SaysAProgramSeesKeysTypedIntoAdministratorWindows()
        {
            string line = Strings.Instance.Extras_Automation_LevelLine_Read;
            Assert.Contains("every key pressed on every device", line);
            Assert.Contains("windows that run as administrator", line);
        }
    }
}
