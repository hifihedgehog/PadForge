using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Privacy;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The state a program's run of changes is undone to (#600), taken and
    /// put back through the calls the profile switch makes: the stored
    /// profiles and the default's parked snapshot, which one is in force, the
    /// live setup (InputService.SnapshotCurrentProfile and ApplyProfile), and
    /// a copy of the app settings, which that snapshot does not hold
    /// (SettingsService.BuildAppSettings and LoadAppSettings). Called on the
    /// interface's thread.
    /// </summary>
    internal static class AssistantUndoStore
    {
        internal static AssistantUndoState Take(InputService input, SettingsService settings)
            => new AssistantUndoState
            {
                Profile = PadForge.Common.ProfileTransfer.Clone(input.SnapshotCurrentProfile()),
                AppSettings = settings.BuildAppSettings(),
                Profiles = SettingsManager.Profiles.Select(PadForge.Common.ProfileTransfer.Clone).ToList(),
                DefaultSnapshot = PadForge.Common.ProfileTransfer.Clone(SettingsManager.PendingDefaultSnapshot),
                ActiveProfileId = SettingsManager.ActiveProfileId,
                Taken = DateTimeOffset.Now,
            };

        /// <summary>The app settings of the controls only the user reaches
        /// (Reach.Class UsersOwn): Automation's switch and level, the Plugins
        /// page's switch, plugins, Test Controller and Replay, the Web
        /// Controller's access code, Remote Link's identity and trusted PCs,
        /// and what a recording holds. Each is taken from
        /// <paramref name="now"/>.</summary>
        private static void KeepTheUsersOwn(AppSettingsData app, AppSettingsData now)
        {
            app.EnableAutomation = now.EnableAutomation;
            app.AutomationLevel = now.AutomationLevel;
            app.EnablePlugins = now.EnablePlugins;
            app.Plugins = now.Plugins;
            app.TestControllerOn = now.TestControllerOn;
            app.TestControllerValues = now.TestControllerValues;
            app.ReplayOn = now.ReplayOn;
            app.ReplayRepeat = now.ReplayRepeat;
            app.WebControllerAccessCodeProtected = now.WebControllerAccessCodeProtected;
            app.RemoteLinkIdentityPrivate = now.RemoteLinkIdentityPrivate;
            app.RemoteLinkIdentityPublic = now.RemoteLinkIdentityPublic;
            app.RemoteLinkIdentityProtection = now.RemoteLinkIdentityProtection;
            app.RemoteLinkPeers = now.RemoteLinkPeers;
            app.ProblemRecordingLeaveOut = now.ProblemRecordingLeaveOut;
        }

        /// <summary>The fields of the app settings copy BuildAppSettings takes
        /// from the default's snapshot while a named profile is in force,
        /// each from that profile's snapshot.</summary>
        private static void TakeTheProfilesControllers(AppSettingsData app, ProfileData profile, MainViewModel vm)
        {
            app.SlotCreated = profile.SlotCreated;
            app.SlotEnabled = profile.SlotEnabled;
            app.SlotControllerTypes = profile.SlotControllerTypes;
            app.SlotProfileIds = profile.SlotProfileIds;
            app.SlotModel3DAppearances = SlotAppearancePersistence.ResolveProfile(profile, vm.Pads);
            app.TouchpadGestures = profile.TouchpadGestures;
            app.ExtendedConfigs = profile.ExtendedConfigs;
            app.DeviceSlotConfigs = profile.DeviceSlotConfigs;
            app.MidiConfigs = profile.MidiConfigs;
            app.KbmConfigs = profile.KbmConfigs;
            app.XboxSlotOrder = profile.XboxSlotOrder;
            app.PlayStationSlotOrder = profile.PlayStationSlotOrder;
            app.NintendoSlotOrder = profile.NintendoSlotOrder;
            app.ExtendedSlotOrder = profile.ExtendedSlotOrder;
            app.KeyboardMouseSlotOrder = profile.KeyboardMouseSlotOrder;
            app.MidiSlotOrder = profile.MidiSlotOrder;
            app.VrSlotOrder = profile.VrSlotOrder;
        }

        /// <summary>What putting a state back would turn on that listens on
        /// the network and is off now (#600), by the names the question card
        /// gives: the extras the setup in force holds
        /// (<see cref="AssistantScan.NetworkStarts"/>), Remote Link, and
        /// Switch Profiles by Game while a profile it would put in force turns
        /// one on. Empty when it turns on none. Interface thread.</summary>
        internal static IReadOnlyList<string> NetworkStarts(AssistantUndoState state, MainViewModel vm)
        {
            var names = AssistantScan.NetworkStarts((ProfileData)state.Profile, vm.Dashboard);
            var app = (AppSettingsData)state.AppSettings;
            if (app == null) return names;
            if (app.EnableRemoteLink && !vm.Dashboard.EnableRemoteLink) names.Add(Strings.Instance.Dashboard_RemoteLink);
            if (app.EnableAutoProfileSwitching && !vm.Settings.EnableAutoProfileSwitching
                && AssistantScan.NetworkLines(nameof(SettingsViewModel.EnableAutoProfileSwitching), vm.Settings, vm) is { Count: > 0 })
                names.Add(Strings.Instance.Profiles_AutoSwitch);
            return names;
        }

        /// <summary>Puts a state back as one step and says what came back.
        /// <paramref name="redraw"/> draws the sidebar's controllers
        /// again.</summary>
        internal static string Restore(AssistantUndoState state, InputService input, SettingsService settings, MainViewModel vm, Action redraw)
        {
            var app = (AppSettingsData)state.AppSettings;
            var profile = (ProfileData)state.Profile;
            // Under a named profile the copy holds the default's controllers
            // and their configs (BuildAppSettings keeps the default's there),
            // and LoadAppSettings below would write them over the profile's,
            // which ApplyProfile has just put in. A load mends the same thing
            // with LoadProfiles. Here the copy takes the profile's own, so
            // the load writes what ApplyProfile wrote.
            if (app.DefaultProfileSnapshot != null && profile != null) TakeTheProfilesControllers(app, profile, vm);
            // What only the user changes stays as the user left it (rule 12):
            // no program's run changed it, so an undo would only take back the
            // user's own change since, the Plugins switch and a plugin the
            // user removed among them. LoadFromFile keeps the allowed
            // programs, the web grants and the answer devices out of the
            // copy's reach for the same reason.
            KeepTheUsersOwn(app, settings.BuildAppSettings());
            SettingsManager.Profiles.Clear();
            foreach (var p in (List<ProfileData>)state.Profiles) SettingsManager.Profiles.Add(PadForge.Common.ProfileTransfer.Clone(p));
            SettingsManager.PendingDefaultSnapshot = PadForge.Common.ProfileTransfer.Clone((ProfileData)state.DefaultSnapshot);
            SettingsManager.ActiveProfileId = state.ActiveProfileId;
            input.ApplyProfile(PadForge.Common.ProfileTransfer.Clone(profile));
            settings.LoadAppSettings(app);

            // The list the Profiles page shows, as a load builds it.
            var items = vm.Settings.ProfileItems;
            for (int i = items.Count - 1; i >= 0; i--) if (!items[i].IsDefault) items.RemoveAt(i);
            foreach (var p in SettingsManager.Profiles)
            {
                var item = new ProfileListItem
                {
                    Id = p.Id,
                    Name = p.Name,
                    Executables = InputService.FormatExePaths(p.ExecutableNames ?? string.Empty),
                    ExecutablePaths = p.ExecutableNames ?? string.Empty,
                };
                SettingsService.UpdateTopologyCounts(item, p.SlotCreated, p.SlotControllerTypes, p.SlotProfileIds, p.SlotModel3DAppearances);
                items.Add(item);
            }
            var active = SettingsManager.Profiles.Find(p => p.Id == state.ActiveProfileId);
            vm.Settings.ActiveProfileInfo = active?.Name ?? Strings.Instance.Profile_Default;
            settings.MarkDirty();
            redraw?.Invoke();
            return "Put back the profiles, the profile in force (" + (active?.Name ?? "the default profile")
                + ") with its controllers, assignments, mappings and macros, and the app settings, as they were at "
                + state.Taken.ToLocalTime().ToString("T", CultureInfo.InvariantCulture) + ". What only the user changes stayed as it is now:"
                + " Automation, the Plugins page (its switch, the plugins, Test Controller and Replay), the Web Controller's access code,"
                + " Remote Link's identity and trusted PCs, and what a recording holds.";
        }
    }
}
