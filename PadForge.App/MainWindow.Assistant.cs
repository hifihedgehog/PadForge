using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.ViewModels;

namespace PadForge
{
    /// <summary>
    /// The main window's part in the assistants' tools (#597): what an
    /// assistant's tool acts on, through the same calls the pages make. Every
    /// member runs on the interface's thread unless its summary says
    /// otherwise.
    /// </summary>
    public partial class MainWindow
    {
        private AssistantServer _assistants;

        /// <summary>Joins the assistants' server, the settings catalog and the
        /// undo journal to the host. Called once the host is made.</summary>
        private void StartAssistants()
        {
            var app = new WindowAssistantApp(this);
            _pointer = new AssistantPointer(() => this);
            var parts = new AssistantCatalogParts
            {
                CatalogPageOf = page => page switch
                {
                    PadForge.Search.SearchPage.Home => HomePageView,
                    PadForge.Search.SearchPage.About => AboutPageView,
                    PadForge.Search.SearchPage.Window => this,
                    _ => null,
                },
                NavItem = NavItemOf,
                NavTag = () => _viewModel.SelectedNavTag,
                PressTray = key =>
                {
                    if (key == "Exit") ExitFromTray();
                },
                MainWindow = () => this,
                Pointer = _pointer,
            };
            var catalog = new AssistantCatalog(_searchService, _searchNavigator, SearchPageOf, _viewModel, BindPadPage, app.WindowPlace, parts);
            var undo = new AssistantUndo(app.TakeUndoState, app.RestoreUndoState);
            var ui = new DispatcherAutomationUi(Dispatcher);
            var calls = new AssistantCalls(_automation, app, catalog, ui, undo, AssistantTools.All);
            _assistants = new AssistantServer(_automation, calls, ui);
            _assistants.Attach();
            // A new run of changes, or one undone, changes which Activity
            // entries offer Undo.
            undo.Changed += (s, e) => _viewModel.Dashboard.RefreshAutomationUndoFromHost();
            // The programs the user allowed are kept in PadForge.xml (#600).
            AutomationConsent.Shared.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(() => _settingsService?.MarkDirty()));
            // The Automation page lists them with Remove, and its Add on an
            // assistant's row is the consent for it.
            ConsentForPage = new AutomationConsentForPage(AutomationConsent.Shared, _automation.Sessions, _automation.Clients);
            _viewModel.Dashboard.AttachConsent(ConsentForPage);
            // The question card words its two answers by the question's kind.
            Views.AutomationRequestCard.KindOf = id => _automation.Approvals.KindOf(id);
            // Home's cards are built in code, so they reach the catalog
            // through the one seam for live rows.
            PadForge.Services.Catalog.LiveCatalog.AddProvider(() => AssistantHomeCards.Items(HomePageView, _viewModel.Dashboard, _viewModel.Pads));
        }

        private AssistantPointer _pointer;

        /// <summary>The consent store in the Automation page's terms, which
        /// the page's AttachConsent takes.</summary>
        internal AutomationConsentForPage ConsentForPage { get; private set; }

        /// <summary>The sidebar's entry for a page's tag, which the guided
        /// pointer rings on the way to a place (#600).</summary>
        private FrameworkElement NavItemOf(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            foreach (var items in new System.Collections.IEnumerable[] { NavView.MenuItems, NavView.FooterMenuItems })
                foreach (var item in items)
                    if (item is Wpf.Ui.Controls.NavigationViewItem nvi && nvi.Tag?.ToString() == tag && nvi.IsVisible) return nvi;
            return null;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>What the assistants' tools act on: this window, its
        /// services and its view models.</summary>
        private sealed class WindowAssistantApp : IAssistantApp
        {
            private readonly MainWindow _w;

            public WindowAssistantApp(MainWindow window) => _w = window;

            private MainViewModel Vm => _w._viewModel;

            // ─────────────────────────────────────────────
            //  Read
            // ─────────────────────────────────────────────

            public AssistantSystemResult ReadSystem()
            {
                var s = Vm.Settings;
                var result = new AssistantSystemResult
                {
                    PollingRateHz = s.PollingRateHz,
                    RunningAtHz = Math.Round(_w._inputService?.EngineFrequency ?? 0, 1),
                    HidHideInstalled = s.IsHidHideInstalled,
                };
                result.Drivers.Add(new AssistantDriverState { Name = "HIDMaestro", Installed = s.IsHIDMaestroInstalled, Version = Blank(s.HIDMaestroVersion), For = "The virtual controllers games see." });
                result.Drivers.Add(new AssistantDriverState { Name = "HidHide", Installed = s.IsHidHideInstalled, Version = Blank(s.HidHideVersion), For = "Hiding a physical device from games, so a game sees only the virtual controller." });
                result.Drivers.Add(new AssistantDriverState { Name = "Windows MIDI Services", Installed = s.IsMidiRuntimeInstalled, Version = Blank(s.MidiRuntimeVersion), For = "MIDI virtual controllers and MIDI devices." });
                result.Drivers.Add(new AssistantDriverState { Name = "SteamVR", Installed = s.IsSteamVrInstalled, For = "VR virtual controllers." });
                result.Drivers.Add(new AssistantDriverState { Name = "SDL", Installed = true, Version = Blank(s.SdlVersion), For = "Reading game controllers." });
                var devices = SettingsManager.UserDevices;
                if (devices != null) lock (devices.SyncRoot) result.DevicesHidden = devices.Items.Count(d => d != null && d.HidHideEnabled);
                return result;
            }

            private static string Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

            private static bool IsActive(ProfileListItem item)
                => item.IsDefault ? SettingsManager.ActiveProfileId == null : SettingsManager.ActiveProfileId == item.Id;

            /// <summary>The game files of a profile, by name: a path to a game
            /// says where the user keeps it.</summary>
            private static List<OutsideText> Games(string paths)
                => string.IsNullOrEmpty(paths)
                    ? new List<OutsideText>()
                    : paths.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => new OutsideText(Path.GetFileName(p))).ToList();

            /// <summary>A profile as it is stored, or the live setup for the
            /// one in force.</summary>
            private ProfileData DataOf(ProfileListItem item)
            {
                if (IsActive(item)) return _w._inputService.SnapshotCurrentProfile();
                if (item.IsDefault) return SettingsManager.PendingDefaultSnapshot;
                return SettingsManager.Profiles.Find(p => p.Id == item.Id);
            }

            public List<AssistantProfile> ListProfiles()
            {
                var list = new List<AssistantProfile>();
                foreach (var item in Vm.Settings.ProfileItems)
                {
                    int controllers = IsActive(item)
                        ? SettingsManager.SlotCreated.Count(c => c)
                        : (item.IsDefault ? SettingsManager.PendingDefaultSnapshot : SettingsManager.Profiles.Find(p => p.Id == item.Id))?.SlotCreated?.Count(c => c) ?? 0;
                    list.Add(new AssistantProfile
                    {
                        Id = item.Id,
                        Name = OutsideText.Of(item.Name),
                        Active = IsActive(item),
                        Default = item.IsDefault,
                        Games = Games(item.ExecutablePaths),
                        Controllers = controllers,
                    });
                }
                return list;
            }

            /// <summary>A profile's list entry by ID or name, refused when
            /// none has it.</summary>
            private ProfileListItem ItemOf(string profile)
            {
                string want = profile?.Trim() ?? string.Empty;
                var items = Vm.Settings.ProfileItems;
                var item = items.FirstOrDefault(p => p.Id == want)
                    ?? items.FirstOrDefault(p => string.Equals(p.Name, want, StringComparison.CurrentCultureIgnoreCase))
                    ?? (string.Equals(want, "default", StringComparison.OrdinalIgnoreCase) ? items.FirstOrDefault(p => p.IsDefault) : null);
                return item ?? throw new AssistantRefusal("unknown-profile", "No profile has the name or ID " + want + ". list_profiles gives them.");
            }

            public AssistantProfileDetail ReadProfile(string profile)
            {
                var item = ItemOf(profile);
                var data = DataOf(item);
                var detail = new AssistantProfileDetail
                {
                    Id = item.Id,
                    Name = OutsideText.Of(item.Name),
                    Active = IsActive(item),
                    Default = item.IsDefault,
                    Games = Games(item.ExecutablePaths),
                    PollingRateHz = data?.PollingRateOverrideHz ?? 0,
                    Macros = data?.Macros?.Length ?? 0,
                };
                if (data?.SlotCreated != null)
                {
                    int number = 0;
                    for (int i = 0; i < data.SlotCreated.Length; i++)
                    {
                        if (!data.SlotCreated[i]) continue;
                        var type = data.SlotControllerTypes != null && i < data.SlotControllerTypes.Length
                            ? (VirtualControllerType)data.SlotControllerTypes[i] : VirtualControllerType.Xbox;
                        var controller = new AssistantProfileController
                        {
                            Number = ++number,
                            Type = AssistantNames.Of(type),
                            Preset = data.SlotProfileIds != null && i < data.SlotProfileIds.Length ? data.SlotProfileIds[i] : null,
                            On = data.SlotEnabled == null || i >= data.SlotEnabled.Length || data.SlotEnabled[i],
                        };
                        foreach (var entry in data.Entries ?? Array.Empty<ProfileEntry>())
                        {
                            if (entry.MapTo != i) continue;
                            var ud = SettingsManager.FindDeviceByInstanceGuid(entry.InstanceGuid);
                            controller.Devices.Add(new OutsideText(ud != null ? AssistantTools.DeviceName(ud) : AutomationDevices.IdOf(entry.InstanceGuid)));
                        }
                        detail.Controllers.Add(controller);
                    }
                }
                if (data != null)
                {
                    var s = Strings.Instance;
                    if (data.EnableWebController) detail.ExtrasOn.Add(s.Dashboard_WebController);
                    if (data.EnableDsuMotionServer) detail.ExtrasOn.Add(s.Dashboard_MotionServer);
                    if (data.EnableGameModServer) detail.ExtrasOn.Add(s.Dashboard_GameMods);
                    if (data.EnableGameTelemetry) detail.ExtrasOn.Add(s.Dashboard_GameTelemetry);
                    if (data.EnableTouchpadOverlay) detail.ExtrasOn.Add(s.Dashboard_TouchpadOverlay);
                }
                return detail;
            }

            /// <summary>A controller's pad index by its number, refused when no
            /// controller has it.</summary>
            private int SlotOf(int number)
            {
                int slot = SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber(number);
                if (slot < 0 || slot >= InputManager.MaxPads || !SettingsManager.SlotCreated[slot])
                    throw new AssistantRefusal("unknown-slot", "No virtual controller has the number " + number + ". list_controllers gives them.");
                return slot;
            }

            private PadViewModel PadOf(int number) => Vm.Pads[SlotOf(number)];

            private static int NumberOf(int slot)
            {
                int n = SettingsManager.SlotOrders.GetGlobalSlotNumber(slot);
                return n > 0 ? n : slot + 1;
            }

            public string ControllerType(int number)
            {
                int slot = SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber(number);
                if (slot < 0 || slot >= InputManager.MaxPads || !SettingsManager.SlotCreated[slot]) return null;
                return AssistantNames.Of(Vm.Pads[slot].OutputType);
            }

            public List<AssistantController> ListControllers()
            {
                // Each device's controllers, read with the device list's
                // lock before the settings' (SettingsManager's lock order).
                var bySlot = new Dictionary<int, List<AssistantControllerDevice>>();
                var devices = SettingsManager.UserDevices;
                List<UserDevice> rows;
                if (devices == null) rows = new List<UserDevice>();
                else lock (devices.SyncRoot) rows = devices.Items.Where(d => d != null).ToList();
                foreach (var ud in rows)
                {
                    foreach (int slot in SettingsManager.GetAssignedSlots(ud.InstanceGuid))
                    {
                        if (!bySlot.TryGetValue(slot, out var list)) bySlot[slot] = list = new List<AssistantControllerDevice>();
                        if (list.Any(d => d.Id == AutomationDevices.IdOf(ud.InstanceGuid))) continue;
                        list.Add(new AssistantControllerDevice
                        {
                            Id = AutomationDevices.IdOf(ud.InstanceGuid),
                            Name = OutsideText.Of(AssistantTools.DeviceName(ud)),
                            Online = ud.IsOnline,
                        });
                    }
                }
                var controllers = new List<AssistantController>();
                for (int i = 0; i < InputManager.MaxPads && i < Vm.Pads.Count; i++)
                {
                    if (!SettingsManager.SlotCreated[i]) continue;
                    var pad = Vm.Pads[i];
                    controllers.Add(new AssistantController
                    {
                        Number = NumberOf(i),
                        Type = AssistantNames.Of(pad.OutputType),
                        Preset = pad.ProfileId,
                        Label = PadForge.Search.SearchService.SlotName(pad),
                        On = SettingsManager.SlotEnabled[i],
                        Devices = bySlot.TryGetValue(i, out var assigned) ? assigned : new List<AssistantControllerDevice>(),
                    });
                }
                controllers.Sort((a, b) => a.Number.CompareTo(b.Number));
                return controllers;
            }

            public Dictionary<string, string> Outputs(int number)
            {
                string line = _w._inputService?.AutomationOutputsNow(number);
                if (line == null) return null;
                var parsed = AutomationLine.Parse("outputs " + line);
                var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var word in parsed.Words)
                    if (word.IsPair) outputs[word.Key] = word.Value;
                return outputs;
            }

            public List<AssistantPreset> Presets(string type)
            {
                var profiles = AssistantNames.ToType(type) switch
                {
                    VirtualControllerType.Xbox => HMaestroProfileCatalog.XboxProfiles,
                    VirtualControllerType.PlayStation => HMaestroProfileCatalog.PlayStationProfiles,
                    VirtualControllerType.Nintendo => HMaestroProfileCatalog.NintendoProfiles,
                    VirtualControllerType.Extended => HMaestroProfileCatalog.ExtendedProfiles,
                    _ => (IReadOnlyList<HIDMaestro.HMProfile>)Array.Empty<HIDMaestro.HMProfile>(),
                };
                return profiles.Select(p => new AssistantPreset { Id = p.Id, Name = HMaestroProfileCatalog.DisplayName(p) }).ToList();
            }

            /// <summary>The rows of a controller as its page shows them: the
            /// stored rows mirrored into its view first, as a visit to its
            /// page does (BindPadPage).</summary>
            private IReadOnlyList<MappingItem> RowsOf(PadViewModel pad)
            {
                if (_w._settingsService != null) _w._settingsService.MirrorOnly(() => InputService.RefreshMappingsToViewModel(pad));
                else InputService.RefreshMappingsToViewModel(pad);
                return pad.RowsOfSlot.ToList();
            }

            private static string DeviceIdOf(string guid)
                => Guid.TryParse(guid, out Guid g) && g != Guid.Empty ? AutomationDevices.IdOf(g) : string.Empty;

            public List<AssistantMappingRow> ReadMappings(int number)
            {
                var pad = PadOf(number);
                return RowsOf(pad).Select(m => new AssistantMappingRow
                {
                    Target = m.TargetSettingName,
                    Label = m.TargetLabel,
                    Source = m.SourceDescriptor ?? string.Empty,
                    SourceDevice = DeviceIdOf(m.PrimarySourceDeviceGuid),
                    SourceName = OutsideText.Of(m.IsMapped ? m.SourceDisplayText : null),
                    NegativeSource = string.IsNullOrEmpty(m.NegSourceDescriptor) ? null : m.NegSourceDescriptor,
                    Inverted = m.IsInverted,
                    HalfAxis = m.IsHalfAxis,
                    DeadZone = m.MappingDeadZone,
                    MoreSources = m.ExtraSources.Select(x => new AssistantMappingSource { Input = x.Descriptor, Device = DeviceIdOf(x.DeviceGuid) }).ToList(),
                    Combine = string.IsNullOrEmpty(m.CombineMode) ? null : m.CombineMode,
                }).ToList();
            }

            public List<AssistantMacro> ReadMacros(int number)
            {
                var pad = PadOf(number);
                var list = new List<AssistantMacro>();
                for (int i = 0; i < pad.Macros.Count; i++)
                {
                    var macro = pad.Macros[i];
                    list.Add(new AssistantMacro
                    {
                        Index = i,
                        Name = OutsideText.Of(macro.Name),
                        Trigger = macro.TriggerDisplayText,
                        Steps = macro.Actions.Count,
                        StepKinds = macro.Actions.Select(a => a.Type.ToString()).ToList(),
                        Data = DataOf(SettingsService.BuildMacroDataForMacro(macro, pad.PadIndex)),
                    });
                }
                return list;
            }

            public List<AssistantMenu> ReadMenus(int number)
                => PadOf(number).Menus.Select(m => new AssistantMenu { Name = OutsideText.Of(m.Entry?.Name), Items = m.Entry?.Items?.Count ?? 0 }).ToList();

            public long Revision => _w._settingsService?.AssignmentRevision ?? 0;

            /// <summary>The user's click on Show of the question card brings
            /// the window forward (#600), as the tray's Show does.</summary>
            public void BringForward() => _w.RestoreFromTray();

            public AssistantWindowPlace WindowPlace()
            {
                if (!_w.IsVisible) return AssistantWindowPlace.InTray;
                if (_w.WindowState == WindowState.Minimized) return AssistantWindowPlace.Minimized;
                var hwnd = new System.Windows.Interop.WindowInteropHelper(_w).Handle;
                return GetForegroundWindow() == hwnd ? AssistantWindowPlace.InFront : AssistantWindowPlace.Behind;
            }

            // ─────────────────────────────────────────────
            //  Profiles
            // ─────────────────────────────────────────────

            /// <summary>Lists a profile the registry holds, as Save As and
            /// New do.</summary>
            private ProfileListItem List(ProfileData profile)
            {
                var item = new ProfileListItem
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    Executables = InputService.FormatExePaths(profile.ExecutableNames ?? string.Empty),
                    ExecutablePaths = profile.ExecutableNames ?? string.Empty,
                };
                SettingsService.UpdateTopologyCounts(item, profile.SlotCreated, profile.SlotControllerTypes, profile.SlotProfileIds, profile.SlotModel3DAppearances);
                Vm.Settings.ProfileItems.Add(item);
                _w._settingsService.MarkDirty();
                return item;
            }

            public string CreateProfile(string name, string copyOf)
            {
                name = SettingsManager.UniqueProfileName(name);
                ProfileData made;
                if (string.IsNullOrWhiteSpace(copyOf))
                {
                    made = _w._inputService.CreateEmptyProfile(name, string.Empty);
                }
                else
                {
                    var source = ItemOf(copyOf);
                    if (IsActive(source))
                    {
                        // Save As: the live setup under a new name, with no
                        // game of its own yet.
                        made = _w._inputService.CreateSnapshotProfile(name, string.Empty);
                    }
                    else
                    {
                        var stored = DataOf(source) ?? throw new AssistantRefusal("unknown-profile", "That profile holds nothing to copy.");
                        made = PadForge.Common.ProfileTransfer.Clone(stored);
                        made.Id = Guid.NewGuid().ToString("N");
                        made.Name = name;
                        made.ExecutableNames = string.Empty;
                        made.WorkshopSource = null;
                        SettingsManager.Profiles.Add(made);
                    }
                }
                List(made);
                return made.Id;
            }

            public void RenameProfile(string profile, string name)
            {
                var item = ItemOf(profile);
                if (item.IsDefault) throw new AssistantRefusal("bad-argument", "The default profile keeps its name.");
                var stored = SettingsManager.Profiles.Find(p => p.Id == item.Id)
                    ?? throw new AssistantRefusal("unknown-profile", "That profile is gone. list_profiles gives them.");
                _w._inputService.EditProfile(item.Id, name, stored.ExecutableNames ?? string.Empty, stored.PollingRateOverrideHz);
                item.Name = name;
                if (SettingsManager.ActiveProfileId == item.Id) Vm.Settings.ActiveProfileInfo = name;
                _w._settingsService.MarkDirty();
            }

            public void DeleteProfile(string profile)
            {
                var item = ItemOf(profile);
                if (item.IsDefault) throw new AssistantRefusal("bad-argument", "The default profile cannot be deleted.");
                _w.DeleteListedProfile(item);
            }

            /// <summary>What an import reads: a .pfprofile with its packages
            /// left in the file, or another program's file translated.</summary>
            private sealed class ImportSource
            {
                public string Path;
                public ProfileData Profile;
                public PadForge.Common.ConfigImport.ForeignImportResult Foreign;
            }

            public AssistantImport ReadProfileFile(string path)
            {
                var source = new ImportSource { Path = path };
                string report = null;
                if (path.EndsWith(PadForge.Common.ProfileTransfer.FileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    source.Profile = PadForge.Common.ProfileTransfer.Peek(path)
                        ?? throw new AssistantRefusal("bad-argument", "That file is no PadForge profile PadForge can read.");
                }
                else
                {
                    var foreign = PadForge.Common.ConfigImport.ForeignConfigImporter.Translate(path);
                    if (foreign.Refused) throw new AssistantRefusal("bad-argument", Strings.Get(foreign.RefusedKey));
                    source.Foreign = foreign;
                    source.Profile = foreign.Profile;
                    report = "Imported: " + foreign.Report.CleanCount + " rows came over whole, " + foreign.Report.PartialCount
                        + " in part and " + foreign.Report.SkippedCount + " not at all.";
                }
                return new AssistantImport
                {
                    Profile = source,
                    Name = source.Profile?.Name,
                    Asks = AssistantScan.Profile(source.Profile),
                    Report = report,
                };
            }

            public (string Id, string Name) AddProfile(AssistantImport import)
            {
                var source = (ImportSource)import.Profile;
                ProfileData profile;
                if (source.Foreign != null) profile = source.Foreign.Profile;
                else
                {
                    // The .pfprofile import, which lands its sound packages.
                    profile = PadForge.Common.ProfileTransfer.Import(source.Path, out _)
                        ?? throw new AssistantRefusal("bad-argument", "That file is no PadForge profile PadForge can read.");
                }
                string name = _w.AddWorkshopProfile(profile, applyAfter: false);
                return (profile.Id, name);
            }

            public (byte[] Bytes, string FileName) ExportProfile(string profileName)
            {
                var item = ItemOf(profileName);
                // As Export does: the live edits folded into the snapshot
                // that owns them first.
                _w._inputService.SaveActiveProfileState();
                ProfileData profile = item.IsDefault
                    ? SettingsManager.PendingDefaultSnapshot ?? _w._inputService.SnapshotCurrentProfile()
                    : SettingsManager.Profiles.Find(p => p.Id == item.Id);
                if (profile == null) throw new AssistantRefusal("unknown-profile", "That profile holds nothing to export.");
                string exportName = item.IsDefault ? item.Name : profile.Name;
                string temp = Path.Combine(Path.GetTempPath(), "PadForge-export-" + Guid.NewGuid().ToString("N") + PadForge.Common.ProfileTransfer.FileExtension);
                string priorName = profile.Name, priorPicture = profile.DreamcastPicture, priorScreen = profile.KeyboardScreenPicture;
                try
                {
                    profile.Name = exportName;
                    if (item.IsDefault)
                    {
                        profile.DreamcastPicture = Vm.Settings.DefaultProfileDreamcastPicture;
                        profile.KeyboardScreenPicture = Vm.Settings.DefaultProfileKeyboardScreenPicture;
                    }
                    PadForge.Common.ProfileTransfer.Export(profile, temp);
                    return (File.ReadAllBytes(temp), exportName + PadForge.Common.ProfileTransfer.FileExtension);
                }
                finally
                {
                    profile.Name = priorName;
                    profile.DreamcastPicture = priorPicture;
                    profile.KeyboardScreenPicture = priorScreen;
                    try { File.Delete(temp); } catch { }
                }
            }

            // ─────────────────────────────────────────────
            //  Virtual controllers
            // ─────────────────────────────────────────────

            private void CheckPreset(VirtualControllerType type, string preset)
            {
                if (string.IsNullOrWhiteSpace(preset)) return;
                if (!Presets(AssistantNames.Of(type)).Any(p => string.Equals(p.Id, preset, StringComparison.OrdinalIgnoreCase)))
                    throw new AssistantRefusal("bad-argument", "That type has no preset " + preset + ". list_controllers with presets true gives them.");
            }

            private void CheckTypeAvailable(VirtualControllerType type)
            {
                if (type == VirtualControllerType.Midi && !Vm.Settings.IsMidiAvailable)
                    throw new AssistantRefusal("not-available", "MIDI controllers need Windows MIDI Services, which this PC does not have. status says so at Read.");
                if (type == VirtualControllerType.Vr && !HMaestroVRController.IsAvailable())
                    throw new AssistantRefusal("not-available", "VR controllers need SteamVR, which this PC does not have.");
            }

            public int AddController(string type, string preset)
            {
                var t = AssistantNames.ToType(type);
                CheckTypeAvailable(t);
                CheckPreset(t, preset);
                int slot = _w._deviceService.CreateSlot(t);
                if (slot < 0) throw new AssistantRefusal("too-many", "PadForge makes up to 16 virtual controllers, and one VR controller. Remove one first.");
                if (!string.IsNullOrWhiteSpace(preset))
                    Vm.Pads[slot].ProfileId = Presets(type).First(p => string.Equals(p.Id, preset, StringComparison.OrdinalIgnoreCase)).Id;
                return NumberOf(slot);
            }

            public void RemoveController(int number) => _w.DeleteSlotNow(SlotOf(number));

            public void SetControllerType(int number, string type, string preset)
            {
                int slot = SlotOf(number);
                var t = AssistantNames.ToType(type);
                CheckTypeAvailable(t);
                CheckPreset(t, preset);
                var pad = Vm.Pads[slot];
                if (pad.OutputType != t) _w.ChangeSlotType(slot, t);
                if (pad.OutputType != t)
                    throw new AssistantRefusal("too-many", "PadForge cannot make another controller of that type. One VR controller at most.");
                if (!string.IsNullOrWhiteSpace(preset))
                    pad.ProfileId = Presets(type).First(p => string.Equals(p.Id, preset, StringComparison.OrdinalIgnoreCase)).Id;
            }

            // ─────────────────────────────────────────────
            //  Devices
            // ─────────────────────────────────────────────

            private Guid DeviceGuidOf(string device)
            {
                var (ud, entry) = _w._automation.Sessions.FindDevice(device?.Trim());
                Guid guid = ud?.InstanceGuid ?? entry?.Guid ?? Guid.Empty;
                if (guid == Guid.Empty) throw new AssistantRefusal("unknown-device", "No device has the ID " + device + ". list_devices gives them.");
                return guid;
            }

            public void AssignDevice(string device, int number, bool assigned, long? revision)
            {
                // RemoteAssignmentService's checks, in its order.
                Guid guid = DeviceGuidOf(device);
                if (InputService.VmMappingsStale)
                    throw new AssistantRefusal("busy", "PadForge is loading mappings. Call again in a moment.");
                if (revision != null && revision.Value != _w._settingsService.AssignmentRevision)
                    throw new AssistantRefusal("stale", "Which device is on which controller changed since you read it. Call list_devices again and use its revision.");
                int slot = SlotOf(number);
                if (!_w._deviceService.SetDeviceSlotAssignment(guid, slot, assigned))
                    throw new AssistantRefusal("not-available", "PadForge could not make that assignment now. The device may be one a controller of that kind cannot take.");
            }

            // ─────────────────────────────────────────────
            //  Mappings
            // ─────────────────────────────────────────────

            public void SetMappings(int number, IReadOnlyList<AssistantMappingEdit> edits)
            {
                var pad = PadOf(number);
                var rows = RowsOf(pad);
                // Every row is read before any is written, so a mistake in
                // the last leaves the first as it was.
                var plan = new List<(MappingItem Row, AssistantMappingEdit Edit, string Device)>();
                foreach (var edit in edits)
                {
                    var row = rows.FirstOrDefault(m => string.Equals(m.TargetSettingName, edit.Target, StringComparison.OrdinalIgnoreCase))
                        ?? throw new AssistantRefusal("bad-argument", "Controller " + number + " has no row with the target " + edit.Target + ". get_mappings gives the targets.");
                    string device = string.Empty;
                    if (!string.IsNullOrWhiteSpace(edit.Device)) device = DeviceGuidOf(edit.Device).ToString().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(edit.Source)
                        && PadForge.Engine.Common.Mapping.SourceCoercion.ClassifyDescriptor(edit.Source.Trim()) == PadForge.Engine.Common.Mapping.SourceCoercion.SourceType.Unmapped)
                        throw new AssistantRefusal("bad-argument", "PadForge does not read " + edit.Source + " as an input. get_device gives each control's input, as in Button 3, Axis 1 or POV 0 Up.");
                    if (edit.NegativeSource != null && !row.HasNegDirection)
                        throw new AssistantRefusal("bad-argument", "The row " + edit.Target + " has no other direction for negative_source.");
                    if (edit.DeadZone is int dz && (dz < 0 || dz > 100))
                        throw new AssistantRefusal("out-of-range", "A dead zone is 0 to 100 percent.");
                    plan.Add((row, edit, device));
                }
                foreach (var (row, edit, device) in plan)
                {
                    if (edit.Source != null)
                    {
                        string source = edit.Source.Trim();
                        if (source.Length == 0)
                        {
                            pad.ClearMappingRows(new[] { row });
                        }
                        else
                        {
                            // The picker's own steps: the device first, then the
                            // input with its invert and half prefix, then the
                            // scroll row's authoring default.
                            row.PrimarySourceDeviceGuid = device;
                            row.PrimarySourceDeviceLabel = DeviceLabel(device);
                            string prefix = (edit.Inverted == true ? "I" : string.Empty) + (edit.HalfAxis == true ? "H" : string.Empty);
                            row.LoadDescriptor(prefix + source);
                            row.ApplyScrollUpAuthoringDefault(source);
                        }
                    }
                    else
                    {
                        if (edit.Inverted is bool inv) row.IsInverted = inv;
                        if (edit.HalfAxis is bool half) row.IsHalfAxis = half;
                    }
                    if (edit.NegativeSource != null) row.LoadNegDescriptor(edit.NegativeSource.Trim());
                    if (edit.DeadZone is int dead) row.MappingDeadZone = dead;
                }
                _w._settingsService.MarkDirty();
            }

            private static string DeviceLabel(string guid)
            {
                if (string.IsNullOrEmpty(guid)) return Strings.Instance.Mapping_AnyDevice;
                var ud = Guid.TryParse(guid, out Guid g) ? SettingsManager.FindDeviceByInstanceGuid(g) : null;
                return (ud == null ? null : InputService.LocalizedDeviceName(ud)) ?? guid;
            }

            public void ClearMappings(int number, IReadOnlyList<string> targets)
            {
                var pad = PadOf(number);
                var rows = RowsOf(pad);
                if (targets == null || targets.Count == 0)
                {
                    pad.ClearMappingRows(pad.Mappings);
                }
                else
                {
                    var picked = new List<MappingItem>();
                    foreach (string target in targets)
                        picked.Add(rows.FirstOrDefault(m => string.Equals(m.TargetSettingName, target, StringComparison.OrdinalIgnoreCase))
                            ?? throw new AssistantRefusal("bad-argument", "Controller " + number + " has no row with the target " + target + ". get_mappings gives the targets."));
                    pad.ClearMappingRows(picked);
                }
                _w._settingsService.MarkDirty();
            }

            public void AutoMap(int number)
            {
                int slot = SlotOf(number);
                var pad = Vm.Pads[slot];
                // The type change's automap for the type the controller has,
                // in its load-bearing order (ChangeSlotType).
                SettingsManager.ReAutoMapSlot(slot, pad.OutputType, pad.ProfileId, pad.ExtendedConfig);
                SettingsService.RefreshMappingSetsFromLegacy();
                pad.MappingsViewLoaded = false;
                _w._settingsService.MarkDirty();
                _w._inputService.RefreshDeviceList();
                InputService.RefreshMappingsToViewModel(pad);
            }

            // ─────────────────────────────────────────────
            //  Macros
            // ─────────────────────────────────────────────

            /// <summary>The prefix a device's short ID takes in a macro's data,
            /// in the place of the instance GUID PadForge keeps there.</summary>
            private const string DevicePrefix = "device:";

            /// <summary>A macro's data as a program reads it: every device the
            /// data names by its instance GUID named by its short ID instead,
            /// as every other result names devices.</summary>
            private static JsonElement DataOf(MacroData data)
            {
                var node = JsonSerializer.SerializeToNode(data);
                node = AssistantJson.ScrubStrings(node, (property, text) =>
                    Guid.TryParse(text, out Guid g) && g != Guid.Empty && SettingsManager.FindDeviceByInstanceGuid(g) != null
                        ? DevicePrefix + AutomationDevices.IdOf(g) : text);
                return JsonSerializer.SerializeToElement(node);
            }

            /// <summary>The stand-ins the scrubber writes, such as
            /// <c>&lt;folder&gt;</c> and <c>&lt;user 1&gt;</c>.</summary>
            private static readonly System.Text.RegularExpressions.Regex StandIn =
                new(@"<[a-z][a-z-]*( [0-9]+)?>", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            /// <summary>A macro's data as a program gives it back: a device's
            /// short ID turned into its instance GUID again. Refused when the
            /// data still holds a value PadForge left out for privacy, which
            /// the macro would keep as it is written.</summary>
            private MacroData ReadMacro(JsonElement json)
            {
                if (json.ValueKind != JsonValueKind.Object) return null;
                bool left = false;
                var node = AssistantJson.ScrubStrings(JsonSerializer.SerializeToNode(json), (property, text) =>
                {
                    if (StandIn.IsMatch(text)) left = true;
                    if (!text.StartsWith(DevicePrefix, StringComparison.Ordinal)) return text;
                    var (ud, entry) = _w._automation.Sessions.FindDevice(text.Substring(DevicePrefix.Length));
                    Guid guid = ud?.InstanceGuid ?? entry?.Guid ?? Guid.Empty;
                    return guid == Guid.Empty ? text : guid.ToString();
                });
                if (left)
                    throw new AssistantRefusal("bad-argument", "The macro holds a value PadForge left out for privacy, such as <folder>. Give the whole value, or leave that step as it was by not saving this macro.");
                try
                {
                    return node.Deserialize<MacroData>();
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            public List<string> ScanMacro(JsonElement json)
            {
                var data = ReadMacro(json) ?? throw new AssistantRefusal("bad-argument", "The macro is not in PadForge's JSON form. get_mappings with macros gives a macro's data to start from.");
                return AssistantScan.Macro(data);
            }

            public int SaveMacro(int number, int? index, JsonElement json)
            {
                var pad = PadOf(number);
                var data = ReadMacro(json) ?? throw new AssistantRefusal("bad-argument", "The macro is not in PadForge's JSON form. get_mappings with macros gives a macro's data to start from.");
                if (index != null && (index < 0 || index >= pad.Macros.Count))
                    throw new AssistantRefusal("unknown-macro", "Controller " + number + " has no macro " + index + ". get_mappings with macros gives them.");
                // The paste's own steps (OnPasteMacro): the load, the slot,
                // and the layer scope kept only where this slot declares it.
                var macro = SettingsService.LoadMacroFromData(data, pad.OutputType, pad.ExtendedConfig?.ButtonCount, pad.ProfileId);
                macro.PadIndex = pad.PadIndex;
                if (!DestinationDeclaresLayer(pad, macro.LayerMask)) macro.LayerMask = string.Empty;
                int at;
                if (index == null)
                {
                    pad.Macros.Add(macro);
                    at = pad.Macros.Count - 1;
                }
                else
                {
                    SoundMacroService.StopSlot(pad.PadIndex);
                    pad.Macros[index.Value] = macro;
                    at = index.Value;
                }
                pad.SelectedMacro = macro;
                _w._settingsService.MarkDirty();
                return at;
            }

            public void DeleteMacro(int number, int index)
            {
                var pad = PadOf(number);
                if (index < 0 || index >= pad.Macros.Count)
                    throw new AssistantRefusal("unknown-macro", "Controller " + number + " has no macro " + index + ". get_mappings with macros gives them.");
                // The Remove button's step, for the macro named.
                var macro = pad.Macros[index];
                pad.Macros.Remove(macro);
                if (ReferenceEquals(pad.SelectedMacro, macro) || pad.SelectedMacro == null) pad.SelectedMacro = pad.Macros.LastOrDefault();
                _w._settingsService.MarkDirty();
            }

            // ─────────────────────────────────────────────
            //  Undo
            // ─────────────────────────────────────────────

            public AssistantUndoState TakeUndoState() => AssistantUndoStore.Take(_w._inputService, _w._settingsService);

            public string RestoreUndoState(AssistantUndoState state)
                => AssistantUndoStore.Restore(state, _w._inputService, _w._settingsService, Vm, _w.RebuildControllerSection);

            // ─────────────────────────────────────────────
            //  Problem recorder
            // ─────────────────────────────────────────────

            public async Task<AssistantRecording> RecordAsync(int seconds, string note, CancellationToken stopping)
            {
                var recorder = ProblemRecorder.Instance;
                var started = recorder.Start();
                if (!started.Ok)
                {
                    throw started.Refusal switch
                    {
                        RecorderRefusal.AlreadyRecording or RecorderRefusal.Busy =>
                            new AssistantRefusal("busy", "The problem recorder is taken up: a recording runs, or the last file is being finished. Try again when it is done."),
                        RecorderRefusal.Privacy =>
                            new AssistantRefusal("not-available", "The problem recorder refused to start: its check found a private value it would let through."),
                        _ => new AssistantRefusal("not-available", "The problem recorder could not start: " + started.Detail),
                    };
                }
                if (!string.IsNullOrWhiteSpace(note)) recorder.Mark(note);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stopping).ConfigureAwait(false);
                }
                finally
                {
                    // A recording a program started ends with its call,
                    // whether the call ran to its end or not.
                    var stopped = recorder.Stop(wait: true, how: "the program that started it stopped it");
                    _lastRecording = stopped.Ok ? stopped.Path : null;
                }
                string zip = _lastRecording;
                if (zip == null || !File.Exists(zip))
                    throw new AssistantRefusal("internal", "The recording stopped before its time. It waits in PadForge for the user to look over and save.");
                string summary;
                using (var reader = PadForge.Engine.Recording.RecordingReader.Open(zip))
                    summary = string.Join("\n", PadForge.Engine.Recording.RecordingSummary.Read(reader).Lines());
                return new AssistantRecording { Summary = summary };
            }

            private string _lastRecording;

            // ─────────────────────────────────────────────
            //  Privacy
            // ─────────────────────────────────────────────

            public PrivacyScrubber MakeScrubber() => AutomationReadScrub.Fresh();
        }
    }
}
