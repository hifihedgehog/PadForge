using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Search;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The ask-first sweep (#600 item 2, AS-191). Every call of the app that
    /// starts a process or hands the desktop shell a file, a folder or a web
    /// address is pinned by file, and each was walked back by hand to the
    /// elements, menu items and catalog actions that reach it. Each of those
    /// asks first, with the reason the request card shows, or is the user's
    /// alone: a program at Change Settings never runs a program or opens a
    /// place without the card. A new call fails the census until it is walked
    /// and its elements get their rows here, and a new element wired to a
    /// route below fails unless it carries the class.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AskFirstSweepTests
    {
        /// <summary>A call that starts a process or opens a place: Process.Start,
        /// the desktop shell's start (ShellLaunch), the allow-listed start with
        /// rights (RightsVariables), CreateProcess and the plugin launcher's
        /// start.</summary>
        internal const string Starts =
            @"(?<![\w.])(?:System\.Diagnostics\.)?Process\.Start\(|\bShellLaunch\.(?:Start|AsTheUser|StartOwnHelper)\(|\bRightsVariables\.Start\(|\bCreateProcess(?:AsUser)?W?\(|\b_?launcher\.Start\(";

        /// <summary>Each file's calls, and what reaches them. A file with a
        /// call nobody walked is not here, and the census fails.</summary>
        internal static readonly (string File, int Calls, string Reached)[] Census =
        {
            ("PadForge.App/App.xaml.cs", 1, "App.StartMovedCopy: the copy Move PadForge to Its Own Folder made (route MoveToOwnFolder_Click, SharedFolderMove_Click)"),
            ("PadForge.App/Common/DriverInstaller.cs", 1, "StartProcess, every installer's start: HidHide, the MIDI runtime, steamcmd, the Visual C++ 2008 runtime (routes Install*Command, Uninstall*Command)"),
            ("PadForge.App/Common/HidHideArm64Installer.cs", 1, "HidHide on ARM64, from InstallHidHide and UninstallHidHide"),
            ("PadForge.App/Common/Input/InputManager.Step4b.EvaluateMacros.cs", 1, "a macro's Run Program step (routes ProgramPath, PasteSettingsCommand, PasteMacroCommand, the save_macro tool)"),
            ("PadForge.App/Common/Recording/RecordingSave.cs", 1, "a recording's Save… as the user (route Save_Click in the review, the user's own)"),
            ("PadForge.App/Common/ShellLaunch.cs", 1, "StartOwnHelper's own start"),
            ("PadForge.App/Common/StartupHelper.cs", 2, "schtasks for Start at Login (routes StartAtLogin, its reset, the Window card's Reset All)"),
            ("PadForge.App/Common/System32Calls.cs", 1, "CreateProcess, for ProtectedStart's restart at a start with rights, which no element reaches"),
            ("PadForge.App/MainWindow.Falcon.cs", 1, "Force Dimension's SDK page (route OpenFalconDownloadCommand)"),
            ("PadForge.App/MainWindow.ProblemRecording.cs", 2, "Explorer with a saved recording selected, and the page where reports go after Save and Post (routes Save_Click and SaveAndPost_Click in the review, the user's own)"),
            ("PadForge.App/MainWindow.xaml.cs", 1, "Explorer on the settings folder (route OpenSettingsFolderCommand)"),
            ("PadForge.App/Search/SearchNavigator.cs", 1, "a docs result of Search, opened only by the person's Enter or click on it (SearchPanel.xaml.cs), never by set or press, and never by the catalog's navigator"),
            ("PadForge.App/Services/Automation/Assistant/AssistantMirror.cs", 1, "PadForge itself, started by the copy an assistant starts, which no element reaches"),
            ("PadForge.App/Services/Automation/Web/WebDoor.cs", 1, "the web helper (route IsOn of Reach PadForge From the Web, the user's own)"),
            ("PadForge.App/Services/Automation/Web/WebTunnels.cs", 2, "a tunnel program, started by the web helper for the tunnel the user picked on the Assistants on the Web card, tunnel-client among them, the user's own"),
            ("PadForge.App/Services/Ds3DriverInstaller.cs", 1, "msiexec for BthPS3's own removal (routes UninstallBthPS3Command, ActionButton_Click)"),
            ("PadForge.App/Services/DsxStandIn.cs", 1, "PadForge's own DSX stand-in while Game Mods is on (Asks, Network)"),
            ("PadForge.App/Services/GiveBackTask.cs", 2, "schtasks for a give-back task (routes DriverBind_Click and DriverRestore_Click, and a touchpad's Use Only in PadForge)"),
            ("PadForge.App/Services/Install/UserInstall.cs", 2, "an assistant's own add command, and PadForge.exe --install as the user (route PressCommand on the Automation page, the user's own)"),
            ("PadForge.App/Services/LogonStart.cs", 2, "the app in the user's session, at the Start at Login task's run, which no element reaches"),
            ("PadForge.App/Services/NativeHost/NativeHost.cs", 2, "PadForge's own library host for OpenVR, OpenXR, the Falcon, the Strike 7 screen and Aura lighting, which no program names"),
            ("PadForge.App/Services/Plugins/PluginLauncher.cs", 1, "CreateProcess under the desktop shell, for every launcher start in this list"),
            ("PadForge.App/Services/Plugins/PluginFile.cs", 1, "Explorer with a plugin's file selected (route ShowInFolderCommand on a plugin's card, the user's own)"),
            ("PadForge.App/Services/Plugins/PluginManager.cs", 1, "a plugin (routes ApprovePlugin_Click and AddPlugin_Click, the user's own)"),
            ("PadForge.App/Services/ProtectedStart.cs", 1, "the restart from the protected folder at a start with rights, which no element reaches"),
            ("PadForge.App/Services/RightsVariables.Start.cs", 1, "the allow-listed start every RightsVariables.Start in this list goes through"),
            ("PadForge.App/Services/SettingsFolderRun.cs", 1, "Explorer for --settings-folder, the caller's own run, which no element reaches"),
            ("PadForge.App/Services/StartWithRights.cs", 1, "the app with rights, at a start without them, which no element reaches"),
            ("PadForge.App/Services/UpdateController.cs", 1, "a release's page (route OpenReleaseNotesCommand)"),
            ("PadForge.App/Services/UpdateService.cs", 1, "StartProcess: the staged update's helper and the relaunch after it (routes InstallUpdateCommand, InstallUpdatesAutomatically)"),
            ("PadForge.App/Services/WebControllerServer.cs", 1, "netsh for the Web Controller's address and firewall rule (Asks, Network, on its page)"),
            ("PadForge.App/Services/WebControllerTls.cs", 1, "netsh for the Web Controller's certificate binding (Asks, Network, on its page)"),
            ("PadForge.App/ViewModels/DashboardViewModel.AutomationPage.cs", 1, "an assistant's settings file or its folder (route OpenSettingsCommand, the user's own)"),
            ("PadForge.App/ViewModels/ScriptLibraryRows.cs", 1, "a library's folder (route PressCommand on the Scripts card, the user's own)"),
            ("PadForge.App/ViewModels/WebAccessViewModel.cs", 2, "an assistant's add page, and OpenAI's key, tunnel and ChatGPT pages (routes AddToClaudeCommand, MakeKeyCommand, MakeTunnelCommand, OpenChatGptCommand, the user's own)"),
            ("PadForge.App/Views/AboutPage.xaml.cs", 1, "About's links (routes Documentation_Click, GitHub_Click, the credits' invites)"),
            ("PadForge.App/Views/CommunityConfigsPanel.xaml.cs", 1, "a Workshop item's page (route OpenInSteam_Click)"),
            ("PadForge.App/Views/DevicesPage.xaml.cs", 2, "a new device mapping issue and the community database's page (routes SubmitMapping_Click, SdlCommunity_Click)"),
            ("PadForge.App/Views/SettingsPage.xaml.cs", 1, "Explorer on the diagnostics folder (route OpenDiagFolder_Click)"),
        };

        /// <summary>A call that switches the profile in force, which can turn
        /// on an extra that listens on the network (#600): an apply, a load,
        /// a return to the default, a delete of the profile in force, and the
        /// command set's activate and deactivate, its target's deactivate
        /// handed on as a method included. A declaration is no call.</summary>
        internal const string ProfileSwitches =
            @"(?<!(?:void|bool|string|>) )\b(?:ApplyProfile|ApplyDefaultProfile|LoadProfile|RevertToDefaultProfile|ExternalActivate|ExternalDeactivate|DeleteProfile|ActivateProfile|DeactivateProfile)\(|\b_target\.DeactivateProfile\b(?!\()";

        /// <summary>Each file's profile switches and whose they are: a
        /// program's asks first when the switch would turn on an extra that
        /// listens on the network, off now, and the user's own never ask. A
        /// file with a switch nobody ruled on is not here, and the census
        /// fails.</summary>
        internal static readonly (string File, int Calls, string Ruling)[] ProfileSwitchCensus =
        {
            ("PadForge.App/MainWindow.Automation.cs", 2, "the command set's target: activate and deactivate, which a program's line reaches only through AutomationCommands.SwitchAsked"),
            ("PadForge.App/MainWindow.xaml.cs", 5, "the title bar's switcher, the Profiles page's Load and the default card's, which no program reaches. The Profiles page's Delete, which a program reaches through the confirm dialog's Delete and its card, and Get Profiles' Save and Apply, behind its import card: each card asks and names what the switch would turn on that listens on the network, off now, and the press goes only on what it named (Reach.Starts, AssistantPressPlan.Check)"),
            ("PadForge.App/Services/Automation/Assistant/AssistantTools.cs", 1, "delete_profile, which refuses to delete the profile in force while the default would turn on such an extra (MainWindow.Assistant.cs DeleteProfile)"),
            ("PadForge.App/Services/Automation/Assistant/AssistantUndoStore.cs", 1, "undo, which puts back what was in force before the program's own run of changes, nothing that was off then"),
            ("PadForge.App/Services/Automation/AutomationCommands.cs", 4, "activate and deactivate: a starting copy's own line at once, a program's after the card when it would start such an extra (SwitchAsked)"),
            ("PadForge.App/Services/InputService.cs", 9, "ExternalActivate and ExternalDeactivate (the command set), CompactSlotsForGaps (the setup in force, compacted), OnProfileSwitchRequired (the foreground watcher, a controller's profile shortcut from a macro the user set, a hotkey and the Web Controller's menus), ApplyDefaultProfile, DeleteProfile, LoadProfile and RevertToDefaultProfile (the window's)"),
        };

        internal enum RouteKind
        {
            /// <summary>A Click handler of the file's own code.</summary>
            Click,
            /// <summary>A command's path, in any file.</summary>
            Command,
            /// <summary>A setting a settable element binds.</summary>
            Binding,
            /// <summary>A CommandParameter: a reset or a card's Reset All.</summary>
            Parameter,
        }

        /// <summary>An element that reaches a call of the census, by what its
        /// markup wires, with the class and reason it carries. A command of a
        /// view model is the same wherever it is bound, so it is checked in
        /// every file, unless its name is one several view models give their
        /// own command (<see cref="Local"/>).</summary>
        internal sealed record Route(string File, RouteKind Kind, string Name, ReachClass Class, ReachReason Why, string Reaches, bool Local = false)
        {
            /// <summary>Whether the route is checked in its own file
            /// only.</summary>
            public bool OwnFileOnly => Kind != RouteKind.Command || Local;
        }

        internal static readonly Route[] Routes =
        {
            // About: links and the release's page.
            new("Views/AboutPage.xaml", RouteKind.Click, "Documentation_Click", ReachClass.Asks, ReachReason.OpensPlace, "the docs site (AboutPage.xaml.cs Open)"),
            new("Views/AboutPage.xaml", RouteKind.Click, "GitHub_Click", ReachClass.Asks, ReachReason.OpensPlace, "the repository (AboutPage.xaml.cs Open)"),
            new("Views/AboutPage.xaml", RouteKind.Command, "OpenReleaseNotesCommand", ReachClass.Asks, ReachReason.OpensPlace, "the release's page (UpdateController.OpenReleaseNotes)"),
            // Settings.
            new("Views/SettingsPage.xaml", RouteKind.Command, "OpenReleaseNotesCommand", ReachClass.Asks, ReachReason.OpensPlace, "the release's page (UpdateController.OpenReleaseNotes)"),
            new("Views/SettingsPage.xaml", RouteKind.Command, "InstallUpdateCommand", ReachClass.Asks, ReachReason.RunsProgram, "the staged update's helper (UpdateController.InstallAsync, UpdateService.StartHelper)"),
            new("Views/SettingsPage.xaml", RouteKind.Binding, "InstallUpdatesAutomatically", ReachClass.Asks, ReachReason.RunsProgram, "a found update installed without a press (UpdateController.AutoInstallOn)"),
            // AutoInstallOn takes both switches: turning the check on runs
            // an update when the install switch is on already.
            new("Views/SettingsPage.xaml", RouteKind.Binding, "CheckForUpdatesAutomatically", ReachClass.Asks, ReachReason.RunsProgram, "a found update installed without a press, with Install Updates Automatically on (UpdateController.AutoInstallOn)"),
            new("Views/SettingsPage.xaml", RouteKind.Binding, "StartAtLogin", ReachClass.Asks, ReachReason.RunsProgram, "schtasks (MainWindow.xaml.cs, StartupHelper.SetStartupEnabled)"),
            new("Views/SettingsPage.xaml", RouteKind.Parameter, "StartAtLogin", ReachClass.Asks, ReachReason.RunsProgram, "schtasks, when the reset turns it off"),
            new("Views/SettingsPage.xaml", RouteKind.Parameter, "Window", ReachClass.Asks, ReachReason.RunsProgram, "schtasks, when the Window card's Reset All turns Start at Login off"),
            // Reset to Defaults deletes every controller, purges HidHide's
            // cloaks and turns Start at Login off. The settings view model's
            // reset, a name other view models give their own commands.
            new("Views/SettingsPage.xaml", RouteKind.Command, "ResetCommand", ReachClass.Asks, ReachReason.Drivers, "HidHide's cloaks purged, and schtasks when Start at Login was on (MainWindow.xaml.cs, ResetRequested)", Local: true),
            new("Views/SettingsPage.xaml", RouteKind.Command, "OpenSettingsFolderCommand", ReachClass.Asks, ReachReason.OpensPlace, "Explorer (MainWindow.OnOpenSettingsFolder)"),
            new("Views/SettingsPage.xaml", RouteKind.Click, "OpenDiagFolder_Click", ReachClass.Asks, ReachReason.OpensPlace, "Explorer on the diagnostics folder"),
            new("Views/SettingsPage.xaml", RouteKind.Click, "MoveToOwnFolder_Click", ReachClass.Asks, ReachReason.RunsProgram, "the moved copy (ExeFolderState.RequestMove, MainWindow.OnMoveToOwnFolder, App.StartMovedCopy)"),
            new("Views/SettingsPage.xaml", RouteKind.Command, "InstallVc2008RuntimeCommand", ReachClass.Asks, ReachReason.RunsProgram, "Microsoft's redistributable (DriverInstaller.InstallVc2008Runtime)"),
            new("Views/SettingsPage.xaml", RouteKind.Command, "OpenFalconDownloadCommand", ReachClass.Asks, ReachReason.OpensPlace, "Force Dimension's SDK page (MainWindow.Falcon.cs)"),
            new("Views/SettingsPage.xaml", RouteKind.Command, "InstallHidHideCommand", ReachClass.Asks, ReachReason.Drivers, "HidHide's setup (DriverInstaller.InstallHidHide)"),
            new("Views/SettingsPage.xaml", RouteKind.Command, "UninstallHidHideCommand", ReachClass.Asks, ReachReason.Drivers, "HidHide's removal (DriverInstaller.UninstallHidHide)"),
            new("Views/SettingsPage.xaml", RouteKind.Command, "UninstallBthPS3Command", ReachClass.Asks, ReachReason.Drivers, "msiexec (Ds3DriverInstaller.UninstallBthPs3Setup)"),
            new("Views/SettingsPage.xaml", RouteKind.Command, "InstallMidiRuntimeCommand", ReachClass.Asks, ReachReason.Drivers, "the MIDI runtime's setup (DriverInstaller.InstallMidiRuntime)"),
            new("Views/SettingsPage.xaml", RouteKind.Command, "UninstallMidiServicesCommand", ReachClass.Asks, ReachReason.Drivers, "the MIDI runtime's removal (DriverInstaller.UninstallMidiRuntime)"),
            new("Views/SettingsPage.xaml", RouteKind.Command, "InstallSteamVrCommand", ReachClass.Asks, ReachReason.Drivers, "steamcmd (DriverInstaller.InstallSteamVRAsync)"),
            new("Views/UninstallBthPS3Dialog.xaml", RouteKind.Click, "ActionButton_Click", ReachClass.Asks, ReachReason.Drivers, "msiexec (Ds3DriverInstaller.UninstallBthPs3Setup)"),
            // Devices.
            new("Views/DevicesPage.xaml", RouteKind.Click, "SdlCommunity_Click", ReachClass.Asks, ReachReason.OpensPlace, "the community database's page"),
            new("Views/DevicesPage.xaml", RouteKind.Click, "SubmitMapping_Click", ReachClass.Asks, ReachReason.OpensPlace, "a new device mapping issue on GitHub"),
            new("Views/DevicesPage.xaml", RouteKind.Click, "MoveToOwnFolder_Click", ReachClass.Asks, ReachReason.RunsProgram, "the moved copy (App.StartMovedCopy)"),
            new("Views/DevicesPage.xaml", RouteKind.Click, "DriverBind_Click", ReachClass.Asks, ReachReason.UseOnly, "schtasks for the give-back task (GiveBackTask.RegisterKeyboard)"),
            new("Views/DevicesPage.xaml", RouteKind.Click, "DriverRestore_Click", ReachClass.Asks, ReachReason.UseOnly, "schtasks, the give-back task taken out (GiveBackTask.Delete)"),
            // Home.
            new("Views/HomePage.xaml", RouteKind.Click, "SharedFolderMove_Click", ReachClass.Asks, ReachReason.RunsProgram, "the moved copy (App.StartMovedCopy)"),
            // Get Profiles, the Workshop's panel.
            new("Views/CommunityConfigsPanel.xaml", RouteKind.Click, "OpenInSteam_Click", ReachClass.Asks, ReachReason.OpensPlace, "the Workshop item's page"),
            // A controller's page: a macro whose step runs a program, made in
            // the step's fields or brought in by a paste, and fired later by
            // its trigger.
            new("Views/PadPage.xaml", RouteKind.Binding, "ProgramPath", ReachClass.Asks, ReachReason.RunsProgram, "a Run Program step (ExecuteRunProgramAction)"),
            new("Views/PadPage.xaml", RouteKind.Binding, "ProgramArgs", ReachClass.Asks, ReachReason.RunsProgram, "a Run Program step (ExecuteRunProgramAction)"),
            new("Views/PadPage.xaml", RouteKind.Binding, "ProgramWorkingDir", ReachClass.Asks, ReachReason.RunsProgram, "a Run Program step (ExecuteRunProgramAction)"),
            new("Views/PadPage.xaml", RouteKind.Command, "PasteSettingsCommand", ReachClass.Asks, ReachReason.ImportsProfile, "a pasted controller's macros (__SlotMacros), a Run Program step among them (OnPasteSettings)"),
            new("Views/PadPage.xaml", RouteKind.Command, "PasteMacroCommand", ReachClass.Asks, ReachReason.SendsInput, "a pasted macro, a Run Program step among them (OnPasteMacro)"),
            // A layer tab's Paste and Clear, as the controller's Paste and
            // Clear All. Paste replaces the layer's rows with the rows a
            // layer's Copy kept, which press keys in Windows on a keyboard
            // and mouse controller, and its card names them. Clear empties
            // the layer, as Clear All empties the controller behind a
            // confirm whose buttons ask (ConfirmDialog.xaml).
            new("Views/PadPage.xaml", RouteKind.Click, "ShiftLayer_Paste_Click", ReachClass.Asks, ReachReason.None,
                "the layer's rows replaced, keys and mouse input in Windows on a keyboard and mouse controller (PadPage.PasteLayer)"),
            new("Views/PadPage.xaml", RouteKind.Click, "ShiftLayer_Clear_Click", ReachClass.Asks, ReachReason.None,
                "the layer's rows emptied, as Clear All's confirm empties the controller's"),
            // The user's own: the Automation and Plugins pages, the web
            // door and a recording's review.
            new("Views/ExtrasPage.xaml", RouteKind.Command, "PressCommand", ReachClass.UsersOwn, ReachReason.None, "an assistant row's Add, Update and Remove, and a Scripts row's Install, Update and Open Folder (UserInstall.RunAsUserAsync, ScriptLibraryRows.PressAsync)", Local: true),
            new("Views/ExtrasPage.xaml", RouteKind.Command, "OpenSettingsCommand", ReachClass.UsersOwn, ReachReason.None, "an assistant's settings file or its folder (DashboardViewModel.OpenAssistantSettings)", Local: true),
            new("Views/ExtrasPage.xaml", RouteKind.Click, "ApprovePlugin_Click", ReachClass.UsersOwn, ReachReason.None, "a plugin (PluginManager, PluginLauncher)"),
            new("Views/ExtrasPage.xaml", RouteKind.Click, "AddPlugin_Click", ReachClass.UsersOwn, ReachReason.None, "a plugin (PluginManager, PluginLauncher)"),
            new("Views/ExtrasPage.xaml", RouteKind.Command, "ShowInFolderCommand", ReachClass.UsersOwn, ReachReason.None, "Explorer with the plugin's file selected (PluginFile.ShowInFolder)", Local: true),
            new("Views/AssistantsWebSwitch.xaml", RouteKind.Binding, "IsOn", ReachClass.UsersOwn, ReachReason.None, "the web helper (WebDoor.TurnOn)"),
            new("Views/AssistantsWebCard.xaml", RouteKind.Command, "AddToClaudeCommand", ReachClass.UsersOwn, ReachReason.None, "an assistant's add page (WebAccessViewModel.OpenClaude)"),
            new("Views/AssistantsWebCard.xaml", RouteKind.Command, "MakeKeyCommand", ReachClass.UsersOwn, ReachReason.None, "OpenAI's API keys page (WebAccessViewModel.Open)"),
            new("Views/AssistantsWebCard.xaml", RouteKind.Command, "MakeTunnelCommand", ReachClass.UsersOwn, ReachReason.None, "OpenAI's tunnel management (WebAccessViewModel.Open)"),
            new("Views/AssistantsWebCard.xaml", RouteKind.Command, "OpenChatGptCommand", ReachClass.UsersOwn, ReachReason.None, "ChatGPT's plugins page (WebAccessViewModel.Open)"),
            new("Views/AssistantsWebSwitch.xaml", RouteKind.Binding, "IsNetworkOn", ReachClass.UsersOwn, ReachReason.None, "the web helper and netsh (WebDoor.TurnNetworkOn, NetworkDoor)"),
            new("Views/RecordingReviewView.xaml", RouteKind.Click, "Save_Click", ReachClass.UsersOwn, ReachReason.None, "the save as the user and Explorer (RecordingSave.AsUserAsync, MainWindow.ProblemRecording.cs)"),
            new("Views/RecordingReviewView.xaml", RouteKind.Click, "SaveAndPost_Click", ReachClass.UsersOwn, ReachReason.None, "the save as the user, Explorer and the page where reports go (RecordingSave.AsUserAsync, MainWindow.ProblemRecording.cs)"),
            // The extras that listen on the network, whose pages ask.
            new("Views/ExtrasPage.xaml", RouteKind.Binding, "EnableWebController", ReachClass.Asks, ReachReason.Network, "netsh (WebControllerServer, WebControllerTls)"),
            new("Views/ExtrasPage.xaml", RouteKind.Binding, "EnableGameModServer", ReachClass.Asks, ReachReason.Network, "PadForge's own DSX stand-in (DsxStandIn)"),
            new("Views/ExtrasPage.xaml", RouteKind.Binding, "EnableRemoteLink", ReachClass.Asks, ReachReason.Network, "Remote Link's server, discovery and DHT (LinkServer, LinkDiscovery, UdpKrpcTransport)"),
            new("Views/ExtrasPage.xaml", RouteKind.Binding, "EnableDsuMotionServer", ReachClass.Asks, ReachReason.Network, "the DSU server (DsuMotionServer)"),
            // Head Tracking's UDP input and Game Telemetry listen on every
            // address, so their switches ask as the four above do: the
            // UDP input's own switch, and the page's switch, which turns it
            // on with the rest.
            new("Views/ExtrasPage.xaml", RouteKind.Binding, "HeadTrackingEnabled", ReachClass.Asks, ReachReason.Network, "a UDP listener on every address (HeadTrackerDevice)"),
            new("Views/ExtrasPage.xaml", RouteKind.Binding, "HeadTrackingAnyEnabled", ReachClass.Asks, ReachReason.Network, "the UDP input with the inputs it brings back (DashboardViewModel.HeadTrackingAnyEnabled)"),
            new("Views/ExtrasPage.xaml", RouteKind.Binding, "EnableGameTelemetry", ReachClass.Asks, ReachReason.Network, "a UDP listener on every address for the game picked (GameTelemetryDevice)"),
            // What lets the foreground watcher put a profile in force:
            // Switch Profiles by Game turned on, and a game added to a
            // profile's list while it is on. Each asks when a profile it
            // would put in force turns on one of those extras, off now, and
            // names the profile (AssistantScan.NetworkLines,
            // ProfileDialog.SaveStarts).
            new("Views/ProfilesPage.xaml", RouteKind.Binding, "EnableAutoProfileSwitching", ReachClass.Asks, ReachReason.Network,
                "the foreground watcher's profile switches, each profile's extras with them (ForegroundMonitorService, InputService.OnAutoProfileSwitchRequired)"),
            new("Views/ProfileDialog.xaml", RouteKind.Click, "SaveButton_Click", ReachClass.Asks, ReachReason.Network,
                "the profile's games, which the foreground watcher switches by (MainWindow.OnEditProfile, InputService.EditProfile)"),
        };

        /// <summary>
        /// The switch of each tile of the Extras catalog (#600), by what
        /// turning it on starts: the setting the tile binds, the settings of
        /// the extra's page it turns on, its class and reason, and why. A
        /// switch that starts a server, a listener or a program asks, unless
        /// its page's own switch is the user's alone. The rest are free, each
        /// with its reason. The class sits on the switch alone
        /// (ExtraTile.SwitchReach), so the tile's own button, which opens the
        /// page, and the catalog's entry for it stay free. No program reaches
        /// a tile's switch today (set on an extra opens its page), and the
        /// class holds should one ever do so. A new tile fails until it has a
        /// row here.
        /// </summary>
        internal static readonly (string Key, string Setting, string[] TurnsOn, ReachClass Class, ReachReason Why, string Starts)[] Tiles =
        {
            ("WebController", "EnableWebController", new[] { "EnableWebController" }, ReachClass.Asks, ReachReason.Network,
                "the Web Controller's HTTP listener on the network, and netsh (WebControllerServer, WebControllerListener)"),
            ("HeadTracking", "HeadTrackingAnyEnabled", new[] { "HeadTrackingAnyEnabled", "HeadTrackingEnabled", "HeadTrackingFreeTrack", "HeadTrackingOpenXr" }, ReachClass.Asks, ReachReason.Network,
                "the UDP input's listener on every address with FreeTrack's reader the first time, else the inputs it turned off last, OpenXR's session among them when it was on"),
            ("GameTelemetry", "EnableGameTelemetry", new[] { "EnableGameTelemetry" }, ReachClass.Asks, ReachReason.Network,
                "a UDP listener on every address for the game picked (GameTelemetryDevice, and the Forza, Codemasters and OutGauge sources)"),
            ("TwitchChat", "EnableTwitchChat", new[] { "EnableTwitchChat" }, ReachClass.Free, ReachReason.None,
                "a WebSocket out to Twitch's chat that reads and never posts, and a device row whose buttons viewers' commands press, which do what the user's own mappings do (TwitchChatClient, TwitchChatDevice): no server, listener or program"),
            ("RemoteLink", "EnableRemoteLink", new[] { "EnableRemoteLink" }, ReachClass.Asks, ReachReason.Network,
                "Remote Link's server, discovery and DHT (LinkServer, LinkDiscovery, UdpKrpcTransport)"),
            ("MotionServer", "EnableDsuMotionServer", new[] { "EnableDsuMotionServer" }, ReachClass.Asks, ReachReason.Network,
                "the DSU server (DsuMotionServer)"),
            ("GameMods", "EnableGameModServer", new[] { "EnableGameModServer" }, ReachClass.Asks, ReachReason.Network,
                "the game mod server and PadForge's own DSX stand-in (GameModServer, DsxStandIn)"),
            ("Overlays", "OverlaysAnyEnabled", new[] { "OverlaysAnyEnabled", "EnableMenuOverlay", "EnableShiftLayerFlyout", "EnableProfileOverlay", "EnableDeviceDisconnectedNote", "EnableDeviceConnectedNote", "EnableControllersNote" }, ReachClass.Free, ReachReason.None,
                "PadForge's own windows over the screen"),
            ("TouchpadOverlay", "EnableTouchpadOverlay", new[] { "EnableTouchpadOverlay" }, ReachClass.Free, ReachReason.None,
                "PadForge's own window over the screen"),
            ("ScreenOverlay", "ScreenOverlaysAnyEnabled", new[] { "ScreenOverlaysAnyEnabled", "Enabled" }, ReachClass.Free, ReachReason.None,
                "PadForge's own windows over the screen, each overlay of the list"),
            ("Crosshair", "CrosshairsAnyEnabled", new[] { "CrosshairsAnyEnabled", "Enabled" }, ReachClass.Free, ReachReason.None,
                "PadForge's own windows over the screen, each crosshair of the list"),
            ("Magnifier", "MagnifierEnabled", new[] { "MagnifierEnabled", "Enabled" }, ReachClass.Free, ReachReason.None,
                "PadForge's own magnifier window (MagnifierNative)"),
            ("Automation", "EnableAutomation", new[] { "EnableAutomation" }, ReachClass.UsersOwn, ReachReason.None,
                "Automation's two pipes, whose switch on its page is the user's alone"),
            ("Plugins", "EnablePlugins", new[] { "EnablePlugins" }, ReachClass.UsersOwn, ReachReason.None,
                "the approved plugins' programs, whose switch on its page is the user's alone"),
        };

        private static readonly Regex PathOf = new(@"^\{Binding\s+(?:Path=)?([\w.\[\]]+)");

        private static string LastPart(string path) => path?.Split('.').Last();

        /// <summary>Whether an element is wired to a route.</summary>
        internal static bool Wired(XElement e, Route route)
        {
            switch (route.Kind)
            {
                case RouteKind.Click:
                    return ReachMarkup.Attr(e, "Click") == route.Name;
                case RouteKind.Command:
                    return ReachMarkup.Attr(e, "Command") is { } command && PathOf.Match(command) is { Success: true } m && LastPart(m.Groups[1].Value) == route.Name;
                case RouteKind.Parameter:
                    return ReachMarkup.Attr(e, "CommandParameter") == route.Name;
                default:
                    // A setting is changed by a settable element bound to it.
                    if (!ReachMarkup.Settable.Contains(e.Name.LocalName) && e.Name.LocalName is not ("ToggleButton" or "RadioButton")) return false;
                    return e.Attributes().Any(a => PathOf.Match(a.Value) is { Success: true } b && b.Groups[1].Value == route.Name);
            }
        }

        /// <summary>The reason of the nearest Asks on the way up, as
        /// Reach.Of and the snapshot read it.</summary>
        private static ReachReason WhyOf(XElement e)
        {
            for (var at = e; at != null; at = at.Parent)
                if (ReachMarkup.Attr(at, "Reach.Class") == "Asks")
                    return Enum.TryParse(ReachMarkup.Attr(at, "Reach.Why"), out ReachReason why) ? why : ReachReason.None;
            return ReachReason.None;
        }

        /// <summary>Each element of a file wired to a route of
        /// <paramref name="routes"/>, with its class and reason. A hosted
        /// control's elements take their host's class.</summary>
        internal static List<(Route Route, XElement Element, int Line, ReachClass Class, ReachReason Why)> WiredIn(string relative, string text, IEnumerable<Route> routes, ReachClass hostClass = ReachClass.Unset, ReachReason hostWhy = ReachReason.None)
        {
            var found = new List<(Route, XElement, int, ReachClass, ReachReason)>();
            // One row per route, the file's own first: a command listed for
            // two files is one route where it is bound.
            var list = routes.Where(r => r.File == relative || !r.OwnFileOnly)
                .GroupBy(r => (r.Kind, r.Name)).Select(g => g.FirstOrDefault(r => r.File == relative) ?? g.First()).ToList();
            foreach (var (element, cls, line, _) in ReachMarkup.Read(text))
            {
                foreach (var route in list)
                {
                    if (!Wired(element, route)) continue;
                    var c = cls == ReachClass.Unset ? hostClass : cls;
                    var why = c != ReachClass.Asks ? ReachReason.None : cls == ReachClass.Unset ? hostWhy : WhyOf(element);
                    found.Add((route, element, line, c, why));
                }
            }
            return found;
        }

        /// <summary>The class of the place a hosted control is drawn in.</summary>
        private static readonly Dictionary<string, (ReachClass Class, ReachReason Why)> Hosts = new()
        {
            ["Views/CommunityConfigsPanel.xaml"] = (ReachClass.Asks, ReachReason.ImportsProfile),   // in GetProfilesDialog
        };

        private static IEnumerable<(string Relative, string Text)> Markup()
            => ReachMarkup.Files().Select(f => (Path.GetRelativePath(ReachMarkup.AppRoot, f).Replace('\\', '/'), File.ReadAllText(f)));

        [Fact]
        public void TheCallsThatStartAProcessOrOpenAPlace_AreTheOnesWalked()
        {
            var found = FenceSource.Census(Starts, callsOnly: true);
            var pinned = Census.ToDictionary(c => c.File, c => c.Calls);
            Assert.True(found.Count == pinned.Count && found.All(f => pinned.TryGetValue(f.Key, out int n) && n == f.Value),
                "The calls that start a process or open a place differ from the census. Walk a new one to the elements that reach it, give them their class, and add its rows here. Found:\n"
                + string.Join("\n", found.Select(f => f.Key + " " + f.Value)));
            // The census reads the code: a call in a comment is no call.
            Assert.True(found.Values.Sum() >= 40, "the census read too little: " + found.Values.Sum());
        }

        [Fact]
        public void TheCallsThatSwitchTheProfile_AreTheOnesRuledOn()
        {
            var found = FenceSource.Census(ProfileSwitches, callsOnly: true);
            var pinned = ProfileSwitchCensus.ToDictionary(c => c.File, c => c.Calls);
            Assert.True(found.Count == pinned.Count && found.All(f => pinned.TryGetValue(f.Key, out int n) && n == f.Value),
                "The calls that switch the profile differ from the census. Walk a new one to who makes it, rule on it, and add its row here. Found:\n"
                + string.Join("\n", found.Select(f => f.Key + " " + f.Value)));
            // The pattern finds calls and leaves declarations: the command
            // set's two calls and its two after the card, and the window's.
            Assert.True(found.Values.Sum() >= 20, "the census read too little: " + found.Values.Sum());
        }

        [Fact]
        public void EveryElementWiredToOne_AsksWithItsReason_OrIsTheUsersOwn()
        {
            var wrong = new List<string>();
            var seen = new HashSet<Route>();
            foreach (var (relative, text) in Markup())
            {
                if (XDocument.Parse(text).Root.Name.LocalName is "ResourceDictionary" or "Application") continue;
                var (hostClass, hostWhy) = Hosts.TryGetValue(relative, out var host) ? host : (ReachClass.Unset, ReachReason.None);
                foreach (var (route, element, line, cls, why) in WiredIn(relative, text, Routes, hostClass, hostWhy))
                {
                    string where = relative + ":" + line + " <" + element.Name.LocalName + "> " + route.Kind + " " + route.Name;
                    // Wherever a command is bound, it never goes Free.
                    if (cls is ReachClass.Free or ReachClass.Unset) wrong.Add(where + " is " + cls + ", and reaches " + route.Reaches);
                    if (route.File != relative) continue;
                    seen.Add(route);
                    if (cls != route.Class || why != route.Why)
                        wrong.Add(where + " is " + cls + "/" + why + ", not " + route.Class + "/" + route.Why);
                }
            }
            foreach (var route in Routes.Where(r => !seen.Contains(r)))
                wrong.Add("no element of " + route.File + " is wired to " + route.Kind + " " + route.Name);
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        /// <summary>Each tile's switch carries its row's class and reason on
        /// the switch alone, and the strictest switch of its page among the
        /// settings it turns on carries the same: a switch never asks less
        /// than the page's own switch for the same start, nor more. Home's
        /// tiles bind one way and draw no switch.</summary>
        [Fact]
        public void EveryTilesSwitch_CarriesTheClassOfWhatItStarts()
        {
            string text = File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "Views", "ExtrasPage.xaml"));
            var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
            var tiles = doc.Descendants().Where(e => e.Name.LocalName == "ExtraTile").ToList();
            Assert.Equal(Tiles.Select(t => t.Key).OrderBy(k => k, StringComparer.Ordinal),
                tiles.Select(t => ReachMarkup.Attr(t, "Key")).OrderBy(k => k, StringComparer.Ordinal));
            // The strictest class on the way up, as Reach.Of reads it.
            static ReachClass ClassOf(XElement e) => e.AncestorsAndSelf().Select(a => ReachMarkup.Parse(ReachMarkup.Attr(a, "Reach.Class"))).Max();
            var wrong = new List<string>();
            foreach (var (key, setting, turnsOn, cls, why, _) in Tiles)
            {
                var tile = tiles.Single(t => ReachMarkup.Attr(t, "Key") == key);
                if (PathOf.Match(ReachMarkup.Attr(tile, "IsOn") ?? string.Empty) is not { Success: true } bound || bound.Groups[1].Value != setting)
                    wrong.Add(key + "'s tile does not bind " + setting);
                // The tile itself carries no class, so its button keeps the page's.
                if (ReachMarkup.Attr(tile, "Reach.Class") != null) wrong.Add(key + "'s tile carries a class of its own");
                var own = ReachMarkup.Parse(ReachMarkup.Attr(tile, "SwitchReach"));
                var ownWhy = Enum.TryParse(ReachMarkup.Attr(tile, "SwitchWhy"), out ReachReason w) ? w : ReachReason.None;
                if (own != cls || ownWhy != why) wrong.Add(key + "'s switch is " + own + "/" + ownWhy + ", not " + cls + "/" + why);

                // The switches of the extra's page among what the tile turns on.
                var page = doc.Descendants().Single(e => (string)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == key + "Page");
                var switches = page.Descendants()
                    .Where(e => ReachMarkup.Settable.Contains(e.Name.LocalName) || e.Name.LocalName is "ToggleButton" or "RadioButton")
                    .Where(e => e.Attributes().Any(a => PathOf.Match(a.Value) is { Success: true } b && turnsOn.Contains(b.Groups[1].Value)))
                    .ToList();
                if (switches.Count == 0)
                {
                    wrong.Add(key + "Page has no switch of " + string.Join(", ", turnsOn));
                    continue;
                }
                var strictest = switches.Select(e => (Class: ClassOf(e), Why: WhyOf(e)))
                    .OrderByDescending(s => s.Class).First();
                var pageWhy = strictest.Class == ReachClass.Asks ? strictest.Why : ReachReason.None;
                if (strictest.Class != cls || pageWhy != why)
                    wrong.Add(key + "Page's strictest switch is " + strictest.Class + "/" + pageWhy + ", the tile's " + cls + "/" + why);
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));

            // Home shows an extra that is on with no switch: it binds one way.
            var home = XDocument.Parse(File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "Views", "HomePage.xaml")));
            var rows = home.Descendants().Where(e => e.Name.LocalName == "ExtraTile").ToList();
            Assert.Equal(Tiles.Length, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Equal("{StaticResource HomeExtraTile}", ReachMarkup.Attr(r, "Style"));
                Assert.Contains("Mode=OneWay", ReachMarkup.Attr(r, "IsOn"));
                Assert.Null(ReachMarkup.Attr(r, "SwitchReach"));
            });

            // The catalog's entry for a tile opens its page and stays free.
            var extras = SearchIndexBuilder.Build(SearchSources.Load()).Where(e => e.Kind == SearchKind.Extra).ToList();
            Assert.Equal(Tiles.Length, extras.Count);
            Assert.All(extras, e => Assert.Equal(ReachClass.Free, e.Reach));
        }

        /// <summary>The catalog's entries for the same routes carry the same
        /// class, so find, read, set and press see it.</summary>
        [Fact]
        public void EveryCatalogEntryWiredToOne_IsNotFree()
        {
            var entries = SearchIndexBuilder.Build(SearchSources.Load()).Concat(SearchIndexBuilder.BuildCatalog(SearchSources.LoadCatalog())).ToList();
            // A Click handler is its own file's: an entry of that page or
            // dialog. A command is the same wherever it is bound.
            bool Wires(SearchEntry e, Route r)
            {
                if (e.Handler == null || LastPart(e.Handler) != r.Name) return false;
                if (!r.OwnFileOnly) return true;
                string name = Path.GetFileNameWithoutExtension(r.File);
                return e.Dialog != null ? e.Dialog == name : name == e.Page + "Page";
            }
            var wired = entries.Where(e => Routes.Any(r => r.Kind is RouteKind.Click or RouteKind.Command && Wires(e, r))).ToList();
            Assert.Contains(wired, e => LastPart(e.Handler) == "OpenReleaseNotesCommand");
            Assert.Contains(wired, e => LastPart(e.Handler) == "MoveToOwnFolder_Click");
            Assert.Contains(wired, e => LastPart(e.Handler) == "InstallUpdateCommand");
            var free = wired.Where(e => AssistantCatalog.ReachOf(e) == ReachClass.Free)
                .Select(e => e.Page + " " + LastPart(e.Handler) + " " + (e.AutomationId ?? SearchLocator.EnglishText(e.Label))).ToList();
            Assert.True(free.Count == 0, "Free in the catalog: " + string.Join("; ", free));
            // A press of one asks with the reason the markup gives.
            var notes = wired.Single(e => e.Page == SearchPage.Settings && LastPart(e.Handler) == "OpenReleaseNotesCommand");
            Assert.Equal((ReachClass.Asks, ReachReason.OpensPlace), (notes.Reach, notes.Why));
            var install = wired.Single(e => e.Page == SearchPage.Settings && LastPart(e.Handler) == "InstallUpdateCommand");
            Assert.Equal((ReachClass.Asks, ReachReason.RunsProgram), (install.Reach, install.Why));
        }

        /// <summary>A button wired to a route, added to a Free page with no
        /// class of its own, is caught, and one inside a part that asks is
        /// not: the check is no empty pass.</summary>
        [Fact]
        public void APlantedFreeButtonWiredToOne_IsCaught()
        {
            const string head = "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:common=\"clr-namespace:PadForge.Common\" common:Reach.Class=\"Free\"><StackPanel>";
            string planted = head + "<Button Click=\"OpenDiagFolder_Click\"/><Button Command=\"{Binding OpenReleaseNotesCommand}\"/>"
                             + "<StackPanel common:Reach.Class=\"Asks\" common:Reach.Why=\"OpensPlace\"><Button Command=\"{Binding Path=OpenReleaseNotesCommand}\"/></StackPanel></StackPanel></UserControl>";
            var found = WiredIn("Views/SettingsPage.xaml", planted, Routes);
            Assert.Equal(3, found.Count);
            Assert.Equal(2, found.Count(f => f.Class == ReachClass.Free));
            Assert.Single(found, f => f.Class == ReachClass.Asks && f.Why == ReachReason.OpensPlace);
            // A handler of the same name in another file's code is another
            // method, and a command is the same wherever it is bound.
            var elsewhere = WiredIn("Views/HomePage.xaml", planted, Routes);
            Assert.Equal(2, elsewhere.Count);
            Assert.All(elsewhere, f => Assert.Equal(RouteKind.Command, f.Route.Kind));
        }

        /// <summary>The Run Program step's fields ask as running a program,
        /// on its own words, wherever the macro section asks as sending
        /// input.</summary>
        [Fact]
        public void TheRunProgramStep_AsksAsRunningAProgram()
        {
            string text = File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "Views", "PadPage.xaml"));
            var doc = XDocument.Parse(text);
            var template = doc.Descendants().Single(e => e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "StepFieldsRunProgram"));
            var fields = template.Descendants().Where(e => ReachMarkup.Counts(e)).ToList();
            Assert.True(fields.Count >= 6, "the step's fields were not found");
            Assert.All(fields, f => Assert.Equal(ReachReason.RunsProgram, WhyOf(f)));
            // The words the card gives for it.
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Automation_Ask_RunsProgram, AssistantToolWords.Reason(ReachReason.RunsProgram));
        }

        /// <summary>The typed tool asks at the same point: save_macro scans
        /// the macro, a Run Program step gives the card's "Runs" line, and the
        /// macro is saved only when the user allows it (#600 item 2's
        /// indirect route, AS-191).</summary>
        [Fact]
        public async Task AMacroThatRunsAProgram_WaitsForTheCard_BeforeItIsSaved()
        {
            // The scan of a real macro.
            var macro = new MacroData { Actions = new[] { new ActionData { Type = MacroActionType.RunProgram, ProgramPath = @"C:\Tools\tool.exe" } } };
            Assert.Contains(AssistantToolWords.RunsProgram(@"C:\Tools\tool.exe"), AssistantScan.Macro(macro));
            Assert.Empty(AssistantScan.Macro(new MacroData { Actions = new[] { new ActionData { Type = MacroActionType.ButtonPress } } }));

            // The tool's ask, through the server, the card and its answers.
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings, approvalWait: TimeSpan.FromSeconds(5), answerWait: TimeSpan.FromSeconds(5));
            var client = await rig.ConnectAsync();
            async Task<ModelContextProtocol.Protocol.CallToolResult> Save(bool allow)
            {
                var call = client.CallToolAsync("save_macro", AssistantRig.Args(("controller", 1),
                    ("macro", System.Text.Json.JsonDocument.Parse("{\"runs\":\"C:\\\\Tools\\\\tool.exe\"}").RootElement))).AsTask();
                await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 1 || call.IsCompleted);
                var pending = Assert.Single(rig.Host.Approvals.Pending);
                Assert.Contains(pending.Request.Detail, d => d.Contains(@"C:\Tools\tool.exe", StringComparison.Ordinal));
                Assert.DoesNotContain("macro 1", rig.App.Calls);
                Assert.True(rig.Host.Approvals.Answer(pending.Id, allow));
                return await call;
            }
            var refused = await Save(false);
            Assert.True(refused.IsError);
            Assert.DoesNotContain("macro 1", rig.App.Calls);
            var allowed = await Save(true);
            Assert.False(allowed.IsError == true, AssistantRig.Text(allowed));
            Assert.Contains("macro 1", rig.App.Calls);
        }

        /// <summary>Search's docs results open a page only on the person's
        /// own Enter or click: the catalog's navigator, which set, press and
        /// show go through, never opens one.</summary>
        [Fact]
        public void ADocsResult_IsOpenedOnlyByThePerson()
        {
            string catalog = OffLimitsSource.Read("PadForge.App/Services/Automation/Assistant/AssistantCatalog.cs");
            Assert.DoesNotContain("SearchTarget.Docs", catalog);
            string panel = OffLimitsSource.Read("PadForge.App/Views/SearchPanel.xaml.cs");
            Assert.Equal(2, Regex.Matches(panel, @"Vm\?\.Open\(|vm\.Open\(\)").Count);
        }
    }
}
