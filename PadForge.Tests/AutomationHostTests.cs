using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Automation's host (#597) and what it owns: the control pipe with its
    /// access list and its hold on the name, the record of what programs
    /// asked, the questions that wait for the user, and the list of
    /// connected programs. Every pipe here has a name of the test's own.
    /// </summary>
    public class AutomationHostTests
    {
        private static string OwnPipe(string tag) => "PadForge.Tests.Host." + tag + "." + Guid.NewGuid().ToString("N");

        private static AutomationHost NewHost(out RecordingAutomationTarget target, string pipe = null,
            Func<int, AutomationProgram> programOf = null, Func<DateTimeOffset> now = null)
        {
            target = new RecordingAutomationTarget();
            return TestConsent.AllowAll(new AutomationHost(target, new InlineAutomationUi(), pipe ?? OwnPipe("host"), programOf, now));
        }

        private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.ElapsedMilliseconds < timeoutMs, "the wait ran out");
                await Task.Delay(20);
            }
        }

        // ── The switch and the level ──

        [Fact]
        public void TheHost_IsOffUntilTheSwitchSaysOn_AndServesWhileItIsOn()
        {
            string pipe = OwnPipe("switch");
            using var host = NewHost(out _, pipe);
            int changes = 0, turnedOff = 0;
            host.Changed += (_, _) => changes++;
            host.TurnedOff += (_, _) => turnedOff++;

            Assert.False(host.IsOn);
            Assert.Equal(AutomationLevel.Commands, host.Level);
            Assert.False(host.IsServing);
            Assert.Equal(ExternalControlService.PipeState.Stopped, host.ControlPipe);
            Assert.False(ControlPipeClient.Stands(pipe));

            host.Apply(true, AutomationLevel.Commands);
            Assert.True(host.IsOn);
            Assert.True(host.IsServing);
            Assert.Equal(1, changes);
            Assert.True(ControlPipeClient.Stands(pipe));
            Assert.Equal("ok default unpinned", ControlPipeClient.Exchange(pipe, "query", 5000));

            // The same again changes nothing and raises nothing.
            host.Apply(true, AutomationLevel.Commands);
            Assert.Equal(1, changes);

            // The level alone is a change, and the pipe stays up through it.
            host.Apply(true, AutomationLevel.ChangeSettings);
            Assert.Equal(AutomationLevel.ChangeSettings, host.Level);
            Assert.Equal(2, changes);
            Assert.True(host.IsServing);
            Assert.Equal(0, turnedOff);

            host.Apply(false, AutomationLevel.ChangeSettings);
            Assert.False(host.IsOn);
            Assert.False(host.IsServing);
            Assert.Equal(3, changes);
            Assert.Equal(1, turnedOff);
            Assert.False(ControlPipeClient.Stands(pipe));
            Assert.Null(ControlPipeClient.Exchange(pipe, "query", 200));

            // Off again raises nothing, and a second on serves again.
            host.Apply(false, AutomationLevel.ChangeSettings);
            Assert.Equal(1, turnedOff);
            host.Apply(true, AutomationLevel.Commands);
            Assert.True(host.IsServing);
            Assert.Equal("ok default unpinned", ControlPipeClient.Exchange(pipe, "query", 5000));
        }

        [Theory]
        [InlineData(false, AutomationLevel.ChangeSettings, AutomationLevel.Commands, false)]
        [InlineData(true, AutomationLevel.Commands, AutomationLevel.Commands, true)]
        [InlineData(true, AutomationLevel.Commands, AutomationLevel.Read, false)]
        [InlineData(true, AutomationLevel.Read, AutomationLevel.Commands, true)]
        [InlineData(true, AutomationLevel.Read, AutomationLevel.Read, true)]
        [InlineData(true, AutomationLevel.Read, AutomationLevel.SendInputs, false)]
        [InlineData(true, AutomationLevel.SendInputs, AutomationLevel.SendInputs, true)]
        [InlineData(true, AutomationLevel.SendInputs, AutomationLevel.ChangeSettings, false)]
        [InlineData(true, AutomationLevel.ChangeSettings, AutomationLevel.ChangeSettings, true)]
        public void ALevel_AllowsItselfAndTheOnesBelow_WhileAutomationIsOn(bool on, AutomationLevel inForce, AutomationLevel needed, bool allowed)
        {
            using var host = NewHost(out _);
            host.Apply(on, inForce);
            Assert.Equal(allowed, host.Allows(needed));
        }

        /// <summary>The level is saved by number, so each member keeps its
        /// number for good, and a number no level has reads as the
        /// lowest.</summary>
        [Fact]
        public void TheLevelsNumbers_ArePinned()
        {
            Assert.Equal(0, (int)AutomationLevel.Commands);
            Assert.Equal(1, (int)AutomationLevel.Read);
            Assert.Equal(2, (int)AutomationLevel.SendInputs);
            Assert.Equal(3, (int)AutomationLevel.ChangeSettings);
            Assert.Equal(new[] { 0, 1, 2, 3 }, Enum.GetValues<AutomationLevel>().Select(l => (int)l));
            Assert.Equal(default, AutomationLevel.Commands);

            Assert.Equal(AutomationLevel.Commands, AutomationLevels.FromSaved(0));
            Assert.Equal(AutomationLevel.Read, AutomationLevels.FromSaved(1));
            Assert.Equal(AutomationLevel.SendInputs, AutomationLevels.FromSaved(2));
            Assert.Equal(AutomationLevel.ChangeSettings, AutomationLevels.FromSaved(3));
            foreach (int stray in new[] { -1, 4, 99, int.MinValue, int.MaxValue })
                Assert.Equal(AutomationLevel.Commands, AutomationLevels.FromSaved(stray));
        }

        // ── The control pipe's access list ──

        private static readonly SecurityIdentifier Network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        private static readonly SecurityIdentifier Administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier LocalSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier Everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        private static readonly SecurityIdentifier AuthenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        private static List<(SecurityIdentifier Who, AccessControlType Kind, PipeAccessRights Rights)> Rules(PipeSecurity security)
            => security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()
                .Select(r => ((SecurityIdentifier)r.IdentityReference, r.AccessControlType, r.PipeAccessRights)).ToList();

        /// <summary>As PadForge builds it: the desktop's user reads and
        /// writes and can add no instance, administrators and the system
        /// have every right, and the network is refused.</summary>
        [Fact]
        public void TheAccessList_AdmitsTheDesktopsUser_AdministratorsAndTheSystem_AndRefusesTheNetwork()
        {
            var user = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001");
            var rules = Rules(ExternalControlService.BuildSecurity(user, user, serverIsAdministrator: true));

            Assert.Equal(4, rules.Count);
            var refused = Assert.Single(rules, r => r.Kind == AccessControlType.Deny);
            Assert.Equal(Network, refused.Who);
            // Every right but the one to wait on the handle, which a deny
            // rule never takes.
            Assert.Equal(PipeAccessRights.FullControl & ~PipeAccessRights.Synchronize, refused.Rights & ~PipeAccessRights.Synchronize);

            var desktop = Assert.Single(rules, r => r.Who == user);
            Assert.Equal(AccessControlType.Allow, desktop.Kind);
            Assert.Equal(PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, desktop.Rights);
            Assert.False(desktop.Rights.HasFlag(PipeAccessRights.CreateNewInstance));
            Assert.False(desktop.Rights.HasFlag(PipeAccessRights.ChangePermissions));
            Assert.False(desktop.Rights.HasFlag(PipeAccessRights.TakeOwnership));

            foreach (var whole in new[] { Administrators, LocalSystem })
            {
                var rule = Assert.Single(rules, r => r.Who == whole);
                Assert.Equal(AccessControlType.Allow, rule.Kind);
                Assert.Equal(PipeAccessRights.FullControl, rule.Rights);
                Assert.True(rule.Rights.HasFlag(PipeAccessRights.CreateNewInstance));
            }

            // Nobody else is named: not every account, as before #597.
            Assert.DoesNotContain(rules, r => r.Who == Everyone || r.Who == AuthenticatedUsers);
        }

        [Fact]
        public void TheAccessList_NamesTheDesktopsUser_WhenAnotherAccountRunsPadForge()
        {
            var desktop = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001");
            var other = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1002");
            var rules = Rules(ExternalControlService.BuildSecurity(desktop, other, serverIsAdministrator: true));
            Assert.Contains(rules, r => r.Who == desktop && r.Kind == AccessControlType.Allow);
            // The account PadForge runs as has its rights as an administrator.
            Assert.DoesNotContain(rules, r => r.Who == other);
        }

        /// <summary>A server without administrator rights, which PadForge
        /// never is and a test's server is, gets the right to make its own
        /// next instance. No rule gives it more.</summary>
        [Fact]
        public void AServerWithoutAdministratorRights_MayAddItsOwnInstance()
        {
            var user = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001");
            var rules = Rules(ExternalControlService.BuildSecurity(user, user, serverIsAdministrator: false));
            var own = Assert.Single(rules, r => r.Who == user);
            Assert.Equal(PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, own.Rights);
            Assert.Contains(rules, r => r.Who == Network && r.Kind == AccessControlType.Deny);

            string source = OffLimitsSource.Read("PadForge.App/Services/ExternalControlService.cs");
            Assert.Contains("if (!serverIsAdministrator && server != null)", source, StringComparison.Ordinal);
            Assert.Contains("bool administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);", source, StringComparison.Ordinal);
            // The desktop's user is the shell's, as the lighting state reads it.
            Assert.Contains("PadForge.Common.Input.Peripherals.WindowsLightingState.ShellUserSid();", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AuthenticatedUserSid", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorldSid", source, StringComparison.Ordinal);
        }

        /// <summary>The list as it stands on a pipe that serves, read back
        /// from Windows through a client's handle.</summary>
        [Fact]
        public async Task TheAccessList_OnAServingPipe_IsTheOneBuilt()
        {
            string pipe = OwnPipe("acl");
            using var service = new ExternalControlService(_ => "ok", pipe);
            Assert.Equal(ExternalControlService.PipeState.Serving, service.Start());

            List<(SecurityIdentifier Who, AccessControlType Kind, PipeAccessRights Rights)> rules;
            using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.None,
                       TokenImpersonationLevel.None, HandleInheritability.None))
            {
                await client.ConnectAsync(5000);
                rules = Rules(client.GetAccessControl());
            }

            using var me = WindowsIdentity.GetCurrent();
            Assert.Contains(rules, r => r.Who == Network && r.Kind == AccessControlType.Deny);
            Assert.Contains(rules, r => r.Who == Administrators && r.Kind == AccessControlType.Allow && r.Rights == PipeAccessRights.FullControl);
            Assert.Contains(rules, r => r.Who == LocalSystem && r.Kind == AccessControlType.Allow && r.Rights == PipeAccessRights.FullControl);
            Assert.Contains(rules, r => r.Who == me.User && r.Kind == AccessControlType.Allow && r.Rights.HasFlag(PipeAccessRights.ReadWrite));
            Assert.DoesNotContain(rules, r => r.Who == Everyone || r.Who == AuthenticatedUsers);
        }

        // ── The hold on the name ──

        /// <summary>A program that made the name first is found out: the
        /// service takes no second place on its pipe and serves
        /// nothing.</summary>
        [Fact]
        public void ANameAnotherProgramMadeFirst_IsRefused()
        {
            string pipe = OwnPipe("taken");
            using var squatter = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            int calls = 0;
            using var service = new ExternalControlService(_ => { calls++; return "ok"; }, pipe);
            Assert.Equal(ExternalControlService.PipeState.NameTaken, service.Start());
            Assert.Equal(ExternalControlService.PipeState.NameTaken, service.State);
            Assert.False(service.IsRunning);
            Assert.Equal(0, calls);

            // Once the other program lets go, the name is PadForge's to take.
            squatter.Dispose();
            Assert.Equal(ExternalControlService.PipeState.Serving, service.Start());
            Assert.Equal("ok", ControlPipeClient.Exchange(pipe, "query", 5000));
            Assert.Equal(1, calls);

            string source = OffLimitsSource.Read("PadForge.App/Services/ExternalControlService.cs");
            Assert.Contains("firstOfItsName ? PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance : PipeOptions.Asynchronous", source, StringComparison.Ordinal);
            Assert.Contains("first = CreateServer(_pipeName, firstOfItsName: true);", FenceSource.Body(source, "public PipeState Start()"), StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheHost_SaysWhoHoldsTheName_AndTakesItWhenItIsFree()
        {
            string pipe = OwnPipe("retry");
            var squatter = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var host = NewHost(out _, pipe);
            int changes = 0;
            host.Changed += (_, _) => Interlocked.Increment(ref changes);

            host.Apply(true, AutomationLevel.Commands);
            Assert.True(host.IsOn);
            Assert.False(host.IsServing);
            Assert.Equal(ExternalControlService.PipeState.NameTaken, host.ControlPipe);

            // Asked again while the other program still holds it: no change.
            int before = Volatile.Read(ref changes);
            host.RetryControlPipe();
            Assert.Equal(ExternalControlService.PipeState.NameTaken, host.ControlPipe);
            Assert.Equal(before, Volatile.Read(ref changes));

            squatter.Dispose();
            host.RetryControlPipe();
            Assert.True(host.IsServing);
            Assert.Equal(before + 1, Volatile.Read(ref changes));
            Assert.Equal("ok default unpinned", ControlPipeClient.Exchange(pipe, "query", 5000));

            // Turned off, the host asks for nothing.
            host.Apply(false, AutomationLevel.Commands);
            host.RetryControlPipe();
            Assert.False(host.IsServing);
            await Task.CompletedTask;
        }

        /// <summary>The name stays PadForge's between two commands: the next
        /// instance stands before a connection is served, so a client that
        /// comes while a command runs finds the pipe.</summary>
        [Fact]
        public async Task TheNameStands_WhileACommandIsServed()
        {
            string pipe = OwnPipe("stands");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var service = new ExternalControlService(line =>
            {
                if (line == "slow")
                {
                    entered.TrySetResult(true);
                    release.Task.Wait(10000);
                }
                return "ok " + line;
            }, pipe);
            service.Start();

            var slow = Task.Run(() => ControlPipeClient.Exchange(pipe, "slow", 5000));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The first command is still running, and the name is there.
            Assert.True(ControlPipeClient.Stands(pipe));
            var second = Task.Run(() => ControlPipeClient.Exchange(pipe, "second", 5000));
            await Task.Delay(100);
            Assert.False(second.IsCompleted);
            release.TrySetResult(true);
            Assert.Equal("ok slow", await slow.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("ok second", await second.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        /// <summary>A request is taken up to 1,024 bytes, and an answer can
        /// be longer. A line of exactly 1,024 bytes runs whole.</summary>
        [Fact]
        public async Task TheRequestIsCappedAt1024Bytes_AndTheAnswerIsNot()
        {
            string pipe = OwnPipe("cap");
            string seen = null;
            using var service = new ExternalControlService(line =>
            {
                seen = line;
                return "ok " + new string('y', 5000);
            }, pipe);
            service.Start();
            string fits = "activate " + new string('x', 1024 - "activate ".Length);
            string answer = await Task.Run(() => ControlPipeClient.Exchange(pipe, fits, 5000));
            Assert.Equal(1024, ExternalControlService.LongestRequest);
            Assert.Equal(fits, seen);
            Assert.Equal(5003, answer.Length);
        }

        /// <summary>A line past the cap is read to its end, runs nothing and
        /// is answered <c>error too-long</c>: its first 1,024 bytes are no
        /// line the program sent, and a profile's name cut there names no
        /// profile. The client that wrote it is not left in its write, and
        /// the next line is served.</summary>
        [Fact]
        public async Task ALinePastTheCap_IsReadToItsEnd_AndAnsweredTooLong()
        {
            string pipe = OwnPipe("drain");
            string seen = null;
            using var service = new ExternalControlService(line => { seen = line; return "ok"; }, pipe);
            service.Start();
            string big = "record mark " + new string('z', 40000);
            Assert.Equal("error too-long", await Task.Run(() => ControlPipeClient.Exchange(pipe, big, 5000)));
            Assert.Null(seen);
            Assert.Equal("error too-long", await Task.Run(() => ControlPipeClient.Exchange(pipe, "activate " + new string('x', 1100), 5000)));
            Assert.Null(seen);
            Assert.Equal("ok", await Task.Run(() => ControlPipeClient.Exchange(pipe, "query", 5000)));
            Assert.Equal("query", seen);
        }

        /// <summary>Through the host, a line past the cap reaches no
        /// command and Activity keeps it as refused too long.</summary>
        [Fact]
        public void ALinePastTheCap_ThroughTheHost_IsInActivityAsTooLong()
        {
            string pipe = OwnPipe("hostlong");
            using var host = NewHost(out var target, pipe);
            host.Apply(true, AutomationLevel.Commands);
            string name = string.Concat(Enumerable.Repeat("\u30ec\u30fc\u30b9", 120));
            Assert.Equal("error too-long", ControlPipeClient.Exchange(pipe, "activate " + name, 5000));
            Assert.Empty(target.Calls);
            var entry = Assert.Single(host.Activity.Snapshot());
            Assert.Equal(AutomationCommands.TooLong, entry.Result);
            Assert.False(entry.Succeeded);
        }

        /// <summary>One connection is served at a time, so a client that
        /// connects and says nothing is let go, and the next one is
        /// served.</summary>
        [Fact]
        public async Task AClientThatSaysNothing_DoesNotHoldThePipe()
        {
            string pipe = OwnPipe("silent");
            Assert.Equal(TimeSpan.FromSeconds(5), ExternalControlService.DefaultClientTimeout);
            using var service = new ExternalControlService(line => "ok " + line, pipe) { ClientTimeout = TimeSpan.FromMilliseconds(250) };
            service.Start();

            using var silent = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
            await silent.ConnectAsync(5000);
            // The silent client is first in line, and the next one is
            // answered once the first has had its time.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal("ok query", await Task.Run(() => ControlPipeClient.Exchange(pipe, "query", 5000)));
            Assert.True(clock.ElapsedMilliseconds < 4000, clock.ElapsedMilliseconds + " ms");
            Assert.Equal(ExternalControlService.PipeState.Serving, service.State);
        }

        /// <summary>The executor is told who sent a line and when the pipe
        /// stops, and what it leaves for after the answer runs once the
        /// answer is written.</summary>
        [Fact]
        public async Task TheExecutor_GetsTheCallersProcess_AndItsAfterWorkRunsOnceTheAnswerIsOut()
        {
            string pipe = OwnPipe("after");
            int caller = 0;
            var order = new List<string>();
            var after = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var service = new ExternalControlService(request =>
            {
                caller = request.ClientProcessId;
                Assert.False(request.Stopping.IsCancellationRequested);
                lock (order) order.Add("answered");
                return new ExternalControlService.Answer("ok", () =>
                {
                    lock (order) order.Add("after");
                    after.TrySetResult(true);
                });
            }, pipe);
            service.Start();

            string answer = await Task.Run(() => ControlPipeClient.Exchange(pipe, "exit", 5000));
            lock (order) order.Add("read");
            Assert.Equal("ok", answer);
            await after.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Environment.ProcessId, caller);
            lock (order) Assert.Equal("answered", order[0]);

            // Work that fails after an answer does not take the pipe down.
            using var failing = new ExternalControlService(_ => new ExternalControlService.Answer("ok", () => throw new InvalidOperationException("boom")), OwnPipe("fails"));
            failing.Start();
            Assert.True(failing.IsRunning);
        }

        [Fact]
        public async Task AFailingAfterWork_LeavesThePipeServing()
        {
            string pipe = OwnPipe("afterfail");
            using var service = new ExternalControlService(
                (ExternalControlService.Request _) => new ExternalControlService.Answer("ok", () => throw new InvalidOperationException("boom")), pipe);
            service.Start();
            Assert.Equal("ok", await Task.Run(() => ControlPipeClient.Exchange(pipe, "one", 5000)));
            Assert.Equal("ok", await Task.Run(() => ControlPipeClient.Exchange(pipe, "two", 5000)));
            Assert.Equal(ExternalControlService.PipeState.Serving, service.State);
        }

        /// <summary>Stop does not wait out a command that waits for the
        /// thread Stop runs on: the command is told the pipe is stopping and
        /// lets go.</summary>
        [Fact]
        public async Task Stop_ReleasesACommandThatWaits()
        {
            string pipe = OwnPipe("stop");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var service = new ExternalControlService(request =>
            {
                entered.TrySetResult(true);
                // As the command set waits for the interface's thread.
                request.Stopping.WaitHandle.WaitOne(20000);
                request.Stopping.ThrowIfCancellationRequested();
                return new ExternalControlService.Answer("ok");
            }, pipe);
            service.Start();
            var client = Task.Run(() => ControlPipeClient.Exchange(pipe, "engine on", 5000));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            service.Stop();
            Assert.True(clock.ElapsedMilliseconds < 1500, "Stop waited " + clock.ElapsedMilliseconds + " ms");
            Assert.Equal(ExternalControlService.PipeState.Stopped, service.State);
            Assert.False(service.IsRunning);
            Assert.Null(await client.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(ControlPipeClient.Stands(pipe));
        }

        // ── Activity ──

        [Fact]
        public void EveryControlPipeLine_MakesAnEntry()
        {
            string pipe = OwnPipe("journal");
            var times = new Queue<DateTimeOffset>(new[]
            {
                new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 23, 15, 0, 1, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 23, 15, 0, 2, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 23, 15, 0, 3, TimeSpan.Zero),
            });
            using var host = NewHost(out var target, pipe,
                pid => new AutomationProgram("launcher", pid, "launcher.exe"), () => times.Dequeue());
            int changes = 0;
            host.Activity.Changed += (_, _) => Interlocked.Increment(ref changes);
            host.Apply(true, AutomationLevel.Commands);

            Assert.Equal("ok Racing", ControlPipeClient.Exchange(pipe, "activate Racing", 5000));
            target.ActivateAnswer = "error unknown-profile";
            Assert.Equal("error unknown-profile", ControlPipeClient.Exchange(pipe, "activate Nope", 5000));
            Assert.StartsWith("ok {", ControlPipeClient.Exchange(pipe, "state", 5000), StringComparison.Ordinal);
            Assert.Equal("error unknown-command", ControlPipeClient.Exchange(pipe, "launch rockets", 5000));

            var entries = host.Activity.Snapshot();
            Assert.Equal(4, entries.Count);
            Assert.Equal(4, Volatile.Read(ref changes));

            Assert.Equal(new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero), entries[0].Time);
            Assert.Equal("launcher", entries[0].Program.Name);
            Assert.Equal(Environment.ProcessId, entries[0].Program.ProcessId);
            Assert.Equal("launcher.exe", entries[0].Program.ImageName);
            Assert.Equal(AutomationAskKind.ActivateProfile, entries[0].Asked.Kind);
            Assert.Equal("Racing", entries[0].Asked.Argument);
            Assert.Contains("Racing", entries[0].Asked.Text, StringComparison.Ordinal);
            Assert.Equal("ok Racing", entries[0].Result);
            Assert.True(entries[0].Succeeded);

            Assert.Equal("error unknown-profile", entries[1].Result);
            Assert.False(entries[1].Succeeded);

            // The state line is long and is kept as its ok.
            Assert.Equal(AutomationAskKind.State, entries[2].Asked.Kind);
            Assert.Equal("ok", entries[2].Result);
            Assert.True(entries[2].Succeeded);

            Assert.Equal(AutomationAskKind.Unknown, entries[3].Asked.Kind);
            Assert.Equal("launch rockets", entries[3].Asked.Argument);
            Assert.Equal("error unknown-command", entries[3].Result);
            Assert.False(entries[3].Succeeded);
        }

        [Fact]
        public void ALineRefusedWhileAutomationIsOff_IsStillOnTheRecord_AndAFailureIsInternal()
        {
            // A host that is on and whose command set fails.
            string pipe = OwnPipe("fails");
            using var host = NewHost(out var target, pipe, pid => AutomationProgram.Unknown);
            host.Apply(true, AutomationLevel.Commands);
            target.Throws = new InvalidOperationException("boom");
            Assert.Equal("error internal", ControlPipeClient.Exchange(pipe, "engine on", 5000));
            var entry = Assert.Single(host.Activity.Snapshot());
            Assert.Equal("error internal", entry.Result);
            Assert.False(entry.Succeeded);
            Assert.Same(AutomationProgram.Unknown, entry.Program);
        }

        /// <summary>A close over the signal window (#600 item 44) is on the
        /// record with Automation off, as the pipe's exit is, and its
        /// program is unknown: a posted message names no sender.</summary>
        [Fact]
        public void ACloseOverTheSignalWindow_IsOnTheRecord_WithItsProgramUnknown()
        {
            var at = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
            using var host = NewHost(out _, now: () => at);
            Assert.False(host.IsServing);
            host.NoteExitBySignal();
            var entry = Assert.Single(host.Activity.Snapshot());
            Assert.Same(AutomationProgram.Unknown, entry.Program);
            Assert.Equal(AutomationAskKind.Exit, entry.Asked.Kind);
            Assert.Equal(AutomationCommands.Ok, entry.Result);
            Assert.True(entry.Succeeded);
            Assert.Equal(at, entry.Time);
        }

        /// <summary>The record keeps every call as it came, oldest first, up to
        /// its capacity (#600). Past that the oldest go, whoever made them,
        /// and the record counts them. A snapshot is the caller's own, and
        /// Clear empties the record and its count.</summary>
        [Fact]
        public void TheRecord_KeepsEveryCall_AndPastItsCapacityTheOldestGo()
        {
            Assert.Equal(100_000, AutomationActivity.Capacity);
            var activity = new AutomationActivity(500);
            int changes = 0;
            activity.Changed += (_, _) => changes++;
            var start = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);
            AutomationProgram Program(int p) => new AutomationProgram("p" + p, 1000 + p, "p" + p + ".exe", @"C:\Tools\p" + p + ".exe");
            // Twelve programs read sixty devices each, one after another.
            for (int p = 0; p < 12; p++)
                for (int i = 0; i < 60; i++)
                    activity.Add(new AutomationActivityEntry(start.AddSeconds(p * 60 + i), Program(p),
                        new AutomationAsk(AutomationAskKind.Read, p + "." + i), "ok", true));
            Assert.Equal(720, changes);
            var kept = activity.Snapshot();
            Assert.Equal(500, kept.Count);
            Assert.Equal(220, activity.Dropped);
            // The newest 500, oldest first, each as it came.
            var expected = Enumerable.Range(0, 12).SelectMany(p => Enumerable.Range(0, 60).Select(i => p + "." + i)).Skip(220);
            Assert.Equal(expected, kept.Select(e => e.Asked.Argument));
            Assert.All(kept, e => Assert.Equal(1, e.Asked.Count));

            // A snapshot is the caller's own: a later call does not change it.
            var before = activity.Snapshot();
            activity.Add(new AutomationActivityEntry(start, Program(11), new AutomationAsk(AutomationAskKind.Exit), "ok", true));
            Assert.Equal(500, before.Count);
            Assert.Equal("11.59", before[^1].Asked.Argument);
            Assert.Equal(AutomationAskKind.Exit, activity.Snapshot()[^1].Asked.Kind);
            Assert.Equal(221, activity.Dropped);

            activity.Add(null);
            Assert.Equal(500, activity.Count);

            activity.Clear();
            Assert.Equal(0, activity.Count);
            Assert.Equal(0, activity.Dropped);
            Assert.Empty(activity.Snapshot());
        }

        /// <summary>A reader asks what came since its last look: the calls
        /// after its number, the number of the oldest still held, so it lets
        /// go of what the record dropped, and a new generation after
        /// Clear.</summary>
        [Fact]
        public void TheRecord_GivesAReaderWhatCameSinceItsLastLook()
        {
            var activity = new AutomationActivity(4);
            var who = new AutomationProgram("p", 1, "p.exe", @"C:\p.exe");
            AutomationActivityEntry Call(int n) => new AutomationActivityEntry(DateTimeOffset.Now, who, new AutomationAsk(AutomationAskKind.Read, n.ToString()), "ok", true);
            var first = activity.Since(-1, 0);
            Assert.Empty(first.New);
            for (int n = 1; n <= 3; n++) activity.Add(Call(n));
            var d = activity.Since(first.Generation, 0);
            Assert.Equal(new[] { "1", "2", "3" }, d.New.Select(e => e.Asked.Argument));
            Assert.Equal(3, d.Added);
            Assert.Equal(1, d.FirstKept);
            for (int n = 4; n <= 6; n++) activity.Add(Call(n));
            var e2 = activity.Since(d.Generation, d.Added);
            Assert.Equal(new[] { "4", "5", "6" }, e2.New.Select(e => e.Asked.Argument));
            Assert.Equal(3, e2.FirstKept);
            // A reader far behind gets only what is still held.
            var behind = activity.Since(d.Generation, 0);
            Assert.Equal(new[] { "3", "4", "5", "6" }, behind.New.Select(e => e.Asked.Argument));
            activity.Clear();
            activity.Add(Call(7));
            var cleared = activity.Since(d.Generation, e2.Added);
            Assert.NotEqual(d.Generation, cleared.Generation);
            Assert.Equal(new[] { "7" }, cleared.New.Select(e => e.Asked.Argument));
        }

        /// <summary>A flood of one program's calls takes the oldest out,
        /// whoever made them, and the count of what went says so. A
        /// refusal stays as each call came, so its count and what it named
        /// are on the record.</summary>
        [Fact]
        public void AFlood_OfRefusedLines_KeepsEachCall()
        {
            var activity = new AutomationActivity();
            var start = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);
            var who = new AutomationProgram("spam", 9, "spam.exe", @"C:\Tools\spam.exe");
            for (int i = 0; i < 4000; i++)
                activity.Add(new AutomationActivityEntry(start.AddMilliseconds(i), who,
                    new AutomationAsk(AutomationAskKind.Read, i % 2 == 0 ? "a" : "b"), "err not-allowed the Automation level does not allow this", false));
            var kept = activity.Snapshot();
            Assert.Equal(4000, kept.Count);
            Assert.Equal(0, activity.Dropped);
            Assert.Equal("b", kept[^1].Asked.Argument);
            Assert.Equal(start.AddMilliseconds(3999), kept[^1].Time);
        }

        [Fact]
        public async Task TheRecord_TakesCallsFromManyThreads()
        {
            var activity = new AutomationActivity();
            var entry = new AutomationActivityEntry(DateTimeOffset.Now, AutomationProgram.Unknown,
                new AutomationAsk(AutomationAskKind.QueryProfile), "ok", true);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    activity.Add(entry);
                    activity.Snapshot();
                }
            })));
            // Every call is on the record.
            Assert.Equal(1600, activity.Count);
        }

        [Fact]
        public void AProgram_IsNamedFromItsProcess_WithNoFolder()
        {
            var me = AutomationProgram.FromProcessId(Environment.ProcessId);
            Assert.Equal(Environment.ProcessId, me.ProcessId);
            Assert.False(string.IsNullOrEmpty(me.ImageName));
            Assert.Equal(Path.GetFileName(Environment.ProcessPath), me.ImageName);
            Assert.DoesNotContain('\\', me.ImageName);
            Assert.Equal(Path.GetFileNameWithoutExtension(Environment.ProcessPath), me.Name);

            Assert.Same(AutomationProgram.Unknown, AutomationProgram.FromProcessId(0));
            Assert.Same(AutomationProgram.Unknown, AutomationProgram.FromProcessId(-5));
            // A process that is gone keeps its number and has no name.
            var gone = AutomationProgram.FromProcessId(0x3FFFFFFC);
            Assert.Equal(0x3FFFFFFC, gone.ProcessId);
            Assert.Null(gone.ImageName);
            Assert.Null(gone.Name);
        }

        // ── Approvals ──

        private static ApprovalRequest Ask(string title = "Start a program")
            => new ApprovalRequest(new AutomationProgram("assistant", 4242, "assistant.exe"), title,
                new[] { @"C:\Games\launcher.exe --fullscreen" });

        [Fact]
        public async Task AQuestion_IsAllowed()
        {
            var approvals = new AutomationApprovals();
            Assert.Equal(TimeSpan.FromMinutes(2), AutomationApprovals.DefaultWait);
            int changes = 0;
            approvals.PendingChanged += (_, _) => Interlocked.Increment(ref changes);

            var asked = approvals.AskAsync(Ask());
            Assert.False(asked.IsCompleted);
            var pending = Assert.Single(approvals.Pending);
            Assert.Equal("Start a program", pending.Request.Title);
            Assert.Equal("assistant", pending.Request.Who.Name);
            Assert.Equal(new[] { @"C:\Games\launcher.exe --fullscreen" }, pending.Request.Detail);
            Assert.Equal(pending.Asked + TimeSpan.FromMinutes(2), pending.Expires);
            Assert.Equal(1, Volatile.Read(ref changes));

            Assert.True(approvals.Answer(pending.Id, allowed: true));
            Assert.Equal(ApprovalAnswer.Allowed, await asked.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Empty(approvals.Pending);
            Assert.Equal(2, Volatile.Read(ref changes));

            // An answer for a question that no longer waits is no answer.
            Assert.False(approvals.Answer(pending.Id, allowed: false));
            Assert.False(approvals.Answer(9999, allowed: true));
            Assert.Equal(2, Volatile.Read(ref changes));
        }

        [Fact]
        public async Task AQuestion_IsDenied()
        {
            var approvals = new AutomationApprovals();
            var asked = approvals.AskAsync(Ask());
            Assert.True(approvals.Answer(Assert.Single(approvals.Pending).Id, allowed: false));
            Assert.Equal(ApprovalAnswer.Denied, await asked.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Empty(approvals.Pending);
        }

        [Fact]
        public async Task AQuestionNobodyAnswers_TimesOut()
        {
            var approvals = new AutomationApprovals(TimeSpan.FromMilliseconds(120));
            int changes = 0;
            approvals.PendingChanged += (_, _) => Interlocked.Increment(ref changes);
            var asked = approvals.AskAsync(Ask());
            int id = Assert.Single(approvals.Pending).Id;
            Assert.Equal(ApprovalAnswer.TimedOut, await asked.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Empty(approvals.Pending);
            Assert.Equal(2, Volatile.Read(ref changes));
            // Too late to answer.
            Assert.False(approvals.Answer(id, allowed: true));
        }

        [Fact]
        public async Task AQuestionWhoseProgramWentAway_IsCanceled()
        {
            var approvals = new AutomationApprovals();
            using var gone = new CancellationTokenSource();
            var asked = approvals.AskAsync(Ask(), gone.Token);
            int id = Assert.Single(approvals.Pending).Id;
            gone.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(asked.IsCanceled);
            Assert.Empty(approvals.Pending);
            Assert.False(approvals.Answer(id, allowed: true));

            // A token canceled before the question is put asks nothing.
            var never = approvals.AskAsync(Ask(), gone.Token);
            Assert.True(never.IsCanceled);
            Assert.Empty(approvals.Pending);

            await Assert.ThrowsAsync<ArgumentNullException>(() => approvals.AskAsync(null));
        }

        [Fact]
        public async Task TwoQuestionsAtOnce_AreAnsweredEachByItsOwnNumber()
        {
            var approvals = new AutomationApprovals();
            var first = approvals.AskAsync(Ask("first"));
            var second = approvals.AskAsync(Ask("second"));
            var pending = approvals.Pending;
            Assert.Equal(new[] { "first", "second" }, pending.Select(p => p.Request.Title));
            Assert.NotEqual(pending[0].Id, pending[1].Id);

            // The second is answered first, and the first still waits.
            Assert.True(approvals.Answer(pending[1].Id, allowed: false));
            Assert.Equal(ApprovalAnswer.Denied, await second.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(first.IsCompleted);
            Assert.Equal("first", Assert.Single(approvals.Pending).Request.Title);

            Assert.True(approvals.Answer(pending[0].Id, allowed: true));
            Assert.Equal(ApprovalAnswer.Allowed, await first.WaitAsync(TimeSpan.FromSeconds(10)));

            // A number is not used again.
            var third = approvals.AskAsync(Ask("third"));
            int id = Assert.Single(approvals.Pending).Id;
            Assert.DoesNotContain(id, pending.Select(p => p.Id));
            approvals.Answer(id, allowed: true);
            await third.WaitAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>The answer comes back off the thread that gave it, so a
        /// click on the interface never runs a program's waiting call.</summary>
        [Fact]
        public async Task TheWaitingCall_GoesOnOffTheAnsweringThread()
        {
            var approvals = new AutomationApprovals();
            int answering = Environment.CurrentManagedThreadId;
            int resumed = 0;
            var asked = approvals.AskAsync(Ask());
            var after = asked.ContinueWith(_ => resumed = Environment.CurrentManagedThreadId,
                TaskContinuationOptions.ExecuteSynchronously);
            approvals.Answer(Assert.Single(approvals.Pending).Id, allowed: true);
            await after.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(answering, resumed);
        }

        /// <summary>The command set asks one question, a program's profile
        /// switch that would turn on an extra that listens on the network
        /// (#600, AutomationCommands.SwitchAsked), and no line answers one:
        /// the answer is the user's alone.</summary>
        [Fact]
        public void NoCommand_AnswersAQuestion_AndOnlyAProfileSwitchAsksOne()
        {
            string commands = OffLimitsSource.Read("PadForge.App/Services/Automation/AutomationCommands.cs");
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(commands, @"\bAskAsync\("));
            Assert.Contains("Approvals.AskAsync(", FenceSource.Body(commands, "private ExternalControlService.Answer SwitchAsked("), StringComparison.Ordinal);
            Assert.DoesNotContain("Approvals.Answer(", commands, StringComparison.Ordinal);
            using var host = NewHost(out _);
            Assert.Empty(host.Approvals.Pending);
        }

        // ── The connected programs ──

        [Fact]
        public void TheClientList_KeepsTheOrderOfConnecting_AndNumbersEachOnce()
        {
            var when = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);
            var clients = new AutomationClients(() => when);
            int changes = 0;
            clients.Changed += (_, _) => changes++;

            var script = clients.Add("lap timer", AutomationClientKind.Script, 100, "AutoHotkey64.exe");
            var assistant = clients.Add("assistant", AutomationClientKind.Assistant, 200, "PadForge.exe");
            Assert.Equal(2, clients.Count);
            Assert.Equal(2, changes);
            Assert.NotEqual(script.Id, assistant.Id);
            Assert.Equal("lap timer", script.Name);
            Assert.Equal(AutomationClientKind.Script, script.Kind);
            Assert.Equal(100, script.ProcessId);
            Assert.Equal("AutoHotkey64.exe", script.ImageName);
            Assert.Equal(when, script.ConnectedSince);
            Assert.Equal(new[] { script, assistant }, clients.Snapshot());

            Assert.True(clients.Remove(script.Id));
            Assert.False(clients.Remove(script.Id));
            Assert.Equal(3, changes);
            Assert.Equal(new[] { assistant }, clients.Snapshot());

            // A number is not used again in the session.
            var plugin = clients.Add("test controller", AutomationClientKind.Plugin, 300, null);
            Assert.DoesNotContain(plugin.Id, new[] { script.Id, assistant.Id });
            Assert.Null(plugin.ImageName);

            using var host = NewHost(out _);
            Assert.Equal(0, host.Clients.Count);
        }

        // ── The host's surface ──

        /// <summary>Later parts of Automation build on the host. Its surface
        /// is one file, and every member there says what it is.</summary>
        [Fact]
        public void EveryMemberOfTheHost_HasItsSummary()
        {
            string source = OffLimitsSource.Read("PadForge.App/Services/Automation/AutomationHost.cs");
            var lines = source.Split('\n');
            var bare = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("public ", StringComparison.Ordinal) && !line.StartsWith("internal ", StringComparison.Ordinal)) continue;
                if (line.StartsWith("internal sealed class AutomationHost", StringComparison.Ordinal)) continue;
                if (line.StartsWith("public void Dispose()", StringComparison.Ordinal)) continue;
                int above = i - 1;
                while (above >= 0 && lines[above].Trim().Length == 0) above--;
                string before = above >= 0 ? lines[above].Trim() : string.Empty;
                if (!before.StartsWith("///", StringComparison.Ordinal)) bare.Add(line);
            }
            Assert.Empty(bare);
            foreach (string member in new[] { "IsOn", "Level", "Allows(", "Changed", "TurnedOff", "ControlPipe", "IsServing", "Commands",
                         "Activity", "Approvals", "Clients", "RecordStart", "RecordStop", "RecordMark", "Recording", "Apply(" })
                Assert.Contains(" " + member, source, StringComparison.Ordinal);
        }

        [Fact]
        public void ADisposedHost_ServesNothing_AndTakesNoMoreSettings()
        {
            string pipe = OwnPipe("gone");
            var host = NewHost(out _, pipe);
            host.Apply(true, AutomationLevel.Read);
            Assert.True(host.IsServing);
            host.Dispose();
            Assert.False(host.IsServing);
            Assert.False(ControlPipeClient.Stands(pipe));
            host.Apply(true, AutomationLevel.ChangeSettings);
            Assert.False(host.IsServing);
            host.RetryControlPipe();
            Assert.False(host.IsServing);
            host.Dispose();
        }
    }
}
