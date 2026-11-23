using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// tools/capture_all.ps1 writes the published screenshots. These tests
    /// read the script's text and run nothing: a frame whose subject is
    /// missing is refused and counted in the exit code, a frame is never
    /// saved over a published picture before its last check, a UI
    /// Automation search that can throw is retried, and a key is sent only
    /// to the window that has the foreground.
    /// </summary>
    public class CaptureScriptRefusalTests
    {
        private static readonly string Script = File.ReadAllText(
            AuditDelta20260823Tests.FindRepoFile(Path.Combine("tools", "capture_all.ps1"))).Replace("\r\n", "\n");

        private static string Body(string name)
        {
            int at = Script.IndexOf("\nfunction " + name + " {\n", StringComparison.Ordinal);
            Assert.True(at >= 0, "no function " + name);
            return Script.Substring(at, Script.IndexOf("\n}\n", at, StringComparison.Ordinal) - at);
        }

        /// <summary>The Settings search frame is held outside the published
        /// folder until the box is read once more, and goes there only when
        /// it still holds the query.</summary>
        [Fact]
        public void TheSettingsSearchFrameReachesTheDocsOnlyAfterItsLastCheck()
        {
            string body = Body("Capture-SettingsSearch");
            Assert.DoesNotMatch(new Regex("Cap \"settings-search\"(?! -SavePath \\$held)"), body);
            Assert.Contains("$held = Join-Path $logDir ", body, StringComparison.Ordinal);
            int lastCheck = body.LastIndexOf("$read = \"$($vp.Current.Value)\"", StringComparison.Ordinal);
            int move = body.IndexOf("Move-Item", StringComparison.Ordinal);
            Assert.True(lastCheck > 0 && move > lastCheck, "the frame is moved into the docs folder before the last check of the box");
            Assert.Contains("if ($SavePath) { $SavePath } else { Join-Path $script:OutputDir \"$Name.png\" }", Body("Cap"), StringComparison.Ordinal);
        }

        /// <summary>Cap's log line names the file it wrote, so a held frame
        /// shows under its held name and not as the published picture.</summary>
        [Fact]
        public void CapLogsTheFileItWrote()
        {
            string cap = Body("Cap");
            Assert.Contains("Write-Host (\"  >> {0} ({1}KB) at {2:N1} s\" -f (Split-Path -Leaf $p), $kb,", cap, StringComparison.Ordinal);
            Assert.DoesNotContain("-f $Name, $kb", cap, StringComparison.Ordinal);
        }

        /// <summary>The empty helper the bar's More Options left is gone with
        /// its calls.</summary>
        [Fact]
        public void NoEmptyPickHelperIsLeft()
        {
            Assert.DoesNotContain("Close-PickedOptions", Script, StringComparison.Ordinal);
        }

        /// <summary>A stray dialog is declined before anything accepts it,
        /// in the order Close-AnyModal tries its buttons.</summary>
        [Fact]
        public void AStrayDialogIsDeclinedBeforeItIsAccepted()
        {
            string order = "@('^(Cancel|No)$', '^(Close|Dismiss)$', '^(OK|Done)$')";
            Assert.Contains(order, Body("Close-DialogHwnd"), StringComparison.Ordinal);
            Assert.Contains(order, Body("Close-AnyModal"), StringComparison.Ordinal);
            Assert.DoesNotContain("'^(Close|Cancel|OK|Done|Dismiss)$'", Script, StringComparison.Ordinal);
        }

        /// <summary>A clean launch that writes no settings file stops the
        /// run once the backup is back, so the owner's own settings never
        /// reach a frame.</summary>
        [Fact]
        public void ARunWhoseCleanLaunchWroteNoSettingsStops()
        {
            int at = Script.IndexOf("\"  !! PadForge.xml not regenerated after clean launch\"", StringComparison.Ordinal);
            Assert.True(at > 0, "no not-regenerated branch");
            string branch = Script.Substring(at, Script.IndexOf("\n}\n", at, StringComparison.Ordinal) - at);
            int restore = branch.IndexOf("Copy-Item $xmlBak $PadForgeXml -Force", StringComparison.Ordinal);
            int exit = branch.IndexOf("exit 1", StringComparison.Ordinal);
            Assert.True(restore >= 0 && exit > restore, "the not-regenerated branch does not end the run after the restore");
        }

        /// <summary>Every search of the main window's tree a Devices recipe
        /// makes goes through Find-AllSafe, which retries the transient COM
        /// fault. Only the state dump, inside its own try, asks the tree
        /// directly.</summary>
        [Fact]
        public void TheMainWindowTreeIsSearchedThroughTheRetry()
        {
            string select = Body("Select-DeviceByName36");
            Assert.DoesNotContain(".FindAll(", select, StringComparison.Ordinal);
            Assert.Contains("Find-AllSafe $script:uiaWin $li36", select, StringComparison.Ordinal);
            string rest = Script.Replace(Body("Write-UiaState"), "");
            Assert.DoesNotContain("$script:uiaWin.FindAll(", rest, StringComparison.Ordinal);
        }

        /// <summary>Escape goes only to the window the foreground is on.
        /// ScrollContent and Close-DialogHwnd send theirs through
        /// Send-EscapeTo, which reads the foreground window first.</summary>
        [Fact]
        public void EscapeIsSentOnlyToTheForegroundTarget()
        {
            foreach (string name in new[] { "ScrollContent", "Close-DialogHwnd" })
            {
                string body = Body(name);
                Assert.DoesNotContain("SendWait(\"{ESC}\")", body, StringComparison.Ordinal);
                Assert.Contains("Send-EscapeTo ", body, StringComparison.Ordinal);
            }
            string send = Body("Send-EscapeTo");
            int check = send.IndexOf("[Win32]::GetForegroundWindow() -ne [IntPtr]$Hwnd", StringComparison.Ordinal);
            int key = send.IndexOf("SendWait(\"{ESC}\")", StringComparison.Ordinal);
            Assert.True(check >= 0 && key > check, "Send-EscapeTo sends the key before it reads the foreground window");
        }

        /// <summary>The Workshop's stillness waits watch Get Profiles, which
        /// the art and the names land in, and not the main window, whose
        /// sidebar moves while the engine forges.</summary>
        [Fact]
        public void TheWorkshopWaitsWatchItsOwnWindow()
        {
            var waits = Regex.Matches(Body("Capture-Workshop"), @"\[UiSettle\]::WaitStable\(([^,]+),");
            Assert.Equal(2, waits.Count);
            Assert.All(waits.Cast<Match>(), m => Assert.Equal("[IntPtr]$wsDlg", m.Groups[1].Value));
        }

        /// <summary>A Workshop frame whose subject never came is refused, and
        /// so is every frame a failed step skips, so the run's exit code
        /// counts them.</summary>
        [Fact]
        public void EveryWorkshopFrameThatIsNotTakenIsRefused()
        {
            string body = Body("Capture-Workshop");
            Assert.Contains("if ($tiles.Count -gt 0) { Cap \"workshop-search\" -AllowModal }", body, StringComparison.Ordinal);
            Assert.Contains("if ($statEl) { Cap \"workshop-manifest\" -AllowModal }", body, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"^\s+Cap ""workshop-(search|manifest)""", RegexOptions.Multiline), body);
            foreach (string why in new[]
            {
                "\"the Enable Community Configs button could not be clicked\"",
                "\"Get Profiles shows no search box\"",
                "\"the store search showed no game\"",
                "\"the game lists no config\"",
                "\"the config's What Comes Across never appeared\"",
                "\"the config offers no Save and Apply\"",
            })
                Assert.Contains("Refuse-Shots ", body.Substring(0, body.IndexOf(why, StringComparison.Ordinal) + 1).Split('\n').Last(), StringComparison.Ordinal);
            Assert.DoesNotContain("Write-Host \"  !! ws search box not found\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Write-Host \"  !! Save and Apply not clickable", body, StringComparison.Ordinal);
        }

        /// <summary>Both passes take the profile dialog's frame through one
        /// recipe, which finds Polling Rate before the frame and refuses it
        /// when the row is not there.</summary>
        [Fact]
        public void BothPassesCheckThePollingRowBeforeTheFrame()
        {
            string body = Body("Capture-ProfilePollingOverride");
            int check = body.IndexOf("-eq \"Polling Rate\"", StringComparison.Ordinal);
            int cap = body.IndexOf("Cap \"profile-polling-override\" -AllowModal", StringComparison.Ordinal);
            Assert.True(check > 0 && cap > check, "the frame is taken before the Polling Rate check");
            Assert.Contains("if ($sawPolling) { Cap \"profile-polling-override\" -AllowModal }", body, StringComparison.Ordinal);
            Assert.Contains("Refuse-Shots @(\"profile-polling-override\") \"the profile dialog shows no Polling Rate row\"", body, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(Script, "Cap \"profile-polling-override\""));
            Assert.Equal(3, Regex.Matches(Script, @"(?<![\w-])Capture-ProfilePollingOverride(?![\w-])").Count);
        }

        /// <summary>Either failure of the colorways' tags toggle leaves the
        /// overlay's state unknown, since the toggle may have been pressed.</summary>
        [Fact]
        public void EitherColorwayToggleFailureMarksTheOverlayUnknown()
        {
            string body = Body("Capture-Colorways");
            foreach (string call in new[] { "Set-AnnotationOverlay -On $false", "Set-AnnotationOverlay -On $true" })
            {
                int at = body.IndexOf(call, StringComparison.Ordinal);
                Assert.True(at > 0, call);
                string branch = body.Substring(at, body.IndexOf("}", at, StringComparison.Ordinal) - at);
                Assert.Contains("$script:AnnotationUnknown = $true", branch, StringComparison.Ordinal);
            }
        }

        /// <summary>The shapes a skipped frame took before it was refused
        /// are gone from both passes: a line that says SKIPPED or SKIPPING
        /// (the staging helper's own lines aside, which skip settings and
        /// no frame), a one-line else that only logs a warning, a group
        /// opened unchecked before its frame, and a device row whose frames
        /// had no else at all. Each frame not taken goes through
        /// Refuse-Shots, which the exit code counts.</summary>
        [Fact]
        public void NoFrameIsSkippedWithoutBeingRefused()
        {
            string passes = Script.Replace(Body("Write-SlotStructures"), "");
            Assert.DoesNotMatch(new Regex(@"Write-Host ""[^""]*SKIPP(ED|ING)"), passes);
            Assert.DoesNotContain("keeping the existing", Script, StringComparison.Ordinal);
            Assert.Contains("else { Refuse-Shots @($f.Shot) \"the Pair dialog did not take the family $($f.Name)\" }", Body("Capture-PairDialog"), StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"else \{ Write-Host ""  !![^""]*"" -ForegroundColor \w+ \}"), Script);
            Assert.DoesNotMatch(new Regex(@"(?m)^[ \t]*Tab ""[^""]+""; [^\n]*\bCap "), Script);
            foreach (string refusal in new[]
            {
                "else { Refuse-Shots @(\"devices-consumer\") \"no Consumer Control row\" }",
                "Refuse-Shots @(\"midi-input\", \"midi-input-mode-devices-page\") \"no MIDI Keyboard row\"",
                "Refuse-Shots @(\"devices-nfc\", \"nfc-live-preview\", \"nfc-register\") \"no NFC reader row\"",
                "Refuse-Shots @(\"nfc-register\") \"'Register / Manage NFC Tags' button not found\"",
            })
                Assert.Contains(refusal, Script, StringComparison.Ordinal);
            int click = Script.IndexOf("Click-El $voiceBtn", StringComparison.Ordinal);
            int find = Script.IndexOf("$voiceDlg = Find-DialogHwndByEnum", click, StringComparison.Ordinal);
            int cap = Script.IndexOf("Cap \"voice-phrases\" -AllowModal", click, StringComparison.Ordinal);
            Assert.True(click > 0 && find > click && cap > find, "the Voice Macros frame is taken before the dialog is found");
            Assert.Contains("Refuse-Shots @(\"voice-phrases\") \"the Voice Macros dialog did not open\"", Script, StringComparison.Ordinal);
            Assert.Contains("if ((Select-MappedDevice \"Steam Controller\") -and (Tab \"Audio\"))", Script, StringComparison.Ordinal);
        }

        private static string Region(string from, string to)
        {
            int at = Script.IndexOf(from, StringComparison.Ordinal);
            Assert.True(at >= 0, "no " + from);
            int end = Script.IndexOf(to, at, StringComparison.Ordinal);
            Assert.True(end > at, "no " + to + " after " + from);
            return Script.Substring(at, end - at);
        }

        /// <summary>When the full run's Xbox or PlayStation slot page does
        /// not open, every frame its block takes is refused: each shot name
        /// the block names, and the shots of each recipe it calls.</summary>
        [Theory]
        [InlineData("Write-Host \"--- Xbox Slot ---\"", "# ---- 13g.", "\"the Xbox slot's page did not open\"")]
        [InlineData("$padPage = Open-SlotPage -Type 1 -Label \"PlayStation slot\"\nif ($padPage) {", "# ---- 13c. Nintendo", "\"the PlayStation slot's page did not open\"")]
        public void ASlotPageThatDoesNotOpenRefusesItsFrames(string from, string to, string why)
        {
            string block = Region(from, to);
            int els = block.LastIndexOf("\n} else {\n", StringComparison.Ordinal);
            Assert.True(els > 0, "the block has no else");
            string body = block.Substring(0, els), refusal = block.Substring(els);
            Assert.Contains("Refuse-Shots", refusal, StringComparison.Ordinal);
            Assert.Contains(why, refusal, StringComparison.Ordinal);
            foreach (Match m in Regex.Matches(body, @"""([a-z0-9]+(?:-[a-z0-9]+)+)"""))
                Assert.Contains(m.Value, refusal, StringComparison.Ordinal);
            var recipeShots = new System.Collections.Generic.Dictionary<string, string[]>
            {
                ["Capture-AnnotationOverlay"] = Array.Empty<string>(),
                ["Capture-2DAnnotationOverlay"] = new[] { "\"2d-annotation-overlay\"" },
                ["Capture-IconPicker"] = new[] { "\"icon-picker\"" },
                ["Capture-RowOptionShots"] = new[] { "\"mapping-source-shaping\"", "$script:RowOptionShots" },
                ["Capture-WheelCentering"] = new[] { "$script:WheelCenteringShots" },
                ["Capture-GyroEngage"] = new[] { "\"pad-gyro-engage\"" },
                ["Capture-MacroShots"] = new[] { "\"pad-macros\"", "\"macro-add-from-list\"", "$script:MacroShots" },
                ["Capture-MotionRow"] = new[] { "\"mapping-motion-rows\"" },
            };
            foreach (Match m in Regex.Matches(body, @"(?m)^\s*(Capture-[A-Za-z0-9]+)\b"))
            {
                Assert.True(recipeShots.ContainsKey(m.Groups[1].Value), "the test does not know the shots of " + m.Groups[1].Value);
                foreach (string shot in recipeShots[m.Groups[1].Value])
                    Assert.Contains(shot, refusal, StringComparison.Ordinal);
            }
        }

        /// <summary>A focused run that did not write a name it was given ends
        /// with exit code 1: the names join the refused shots.</summary>
        [Fact]
        public void AFocusedRunCountsTheNamesItMissed()
        {
            int at = Script.IndexOf("if ($missed.Count -gt 0) {", StringComparison.Ordinal);
            Assert.True(at > 0, "no missed branch");
            string branch = Script.Substring(at, Script.IndexOf("\n    }\n", at, StringComparison.Ordinal) - at);
            Assert.Contains("$script:RefusedShots += @($missed | Where-Object { $script:RefusedShots -notcontains $_ })", branch, StringComparison.Ordinal);
        }

        /// <summary>The run's summary counts each name not saved once, a
        /// name both refused and missed included, and names no cause it did
        /// not read.</summary>
        [Fact]
        public void TheSummaryCountsEachNameNotSavedOnce()
        {
            int at = Script.IndexOf("$notSaved = @($script:RefusedShots | Sort-Object -Unique)", StringComparison.Ordinal);
            Assert.True(at > 0, "the summary does not count the unique names");
            string summary = Script.Substring(at, Script.IndexOf("if ((Get-Count $script:StateFailures) -gt 0) {", at, StringComparison.Ordinal) - at);
            Assert.Contains("-f $notSaved.Count", summary, StringComparison.Ordinal);
            Assert.Contains("foreach ($x in $notSaved)", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("Get-Count $script:RefusedShots", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("state unknown", Script, StringComparison.Ordinal);
            Assert.DoesNotContain("refused for state", Script, StringComparison.Ordinal);
        }
    }
}
