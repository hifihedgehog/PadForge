using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// External control over a named pipe (#366, asked in discussion #363).
    ///
    /// <para>A launcher (Playnite, LaunchBox) or a script activates and
    /// deactivates profiles without PadForge's UI or window focus. The pipe
    /// carries a fixed ASCII grammar, never localized, and an externally
    /// activated profile is PINNED so the foreground monitor cannot clobber
    /// a scripted choice.</para>
    /// </summary>
    public class ExternalControlTests
    {
        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        /// <summary>The wire itself: a client writes one line and reads one
        /// response, and the injected executor sees exactly what was sent.
        /// Runs the real server, so the pipe ACL, the framing, and the
        /// per-connection lifecycle are all exercised.</summary>
        [Fact]
        public async Task ThePipeCarriesOneCommandAndOneResponse()
        {
            string seen = null;
            string pipe = UniquePipeName("carries");
            using var svc = new ExternalControlService(cmd =>
            {
                seen = cmd;
                return "ok TestProfile";
            }, pipe);
            svc.Start();

            string response = await SendAsync(pipe, "activate TestProfile");

            Assert.Equal("activate TestProfile", seen);
            Assert.Equal("ok TestProfile", response);
        }

        /// <summary>One bad connection never kills the accept loop: a client
        /// that throws inside the executor still gets an answer, and the NEXT
        /// client is served normally.</summary>
        [Fact]
        public async Task AFailedCommandDoesNotKillTheServer()
        {
            int calls = 0;
            string pipe = UniquePipeName("failed");
            using var svc = new ExternalControlService(cmd =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("boom");
                return "ok second";
            }, pipe);
            svc.Start();

            string first = await SendAsync(pipe, "activate Boom");
            string second = await SendAsync(pipe, "activate Fine");

            Assert.Equal("error internal", first);
            Assert.Equal("ok second", second);
        }

        /// <summary>Every request gets a response, an empty line included.
        /// A client that gets nothing back blocks in its own read until the
        /// pipe closes, which reads as a hung launcher rather than a rejected
        /// command. Caught on live hardware: an earlier guard skipped the
        /// write for empty input and a bare newline hung the client.</summary>
        [Fact]
        public async Task AnEmptyCommandStillAnswers()
        {
            string pipe = UniquePipeName("empty");
            using var svc = new ExternalControlService(cmd =>
                string.IsNullOrWhiteSpace(cmd) ? "error empty" : "ok", pipe);
            svc.Start();

            Assert.Equal("error empty", await SendAsync(pipe, ""));
            Assert.Equal("ok", await SendAsync(pipe, "query"));   // loop survives it
        }

        /// <summary>The read is bounded: a client that never sends a newline
        /// cannot grow the server's buffer without limit.</summary>
        [Fact]
        public void TheRequestReadIsCapped()
        {
            string src = RepoText("PadForge.App", "Services", "ExternalControlService.cs");
            Assert.Contains("sb.Length < 1024", src);
        }

        /// <summary>Authenticated Users get read-write on the pipe, which is
        /// what lets an UNELEVATED launcher drive an elevated PadForge
        /// (app.manifest requires administrator). Without this rule the whole
        /// feature is unreachable from Playnite or LaunchBox running normally.
        /// Mirrors Lenovo Legion Toolkit's IpcServer.</summary>
        [Fact]
        public void ThePipeGrantsAuthenticatedUsers()
        {
            string src = RepoText("PadForge.App", "Services", "ExternalControlService.cs");
            Assert.Contains("WellKnownSidType.AuthenticatedUserSid", src);
            Assert.Contains("PipeAccessRights.ReadWrite", src);
            Assert.Contains("NamedPipeServerStreamAcl.Create", src);
        }

        /// <summary>The command grammar is fixed ASCII. Localizing a machine
        /// interface would break every script the moment the user changes
        /// language, so the verbs and responses must not read from Strings.</summary>
        [Fact]
        public void TheGrammarIsNeverLocalized()
        {
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = svc.IndexOf("internal string ExecuteExternalControlCommand", StringComparison.Ordinal);
            Assert.True(at > 0);
            int end = svc.IndexOf("private void StartDsuServerIfEnabled", at, StringComparison.Ordinal);
            Assert.True(end > at);
            string body = svc.Substring(at, end - at);

            Assert.Contains("case \"activate\":", body);
            Assert.Contains("case \"deactivate\":", body);
            Assert.Contains("case \"query\":", body);
            Assert.Contains("error unknown-command", body);
            Assert.Contains("error unknown-profile", body);
            // Status text is localized (it is UI), but no RESPONSE may be.
            Assert.DoesNotContain("return Strings.Instance", body);
        }

        /// <summary>The pin is the point (#363's use case): the same exe is
        /// launched two ways and must keep the profile the launcher chose, so
        /// the foreground monitor early-returns while a pin is held. Without
        /// this, focusing the game fires a foreground match (or the no-match
        /// default revert) and undoes the script.</summary>
        [Fact]
        public void AnExternallyActivatedProfileIsPinnedAgainstTheForegroundMonitor()
        {
            string mon = RepoText("PadForge.App", "Services", "ForegroundMonitorService.cs");
            int at = mon.IndexOf("public void CheckForegroundWindow()", StringComparison.Ordinal);
            Assert.True(at > 0);
            string head = mon.Substring(at, 700);
            Assert.Contains("SettingsManager.ExternalProfilePinActive", head);
            Assert.Contains("return;", head);

            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("SettingsManager.ExternalProfilePinActive = true;", svc);
        }

        /// <summary>The user outranks the script: any manual switch releases
        /// the pin, through the one choke point every manual lane already
        /// calls.</summary>
        [Fact]
        public void AManualSwitchReleasesThePin()
        {
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = svc.IndexOf("public void NoteManualProfileSwitch()", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = svc.Substring(at, 420);
            Assert.Contains("ExternalProfilePinActive = false", body);
        }

        /// <summary>EVERY manual lane calls that choke point, which is the
        /// half the sibling test above assumed rather than checked. There are
        /// four ways a user switches profiles by hand: the status-bar
        /// switcher, the Profiles page Load button, Revert to Default, and a
        /// controller shortcut. Only the switcher called
        /// NoteManualProfileSwitch. The Load and Revert buttons noted nothing
        /// at all, so auto-switch could yank the profile straight back, and
        /// the shortcut lane set the override directly and left the external
        /// pin standing, so the checkbox tooltip's promise that switching
        /// profiles yourself releases a scripted hold was false on two of the
        /// four. All four go through the one funnel now.</summary>
        [Fact]
        public void EveryManualSwitchLaneGoesThroughThatChokePoint()
        {
            string mw = RepoText("PadForge.App", "MainWindow.xaml.cs");
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");

            // The Profiles page Load button, before the switch.
            int load = mw.IndexOf("private void OnLoadProfile(object sender, EventArgs e)", StringComparison.Ordinal);
            Assert.True(load > 0);
            string loadBody = mw.Substring(load, 900);
            int loadNote = loadBody.IndexOf("_inputService.NoteManualProfileSwitch();", StringComparison.Ordinal);
            int loadApply = loadBody.IndexOf("_inputService.LoadProfile(", StringComparison.Ordinal);
            Assert.True(loadNote > 0 && loadApply > loadNote);

            // Revert to Default, before the switch.
            int revert = mw.IndexOf("private void OnRevertToDefault(object sender, EventArgs e)", StringComparison.Ordinal);
            Assert.True(revert > 0);
            string revertBody = mw.Substring(revert, 600);
            int revertNote = revertBody.IndexOf("_inputService.NoteManualProfileSwitch();", StringComparison.Ordinal);
            int revertApply = revertBody.IndexOf("_inputService.RevertToDefaultProfile();", StringComparison.Ordinal);
            Assert.True(revertNote > 0 && revertApply > revertNote);

            // The status-bar switcher, which always did.
            int sw = mw.IndexOf("private void ActivateProfileFromSwitcher(", StringComparison.Ordinal);
            Assert.True(sw > 0);
            Assert.Contains("_inputService.NoteManualProfileSwitch();", mw.Substring(sw, 900));

            // The controller shortcut lane takes the funnel rather than
            // poking the foreground monitor and skipping the pin release.
            int tick = svc.IndexOf("string pendingSwitch = _inputManager.PendingProfileSwitchId;", StringComparison.Ordinal);
            Assert.True(tick > 0);
            string tickBody = svc.Substring(tick, 1400);
            Assert.Contains("NoteManualProfileSwitch();", tickBody);
            Assert.DoesNotContain("_foregroundMonitor.SetManualOverride(SettingsManager.ActiveProfileId);", tickBody);
        }

        /// <summary>The pin is runtime-only. Persisting it would leave a user
        /// stuck on a scripted profile after a restart with no UI that
        /// explains why.</summary>
        [Fact]
        public void ThePinIsNeverPersisted()
        {
            string settings = RepoText("PadForge.App", "Services", "SettingsService.cs");
            int at = settings.IndexOf("public bool EnableExternalControl { get; set; }", StringComparison.Ordinal);
            Assert.True(at > 0);   // the OPT-IN persists
            Assert.DoesNotContain("public bool ExternalProfilePinActive { get; set; }", settings);
            Assert.DoesNotContain("ExternalProfilePinActive =", settings.Substring(
                settings.IndexOf("class AppSettingsData", StringComparison.Ordinal)));
        }

        /// <summary>The opt-in rides all four persistence sites its sibling
        /// EnableAutoProfileSwitching rides: load (both mirrors), the
        /// static sync on save, the saved record, and reset-to-defaults.
        /// A missing site is how a global toggle silently fails to
        /// round-trip.</summary>
        [Fact]
        public void TheOptInRidesEverySiblingSite()
        {
            string src = RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("vm.EnableExternalControl = appSettings.EnableExternalControl;", src);
            Assert.Contains("SettingsManager.EnableExternalControl = appSettings.EnableExternalControl;", src);
            Assert.Contains("SettingsManager.EnableExternalControl = vm.EnableExternalControl;", src);
            Assert.Contains("EnableExternalControl = vm.EnableExternalControl,", src);
            Assert.Contains("SettingsManager.EnableExternalControl = false;", src);
        }

        /// <summary>Default OFF. The pipe is an attack surface the user did
        /// not ask for until they check the box, and every pre-#366 settings
        /// file must deserialize to off.</summary>
        [Fact]
        public void ExternalControlIsOffUntilAsked()
        {
            var data = new PadForge.Services.AppSettingsData();
            Assert.False(data.EnableExternalControl);
        }

        [Theory]
        [InlineData(new[] { "--profile", "Racing" }, "activate Racing")]
        [InlineData(new[] { "--default-profile" }, "deactivate")]
        [InlineData(new[] { "--PROFILE", "Mixed Case Name" }, "activate Mixed Case Name")]
        [InlineData(new[] { "--minimized" }, null)]
        [InlineData(new[] { "--profile" }, null)]          // missing value
        [InlineData(new string[0], null)]
        public void TheCommandLineMapsOntoTheSameGrammar(string[] args, string expected)
        {
            Assert.Equal(expected, PadForge.App.ParseProfileCommand(args));
        }

        /// <summary>A second instance carrying a profile argument forwards it
        /// and exits, instead of showing the "already running" box. A launcher
        /// calling PadForge.exe repeatedly must never stack modal dialogs on
        /// the user's screen.</summary>
        [Fact]
        public void ASecondInstanceForwardsInsteadOfNagging()
        {
            string app = RepoText("PadForge.App", "App.xaml.cs");
            int at = app.IndexOf("out bool isNewInstance", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = app.Substring(at, 900);
            Assert.Contains("ParseProfileCommand(e.Args)", body);
            Assert.Contains("TryForwardExternalCommand", body);
            // The forward path shuts down BEFORE the message box.
            int forward = body.IndexOf("TryForwardExternalCommand", StringComparison.Ordinal);
            int box = body.IndexOf("MessageBox.Show", StringComparison.Ordinal);
            Assert.True(forward < box);
        }

        /// <summary>A pipe name is machine-global, so every wire test serves
        /// its OWN name. Serving the production name would put the test client
        /// in a race with a running PadForge on the same machine (observed:
        /// the client reached the real app and read the real app's reply).</summary>
        private static string UniquePipeName(string tag)
            => "PadForge.Control.Test." + tag + "." + Environment.ProcessId;

        private static async Task<string> SendAsync(string pipeName, string command)
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            // The server may still be creating its first instance.
            for (int attempt = 0; ; attempt++)
            {
                try { await client.ConnectAsync(2000); break; }
                catch when (attempt < 4) { await Task.Delay(50); }
            }

            byte[] outBytes = Encoding.UTF8.GetBytes(command + "\n");
            await client.WriteAsync(outBytes);
            await client.FlushAsync();

            var sb = new StringBuilder();
            var one = new byte[1];
            while (sb.Length < 1024)
            {
                int n = await client.ReadAsync(one.AsMemory(0, 1));
                if (n == 0 || one[0] == (byte)'\n') break;
                sb.Append((char)one[0]);
            }
            return sb.ToString();
        }

        /// <summary>The pipe starter is engine-gated like its DSU sibling:
        /// the docs promise the pipe "while the engine is running", and an
        /// un-gated starter let a checkbox toggle arm it engine-stopped
        /// while an engine stop always killed it (audit C4).</summary>
        [Fact]
        public void ThePipeStarterIsEngineGated()
        {
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = svc.IndexOf("private void StartExternalControlIfEnabled()", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = svc.Substring(at, 700);
            Assert.Contains("_inputManager == null", body);
        }

        /// <summary>Releasing the pin drops the foreground monitor's dedup
        /// cache. While pinned the check returns before reading the exe
        /// path, so the cache holds pre-pin state; without the drop, a
        /// matched game left focused through a deactivate never re-fires
        /// its rule until focus changes (audit C5).</summary>
        [Fact]
        public void PinReleaseInvalidatesTheForegroundCache()
        {
            var mon = new PadForge.Services.ForegroundMonitorService();
            var t = typeof(PadForge.Services.ForegroundMonitorService);
            t.GetField("_lastExePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(mon, @"C:\game.exe");
            t.GetField("_lastMatchedProfileId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(mon, "some-profile");

            mon.InvalidateCache();

            Assert.Null(mon.LastForegroundExePath);
            Assert.Null(mon.LastMatchedProfileId);

            // Both pin-release sites call it: the external deactivate and
            // the pipe teardown.
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            int deact = svc.IndexOf("private string ExternalDeactivate()", StringComparison.Ordinal);
            Assert.True(deact > 0);
            Assert.Contains("InvalidateCache", svc.Substring(deact, 900));
            int stop = svc.IndexOf("private void StopExternalControl()", StringComparison.Ordinal);
            Assert.True(stop > 0);
            Assert.Contains("InvalidateCache", svc.Substring(stop, 700));
        }
    }
}
