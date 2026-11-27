using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;
using Strings = PadForge.Resources.Strings.Strings;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Plugins;
using Xunit;
using PadForge.Engine.Common;

namespace PadForge.Tests
{
    /// <summary>
    /// The plugin manager (#597) through its seams: a launcher that starts
    /// nothing and a clock the test moves. Approval by hash, the start with
    /// the engine and the switch, the growing pause, five failures, the
    /// count a minute's run clears, the one-time key and the log.
    /// </summary>
    public sealed class PluginManagerTests : IDisposable
    {
        // ── Seams ──

        internal sealed class FakeProcess : IPluginProcess
        {
            private Action<int> _exited;
            public int Id { get; init; }
            public HashSet<int> Children { get; } = new();
            public bool Stopped, Disposed, Ended;
            /// <summary>A debugger is attached to it (#600 item 18).</summary>
            public bool Debugged;
            public bool DebuggerAttached => Debugged;
            public bool Owns(int processId) => processId == Id || Children.Contains(processId);

            public event Action<int> Exited
            {
                add => _exited += value;
                remove => _exited -= value;
            }

            public void Exit(int code)
            {
                if (Ended) return;
                Ended = true;
                _exited?.Invoke(code);
            }

            /// <summary>Its Stop ends nothing yet: the exit comes when the
            /// test calls <see cref="Exit"/>, as a process slow to end.</summary>
            public bool Hangs;

            public void Stop()
            {
                Stopped = true;
                if (!Hangs) Exit(1);
            }

            public void Dispose() => Disposed = true;
        }

        internal sealed class FakeLauncher : IPluginLauncher
        {
            public readonly List<(PluginCommand Command, Dictionary<string, string> Variables, FakeProcess Process)> Starts = new();
            public PluginRefusal Refuse = PluginRefusal.None;
            private int _nextId = 4000;

            public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                if (Refuse != PluginRefusal.None) return PluginLaunch.Refused(Refuse, 5);
                var process = new FakeProcess { Id = ++_nextId };
                Starts.Add((command, new Dictionary<string, string>(variables), process));
                return new PluginLaunch { Process = process };
            }

            public FakeProcess Last => Starts[^1].Process;
        }

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Plugins." + Guid.NewGuid().ToString("N"));
        private readonly FakeLauncher _launcher = new();
        private long _now = 1_000_000;
        private readonly PluginManager _manager;

        /// <summary>The signature check's answer for a held file: a fake,
        /// since the real one asks the certificate authorities' servers
        /// (#600 N9). Null answers that the file is not validly
        /// signed.</summary>
        private Func<string, FileStream, AuthenticodeSignature> _signature;

        public PluginManagerTests()
        {
            Directory.CreateDirectory(_dir);
            _manager = new PluginManager(_launcher, "PadForge.Tests.Pipe", () => _now,
                commandFor: path => new PluginCommand(path, "\"" + path + "\"", Path.GetDirectoryName(path)), timer: false,
                runCheck: work => work(), signatureOf: (path, held) => _signature?.Invoke(path, held));
        }

        public void Dispose()
        {
            _manager.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string NewFile(string name = "bridge.exe", string text = "plugin bytes")
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, text);
            return path;
        }

        private PluginRun AddRunning(string name = "bridge.exe")
        {
            string path = NewFile(name);
            var run = _manager.Add(path, PluginFile.Sha256(path));
            Assert.NotNull(run);
            _manager.SetPluginsOn(true);
            _manager.SetEngine(true);
            Assert.Equal(PluginStatus.Running, run.Status);
            return run;
        }

        // ── Starting and stopping ──

        [Fact]
        public void AnApprovedPlugin_StartsWithTheEngineWhilePluginsIsOn_AndStopsWithIt()
        {
            string path = NewFile();
            var run = _manager.Add(path, PluginFile.Sha256(path));
            Assert.Empty(_launcher.Starts);
            Assert.Equal(PluginStatus.Stopped, run.Status);

            _manager.SetPluginsOn(true);
            Assert.Empty(_launcher.Starts);
            Assert.Equal(PluginStatus.Waiting, run.Status);

            _manager.SetEngine(true);
            Assert.Single(_launcher.Starts);
            Assert.Equal(PluginStatus.Running, run.Status);
            Assert.Equal(1, _manager.RunningCount);

            _manager.SetEngine(false);
            Assert.True(_launcher.Last.Stopped);
            Assert.True(_launcher.Last.Disposed || WaitDisposed(_launcher.Last));
            Assert.Equal(PluginStatus.Waiting, run.Status);
            // A stop by PadForge is no failure.
            Assert.Equal(0, run.Failures);

            _manager.SetEngine(true);
            Assert.Equal(2, _launcher.Starts.Count);
            _manager.SetPluginsOn(false);
            Assert.True(_launcher.Last.Stopped);
            Assert.Equal(PluginStatus.Stopped, run.Status);
        }

        private static bool WaitDisposed(FakeProcess process)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!process.Disposed && DateTime.UtcNow < until) System.Threading.Thread.Sleep(10);
            return process.Disposed;
        }

        [Fact]
        public void ThePluginsSwitch_StartsAndStopsIt_AndSwitchingOnClearsItsFailures()
        {
            var run = AddRunning();
            _manager.SetEnabled(run, false);
            Assert.True(_launcher.Last.Stopped);
            Assert.Equal(PluginStatus.Stopped, run.Status);
            Assert.False(run.Enabled);

            _manager.SetEnabled(run, true);
            Assert.Equal(2, _launcher.Starts.Count);
            Assert.Equal(PluginStatus.Running, run.Status);
            Assert.True(_manager.Save().Single().Enabled);
        }

        [Fact]
        public void TheStart_GivesTheKeyAndThePipesName_InTheFolderOfTheFile()
        {
            var run = AddRunning();
            var (command, variables, _) = _launcher.Starts.Single();
            Assert.Equal("PadForge.Tests.Pipe", variables[PluginManager.PipeVariable]);
            Assert.Matches("^[0-9A-F]{64}$", variables[PluginManager.TokenVariable]);
            Assert.Equal(_dir, command.Folder);
            Assert.Equal(run.Path, command.Program);
        }

        // ── Exits, the pause and five failures ──

        [Theory]
        [InlineData(1, 1000)]
        [InlineData(2, 2000)]
        [InlineData(3, 4000)]
        [InlineData(4, 8000)]
        public void ThePause_Grows_FromASecondToEight(int failures, int pauseMs)
            => Assert.Equal(pauseMs, PluginManager.PauseAfter(failures));

        [Fact]
        public void AnExit_StartsItAgainAfterAGrowingPause_AndTheFifthFailureStopsIt()
        {
            var run = AddRunning();
            int[] pauses = { 1000, 2000, 4000, 8000 };
            for (int i = 0; i < pauses.Length; i++)
            {
                _launcher.Last.Exit(3);
                Assert.Equal(PluginStatus.Restarting, run.Status);
                Assert.Equal(pauses[i], run.PauseMs);
                Assert.Equal(i + 1, run.Failures);
                Assert.Equal(3, run.ExitCode);
                int starts = _launcher.Starts.Count;

                _now += pauses[i] - 1;
                _manager.Tick();
                Assert.Equal(starts, _launcher.Starts.Count);
                _now += 1;
                _manager.Tick();
                Assert.Equal(starts + 1, _launcher.Starts.Count);
                Assert.Equal(PluginStatus.Running, run.Status);
            }

            _launcher.Last.Exit(3);
            Assert.Equal(PluginStatus.Failed, run.Status);
            Assert.Equal(5, run.Failures);
            int count = _launcher.Starts.Count;
            _now += 60_000;
            _manager.Tick();
            Assert.Equal(count, _launcher.Starts.Count);
            Assert.Contains(_manager.LogOf(run), l => l.Kind == PluginLogKind.Failed);

            // Start clears the count and starts it.
            _manager.StartAgain(run);
            Assert.Equal(count + 1, _launcher.Starts.Count);
            Assert.Equal(0, run.Failures);
            Assert.Equal(PluginStatus.Running, run.Status);
        }

        [Fact]
        public void ARunThatLastedAMinute_ClearsTheFailuresBeforeIt()
        {
            var run = AddRunning();
            for (int i = 0; i < 3; i++)
            {
                _launcher.Last.Exit(1);
                _now += run.PauseMs;
                _manager.Tick();
            }
            Assert.Equal(3, run.Failures);

            // A run that said hello and lasted a minute.
            void Hello() => Assert.NotNull(_manager.AcceptToken(_launcher.Starts[^1].Variables[PluginManager.TokenVariable], _launcher.Last.Id));
            Hello();
            _now += PluginManager.SteadyRunMs;
            _launcher.Last.Exit(1);
            Assert.Equal(1, run.Failures);
            Assert.Equal(1000, run.PauseMs);

            // A minute less a millisecond does not.
            _now += run.PauseMs;
            _manager.Tick();
            Hello();
            _now += PluginManager.SteadyRunMs - 1;
            _launcher.Last.Exit(1);
            Assert.Equal(2, run.Failures);

            // Nor does a minute with no hello.
            _now += run.PauseMs;
            _manager.Tick();
            _now += PluginManager.SteadyRunMs;
            _launcher.Last.Exit(1);
            Assert.Equal(3, run.Failures);
        }

        /// <summary>A plugin that has not said hello a minute after its start
        /// is ended, unless a debugger is attached to it, and started again
        /// with the back-off. Its key is taken from it, so a late hello is
        /// refused. A run with no hello is never a steady one, so the pause
        /// grows and five misses stop it. The run that says hello is
        /// connected and never ended for its hello.</summary>
        [Fact]
        public void APluginThatNeverSaysHello_IsEndedAtItsDeadline_AndStartsAgainWithAGrowingPause()
        {
            Assert.Equal(PluginsController.EndAfterSilenceMs, PluginManager.HelloDeadlineMs);
            var run = AddRunning();
            string token = _launcher.Starts[0].Variables[PluginManager.TokenVariable];
            var first = _launcher.Last;
            _now += PluginManager.HelloDeadlineMs - 1;
            _manager.CheckHello();
            Assert.False(first.Stopped);
            _now += 1;
            first.Debugged = true;
            _manager.CheckHello();
            Assert.False(first.Stopped);
            Assert.False(run.NeverConnected);
            first.Debugged = false;
            _manager.CheckHello();
            Assert.True(first.Stopped);
            Assert.True(run.NeverConnected);
            Assert.Equal(PluginStatus.Restarting, run.Status);
            Assert.Equal(1, run.Failures);
            Assert.Contains(_manager.LogOf(run), l => l.Kind == PluginLogKind.NeverConnected);
            Assert.Null(_manager.AcceptToken(token, first.Id));

            // Its next run lasts past a minute and never says hello either:
            // no steady run, so the failures count on and the pause grows.
            _now += run.PauseMs;
            _manager.Tick();
            Assert.Equal(2, _launcher.Starts.Count);
            _now += PluginManager.HelloDeadlineMs;
            _manager.CheckHello();
            Assert.True(_launcher.Last.Stopped);
            Assert.Equal(2, run.Failures);
            Assert.Equal(PluginManager.PauseAfter(2), run.PauseMs);

            // The run that says hello is connected and is not ended for it.
            _now += run.PauseMs;
            _manager.Tick();
            Assert.NotNull(_manager.AcceptToken(_launcher.Starts[2].Variables[PluginManager.TokenVariable], _launcher.Last.Id));
            Assert.False(run.NeverConnected);
            _now += PluginManager.HelloDeadlineMs * 2;
            _manager.CheckHello();
            Assert.False(_launcher.Last.Stopped);
            Assert.Equal(PluginStatus.Running, run.Status);
        }

        [Fact]
        public void AnExitWhileStopping_IsNoFailure_AndTheEngineStopEndsThePause()
        {
            var run = AddRunning();
            _launcher.Last.Exit(9);
            Assert.Equal(PluginStatus.Restarting, run.Status);
            _manager.SetEngine(false);
            Assert.Equal(PluginStatus.Waiting, run.Status);
            int count = _launcher.Starts.Count;
            _now += 10_000;
            _manager.Tick();
            Assert.Equal(count, _launcher.Starts.Count);
        }

        // ── Approval ──

        // ── Trust New Versions From This Signer (#600 N9) ──

        private const string KeyOne = "ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB";
        private const string KeyTwo = "CDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCD";

        /// <summary>The check answers for the handle that was hashed: a
        /// signature whose key is <paramref name="key"/> while the file held
        /// is <paramref name="bytes"/>, else none.</summary>
        private Func<string, FileStream, AuthenticodeSignature> SignedFor(string bytes, string key, string signer = "Example Studio")
            => (path, held) =>
            {
                Assert.NotNull(held);
                held.Position = 0;
                string text = new StreamReader(held, leaveOpen: true).ReadToEnd();
                held.Position = 0;
                return text == bytes ? new AuthenticodeSignature(signer, key) : null;
            };

        /// <summary>With the switch on, a changed file restarts without
        /// Approve only when its signature verifies and its key is the one
        /// trusted. Another key, no valid signature, the same name on another
        /// key, or the switch off: Changed, as before. The new bytes become
        /// the approved ones and are saved.</summary>
        [Fact]
        public void ATrustedSignersNewVersion_StartsWithoutApprove_OnlyForTheSameKey()
        {
            string path = NewFile(text: "version 1");
            var run = _manager.Add(path, PluginFile.Sha256(path), "Example Studio", KeyOne);
            Assert.Equal(KeyOne, run.SignerKey);
            Assert.False(run.TrustSigner);
            File.WriteAllText(path, "version 2");
            _manager.SetPluginsOn(true);

            // Off: the same key still asks.
            _signature = SignedFor("version 2", KeyOne);
            _manager.SetEngine(true);
            Assert.Equal(PluginStatus.Changed, run.Status);
            Assert.Empty(_launcher.Starts);

            _manager.SetTrust(run, true);
            Assert.True(run.TrustSigner);
            Assert.True(_manager.Save().Single().TrustSigner);

            // Another key under the same name, and no valid signature, ask.
            _signature = SignedFor("version 2", KeyTwo);
            _manager.StartAgain(run);
            Assert.Equal(PluginStatus.Changed, run.Status);
            _signature = (_, _) => null;
            _manager.StartAgain(run);
            Assert.Equal(PluginStatus.Changed, run.Status);
            Assert.Empty(_launcher.Starts);

            // The same key: it starts on the new bytes, which are approved
            // and saved.
            _signature = SignedFor("version 2", KeyOne, "Example Studio Ltd");
            _manager.StartAgain(run);
            Assert.Equal(PluginStatus.Running, run.Status);
            Assert.Single(_launcher.Starts);
            Assert.Equal(PluginFile.Sha256(path), run.Sha256);
            Assert.Equal("Example Studio Ltd", run.Signer);
            Assert.Equal(KeyOne, run.SignerKey);
            Assert.True(run.TrustSigner);
            Assert.Equal(PluginFile.Sha256(path), _manager.Save().Single().Sha256);
            Assert.Contains(_manager.LogOf(run), l => l.Kind == PluginLogKind.TrustedVersion);
        }

        /// <summary>A running plugin's file that changes to a version signed
        /// with the trusted key restarts on it, as Approve and Restart does.
        /// One signed with another key shows Approve and Restart and keeps
        /// running.</summary>
        [Fact]
        public void ARunningPlugin_RestartsOnATrustedVersion_AndKeepsRunningOnAnyOther()
        {
            string path = NewFile(text: "version 1");
            var run = _manager.Add(path, PluginFile.Sha256(path), "Example Studio", KeyOne);
            _manager.SetTrust(run, true);
            _manager.SetPluginsOn(true);
            _manager.SetEngine(true);
            Assert.NotNull(_manager.AcceptToken(_launcher.Starts[0].Variables[PluginManager.TokenVariable], _launcher.Last.Id));
            var first = _launcher.Last;

            File.WriteAllText(path, "version 2");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            _signature = SignedFor("version 2", KeyTwo);
            _manager.CheckFiles();
            Assert.True(run.FileChanged);
            Assert.False(first.Stopped);

            // Moved again, still not the trusted key: looked at again, and
            // the log says it changed once.
            File.WriteAllText(path, "version 2b");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1.5));
            _manager.CheckFiles();
            Assert.True(run.FileChanged);
            Assert.False(first.Stopped);
            Assert.Single(_manager.LogOf(run), l => l.Kind == PluginLogKind.ChangedWhileRunning);

            // Rebuilt again, now with the trusted key: the marked file is
            // looked at again, since the user trusts its signer.
            File.WriteAllText(path, "version 3");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
            _signature = SignedFor("version 3", KeyOne);
            _manager.CheckFiles();
            Assert.True(first.Stopped);
            Assert.NotSame(first, _launcher.Last);
            Assert.False(run.FileChanged);
            Assert.Equal(PluginStatus.Running, run.Status);
            Assert.Equal(PluginFile.Sha256(path), run.Sha256);
        }

        /// <summary>The switch turns on only while a key is kept. Approving
        /// bytes with another key, or with none, turns it off. The key and
        /// the switch are kept in PadForge.xml.</summary>
        [Fact]
        public void TheTrustSwitch_NeedsAKey_AndApprovingAnotherKeyTurnsItOff()
        {
            string unsigned = NewFile("plain.exe");
            var plain = _manager.Add(unsigned, PluginFile.Sha256(unsigned), null, null);
            _manager.SetTrust(plain, true);
            Assert.False(plain.TrustSigner);

            string path = NewFile();
            var run = _manager.Add(path, PluginFile.Sha256(path), "Example Studio", KeyOne.ToLowerInvariant());
            _manager.SetTrust(run, true);
            var saved = _manager.Save().Single(d => d.Path == run.Path);
            Assert.Equal(KeyOne, saved.SignerKey);
            Assert.True(saved.TrustSigner);
            var copy = new PluginRun(saved);
            Assert.True(copy.TrustSigner);
            Assert.Equal(KeyOne, copy.SignerKey);
            // A record whose key is gone trusts nothing.
            Assert.False(new PluginRun(new PluginData { Path = path, Sha256 = "AA", TrustSigner = true }).TrustSigner);

            // The same key approved again keeps it on. Another turns it off.
            _manager.Approve(run, run.Sha256, "Example Studio", KeyOne);
            Assert.True(run.TrustSigner);
            _manager.Approve(run, run.Sha256, "Someone Else", KeyTwo);
            Assert.False(run.TrustSigner);
            Assert.Equal(KeyTwo, run.SignerKey);
            _manager.SetTrust(run, true);
            _manager.Approve(run, run.Sha256, null, null);
            Assert.False(run.TrustSigner);
            Assert.Null(run.SignerKey);
        }

        /// <summary>A plugin approved before keys were kept: at its next
        /// start, while its file is still the approved bytes, the key is read
        /// on the handle that was hashed and kept, so the trust switch shows
        /// with no new approval. A file approved as unsigned, or changed
        /// since, gets none.</summary>
        [Fact]
        public void AnOldApproval_GetsItsKeyAtItsNextStart_FromTheHashedFile()
        {
            string core = typeof(object).Assembly.Location;
            string path = Path.Combine(_dir, "signed.dll");
            File.Copy(core, path);
#pragma warning disable SYSLIB0057
            using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(core));
#pragma warning restore SYSLIB0057
            string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

            var run = _manager.Add(path, PluginFile.Sha256(path), "Example Studio", null);
            string copy = Path.Combine(_dir, "unsigned-approval.dll");
            File.Copy(core, copy);
            var unsignedApproval = _manager.Add(copy, PluginFile.Sha256(copy), null, null);
            Assert.Null(run.SignerKey);
            _manager.SetPluginsOn(true);
            _manager.SetEngine(true);
            Assert.Equal(PluginStatus.Running, run.Status);
            Assert.Equal(expected, run.SignerKey);
            Assert.Equal(expected, _manager.Save().Single(d => d.Path == run.Path).SignerKey);
            Assert.False(run.TrustSigner);
            Assert.Null(unsignedApproval.SignerKey);

            // A file changed since its approval: no key from it.
            string other = Path.Combine(_dir, "changed.dll");
            File.Copy(core, other);
            var changed = _manager.Add(other, "00", "Example Studio", null);
            _manager.StartAgain(changed);
            Assert.Equal(PluginStatus.Changed, changed.Status);
            Assert.Null(changed.SignerKey);
        }

        [Fact]
        public void AChangedFile_IsNotStarted_AndApprovingItsNewBytesStartsIt()
        {
            string path = NewFile();
            var run = _manager.Add(path, PluginFile.Sha256(path));
            File.WriteAllText(path, "other bytes");
            _manager.SetPluginsOn(true);
            _manager.SetEngine(true);
            Assert.Empty(_launcher.Starts);
            Assert.Equal(PluginStatus.Changed, run.Status);
            Assert.Contains(_manager.LogOf(run), l => l.Kind == PluginLogKind.Changed);

            // Approval is for exact bytes: the old hash again changes nothing.
            _manager.Approve(run, run.Sha256);
            Assert.Empty(_launcher.Starts);
            Assert.Equal(PluginStatus.Changed, run.Status);

            _manager.Approve(run, PluginFile.Sha256(path));
            Assert.Single(_launcher.Starts);
            Assert.Equal(PluginStatus.Running, run.Status);
            Assert.Equal(PluginFile.Sha256(path), _manager.Save().Single().Sha256);
        }

        [Fact]
        public void ARunningPlugin_HoldsItsFile_SoNothingChangesIt()
        {
            var run = AddRunning();
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(run.Path, "swapped"));
            Assert.ThrowsAny<IOException>(() => File.Delete(run.Path));
            _launcher.Last.Exit(0);
            File.WriteAllText(run.Path, "edited after it ended");
        }

        /// <summary>A manager whose start opens and hashes on the thread
        /// pool, as the app's does, through an open that waits for
        /// <paramref name="release"/> once it has the file.</summary>
        private PluginManager Gated(ManualResetEventSlim entered, ManualResetEventSlim release,
            Action<int, FileStream> opened)
            => new PluginManager(_launcher, "PadForge.Tests.Pipe", () => _now,
                commandFor: path => new PluginCommand(path, "\"" + path + "\"", Path.GetDirectoryName(path)), timer: false,
                openHeld: path =>
                {
                    var held = PluginFile.OpenHeld(path, out string sha);
                    opened(Environment.CurrentManagedThreadId, held);
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(20)), "the test never let the hash finish");
                    return (held, sha);
                });

        private static void Until(Func<bool> condition, string what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.ElapsedMilliseconds < 10000, what);
                Thread.Sleep(5);
            }
        }

        /// <summary>The file is opened and hashed off the thread that asked
        /// for the start and with the manager's lock let go: while the hash
        /// runs, a call that takes the lock answers at once, and the row says
        /// the file is being checked. The handle that was hashed is the one
        /// the run holds, so nothing can change the file between the hash and
        /// the plugin's exit.</summary>
        [Fact]
        public async System.Threading.Tasks.Task TheFile_IsHashedOffTheCallersThread_WithTheLockLetGo_AndTheHashedHandleIsHeld()
        {
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            int hashThread = 0;
            FileStream hashed = null;
            using var manager = Gated(entered, release, (thread, held) => { hashThread = thread; hashed = held; });
            string path = NewFile();
            var run = manager.Add(path, PluginFile.Sha256(path));
            manager.SetPluginsOn(true);
            // The call that starts it returns while the file is still read.
            manager.SetEngine(true);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the file was never opened");
            try
            {
                Assert.NotEqual(Environment.CurrentManagedThreadId, hashThread);
                // Every member that takes the lock answers from another
                // thread while the hash waits.
                var asked = System.Threading.Tasks.Task.Run(() => (manager.Snapshot().Count, manager.RunningCount, manager.Has(path)));
                var first = await System.Threading.Tasks.Task.WhenAny(asked, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2)));
                Assert.True(first == asked, "the manager's lock was held while the file was hashed");
                Assert.Equal((1, 0, true), await asked);
                Assert.Equal(PluginStatus.Checking, run.Status);
                Assert.Equal(Strings.Instance.Plugins_State_Checking, PluginsController.StateOf(run));
                Assert.Empty(_launcher.Starts);
                // Already held against changes while it is hashed.
                Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "swapped"));
            }
            finally
            {
                release.Set();
            }
            Until(() => run.Status == PluginStatus.Running, "the plugin did not start once its file was checked");
            Assert.Single(_launcher.Starts);
            Assert.Same(hashed, run.Held);
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "swapped"));
            Assert.ThrowsAny<IOException>(() => File.Delete(path));
            _launcher.Last.Exit(0);
            manager.SetEnabled(run, false);
            File.WriteAllText(path, "edited after it ended");
        }

        /// <summary>A stop while the file is read: the check that comes back
        /// starts nothing and lets go of the file.</summary>
        [Fact]
        public void AStopWhileTheFileIsChecked_StartsNothing_AndLetsGoOfTheFile()
        {
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            FileStream hashed = null;
            using var manager = Gated(entered, release, (_, held) => hashed = held);
            string path = NewFile();
            var run = manager.Add(path, PluginFile.Sha256(path));
            manager.SetPluginsOn(true);
            manager.SetEngine(true);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the file was never opened");
            manager.SetEnabled(run, false);
            Assert.Equal(PluginStatus.Stopped, run.Status);
            release.Set();
            Until(() => hashed != null && !hashed.CanRead, "the check that came back late kept the file");
            Assert.Empty(_launcher.Starts);
            Assert.Equal(PluginStatus.Stopped, run.Status);
            Assert.Null(run.Held);
            File.WriteAllText(path, "edited after the stop");
        }

        [Fact]
        public void AFileThatIsGone_IsNotStarted_AndItsRowSaysWhy()
        {
            string path = NewFile();
            var run = _manager.Add(path, PluginFile.Sha256(path));
            File.Delete(path);
            _manager.SetPluginsOn(true);
            _manager.SetEngine(true);
            Assert.Empty(_launcher.Starts);
            Assert.Equal(PluginStatus.Refused, run.Status);
            Assert.Equal(PluginRefusal.Missing, run.Refusal);
        }

        /// <summary>Approve and Restart with the engine running keeps the
        /// plugin's state until the old run's exit starts the new one: the
        /// card never says it waits for the engine.</summary>
        [Fact]
        public void ApproveAndRestart_WithTheEngineOn_NeverSaysItWaitsForTheEngine()
        {
            var run = AddRunning();
            Assert.NotNull(_manager.AcceptToken(_launcher.Starts[0].Variables[PluginManager.TokenVariable], _launcher.Last.Id));
            File.WriteAllText(run.Path, "rebuilt");
            File.SetLastWriteTimeUtc(run.Path, DateTime.UtcNow.AddMinutes(1));
            _manager.CheckFiles();
            Assert.True(run.FileChanged);
            var first = _launcher.Last;
            first.Hangs = true;
            _manager.Approve(run, PluginFile.Sha256(run.Path));
            Assert.True(first.Stopped);
            Assert.NotEqual(PluginStatus.Waiting, run.Status);
            Assert.Equal(PluginStatus.Running, run.Status);
            first.Exit(1);
            Assert.Equal(2, _launcher.Starts.Count);
            Assert.Equal(PluginStatus.Running, run.Status);
            Assert.Equal(0, run.Failures);
        }

        /// <summary>A file that changed while its plugin ran and then went
        /// back to the approved bytes is read again when its stamp moves, and
        /// the card no longer offers Approve and Restart.</summary>
        [Fact]
        public void AChangedFile_ThatIsTheApprovedBytesAgain_IsNoLongerMarked()
        {
            var run = AddRunning();
            Assert.NotNull(_manager.AcceptToken(_launcher.Starts[0].Variables[PluginManager.TokenVariable], _launcher.Last.Id));
            File.WriteAllText(run.Path, "rebuilt");
            File.SetLastWriteTimeUtc(run.Path, DateTime.UtcNow.AddMinutes(1));
            _manager.CheckFiles();
            Assert.True(run.FileChanged);
            File.WriteAllText(run.Path, "plugin bytes");
            File.SetLastWriteTimeUtc(run.Path, DateTime.UtcNow.AddMinutes(2));
            _manager.CheckFiles();
            Assert.False(run.FileChanged);
            Assert.Equal(PluginStatus.Running, run.Status);
        }

        /// <summary>The once-a-second look reads each file's size and write
        /// time where it hashes it, off the caller's thread, so a share that
        /// stops answering holds no window.</summary>
        [Fact]
        public void TheFileLook_StatsOffTheCallersThread()
        {
            var threads = new System.Collections.Concurrent.ConcurrentBag<int>();
            using var manager = new PluginManager(_launcher, "PadForge.Tests.Pipe", () => _now,
                commandFor: path => new PluginCommand(path, "\"" + path + "\"", Path.GetDirectoryName(path)), timer: false,
                runCheck: work =>
                {
                    var worker = new Thread(() => work());
                    worker.Start();
                    worker.Join();
                });
            string path = NewFile("share.exe");
            var run = manager.Add(path, PluginFile.Sha256(path));
            manager.SetPluginsOn(true);
            manager.SetEngine(true);
            Assert.Equal(PluginStatus.Running, run.Status);
            Assert.NotNull(manager.AcceptToken(_launcher.Starts[^1].Variables[PluginManager.TokenVariable], _launcher.Last.Id));
            manager.StatFile = p =>
            {
                threads.Add(Environment.CurrentManagedThreadId);
                return (1, DateTime.MinValue);
            };
            manager.CheckFiles();
            Assert.NotEmpty(threads);
            Assert.DoesNotContain(Environment.CurrentManagedThreadId, threads);
        }

        /// <summary>A start runs the file the held handle reached, every
        /// junction and link on its path followed, so a junction pointed
        /// elsewhere after the hash does not change what starts.</summary>
        [Fact]
        public void TheStart_RunsTheFileTheHeldHandleReached()
        {
            string approved = NewFile("bridge.exe");
            string real = Path.Combine(_dir, "real");
            Directory.CreateDirectory(real);
            string reached = Path.Combine(real, "bridge.exe");
            File.WriteAllText(reached, "plugin bytes");
            var asked = new List<string>();
            using var manager = new PluginManager(_launcher, "PadForge.Tests.Pipe", () => _now,
                commandFor: path =>
                {
                    asked.Add(path);
                    return new PluginCommand(path, "\"" + path + "\"", Path.GetDirectoryName(path));
                },
                timer: false, runCheck: work => work(),
                openHeld: path =>
                {
                    // The approved path reaches the file in "real", as a
                    // junction above it would.
                    var held = PluginFile.OpenHeld(reached, out string sha);
                    return (held, sha);
                });
            var run = manager.Add(approved, PluginFile.Sha256(approved));
            manager.SetPluginsOn(true);
            manager.SetEngine(true);
            Assert.Equal(PluginStatus.Running, run.Status);
            string started = Assert.Single(asked);
            Assert.EndsWith(@"\real\bridge.exe", started, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(started), started);
        }

        /// <summary>The kind of start is the approved file's: a plugin
        /// approved as a PowerShell script whose path reaches a program
        /// (a link with another extension) runs in PowerShell with the
        /// file it reached, never as that program.</summary>
        [Fact]
        public void TheKindOfStart_IsTheApprovedFilesKind_WhereverItsPathLeads()
        {
            string approved = NewFile("pad.ps1");
            string tool = NewFile("tool.exe", "plugin bytes");
            using var manager = new PluginManager(_launcher, "PadForge.Tests.Pipe", () => _now, timer: false, runCheck: work => work(),
                openHeld: path =>
                {
                    // The approved path reaches tool.exe, as a symbolic link
                    // named pad.ps1 would.
                    var held = PluginFile.OpenHeld(tool, out string sha);
                    return (held, sha);
                });
            var run = manager.Add(approved, PluginFile.Sha256(tool));
            manager.SetPluginsOn(true);
            manager.SetEngine(true);
            Assert.Equal(PluginStatus.Running, run.Status);
            var command = _launcher.Starts.Single().Command;
            Assert.EndsWith(@"\powershell.exe", command.Program, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("-File " + PluginFile.Quote(tool), command.CommandLine);
        }

        // ── The key ──

        [Fact]
        public void TheKey_IsTakenOnce_OnlyFromTheProcessPadForgeStarted()
        {
            var run = AddRunning();
            var (_, variables, process) = _launcher.Starts.Single();
            string token = variables[PluginManager.TokenVariable];

            // A key one digit off. The key is hex, so a fixed first digit of
            // "0" was the key itself whenever the key began with 0, one start
            // in 16.
            string wrong = (token[0] == '0' ? "1" : "0") + token.Substring(1);
            Assert.NotEqual(token, wrong);
            Assert.Null(_manager.AcceptToken(wrong, process.Id));
            Assert.Null(_manager.AcceptToken(token, process.Id + 1));
            Assert.Contains(_manager.LogOf(run), l => l.Kind == PluginLogKind.ForeignKey && l.Code == process.Id + 1);

            var grant = _manager.AcceptToken(token, process.Id);
            Assert.NotNull(grant);
            Assert.Equal("bridge", grant.Name);
            Assert.True(_manager.IsPluginsGrant(grant));

            // Refused twice.
            Assert.Null(_manager.AcceptToken(token, process.Id));
        }

        [Fact]
        public void TheKey_IsTakenFromAProcessInThePluginsJob_AsAnInterpretersLauncherStartsOne()
        {
            AddRunning("bridge.py");
            var (_, variables, process) = _launcher.Starts.Single();
            process.Children.Add(process.Id + 50);
            Assert.NotNull(_manager.AcceptToken(variables[PluginManager.TokenVariable], process.Id + 50));
        }

        [Fact]
        public void TheKey_IsRefused_WhenNoPluginIsExpected()
        {
            var run = AddRunning();
            var (_, variables, process) = _launcher.Starts.Single();
            string token = variables[PluginManager.TokenVariable];
            _manager.SetEngine(false);
            Assert.Null(_manager.AcceptToken(token, process.Id));

            // A new start has a new key: the old one opens nothing.
            _manager.SetEngine(true);
            Assert.Null(_manager.AcceptToken(token, _launcher.Last.Id));
            Assert.NotNull(_manager.AcceptToken(_launcher.Starts[^1].Variables[PluginManager.TokenVariable], _launcher.Last.Id));
            Assert.Null(_manager.AcceptToken(null, 1));
            Assert.Null(_manager.AcceptToken("", 1));
            Assert.Equal(PluginStatus.Running, run.Status);
        }

        // ── The log and the name ──

        [Fact]
        public void TheLog_KeepsItsLast200Lines_WithStartsExitsAndWhatThePluginWrote()
        {
            var run = AddRunning();
            var grant = _manager.AcceptToken(_launcher.Starts[0].Variables[PluginManager.TokenVariable], _launcher.Last.Id);
            for (int i = 0; i < 250; i++) grant.Log("line " + i);
            var log = _manager.LogOf(run);
            Assert.Equal(PluginManager.LogLines, log.Count);
            Assert.Equal("line 249", log[^1].Text);
            Assert.Equal("line 50", log[0].Text);

            _launcher.Last.Exit(7);
            log = _manager.LogOf(run);
            Assert.Equal(PluginLogKind.Exited, log[^1].Kind);
            Assert.Equal(7, log[^1].Code);
            Assert.Equal(PluginManager.LogLines, log.Count);
        }

        [Fact]
        public void TheNameItGives_IsShownAndSaved_AndItsIdentityStays()
        {
            var run = AddRunning();
            Assert.Equal("bridge.exe", run.ShownName);
            int saved = 0;
            _manager.SavedChanged += () => saved++;
            var grant = _manager.AcceptToken(_launcher.Starts[0].Variables[PluginManager.TokenVariable], _launcher.Last.Id);
            grant.Named("Wheel Bridge");
            Assert.Equal("Wheel Bridge", run.ShownName);
            Assert.Equal(1, saved);
            var data = _manager.Save().Single();
            Assert.Equal("Wheel Bridge", data.Name);
            Assert.Equal("bridge", data.Identity);
        }

        [Fact]
        public void TheNamesAPluginGives_AreShownWithNoFormatCharacters()
        {
            var run = AddRunning();
            var grant = _manager.AcceptToken(_launcher.Starts[0].Variables[PluginManager.TokenVariable], _launcher.Last.Id);
            // A right-to-left override, a zero-width space, a word joiner and
            // a tag character, none of which may reach the page.
            grant.Named("Wheel\u202E Bridge\u200B\u2060\U000E0041");
            Assert.Equal("Wheel Bridge", run.ShownName);
            grant.Log("ok\u2066 fine\u202Dx");
            Assert.Equal("ok finex", _manager.LogOf(run)[^1].Text);
            Assert.Equal("Pad", ProgramDeviceDeclaration.Clean("P\u202Ea\u200Dd", 64));
            Assert.Equal("ab", ShownText.Clean("a\u200E\u200F\u061C\uFEFFb"));
            Assert.Null(ShownText.Clean(null));
        }

        [Fact]
        public void TwoFilesOfOneName_GetIdentitiesOfTheirOwn_AndAFileIsAddedOnce()
        {
            string a = NewFile("plugin.exe");
            Directory.CreateDirectory(Path.Combine(_dir, "b"));
            string b = Path.Combine(_dir, "b", "plugin.exe");
            File.WriteAllText(b, "other");
            Assert.NotNull(_manager.Add(a, PluginFile.Sha256(a)));
            Assert.NotNull(_manager.Add(b, PluginFile.Sha256(b)));
            Assert.Null(_manager.Add(a, PluginFile.Sha256(a)));
            Assert.True(_manager.Has(a));
            Assert.Equal(new[] { "plugin", "plugin 2" }, _manager.Save().Select(p => p.Identity));
        }

        [Fact]
        public void TheList_IsSavedInPadForgeXml_AndLoadedBack()
        {
            var run = AddRunning();
            _manager.SetEnabled(run, false);
            // A value the plugin declared, and one of a device of its own
            // (#600 item 17), kept with it.
            run.Settings.Load(new[]
            {
                new PluginValueData { Key = "speed", Value = "2" },
                new PluginValueData { Device = "pedals", Key = "dead", Value = "5" },
            });
            var settings = new AppSettingsData
            {
                Plugins = _manager.Save(), TestControllerOn = true, ReplayOn = true, ReplayRepeat = true,
                TestControllerValues = new[] { new PluginValueData { Key = "by-hand", Value = "true" } },
            };
            var xml = new XmlSerializer(typeof(AppSettingsData));
            string text;
            using (var write = new StringWriter())
            {
                xml.Serialize(write, settings);
                text = write.ToString();
            }
            Assert.Contains("<Plugins>", text);
            Assert.Contains("Sha256=\"" + run.Sha256 + "\"", text);
            AppSettingsData back;
            using (var read = new StringReader(text)) back = (AppSettingsData)xml.Deserialize(read);
            var data = Assert.Single(back.Plugins);
            Assert.Equal(run.Path, data.Path);
            Assert.False(data.Enabled);
            Assert.True(back.TestControllerOn);
            Assert.Equal("by-hand", Assert.Single(back.TestControllerValues).Key);
            Assert.Equal(new[] { (null as string, "speed", "2"), ("pedals", "dead", "5") },
                data.Values.Select(v => (string.IsNullOrEmpty(v.Device) ? null : v.Device, v.Key, v.Value)));
            Assert.True(back.ReplayOn && back.ReplayRepeat);

            // A file with none writes none of the parts.
            using var empty = new StringWriter();
            xml.Serialize(empty, new AppSettingsData());
            Assert.DoesNotContain("<Plugins", empty.ToString());
            Assert.DoesNotContain("TestController", empty.ToString());
            Assert.DoesNotContain("<Replay", empty.ToString());

            using var other = new PluginManager(_launcher, "x", () => _now, timer: false, runCheck: work => work());
            other.Load(back.Plugins);
            Assert.Equal(run.Path, other.Snapshot().Single().Path);
        }

        [Fact]
        public void ALoadInPlaceOfTheList_StopsThePluginsOfTheOldOne()
        {
            AddRunning();
            var first = _launcher.Last;
            _manager.Load(Array.Empty<PluginData>());
            Assert.True(first.Stopped);
            Assert.Empty(_manager.Snapshot());
        }

        [Fact]
        public void Removing_StopsIt_AndItIsNoLongerOnTheList()
        {
            var run = AddRunning();
            _manager.Remove(run);
            Assert.True(_launcher.Last.Stopped);
            Assert.Empty(_manager.Snapshot());
            Assert.Empty(_manager.Save());
        }
    }
}
