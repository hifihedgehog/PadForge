using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using Jint;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The engine's stop, the Home cards, the Devices list and the lines the
    /// pages read from the services, in the shapes the audit of 2026-11-13
    /// found them wrong: what runs on which thread when the engine stops,
    /// what a card shows while no timer refreshes it, and what a line says
    /// while nothing has been read for it.
    /// </summary>
    public class AuditDelta20261113Tests
    {
        private static string Text(params string[] parts) => ExtrasPageTests.RepoText(parts).Replace("\r", "");

        private static string Service() => Text("PadForge.App", "Services", "InputService.cs");

        /// <summary>A method's text, from its signature to the next member
        /// that starts at the same depth.</summary>
        private static string Body(string source, string signature)
        {
            int from = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(from >= 0, "not found: " + signature);
            int to = source.IndexOf("\n        }\n", from, StringComparison.Ordinal);
            Assert.True(to > from, "no end: " + signature);
            return source.Substring(from, to - from);
        }

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(20000), "STA test body timed out");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A list a control is bound to throws when it changes on
        /// another thread, empty or not, and Stop runs on a worker. The
        /// phones' list is cleared in a call queued to the interface's
        /// thread, and only while no server has started since.</summary>
        [Fact]
        public void StoppingTheEngine_ClearsThePhonesOnTheInterfacesThread()
        {
            Exception thrown = null;
            RunSta(() =>
            {
                var list = new ObservableCollection<string>();
                var bound = new ItemsControl { ItemsSource = list };
                try { Task.Run(() => list.Clear()).GetAwaiter().GetResult(); }
                catch (Exception ex) { thrown = ex; }
                GC.KeepAlive(bound);
            });
            Assert.IsType<NotSupportedException>(thrown);

            string stop = Body(Service(), "private void StopWebServer()");
            int clear = stop.IndexOf("_mainVm.Dashboard.WebControllerPhones.Clear();", StringComparison.Ordinal);
            Assert.True(clear > 0);
            int queued = stop.LastIndexOf("_dispatcher.BeginInvoke(new Action(() =>", clear, StringComparison.Ordinal);
            Assert.True(queued > 0, "the clear is not in a queued call");
            string call = stop.Substring(queued, stop.IndexOf("}));", queued, StringComparison.Ordinal) - queued);
            Assert.Contains("if (_webServer == null) _mainVm.Dashboard.WebControllerPhones.Clear();", call);
        }

        /// <summary>No timer refreshes a Home card while the engine is
        /// stopped, so a still that lands and a switch of the page's view
        /// give the cards and the Devices panel their pictures at
        /// once.</summary>
        [Fact]
        public void AStillThatLands_AndAViewSwitch_RefreshThePicturesAtOnce()
        {
            string service = Service();
            Assert.Contains("RefreshPictures();", Body(service, "private void StillMade()"));
            string refresh = Body(service, "internal void RefreshPictures()");
            int forget = refresh.IndexOf("slot.ForgetPicture();", StringComparison.Ordinal);
            int cards = refresh.IndexOf("RefreshSlotSummaryProperties();", StringComparison.Ordinal);
            int panel = refresh.IndexOf("UpdateSelectedDevicePicture();", StringComparison.Ordinal);
            Assert.True(forget > 0 && cards > forget && panel > cards);

            string window = Text("PadForge.App", "MainWindow.xaml.cs");
            Assert.Matches(@"if \(e\.PropertyName == nameof\(SettingsViewModel\.Use2DControllerView\)\)\s+_inputService\?\.RefreshPictures\(\);", window);
        }

        /// <summary>Home's record of which devices send input holds only
        /// the devices its walk read this pass. One that went offline, left
        /// its slot or is no longer read starts over when it returns.</summary>
        [Fact]
        public void TheHomeWalk_KeepsARecordOnlyOfTheDevicesItRead()
        {
            string walk = Service();
            int from = walk.IndexOf("public void RefreshSlotSummaryProperties(", StringComparison.Ordinal);
            walk = walk.Substring(from, walk.IndexOf("int xboxCount = 0,", from, StringComparison.Ordinal) - from);
            int clear = walk.IndexOf("_activityRead.Clear();", StringComparison.Ordinal);
            int add = walk.IndexOf("if (mapped != null && engineReads) _activityRead.Add(dev.InstanceGuid);", StringComparison.Ordinal);
            int keep = walk.IndexOf("_inputActivity.KeepOnly(_activityRead);", StringComparison.Ordinal);
            Assert.True(clear > 0 && add > clear && keep > add, $"clear {clear}, add {add}, keep {keep}");
        }

        /// <summary>A stop clears every live reading the stopped timer
        /// wrote: a row's meter, its two readings beside it, and the light
        /// beside each mapped device's name.</summary>
        [Fact]
        public void AStop_ClearsEveryLiveReading()
        {
            string stop = Service();
            int from = stop.IndexOf("public void Stop()", StringComparison.Ordinal);
            stop = stop.Substring(from, 6000);
            foreach (string line in new[]
            {
                "m.IsInputActive = false;", "m.LiveLevel = 0;", "m.CurrentValueText = string.Empty;",
                "m.CurrentPercentText = string.Empty;", "foreach (var dev in padVm.MappedDevices) dev.IsSending = false;",
            })
                Assert.Contains(line, stop);
        }

        /// <summary>Stop clears the offers on the interface's thread, and a
        /// device walk that was queued before the stop and runs during it
        /// makes no offer.</summary>
        [Fact]
        public void AWalkDuringAStop_MakesNoOffer()
        {
            string service = Service();
            Assert.Matches(@"_dispatcher\.Invoke\(\(\) =>\s+\{\s+foreach \(var pad in _mainVm\.Pads\) pad\.ClearAssignOffer\(\);\s+_mainVm\.Dashboard\.ClearHomeOffer\(\);\s+_mainVm\.Dashboard\.ClearFirstRunDevice\(\);\s+\}\);",
                service);
            Assert.Matches(@"if \(System\.Threading\.Volatile\.Read\(ref _stopped\) == 0\)\s+\{\s+EvaluateAssignOffers\(\);\s+EvaluateHomeOffer\(\);\s+EvaluateFirstRunDevice\(\);\s+\}",
                service);
        }

        /// <summary>A same-serial twin with no product identity is made
        /// again at every launch, so it owes no Home offer: an offer that is
        /// asked once cannot be remembered for it.</summary>
        [Fact]
        public void ATwinWithNoProductIdentity_OwesNoHomeOffer()
        {
            string step = Text("PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs");
            Assert.Contains("HomeOfferPending = productGuid != Guid.Empty,", step);
            Assert.DoesNotContain("ProductGuid = productGuid, HomeOfferPending = true", step);
        }

        /// <summary>The Devices lists show a selection and write none back,
        /// so the sync that removes a row clears the selection when the row
        /// is the selected one.</summary>
        [Fact]
        public void ARowThatGoes_TakesTheSelectionWithIt()
        {
            Assert.Matches(@"if \(ReferenceEquals\(devVm\.SelectedDevice, row\)\) devVm\.SelectedDevice = null;\s+devVm\.Devices\.RemoveAt\(i\);",
                Service());
        }

        /// <summary>Who sets the polling rate is written while the engine
        /// is stopped too: only the loop's own rate waits for an
        /// engine.</summary>
        [Fact]
        public void ThePollingNote_IsWrittenWhileTheEngineIsStopped()
        {
            string service = Service();
            int from = service.IndexOf("internal void ApplyEffectivePollingRate()", StringComparison.Ordinal);
            string apply = service.Substring(from, service.IndexOf("internal static int ResolvePollingMs(", from, StringComparison.Ordinal) - from);
            Assert.DoesNotContain("if (_inputManager == null) return;", apply);
            Assert.Matches(@"if \(_inputManager != null\)\s+_inputManager\.PollingIntervalMs =", apply);
            Assert.Contains("settingsVm.PollingOverrideNote =", apply);
        }

        /// <summary>The In front now line reads the foreground for itself
        /// and shows only once a program has been read: until then it would
        /// name "-" and say what profile it has.</summary>
        [Fact]
        public void TheInFrontLine_ReadsForItself_AndHidesUntilAProgramIsRead()
        {
            var vm = new SettingsViewModel();
            Assert.False(vm.ShowsInFront);
            vm.EnableAutoProfileSwitching = true;
            Assert.False(vm.ShowsInFront);
            bool raised = false;
            vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(SettingsViewModel.ShowsInFront);
            vm.ForegroundExeName = "game.exe";
            Assert.True(raised);
            Assert.True(vm.ShowsInFront);
            vm.ForegroundExeName = "-";
            Assert.False(vm.ShowsInFront);
            vm.ForegroundExeName = "game.exe";
            vm.EnableAutoProfileSwitching = false;
            Assert.False(vm.ShowsInFront);

            Assert.Contains("var (inFront, matchedId) = ForegroundMonitorService.ReadInFront();", Service());
            Assert.DoesNotContain("_foregroundMonitor.LastForegroundExePath", Service());
            Assert.Contains("Visibility=\"{Binding ShowsInFront, Converter={StaticResource BoolToVisibilityConverter}}\"",
                Text("PadForge.App", "Views", "ProfilesPage.xaml"));
        }

        /// <summary>A card that opened or folded is saved quietly and is no
        /// change to a setting, so it does not show as unsaved, and the two
        /// flags that say so are set as one step.</summary>
        [Fact]
        public void AFoldedCard_DoesNotShowAsUnsaved()
        {
            string settings = Text("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("if (!_onlyViewStateIsDirty) _mainVm.Settings.HasUnsavedChanges = true;", settings);
            Assert.Matches(@"lock \(_dirtyGate\)\s+\{\s+_onlyViewStateIsDirty = viewState && \(!IsDirty \|\| _onlyViewStateIsDirty\);\s+IsDirty = true;\s+\}",
                settings);
        }

        /// <summary>The Dreamcast Screen action checks the port again after
        /// its dialog, as the two actions beside it do.</summary>
        [Fact]
        public void TheDreamcastScreen_RechecksThePortAfterItsDialog()
        {
            string window = Text("PadForge.App", "MainWindow.BlissBox.cs");
            Assert.Matches(@"if \(choice == null\) return;(\s+//[^\n]*)+\s+if \(port\.Replaced \|\| port\.Session\.Busy\) return;\s+if \(profile != null\) profile\.DreamcastPicture = choice\.ProfilePicture;",
                window);
        }

        /// <summary>The Add a Controller view has no data until it opens,
        /// as it has none after it closes. Without it the view's bindings
        /// read the window's own data at launch and logged five
        /// errors.</summary>
        [Fact]
        public void TheAddControllerView_StartsWithNoData()
        {
            Assert.Contains("<views:AddControllerView x:Name=\"AddControllerView\" DataContext=\"{x:Null}\"/>",
                Text("PadForge.App", "MainWindow.xaml"));
        }

        /// <summary>A Home card's summary holds no member that nothing
        /// reads: the card's words come from StateWord and its roster.</summary>
        [Fact]
        public void AHomeCardsSummary_HoldsNoUnreadMember()
        {
            Type summary = typeof(DashboardViewModel).Assembly.GetTypes().Single(t => t.Name == "SlotSummary");
            foreach (string gone in new[] { "StatusText", "IsActive", "DeviceName", "BatteryText" })
                Assert.Null(summary.GetProperty(gone));
            Assert.NotNull(summary.GetProperty("StateWord"));
        }
    }

    /// <summary>
    /// The controller page and its view models, in the shapes the audit of
    /// 2026-11-13 found them wrong: what a picker keeps through a rebuild
    /// of its list, what a reset writes, and which words follow a language
    /// change.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261113PageTests
    {
        private static string Text(params string[] parts) => ExtrasPageTests.RepoText(parts).Replace("\r", "");

        private static string PageCode() => Text("PadForge.App", "Views", "PadPage.xaml.cs");

        private static string Page() => Text("PadForge.App", "Views", "PadPage.xaml");

        /// <summary>A section's header button holds an icon beside its
        /// text, so its name is read from its accessible name. Read from
        /// its content, the name was empty for every section and Search's
        /// where line lost it.</summary>
        [Fact]
        public void ASectionsName_IsReadFromItsButtonsAccessibleName()
        {
            string code = PageCode();
            Assert.Contains("? System.Windows.Automation.AutomationProperties.GetName(button) ?? string.Empty", code);
            Assert.DoesNotContain("Content as TextBlock)?.Text", code);
            string page = Page();
            foreach (string button in new[] { "SectionButtons", "SectionSticks", "SectionRumble", "SectionMotion", "SectionLights", "SectionMacros" })
            {
                int at = page.IndexOf("<RadioButton x:Name=\"" + button + "\"", StringComparison.Ordinal);
                Assert.True(at > 0, button);
                string tag = page.Substring(at, page.IndexOf('>', at) - at);
                Assert.Contains("AutomationProperties.Name=\"{Binding ", tag);
            }
        }

        /// <summary>A jump to the tab already selected changes no value, so
        /// the page scrolls to its group itself.</summary>
        [Fact]
        public void AJumpToTheSelectedTab_StillGoesToItsGroup()
        {
            string code = PageCode();
            Assert.Matches(@"bool same = vm\.SelectedConfigTab == tab;\s+try \{ vm\.SelectedConfigTab = tab; \}\s+finally \{ _keepScroll = was; \}(\s+//[^\n]*)+\s+if \(same && !keepScroll\) ScrollGroupToTop\(tab\);",
                code);
        }

        /// <summary>Reset All on a MIDI card resets the two starts before
        /// the two counts, which are held under 128 less their start.</summary>
        [Fact]
        public void ResetAllOnAMidiCard_ResetsTheStartsBeforeTheCounts()
        {
            var config = new MidiSlotConfig { StartCc = 125, StartNote = 120 };
            Assert.Equal(3, config.CcCount);
            Assert.Equal(8, config.NoteCount);
            string[] fields = Array.FindAll(PadForge.Views.PadPage.MidiFields, f => f != nameof(MidiSlotConfig.OutputPort));
            Assert.True(PadForge.Views.PadPage.ResetMidiFields(config, fields));
            Assert.Equal((1, 6, 60, 11), (config.StartCc, config.CcCount, config.StartNote, config.NoteCount));
            Assert.False(PadForge.Views.PadPage.ResetMidiFields(config, "NoSuchField"));
        }

        /// <summary>A slot's rows reach the table after the page's own
        /// DataContext handler has run, so the picked row is selected again
        /// once they have.</summary>
        [Fact]
        public void ASlotThatComesBackWithAPick_SelectsItsRowOnceTheRowsMoved()
        {
            Assert.Matches(@"SyncPickedRowSelection\(\);(\s+//[^\n]*)+\s+Dispatcher\.BeginInvoke\(System\.Windows\.Threading\.DispatcherPriority\.DataBind, new Action\(SyncPickedRowSelection\)\);",
                PageCode());
        }

        /// <summary>A Switch Layer step's picker has a list of its own,
        /// kept in place: the same entry stands for a mask through a rebuild
        /// of the tabs, and "Any layer" is no target.</summary>
        [Fact]
        public void ASwitchLayerStepsPicker_KeepsItsEntriesThroughARebuild()
        {
            var vm = new PadViewModel(0);
            var acts = new System.Collections.Generic.List<PadForge.Engine.Data.ShiftActivator>
            {
                new PadForge.Engine.Data.ShiftActivator { LayerMask = "Shift", LayerName = "Shift", Mode = "Hold", Descriptor = "Button 9" },
            };
            vm.RebuildLayerTabs(acts);
            Assert.Equal(new[] { "Base", "Shift" }, vm.SwitchLayerChoices.Select(c => c.LayerMask).ToArray());
            var before = vm.SwitchLayerChoices.ToArray();

            acts[0].LayerName = "Combat";
            vm.RebuildLayerTabs(acts);
            Assert.Same(before[0], vm.SwitchLayerChoices[0]);
            Assert.Same(before[1], vm.SwitchLayerChoices[1]);
            Assert.Equal("Combat", vm.SwitchLayerChoices[1].LayerName);
            // The tabs themselves are made again, which is what cost a
            // picker bound to them its pick.
            Assert.DoesNotContain(vm.LayerTabs, t => ReferenceEquals(t, before[1]));
            Assert.Equal(string.Empty, vm.MacroLayerChoices[0].LayerMask);

            string page = Page();
            int at = page.IndexOf("SelectedValue=\"{Binding SwitchLayerMask, Mode=TwoWay}\"", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.Contains("ItemsSource=\"{Binding DataContext.SwitchLayerChoices, RelativeSource={RelativeSource AncestorType=UserControl}}\"",
                page.Substring(at - 220, 220));
        }

        /// <summary>The Trigger Routing fold's summary reads one flag, on
        /// while either trigger's rumble is routed from a source.</summary>
        [Fact]
        public void TheTriggerRoutingSummary_ReadsOneFlag()
        {
            var vm = new PadViewModel(0);
            Assert.False(vm.IsTriggerRoutingOn);
            int raised = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PadViewModel.IsTriggerRoutingOn)) raised++; };
            vm.LeftTriggerRouteSource = "MainLeft";
            Assert.True(vm.IsTriggerRoutingOn);
            vm.LeftTriggerRouteSource = "None";
            vm.RightTriggerRouteSource = "SumOfBoth";
            Assert.True(vm.IsTriggerRoutingOn);
            Assert.Equal(3, raised);
            Assert.True(PadForge.Converters.OnOffTextConverter.IsOn(true));
            Assert.False(PadForge.Converters.OnOffTextConverter.IsOn("MainLeft"));

            string page = Page();
            Assert.Contains("<Binding Path=\"IsTriggerRoutingOn\"/>", page);
            Assert.DoesNotContain("<Binding Path=\"LeftTriggerRouteSource\"/>", page);
        }

        /// <summary>The row's Invert is inert on a primary of a key kind,
        /// whose save writes the kind's own holder, and says so by being
        /// disabled.</summary>
        [Fact]
        public void TheRowsInvert_IsInertOnAKeyKindPrimary()
        {
            var row = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            Assert.True(row.IsInvertApplicable);
            bool raised = false;
            row.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(MappingItem.IsInvertApplicable);
            row.PrimaryKindSource.Kind = "Ramp";
            Assert.True(raised);
            Assert.False(row.IsInvertApplicable);
            row.PrimaryKindSource.Kind = "Direct";
            Assert.True(row.IsInvertApplicable);
        }

        /// <summary>A hotbar's engage deadzone resets to the hotbar's own
        /// default, the value a switch to the style sets.</summary>
        [Fact]
        public void AHotbarsDeadzoneReset_IsTheHotbarsDefault()
        {
            var menu = new MenuEditorItem(new PadForge.Engine.Menus.MenuDefinitionEntry
            {
                MenuId = 1, Kind = PadForge.Engine.Menus.MenuKind.Radial, CellCount = 4, HostDescriptor = "Gamepad DPad",
            });
            menu.KindIndex = 3;
            Assert.True(menu.IsHotbar);
            int hotbar = menu.EngageDeadzonePercent;
            Assert.Equal(PadForge.Engine.Menus.MenuHotbar.SteamDeadzonePercent, hotbar);
            menu.EngageDeadzonePercent = 40;
            menu.ResetDeadzoneCommand.Execute(null);
            Assert.Equal(hotbar, menu.EngageDeadzonePercent);
            Assert.NotEqual(25, hotbar);
        }

        /// <summary>An option of a list says when the language changed, so
        /// a box reads its text again and its list is never replaced under
        /// a selection. The pairing identity list is one such list.</summary>
        [Fact]
        public void AnOptionSaysWhenTheLanguageChanged_AndItsListStays()
        {
            string word = "one";
            var option = new GyroLabeledOption(() => word, "1");
            string raised = null;
            option.PropertyChanged += (_, e) => raised = e.PropertyName;
            word = "eins";
            typeof(GyroLabeledOption).GetMethod("OnCultureChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(option, null);
            Assert.Equal(nameof(GyroLabeledOption.Display), raised);
            Assert.Equal("eins", option.Display);

            var settings = new SettingsViewModel();
            Assert.Same(settings.IdentityProtectionModes, settings.IdentityProtectionModes);
            Assert.Equal(new[] { "0", "1", "2" }, settings.IdentityProtectionModes.Select(o => o.Value).ToArray());
            Assert.All(settings.IdentityProtectionModes, o => Assert.False(string.IsNullOrEmpty(o.Display)));
            string extras = Text("PadForge.App", "Views", "ExtrasPage.xaml");
            Assert.Contains("ItemsSource=\"{Binding RemoteLink.IdentityProtectionModes}\" DisplayMemberPath=\"Display\"", extras);
            Assert.DoesNotContain("OnPropertyChanged(nameof(IdentityProtectionModes));",
                Text("PadForge.App", "ViewModels", "SettingsViewModel.cs"));
        }

        /// <summary>The words a language change must raise by hand, because
        /// nothing else rewrites them, are raised.</summary>
        [Fact]
        public void ALanguageChange_RaisesTheWordsNothingElseRewrites()
        {
            string pad = Text("PadForge.App", "ViewModels", "PadViewModel.cs");
            int from = pad.IndexOf("protected override void OnCultureChanged()", StringComparison.Ordinal);
            string onCulture = pad.Substring(from, 4500);
            foreach (string line in new[]
            {
                "foreach (var dev in MappedDevices) dev.RaiseWords();", "foreach (var voice in RumbleAudioVoices) voice.RaiseWords();",
                "OnPropertyChanged(nameof(GyroOutputCurveOptions));", "OnPropertyChanged(nameof(GyroSensitivityUnitsOptions));",
                "OnPropertyChanged(nameof(GyroSpaceOptions));", "OnPropertyChanged(nameof(MotionGripOptions));",
            })
                Assert.Contains(line, onCulture);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(onCulture, @"OnPropertyChanged\(nameof\(PictureLineText\)\);"));

            Assert.Contains("OnPropertyChanged(nameof(PresetNameBoth));", Text("PadForge.App", "ViewModels", "StickConfigItem.cs"));
            string devices = Text("PadForge.App", "ViewModels", "DevicesViewModel.cs");
            Assert.Contains("OnPropertyChanged(nameof(TouchpadLabel));", devices);
            Assert.Contains("OnPropertyChanged(nameof(Touchpad2Label));", devices);
            string row = Text("PadForge.App", "ViewModels", "DeviceRowViewModel.cs");
            int handler = row.IndexOf("private void OnCultureChanged()", StringComparison.Ordinal);
            Assert.Contains("OnPropertyChanged(nameof(HandheldDaemonWarning));", row.Substring(handler, 1500));
            string code = PageCode();
            Assert.Contains("Strings.CultureChanged += RewordComposedLines;", code);
            Assert.Contains("private void RewordComposedLines() => SyncTabVisibility();", code);
        }

        /// <summary>A touchpad past the device's new count goes back to the
        /// first before the list of pads is raised.</summary>
        [Fact]
        public void ThePickedTouchpad_StaysInsideTheCount()
        {
            Assert.Matches(@"if \(_selectedTouchpadIndex >= Math\.Max\(1, _maxTouchpadIndex\)\) SelectedTouchpadIndex = 0;\s+OnPropertyChanged\(nameof\(HasMultipleTouchpads\)\);\s+OnPropertyChanged\(nameof\(TouchpadIndexOptions\)\);",
                Text("PadForge.App", "ViewModels", "PadViewModel.Touchpad.cs"));
        }

        /// <summary>Two entries for one audio endpoint are the same entry,
        /// so the mic light's picker keeps its pick when its list is made
        /// again.</summary>
        [Fact]
        public void TwoEntriesForOneEndpoint_AreTheSameEntry()
        {
            var a = new MicLedDeviceItem("{0.0.1}.{abc}", "Headset");
            var b = new MicLedDeviceItem("{0.0.1}.{abc}", "Headset (2)");
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, new MicLedDeviceItem("{0.0.1}.{ABC}", "Headset"));
            Assert.Contains(b, new[] { a });
        }

        /// <summary>The small fixes of the Profiles and Add a Controller
        /// code: a deleted row ends its recording, a rebuilt device list
        /// restores before it raises, the type names live in one table and
        /// a join of words takes the language's separator.</summary>
        [Fact]
        public void TheProfilesAndAddControllerCode_HoldsItsSmallFixes()
        {
            Assert.Matches(@"if \(ReferenceEquals\(_recordingShortcut, shortcut\)\) CancelRecording\(\);\s+vm\.ProfileShortcuts\.Remove\(shortcut\);",
                Text("PadForge.App", "Views", "ProfilesPage.xaml.cs"));
            Assert.Matches(@"Data\.TriggerDeviceGuid = currentGuid;\s+OnPropertyChanged\(nameof\(TriggerDeviceGuid\)\);\s+\}",
                Text("PadForge.App", "ViewModels", "ProfileShortcutViewModel.cs"));
            string add = Text("PadForge.App", "ViewModels", "AddControllerViewModel.cs");
            Assert.Contains("internal static string NameOf(VirtualControllerType type) => SlotTypeIconMap.NameFor(type);", add);
            Assert.DoesNotContain("Strings.Instance.ControllerType_", add);
            Assert.Contains("name + Strings.Instance.Search_WhereSeparator + line", add);
            Assert.Contains("private static string NameOf(Engine.VirtualControllerType type) => SlotTypeIconMap.NameFor(type);",
                Text("PadForge.App", "ViewModels", "SettingsViewModel.cs"));
            Assert.Contains("Strings.Instance.Pad_Transport_Bluetooth + Strings.Instance.Search_WhereSeparator",
                Text("PadForge.App", "ViewModels", "DashboardViewModel.FirstRun.cs"));
            Assert.Equal(PadForge.ViewModels.ControllerTypeChoice.NameOf(PadForge.Engine.VirtualControllerType.Nintendo),
                PadForge.ViewModels.SlotTypeIconMap.NameFor(PadForge.Engine.VirtualControllerType.Nintendo));
        }
    }

    /// <summary>
    /// Search and the dialogs, in the shapes the audit of 2026-11-13 found
    /// them wrong: which entries Search can find on a page, which key a
    /// focused control gets, and what a dialog does with a press, with
    /// Enter and with a value it cannot list.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class AuditDelta20261113SearchTests
    {
        private static string Text(params string[] parts) => ExtrasPageTests.RepoText(parts).Replace("\r", "");

        /// <summary>Search reads a row option's rules on the mapping row as
        /// WPF reads them: a part that sets its own data (the Primary Mode
        /// rows read the row's PrimaryKindSource) has its rules read through
        /// that data, and every rule of a row option names a path a mapping
        /// row has. Read on the row itself, the kind rules found nothing. One
        /// search of the runtime sweep wrote 264 binding errors, and the
        /// options under those rules never came up as results.</summary>
        [Fact]
        public void ARowOptionsRules_AreReadThroughTheDataItsPartSets()
        {
            var markup = PadForge.Search.SearchSources.FileNames.ToDictionary(
                p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
            var index = PadForge.Search.SearchIndexBuilder.Read(markup, PadForge.Views.PadPage.TabOfGroupName);
            var gates = index.Entries.Where(e => e.RowOptions).SelectMany(e => e.ItemGates).Select(g => g.Path).Distinct().ToList();
            Assert.Contains("PrimaryKindSource.IsRampedKind", gates);
            Assert.Contains("PrimaryKindSource.UsesUpDownKeys", gates);
            Assert.DoesNotContain("IsRampedKind", gates);
            foreach (string path in gates)
            {
                Type type = typeof(MappingItem);
                foreach (string step in path.Split('.'))
                {
                    var property = type.GetProperty(step, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    Assert.True(property != null, path + ": " + type.Name + " has no " + step);
                    type = property!.PropertyType;
                }
            }
        }

        /// <summary>An entry made from an element with a name of its own
        /// (a fold card, a button, a tile, a reset) has that element as its
        /// scope, and is found: the walk of a scope starts at the scope's
        /// own element. No two entries of the page resolve to one
        /// element.</summary>
        [Fact]
        public void AnEntryMadeFromANamedElement_IsFound_AndNoTwoEntriesShareAnElement()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new SettingsViewModel();
                var page = new PadForge.Views.SettingsPage { DataContext = vm };
                page.Measure(new System.Windows.Size(1000, 800));
                page.Arrange(new System.Windows.Rect(0, 0, 1000, 800));
                page.UpdateLayout();

                var markup = PadForge.Search.SearchSources.FileNames.ToDictionary(p => p.Key,
                    p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
                var index = PadForge.Search.SearchIndexBuilder.Read(markup, PadForge.Views.PadPage.TabOfGroupName).Entries;
                System.Windows.FrameworkElement PageOf(PadForge.Search.SearchPage p)
                    => p == PadForge.Search.SearchPage.Settings ? page : null;
                var service = new PadForge.Search.SearchService(new MainViewModel(), PageOf);
                service.SetIndex(index);

                var entries = index.Where(e => e.Page == PadForge.Search.SearchPage.Settings && e.ItemsPath == null && !e.RowOptions).ToList();
                int own = 0;
                foreach (var entry in entries)
                {
                    if (entry.ScopeName == null || page.FindName(entry.ScopeName) is not System.Windows.DependencyObject named) continue;
                    string words = PadForge.Search.SearchLocator.LabelText(entry.Label, vm);
                    if (string.IsNullOrEmpty(words) || PadForge.Search.SearchLocator.TextOf(named, entry.LabelSource) != words) continue;
                    own++;
                    Assert.Same(named, service.Locator.Find(entry, index));
                }
                Assert.True(own > 0, "the Settings page holds no entry made from a named element, so this proves nothing");

                var found = entries.Select(e => (Entry: e, Element: service.Locator.Find(e, index))).Where(x => x.Element != null).ToList();
                var shared = found.GroupBy(x => x.Element, System.Collections.Generic.ReferenceEqualityComparer.Instance)
                    .Where(g => g.Count() > 1)
                    .Select(g => string.Join(" and ", g.Select(x => x.Entry.Kind + " " + PadForge.Search.SearchLocator.LabelText(x.Entry.Label, vm))))
                    .ToList();
                Assert.True(shared.Count == 0, "entries that resolve to one element: " + string.Join("; ", shared));
            }, TimeSpan.FromMinutes(3), "the Search locator run timed out");
        }

        /// <summary>The count that picks the Nth element runs over the list
        /// the entry is looked up in, where a card's title stands among the
        /// texts, and a title that runs on past its words is counted after
        /// the texts that are the words exactly.</summary>
        [Fact]
        public void TheLocatorsCount_RunsOverTheListAnEntryIsLookedUpIn()
        {
            string locator = Text("PadForge.App", "Search", "SearchLocator.cs");
            Assert.Contains("&& ListOf(other.LabelSource) == ListOf(entry.LabelSource) && LabelText(other.Label, page.DataContext) == text)", locator);
            Assert.Contains("=> source == SearchLabelSource.CardTitle ? SearchLabelSource.Text : source;", locator);
            Assert.Contains("int at = n - (list?.Count ?? 0);", locator);
            Assert.Contains("if (at < all.Count) return all[at];", locator);
            Assert.DoesNotContain("return all[0];", locator);
            Assert.Contains("foreach (var d in new[] { scope }.Concat(LogicalDescendants(scope)))", locator);
        }

        /// <summary>Enter on the focused close chip is the chip's own, Tab
        /// reaches the query box before the chip, and the documentation
        /// result does nothing on a PC with no program for web pages.</summary>
        [Fact]
        public void TheSearchPanelsKeys_GoWhereTheFocusIs()
        {
            Assert.Matches(@"else if \(e\.Key == Key\.Enter && Keyboard\.Modifiers == ModifierKeys\.None\s+&& !ReferenceEquals\(Keyboard\.FocusedElement, SearchCloseChip\)\)",
                Text("PadForge.App", "Views", "SearchPanel.xaml.cs"));
            string panel = Text("PadForge.App", "Views", "SearchPanel.xaml");
            Assert.Contains("<controls:ClickBorder x:Name=\"SearchCloseChip\" DockPanel.Dock=\"Right\" Style=\"{StaticResource KeyChip}\" KeyboardNavigation.TabIndex=\"1\"", panel);
            Assert.Contains("<TextBox x:Name=\"SearchQueryBox\" Height=\"32\" FontSize=\"17\" Padding=\"0\" BorderThickness=\"0\" TabIndex=\"0\"", panel);
            Assert.Matches(@"case SearchTarget\.Docs:\s+try\s+\{\s+PadForge\.Common\.ShellLaunch\.Start\([^\n]+\n\s+\}\s+catch \(System\.ComponentModel\.Win32Exception\)",
                Text("PadForge.App", "Search", "SearchNavigator.cs"));
        }

        /// <summary>The community panel: a failed run leaves nothing to
        /// save, Back stops the game's waiting search, Enter with a search
        /// pending runs that search, and a slot token of underscores reads
        /// as the Other group.</summary>
        [Fact]
        public void TheCommunityPanel_HoldsItsFourFixes()
        {
            string panel = Text("PadForge.App", "Views", "CommunityConfigsPanel.xaml.cs");
            Assert.Matches(@"catch \(Exception ex\)\s+\{(\s+//[^\n]*)+\s+_outcome = null;\s+ShowManifestError\(ex\.Message\);", panel);
            Assert.Matches(@"_gameCts\?\.Cancel\(\);(\s+//[^\n]*)+\s+_configSearchDebounce\?\.Stop\(\);\s+ClearManifest\(\);\s+SetState\(WsState\.Search\);", panel);
            Assert.Matches(@"if \(key == Key\.Enter && _searchDebounce\.IsEnabled\)\s+\{(\s+//[^\n]*)+\s+_searchDebounce\.Stop\(\);\s+_ = RunSearchAsync\(_searchText\);\s+return true;\s+\}\s+if \(Games\.Count == 0\) return false;",
                panel);

            var prettify = typeof(PadForge.Views.CommunityConfigsPanel).GetMethod("PrettifySlotToken",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(prettify);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Workshop_GroupOther, (string)prettify.Invoke(null, new object[] { "__" }));
            Assert.Equal("Left stick", (string)prettify.Invoke(null, new object[] { "left_stick" }));
        }

        /// <summary>A press in a dialog's body moves the window: the body's
        /// own scroller is no control, and any other scroller, a button and
        /// a box still keep the press.</summary>
        [Fact]
        public void APressInADialogsBody_MovesTheWindow()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var label = new System.Windows.Controls.TextBlock { Text = "Body" };
                var inner = new System.Windows.Controls.TextBlock { Text = "List" };
                var button = new System.Windows.Controls.Button { Content = "Go" };
                var list = new System.Windows.Controls.ScrollViewer { Content = inner };
                var stack = new System.Windows.Controls.StackPanel();
                stack.Children.Add(label);
                stack.Children.Add(list);
                stack.Children.Add(button);
                var body = new System.Windows.Controls.ScrollViewer { Name = "PART_BodyScroll", Content = stack };
                body.Measure(new System.Windows.Size(400, 300));
                body.Arrange(new System.Windows.Rect(0, 0, 400, 300));
                body.UpdateLayout();
                Assert.False(PadForge.Views.DialogWindow.PressLandedOnSomethingClickable(label));
                Assert.True(PadForge.Views.DialogWindow.PressLandedOnSomethingClickable(inner));
                Assert.True(PadForge.Views.DialogWindow.PressLandedOnSomethingClickable(button));
            }, TimeSpan.FromMinutes(1), "the dialog press run timed out");
        }

        /// <summary>The dialogs: the theme is followed from a window's
        /// first show, Enter imports from the picked device, Enter in a
        /// value box saves what was typed, and a polling interval the list
        /// does not hold gets an entry of its own.</summary>
        [Fact]
        public void TheDialogs_HoldTheirFixes()
        {
            string window = Text("PadForge.App", "Views", "DialogWindow.cs");
            int shown = window.IndexOf("protected override void OnSourceInitialized(EventArgs e)", StringComparison.Ordinal);
            int closed = window.IndexOf("protected override void OnClosed(EventArgs e)", StringComparison.Ordinal);
            int subscribe = window.IndexOf("ApplicationThemeManager.Changed += OnThemeChanged;", StringComparison.Ordinal);
            Assert.True(shown > 0 && subscribe > shown && subscribe < closed, "the theme is not followed from the first show");
            Assert.Equal(subscribe, window.LastIndexOf("ApplicationThemeManager.Changed += OnThemeChanged;", StringComparison.Ordinal));

            Assert.Contains("<Button x:Name=\"ImportButton\" Style=\"{StaticResource DialogButton}\" Margin=\"10,0,0,0\" IsEnabled=\"False\" IsDefault=\"True\"",
                Text("PadForge.App", "Views", "ManageProfilesDialog.xaml"));
            Assert.Matches(@"private void SaveButton_Click\(object sender, RoutedEventArgs e\)\s+\{(\s+//[^\n]*)+\s+if \(System\.Windows\.Input\.Keyboard\.FocusedElement is System\.Windows\.Controls\.TextBox typed\)\s+typed\.GetBindingExpression\(System\.Windows\.Controls\.TextBox\.TextProperty\)\?\.UpdateSource\(\);",
                Text("PadForge.App", "Views", "ShiftActivatorDialog.xaml.cs"));
            string profile = Text("PadForge.App", "Views", "ProfileDialog.xaml.cs");
            Assert.Contains("=> _pollingMs[System.Math.Clamp(PollingRateBox.SelectedIndex, 0, _pollingMs.Count - 1)];", profile);
            Assert.Contains("_pollingMs.Insert(idx, ms);", profile);
            Assert.DoesNotContain("PollingRateBox.SelectedIndex = idx >= 0 ? idx : 0;", profile);

            string home = Text("PadForge.App", "Views", "HomePage.xaml.cs");
            Assert.Matches(@"SlotsItemsControl\.LostMouseCapture -= OnDragCaptureLost;\s+SlotsItemsControl\.LostMouseCapture \+= OnDragCaptureLost;", home);
            Assert.DoesNotContain("using NavigationView", home);
        }
    }

    /// <summary>
    /// The shell, the Devices page and two helpers under every page, in the
    /// shapes the audit of 2026-11-13 found them wrong.
    /// </summary>
    public class AuditDelta20261113ShellTests
    {
        private static string Text(params string[] parts) => ExtrasPageTests.RepoText(parts).Replace("\r", "");

        /// <summary>Half a surrogate pair is no glyph and raises nothing: a
        /// device's name can be cut on one, and the test runs on every text
        /// under a button.</summary>
        [Fact]
        public void HalfASurrogatePair_IsNoGlyph()
        {
            var isGlyphOnly = typeof(PadForge.Common.AccessibleNames).GetMethod("IsGlyphOnly",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(isGlyphOnly);
            // Built here: a string in an attribute cannot hold half a pair,
            // and the compiler puts another character in its place.
            static string Of(params int[] units) => new string(Array.ConvertAll(units, u => (char)u));
            var rows = new (string Text, bool GlyphOnly)[]
            {
                (Of(0xE700), true),
                (Of(' ', 0xE700, ' ', 0xE8FB, ' '), true),
                (Of(0xDB80, 0xDC00), true),
                (Of(0xD800), false),
                (Of(0xDC00), false),
                (Of(0xE700, 0xD800), false),
                (Of('A', 0xD800), false),
                (Of(0xD800, 'A'), false),
                ("Pad", false),
                (string.Empty, false),
            };
            foreach (var (text, glyphOnly) in rows)
                Assert.Equal(glyphOnly, (bool)isGlyphOnly.Invoke(null, new object[] { text }));
            Assert.True(char.IsHighSurrogate(rows[3].Text[0]) && rows[3].Text.Length == 1);
        }

        /// <summary>A list item's peer is looked for among the items the
        /// list already holds before the list reads its items again.</summary>
        [Fact]
        public void AListReadsItsItemsAgain_OnlyForOneItDoesNotHold()
        {
            string peers = Text("PadForge.App", "Common", "ListItemPeers.cs");
            int first = peers.IndexOf("if (RefreshIn(list.GetChildren(), item))", StringComparison.Ordinal);
            int reset = peers.IndexOf("list.ResetChildrenCache();", StringComparison.Ordinal);
            Assert.True(first > 0 && reset > first);
            Assert.Equal(reset, peers.LastIndexOf("list.ResetChildrenCache();", StringComparison.Ordinal));
        }

        /// <summary>A sidebar pill: its type icon takes its color by
        /// reference, so a theme switch reaches it, and its X takes no press
        /// while hidden, shows for a touch when the tap ends, and sits on
        /// the pill's own ground.</summary>
        [Fact]
        public void ASidebarPill_HoldsItsThreeFixes()
        {
            string window = Text("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("string chipInk = lit ? \"EmberInkBrush\" : \"TextFillColorPrimaryBrush\";", window);
            Assert.Contains("logo.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, chipInk);", window);
            Assert.Contains("text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, chipInk);", window);
            Assert.DoesNotContain("(System.Windows.Media.Brush)FindResource(lit ?", window);

            Assert.Matches(@"Child = deleteWash,\s+Opacity = 0,\s+IsHitTestVisible = false,", window);
            Assert.Matches(@"deleteHost\.Opacity = shown \? 1\.0 : 0;\s+deleteHost\.IsHitTestVisible = shown;", window);
            Assert.Contains("card.MouseEnter += (s, e) => { if (e.StylusDevice == null) Show(true); };", window);
            Assert.Contains("card.PreviewMouseLeftButtonUp += (s, e) => { if (e.StylusDevice != null) Show(true); };", window);
            Assert.Matches(@"if \(lit && !picked && !card\.IsMouseOver\)\s+deleteWash\.SetResourceReference\(System\.Windows\.Controls\.Border\.BackgroundProperty, ""EmberWashBrush""\);\s+else\s+deleteWash\.Background = null;",
                window);
        }

        /// <summary>The Devices page: the hide warning is picked by the
        /// setting its box writes, its glyph takes the theme's caution
        /// color, and a row's drag is armed only while the pointer is on the
        /// card it was pressed on.</summary>
        [Fact]
        public void TheDevicesPage_HoldsItsThreeFixes()
        {
            string page = Text("PadForge.App", "Views", "DevicesPage.xaml.cs");
            Assert.DoesNotContain("Contains(\"HidHide\")", page);
            Assert.Contains("?.ParentBinding?.Path?.Path?.EndsWith(nameof(ViewModels.DeviceRowViewModel.HidHideEnabled), StringComparison.Ordinal) == true;", page);
            Assert.Contains("IsChecked=\"{Binding SelectedDevice.HidHideEnabled, Mode=TwoWay}\"", Text("PadForge.App", "Views", "DevicesPage.xaml"));
            Assert.DoesNotContain("Brushes.Orange", page);
            Assert.Contains("warningIcon.SetResourceReference(TextBlock.ForegroundProperty, \"SystemFillColorCautionBrush\");", page);
            Assert.Matches(@"card\.MouseLeave -= DeviceCard_MouseLeave;\s+card\.MouseLeave \+= DeviceCard_MouseLeave;", page);
            Assert.Matches(@"private void DeviceCard_MouseLeave\(object sender, MouseEventArgs e\)\s+\{\s+_deviceDragStarted = false;", page);
        }

        /// <summary>A keyboard that opens the controller page's device
        /// filter is taken to its first box, Tab stays among the boxes and
        /// Escape closes it. A model the 3D view loads is reported at the
        /// next frame.</summary>
        [Fact]
        public void TheDeviceFilter_TakesAKeyboard_AndAModelLoadIsReported()
        {
            string page = Text("PadForge.App", "Views", "PadPage.xaml");
            Assert.Contains("<Popup IsOpen=\"{Binding IsChecked, ElementName=MappingDeviceFilterToggle}\" Opened=\"DeviceFilterPopup_Opened\"", page);
            Assert.Contains("KeyboardNavigation.TabNavigation=\"Cycle\" PreviewKeyDown=\"DeviceFilterPopup_KeyDown\">", page);
            string code = Text("PadForge.App", "Views", "PadPage.xaml.cs");
            Assert.Contains("if (System.Windows.Input.InputManager.Current.MostRecentInputDevice is not KeyboardDevice) return;", code);
            Assert.Contains("body.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))", code);
            Assert.Matches(@"if \(e\.Key != Key\.Escape\) return;\s+MappingDeviceFilterToggle\.IsChecked = false;\s+MappingDeviceFilterToggle\.Focus\(\);", code);
            Assert.Matches(@"_pickShapes\.Clear\(\);(\s+//[^\n]*)+\s+_lastToScreen = default;", Text("PadForge.App", "Views", "ControllerModelView.xaml.cs"));
        }

        /// <summary>The profile switcher takes the type bar's surface, edge
        /// and corner by reference, so the shell's two flyouts match and
        /// follow a theme switch with no handler of their own.</summary>
        [Fact]
        public void TheProfileSwitcher_TakesTheTypeBarsSurface()
        {
            string window = Text("PadForge.App", "MainWindow.xaml.cs");
            int at = window.IndexOf("_profileSwitcherPopup = popup;", StringComparison.Ordinal);
            Assert.True(at > 0);
            string flyout = window.Substring(at, 3200);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(flyout, @"CornerRadius = new CornerRadius\(10\),").Count);
            Assert.Contains("shadowBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, \"SteelRaisedBrush\");", flyout);
            Assert.Contains("border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, \"SteelRaisedBrush\");", flyout);
            Assert.Contains("border.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, \"SteelPillLineBrush\");", flyout);
            Assert.DoesNotContain("ApplyPopupTheme", window);
            Assert.DoesNotContain("FromRgb(0x1B, 0x23, 0x33)", window);
        }

        /// <summary>A plain button's edge under the pointer comes from the
        /// key wpf-ui's template reads there, a quiet danger button fixes no
        /// ink for a state the theme colors, an icon button's pressed fill
        /// follows the theme, and a fixed dark fill carries a fixed light
        /// ink.</summary>
        [Fact]
        public void ButtonStates_TakeTheKeysTheTemplateReads_AndFollowTheTheme()
        {
            string app = Text("PadForge.App", "App.xaml");
            string dialogs = Text("PadForge.App", "Resources", "Dialogs.xaml");
            // wpf-ui 4.3.0, Controls/Button/Button.xaml: the plain Button's
            // pointer-over trigger sets the edge from ControlElevationBorderBrush
            // and its words from nothing.
            foreach (string unread in new[] { "ButtonBorderBrushPointerOver", "ButtonForegroundPointerOver" })
            {
                Assert.DoesNotContain("x:Key=\"" + unread + "\"", app);
                Assert.DoesNotContain("x:Key=\"" + unread + "\"", dialogs);
            }
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(app, "<SolidColorBrush x:Key=\"ControlElevationBorderBrush\" Color=\"[^\"]+\"/>").Count);
            Assert.Contains("<SolidColorBrush x:Key=\"ControlElevationBorderBrush\" Color=\"Transparent\"/>", dialogs);
            Assert.DoesNotContain("x:Key=\"ButtonForegroundPressed\" Color=\"#FFB08A\"", dialogs);

            Assert.Contains("<Setter Property=\"PressedBackground\" Value=\"{DynamicResource SteelPressedBrush}\"/>", app);
            Assert.DoesNotContain("Value=\"#161D2B\"", app);
            Assert.Contains("<SolidColorBrush x:Key=\"SteelPressedBrush\" Color=\"#161D2B\"/>", app);
            Assert.Contains("SetBrush(\"SteelPressedBrush\", dark ? Color.FromRgb(0x16, 0x1D, 0x2B) : Color.FromRgb(0xD6, 0xD8, 0xDE));",
                Text("PadForge.App", "Common", "EmberTheme.cs"));

            string panel = Text("PadForge.App", "Views", "CommunityConfigsPanel.xaml");
            Assert.Contains("Fill=\"Transparent\" Stroke=\"#8BFFFFFF\"", panel);
            Assert.Matches("<TextBlock Text=\"\\{Binding Initial\\}\"[^>]*Foreground=\"#8BFFFFFF\"", panel);
        }
    }

    /// <summary>
    /// The audit's rows (package 6): labels, resets, hints and the rules
    /// that show a row. Each is read from the markup the page is built
    /// from, and the view model's and the helpers' rules are run.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261113RowTests
    {
        private const string S = ", Source={x:Static strings:Strings.Instance}}";
        private static string Text(params string[] parts) => ExtrasPageTests.RepoText(parts).Replace("\r", "");
        private static string PageText() => Text("PadForge.App", "Views", "PadPage.xaml");
        private static System.Xml.Linq.XElement Xml(params string[] parts) => System.Xml.Linq.XDocument.Parse(ExtrasPageTests.RepoText(parts)).Root;
        private static System.Xml.Linq.XElement Page() => Xml("PadForge.App", "Views", "PadPage.xaml");
        private static string At(System.Xml.Linq.XElement e, string attribute)
            => (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == attribute);
        private static string[] Paths(System.Xml.Linq.XElement holder)
            => holder.Descendants().Where(e => e.Name.LocalName == "Binding").Select(e => At(e, "Path")).ToArray();

        private static void Sta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(20000));
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A reset's tooltip names the row it stands in: Primary
        /// Mode, Combine in its own case, Delay, a rumble's Hold Time, and a
        /// color block's own name before its channel.</summary>
        [Fact]
        public void AResetNamesItsOwnRow()
        {
            var page = Page();
            System.Xml.Linq.XElement ById(string id) => page.Descendants().Single(e => At(e, "AutomationProperties.AutomationId") == id);
            Assert.Equal("{Binding Pad_Mapping_PrimaryMode" + S, At(ById("Reset_MappingSourceItem_Kind_4282"), "SettingLabel"));
            Assert.Equal("{Binding Pad_Combine_Label" + S, At(ById("Reset_MappingItem_CombineMode_4340"), "SettingLabel"));

            // Delay: the row's own name through the reset's format.
            var delayLabel = page.Descendants().Single(e => e.Name.LocalName == "TextBlock" && At(e, "Text") == "{Binding Macro_DelayMs" + S);
            var delayReset = delayLabel.Parent.Elements().Single(e => At(e, "Command") == "{Binding ResetRepeatDelayMsCommand}");
            Assert.Null(At(delayReset, "ToolTip"));
            Assert.Equal(new[] { "Macro_DelayMs", "Pad_ResetSection_Format" }, Paths(delayReset));
            // The two rows named Interval keep the Interval reset.
            Assert.Equal(2, page.Descendants().Count(e => (At(e, "ToolTip") ?? "").StartsWith("{Binding Macro_Reset_Interval,", StringComparison.Ordinal)));

            // A rumble's hold time: the label and the reset say one thing.
            var holdReset = page.Descendants().Single(e => At(e, "CommandParameter") == "RumbleHoldMs");
            Assert.Equal("{Binding Macro_TriggerHoldMs_Label" + S, At(holdReset, "SettingLabel"));
            Assert.Equal("{Binding Macro_TriggerHoldMs_Label" + S, At(holdReset.Parent.Elements().First(), "Text"));
            Assert.DoesNotContain("Macro_RumbleHoldMs_Label", PageText());

            // Nine channel resets in three color blocks: each names its block.
            foreach (var (block, commands) in new[]
                     {
                         ("Pad_Lighting_BatteryLowColor", new[] { "ResetLightbarBatteryLowR", "ResetLightbarBatteryLowG", "ResetLightbarBatteryLowB" }),
                         ("Pad_Lighting_BatteryHighColor", new[] { "ResetLightbarBatteryHighR", "ResetLightbarBatteryHighG", "ResetLightbarBatteryHighB" }),
                         ("Pad_Lighting_InputReactive_FixedColor", new[] { "ResetInputReactiveR", "ResetInputReactiveG", "ResetInputReactiveB" }),
                     })
            {
                string[] channels = { "Pad_ResetLightbarRed", "Pad_ResetLightbarGreen", "Pad_ResetLightbarBlue" };
                for (int i = 0; i < 3; i++)
                {
                    var reset = page.Descendants().Single(e => At(e, "Command") == "{Binding DeviceConfig." + commands[i] + "Command}");
                    Assert.Null(At(reset, "ToolTip"));
                    Assert.Equal(new[] { block, channels[i], "Pad_Lighting_KeyColors_LightColor_Format" }, Paths(reset));
                    // The block the reset names is the block it stands in.
                    Assert.Contains(reset.Ancestors(), a => a.Descendants().Any(t => t.Name.LocalName == "TextBlock"
                        && At(t, "Text") == "{Binding " + block + S) && a.Descendants().Count(d => (At(d, "Command") ?? "").EndsWith("RCommand}", StringComparison.Ordinal)) == 1);
                }
            }
            var s = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal("Low Battery: Reset Red", string.Format(s.Pad_Lighting_KeyColors_LightColor_Format, s.Pad_Lighting_BatteryLowColor, s.Pad_ResetLightbarRed));
            Assert.Equal("Reset Delay", string.Format(s.Pad_ResetSection_Format, s.Macro_DelayMs));
        }

        /// <summary>A reset whose tooltip is put together from two strings
        /// of the language is named by that tooltip for a screen reader: the
        /// tooltip is a string on the button, whichever way the markup
        /// writes its binding.</summary>
        [Fact]
        public void AButtonWithAComposedTooltip_IsNamedByIt() => Sta(() =>
        {
            var button = new Button { Content = "\uE72C" };
            var tip = new System.Windows.Data.MultiBinding { Converter = new PadForge.Converters.FormatTextConverter() };
            tip.Bindings.Add(new System.Windows.Data.Binding { Source = "Low Battery" });
            tip.Bindings.Add(new System.Windows.Data.Binding { Source = "Reset Red" });
            tip.Bindings.Add(new System.Windows.Data.Binding { Source = "{0}: {1}" });
            System.Windows.Data.BindingOperations.SetBinding(button, System.Windows.FrameworkElement.ToolTipProperty, tip);
            Assert.Equal("Low Battery: Reset Red", button.ToolTip);
            PadForge.Common.AccessibleNames.NameButton(button);
            Assert.Equal("Low Battery: Reset Red", System.Windows.Automation.AutomationProperties.GetName(button));
        });

        /// <summary>The count of one-line rows left with the row form it
        /// counted: no readout, no count, and nothing that only fed it. The
        /// pipeline chips still hear of a change to the rows.</summary>
        [Fact]
        public void TheCountOfOneLineRows_IsGone()
        {
            string model = Text("PadForge.App", "ViewModels", "PadViewModel.cs");
            foreach (string source in new[] { PageText(), model, Text("PadForge.App", "ViewModels", "MappingItem.cs"), Text("PadForge.App", "ViewModels", "DeviceSlotConfig.cs") })
                foreach (string gone in new[] { "DirectMappingCount", "IsTrivialDirect", "_directCountHooked", "Text=\"DIRECT\"" })
                    Assert.DoesNotContain(gone, source);
            Assert.Null(typeof(PadViewModel).GetProperty("DirectMappingCount"));
            Assert.Null(typeof(MappingItem).GetProperty("IsTrivialDirect"));
            Assert.Contains("Mappings.CollectionChanged += OnMappingsChangedForPipelineChips;", model);
            Assert.Matches(@"private void OnMappingsChangedForPipelineChips\([^)]*\)\s*\{(\s*//[^\n]*)*\s*_pipelineChipsConfigDirty = true;\s*\}", model);
        }

        /// <summary>A formula's status line takes the theme's status colors,
        /// in the mapping row and in the macro.</summary>
        [Fact]
        public void AStatusLine_TakesTheThemesColors()
        {
            string page = PageText();
            foreach (string hex in new[] { "#16C60C", "#F2A33C", "#C42B1C", "#CA5010", "#E81123" })
                Assert.DoesNotContain("Value=\"" + hex + "\"", page);
            var xml = Page();
            foreach (var (text, warning, invalid, rest) in new[]
                     {
                         ("{Binding CombineExpressionStatus}", "IsCombineExpressionWarning", "IsCombineExpressionInvalid", "{DynamicResource SystemFillColorSuccessBrush}"),
                         ("{Binding CustomExpressionStatus}", "IsCustomExpressionWarning", "IsCustomExpressionInvalid", "{DynamicResource TextFillColorSecondaryBrush}"),
                     })
            {
                var line = xml.Descendants().Single(e => e.Name.LocalName == "TextBlock" && At(e, "Text") == text);
                var style = line.Descendants().Single(e => e.Name.LocalName == "Style");
                Assert.Equal(rest, At(style.Elements().Single(e => e.Name.LocalName == "Setter"), "Value"));
                var triggers = style.Descendants().Where(e => e.Name.LocalName == "DataTrigger").ToList();
                // Warning first, so that invalid has the last word.
                Assert.Equal(new[] { "{Binding " + warning + "}", "{Binding " + invalid + "}" }, triggers.Select(t => At(t, "Binding")));
                Assert.Equal(new[] { "{DynamicResource SystemFillColorCautionBrush}", "{DynamicResource SystemFillColorCriticalBrush}" },
                    triggers.Select(t => At(t.Elements().Single(), "Value")));
            }
        }

        /// <summary>The Mouse Gestures card has one label column, the
        /// section's, for the four rows under its switch. The switch row
        /// is an Ends row, as every switch row of the page is: its label
        /// at the start and the switch at the far end.</summary>
        [Fact]
        public void TheMouseGesturesCard_HasOneLabelColumn()
        {
            var title = Page().Descendants().Single(e => e.Name.LocalName == "TextBlock" && At(e, "Text") == "{Binding Pad_MouseGestures_Header" + S);
            var card = title.Ancestors().First(e => e.Name.LocalName == "Border" && At(e, "Style") == "{StaticResource CardBorder}");
            var rows = card.Descendants().Where(e => e.Name.LocalName == "SettingRow").ToList();
            Assert.Equal(5, rows.Count);
            Assert.Equal("True", At(rows[0], "SettingRow.Ends"));
            Assert.Equal("{StaticResource EndsLabel}", At(rows[0].Elements().First(), "Style"));
            Assert.All(rows.Skip(1), row => Assert.Null(At(row, "SettingRow.Ends")));
            var labels = rows.Select(row => row.Elements().First()).ToList();
            Assert.All(labels, label =>
            {
                Assert.Equal("TextBlock", label.Name.LocalName);
                Assert.Null(At(label, "Width"));
            });
            Assert.All(labels.Skip(1), label => Assert.Equal("{StaticResource DzLabel}", At(label, "Style")));
            Assert.DoesNotContain(card.Descendants(), e => At(e, "Width") == "242");
        }

        /// <summary>An open list row's Record and Clear are Tab stops: the
        /// open row holds nothing else that records or clears it. A closed
        /// row's are not, and neither is the chevron: the row is the stop.</summary>
        [Fact]
        public void AnOpenListRowsRecordAndClear_TakeTheKeyboard()
        {
            var page = Page();
            System.Xml.Linq.XElement Keyed(string key) => page.Descendants().Single(e => e.Name.LocalName == "Style"
                && (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key") == key && e.Parent.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)
                && e.Parent.Parent == page);
            var plain = Keyed("RowIconButton");
            Assert.Contains(plain.Elements(), e => At(e, "Property") == "IsTabStop" && At(e, "Value") == "False");
            var action = Keyed("RowActionButton");
            Assert.Equal("{StaticResource RowIconButton}", At(action, "BasedOn"));
            var open = Assert.Single(action.Descendants(), e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding IsRowSelected}", At(open, "Binding"));
            Assert.Equal("True", At(open, "Value"));
            var stop = Assert.Single(open.Elements());
            Assert.Equal("IsTabStop", At(stop, "Property"));
            Assert.Equal("True", At(stop, "Value"));

            var chevron = page.Descendants().Single(e => At(e, "Click") == "MappingRowToggle_Click" && e.Name.LocalName == "Button");
            Assert.Equal("{StaticResource RowIconButton}", At(chevron, "Style"));
            var buttons = chevron.Parent.Elements().Where(e => e.Name.LocalName == "Button").ToList();
            Assert.Equal(new[] { "{Binding ToggleRecordCommand}", "{Binding ClearCommand}", null }, buttons.Select(b => At(b, "Command")));
            Assert.Equal(new[] { "{StaticResource RowActionButton}", "{StaticResource RowActionButton}", "{StaticResource RowIconButton}" }, buttons.Select(b => At(b, "Style")));
            // The row's own template tells the open row by the same flag.
            Assert.Contains("mm.IsRowSelected = ", Text("PadForge.App", "Views", "PadPage.xaml.cs").Replace("rm.", "mm.").Replace("am.", "mm."));
        }

        /// <summary>A VR slot's note says to click the ring around A or B,
        /// so it shows under the picture and not under the list.</summary>
        [Fact]
        public void TheVrNote_ShowsUnderThePicture()
        {
            var vm = new PadViewModel(0) { OutputType = PadForge.Engine.VirtualControllerType.Vr };
            Assert.True(vm.IsButtonsPictureView);
            Assert.True(vm.ShowVrFoot);
            var raised = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.SelectedConfigTab = 2;
            Assert.True(vm.IsButtonsListView);
            Assert.False(vm.ShowVrFoot);
            Assert.Contains(nameof(PadViewModel.ShowVrFoot), raised);
            raised.Clear();
            vm.SelectedConfigTab = 0;
            Assert.True(vm.ShowVrFoot);
            Assert.Contains(nameof(PadViewModel.ShowVrFoot), raised);
            vm.OutputType = PadForge.Engine.VirtualControllerType.Xbox;
            Assert.False(vm.ShowVrFoot);
            Assert.Contains("click the ring", PadForge.Resources.Strings.Strings.Instance.Pad_Vr_MapAllSkipsTouch, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A caption's column is as wide as its word, the old
        /// width at the least, and the gyro's readings name their axes in
        /// the language beside numbers of one width.</summary>
        [Fact]
        public void CaptionsAndReadings_HoldALongerWord()
        {
            string page = PageText();
            Assert.Contains("<ColumnDefinition Width=\"Auto\" MinWidth=\"38\"/>", page);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, "FontSize=\"11\" MinWidth=\"36\"").Count);
            Assert.DoesNotContain("FontSize=\"11\" Width=\"36\"", page);

            var xml = Page();
            Assert.DoesNotContain("StringFormat={}Pitch ", page);
            // The readings stand in the Gyro card's live view (the sections
            // pass of #574): each axis's name in the language, its bar, and
            // its number at the right of a column the three share, at least
            // 78 wide, which holds -2000.0 degrees a second, so a changing
            // digit moves nothing whatever the name's length.
            foreach (string axis in new[] { "Pitch", "Yaw", "Roll" })
            {
                var number = xml.Descendants().Single(e => e.Name.LocalName == "TextBlock"
                    && At(e, "Text") == "{Binding GyroLiveRate" + axis + ", Mode=OneWay, StringFormat={}{0:+0.0;-0.0;0.0}\u00B0/s}");
                Assert.Equal("{StaticResource LiveNumber}", At(number, "Style"));
                Assert.Equal("78", At(number, "MinWidth"));
                Assert.Equal("2", At(number, "Grid.Column"));
                string row = At(number, "Grid.Row");
                var name = number.Parent.Elements().Single(e => e.Name.LocalName == "TextBlock" && At(e, "Grid.Row") == row && At(e, "Grid.Column") == null);
                Assert.Equal("{Binding Dashboard_HeadTrackingAxis" + axis + S, At(name, "Text"));
                Assert.Equal("GyroRatesLive", (string)number.Parent.Attributes().Single(a => a.Name.LocalName == "Name"));
            }
            System.Xml.Linq.XElement Keyed(string key) => xml.Descendants().Single(e => e.Name.LocalName == "Style"
                && (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key") == key);
            Assert.Contains(Keyed("LiveNumber").Elements(), e => At(e, "Property") == "TextAlignment" && At(e, "Value") == "Right");
            Assert.DoesNotContain(xml.Descendants(), e => e.Name.LocalName == "Style"
                && ((string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key") ?? "").StartsWith("LiveReading", StringComparison.Ordinal));
            var s = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal(new[] { "Pitch", "Yaw", "Roll" }, new[] { s.Dashboard_HeadTrackingAxisPitch, s.Dashboard_HeadTrackingAxisYaw, s.Dashboard_HeadTrackingAxisRoll });
        }

        /// <summary>What stands under a macro step's control starts where
        /// the control does: the label's 190 and its 12.</summary>
        [Fact]
        public void WhatStandsUnderAControl_StartsWhereTheControlDoes()
        {
            string page = PageText();
            Assert.Contains("<StackPanel Orientation=\"Horizontal\" Margin=\"202,0,0,6\"", page);
            Assert.Contains("<ItemsControl ItemsSource=\"{Binding DreamcastFrameImages}\" Margin=\"202,0,0,6\">", page);
            Assert.Contains("<ItemsControl ItemsSource=\"{Binding ScreenFrameImages}\" Margin=\"202,0,0,6\">", page);
            Assert.DoesNotContain("Margin=\"128,0,0,6\"", page);
            Assert.DoesNotContain("Margin=\"88,0,0,6\"", page);
        }

        /// <summary>A menu's More hint names the rows its fold shows. The
        /// converter keeps a name whose rule holds, takes a name that two
        /// rules give once, and always keeps a last name with no rule. In
        /// the markup each name follows the rule its own row is shown by.</summary>
        [Fact]
        public void AMenusMoreHint_NamesTheRowsItsFoldShows()
        {
            var names = new PadForge.Converters.ShownNamesConverter();
            string Hint(params object[] values) => (string)names.Convert(values, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal("Layer, Click Input, Menu Overlay", Hint(true, "Layer", false, "Layer", false, "Hold", true, "Click Input", "Menu Overlay"));
            Assert.Equal("Layer, Menu Overlay", Hint(false, "Layer", true, "Layer", "Menu Overlay"));
            Assert.Equal("Layer", Hint(true, "Layer", true, "Layer"));
            Assert.Equal("Menu Overlay", Hint(false, "A", System.Windows.DependencyProperty.UnsetValue, "B", null, "C", "Menu Overlay"));
            Assert.Equal(string.Empty, Hint(false, "A"));
            Assert.Equal(string.Empty, Hint());
            Assert.Equal(string.Empty, (string)names.Convert(null, typeof(string), null, null));

            var more = Page().Descendants().Single(e => At(e, "AutomationProperties.AutomationId") == "Pad_More_Menu");
            var hint = more.Elements().Single(e => e.Name.LocalName == "MoreRow.Hint").Elements().Single();
            Assert.Equal("{StaticResource ShownNamesConverter}", At(hint, "Converter"));
            string[] paths = Paths(hint);
            Assert.Equal(17, paths.Length);
            Assert.Equal("Dashboard_MenuOverlay", paths[^1]);
            // The overlay's rows that always show are under its head.
            Assert.Contains(more.Descendants(), e => e.Name.LocalName == "TextBlock" && At(e, "Text") == "{Binding Dashboard_MenuOverlay" + S && At(e, "Visibility") == null);
            for (int i = 0; i + 1 < paths.Length; i += 2)
            {
                string rule = paths[i], name = paths[i + 1];
                var label = more.Descendants().First(e => e.Name.LocalName == "TextBlock" && At(e, "Text") == "{Binding " + name + S);
                if (name == "Menu_Layer")
                {
                    var rules = label.Ancestors().First(a => a.Elements().Any(e => e.Name.LocalName == "StackPanel.Style"))
                        .Descendants().Where(e => e.Name.LocalName == "DataTrigger").Select(t => At(t, "Binding")).ToList();
                    Assert.Equal(new[] { "{Binding DataContext.HasShiftLayers, RelativeSource={RelativeSource AncestorType=UserControl}}", "{Binding ShowsLayerRow}" }, rules);
                    Assert.Contains(rule, new[] { "DataContext.HasShiftLayers", "ShowsLayerRow" });
                }
                else
                    Assert.Equal("{Binding " + rule + ", Converter={StaticResource BoolToVisibilityConverter}}", At(label.Parent, "Visibility"));
            }
            Assert.Contains("<conv:ShownNamesConverter x:Key=\"ShownNamesConverter\" />", Text("PadForge.App", "App.xaml"));
        }

        /// <summary>A macro step's picker of translated options is as wide
        /// as its widest option and never narrower than it was. The
        /// behavior measures options written in the markup, and measures
        /// them again when their words change, as a language change does.</summary>
        [Fact]
        public void AMacroStepsPicker_IsAsWideAsItsWidestOption()
        {
            var page = Page();
            foreach (var (binding, least) in new[]
                     {
                         ("{Binding DisconnectTarget, Mode=TwoWay}", "220"), ("{Binding SetGyroEngagedMode, Mode=TwoWay}", "160"),
                         ("{Binding CursorRecenterMode, Mode=TwoWay}", "160"), ("{Binding CursorPinMode, Mode=TwoWay}", "160"),
                         ("{Binding CursorClampMode, Mode=TwoWay}", "160"), ("{Binding ScreenKindIndex, Mode=TwoWay}", "180"),
                         ("{Binding ScreenOverlayNumber, Mode=TwoWay}", "180"), ("{Binding ScreenModeIndex, Mode=TwoWay}", "180"),
                         ("{Binding StopwatchActionIndex, Mode=TwoWay}", "180"), ("{Binding PanDirectionIndex, Mode=TwoWay}", "180"),
                         ("{Binding PointerSetMode, Mode=TwoWay}", "240"),
                     })
            {
                var picker = page.Descendants().Single(e => e.Name.LocalName == "ComboBox"
                    && (At(e, "SelectedValue") == binding || At(e, "SelectedIndex") == binding));
                Assert.Null(At(picker, "Width"));
                Assert.Equal(least, At(picker, "MinWidth"));
                Assert.Equal("True", At(picker, "ComboBoxWidthBehavior.SizeToItems"));
            }

            Sta(() =>
            {
                var shortOne = new ComboBoxItem { Content = "A" };
                var longOne = new ComboBoxItem { Content = new string('W', 15) };
                var combo = new ComboBox { MinWidth = 160 };
                combo.Items.Add(shortOne);
                combo.Items.Add(longOne);
                PadForge.Common.ComboBoxWidthBehavior.SetSizeToItems(combo, true);
                var window = new System.Windows.Window { Content = combo, Width = 600, Height = 150, ShowActivated = false, Left = -10000, Top = -10000 };
                void Drain() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                try
                {
                    window.Show();
                    Drain();
                    double wide = combo.Width;
                    Assert.True(wide > 200, $"the written options were not measured: {wide}");
                    // The words change under the same items.
                    longOne.Content = "B";
                    PadForge.Common.ComboBoxWidthBehavior.RemeasureAll();
                    Drain();
                    Assert.Equal(160, combo.Width);
                    longOne.Content = new string('W', 20);
                    PadForge.Common.ComboBoxWidthBehavior.RemeasureAll();
                    Drain();
                    Assert.True(combo.Width > wide, $"{combo.Width} after {wide}");
                    // A closed picker is no longer measured.
                    double last = combo.Width;
                    window.Content = null;
                    Drain();
                    longOne.Content = "C";
                    PadForge.Common.ComboBoxWidthBehavior.RemeasureAll();
                    Drain();
                    Assert.Equal(last, combo.Width);
                }
                finally { window.Close(); }
            });
            Assert.Contains("Strings.CultureChanged += RemeasureAll;", Text("PadForge.App", "Common", "ComboBoxWidthBehavior.cs"));
        }

        /// <summary>A control that moved keeps the rule it had: the In-Box
        /// Gestures card's Reset All is off while its rows are, and the
        /// Sound Output title's paragraph on Sony's audio shows for a Sony
        /// pad (pinned with the section's other titles in PadSectionsTests).</summary>
        [Fact]
        public void AMovedControl_KeepsItsRule()
        {
            var page = Page();
            var card = page.Descendants().Single(e => (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name") == "InBoxGesturesFoldCard");
            var resetAll = card.Elements().Single(e => e.Name.LocalName == "FoldCard.HeaderAction").Elements().Single();
            Assert.Equal("{Binding ResetTouchpadInBoxCardCommand}", At(resetAll, "Command"));
            Assert.Equal("{Binding TouchpadInBoxSectionEnabled}", At(resetAll, "IsEnabled"));
            Assert.Equal("{Binding TouchpadInBoxSectionEnabled}", At(card.Elements().Single(e => e.Name.LocalName == "StackPanel"), "IsEnabled"));
            Assert.NotNull(typeof(PadViewModel).GetProperty("SelectedDeviceHasDspChain"));
        }

        /// <summary>A device of a controller's roster has a battery while
        /// it reports a level, and says so once as the level comes and once
        /// as it goes. The roster draws a meter from the level, so no glyph
        /// is kept for it.</summary>
        [Fact]
        public void ARosterDevice_HasABatteryWhileItReportsALevel()
        {
            var device = new PadViewModel.MappedDeviceInfo();
            Assert.False(device.HasBattery);
            Assert.False(device.HasLinkOrBattery);
            var raised = new System.Collections.Generic.List<string>();
            device.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            device.SetBattery(66, false, 15);
            Assert.True(device.HasBattery);
            Assert.True(device.HasLinkOrBattery);
            Assert.Equal(1, raised.Count(n => n == nameof(PadViewModel.MappedDeviceInfo.HasBattery)));
            Assert.Equal(1, raised.Count(n => n == nameof(PadViewModel.MappedDeviceInfo.HasLinkOrBattery)));

            // Another level, charging or not: still a battery, and nothing more is said of that.
            device.SetBattery(67, true, 15);
            device.SetBattery(0, false, 15);
            Assert.True(device.HasBattery);
            Assert.Equal(1, raised.Count(n => n == nameof(PadViewModel.MappedDeviceInfo.HasBattery)));

            device.SetBattery(-5, true, 15);
            Assert.False(device.HasBattery);
            Assert.Equal(-1, device.BatteryPercent);
            Assert.Equal(2, raised.Count(n => n == nameof(PadViewModel.MappedDeviceInfo.HasBattery)));
            Assert.Equal(2, raised.Count(n => n == nameof(PadViewModel.MappedDeviceInfo.HasLinkOrBattery)));
            Assert.Null(typeof(PadViewModel.MappedDeviceInfo).GetProperty("BatteryGlyph"));

            // A Bluetooth mark alone still stands after the name.
            device.TransportGlyph = "\uE702";
            Assert.True(device.HasLinkOrBattery);
        }

        /// <summary>The chip of the profile cards and the dialogs is the
        /// controller page's quiet tag under another name: one style,
        /// written once.</summary>
        [Fact]
        public void TheChip_IsWrittenOnce()
        {
            var app = System.Xml.Linq.XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "App.xaml"));
            var chip = app.Descendants().Single(e => e.Name.LocalName == "Style"
                && (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key") == "PillChip");
            Assert.Equal("{StaticResource ChipSteel}", (string)chip.Attribute("BasedOn"));
            Assert.Empty(chip.Elements());
            // The tag's own look is pinned where it is written (PadSectionsTests).
            var icons = System.Xml.Linq.XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml"));
            Assert.Single(icons.Descendants(), e => (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key") == "ChipSteel");
        }

        /// <summary>On Extras: Tab reaches a Screen Overlay card's Remove
        /// before the Reset All at its right, and the hint to point a game
        /// at a port shows for a game that is read from one.</summary>
        [Fact]
        public void ExtrasRows_FollowTheEyeAndTheGame()
        {
            var page = Xml("PadForge.App", "Views", "ExtrasPage.xaml");
            var remove = page.Descendants().Single(e => At(e, "AutomationProperties.AutomationId") == "{Binding RemoveId}");
            var resetAll = page.Descendants().Single(e => At(e, "AutomationProperties.AutomationId") == "{Binding ResetAllId}");
            Assert.Same(remove.Parent, resetAll.Parent);
            Assert.Equal("Local", At(remove.Parent, "KeyboardNavigation.TabNavigation"));
            Assert.Equal("0", At(remove, "TabIndex"));
            Assert.Equal("1", At(resetAll, "TabIndex"));
            // Reset All is written first: the head docks from its end.
            Assert.Contains(remove, resetAll.ElementsAfterSelf());

            var hint = page.Descendants().Single(e => At(e, "Text") == "{Binding Extras_StatusHint_GameTelemetry" + S);
            Assert.Equal("{Binding GameTelemetryReadsUdp, Converter={StaticResource BoolToVisibilityConverter}}", At(hint, "Visibility"));
            Assert.NotNull(typeof(DashboardViewModel).GetProperty("GameTelemetryReadsUdp"));
        }

        /// <summary>A dialog's one-word label is as wide as its word, 60 at
        /// the least, and the two labels of the handheld dialog share the
        /// longer word's width.</summary>
        [Fact]
        public void ANarrowDialogLabel_IsAsWideAsItsWord()
        {
            var dialogs = Xml("PadForge.App", "Resources", "Dialogs.xaml");
            var narrow = dialogs.Descendants().Single(e => e.Name.LocalName == "Style"
                && (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key") == "DialogLabelNarrow");
            Assert.Equal(new[] { "Width=Auto", "MinWidth=60", "TextWrapping=NoWrap" }, narrow.Elements().Select(e => At(e, "Property") + "=" + At(e, "Value")));
            var dialog = Xml("PadForge.App", "Views", "LearnHandheldButtonDialog.xaml");
            foreach (string label in new[] { "SourceLabel", "NameLabel" })
                Assert.Equal("{StaticResource DialogLabelNarrow}", At(dialog.Descendants().Single(e => (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name") == label), "Style"));
            Assert.Contains("Loaded += (_, _) => ShareLabelColumn(SourceLabel, NameLabel);", Text("PadForge.App", "Views", "LearnHandheldButtonDialog.xaml.cs"));

            Sta(() =>
            {
                TextBlock Label(string word) => new TextBlock { Text = word, MinWidth = 60, Margin = new System.Windows.Thickness(0, 0, 10, 0) };
                var both = new[] { Label("Name"), Label("Pavadinimas, a long word") };
                PadForge.Views.LearnHandheldButtonDialog.ShareLabelColumn(both);
                Assert.True(both[0].MinWidth > 100, $"{both[0].MinWidth}");
                Assert.Equal(both[1].MinWidth, both[0].MinWidth);
                both[1].Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.Equal(both[1].DesiredSize.Width - 10, both[0].MinWidth, 3);
                // Two short words stay at the least width.
                var small = new[] { Label("Name"), Label("Word") };
                PadForge.Views.LearnHandheldButtonDialog.ShareLabelColumn(small);
                Assert.Equal(new[] { 60.0, 60.0 }, small.Select(l => l.MinWidth));
            });
        }

        /// <summary>The Sound Macros fold follows the macros while it
        /// shows: a macro coming or going, a step added, and a step that
        /// becomes Play Sound or stops being one each raise the list and its
        /// empty line. A profile switch replaces the macros while Lights and
        /// Sound can be showing. A macro taken away raises nothing more.</summary>
        [Fact]
        public void TheSoundMacrosFold_FollowsTheMacros()
        {
            var vm = new PadViewModel(0);
            var raised = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            int Raises() => raised.Count(n => n == nameof(PadViewModel.SoundMacros));

            int before = Raises();
            var sound = new MacroItem { Name = "a" };
            sound.Actions.Add(new MacroAction { Type = MacroActionType.PlaySound });
            vm.Macros.Add(sound);
            Assert.True(Raises() > before);
            Assert.Single(vm.SoundMacros);
            Assert.False(vm.HasNoSoundMacros);
            Assert.Contains(nameof(PadViewModel.HasNoSoundMacros), raised);

            var other = new MacroItem { Name = "b" };
            vm.Macros.Add(other);
            before = Raises();
            other.Actions.Add(new MacroAction { Type = MacroActionType.PlaySound });
            Assert.True(Raises() > before);
            Assert.Equal(2, vm.SoundMacros.Count);

            before = Raises();
            other.Actions[0].Type = MacroActionType.ButtonPress;
            Assert.True(Raises() > before);
            Assert.Single(vm.SoundMacros);

            before = Raises();
            vm.Macros.Remove(sound);
            Assert.True(Raises() > before);
            Assert.True(vm.HasNoSoundMacros);
            before = Raises();
            sound.Actions.Add(new MacroAction { Type = MacroActionType.PlaySound });
            sound.Actions[0].Type = MacroActionType.ButtonPress;
            Assert.Equal(before, Raises());

            before = Raises();
            vm.Macros.Clear();
            Assert.True(Raises() > before);
        }

        /// <summary>A language change has the Devices page write a Falcon's
        /// reach line again, in the new language. The line is written once
        /// for the row picked, so the service forgets the row and the next
        /// tick treats it as picked anew. The other stored lines of a row's
        /// panel are written on every tick.</summary>
        [Fact]
        public void ALanguageChange_HasTheFalconReachLineWrittenAgain()
        {
            var vm = new MainViewModel();
            var svc = new PadForge.Services.InputService(vm) { SettingsService = new PadForge.Services.SettingsService(vm) };
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var shown = typeof(PadForge.Services.InputService).GetField("_falconRowShown", flags);
            shown.SetValue(svc, Guid.NewGuid());
            typeof(PadForge.Services.InputService).GetMethod("OnCultureChanged", flags).Invoke(svc, null);
            // The handler runs on the dispatcher's next turn.
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Assert.Equal(Guid.Empty, (Guid)shown.GetValue(svc));
        }
    }

    /// <summary>
    /// The audit's web page fix (package 8), run in Jint as the phone home
    /// page's own tests run its script.
    /// </summary>
    public class AuditDelta20261113WebTests
    {
        private static string Web(params string[] parts)
            => ExtrasPageTests.RepoText(new[] { "PadForge.App", "WebAssets" }.Concat(parts).ToArray());

        private static Jint.Engine Engine()
            => new Jint.Engine(o => { o.TimeoutInterval(TimeSpan.FromSeconds(5)); o.MaxStatements(100_000); });

        /// <summary>A request for a layout that gives none says so: the
        /// server refused, the answer was no JSON, or it never came. A
        /// caller that gives no second callback is as it was.</summary>
        [Fact]
        public void ALayoutRequestThatGivesNoLayout_SaysSo()
        {
            var engine = Engine();
            engine.Execute(@"var window = {}; var document = {}; var last = null;
                function XMLHttpRequest() { last = this; }
                XMLHttpRequest.prototype.open = function (method, url) { this.url = url; };
                XMLHttpRequest.prototype.send = function () { };");
            engine.Execute(Web("js", "layout_view.js"), "layout_view.js");
            engine.Execute(@"var got = [], failed = 0;
                function ask() { window.PadForgeLayoutView.fetchLayout('ds4', 'white', function (l) { got.push(l); }, function () { failed++; }); }");
            double Number(string expression) => engine.Evaluate(expression).AsNumber();

            engine.Execute("ask(); last.status = 503; last.onload();");
            Assert.Equal(1, Number("failed"));
            engine.Execute("ask(); last.status = 200; last.responseText = 'not json'; last.onload();");
            Assert.Equal(2, Number("failed"));
            engine.Execute("ask(); last.onerror();");
            Assert.Equal(3, Number("failed"));
            engine.Execute("ask(); last.ontimeout();");
            Assert.Equal(4, Number("failed"));
            engine.Execute("ask(); last.onabort();");
            Assert.Equal(5, Number("failed"));
            Assert.Equal(0, Number("got.length"));

            engine.Execute("ask(); last.status = 200; last.responseText = '{\"baseWidth\":320}'; last.onload();");
            Assert.Equal(5, Number("failed"));
            Assert.Equal(320, Number("got[0].baseWidth"));
            Assert.Equal("/api/layout?type=ds4&finish=white", engine.Evaluate("last.url").AsString());

            // The picture page asks with no second callback.
            engine.Execute(@"window.PadForgeLayoutView.fetchLayout('ds4', '', function (l) { got.push(l); });
                last.status = 500; last.onload(); last.onerror();");
            Assert.Equal(1, Number("got.length"));
            Assert.Single(Regex.Matches(Web("js", "display_client.js"), @"PadForgeLayoutView\.fetchLayout\(layoutKey, finish, function \(layout\) \{"));
        }

        /// <summary>A tile whose layout did not come asks again at its next
        /// draw. It was left marked as asked, and stayed empty for as long
        /// as the page was open.</summary>
        [Fact]
        public void ATileWhoseLayoutDidNotCome_AsksAgain()
        {
            const string start = "    document.addEventListener(\"DOMContentLoaded\", start);";
            string source = Web("js", "home_client.js");
            Assert.Single(Regex.Matches(source, Regex.Escape(start)));
            var engine = Engine();
            engine.Execute(@"var window = {}; var document = {}; var asked = [];
                var PadForgeLayoutView = {
                    fetchLayout: function (key, finish, done, failed) { asked.push({ key: key, done: done, failed: failed }); },
                    draw: function () { return {}; }, fit: function () { } };
                var attrs = { 'data-layout': 'xboxseries' };
                var box = {
                    getAttribute: function (n) { return Object.prototype.hasOwnProperty.call(attrs, n) ? attrs[n] : null; },
                    setAttribute: function (n, v) { attrs[n] = v; },
                    removeAttribute: function (n) { delete attrs[n]; },
                    appendChild: function () { }, clientWidth: 0, clientHeight: 0, innerHTML: '' };
                var tile = { querySelector: function () { return box; } };");
            engine.Execute(source.Replace(start, "    window.homeDraw = draw;"), "home_client.js");
            double Number(string expression) => engine.Evaluate(expression).AsNumber();

            engine.Execute("window.homeDraw(tile); window.homeDraw(tile);");
            Assert.Equal(1, Number("asked.length"));
            Assert.Equal("xboxseries|", engine.Evaluate("attrs['data-want']").AsString());
            Assert.True(engine.Evaluate("typeof asked[0].failed === 'function'").AsBoolean());

            // No layout came: the mark goes, and the next draw asks again.
            engine.Execute("asked[0].failed();");
            Assert.True(engine.Evaluate("attrs['data-want'] === undefined").AsBoolean());
            engine.Execute("window.homeDraw(tile);");
            Assert.Equal(2, Number("asked.length"));

            // A failure that comes after a newer ask leaves the newer mark.
            engine.Execute("attrs['data-want'] = 'xboxseries|black'; asked[1].failed();");
            Assert.Equal("xboxseries|black", engine.Evaluate("attrs['data-want']").AsString());
        }
    }

    /// <summary>
    /// The strings, as the audit of 2026-11-13 found them: words in markup
    /// that no language could translate, two rows with one name, a tile
    /// that gave one reason for three, English that went past what the
    /// program read, and strings nothing reads any more.
    /// </summary>
    public class AuditDelta20261113StringTests
    {
        private static string Text(params string[] parts) => ExtrasPageTests.RepoText(parts).Replace("\r", "");
        private static System.Xml.Linq.XElement Xml(params string[] parts) => System.Xml.Linq.XDocument.Parse(ExtrasPageTests.RepoText(parts)).Root;
        private const string S = ", Source={x:Static strings:Strings.Instance}}";

        private static System.Collections.Generic.Dictionary<string, string> Table(string file)
            => Xml("PadForge.App", "Resources", "Strings", file).Elements("data")
                .ToDictionary(d => (string)d.Attribute("name"), d => (string)d.Element("value"));

        private static string English(string key) => Table("Strings.resx").TryGetValue(key, out var v) ? v : null;

        /// <summary>The five logic chips of both formula editors show a
        /// string, as their tooltips do: and, or, not, if? and else: put in a
        /// symbol, so the face is a word of the language in use. Every other
        /// chip with a letter on it puts in what it shows, a function or an
        /// input, and keeps that name.</summary>
        [Fact]
        public void TheLogicChipsFaces_AreStrings()
        {
            var page = Xml("PadForge.App", "Views", "PadPage.xaml");
            var faces = new[] { (" && ", "And", "and"), (" || ", "Or", "or"), ("!", "Not", "not"), (" ? ", "If", "if?"), (" : ", "Else", "else:") };
            foreach (var style in new[] { "{StaticResource FormulaChipStyle}", "{StaticResource MacroFormulaChipStyle}" })
            {
                var chips = page.Descendants().Where(e => e.Name.LocalName == "Button" && (string)e.Attribute("Style") == style).ToList();
                Assert.True(chips.Count > 30, style);
                foreach (var (tag, name, english) in faces)
                {
                    var chip = Assert.Single(chips, c => (string)c.Attribute("Tag") == tag);
                    Assert.Equal("{Binding Pad_Formula_Chip_" + name + "_Face" + S, (string)chip.Attribute("Content"));
                    Assert.Equal(english, English("Pad_Formula_Chip_" + name + "_Face"));
                }
                foreach (var chip in chips.Where(c => !faces.Any(f => f.Item1 == (string)c.Attribute("Tag"))))
                {
                    string face = (string)chip.Attribute("Content"), tag = (string)chip.Attribute("Tag");
                    if (!face.Any(char.IsLetter)) continue;
                    Assert.True(string.Equals(face.Replace('\u2212', '-'), tag.TrimEnd('('), StringComparison.OrdinalIgnoreCase), face + " puts in " + tag);
                }
            }
        }

        /// <summary>The Workshop's Legacy tag is a string, set in capitals
        /// as it was, on a config's row and over its empty panel.</summary>
        [Fact]
        public void TheLegacyTag_IsAString()
        {
            string panel = Text("PadForge.App", "Views", "CommunityConfigsPanel.xaml");
            Assert.DoesNotContain("Text=\"LEGACY\"", panel);
            Assert.Equal(2, Regex.Matches(panel, Regex.Escape(
                "Text=\"{Binding Workshop_LegacyTag, Source={x:Static strings:Strings.Instance}, Converter={StaticResource UpperConverter}}\"")).Count);
            Assert.Equal("Legacy", English("Workshop_LegacyTag"));
        }

        /// <summary>A Keyboard + Mouse pointer stick's card names its curve
        /// preset Sensitivity, as both boards do, and its speed multiplier
        /// Speed, with a reset that says Reset Speed. No two rows of the
        /// card's first column share a name.</summary>
        [Fact]
        public void APointerSticksSpeed_HasANameOfItsOwn()
        {
            var page = Xml("PadForge.App", "Views", "PadPage.xaml");
            var reset = Assert.Single(page.Descendants(), e => e.Name.LocalName == "Button"
                && (string)e.Attribute("Command") == "{Binding ResetStickSensitivityCommand}");
            var row = reset.Parent!;
            Assert.Equal("SettingRow", row.Name.LocalName);
            Assert.Equal("{Binding IsPointerStick, Converter={StaticResource BoolToVisibilityConverter}}", (string)row.Attribute("Visibility"));
            Assert.Equal("{Binding Pad_Stick_Speed" + S, (string)row.Elements().First().Attribute("Text"));
            Assert.Null(reset.Attribute("ToolTip"));
            var composed = Assert.Single(reset.Elements(), e => e.Name.LocalName == "Button.ToolTip").Elements().Single();
            Assert.Equal("MultiBinding", composed.Name.LocalName);
            Assert.Equal("{StaticResource FormatTextConverter}", (string)composed.Attribute("Converter"));
            Assert.Equal(new[] { "Pad_Stick_Speed", "Pad_ResetSection_Format" }, composed.Elements().Select(b => (string)b.Attribute("Path")));
            Assert.Equal("Speed", English("Pad_Stick_Speed"));

            var labels = row.Parent!.Elements().Where(e => e.Name.LocalName == "SettingRow")
                .Select(r => (string)r.Elements().First().Attribute("Text")).ToList();
            Assert.Contains("{Binding Macro_Sensitivity" + S, labels);
            Assert.Equal(labels.Count, labels.Distinct().Count());
        }

        /// <summary>The MIDI tile of Add a Controller says why MIDI is not
        /// running in the Settings card's words: the runtime's uninstaller, a
        /// driver, or the service. With no reason, the tile keeps its own
        /// line. The window hands the card's reason to the dialog.</summary>
        [Fact]
        public void TheMidiTile_GivesTheSettingsCardsReason()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var build = typeof(AddControllerViewModel).GetMethod("Build", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.Equal(6, build!.GetParameters().Length);
            ControllerTypeChoice Midi(string reason)
            {
                var vm = (AddControllerViewModel)build.Invoke(null, new object[]
                {
                    null, (Func<PadForge.Engine.VirtualControllerType, int>)(_ => 0), 0, false, true, reason,
                });
                return vm.More.Single(c => c.Type == PadForge.Engine.VirtualControllerType.Midi);
            }

            var settings = new SettingsViewModel { MidiApiNotStarted = true };
            settings.MidiNotRunningReason = PadForge.Common.Input.MidiNotRunningReason.Uninstalling;
            Assert.Equal(s.Settings_MidiUninstallRunning, settings.MidiServicesDetailText);
            var tile = Midi(settings.MidiServicesDetailText);
            Assert.False(tile.IsAvailable);
            Assert.Equal(s.Settings_MidiUninstallRunning, tile.Line);
            Assert.Equal(s.Main_MIDI_NotRunning, tile.ToolTip);
            settings.MidiNotRunningReason = PadForge.Common.Input.MidiNotRunningReason.DriverDidNotRespond;
            Assert.Equal(s.Settings_MidiNotRunningDriver, Midi(settings.MidiServicesDetailText).Line);
            settings.MidiNotRunningReason = PadForge.Common.Input.MidiNotRunningReason.ServiceDidNotRespond;
            Assert.Equal(s.Settings_MidiNotRunning, Midi(settings.MidiServicesDetailText).Line);
            Assert.Equal(s.AddController_MidiNotRunning, Midi(null).Line);
            Assert.Equal(s.AddController_MidiNotRunning, Midi("").Line);
            // Another tile never takes MIDI's reason.
            Assert.Equal(ControllerTypeChoice.LineOf(PadForge.Engine.VirtualControllerType.Xbox),
                ((AddControllerViewModel)build.Invoke(null, new object[] { null, (Func<PadForge.Engine.VirtualControllerType, int>)(_ => 0), 0, false, true, "x" }))
                .Common.Single(c => c.Type == PadForge.Engine.VirtualControllerType.Xbox).Line);

            Assert.Contains("_viewModel.Settings.IsMidiAvailable ? null : _viewModel.Settings.MidiServicesDetailText",
                Text("PadForge.App", "MainWindow.xaml.cs"));
        }

        /// <summary>The English says what the program does. The button that
        /// opens Controller Presets names it, and the button's tooltip, the
        /// two file dialogs and the two problems say preset as the dialog
        /// does. The window picker's empty line says what it looked for, the
        /// Workshop's legacy line says what was read, the player number hint
        /// gives one rule for who wins, the icon credits name only icons
        /// PadForge draws, and a percent is written as the other English
        /// percents are.</summary>
        [Fact]
        public void TheEnglish_SaysWhatTheProgramDoes()
        {
            Assert.Equal(English("ManageProfiles_Title") + "\u2026", English("Pad_ExtendedImportFromDevice"));
            foreach (var key in new[] { "Pad_ExtendedImportFromDeviceTooltip", "ManageProfiles_ExportTitle", "ManageProfiles_ImportFromFileTitle",
                         "ManageProfiles_ProblemRefused_Format", "ManageProfiles_ProblemNoDescriptor" })
            {
                Assert.Contains("preset", English(key), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("profile", English(key), StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal("No other open window was found.", English("ProfileDialog_NoRunningGames"));
            string legacy = English("Workshop_LegacyBody");
            Assert.DoesNotContain("2016", legacy);
            Assert.DoesNotContain("CDN", legacy);
            Assert.StartsWith("Steam lists no file to download for this config", legacy);
            string idle = English("Pad_Lighting_PlayerIdle_Hint");
            Assert.DoesNotContain("always wins", idle);
            Assert.Contains("A game takes over whenever it writes lighting.", idle);
            Assert.DoesNotContain("Ionicons", English("About_IconSourcesDesc"));
            Assert.Equal("{0}%", English("Pad_Value_Percent_Format"));
            Assert.DoesNotContain(Table("Strings.resx").Values, v => (v ?? "").Contains("} %"));
        }

        /// <summary>Bouncy Castle's package names its license MIT (its
        /// nuspec's license expression and its LICENSE.md), and LICENSE and
        /// README say so.</summary>
        [Fact]
        public void BouncyCastle_IsCreditedUnderTheLicenseItsPackageNames()
        {
            string license = Text("LICENSE"), readme = Text("README.md");
            int block = license.IndexOf("https://github.com/bcgit/bc-csharp", StringComparison.Ordinal);
            Assert.True(block > 0);
            Assert.Contains("  Licensed under the MIT License\n", license.Substring(block, license.IndexOf("\n\n", block, StringComparison.Ordinal) - block + 1));
            Assert.DoesNotContain("Bouncy Castle Licence", license);
            Assert.DoesNotContain("Bouncy Castle Licence", readme);
            Assert.Contains("{Binding About_License_Mit" + S, Text("PadForge.App", "Views", "AboutPage.xaml")
                .Split("About_BouncyCastle,")[0].Split("<Border Style=\"{StaticResource CreditRow}\">").Last());
        }

        /// <summary>The strings nothing reads left the Designer and every
        /// table: the device count's old suffix, the NFC reader line, the
        /// old tab and card names, the motor bars, the rumble hold label the
        /// trigger's took the place of, the old state word, Remote Link's
        /// second Rename and the license chip Bouncy Castle no longer
        /// wears.</summary>
        [Fact]
        public void TheStringsNothingReads_LeftEveryTable()
        {
            string designer = Text("PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
            var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "PadForge.sln")))
                root = root.Parent;
            Assert.NotNull(root);
            var tables = System.IO.Directory.GetFiles(System.IO.Path.Combine(root!.FullName, "PadForge.App", "Resources", "Strings"), "Strings*.resx");
            Assert.Equal(PadForge.Common.LanguageCatalog.All.Count, tables.Length);
            foreach (string gone in new[]
            {
                "Main_DevicesSuffix", "Nfc_NoReader", "Status_DeviceAssignedSlot_Format", "Pad_ForceFeedback", "Pad_ImpulseTriggers",
                "Pad_AdaptiveTriggers", "Pad_Touchpad_TouchpadIndex_Label", "Mapping_Sensitivity", "Mapping_Sensitivity_Tooltip",
                "Pad_Pointer", "Pad_Mouse", "Pad_Tab_BassShakers", "Common_ResetToOff", "Common_ResizePanes", "VrCtl_Trigger",
                "Pad_LeftMotor", "Pad_RightMotor", "Macro_RumbleHoldMs_Label", "Status_NoMapping", "RemoteLink_Rename",
                "About_License_BouncyCastle",
            })
            {
                Assert.DoesNotContain("\"" + gone + "\"", designer);
                foreach (var f in tables)
                    Assert.DoesNotContain("name=\"" + gone + "\"", System.IO.File.ReadAllText(f));
            }
        }
    }
}
