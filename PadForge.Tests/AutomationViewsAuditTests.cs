using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using PadForge.Engine.Data;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The Automation and Plugins views of the Extras page and their view
    /// models: the markup's brushes, ids and reset buttons, and the rows'
    /// answers to what the user picks.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationViewsAuditTests
    {
        private static XElement Extras() =>
            XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml")).Root;

        private static string At(XElement e, string name) =>
            (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == name);

        /// <summary>A theme change puts new line brushes into the
        /// application's resources. A row that took one by StaticResource
        /// keeps the old ground's line, so every Steel brush on the page is
        /// taken by DynamicResource.</summary>
        [Fact]
        public void ExtrasPage_TakesNoSteelBrushByStaticResource()
        {
            string page = ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml");
            Assert.DoesNotMatch(@"\{StaticResource Steel\w*Brush\}", page);
        }

        /// <summary>UI Automation lists a collapsed button too, so two
        /// buttons of one template with one id make a search by id land on
        /// the one that does not show.</summary>
        [Fact]
        public void NoTemplateOnExtras_BindsOneAutomationIdTwice()
        {
            foreach (var template in Extras().Descendants().Where(e => e.Name.LocalName == "DataTemplate"))
            {
                var ids = template.Descendants()
                    .Where(e => !e.Ancestors().TakeWhile(a => a != template).Any(a => a.Name.LocalName == "DataTemplate"))
                    .Select(e => At(e, "AutomationProperties.AutomationId"))
                    .Where(id => id != null && id.StartsWith("{Binding", StringComparison.Ordinal))
                    .ToList();
                var twice = ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                Assert.True(twice.Count == 0, "A template on Extras binds " + string.Join(", ", twice) + " twice");
            }
        }

        /// <summary>Every Reset All on Extras has the reset look and a
        /// tooltip that names its section (rule 1), which its screen-reader
        /// name is built from.</summary>
        [Fact]
        public void EveryResetAllOnExtras_HasTheResetLook_AndAResetTooltip()
        {
            var resets = Extras().Descendants()
                .Where(e => At(e, "Content") is string c && c.Contains("Path=Pad_ResetAll", StringComparison.Ordinal)
                    || At(e, "Content") is string d && d.StartsWith("{Binding Pad_ResetAll", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(resets);
            foreach (var reset in resets)
            {
                string id = At(reset, "AutomationProperties.AutomationId") ?? At(reset, "Command");
                Assert.True(At(reset, "Style") == "{StaticResource ResetAllButton}", id + " has no ResetAllButton look");
                bool tip = reset.Elements().Any(c => c.Name.LocalName.EndsWith(".ToolTip", StringComparison.Ordinal)
                    && c.Descendants().Any(b => At(b, "Path") == "Pad_ResetSection_Format"));
                Assert.True(tip, id + " has no Reset tooltip");
            }
        }

        /// <summary>A reach box hands its row only its two choices. Any
        /// other index, the -1 of a box whose list was replaced, changes
        /// nothing.</summary>
        [Fact]
        public void AReachRow_TakesOnlyItsTwoChoices()
        {
            var set = new List<(string, ProgramReach)>();
            var program = new AllowedProgramItem(new AllowedProgramView("k", "Pedal Curve", "python.exe", AutomationLevel.Read, null, false),
                _ => { }, (k, r) => set.Add((k, r)));
            program.SelectedReach = -1;
            program.SelectedReach = 2;
            var key = new NetworkKeyItem("n", "Tablet", "Line", true, _ => { }, null, (k, r) => set.Add((k, r)));
            key.SelectedReach = -1;
            var web = new WebAllowedItem("w", "https://example.com", "Line", _ => { }, null, (k, r) => set.Add((k, r)));
            web.SelectedReach = -1;
            Assert.Empty(set);
            program.SelectedReach = 1;
            Assert.Equal(("k", ProgramReach.OwnDevices), set.Single());
        }

        /// <summary>A program's ask to show a place is answered on the page
        /// in the words the request card gives it: Not Now and Show. A change
        /// keeps Don't Allow and Allow.</summary>
        [Fact]
        public void AWaitingShow_IsAnsweredNotNowAndShow_OnThePage()
        {
            var s = Strings.Instance;
            var show = new AutomationRequestItem(new PendingApproval(1,
                new ApprovalRequest(AutomationProgram.Unknown, "Show", new[] { "Line" }) { Kind = ApprovalKind.Show },
                DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(2)), (_, _) => true);
            Assert.Equal(s.Automation_Request_NotNow, show.DenyWords);
            Assert.Equal(s.Automation_Request_Show, show.AllowWords);
            var change = new AutomationRequestItem(new PendingApproval(2,
                new ApprovalRequest(AutomationProgram.Unknown, "Import", new[] { "Line" }),
                DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(2)), (_, _) => true);
            Assert.Equal(s.Automation_Request_Deny, change.DenyWords);
            Assert.Equal(s.Automation_Request_Allow, change.AllowWords);

            var card = Extras().Descendants().Single(e => At(e, "Name") == "AutomationWaitingCard");
            var deny = card.Descendants().Single(e => At(e, "AutomationProperties.AutomationId") == "{Binding DenyId}");
            var allow = card.Descendants().Single(e => At(e, "AutomationProperties.AutomationId") == "{Binding AllowId}");
            Assert.Equal("{Binding DenyWords}", At(deny, "Content"));
            Assert.Equal("{Binding AllowWords}", At(allow, "Content"));
        }

        // ── The rows keep their place while the user works in them ──

        internal static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new System.Threading.Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(20000), "STA test body timed out");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>The web card reads its view model again each second. A
        /// Tunnel ID the user is typing, which goes to the store when the box
        /// is left, stays in the box through those reads.</summary>
        [Fact]
        public void ATunnelIdBeingTyped_StaysThroughTheCardsReads()
        {
            RunSta(() =>
            {
                using var rig = new AssistantRig();
                var store = new PadForge.Services.Automation.Web.WebAccessStore();
                using var door = new PadForge.Services.Automation.Web.WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName);
                var view = new WebAccessViewModel(door, store);
                var box = new System.Windows.Controls.TextBox();
                box.SetBinding(System.Windows.Controls.TextBox.TextProperty, new System.Windows.Data.Binding(nameof(WebAccessViewModel.TunnelIdText))
                {
                    Source = view,
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus,
                });
                box.Text = "tunnel_typed";
                view.Refresh();
                view.Refresh();
                Assert.Equal("tunnel_typed", box.Text);
                // Left, the box hands it to the store, and a later read keeps it.
                box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty).UpdateSource();
                Assert.Equal("tunnel_typed", store.TunnelId);
                view.Refresh();
                Assert.Equal("tunnel_typed", box.Text);
            });
        }

        private sealed class ReachConsent : IAutomationConsent
        {
            public readonly List<AllowedProgramView> List = new();
            public IReadOnlyList<AllowedProgramView> Allowed => List.ToList();
            public event EventHandler Changed;
            public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
            public void Remove(string key) { List.RemoveAll(p => p.Key == key); Raise(); }
            public void AllowAssistant(string hostKey, string hostName, string program) { }
            public void ForgetAssistant(string hostKey, string program) { }
            public void SetReach(string key, ProgramReach reach)
            {
                int i = List.FindIndex(p => p.Key == key);
                List[i] = List[i] with { Reach = reach };
                Raise();
            }
        }

        /// <summary>A reach picked in an Allowed Programs row, and a program
        /// that connects or leaves, leave every row in place: the box the user
        /// works in is the same one after the store's change. A program that
        /// leaves the list takes its row, and one that joins gets its own.</summary>
        [Fact]
        public void AllowedPrograms_KeepTheirRows_ThroughTheStoresChanges()
        {
            var vm = new DashboardViewModel { AssistantIconSeam = _ => null };
            var consent = new ReachConsent();
            consent.List.Add(new AllowedProgramView("a", "Steer Assist", "AutoHotkey64.exe", AutomationLevel.SendInputs, null, false));
            consent.List.Add(new AllowedProgramView("b", "Pedal Curve", "python.exe", AutomationLevel.Read, null, false));
            vm.AttachConsent(consent);
            var a = vm.AllowedPrograms[0];
            var b = vm.AllowedPrograms[1];
            var changed = new List<string>();
            if ((object)a is System.ComponentModel.INotifyPropertyChanged notify) notify.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            a.SelectedReach = 1;
            Assert.Same(a, vm.AllowedPrograms[0]);
            Assert.Equal(1, a.SelectedReach);
            Assert.True(a.IsLimited);
            Assert.Contains(nameof(AllowedProgramItem.SelectedReach), changed);
            Assert.Contains(nameof(AllowedProgramItem.Line), changed);
            Assert.Contains("Drives its own devices alone.", a.Line);
            // Back to all of PadForge from the same row.
            a.SelectedReach = 0;
            Assert.Null(consent.List[0].Reach);
            Assert.Equal(0, a.SelectedReach);

            consent.List[1] = consent.List[1] with { ConnectedNow = true };
            consent.List.Add(new AllowedProgramView("c", "New", "new.exe", AutomationLevel.ChangeSettings, null, false));
            consent.Raise();
            Assert.Same(a, vm.AllowedPrograms[0]);
            Assert.Same(b, vm.AllowedPrograms[1]);
            Assert.EndsWith(Strings.Instance.Automation_Allowed_ConnectedNow, b.Line);
            Assert.Equal("New", vm.AllowedPrograms[2].Name);
            consent.Remove("a");
            Assert.Same(b, vm.AllowedPrograms[0]);
            Assert.Equal(new[] { "Pedal Curve", "New" }, vm.AllowedPrograms.Select(p => p.Name));
        }

        /// <summary>A reach picked in a web assistant's row or a network key's
        /// row leaves the row in place through the read that follows.</summary>
        [Fact]
        public void WebAndNetworkRows_KeepTheirPlace_ThroughTheReachTheUserPicks()
        {
            using var rig = new AssistantRig();
            var store = new PadForge.Services.Automation.Web.WebAccessStore();
            using var door = new PadForge.Services.Automation.Web.WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName);
            var view = new WebAccessViewModel(door, store) { AskToTurnOn = () => true };
            view.IsOn = true;
            store.Add(new PadForge.Services.Automation.Web.WebGrant { Id = "g1", Origin = "claude.ai", Name = "Claude", Allowed = DateTimeOffset.UtcNow });
            store.AddKey(new PadForge.Services.Automation.Web.NetworkKey
            {
                Id = "k1", Name = "Office PC", Level = AutomationLevel.SendInputs,
                Hash = PadForge.Services.Automation.Web.WebSecrets.Hash("pfn_x"), Made = DateTimeOffset.UtcNow,
            });
            view.Refresh();
            var grant = view.Allowed.Single();
            var key = view.NetworkKeys.Single();
            grant.SelectedReach = 1;
            key.SelectedReach = 1;
            view.Refresh();
            Assert.Same(grant, view.Allowed.Single());
            Assert.Same(key, view.NetworkKeys.Single());
            Assert.Equal(1, grant.SelectedReach);
            Assert.True(key.IsLimited);
            Assert.EndsWith("Drives its own devices alone. It still reads every device.", grant.Line);
            grant.SelectedReach = 0;
            Assert.Null(store.Grants.Single().Reach);
            view.Refresh();
            Assert.Same(grant, view.Allowed.Single());
            Assert.Equal(0, grant.SelectedReach);
        }

        /// <summary>A language change says Home's waiting recording again in
        /// the new language, and takes away the copy note of the old
        /// one.</summary>
        [Fact]
        public void ALanguageChange_SaysTheWaitingRecordingAgain_AndClearsTheCopyNote()
        {
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                var vm = new DashboardViewModel { AutomationClipboardSeam = _ => { } };
                vm.SetWaitingRecording(@"C:\Recordings\one.zip", DateTime.Now);
                vm.CopyAutomationJsonCommand.Execute(null);
                Assert.Equal(Strings.Instance.Extras_Automation_Copied, vm.AutomationCopyNote);
                var raised = new List<string>();
                vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("es"));
                Assert.Contains(nameof(DashboardViewModel.WaitingRecordingText), raised);
                Assert.Null(vm.AutomationCopyNote);
                Assert.False(vm.HasAutomationCopyNote);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }

        /// <summary>A program that clears one mapping row leaves the user's
        /// Map All walk and the recording of another row running. Clearing
        /// the row being recorded, or every row, stops them.</summary>
        [Fact]
        public void ClearingOneRow_LeavesTheWalkOnAnotherRow()
        {
            var vm = RestoredPad.Build(0, PadForge.Engine.VirtualControllerType.Xbox);
            Assert.True(vm.Mappings.Count > 2);
            var a = vm.Mappings[0];
            var b = vm.Mappings[1];
            int cancels = 0;
            vm.MapAllCancelRequested += (_, _) => cancels++;
            vm.IsMapAllActive = true;
            vm.CurrentRecordingTarget = a.TargetSettingName;

            vm.ClearMappingRows(new[] { b });
            Assert.True(vm.IsMapAllActive);
            Assert.Equal(a.TargetSettingName, vm.CurrentRecordingTarget);
            Assert.Equal(0, cancels);

            vm.ClearMappingRows(new[] { a });
            Assert.False(vm.IsMapAllActive);
            Assert.Null(vm.CurrentRecordingTarget);
            Assert.Equal(1, cancels);

            vm.CurrentRecordingTarget = b.TargetSettingName;
            vm.ClearMappingRows(vm.Mappings);
            Assert.Null(vm.CurrentRecordingTarget);
        }
    

        /// <summary>A reach box that stays through a language change offers
        /// the new language's two choices and keeps its pick.</summary>
        [Fact]
        public void AKeptReachRow_OffersTheNewLanguagesChoices()
        {
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                var vm = new DashboardViewModel { AssistantIconSeam = _ => null };
                var consent = new ReachConsent();
                consent.List.Add(new AllowedProgramView("a", "Steer Assist", "AutoHotkey64.exe", AutomationLevel.SendInputs, null, false, ProgramReach.OwnDevices));
                vm.AttachConsent(consent);
                var row = vm.AllowedPrograms[0];
                var raised = new List<string>();
                row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("de"));
                Assert.Same(row, vm.AllowedPrograms[0]);
                int choices = raised.IndexOf(nameof(AllowedProgramItem.ReachChoices));
                Assert.True(choices >= 0, "the kept row did not offer its choices again");
                Assert.True(raised.LastIndexOf(nameof(AllowedProgramItem.SelectedReach)) > choices, "the pick was not said again after the choices");
                Assert.Equal(new[] { Strings.Instance.Reach_All, Strings.Instance.Reach_Own }, row.ReachChoices);
                Assert.Equal(1, row.SelectedReach);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }

        /// <summary>The Devices page's Copied. note beside the script ID
        /// goes when another device is shown: back on the first device, the
        /// note is gone.</summary>
        [Fact]
        public void TheScriptIdNote_GoesWhenAnotherDeviceIsShown()
        {
            var vm = new DevicesViewModel();
            var a = new DeviceRowViewModel { DeviceName = "Pad A", InstanceGuid = Guid.NewGuid() };
            var b = new DeviceRowViewModel { DeviceName = "Pad B", InstanceGuid = Guid.NewGuid() };
            vm.Devices.Add(a);
            vm.Devices.Add(b);
            vm.SelectedDevice = a;
            a.CopyScriptIdCommand.Execute(null);
            Assert.Equal(Strings.Instance.Extras_Automation_Copied, a.ScriptIdNote);
            vm.SelectedDevice = b;
            vm.SelectedDevice = a;
            Assert.Null(a.ScriptIdNote);
        }

        /// <summary>Pick Devices… for a program lists every device but the
        /// ones that program added, which its reach always takes.</summary>
        [Fact]
        public void PickDevices_LeavesOutTheProgramsOwnDevices()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            // A program of its own file: a declare goes by its consent key,
            // and the test host's own file would read as PadForge's helper.
            rig.EachConnectionItsOwnProgram();
            var pad = rig.AddDevice(PadForge.Engine.InputDeviceType.Gamepad, "Xbox Wireless Controller");
            using var script = rig.Open("Steer Assist");
            string declared = script.Final("declare key=steer name=\"Steer\" {\"buttons\":[\"A\",\"B\"],\"axes\":[{\"name\":\"Left X\"}],\"hats\":[\"View\"]}");
            Assert.StartsWith("ok id=", declared);
            string id = declared.Substring(6).Split(' ')[0];
            var own = rig.Host.Sessions.Devices.Find(id);
            Assert.NotNull(own);
            string key = rig.Host.Sessions.Snapshot().Single(s => s.ClaimedName == "Steer Assist").Who.Key;
            Assert.Contains(PadForge.Views.PickDevicesDialog.DevicesNow(), d => d.Id == own.Guid);

            var listed = PadForge.Views.PickDevicesDialog.DevicesFor(rig.Host.Sessions.Devices, key);
            Assert.DoesNotContain(listed, d => d.Id == own.Guid);
            Assert.Contains(listed, d => d.Id == pad.InstanceGuid);
            // Another program's list keeps it.
            Assert.Contains(PadForge.Views.PickDevicesDialog.DevicesFor(rig.Host.Sessions.Devices, "another"), d => d.Id == own.Guid);
        }

        /// <summary>Open a Recording… says why Replay did not open the file,
        /// for each reason ReplayFile.Read gives, and no line claims what the
        /// reader did not find.</summary>
        [Fact]
        public void ARecordingNotOpened_IsToldItsOwnReason()
        {
            string tooMany = PadForge.Views.ExtrasPage.ReplayNotOpenedLine("too-many-devices", "big.zip");
            Assert.NotEqual(PadForge.Views.ExtrasPage.ReplayNotOpenedLine("not-a-recording", "big.zip"), tooMany);
            Assert.Contains("16", tooMany);
            var lines = new[] { "not-a-recording", "malformed", "too-many-devices", "no-devices", "unreadable", "something-else" }
                .ToDictionary(r => r, r => PadForge.Views.ExtrasPage.ReplayNotOpenedLine(r, "a.zip"));
            Assert.All(lines.Values, l => Assert.Contains("a.zip", l));
            Assert.Equal(lines["not-a-recording"], lines["malformed"]);
            Assert.Equal(4, lines.Values.Distinct().Count());
            Assert.Equal(lines["unreadable"], lines["something-else"]);
        }
    

        private static string Source(params string[] parts) => ExtrasPageTests.RepoText(new[] { "PadForge.App" }.Concat(parts).ToArray());

        /// <summary>The Save boxes' default names come from Strings, so they
        /// are in the user's language: Export Activity…'s, the sound
        /// package's and the icon package's.</summary>
        [Fact]
        public void TheSaveBoxesDefaultNames_ComeFromStrings()
        {
            Assert.DoesNotContain("\"PadForge Activity \"", Source("Views", "ExtrasPage.xaml.cs"));
            string pad = Source("Views", "PadPage.xaml.cs");
            Assert.DoesNotContain("\"Sounds\" + ext", pad);
            Assert.DoesNotContain("\"Icons\" + ext", pad);
        }

        /// <summary>A reach box grows to the choice it shows, in every
        /// language: no fixed width, and one smallest width for all three
        /// boxes.</summary>
        [Fact]
        public void TheReachBoxes_HaveNoFixedWidth_AndOneSmallestWidth()
        {
            var boxes = new List<XElement>();
            foreach (string page in new[] { "ExtrasPage.xaml", "AssistantsWebCard.xaml", "AssistantsWebSwitch.xaml" })
                boxes.AddRange(XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", page)).Descendants()
                    .Where(e => e.Name.LocalName == "ComboBox" && At(e, "ItemsSource") == "{Binding ReachChoices}"));
            Assert.Equal(3, boxes.Count);
            Assert.All(boxes, b => Assert.Null(At(b, "Width")));
            Assert.Single(boxes.Select(b => At(b, "MinWidth")).Distinct());
            Assert.NotNull(At(boxes[0], "MinWidth"));
        }

        /// <summary>A save that failed says so in a sentence from Strings,
        /// never in the file system's own English words.</summary>
        [Fact]
        public void AFailedSave_SaysSoInTheUsersLanguage()
        {
            // Nor a copy that failed: the clipboard's error is Windows' own text.
            foreach (var file in new[] { Source("Views", "ExtrasPage.xaml.cs"), Source("ViewModels", "DashboardViewModel.AutomationPage.cs"),
                Source("ViewModels", "WebAccessViewModel.cs"), Source("Views", "NetworkAssistantDialog.xaml.cs") })
                Assert.DoesNotContain("ex.Message", file);
        }

        /// <summary>The Automation and Plugins pages' saves open no file the
        /// user picked in this process, which may run with rights: Export
        /// Activity…, Save the Skill as a ZIP… and Save the Plugin Kit… go
        /// through UserSave, a copy started as the user.</summary>
        [Fact]
        public void ThePagesSaves_OpenNoPickedFileInThisProcess()
        {
            string page = Source("Views", "ExtrasPage.xaml.cs");
            Assert.DoesNotContain("File.WriteAllText(file", page);
            string automation = Source("ViewModels", "DashboardViewModel.AutomationPage.cs");
            Assert.DoesNotContain("new FileStream(file, FileMode.Create", automation);
            Assert.DoesNotContain("=> PluginKit.Save(parent)", Source("Services", "Plugins", "PluginsController.cs"));
        }
    

        /// <summary>A launcher that stands for the copy started as the user:
        /// it notes the line, runs what the copy would run in this process,
        /// and ends the copy with its exit code.</summary>
        private sealed class UserCopyLauncher : PadForge.Services.Plugins.IPluginLauncher
        {
            public readonly List<string> Lines = new();
            public Func<IReadOnlyList<string>, int> Run;
            public PadForge.Services.Plugins.PluginRefusal Refuse;

            public PadForge.Services.Plugins.PluginLaunch Start(PadForge.Services.Plugins.PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                Lines.Add(command.CommandLine);
                if (Refuse != PadForge.Services.Plugins.PluginRefusal.None) return PadForge.Services.Plugins.PluginLaunch.Refused(Refuse);
                var quoted = System.Text.RegularExpressions.Regex.Matches(command.CommandLine, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).Skip(1).ToList();
                var process = new WebTestProcess(4321);
                int code = Run?.Invoke(quoted) ?? 0;
                _ = System.Threading.Tasks.Task.Run(async () => { await System.Threading.Tasks.Task.Delay(50); process.Exit(code); });
                return new PadForge.Services.Plugins.PluginLaunch { Process = process };
            }
        }

        private static string NewFolder()
        {
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForge Tests UserSave " + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Export Activity… with rights the shell lacks stages the
        /// text in the recordings' folder and has a copy started as the user
        /// write the file picked: this process opens no file there. The
        /// staged file is gone after, and a copy Windows did not start saves
        /// nothing.</summary>
        [Fact]
        public async System.Threading.Tasks.Task ExportActivity_IsWrittenByACopyStartedAsTheUser()
        {
            string root = NewFolder();
            string recordings = System.IO.Path.Combine(root, "PadForge Recordings");
            System.IO.Directory.CreateDirectory(recordings);
            string target = System.IO.Path.Combine(root, "activity.txt");
            var launcher = new UserCopyLauncher();
            launcher.Run = words =>
            {
                Assert.False(System.IO.File.Exists(target), "the file was written before the copy started");
                return PadForge.Common.Recording.RecordingSave.Run(words[0], words[1], out _);
            };
            try
            {
                PadForge.Common.Recording.RecordingFolder.Seam = () => recordings;
                PadForge.Common.Recording.RecordingSave.Launcher = launcher;
                PadForge.Common.Recording.RecordingSave.RightsSeam = () => true;
                PadForge.Common.Recording.RecordingSave.Exe = () => @"C:\PadForge\PadForge.exe";
                PadForge.Services.UserSave.HasRightsSeam = () => true;
                PadForge.Services.UserSave.NoLowerRightSeam ??= () => false;
                Assert.True(await PadForge.Views.ExtrasPage.ExportActivityAsync("12:00 Steer Assist read the state", target));
                Assert.Contains(" --record save ", Assert.Single(launcher.Lines));
                Assert.Equal("12:00 Steer Assist read the state", System.IO.File.ReadAllText(target));
                Assert.Equal(0xEF, System.IO.File.ReadAllBytes(target)[0]);
                Assert.Empty(System.IO.Directory.GetFiles(recordings));

                System.IO.File.Delete(target);
                launcher.Refuse = PadForge.Services.Plugins.PluginRefusal.NoShell;
                Assert.False(await PadForge.Views.ExtrasPage.ExportActivityAsync("text", target));
                Assert.False(System.IO.File.Exists(target));
                Assert.Empty(System.IO.Directory.GetFiles(recordings));
            }
            finally
            {
                PadForge.Common.Recording.RecordingFolder.Seam = null;
                PadForge.Common.Recording.RecordingSave.Launcher = null;
                PadForge.Common.Recording.RecordingSave.RightsSeam = null;
                PadForge.Common.Recording.RecordingSave.Exe = () => Environment.ProcessPath;
                PadForge.Services.UserSave.HasRightsSeam = null;
                PadForge.Services.UserSave.NoLowerRightSeam = null;
                try { System.IO.Directory.Delete(root, recursive: true); } catch (System.IO.IOException) { }
            }
        }

        /// <summary>Save the Plugin Kit… with rights the shell lacks starts
        /// --plugin-kit as the user with a new folder's name, a number on it
        /// when the name is taken, and writes nothing in this process.</summary>
        [Fact]
        public async System.Threading.Tasks.Task ThePluginKit_IsWrittenByACopyStartedAsTheUser()
        {
            string parent = NewFolder();
            var launcher = new UserCopyLauncher();
            launcher.Run = _ => 0;
            try
            {
                PadForge.Services.UserSave.Launcher = launcher;
                PadForge.Services.UserSave.HasRightsSeam = () => true;
                PadForge.Services.UserSave.NoLowerRightSeam ??= () => false;
                PadForge.Services.UserSave.Exe = () => @"C:\PadForge\PadForge.exe";
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(parent, PadForge.Services.Plugins.PluginKit.FolderName));
                string folder = await PadForge.Services.UserSave.KitAsync(parent);
                string expected = System.IO.Path.Combine(parent, PadForge.Services.Plugins.PluginKit.FolderName + " 2");
                Assert.Equal(expected, folder);
                Assert.Equal("\"C:\\PadForge\\PadForge.exe\" --plugin-kit \"" + expected + "\"", Assert.Single(launcher.Lines));
                Assert.False(System.IO.Directory.Exists(expected));
                launcher.Run = _ => 1;
                Assert.Null(await PadForge.Services.UserSave.KitAsync(parent));
            }
            finally
            {
                PadForge.Services.UserSave.Launcher = null;
                PadForge.Services.UserSave.HasRightsSeam = null;
                PadForge.Services.UserSave.NoLowerRightSeam = null;
                PadForge.Services.UserSave.Exe = () => Environment.ProcessPath;
                try { System.IO.Directory.Delete(parent, recursive: true); } catch (System.IO.IOException) { }
            }
        }

        /// <summary>The Save boxes' names are the Strings formats filled in.</summary>
        [Fact]
        public void TheSaveBoxesDefaultNames_AreTheirStringsFormats()
        {
            var s = Strings.Instance;
            var when = new DateTime(2026, 11, 26, 14, 5, 0);
            Assert.Equal(string.Format(s.Extras_Automation_ExportFileName_Format, "2026-11-26 1405"), PadForge.Views.ExtrasPage.ActivityFileName(when));
            Assert.Equal("PadForge Activity 2026-11-26 1405.txt", PadForge.Views.ExtrasPage.ActivityFileName(when));
            Assert.Equal("Sounds.pfsounds", string.Format(s.Pad_Audio_Packages_DefaultFileName_Format, ".pfsounds"));
            Assert.Equal("Icons.pficons", string.Format(s.Pad_Menus_IconPackages_DefaultFileName_Format, ".pficons"));
        }
    

        private sealed class GrantConsent : IAutomationConsent
        {
            public readonly List<string> Said = new();
            public IReadOnlyList<AllowedProgramView> Allowed => Array.Empty<AllowedProgramView>();
            public event EventHandler Changed { add { } remove { } }
            public void Remove(string key) { }
            public void AllowAssistant(string hostKey, string hostName, string program) => Said.Add("allow " + hostKey + " " + program);
            public void ForgetAssistant(string hostKey, string program) => Said.Add("forget " + hostKey + " " + program);
        }

        /// <summary>The check copy runs as the user, and a program of the user
        /// can rewrite its report before PadForge reads it. An assistant's Add
        /// allows the program PadForge itself finds for that assistant, never
        /// the one the report names, and allows none when PadForge finds
        /// none: that program asks on the card when it first connects.</summary>
        [Fact]
        public async System.Threading.Tasks.Task AnAssistantsAdd_AllowsTheProgramPadForgeFinds_NotTheReports()
        {
            string found = @"C:\Users\Pat\.local\bin\claude.exe";
            string report = "[{\"key\":\"claude-code\",\"program\":\"C:\\\\Users\\\\Pat\\\\Downloads\\\\claude.exe\",\"entry\":\"missing\",\"skill\":\"missing\"}]";
            var vm = new DashboardViewModel
            {
                AssistantIconSeam = _ => null,
                AssistantInstallSeam = request => System.Threading.Tasks.Task.FromResult(request.Check
                    ? new PadForge.Services.Install.InstallRun(PadForge.Services.Install.UserInstall.Done, null, report)
                    : new PadForge.Services.Install.InstallRun(PadForge.Services.Install.UserInstall.Done, null, "done")),
                AssistantFindSeam = key => key == "claude-code" ? found : null,
            };
            var consent = new GrantConsent();
            vm.AttachConsent(consent);
            vm.EnableAutomation = true;
            await vm.RefreshAssistantRowsAsync();
            var row = vm.AssistantRows.Single();
            await vm.PressAssistantAsync(row, AssistantRowButton.Add);
            Assert.Contains("allow claude-code " + found, consent.Said);
            Assert.DoesNotContain(consent.Said, said => said.Contains("Downloads", StringComparison.Ordinal));

            vm.AssistantFindSeam = _ => null;
            consent.Said.Clear();
            await vm.RefreshAssistantRowsAsync();
            await vm.PressAssistantAsync(vm.AssistantRows.Single(), AssistantRowButton.Add);
            Assert.DoesNotContain(consent.Said, said => said.StartsWith("allow ", StringComparison.Ordinal));
            // Remove still takes a consent away, by the report's program
            // while PadForge finds none.
            await vm.PressAssistantAsync(vm.AssistantRows.Single(), AssistantRowButton.Remove);
            Assert.Contains(@"forget claude-code C:\Users\Pat\Downloads\claude.exe", consent.Said);
        }

        /// <summary>A file dropped from Explorer reaches a copy with rights
        /// the shell lacks only as WM_DROPFILES on a window with no OLE drop
        /// of its own and the three messages let through (GLFW,
        /// win32_window.c). The Plugins page takes that drop.</summary>
        [Fact]
        public void ThePluginsPage_TakesTheShellsDrop_InACopyWithRights()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string page = string.Concat(System.IO.Directory.GetFiles(System.IO.Path.Combine(dir.FullName, "PadForge.App", "Views"), "ExtrasPage*.cs")
                .Select(System.IO.File.ReadAllText));
            Assert.Contains("WM_DROPFILES", page);
            Assert.Contains("ChangeWindowMessageFilterEx", page);
            Assert.Contains("DragAcceptFiles", page);
            Assert.Contains("RevokeDragDrop", page);
        }
    

        /// <summary>PadForge finds an assistant installed for the user on the
        /// user's own Path and in the user's profile, read from the shell
        /// user's variables: an npm shim on the user's Path, and Claude
        /// Code's native install under the profile.</summary>
        [Fact]
        public void TheShellUsersPlaces_FindAPerUserInstall()
        {
            string root = NewFolder();
            try
            {
                string npm = System.IO.Path.Combine(root, "AppData", "Roaming", "npm");
                System.IO.Directory.CreateDirectory(npm);
                System.IO.File.WriteAllText(System.IO.Path.Combine(npm, "codex.cmd"), "@echo off");
                string bin = System.IO.Path.Combine(root, ".local", "bin");
                System.IO.Directory.CreateDirectory(bin);
                System.IO.File.WriteAllText(System.IO.Path.Combine(bin, "claude.exe"), "MZ");
                var user = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PATH"] = @"C:\NotThere;" + npm,
                    ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
                    ["USERPROFILE"] = root,
                };
                Assert.Equal(System.IO.Path.Combine(npm, "codex.cmd"), PadForge.Services.Install.AssistantPrograms.FindAs(user, "codex"));
                Assert.Equal(System.IO.Path.Combine(bin, "claude.exe"), PadForge.Services.Install.AssistantPrograms.FindAs(user, "claude-code"));
                Assert.Null(PadForge.Services.Install.AssistantPrograms.FindAs(user, "gemini-cli"));
            }
            finally
            {
                try { System.IO.Directory.Delete(root, recursive: true); } catch (System.IO.IOException) { }
            }
        }

        /// <summary>The Plugins page gives WPF's own drop back through
        /// DragDrop.RegisterDropTarget, which this WPF has, and lets the
        /// three messages GLFW lets through.</summary>
        [Fact]
        public void TheShellDrop_CanGiveWpfsDropBack()
        {
            Assert.NotNull(PadForge.Views.ExtrasPage.RegisterWpfDrop);
            Assert.Equal(new[] { 0x0233, 0x004A, 0x0049 }, PadForge.Views.ExtrasPage.ShellDropMessages);
        }
    

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr memory);

        private static IntPtr Block(byte[] bytes)
        {
            IntPtr block = GlobalAlloc(0x0040, (UIntPtr)bytes.Length);
            Assert.NotEqual(IntPtr.Zero, block);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, block, bytes.Length);
            return block;
        }

        /// <summary>A WM_DROPFILES that any program without rights may post
        /// is read only when its handle holds a shell drop: a DROPFILES header
        /// with its UTF-16 list right after it, ended by two nulls inside the
        /// block. Another block of this process is neither read nor
        /// freed.</summary>
        [Fact]
        public void AShellDropHandle_IsTold_FromAnyOtherBlock()
        {
            var header = new byte[PadForge.Views.ExtrasPage.DropFilesSize];
            BitConverter.GetBytes(PadForge.Views.ExtrasPage.DropFilesSize).CopyTo(header, 0);
            BitConverter.GetBytes(1).CopyTo(header, 16);
            byte[] names = System.Text.Encoding.Unicode.GetBytes(@"C:\Plugins\bridge.exe" + "\0" + "\0");
            IntPtr drop = Block(header.Concat(names).ToArray());
            IntPtr zeros = Block(new byte[64]);
            IntPtr open = Block(header.Concat(System.Text.Encoding.Unicode.GetBytes(@"C:\Plugins\bridge.exe")).ToArray());
            try
            {
                Assert.True(PadForge.Views.ExtrasPage.LooksLikeShellDrop(drop));
                Assert.False(PadForge.Views.ExtrasPage.LooksLikeShellDrop(zeros));
                Assert.False(PadForge.Views.ExtrasPage.LooksLikeShellDrop(open));
                Assert.False(PadForge.Views.ExtrasPage.LooksLikeShellDrop(IntPtr.Zero));
            }
            finally
            {
                GlobalFree(drop);
                GlobalFree(zeros);
                GlobalFree(open);
            }
        }

        /// <summary>The shell user's Path is searched with rights, so a share
        /// or a device path on it is never looked in.</summary>
        [Fact]
        public void TheShellUsersPath_IsSearchedOnDrivesAlone()
        {
            Assert.True(PadForge.Services.Install.AssistantPrograms.IsOnADrive(@"C:\Users\Pat\AppData\Roaming\npm"));
            Assert.False(PadForge.Services.Install.AssistantPrograms.IsOnADrive(@"\\server\share\npm"));
            Assert.False(PadForge.Services.Install.AssistantPrograms.IsOnADrive(@"\\?\C:\npm"));
            Assert.False(PadForge.Services.Install.AssistantPrograms.IsOnADrive(@"\\.\pipe\npm"));
            Assert.False(PadForge.Services.Install.AssistantPrograms.IsOnADrive(@"npm"));
            var user = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = @"\\server\share\npm", ["USERPROFILE"] = @"\\server\share" };
            Assert.Null(PadForge.Services.Install.AssistantPrograms.FindAs(user, "codex"));
            Assert.Null(PadForge.Services.Install.AssistantPrograms.FindAs(user, "claude-code"));
        }

        /// <summary>With administrator rights, as PadForge runs, the pages'
        /// saves take the route as the user: the desktop shell has none of
        /// those rights on this bench.</summary>
        [ElevatedFact]
        public void InAHostWithRights_TheSavesTakeTheRouteAsTheUser()
        {
            Assert.True(PadForge.Services.UserSave.RunsWithRightsTheShellLacks());
        }

        /// <summary>With administrator rights, Export Activity… stages the
        /// text and starts the copy as the user, through the real rights
        /// reads, and writes nothing at the target in this process.</summary>
        [ElevatedFact]
        public async System.Threading.Tasks.Task InAHostWithRights_ExportActivity_StartsTheCopyAsTheUser()
        {
            string root = NewFolder();
            string recordings = System.IO.Path.Combine(root, "PadForge Recordings");
            System.IO.Directory.CreateDirectory(recordings);
            string target = System.IO.Path.Combine(root, "activity.txt");
            var launcher = new UserCopyLauncher { Run = _ => { Assert.False(System.IO.File.Exists(target)); return 1; } };
            try
            {
                PadForge.Common.Recording.RecordingFolder.Seam = () => recordings;
                PadForge.Common.Recording.RecordingSave.Launcher = launcher;
                PadForge.Common.Recording.RecordingSave.Exe = () => @"C:\PadForge\PadForge.exe";
                Assert.False(await PadForge.Views.ExtrasPage.ExportActivityAsync("text", target));
                Assert.Contains(" --record save ", Assert.Single(launcher.Lines));
                Assert.False(System.IO.File.Exists(target));
            }
            finally
            {
                PadForge.Common.Recording.RecordingFolder.Seam = null;
                PadForge.Common.Recording.RecordingSave.Launcher = null;
                PadForge.Common.Recording.RecordingSave.Exe = () => Environment.ProcessPath;
                try { System.IO.Directory.Delete(root, recursive: true); } catch (System.IO.IOException) { }
            }
        }

        /// <summary>With administrator rights, an assistant's program is
        /// found with the shell user's variables, not this process's.</summary>
        [ElevatedFact]
        public void InAHostWithRights_TheAssistantIsFoundWithTheShellUsersVariables()
        {
            string root = NewFolder();
            try
            {
                string npm = System.IO.Path.Combine(root, "npm");
                System.IO.Directory.CreateDirectory(npm);
                System.IO.File.WriteAllText(System.IO.Path.Combine(npm, "codex.cmd"), "@echo off");
                PadForge.Services.Install.AssistantPrograms.ShellVariablesSeam = () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = npm };
                Assert.Equal(System.IO.Path.Combine(npm, "codex.cmd"), PadForge.Services.Install.AssistantPrograms.FindForShellUser("codex"));
            }
            finally
            {
                PadForge.Services.Install.AssistantPrograms.ShellVariablesSeam = null;
                try { System.IO.Directory.Delete(root, recursive: true); } catch (System.IO.IOException) { }
            }
        }
    

        /// <summary>A recordings folder that cannot be made (a file holds its
        /// name) makes a staged save say false, as every other failure does,
        /// and throws nothing out of FileAsync.</summary>
        [Fact]
        public async System.Threading.Tasks.Task AStagedSave_ThatCannotMakeItsFolder_SaysFalse()
        {
            try
            {
                PadForge.Services.UserSave.HasRightsSeam = () => true;
                PadForge.Services.UserSave.NoLowerRightSeam ??= () => false;
                foreach (var thrown in new Exception[] { new System.IO.IOException("held"), new UnauthorizedAccessException("denied"), new ArgumentException("bad"), new NotSupportedException("no") })
                {
                    PadForge.Common.Recording.RecordingFolder.Seam = () => throw thrown;
                    Assert.False(await PadForge.Services.UserSave.FileAsync(_ => { }, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "never.txt")));
                }
            }
            finally
            {
                PadForge.Common.Recording.RecordingFolder.Seam = null;
                PadForge.Services.UserSave.HasRightsSeam = null;
                PadForge.Services.UserSave.NoLowerRightSeam = null;
            }
        }

        /// <summary>A ShellElevated refusal writes the kit in this process,
        /// which has rights, only when the shell's user has no lower right,
        /// as every other copy started as the user does.</summary>
        [Fact]
        public async System.Threading.Tasks.Task ThePluginKit_OnAShellElevatedRefusal_IsWrittenHereOnlyWhenTheUserHasNoLowerRight()
        {
            string parent = NewFolder();
            var launcher = new UserCopyLauncher { Refuse = PadForge.Services.Plugins.PluginRefusal.ShellElevated };
            try
            {
                PadForge.Services.UserSave.Launcher = launcher;
                PadForge.Services.UserSave.HasRightsSeam = () => true;
                PadForge.Services.UserSave.NoLowerRightSeam ??= () => false;
                PadForge.Services.UserSave.Exe = () => @"C:\PadForge\PadForge.exe";
                PadForge.Services.UserSave.NoLowerRightSeam = () => false;
                Assert.Null(await PadForge.Services.UserSave.KitAsync(parent));
                Assert.Empty(System.IO.Directory.GetFileSystemEntries(parent));
                PadForge.Services.UserSave.NoLowerRightSeam = () => true;
                string folder = await PadForge.Services.UserSave.KitAsync(parent);
                Assert.Equal(System.IO.Path.Combine(parent, PadForge.Services.Plugins.PluginKit.FolderName), folder);
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(folder, "README.md")));
            }
            finally
            {
                PadForge.Services.UserSave.Launcher = null;
                PadForge.Services.UserSave.HasRightsSeam = null;
                PadForge.Services.UserSave.NoLowerRightSeam = null;
                PadForge.Services.UserSave.NoLowerRightSeam = null;
                PadForge.Services.UserSave.Exe = () => Environment.ProcessPath;
                try { System.IO.Directory.Delete(parent, recursive: true); } catch (System.IO.IOException) { }
            }
        }

        /// <summary>A PATH searched for another process's user looks only in
        /// folders on a drive of this PC: a device path to a folder that holds
        /// the command finds nothing, and the drive path to it does.</summary>
        [Fact]
        public void APathSearchedForAnotherUser_LooksOnDrivesAlone()
        {
            string root = NewFolder();
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "codex.cmd"), "@echo off");
                Assert.Null(PadForge.Services.Install.UserInstall.FindCommandOn("codex", @"\\?\" + root, null));
                Assert.Equal(System.IO.Path.Combine(root, "codex.cmd"), PadForge.Services.Install.UserInstall.FindCommandOn("codex", root, null));
                var user = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = @"\\?\" + root + ";" + root };
                Assert.Equal(System.IO.Path.Combine(root, "codex.cmd"), PadForge.Services.Install.AssistantPrograms.FindAs(user, "codex"));
            }
            finally
            {
                try { System.IO.Directory.Delete(root, recursive: true); } catch (System.IO.IOException) { }
            }
        }

        /// <summary>A device the program owns is not listed again because an
        /// earlier pick kept it. A kept device it does not own still is.</summary>
        [Fact]
        public void PickDevices_ListsNoOwnedDevice_ThatAnEarlierPickKept()
        {
            Guid own = Guid.NewGuid(), gone = Guid.NewGuid(), pad = Guid.NewGuid();
            var reach = new ProgramReach { Devices = new[] { new ReachDevice(own, "Steer"), new ReachDevice(gone, "Old Wheel") } };
            var rows = PadForge.Views.PickDevicesDialog.RowsFor(reach, new[] { (pad, "Xbox Wireless Controller") }, new HashSet<Guid> { own });
            Assert.Equal(new[] { pad, gone }, rows.Select(r => r.Id));
            Assert.True(rows.Single(r => r.Id == gone).Picked);
        }
    

        /// <summary>A copy with rights whose shell's user also has a lower
        /// right (UAC on, an Explorer started elevated among them) saves no
        /// file and writes no kit in this process: the save goes to the copy
        /// as the user, and a ShellElevated refusal saves nothing. The
        /// shell's rights read is left to the code (RightsSeam null), and
        /// only the shell user's answer is faked.</summary>
        [Fact]
        public async System.Threading.Tasks.Task WithRights_AndAShellUserWithALowerRight_NothingIsWrittenHere()
        {
            string root = NewFolder();
            string recordings = System.IO.Path.Combine(root, "PadForge Recordings");
            System.IO.Directory.CreateDirectory(recordings);
            string target = System.IO.Path.Combine(root, "activity.txt");
            var refusing = new UserCopyLauncher { Refuse = PadForge.Services.Plugins.PluginRefusal.ShellElevated };
            try
            {
                PadForge.Services.UserSave.HasRightsSeam = () => true;
                PadForge.Services.UserSave.NoLowerRightSeam = () => false;
                PadForge.Services.UserSave.Launcher = refusing;
                PadForge.Services.UserSave.Exe = () => @"C:\PadForge\PadForge.exe";
                PadForge.Common.Recording.RecordingFolder.Seam = () => recordings;
                PadForge.Common.Recording.RecordingSave.Launcher = refusing;
                PadForge.Common.Recording.RecordingSave.RightsSeam = () => true;
                PadForge.Common.Recording.RecordingSave.NoLowerRightSeam = () => false;
                PadForge.Common.Recording.RecordingSave.Exe = () => @"C:\PadForge\PadForge.exe";

                Assert.False(await PadForge.Services.UserSave.FileAsync(s => s.WriteByte(1), target));
                Assert.False(System.IO.File.Exists(target));
                Assert.Null(await PadForge.Services.UserSave.KitAsync(root));
                Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(root, PadForge.Services.Plugins.PluginKit.FolderName)));

                // Without rights, both are made here, as the user.
                PadForge.Services.UserSave.HasRightsSeam = () => false;
                refusing.Lines.Clear();
                Assert.True(await PadForge.Services.UserSave.FileAsync(s => s.WriteByte(1), target));
                Assert.True(System.IO.File.Exists(target));
                Assert.NotNull(await PadForge.Services.UserSave.KitAsync(root));
                Assert.Empty(refusing.Lines);
            }
            finally
            {
                PadForge.Services.UserSave.HasRightsSeam = null;
                PadForge.Services.UserSave.NoLowerRightSeam = null;
                PadForge.Services.UserSave.Launcher = null;
                PadForge.Services.UserSave.Exe = () => Environment.ProcessPath;
                PadForge.Common.Recording.RecordingFolder.Seam = null;
                PadForge.Common.Recording.RecordingSave.Launcher = null;
                PadForge.Common.Recording.RecordingSave.RightsSeam = null;
                PadForge.Common.Recording.RecordingSave.NoLowerRightSeam = null;
                PadForge.Common.Recording.RecordingSave.Exe = () => Environment.ProcessPath;
                try { System.IO.Directory.Delete(root, recursive: true); } catch (System.IO.IOException) { }
            }
        }
    }

    /// <summary>
    /// The Automation view tests that build a card under the app's
    /// resources. PageRenderDumpPages.WithApp makes PadForge's App, puts
    /// the dark theme on and releases the App after. In a collection that
    /// runs beside others, the App's resources reached windows other tests
    /// had open on their own threads, and their implicit Window style changed
    /// after they showed (TextDirectionTests threw "Cannot change
    /// AllowsTransparency after a Window has been shown"), and the dark
    /// theme stayed on for the tests after it (MenuGridRenderTests read a
    /// dark ground). So these run in the WpfApplicationSingleton collection,
    /// which runs alone, as WithApp asks.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class AutomationViewsAppTests
    {
        /// <summary>The web card's clock runs only while the card shows and
        /// the door accepts a new web assistant. A card the window holds
        /// collapsed from the start gets Loaded too, and its clock stays
        /// off.</summary>
        [Fact]
        public void TheWebCardsClock_RunsOnlyWhileTheCardShowsAndTheDoorIsArmed()
        {
            Assert.True(PadForge.Views.AssistantsWebCard.ClockRuns(visible: true, armed: true));
            Assert.False(PadForge.Views.AssistantsWebCard.ClockRuns(visible: false, armed: true));
            Assert.False(PadForge.Views.AssistantsWebCard.ClockRuns(visible: true, armed: false));
            PageRenderDumpPages.WithApp((_, _) =>
            {
                using var rig = new AssistantRig();
                var store = new PadForge.Services.Automation.Web.WebAccessStore();
                using var door = new PadForge.Services.Automation.Web.WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName);
                var view = new WebAccessViewModel(door, store);
                var card = new PadForge.Views.AssistantsWebCard { DataContext = view };
                card.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.LoadedEvent));
                Assert.False(card.ClockRunning);
            }, TimeSpan.FromMinutes(2), "The web card test timed out.");
        }

        /// <summary>A Runtime Key typed and sent with Enter is kept when the
        /// helper's answer comes while the box still holds the keyboard, and
        /// leaving the box then forgets nothing. A box the user empties of
        /// the stand-in still forgets the key.</summary>
        [Fact]
        public void ARuntimeKeyKeptWhileTheBoxHasTheKeyboard_IsNotForgottenWhenTheBoxIsLeft()
        {
            PageRenderDumpPages.WithApp((_, _) =>
            {
                using var rig = new AssistantRig();
                var store = new PadForge.Services.Automation.Web.WebAccessStore();
                using var door = new PadForge.Services.Automation.Web.WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName);
                var view = new WebAccessViewModel(door, store);
                bool focused = true;
                var card = new PadForge.Views.AssistantsWebCard { DataContext = view, BoxHasKeyboard = () => focused };
                card.RuntimeKeyBox.Password = "sk-typed";
                card.Commit();
                Assert.Equal(string.Empty, card.RuntimeKeyBox.Password);
                door.Take(new System.Text.Json.Nodes.JsonObject { ["t"] = "protected", ["blob"] = "blob-1" });
                Assert.Equal("blob-1", store.RuntimeKeyBlob);
                focused = false;
                card.Commit();
                Assert.Equal("blob-1", store.RuntimeKeyBlob);
                // The box shows the stand-in now. Emptied by the user and
                // left, it forgets the key.
                Assert.Equal(PadForge.Views.AssistantsWebCard.StandIn, card.RuntimeKeyBox.Password);
                card.RuntimeKeyBox.Password = string.Empty;
                card.Commit();
                Assert.Null(store.RuntimeKeyBlob);
            }, TimeSpan.FromMinutes(2), "The web card test timed out.");
        }
    }
}
