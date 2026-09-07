using System;
using System.Collections.Generic;
using System.Globalization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>Projects Base mappings into the layerless PadSetting fields.</summary>
    internal static class LegacyBaseMappingProjection
    {
        internal static void Write(PadViewModel pad, Guid fallbackDevice)
        {
            if (pad == null || !pad.MappingsViewLoaded || InputService.VmMappingsStale
                || InputService.SuppressMappingEditPush) return;
            int slot = pad.PadIndex;
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || slot < 0 || slot >= sets.Length || !SettingsManager.SlotCreated[slot]) return;
            var rows = sets[slot]?.Rows;
            if (rows == null) return;

            var devices = new List<(Guid Guid, PadSetting Settings)>();
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                foreach (var entry in SettingsManager.UserSettings.Items)
                {
                    if (entry == null || entry.MapTo != slot) continue;
                    var settings = entry.GetPadSetting();
                    if (settings != null) devices.Add((entry.InstanceGuid, settings));
                }
            }
            if (devices.Count == 0) return;

            var baseRows = new Dictionary<string, MappingRow>(StringComparer.Ordinal);
            foreach (var row in rows)
                if (row != null && !string.IsNullOrEmpty(row.Target)
                    && string.Equals(row.LayerMask ?? "Base", "Base", StringComparison.Ordinal))
                    baseRows[row.Target] = row;

            var values = new Dictionary<PadSetting, Dictionary<string, string>>();
            var deadzones = new Dictionary<PadSetting, Dictionary<string, string>>();
            var bidirectional = new Dictionary<PadSetting, Dictionary<string, string>>();
            PadSetting fallback = null;
            foreach (var device in devices)
            {
                if (!values.ContainsKey(device.Settings))
                {
                    values[device.Settings] = new(StringComparer.Ordinal);
                    deadzones[device.Settings] = new(StringComparer.Ordinal);
                    bidirectional[device.Settings] = new(StringComparer.Ordinal);
                }
                if (device.Guid == fallbackDevice) fallback = device.Settings;
            }

            // The grid supplies target names only. Its current layer's values are not read.
            foreach (var target in pad.Mappings)
            {
                if (target == null || string.IsNullOrEmpty(target.TargetSettingName)
                    || !baseRows.TryGetValue(target.TargetSettingName, out var row)) continue;
                var sources = row.Sources;
                var primary = sources != null && sources.Count > 0 ? sources[0] : null;
                if (!IsDirect(primary)) continue;

                PadSetting owner = fallback;
                if (!string.IsNullOrEmpty(primary.DeviceGuid))
                {
                    owner = null;
                    if (Guid.TryParse(primary.DeviceGuid, out var ownerGuid))
                        foreach (var device in devices)
                            if (device.Guid == ownerGuid) { owner = device.Settings; break; }
                }
                if (owner == null) continue;

                string key = target.TargetSettingName;
                values[owner][key] = Encode(primary, primary.Invert);
                deadzones[owner][key] = primary.DeadZone > 0
                    ? primary.DeadZone.ToString(CultureInfo.InvariantCulture) : "";
                bidirectional[owner][key] = primary.Bidirectional ? "1" : "";

                // The legacy negative field reverses the extra source's stored polarity.
                if (target.NegSettingName != null && sources.Count > 1 && IsDirect(sources[1])
                    && !(row.CombineMode == "Custom" && row.SuppressBipolarPair)
                    && string.Equals(primary.DeviceGuid ?? "", sources[1].DeviceGuid ?? "", StringComparison.OrdinalIgnoreCase)
                    && primary.Invert != sources[1].Invert)
                    values[owner][target.NegSettingName] = Encode(sources[1], !sources[1].Invert);
            }

            foreach (var pair in values)
            {
                // Seed standard fields so a held Base binding is never blanked between writes.
                pair.Key.ClearMappingDescriptors(pair.Value);
                foreach (var value in pair.Value)
                    SettingsService.SetPadSettingProperty(pair.Key, value.Key, value.Value);
                foreach (var value in deadzones[pair.Key])
                    pair.Key.SetMappingDeadZone(value.Key, value.Value);
                foreach (var value in bidirectional[pair.Key])
                    pair.Key.SetMappingBidirectional(value.Key, value.Value);
            }
        }

        private static bool IsDirect(MappingSource source)
            => source != null && !string.IsNullOrEmpty(source.Descriptor)
                && string.Equals(source.Kind ?? "Direct", "Direct", StringComparison.Ordinal);

        private static string Encode(MappingSource source, bool invert)
            => PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(source.Descriptor)
                ? source.Descriptor
                : (invert ? "I" : "") + (source.HalfAxis ? "H" : "") + source.Descriptor;
    }
}
