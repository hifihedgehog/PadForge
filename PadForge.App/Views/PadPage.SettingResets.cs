using System;
using System.Linq;
using System.Windows;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class PadPage
    {
        private void ResetExtendedSetting_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PadViewModel vm || vm.ExtendedConfig == null
                || sender is not FrameworkElement { Tag: string field }) return;
            var config = vm.ExtendedConfig;
            _syncingExtendedConfig = true;
            try
            {
                switch (field)
                {
                    case nameof(ExtendedSlotConfig.Customize): config.Customize = false; break;
                    case nameof(ExtendedSlotConfig.OemNameOverride): config.OemNameOverride = false; break;
                    case nameof(ExtendedSlotConfig.ProductString): config.ProductString = string.Empty; break;
                    case nameof(ExtendedSlotConfig.VendorId): config.VendorId = 0; break;
                    case nameof(ExtendedSlotConfig.ProductId): config.ProductId = 0; break;
                    case nameof(ExtendedSlotConfig.ForceFeedbackEnabled): config.ForceFeedbackEnabled = true; break;
                    default:
                        if (!vm.AvailableProfiles.Any(p => string.Equals(p.Id, vm.ProfileId, StringComparison.OrdinalIgnoreCase))) break;
                        // Start with empty axis capacity so the default calculation
                        // cannot clamp a profile against an unrelated prior layout.
                        var defaults = new ExtendedSlotConfig { TriggerCount = 0, ThumbstickCount = 0 };
                        vm.SeedExtendedConfigFromProfile(defaults);
                        switch (field)
                        {
                            case nameof(ExtendedSlotConfig.ThumbstickCount): config.ThumbstickCount = defaults.ThumbstickCount; break;
                            case nameof(ExtendedSlotConfig.TriggerCount): config.TriggerCount = defaults.TriggerCount; break;
                            case nameof(ExtendedSlotConfig.PovCount): config.PovCount = defaults.PovCount; break;
                            case nameof(ExtendedSlotConfig.ButtonCount): config.ButtonCount = defaults.ButtonCount; break;
                        }
                        break;
                }
            }
            finally { _syncingExtendedConfig = false; }
            SyncExtendedFields(vm);
        }

        private void ResetMidiSetting_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PadViewModel vm || vm.MidiConfig == null
                || sender is not FrameworkElement { Tag: string field }) return;
            var config = vm.MidiConfig;
            var before = (config.CcCount, config.StartCc, config.NoteCount, config.StartNote);
            switch (field)
            {
                case nameof(MidiSlotConfig.Channel): config.Channel = 1; break;
                case nameof(MidiSlotConfig.CcCount): config.CcCount = 6; break;
                case nameof(MidiSlotConfig.StartCc): config.StartCc = 1; break;
                case nameof(MidiSlotConfig.NoteCount): config.NoteCount = 11; break;
                case nameof(MidiSlotConfig.StartNote): config.StartNote = 60; break;
                case nameof(MidiSlotConfig.Velocity): config.Velocity = 127; break;
                default: return;
            }
            if (before != (config.CcCount, config.StartCc, config.NoteCount, config.StartNote)) vm.RebuildMappings();
            SyncMidiConfigBar();
        }

        private void ResetAudioColor_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PadViewModel vm || vm.DeviceConfig == null
                || sender is not FrameworkElement { Tag: string field }) return;
            switch (field)
            {
                case "low": WriteAudioRgb(vm.DeviceConfig, field, 0, 255, 0); break;
                case "mid": WriteAudioRgb(vm.DeviceConfig, field, 255, 255, 0); break;
                case "high": WriteAudioRgb(vm.DeviceConfig, field, 255, 0, 0); break;
                default: return;
            }
            SyncAudioHexBoxes();
        }
    }
}
