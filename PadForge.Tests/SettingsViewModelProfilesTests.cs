using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The Profiles page's view model (#574): a card's command acts on its
    /// own profile, Get Profiles opens on the tab asked for, Reset All on the
    /// Shortcuts card puts each row's settings back, the In front now line
    /// picks its words, and Switch Profiles by Game reaches every card.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SettingsViewModelProfilesTests : IDisposable
    {
        private readonly string _savedActiveProfileId = SettingsManager.ActiveProfileId;
        private readonly List<ProfileData> _savedProfiles = SettingsManager.Profiles;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;

        public SettingsViewModelProfilesTests()
        {
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.ActiveProfileId = "p1";
        }

        public void Dispose()
        {
            SettingsManager.ActiveProfileId = _savedActiveProfileId;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.UserDevices = _savedDevices;
        }

        /// <summary>Default, the active Rocket League and an idle Desktop.</summary>
        private static (SettingsViewModel Vm, ProfileListItem Default, ProfileListItem Active, ProfileListItem Idle) Arrange()
        {
            var vm = new SettingsViewModel();
            var builtIn = new ProfileListItem { Id = ProfileListItem.DefaultProfileId, Name = "Default" };
            var active = new ProfileListItem { Id = "p1", Name = "Rocket League" };
            var idle = new ProfileListItem { Id = "p2", Name = "Desktop" };
            vm.ProfileItems.Add(builtIn);
            vm.ProfileItems.Add(active);
            vm.ProfileItems.Add(idle);
            return (vm, builtIn, active, idle);
        }

        /// <summary>A shortcut's recorded buttons are a key each on its row.
        /// What stands after the keys says None while there are none, and
        /// counts the seconds down while the row records, led by Recording
        /// until a button is held.</summary>
        [Fact]
        public void AShortcutsButtons_AreAKeyEach_AndTheNoteSaysNoneOrCountsDown()
        {
            var s = Strings.Instance;
            var data = new GlobalMacroData { SwitchMode = SwitchProfileMode.Next };
            var vm = new ProfileShortcutViewModel(data, null, null);
            Assert.Empty(vm.ComboParts);
            Assert.False(vm.HasCombo);
            Assert.Equal(s.Common_None, vm.ComboNote);
            Assert.Equal(s.Common_None, vm.ButtonComboDisplay);

            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.IsRecording = true;
            // The row's words follow the start of a recording at once, with
            // no wait for the first count.
            Assert.Contains(nameof(ProfileShortcutViewModel.ComboNote), changed);
            vm.RecordingCountdown = 4;
            Assert.Equal(s.Profiles_ShortcutLearning + " (4s)", vm.ComboNote);
            Assert.Equal(vm.ComboNote, vm.ButtonComboDisplay);

            // Two buttons held: the keys, and the count alone after them.
            changed.Clear();
            data.TriggerEntries = new[] { new TriggerButtonEntry { ButtonIndex = 10 }, new TriggerButtonEntry { ButtonIndex = 5 } };
            vm.NotifyComboChanged();
            foreach (string name in new[] { "ButtonComboDisplay", "ComboParts", "HasCombo", "ComboNote" })
                Assert.Contains(name, changed);
            Assert.True(vm.HasCombo);
            Assert.Equal("(4s)", vm.ComboNote);
            // No device is known here, so a key is its button's number.
            string eleven = string.Format(s.Macro_Btn_Format, 11), six = string.Format(s.Macro_Btn_Format, 6);
            Assert.Equal(new[] { eleven, six }, vm.ComboParts.Select(p => p.Name));
            Assert.All(vm.ComboParts, p => Assert.Equal(string.Empty, p.DeviceTail));
            Assert.Equal(eleven + " + " + six + " (4s)", vm.ButtonComboDisplay);

            // Recorded: the keys alone.
            vm.SetLearnedButtons(data.TriggerEntries);
            Assert.False(vm.IsRecording);
            Assert.Equal(string.Empty, vm.ComboNote);
            Assert.Equal(eleven + " + " + six, vm.ButtonComboDisplay);

            // Cleared: None again.
            changed.Clear();
            vm.ClearCommand.Execute(null);
            Assert.Contains(nameof(ProfileShortcutViewModel.ComboParts), changed);
            Assert.Empty(vm.ComboParts);
            Assert.Equal(s.Common_None, vm.ComboNote);
        }

        /// <summary>A key names its device only where the row's own device
        /// does not. With Any Connected Device picked every key says which
        /// device it is on, since the shortcut answers that device alone.
        /// With the device itself picked there is no second mention. The
        /// sentence a screen reader hears names it either way.</summary>
        [Fact]
        public void AShortcutsKey_NamesItsDevice_OnlyWhereTheRowsDeviceDoesNot()
        {
            var s = Strings.Instance;
            var pad = new UserDevice { InstanceGuid = Guid.NewGuid(), InstanceName = "Xbox Controller", CapType = InputDeviceType.Gamepad, IsOnline = true };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(pad);
            var data = new GlobalMacroData
            {
                SwitchMode = SwitchProfileMode.Next,
                TriggerEntries = new[]
                {
                    new TriggerButtonEntry { ButtonIndex = 10, DeviceInstanceGuid = pad.InstanceGuid },
                    new TriggerButtonEntry { ButtonIndex = 5, DeviceInstanceGuid = pad.InstanceGuid },
                },
            };
            var vm = new ProfileShortcutViewModel(data, null, null);
            Assert.Equal(new[] { s.Btn_Guide, s.Btn_RightShoulder }, vm.ComboParts.Select(p => p.Name));
            Assert.All(vm.ComboParts, p => Assert.Equal("Xbox Controller", p.Device));
            Assert.Equal("  Xbox Controller", vm.ComboParts[0].DeviceTail);

            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.TriggerDeviceGuid = pad.InstanceGuid;
            Assert.Contains(nameof(ProfileShortcutViewModel.ComboParts), changed);
            Assert.All(vm.ComboParts, p => Assert.Equal(string.Empty, p.DeviceTail));
            Assert.Equal(s.Btn_Guide + " (Xbox Controller) + " + s.Btn_RightShoulder + " (Xbox Controller)", vm.ButtonComboDisplay);

            // A part with no device, or no name, draws nothing for it.
            Assert.Equal(string.Empty, new ShortcutKeyPart("Guide", null).DeviceTail);
            Assert.Equal(string.Empty, new ShortcutKeyPart(null, string.Empty).Name);
        }

        [Fact]
        public void EachCardCommand_SetsTheProfile_AndRaisesItsEvent()
        {
            var (vm, builtIn, active, idle) = Arrange();
            Assert.True(active.IsActive);
            var raised = new List<string>();
            vm.LoadProfileRequested += (_, _) => raised.Add("Load");
            vm.RevertToDefaultRequested += (_, _) => raised.Add("Revert");
            vm.SaveAsProfileRequested += (_, _) => raised.Add("SaveAs");
            vm.SaveSetupHereRequested += (_, _) => raised.Add("SaveHere");
            vm.EditProfileRequested += (_, _) => raised.Add("Edit");
            vm.ExportProfileRequested += (_, _) => raised.Add("Export");
            vm.DeleteProfileRequested += (_, _) => raised.Add("Delete");

            void Run(System.Windows.Input.ICommand command, ProfileListItem item, string expected)
            {
                raised.Clear();
                vm.SelectedProfile = null;
                Assert.True(command.CanExecute(item), expected + " refused " + item.Name);
                command.Execute(item);
                Assert.Same(item, vm.SelectedProfile);
                Assert.Equal(new[] { expected }, raised);
            }
            Run(vm.LoadProfileCommand, idle, "Load");
            Run(vm.LoadProfileCommand, builtIn, "Revert");
            Run(vm.SaveAsProfileCommand, active, "SaveAs");
            Run(vm.SaveSetupHereCommand, idle, "SaveHere");
            Run(vm.EditProfileCommand, idle, "Edit");
            Run(vm.ExportProfileCommand, builtIn, "Export");
            Run(vm.DeleteProfileCommand, idle, "Delete");
        }

        [Fact]
        public void CanExecute_RefusesWhatACardDoesNotOffer()
        {
            var (vm, builtIn, active, idle) = Arrange();
            // Default is never edited, deleted or overwritten.
            Assert.False(vm.EditProfileCommand.CanExecute(builtIn));
            Assert.False(vm.DeleteProfileCommand.CanExecute(builtIn));
            Assert.False(vm.SaveSetupHereCommand.CanExecute(builtIn));
            // The active profile has nothing to load, and is the live setup already.
            Assert.False(vm.LoadProfileCommand.CanExecute(active));
            Assert.False(vm.SaveSetupHereCommand.CanExecute(active));
            // Save As is the active card's.
            Assert.True(vm.SaveAsProfileCommand.CanExecute(active));
            Assert.False(vm.SaveAsProfileCommand.CanExecute(idle));
            Assert.False(vm.SaveAsProfileCommand.CanExecute(builtIn));
            // No profile, no command.
            foreach (var command in new System.Windows.Input.ICommand[]
                     {
                         vm.LoadProfileCommand, vm.SaveAsProfileCommand, vm.SaveSetupHereCommand, vm.EditProfileCommand,
                         vm.ExportProfileCommand, vm.DeleteProfileCommand,
                     })
                Assert.False(command.CanExecute(null));

            // A load moves the answers with it.
            int asked = 0;
            vm.LoadProfileCommand.CanExecuteChanged += (_, _) => asked++;
            SettingsManager.ActiveProfileId = "p2";
            vm.ActiveProfileInfo = "Desktop";
            Assert.True(asked > 0);
            Assert.True(vm.LoadProfileCommand.CanExecute(active));
            Assert.False(vm.LoadProfileCommand.CanExecute(idle));
        }

        [Theory]
        [InlineData("Starters", GetProfilesTab.Starters)]
        [InlineData("Community", GetProfilesTab.Community)]
        [InlineData("File", GetProfilesTab.File)]
        [InlineData(null, GetProfilesTab.Starters)]
        [InlineData("Somewhere", GetProfilesTab.Starters)]
        [InlineData("7", GetProfilesTab.Starters)]
        public void GetProfilesCommand_OpensTheTabAskedFor(string parameter, GetProfilesTab expected)
        {
            var vm = new SettingsViewModel();
            GetProfilesTab? opened = null;
            vm.GetProfilesRequested += (_, tab) => opened = tab;
            vm.GetProfilesCommand.Execute(parameter);
            Assert.Equal(expected, opened);
        }

        [Fact]
        public void ResetShortcutsCommand_PutsEachRowsSettingsBack_AndSavesEachChangedRow()
        {
            var vm = new SettingsViewModel();
            var saved = new List<ProfileShortcutViewModel>();
            var device = Guid.NewGuid();
            var changed = new ProfileShortcutViewModel(new GlobalMacroData
            {
                SwitchMode = SwitchProfileMode.Specific,
                TargetProfileId = "p2",
                TriggerDeviceGuid = device,
            }, null, s => saved.Add(s));
            var plain = new ProfileShortcutViewModel(new GlobalMacroData { SwitchMode = SwitchProfileMode.Next }, null, s => saved.Add(s));
            vm.ProfileShortcuts.Add(changed);
            vm.ProfileShortcuts.Add(plain);

            vm.ResetShortcutsCommand.Execute(null);

            Assert.Equal(SwitchProfileMode.Next, changed.SwitchMode);
            Assert.Equal(string.Empty, changed.TargetProfileId);
            Assert.Equal(Guid.Empty, changed.TriggerDeviceGuid);
            // Three settings changed on the first row, each saved, and none on
            // the second. No row and no recorded buttons go.
            Assert.Equal(3, saved.Count(s => s == changed));
            Assert.DoesNotContain(plain, saved);
            Assert.Equal(2, vm.ProfileShortcuts.Count);
        }

        [Fact]
        public void TheReadout_PicksMatchNoGamesOrNone_AndSplitsAroundTheName()
        {
            var vm = new SettingsViewModel();
            string Line() => vm.ForegroundLead + "explorer.exe" + vm.ForegroundTail;

            // No profile has a game.
            Assert.True(vm.NoProfileHasExecutables);
            Assert.Equal(string.Format(Strings.Instance.Profiles_InFront_NoGames_Format, "explorer.exe"), Line());

            // A profile has a game, and the program in front is not one.
            vm.ProfileItems.Add(new ProfileListItem { Id = "p1", Name = "Rocket League", Executables = "RocketLeague.exe", ExecutablePaths = "RocketLeague.exe" });
            Assert.Equal(string.Format(Strings.Instance.Profiles_InFront_None_Format, "explorer.exe"), Line());

            // The program in front loads a profile.
            vm.ForegroundProfileName = "Rocket League";
            vm.IsForegroundMatched = true;
            Assert.Equal(string.Format(Strings.Instance.Profiles_InFront_Match_Format, "explorer.exe", "Rocket League"), Line());

            // A profile's name holding {0} does not move the split.
            vm.ForegroundProfileName = "Odd {0} Name";
            Assert.Equal(Strings.Instance.Profiles_InFront_Match_Format.Replace("{0}", "explorer.exe").Replace("{1}", "Odd {0} Name"), Line());
        }

        [Fact]
        public void TheSplit_PutsThePlaceMidSentence_FillsTheNameInBothParts_AndTakesAFormatWithNoPlace()
        {
            Assert.Equal(("In front now: ", ", which loads Rocket League"),
                SettingsViewModel.SplitAroundPlace("In front now: {0}, which loads {1}", "Rocket League"));
            // A language that names the profile first.
            Assert.Equal(("Rocket League lädt für ", "."), SettingsViewModel.SplitAroundPlace("{1} lädt für {0}.", "Rocket League"));
            Assert.Equal(("No place here", string.Empty), SettingsViewModel.SplitAroundPlace("No place here", "x"));
            Assert.Equal(("A ", " B {0}"), SettingsViewModel.SplitAroundPlace("A {0} B {1}", "{0}"));
            Assert.Equal((string.Empty, string.Empty), SettingsViewModel.SplitAroundPlace(null, null));
        }

        [Fact]
        public void SwitchingByGame_ReachesEveryCard_AndCardsAddedLater()
        {
            var (vm, builtIn, active, idle) = Arrange();
            Assert.All(new[] { builtIn, active, idle }, i => Assert.False(i.SwitchesByGame));
            vm.EnableAutoProfileSwitching = true;
            Assert.All(new[] { builtIn, active, idle }, i => Assert.True(i.SwitchesByGame));
            var later = new ProfileListItem { Id = "p3", Name = "Later" };
            vm.ProfileItems.Add(later);
            Assert.True(later.SwitchesByGame);
            vm.EnableAutoProfileSwitching = false;
            Assert.All(vm.ProfileItems, i => Assert.False(i.SwitchesByGame));
        }
    }

    /// <summary>
    /// Save Current Setup Here (#574): the live setup replaces what a stored
    /// profile holds, and the profile keeps what makes it that profile. The
    /// real services, wired as MainWindow wires them.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SaveSetupHereTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _savedProfiles = SettingsManager.Profiles;
        private readonly string _savedActiveProfileId = SettingsManager.ActiveProfileId;
        private readonly ProfileData _savedPendingDefault = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly MappingSet[] _savedMappingSets = SettingsManager.SlotMappingSets;
        private readonly List<int> _savedXboxOrder = SettingsManager.XboxSlotOrder;
        private readonly List<int> _savedPsOrder = SettingsManager.PlayStationSlotOrder;
        private readonly List<int> _savedExtOrder = SettingsManager.ExtendedSlotOrder;
        private readonly List<int> _savedKbmOrder = SettingsManager.KeyboardMouseSlotOrder;
        private readonly List<int> _savedMidiOrder = SettingsManager.MidiSlotOrder;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.ActiveProfileId = _savedActiveProfileId;
            SettingsManager.PendingDefaultSnapshot = _savedPendingDefault;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
            SettingsManager.PlayStationSlotOrder = _savedPsOrder;
            SettingsManager.ExtendedSlotOrder = _savedExtOrder;
            SettingsManager.KeyboardMouseSlotOrder = _savedKbmOrder;
            SettingsManager.MidiSlotOrder = _savedMidiOrder;
        }

        /// <summary>One created Xbox slot at index 0, no devices, and the real
        /// service pair wired the way MainWindow wires it.</summary>
        private static InputService Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.XboxSlotOrder = new List<int> { 0 };
            SettingsManager.PlayStationSlotOrder = new List<int>();
            SettingsManager.ExtendedSlotOrder = new List<int>();
            SettingsManager.KeyboardMouseSlotOrder = new List<int>();
            SettingsManager.MidiSlotOrder = new List<int>();
            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            return new InputService(vm) { SettingsService = ss };
        }

        private static ProfileData Stored(string id, string name)
        {
            var created = new bool[InputManager.MaxPads];
            created[3] = true;
            var types = new int[InputManager.MaxPads];
            types[3] = (int)VirtualControllerType.Midi;
            return new ProfileData
            {
                Id = id,
                Name = name,
                ExecutableNames = @"C:\Games\RocketLeague.exe",
                PollingRateOverrideMs = 4,
                DreamcastPicture = "vmu.png",
                KeyboardScreenPicture = "screen.png",
                SlotCreated = created,
                SlotControllerTypes = types,
                WorkshopSource = new SteamWorkshopSource { PublishedFileId = 42 },
            };
        }

        [Fact]
        public void TheStoredProfile_TakesTheLiveSetup_AndKeepsWhatMakesItThatProfile()
        {
            var svc = Arrange();
            SettingsManager.Profiles.Add(Stored("p0", "First"));
            SettingsManager.Profiles.Add(Stored("p1", "Rocket League"));
            SettingsManager.Profiles.Add(Stored("p2", "Last"));

            var stored = svc.SaveCurrentSetupInto("p1");

            Assert.NotNull(stored);
            Assert.Same(stored, SettingsManager.Profiles[1]);
            Assert.Equal(3, SettingsManager.Profiles.Count);
            Assert.Equal("p1", stored.Id);
            Assert.Equal("Rocket League", stored.Name);
            Assert.Equal(@"C:\Games\RocketLeague.exe", stored.ExecutableNames);
            Assert.Equal(4, stored.PollingRateOverrideMs);
            Assert.Equal("vmu.png", stored.DreamcastPicture);
            Assert.Equal("screen.png", stored.KeyboardScreenPicture);
            Assert.Null(stored.WorkshopSource);
            // The live topology: one Xbox slot at 0, where the stored one had a MIDI slot at 3.
            Assert.True(stored.SlotCreated[0]);
            Assert.False(stored.SlotCreated[3]);
            Assert.Equal((int)VirtualControllerType.Xbox, stored.SlotControllerTypes[0]);
            // The neighbors are untouched.
            Assert.Equal("First", SettingsManager.Profiles[0].Name);
            Assert.NotNull(SettingsManager.Profiles[2].WorkshopSource);
        }

        [Fact]
        public void TheActiveProfile_Default_AndAnUnknownId_ChangeNothing()
        {
            var svc = Arrange();
            var active = Stored("p1", "Rocket League");
            SettingsManager.Profiles.Add(active);
            SettingsManager.ActiveProfileId = "p1";
            Assert.Null(svc.SaveCurrentSetupInto("p1"));
            Assert.Null(svc.SaveCurrentSetupInto(ProfileListItem.DefaultProfileId));
            Assert.Null(svc.SaveCurrentSetupInto("nope"));
            Assert.Null(svc.SaveCurrentSetupInto(null));
            Assert.Same(active, Assert.Single(SettingsManager.Profiles));
            Assert.NotNull(active.WorkshopSource);
        }
    }

    /// <summary>
    /// Becomes (#574): a community config's gamepad half lands on the virtual
    /// controller picked in Get Profiles, Xbox or PlayStation, with the same
    /// bindings either way.
    /// </summary>
    public class WorkshopMaterializerGamepadTypeTests
    {
        private static PadForge.SteamWorkshop.Translation.TranslatedProfile Translated(bool gamepad = true, bool kbm = true)
        {
            var t = new PadForge.SteamWorkshop.Translation.TranslatedProfile { Name = "Config", NeedsXboxSlot = gamepad, NeedsKbmSlot = kbm };
            t.XboxMappingSet.Rows.Add(new MappingRow { Target = "ButtonA" });
            t.XboxMappingSet.Rows.Add(new MappingRow { Target = "LeftThumbAxisX" });
            t.KbmMappingSet.Rows.Add(new MappingRow { Target = "KbmKey57" });
            return t;
        }

        [Fact]
        public void Xbox_IsTheDefault_AsBefore()
        {
            var p = PadForge.Services.WorkshopProfileMaterializer.Materialize(Translated());
            Assert.Equal((int)VirtualControllerType.Xbox, p.SlotControllerTypes[0]);
            Assert.Equal(InputManager.GetDefaultProfileId(VirtualControllerType.Xbox), p.SlotProfileIds[0]);
            Assert.Equal((int)VirtualControllerType.KeyboardMouse, p.SlotControllerTypes[1]);
        }

        [Fact]
        public void PlayStation_SetsTheSlotsType_AndKeepsItsBindings()
        {
            var xbox = PadForge.Services.WorkshopProfileMaterializer.Materialize(Translated(), null, VirtualControllerType.Xbox);
            var ps = PadForge.Services.WorkshopProfileMaterializer.Materialize(Translated(), null, VirtualControllerType.PlayStation);
            Assert.Equal((int)VirtualControllerType.PlayStation, ps.SlotControllerTypes[0]);
            Assert.Equal(InputManager.GetDefaultProfileId(VirtualControllerType.PlayStation), ps.SlotProfileIds[0]);
            Assert.NotEqual(xbox.SlotProfileIds[0], ps.SlotProfileIds[0]);
            Assert.Equal(xbox.SlotMappingSets[0].Rows.Select(r => r.Target), ps.SlotMappingSets[0].Rows.Select(r => r.Target));
            Assert.Equal(xbox.SlotMappingSets[0].Authoritative, ps.SlotMappingSets[0].Authoritative);
            Assert.Equal((int)VirtualControllerType.KeyboardMouse, ps.SlotControllerTypes[1]);
        }

        [Fact]
        public void AKeysOnlyConfig_MakesNoGamepadSlot_WhateverTheType()
        {
            var p = PadForge.Services.WorkshopProfileMaterializer.Materialize(Translated(gamepad: false), null, VirtualControllerType.PlayStation);
            Assert.Equal(1, p.SlotCreated.Count(c => c));
            Assert.Equal((int)VirtualControllerType.KeyboardMouse, p.SlotControllerTypes[0]);
        }

        [Theory]
        [InlineData(VirtualControllerType.KeyboardMouse)]
        [InlineData(VirtualControllerType.Nintendo)]
        [InlineData(VirtualControllerType.Midi)]
        public void AnyOtherType_ReadsAsXbox(VirtualControllerType asked)
        {
            var p = PadForge.Services.WorkshopProfileMaterializer.Materialize(Translated(), null, asked);
            Assert.Equal((int)VirtualControllerType.Xbox, p.SlotControllerTypes[0]);
            Assert.Equal(InputManager.GetDefaultProfileId(VirtualControllerType.Xbox), p.SlotProfileIds[0]);
        }
    }
}
