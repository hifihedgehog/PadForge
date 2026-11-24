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

            public void Stop()
            {
                Stopped = true;
                Exit(1);
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

        public PluginManagerTests()
        {
            Directory.CreateDirectory(_dir);
            _manager = new PluginManager(_launcher, "PadForge.Tests.Pipe", () => _now,
                commandFor: path => new PluginCommand(path, "\"" + path + "\"", Path.GetDirectoryName(path)), timer: false,
                runCheck: work => work());
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
        [InlineData(5, 16000)]
        [InlineData(6, 30000)]
        [InlineData(40, 30000)]
        public void ThePause_Grows_FromASecondToThirty(int failures, int pauseMs)
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

            _now += PluginManager.SteadyRunMs;
            _launcher.Last.Exit(1);
            Assert.Equal(1, run.Failures);
            Assert.Equal(1000, run.PauseMs);

            // A minute less a millisecond does not.
            _now += run.PauseMs;
            _manager.Tick();
            _now += PluginManager.SteadyRunMs - 1;
            _launcher.Last.Exit(1);
            Assert.Equal(2, run.Failures);
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

        [Fact]
        public void NoShell_IsTriedAgainAfterThePause_ButAnElevatedShellIsNot()
        {
            string path = NewFile();
            var run = _manager.Add(path, PluginFile.Sha256(path));
            _launcher.Refuse = PluginRefusal.NoShell;
            _manager.SetPluginsOn(true);
            _manager.SetEngine(true);
            Assert.Equal(PluginStatus.Restarting, run.Status);
            Assert.Equal(PluginRefusal.NoShell, run.Refusal);
            Assert.Equal(1, run.Failures);

            _launcher.Refuse = PluginRefusal.ShellElevated;
            _now += run.PauseMs;
            _manager.Tick();
            Assert.Equal(PluginStatus.Refused, run.Status);
            Assert.Equal(PluginRefusal.ShellElevated, run.Refusal);
            Assert.Empty(_launcher.Starts);

            // It never runs with PadForge's rights in its place: the start
            // waits for the user.
            _launcher.Refuse = PluginRefusal.None;
            _now += 60_000;
            _manager.Tick();
            Assert.Empty(_launcher.Starts);
            _manager.StartAgain(run);
            Assert.Single(_launcher.Starts);
        }

        // ── The key ──

        [Fact]
        public void TheKey_IsTakenOnce_OnlyFromTheProcessPadForgeStarted()
        {
            var run = AddRunning();
            var (_, variables, process) = _launcher.Starts.Single();
            string token = variables[PluginManager.TokenVariable];

            Assert.Null(_manager.AcceptToken("0" + token.Substring(1), process.Id));
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
            var settings = new AppSettingsData { Plugins = _manager.Save(), TestControllerOn = true, TestControllerSpeed = 2, ReplayOn = true, ReplayRepeat = true };
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
            Assert.Equal(2, back.TestControllerSpeed);
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
