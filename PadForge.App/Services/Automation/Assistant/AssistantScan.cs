using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// What a change would make PadForge do outside itself (#597), one line
    /// each for the request card: the programs a macro or a profile would run
    /// and the files and addresses it would open, the keys it would press,
    /// the text it would type and the mouse input it would send to Windows,
    /// a keyboard and mouse controller, and the extras it would turn on that
    /// listen on the network. An empty list asks nothing.
    ///
    /// <para>No function scanned a profile's or a macro's data for this
    /// before: the foreign importers record the programs they translate
    /// (ImportBuilder.Run), which a .pfprofile never passes through.</para>
    /// </summary>
    internal static class AssistantScan
    {
        /// <summary>The steps that send keys to Windows.</summary>
        private static readonly MacroActionType[] Keys =
        {
            MacroActionType.KeyPress, MacroActionType.KeyRelease, MacroActionType.ToggleKey, MacroActionType.RepeatKeyWhileHeld,
        };

        /// <summary>The steps that move the pointer, click or scroll in
        /// Windows.</summary>
        private static readonly MacroActionType[] Mouse =
        {
            MacroActionType.MouseMove, MacroActionType.MouseButtonPress, MacroActionType.MouseButtonRelease, MacroActionType.MouseScroll,
            MacroActionType.MouseRecenter, MacroActionType.MouseFixPosition, MacroActionType.MouseLimitRegion,
            MacroActionType.MoveMouseToScreenPosition, MacroActionType.MouseWheelTap, MacroActionType.MouseNudge,
            MacroActionType.ToggleMouseButton, MacroActionType.ToggleWheel,
        };

        /// <summary>What one macro would do outside PadForge.</summary>
        internal static List<string> Macro(MacroData macro)
        {
            var lines = new List<string>();
            if (macro?.Actions == null) return lines;
            foreach (var action in macro.Actions)
            {
                if (action == null) continue;
                string line = null;
                if (action.Type == MacroActionType.RunProgram)
                {
                    string program = (action.ProgramPath ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(action.ProgramArgs)) program += " " + action.ProgramArgs.Trim();
                    line = AssistantToolWords.RunsProgram(program.Length > 0 ? program : "?");
                }
                else if (Keys.Contains(action.Type))
                {
                    string key = !string.IsNullOrWhiteSpace(action.KeyString) ? action.KeyString
                        : action.KeyCode != 0 ? "0x" + action.KeyCode.ToString("X2", CultureInfo.InvariantCulture) : "?";
                    line = AssistantToolWords.PressesKey(key);
                }
                else if (action.Type == MacroActionType.TextBlock) line = AssistantToolWords.TypesText();
                else if (Mouse.Contains(action.Type)) line = AssistantToolWords.MovesMouse();
                else if (action.Type == MacroActionType.ProfileShortcut)
                {
                    // A switch to a profile that turns on an extra that
                    // listens on the network (#600): the one it names, or for
                    // Next and Previous every profile the round can land on.
                    foreach (var profile in SwitchesTo(action))
                        foreach (string name in NetworkExtras(profile))
                        {
                            string turnsOn = AssistantToolWords.TurnsOn(name);
                            if (!lines.Contains(turnsOn)) lines.Add(turnsOn);
                        }
                }
                if (line != null && !lines.Contains(line)) lines.Add(line);
            }
            return lines;
        }

        /// <summary>The profiles a Profile Shortcut step can switch to: the
        /// one it names, by name as the step finds it
        /// (InputManager.ProfileIdNamed), or for Next and Previous every
        /// profile and the default. The other modes switch none.</summary>
        private static IEnumerable<ProfileData> SwitchesTo(ActionData action)
        {
            var profiles = ProfilesNow();
            switch (action.ProfileShortcutMode)
            {
                case SwitchProfileMode.Specific:
                    if (string.IsNullOrWhiteSpace(action.ProfileShortcutName)) return System.Array.Empty<ProfileData>();
                    return profiles.Where(p => string.Equals(p.Name, action.ProfileShortcutName, System.StringComparison.OrdinalIgnoreCase)).Take(1);
                case SwitchProfileMode.Next:
                case SwitchProfileMode.Previous:
                    return SettingsManager.PendingDefaultSnapshot is { } byDefault ? profiles.Append(byDefault) : profiles;
                default:
                    return System.Array.Empty<ProfileData>();
            }
        }

        /// <summary>A copy of the profile list, taken again when a change in
        /// flight tears it, as the macro engine takes its own
        /// (InputManager.ProfilesSnapshot).</summary>
        private static ProfileData[] ProfilesNow()
        {
            var list = SettingsManager.Profiles;
            if (list == null) return System.Array.Empty<ProfileData>();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { return list.ToArray().Where(p => p != null).ToArray(); }
                catch (System.ArgumentException) { }
                catch (System.InvalidOperationException) { }
            }
            return System.Array.Empty<ProfileData>();
        }

        /// <summary>The extras that listen on the network a profile turns
        /// on when it comes into force, whatever is on now, by the names the
        /// question card gives: Web Controller, Motion Server, Game Mods and
        /// Game Telemetry by their own switches, and Head Tracking's UDP
        /// input when the profile holds an opinion of it
        /// (SettingsService.ApplyProfileServiceToggles).</summary>
        private static IEnumerable<string> NetworkExtras(ProfileData profile)
        {
            if (profile == null) yield break;
            var s = Strings.Instance;
            if (profile.EnableWebController) yield return s.Dashboard_WebController;
            if (profile.EnableDsuMotionServer) yield return s.Dashboard_MotionServer;
            if (profile.EnableGameModServer) yield return s.Dashboard_GameMods;
            if (profile.EnableGameTelemetry) yield return s.Dashboard_GameTelemetry;
            if (profile.EnableHeadTracking == true) yield return s.Automation_Ask_HeadTrackingUdp;
        }

        /// <summary>The extras that listen on the network which a switch to
        /// <paramref name="target"/> would turn on, being off in
        /// <paramref name="now"/> (#600), by the names the question card
        /// gives. Empty when the switch starts nothing new, which asks
        /// nothing.</summary>
        internal static List<string> NetworkStarts(ProfileData target, DashboardViewModel now)
        {
            var names = new List<string>();
            if (target == null || now == null) return names;
            var s = Strings.Instance;
            if (target.EnableWebController && !now.EnableWebController) names.Add(s.Dashboard_WebController);
            if (target.EnableDsuMotionServer && !now.EnableDsuMotionServer) names.Add(s.Dashboard_MotionServer);
            if (target.EnableGameModServer && !now.EnableGameModServer) names.Add(s.Dashboard_GameMods);
            if (target.EnableGameTelemetry && !now.EnableGameTelemetry) names.Add(s.Dashboard_GameTelemetry);
            if (target.EnableHeadTracking == true && !now.HeadTrackingEnabled) names.Add(s.Automation_Ask_HeadTrackingUdp);
            return names;
        }

        /// <summary>Whether turning on a setting the network reason asks for
        /// starts a listener now (#600). The catalog asks before such a
        /// setting turns on (AssistantCatalog.AskFor), and a switch that
        /// brings back no listener has nothing to ask about: the Head
        /// Tracking switch with only OpenXR or FreeTrack to bring back. Every
        /// other such setting starts its listener when it turns on, and so
        /// does a setting whose data is not at hand.</summary>
        internal static bool StartsAListener(string path, object data)
            => path != nameof(DashboardViewModel.HeadTrackingAnyEnabled) || data is not DashboardViewModel vm || vm.HeadTrackingSwitchStartsUdp;

        /// <summary>
        /// What turning on a setting the network reason asks for would start
        /// now (#600), as the lines the question card adds. Empty when it
        /// would start nothing, which asks nothing. Null for a setting whose
        /// start this does not read, which asks by its reason alone.
        ///
        /// <para>Switch Profiles by Game lets the foreground watcher put in
        /// force each profile with a game, and the default when the game
        /// leaves the front (InputService.OnAutoProfileSwitchRequired), and a
        /// profile's switch turns on what it holds. One line for each such
        /// profile and each extra that listens on the network it holds, on or
        /// off now: a listener turned off after the user allowed this comes
        /// back with the watcher's switch, which asks nothing. An extra's own
        /// switch names the profile it turns the extra on in
        /// (<see cref="ProfileExtraLines"/>).</para>
        /// </summary>
        internal static IReadOnlyList<string> NetworkLines(string path, object data, MainViewModel now)
        {
            if (now == null) return null;
            if (path == nameof(SettingsViewModel.EnableAutoProfileSwitching))
            {
                var lines = new List<string>();
                if ((data as SettingsViewModel ?? now.Settings)?.EnableAutoProfileSwitching == true) return lines;
                bool games = false;
                foreach (var profile in ProfilesNow())
                {
                    if (string.IsNullOrWhiteSpace(profile.ExecutableNames)) continue;
                    games = true;
                    Add(lines, NamedLines(profile.Name, HeldExtras(profile, now.Dashboard)));
                }
                if (games) Add(lines, DefaultLines(now.Dashboard));
                return lines;
            }
            return ProfileExtraLines(path, data as DashboardViewModel ?? now.Dashboard);
        }

        private static void Add(List<string> lines, IEnumerable<string> more)
        {
            foreach (string line in more)
                if (!lines.Contains(line)) lines.Add(line);
        }

        /// <summary>The extras that listen on the network a profile holds, by
        /// the names the question card gives, which its switch turns on
        /// (#600). The profile in force holds the live setup, which goes into
        /// it at the next switch (InputService.SaveActiveProfileState), with
        /// its own opinion of Head Tracking's input
        /// (SettingsService.OnDashboardServiceToggleChanged). Any other holds
        /// what is stored.</summary>
        internal static List<string> HeldExtras(ProfileData profile, DashboardViewModel now)
        {
            if (profile == null) return new List<string>();
            if (!InForce(profile)) return NetworkExtras(profile).ToList();
            var held = LiveExtras(now, headTracking: false);
            if (profile.EnableHeadTracking == true) held.Add(Strings.Instance.Automation_Ask_HeadTrackingUdp);
            return held;
        }

        /// <summary>What the default holds that listens on the network, which
        /// a return to it turns on (#600): the live setup while it is in
        /// force, which goes into its snapshot at the next switch, else its
        /// snapshot. It holds no opinion of Head Tracking's input.</summary>
        internal static List<string> DefaultExtras(DashboardViewModel now)
            => string.IsNullOrEmpty(SettingsManager.ActiveProfileId)
                ? LiveExtras(now, headTracking: false)
                : NetworkExtras(SettingsManager.PendingDefaultSnapshot).ToList();

        /// <summary>The default's lines, naming it.</summary>
        internal static List<string> DefaultLines(DashboardViewModel now)
            => NamedLines(Strings.Instance.Profile_Default, DefaultExtras(now));

        /// <summary>Whether a list of games gains one. Windows reads a path the
        /// same in either case, and so do the list and the watcher.</summary>
        internal static bool AddsAGame(IEnumerable<string> before, IEnumerable<string> after)
        {
            if (after == null) return false;
            var had = new HashSet<string>(before ?? Enumerable.Empty<string>(), System.StringComparer.OrdinalIgnoreCase);
            return !after.All(had.Contains);
        }

        /// <summary>
        /// What a game added to a profile's list while Switch Profiles by
        /// Game is on lets the foreground watcher turn on (#600), as the
        /// question card's lines: what the profile holds, which comes on when
        /// the game is in front, and what the default holds, which comes on
        /// when the game leaves it, each named with its profile, on or off
        /// now.
        /// </summary>
        internal static List<string> WatcherLines(string profile, IEnumerable<string> held, DashboardViewModel now)
        {
            var lines = NamedLines(profile, held);
            Add(lines, DefaultLines(now));
            return lines;
        }

        /// <summary>What an imported profile would hold that listens on the
        /// network, as the question card's lines, named with the profile. With
        /// games while Switch Profiles by Game is on, what the default holds
        /// too, which the watcher returns to (#600).</summary>
        internal static List<string> ImportLines(ProfileData profile, bool switchingByGame, DashboardViewModel now)
        {
            if (profile == null) return new List<string>();
            var lines = NamedLines(profile.Name, NetworkExtras(profile));
            if (switchingByGame && !string.IsNullOrWhiteSpace(profile.ExecutableNames)) Add(lines, DefaultLines(now));
            return lines;
        }

        /// <summary>
        /// What a program's turning on an extra a profile holds would turn on
        /// in a profile (#600), as the question card's line: the profile in
        /// force, or the default, holds the live setup, and a profile's
        /// switch turns on what it holds. Empty when the extra is on already,
        /// which changes nothing. Null for a setting that is no such extra,
        /// and for Head Tracking's UDP input with the default in force: the
        /// default holds no opinion of it (SettingsService.ApplyProfileServiceToggles),
        /// so turning it on there changes the live setup alone, and the card
        /// asks by its reason.
        /// </summary>
        private static List<string> ProfileExtraLines(string path, DashboardViewModel now)
        {
            if (now == null) return null;
            var s = Strings.Instance;
            (bool On, string Name) extra;
            switch (path)
            {
                case nameof(DashboardViewModel.EnableWebController): extra = (now.EnableWebController, s.Dashboard_WebController); break;
                case nameof(DashboardViewModel.EnableDsuMotionServer): extra = (now.EnableDsuMotionServer, s.Dashboard_MotionServer); break;
                case nameof(DashboardViewModel.EnableGameModServer): extra = (now.EnableGameModServer, s.Dashboard_GameMods); break;
                case nameof(DashboardViewModel.EnableGameTelemetry): extra = (now.EnableGameTelemetry, s.Dashboard_GameTelemetry); break;
                case nameof(DashboardViewModel.HeadTrackingEnabled):
                case nameof(DashboardViewModel.HeadTrackingAnyEnabled):
                    if (string.IsNullOrEmpty(SettingsManager.ActiveProfileId)) return null;
                    extra = (now.HeadTrackingEnabled, s.Automation_Ask_HeadTrackingUdp);
                    break;
                default: return null;
            }
            if (extra.On) return new List<string>();
            return new List<string> { AssistantToolWords.ProfileTurnsOn(InForceName(), extra.Name) };
        }

        /// <summary>The profile in force, by the name the question card gives:
        /// the named profile, or the default.</summary>
        internal static string InForceName()
        {
            string id = SettingsManager.ActiveProfileId;
            if (!string.IsNullOrEmpty(id))
                foreach (var profile in ProfilesNow())
                    if (profile.Id == id) return profile.Name;
            return Strings.Instance.Profile_Default;
        }

        /// <summary>The extras that listen on the network on now, by the names
        /// the question card gives (#600): what a profile made of the live
        /// setup holds (InputService.SnapshotCurrentProfile). Head Tracking's
        /// UDP input with them unless <paramref name="headTracking"/> is
        /// false: a snapshot of the live setup carries no opinion of it, so a
        /// profile made of one leaves it as it is.</summary>
        internal static List<string> LiveExtras(DashboardViewModel now, bool headTracking = true)
        {
            var names = new List<string>();
            if (now == null) return names;
            var s = Strings.Instance;
            if (now.EnableWebController) names.Add(s.Dashboard_WebController);
            if (now.EnableDsuMotionServer) names.Add(s.Dashboard_MotionServer);
            if (now.EnableGameModServer) names.Add(s.Dashboard_GameMods);
            if (now.EnableGameTelemetry) names.Add(s.Dashboard_GameTelemetry);
            if (headTracking && now.HeadTrackingEnabled) names.Add(s.Automation_Ask_HeadTrackingUdp);
            return names;
        }

        /// <summary>One line for each extra, naming the profile that turns it
        /// on.</summary>
        internal static List<string> NamedLines(string profile, IEnumerable<string> extras)
            => (extras ?? Enumerable.Empty<string>()).Select(extra => AssistantToolWords.ProfileTurnsOn(profile, extra)).Distinct().ToList();

        /// <summary>What Save Current Setup Here would put into
        /// <paramref name="profile"/> that listens on the network (#600), as
        /// the question card's lines: every extra on now, which the live
        /// setup carries into it whether or not the profile held it before
        /// (InputService.SaveCurrentSetupInto). Head Tracking's input stays
        /// out: the live setup carries no opinion of it.</summary>
        internal static List<string> SaveHereLines(ProfileData profile, DashboardViewModel now)
            => profile == null ? new List<string>() : NamedLines(profile.Name, LiveExtras(now, headTracking: false));

        /// <summary>Whether a profile is the one in force.</summary>
        private static bool InForce(ProfileData profile) => profile.Id != null && profile.Id == SettingsManager.ActiveProfileId;

        /// <summary>What a whole profile would do outside PadForge once it is
        /// in force.</summary>
        internal static List<string> Profile(ProfileData profile)
        {
            var lines = new List<string>();
            if (profile == null) return lines;
            void Add(string line)
            {
                if (line != null && !lines.Contains(line)) lines.Add(line);
            }
            foreach (var macro in profile.Macros ?? System.Array.Empty<MacroData>())
                foreach (string line in Macro(macro)) Add(line);
            if (profile.SlotCreated != null && profile.SlotControllerTypes != null)
            {
                for (int i = 0; i < profile.SlotCreated.Length && i < profile.SlotControllerTypes.Length; i++)
                    if (profile.SlotCreated[i] && (VirtualControllerType)profile.SlotControllerTypes[i] == VirtualControllerType.KeyboardMouse)
                        Add(AssistantToolWords.MakesKeyboardMouse());
            }
            foreach (string name in NetworkExtras(profile)) Add(AssistantToolWords.TurnsOn(name));
            return lines;
        }
    }
}
