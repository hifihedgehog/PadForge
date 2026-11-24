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
    /// The state a program's run of changes is undone to (#597), taken and
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

        /// <summary>Puts a state back as one step and says what came back.
        /// <paramref name="redraw"/> draws the sidebar's controllers
        /// again.</summary>
        internal static string Restore(AssistantUndoState state, InputService input, SettingsService settings, MainViewModel vm, Action redraw)
        {
            SettingsManager.Profiles.Clear();
            foreach (var p in (List<ProfileData>)state.Profiles) SettingsManager.Profiles.Add(PadForge.Common.ProfileTransfer.Clone(p));
            SettingsManager.PendingDefaultSnapshot = PadForge.Common.ProfileTransfer.Clone((ProfileData)state.DefaultSnapshot);
            SettingsManager.ActiveProfileId = state.ActiveProfileId;
            input.ApplyProfile(PadForge.Common.ProfileTransfer.Clone((ProfileData)state.Profile));
            settings.LoadAppSettings((AppSettingsData)state.AppSettings);

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
                + state.Taken.ToLocalTime().ToString("T", CultureInfo.InvariantCulture) + ".";
        }
    }
}
