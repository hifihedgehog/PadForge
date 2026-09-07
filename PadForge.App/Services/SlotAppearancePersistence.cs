using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.ViewModels;

namespace PadForge.Services
{
    internal static class SlotAppearancePersistence
    {
        internal static string[] Empty(int count = InputManager.MaxPads)
            => Normalize(null, count);

        private static string[] Normalize(string[] values, int count)
        {
            var result = new string[count];
            for (int i = 0; i < count; i++)
                result[i] = values != null && i < values.Length ? values[i] ?? "" : "";
            return result;
        }

        internal static string[] Capture(IList<PadViewModel> pads)
        {
            var result = Empty();
            for (int i = 0; i < result.Length && i < pads.Count; i++)
                if (SettingsManager.SlotCreated[i]) result[i] = pads[i].Model3DAppearances ?? "";
            return result;
        }

        internal static void Apply(IList<PadViewModel> pads, string[] values)
        {
            for (int i = 0; i < pads.Count; i++)
                pads[i].Model3DAppearances = i < SettingsManager.SlotCreated.Length
                    && SettingsManager.SlotCreated[i] && values != null && i < values.Length
                    ? values[i] ?? "" : "";
        }

        internal static string[] ResolveApp(AppSettingsData settings, IList<PadViewModel> pads)
        {
            if (settings.SlotModel3DAppearances == null)
            {
                // With a named profile active, root UserSettings belong to that profile.
                if (settings.DefaultProfileSnapshot != null)
                    settings.SlotModel3DAppearances = ResolveProfile(settings.DefaultProfileSnapshot, pads);
                else
                {
                    var rows = new List<(int Slot, Guid Device, string Value)>();
                    lock (SettingsManager.UserSettings.SyncRoot)
                        foreach (var row in SettingsManager.UserSettings.Items)
                            if (row != null)
                                rows.Add((row.MapTo, row.InstanceGuid, row.GetPadSetting()?.Model3DAppearances));
                    settings.SlotModel3DAppearances = ResolveLegacy(rows, pads, InputManager.MaxPads);
                }
            }
            return Normalize(settings.SlotModel3DAppearances, InputManager.MaxPads);
        }

        internal static string[] ResolveProfile(ProfileData profile, IList<PadViewModel> pads = null,
            int count = InputManager.MaxPads)
        {
            if (profile.SlotModel3DAppearances == null)
            {
                var byChecksum = new Dictionary<string, PadSetting>(StringComparer.Ordinal);
                if (profile.PadSettings != null)
                    foreach (var setting in profile.PadSettings)
                        if (setting != null && !string.IsNullOrEmpty(setting.PadSettingChecksum))
                            byChecksum.TryAdd(setting.PadSettingChecksum, setting);
                var rows = new List<(int Slot, Guid Device, string Value)>();
                if (profile.Entries != null)
                    foreach (var entry in profile.Entries)
                        if (entry != null && !string.IsNullOrEmpty(entry.PadSettingChecksum)
                            && byChecksum.TryGetValue(entry.PadSettingChecksum, out var setting))
                            rows.Add((entry.MapTo, entry.InstanceGuid, setting.Model3DAppearances));
                profile.SlotModel3DAppearances = ResolveLegacy(rows, pads, count);
            }
            return Normalize(profile.SlotModel3DAppearances, count);
        }

        private static string[] ResolveLegacy(IEnumerable<(int Slot, Guid Device, string Value)> rows,
            IList<PadViewModel> pads, int count)
        {
            var result = Empty(count);
            var selectedChosen = new bool[count];
            foreach (var row in rows)
            {
                if (row.Slot < 0 || row.Slot >= count || string.IsNullOrWhiteSpace(row.Value)) continue;
                Guid selected = pads != null && row.Slot < pads.Count
                    ? pads[row.Slot].SelectedMappedDevice?.InstanceGuid ?? Guid.Empty : Guid.Empty;
                bool preferred = selected != Guid.Empty && row.Device == selected;
                if (!selectedChosen[row.Slot] && (result[row.Slot].Length == 0 || preferred))
                {
                    result[row.Slot] = row.Value;
                    selectedChosen[row.Slot] = preferred;
                }
            }
            return result;
        }

        internal static PadSetting CreateClipboardSetting(PadViewModel pad, PadSetting selected)
        {
            // A slot can have saved device settings before its picker is populated.
            if (selected == null && SettingsManager.UserSettings is { } settings)
            {
                lock (settings.SyncRoot)
                    foreach (var row in settings.Items)
                        if (row != null && row.MapTo == pad.PadIndex && row.GetPadSetting() is { } saved)
                        { selected = saved; break; }
            }
            var copy = selected?.CloneDeep() ?? new PadSetting();
            copy.Model3DAppearances = pad.Model3DAppearances ?? "";
            copy.SlotPerDeviceSettingsJson = selected == null ? "[]" : null;
            return copy;
        }

        internal static IEnumerable<int> UnlistedCopySlots(IList<PadViewModel> pads, int targetSlot,
            IEnumerable<int> listedSlots)
        {
            var listed = new HashSet<int>(listedSlots);
            for (int i = 0; i < pads.Count && i < SettingsManager.SlotCreated.Length; i++)
                if (i != targetSlot && SettingsManager.SlotCreated[i] && !listed.Contains(i)
                    && !string.IsNullOrWhiteSpace(pads[i].Model3DAppearances))
                    yield return i;
        }

        /// <summary>An empty device list carries slot data without device tuning.
        /// Older payloads have no list and retain their single-device settings.</summary>
        internal static bool CarriesDeviceSettings(PadSetting copy)
        {
            if (copy == null) return false;
            if (string.IsNullOrEmpty(copy.SlotPerDeviceSettingsJson)) return true;
            try
            {
                var entries = System.Text.Json.JsonSerializer.Deserialize<PerDeviceSettingsEntry[]>(
                    copy.SlotPerDeviceSettingsJson);
                return entries == null || entries.Length > 0;
            }
            catch (System.Text.Json.JsonException) { return true; }
        }

        internal static DeviceSlotConfigData[] FilterClipboardDeviceConfigs(
            PadSetting copy, DeviceSlotConfigData[] configs)
        {
            if (configs == null || CarriesDeviceSettings(copy)) return configs;
            // Drop an unconfigured placeholder when no device settings are present.
            return configs.Where(config => config != null && (config.DeviceGuid != Guid.Empty
                || SettingsService.IsDeviceSlotConfigDataConfigured(config))).ToArray();
        }

        internal static void ApplyLegacyDeviceSetting(PadViewModel pad, PadSetting copy)
        {
            if (!string.IsNullOrWhiteSpace(copy?.Model3DAppearances))
                pad.Model3DAppearances = copy.Model3DAppearances;
        }

        internal static void ApplyClipboardSetting(PadViewModel pad, PadSetting copy)
            => pad.Model3DAppearances = copy.Model3DAppearances ?? "";
    }
}
