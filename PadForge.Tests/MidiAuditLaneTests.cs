using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The MIDI lane of the 2026-10-08 audit: the text, the card and the
    /// wire word, read from the sources and the resx files where a run
    /// cannot reach them.
    /// </summary>
    public class MidiAuditLaneTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        private static string RepoFile(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

        /// <summary>The comment prose of a source file, '///' and '//'
        /// prefixes stripped and whitespace collapsed, so a sentence that
        /// wraps is matched whole.</summary>
        private static string Prose(string relative)
        {
            var lines = RepoFile(relative).Split('\n').Select(l => l.Trim())
                .Select(l => l.StartsWith("///") ? l[3..] : l.StartsWith("//") ? l[2..] : l);
            return Regex.Replace(string.Join(" ", lines), @"\s+", " ");
        }

        /// <summary>Every locale's value for <paramref name="key"/>.</summary>
        private static Dictionary<string, string> Locales(string key)
        {
            var dir = Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings");
            var values = new Dictionary<string, string>();
            // The files that hold every string. A partial translation and
            // British English take what they leave out from Strings.resx.
            foreach (var file in LocaleFiles.Complete.Select(f => Path.Combine(dir, f)))
            {
                var data = XDocument.Load(file).Root.Elements("data").FirstOrDefault(d => (string)d.Attribute("name") == key);
                values[Path.GetFileName(file)] = data?.Element("value")?.Value;
            }
            Assert.Equal(LocaleFiles.Complete.Length, values.Count);
            return values;
        }

        // ── Text ──

        /// <summary>The MIDI type button's tooltip shows when no MIDI API
        /// runs, which happens only on a service that is installed but not
        /// answering, so it no longer says MIDI requires Windows MIDI
        /// Services.</summary>
        [Fact]
        public void TheMidiNotRunningTooltip_NamesTheState_InEveryLocale()
        {
            var values = Locales("Main_MIDI_NotRunning");
            Assert.All(values.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
            Assert.DoesNotContain("requires Windows MIDI Services", values["Strings.resx"]);
            Assert.All(Locales("Main_MIDI_RequiresMidiServices").Values, Assert.Null);
            Assert.All(values.Where(kv => kv.Key != "Strings.resx"), kv => Assert.NotEqual(values["Strings.resx"], kv.Value));
        }

        /// <summary>No English string sends anyone to a driver's card in
        /// Settings. PadForge 6's Settings lists the drivers as rows of one
        /// list, under the Drivers group, and a row says why its driver is
        /// not running. The line a MIDI controller raises when no MIDI API
        /// runs still read "The Windows MIDI Services card in Settings says
        /// why." It now names the row and the group, in the words the Add a
        /// Controller tile's own line uses.</summary>
        [Fact]
        public void NoStringSendsAnyoneToADriversCard()
        {
            var dir = Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings");
            var english = XDocument.Load(Path.Combine(dir, "Strings.resx")).Root.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name"), d => d.Element("value")?.Value ?? "");
            // Positive control: the strings loaded, and the group is named.
            Assert.True(english.Count > 5000, $"only {english.Count} English strings were read");
            string drivers = english["Settings_Group_Drivers"];
            Assert.False(string.IsNullOrWhiteSpace(drivers));

            var card = new Regex(@"(Windows MIDI Services|SteamVR|HidHide|HIDMaestro|BthPS3|usbip-win2)[^.]{0,30}\bcard\b|\bcard in Settings\b");
            var wrong = english.Where(kv => card.IsMatch(kv.Value)).Select(kv => kv.Key + ": " + kv.Value).ToList();
            Assert.True(wrong.Count == 0, "these strings name a driver's card, and Settings has rows under Drivers:\n" + string.Join("\n", wrong));

            string line = english["Status_MidiNotRunning_Format"];
            Assert.Contains("{0}", line);
            Assert.Contains("Windows MIDI Services", line);
            Assert.Contains(drivers, line);
        }

        /// <summary>The install names the Visual C++ Redistributable it can
        /// put in first.</summary>
        [Theory]
        [InlineData("Settings_MidiInstallTooltip")]
        [InlineData("Status_DownloadingInstallingMidi")]
        public void TheMidiInstallText_NamesTheVisualCppRedistributable_InEveryLocale(string key)
        {
            Assert.All(Locales(key).Values, v => Assert.Contains("Visual C++", v));
        }

        /// <summary>The velocity tooltip offers 1-127, since 0 is a Note
        /// Off.</summary>
        [Fact]
        public void TheVelocityTooltip_Offers1To127_InEveryLocale()
        {
            Assert.All(Locales("Pad_MidiVelocityTooltip").Values, v =>
            {
                Assert.Contains("1-127", v);
                Assert.DoesNotContain("0-127", v);
            });
        }

        /// <summary>Every new MIDI line is in every locale, and none tells
        /// anyone to restart anything.</summary>
        [Theory]
        [InlineData("Status_MidiNotRunning_Format")]
        [InlineData("Status_MidiCreateFailed_Format")]
        [InlineData("Status_SlotCreateFailed_Format")]
        [InlineData("Status_MidiPortNotPicked_Format")]
        [InlineData("Status_MidiPortNotConnected_Format")]
        [InlineData("Status_MidiPortInUse_Format")]
        [InlineData("Status_MidiPortShared_Format")]
        [InlineData("Status_MidiPortDidNotOpen_Format")]
        [InlineData("Status_MidiReleaseTimedOut")]
        [InlineData("Status_MidiReleaseTimedOutDriver")]
        [InlineData("Status_VcRedistStillRunning")]
        [InlineData("Settings_MidiNotRunningDriver")]
        [InlineData("Settings_MidiUninstallRunning")]
        [InlineData("Settings_MidiInstallAfterUninstaller")]
        [InlineData("Settings_MidiInBoxFailed_Format")]
        public void TheNewMidiLines_AreInEveryLocale_WithNoRestartLine(string key)
        {
            var values = Locales(key);
            Assert.All(values.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
            Assert.DoesNotContain("restart", values["Strings.resx"], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reboot", values["Strings.resx"], StringComparison.OrdinalIgnoreCase);
            // Each locale words the line itself, with the same placeholders.
            static string Holes(string v) => string.Join(",", Regex.Matches(v, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(h => h));
            Assert.All(values.Where(kv => kv.Key != "Strings.resx"), kv =>
            {
                Assert.NotEqual(values["Strings.resx"], kv.Value);
                Assert.Equal(Holes(values["Strings.resx"]), Holes(kv.Value));
            });
        }

        // ── Comments that described other code ──

        /// <summary>Microsoft's projection has the native Shutdown commented
        /// out of ShutdownSdkRuntime (rc-3 and rc-4), so Dispose neither
        /// releases the runtime's DLLs nor can crash on a dying service, and
        /// the uninstall reaches its files through the Restart Manager refusal
        /// and Windows Installer's scheduled removal.</summary>
        [Fact]
        public void NoCommentClaims_DisposeReleasesTheRuntimesDlls()
        {
            foreach (var file in new[]
            {
                "PadForge.App/Common/Input/MidiBackendAppSdk.cs",
                "PadForge.App/Common/Input/MidiVirtualController.cs",
                "PadForge.App/Common/Input/MidiBackend.cs",
                "PadForge.App/MainWindow.xaml.cs",
            })
            {
                var prose = Prose(file);
                Assert.DoesNotContain("lets go of the SDK's native dlls", prose);
                Assert.DoesNotContain("crashes if the service is going away", prose);
                Assert.DoesNotContain("never has to close PadForge to reach its files", prose);
            }
        }

        /// <summary>The App SDK backend serves PadForge's own runtime build
        /// too, the recovery runs once per cooldown from three paths, and the
        /// input runtime rides whichever API the probe started.</summary>
        [Fact]
        public void TheMidiDocComments_DescribeTheCodeUnderThem()
        {
            var appSdk = Prose("PadForge.App/Common/Input/MidiBackendAppSdk.cs");
            Assert.DoesNotContain("serves existing installs only", appSdk);
            Assert.DoesNotContain("moved here unchanged", appSdk);
            Assert.DoesNotContain("It is the only path on Windows 11 24H2", appSdk);

            Assert.DoesNotContain("calls this once per process", Prose("PadForge.App/Common/Input/MidiServiceRecovery.cs"));
            var controller = Prose("PadForge.App/Common/Input/MidiVirtualController.cs");
            Assert.DoesNotContain("Recover the service once per process", controller);
            Assert.DoesNotContain("tell an API that is present but did not start from one that is missing", controller);

            var input = Prose("PadForge.App/Common/Input/MidiInputDevice.cs");
            Assert.DoesNotContain("Shared Windows MIDI Services session", input);
            Assert.DoesNotContain("null when Windows MIDI Services is unavailable", input);
            Assert.DoesNotContain("Every call here is Windows MIDI Services WinRT RPC", input);
        }

        /// <summary>Each member carries one summary describing itself: the
        /// pad-info summary sat stacked over ShutdownMidiInputs.</summary>
        [Fact]
        public void ShutdownMidiInputs_AndUpdatePadDeviceInfo_EachCarryTheirOwnSummary()
        {
            var lines = RepoFile("PadForge.App/Services/InputService.cs").Split('\n').Select(l => l.Trim()).ToList();
            int DocSummaries(string member)
            {
                int at = lines.FindIndex(l => l.StartsWith(member, StringComparison.Ordinal));
                Assert.True(at > 0, member);
                int count = 0;
                for (int i = at - 1; i >= 0 && lines[i].StartsWith("///", StringComparison.Ordinal); i--)
                    if (lines[i].Contains("<summary>")) count++;
                return count;
            }
            Assert.Equal(1, DocSummaries("public void ShutdownMidiInputs()"));
            Assert.Equal(1, DocSummaries("public void UpdatePadDeviceInfo()"));
        }

        // ── The wire word ──

        /// <summary>The word both Windows MIDI Services backends send is the
        /// one Microsoft's message builder packs (MidiMessageBuilder.cpp:
        /// type 2, group, status nibble, channel, data bytes at 7 bits), and
        /// the one MIDI input decodes.</summary>
        [Fact]
        public void TheSentWord_MatchesTheBuilderLayout()
        {
            Assert.Equal(0x20903C64u, MidiUmp.Midi1Word(Midi1Status.NoteOn, 0, 60, 100));
            Assert.Equal(0x208F3C00u, MidiUmp.Midi1Word(Midi1Status.NoteOff, 15, 60, 0));
            Assert.Equal(0x20B30764u, MidiUmp.Midi1Word(Midi1Status.ControlChange, 3, 7, 100));
            Assert.Equal(0x20B07F7Fu, MidiUmp.Midi1Word(Midi1Status.ControlChange, 0x10, 0xFF, 0xFF));

            var dev = new MidiInputDevice("word-test", "Word Test");
            dev.OnUmp(MidiUmp.Midi1Word(Midi1Status.NoteOn, 5, 60, 100), 0);
            dev.OnUmp(MidiUmp.Midi1Word(Midi1Status.ControlChange, 5, 7, 100), 0);
            var s = dev.GetCurrentState().Midi;
            Assert.True(s.Notes[60]);
            Assert.Equal(100, s.Cc[7]);
            dev.OnUmp(MidiUmp.Midi1Word(Midi1Status.NoteOff, 5, 60, 0), 0);
            Assert.False(dev.GetCurrentState().Midi.Notes[60]);
        }

        /// <summary>Building the word allocates nothing, where each send built
        /// three WinRT objects, and both backends send it as a word.</summary>
        [Fact]
        public void TheSentWord_AllocatesNothing_AndBothBackendsSendIt()
        {
            uint sink = 0;
            sink ^= MidiUmp.Midi1Word(Midi1Status.NoteOn, 0, 1, 1);
            // Counted over windows the runtime left alone: the thread's
            // count moves by itself in a full run (AllocationWindow).
            int n = 0;
            foreach (long bytes in AllocationWindow.Clean(() =>
            {
                sink ^= MidiUmp.Midi1Word(Midi1Status.ControlChange, n & 15, n & 127, n & 127);
                n++;
            }, 1_000))
                Assert.Equal(0, bytes);
            Assert.NotEqual(0x12345678u, sink);

            foreach (var file in new[] { "PadForge.App/Common/Input/MidiBackendInBox.cs", "PadForge.App/Common/Input/MidiBackendAppSdk.cs" })
            {
                var text = RepoFile(file);
                Assert.Contains("SendSingleMessageWords(0, MidiUmp.Midi1Word(status, channel, data1, data2))", text);
                Assert.DoesNotContain("BuildMidi1ChannelVoiceMessage", text);
                Assert.DoesNotContain("SendSingleMessagePacket", text);
            }
        }

        /// <summary>The description a Windows MIDI Services endpoint carries
        /// is one fixed text in both backends. It ended in "(slot N)" with
        /// the pad index plus one, which is not the number the UI shows for
        /// the slot and does not follow the slot when the order changes. The
        /// value is a plain literal: no interpolation, no concatenation and
        /// no digit.</summary>
        [Theory]
        [InlineData("PadForge.App/Common/Input/MidiBackendInBox.cs")]
        [InlineData("PadForge.App/Common/Input/MidiBackendAppSdk.cs")]
        public void TheEndpointDescription_HoldsNoNumberThatCanChange(string file)
        {
            var set = Regex.Matches(RepoFile(file), @"\bDescription\s*=\s*(?<value>[^\r\n]*?)\s*[,;]\s*\r?$", RegexOptions.Multiline);
            string value = Assert.Single(set).Groups["value"].Value;
            Assert.Matches(@"^""[^""{}\d]*""$", value);
            Assert.Equal("\"PadForge virtual MIDI controller\"", value);
        }

        // ── The build ──

        /// <summary>cswinrt.exe is not long-path aware, so the project refuses
        /// a generated folder past what its longest file allows, before the
        /// tool runs, instead of letting it crash.</summary>
        [Fact]
        public void TheProject_RefusesAGeneratedFolderPastTheCswinrtPathLimit()
        {
            var project = XDocument.Load(Path.Combine(RepoRoot(), "PadForge.App", "PadForge.App.csproj"));
            var target = project.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "Target"
                && (string)e.Attribute("BeforeTargets") == "CsWinRTGenerateProjection");
            Assert.NotNull(target);
            Assert.Equal("CsWinRTPrepareProjection", (string)target.Attribute("AfterTargets"));
            var error = target.Elements().Single(e => e.Name.LocalName == "Error");
            var condition = (string)error.Attribute("Condition");
            var m = Regex.Match(condition, @"\$\(CsWinRTGeneratedFilesDir\.Length\)\s*>\s*(\d+)");
            Assert.True(m.Success, condition);
            Assert.InRange(int.Parse(m.Groups[1].Value), 1, 210);
            Assert.Contains("shorter folder", (string)error.Attribute("Text"));
        }

        // ── The card and the install ──

        [Theory]
        [InlineData(true, true, (int)MidiApiKind.Legacy, true)]
        [InlineData(true, true, (int)MidiApiKind.None, true)]
        [InlineData(false, true, (int)MidiApiKind.Legacy, false)]
        [InlineData(true, false, (int)MidiApiKind.Legacy, false)]
        [InlineData(true, true, (int)MidiApiKind.AppSdk, false)]
        [InlineData(true, true, (int)MidiApiKind.InBox, false)]
        public void AnInstallPadForgeStoppedFollowing_SwitchesOnceTheRuntimeIsThere(bool pending, bool installed, int engine, bool expected)
        {
            Assert.Equal(expected, MidiApiSelection.SwitchAfterPendingInstall(pending, installed, (MidiApiKind)engine));
        }

        [Fact]
        public void APendingInstall_SwitchesOnce()
        {
            MidiRuntimeSwitch.ClearPendingInstall();
            try
            {
                Assert.False(MidiRuntimeSwitch.TakePendingSwitch(true, MidiApiKind.Legacy, heldForUninstall: false));
                MidiRuntimeSwitch.MarkInstallPending();
                Assert.False(MidiRuntimeSwitch.TakePendingSwitch(false, MidiApiKind.Legacy, heldForUninstall: false));
                Assert.False(MidiRuntimeSwitch.TakePendingSwitch(true, MidiApiKind.AppSdk, heldForUninstall: false));
                Assert.True(MidiRuntimeSwitch.TakePendingSwitch(true, MidiApiKind.Legacy, heldForUninstall: false));
                Assert.False(MidiRuntimeSwitch.TakePendingSwitch(true, MidiApiKind.Legacy, heldForUninstall: false));
            }
            finally { MidiRuntimeSwitch.ClearPendingInstall(); }
        }

        /// <summary>One handler of the window's constructor, from its
        /// subscription up to the next subscription, so a check on one
        /// handler never reads into the one after it.</summary>
        private static string Handler(string text, string name, string next)
        {
            int start = text.IndexOf(name + " += async", StringComparison.Ordinal);
            Assert.True(start >= 0, name);
            int end = text.IndexOf(next, start, StringComparison.Ordinal);
            Assert.True(end > start, next);
            return text[start..end];
        }

        /// <summary>The arguments of the first call to
        /// <paramref name="method"/> in <paramref name="source"/>, split at
        /// the commas of the call's own list and not at any inside a lambda
        /// or a nested call.</summary>
        private static List<string> CallArguments(string source, string method)
        {
            int open = source.IndexOf(method + "(", StringComparison.Ordinal);
            Assert.True(open >= 0, method);
            open += method.Length;
            var arguments = new List<string>();
            int depth = 0, start = open + 1;
            for (int i = open; i < source.Length; i++)
            {
                char c = source[i];
                if (c is '(' or '{' or '[')
                {
                    depth++;
                }
                else if (c is ')' or '}' or ']')
                {
                    if (--depth > 0) continue;
                    arguments.Add(source[start..i].Trim());
                    return arguments;
                }
                else if (c == ',' && depth == 1)
                {
                    arguments.Add(source[start..i].Trim());
                    start = i + 1;
                }
            }
            Assert.Fail(method + " is never closed");
            return arguments;
        }

        /// <summary>The install's switch and the uninstall's release run on
        /// the operation's worker, not the UI thread, which waited there on a
        /// probe hung on a dead service. The operation is the second argument
        /// of RunDriverOperationAsync. The third runs on the UI thread once
        /// the worker is done, and is the card's refresh and nothing else:
        /// with the switch moved back into it, the window froze behind a hung
        /// probe after an install again. An install PadForge stops following
        /// is marked for the card's refresh, which switches off the UI
        /// thread.</summary>
        [Fact]
        public void TheCardsMidiOperations_WaitOnNothingFromTheUiThread()
        {
            var text = RepoFile("PadForge.App/MainWindow.xaml.cs");
            string[] waits =
            {
                "SwitchMidiApi", "ReleaseForUninstall", "SuppressForUninstall", "TryHoldWithoutRelease",
                "ShutdownMidiInputs", "ResetAvailability", "EndUninstall",
            };

            var installHandler = Handler(text, "InstallMidiRuntimeRequested", "UninstallMidiServicesRequested += async");
            var install = CallArguments(installHandler, "RunDriverOperationAsync");
            Assert.Equal(3, install.Count);
            Assert.StartsWith("() => Common.Input.MidiRuntimeSwitch.RunInstall(", install[1]);
            Assert.Contains("DriverInstaller.InstallMidiRuntime", install[1]);
            Assert.Contains("() => _inputService?.SwitchMidiApi()", install[1]);
            Assert.Equal("RefreshMidiServicesStatus", install[2]);
            Assert.All(waits, wait => Assert.DoesNotContain(wait, installHandler.Replace(install[1], string.Empty)));

            var uninstallHandler = Handler(text, "UninstallMidiServicesRequested", "_deviceService.WireEvents();");
            var uninstall = CallArguments(uninstallHandler, "RunDriverOperationAsync");
            Assert.Equal(3, uninstall.Count);
            Assert.StartsWith("() => Common.Input.MidiRuntimeSwitch.RunUninstall(", uninstall[1]);
            Assert.Contains("DriverInstaller.UninstallMidiRuntime(out var exited)", uninstall[1]);
            Assert.Contains("() => _inputService?.ShutdownMidiInputs()", uninstall[1]);
            Assert.Contains("() => _inputService?.ResumeMidiInputs()", uninstall[1]);
            Assert.Equal("RefreshMidiServicesStatus", uninstall[2]);
            Assert.All(waits, wait => Assert.DoesNotContain(wait, uninstallHandler.Replace(uninstall[1], string.Empty)));

            Assert.Contains("Task.Run(() => _inputService?.SwitchMidiApi())", text);
            Assert.Contains("MidiVirtualController.ShutdownForExit()", text);
            // The card learns whether an uninstaller still runs, so it
            // offers no second Uninstall meanwhile.
            Assert.Contains("_viewModel.Settings.MidiUninstallerRunning = Common.Input.MidiRuntimeSwitch.UninstallerRunning;", text);
        }

        /// <summary>The Install button takes its tooltip from the card, which
        /// says Install waits for an uninstaller still running, and shows it
        /// while the button is off. A tooltip on a disabled WPF button shows
        /// only when asked to.</summary>
        [Fact]
        public void TheInstallButton_SaysWhyItWaits_WhileItIsOff()
        {
            var text = RepoFile("PadForge.App/Views/SettingsPage.xaml");
            int id = text.IndexOf("AutomationProperties.AutomationId=\"MidiRuntimeInstallButton\"", StringComparison.Ordinal);
            Assert.True(id > 0);
            int start = text.LastIndexOf("<Button ", id, StringComparison.Ordinal);
            int end = text.IndexOf("/>", id, StringComparison.Ordinal);
            string button = text[start..end];
            Assert.Contains("Command=\"{Binding InstallMidiRuntimeCommand}\"", button);
            Assert.Contains("ToolTip=\"{Binding MidiInstallTooltip}\"", button);
            Assert.Contains("ToolTipService.ShowOnDisabled=\"True\"", button);
        }

        [Fact]
        public void AnotherInBoxActivationFailure_IsReported_AndShownOnTheCard()
        {
            const int AccessDenied = unchecked((int)0x80070005);
            Assert.Equal(MidiApiKind.None, MidiApiSelection.Choose(26200, () => AccessDenied, () => true, out int failure));
            Assert.Equal(AccessDenied, failure);
            Assert.Equal(MidiApiKind.InBox, MidiApiSelection.Choose(26200, () => 0, () => true, out failure));
            Assert.Equal(0, failure);
            Assert.Equal(MidiApiKind.AppSdk, MidiApiSelection.Choose(26200, () => MidiApiSelection.REGDB_E_CLASSNOTREG, () => true, out failure));
            Assert.Equal(0, failure);

            var vm = new SettingsViewModel { HasAnyMidiSlots = () => false };
            vm.ActiveMidiApi = MidiApiKind.Legacy;
            Assert.Equal(Strings.Instance.Settings_MidiLegacy, vm.MidiServicesDetailText);
            vm.MidiInBoxFailure = AccessDenied;
            Assert.Contains("80070005", vm.MidiServicesDetailText);
        }

        /// <summary>While an uninstall holds MIDI released, nothing runs, so
        /// the card reads Not Running with the uninstaller's line, not the
        /// registry's prediction with a lit flame. A probe hung in a classic
        /// driver claims no service restart.</summary>
        [Fact]
        public void TheCard_SaysWhyNothingRuns()
        {
            Assert.Equal((MidiApiKind.None, true), MidiApiSelection.ForCard(MidiApiKind.AppSdk, MidiApiKind.None, false, suppressedForUninstall: true));
            Assert.Equal((MidiApiKind.AppSdk, false), MidiApiSelection.ForCard(MidiApiKind.AppSdk, MidiApiKind.None, false));

            var vm = new SettingsViewModel { HasAnyMidiSlots = () => false };
            vm.ActiveMidiApi = MidiApiKind.None;
            vm.MidiApiNotStarted = true;
            Assert.Equal(Strings.Instance.Settings_MidiNotRunning, vm.MidiServicesDetailText);
            vm.MidiNotRunningReason = MidiNotRunningReason.Uninstalling;
            Assert.Equal(Strings.Instance.Settings_MidiUninstallRunning, vm.MidiServicesDetailText);
            vm.MidiNotRunningReason = MidiNotRunningReason.DriverDidNotRespond;
            Assert.Equal(Strings.Instance.Settings_MidiNotRunningDriver, vm.MidiServicesDetailText);
        }
    }

    /// <summary>The legacy output port through a profile switch: a None
    /// picked for a profile is that profile's choice, and a profile that
    /// never picked a port records none, where the outgoing profile's port
    /// leaked into it and was saved there.</summary>
    [Collection("SettingsManagerStatics")]
    public class MidiProfilePortTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _savedProfiles = SettingsManager.Profiles;
        private readonly string _savedActive = SettingsManager.ActiveProfileId;
        private readonly bool[] _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly MappingSet[] _savedSets = SettingsManager.SlotMappingSets;
        private readonly List<int> _savedXbox = SettingsManager.XboxSlotOrder;
        private readonly List<int> _savedPs = SettingsManager.PlayStationSlotOrder;
        private readonly List<int> _savedExt = SettingsManager.ExtendedSlotOrder;
        private readonly List<int> _savedKbm = SettingsManager.KeyboardMouseSlotOrder;
        private readonly List<int> _savedMidi = SettingsManager.MidiSlotOrder;
        private readonly ProfileData _savedPendingDefault = SettingsManager.PendingDefaultSnapshot;
        private readonly Action _savedAfterRefresh = SettingsService.AfterMappingSetsRefreshed;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.ActiveProfileId = _savedActive;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedSets;
            SettingsManager.XboxSlotOrder = _savedXbox;
            SettingsManager.PlayStationSlotOrder = _savedPs;
            SettingsManager.ExtendedSlotOrder = _savedExt;
            SettingsManager.KeyboardMouseSlotOrder = _savedKbm;
            SettingsManager.MidiSlotOrder = _savedMidi;
            SettingsManager.PendingDefaultSnapshot = _savedPendingDefault;
            SettingsService.AfterMappingSetsRefreshed = _savedAfterRefresh;
        }

        private static (MainViewModel vm, InputService svc) ArrangeMidiSlot()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.PendingDefaultSnapshot = null;
            SettingsManager.XboxSlotOrder = new List<int>();
            SettingsManager.PlayStationSlotOrder = new List<int>();
            SettingsManager.ExtendedSlotOrder = new List<int>();
            SettingsManager.KeyboardMouseSlotOrder = new List<int>();
            SettingsManager.MidiSlotOrder = new List<int> { 0 };
            var vm = new MainViewModel();
            vm.Pads[0].OutputType = VirtualControllerType.Midi;
            var svc = new InputService(vm) { SettingsService = new SettingsService(vm) };
            return (vm, svc);
        }

        private static ProfileData Profile(string port)
        {
            var created = new bool[InputManager.MaxPads];
            var enabled = new bool[InputManager.MaxPads];
            created[0] = enabled[0] = true;
            return new ProfileData
            {
                Id = "b",
                Name = "Profile B",
                SlotCreated = created,
                SlotEnabled = enabled,
                SlotControllerTypes = Enumerable.Repeat((int)VirtualControllerType.Midi, InputManager.MaxPads).ToArray(),
                MidiConfigs = new[] { new MidiSlotConfigData { SlotIndex = 0, OutputPort = port } },
            };
        }

        [Theory]
        [InlineData(null, "Synth", null)]
        [InlineData("", "", "")]
        [InlineData("loopMIDI Port", "loopMIDI Port", "loopMIDI Port")]
        public void TheIncomingProfilesChoice_IsWhatItKeeps(string incoming, string livePort, string saved)
        {
            var (vm, svc) = ArrangeMidiSlot();
            vm.Pads[0].MidiConfig.ChooseOutputPort("Synth");

            svc.ApplyProfile(Profile(incoming));

            Assert.Equal(livePort, vm.Pads[0].MidiConfig.OutputPort);
            var snapshot = svc.SnapshotCurrentProfile();
            Assert.Equal(saved, Assert.Single(snapshot.MidiConfigs).OutputPort);
        }
    }
}
