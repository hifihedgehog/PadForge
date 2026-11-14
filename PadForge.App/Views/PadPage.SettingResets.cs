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
            if (sender is FrameworkElement { Tag: string field }) ResetMidiFields(field);
        }

        /// <summary>Reset All in the MIDI card's head: every setting the
        /// card shows. The port's row shows under the legacy MIDI API alone,
        /// and Reset All leaves a setting that does not show.</summary>
        private void ResetMidiAll_Click(object sender, RoutedEventArgs e)
        {
            bool port = DataContext is PadViewModel { IsLegacyMidi: true };
            ResetMidiFields(port ? MidiFields : Array.FindAll(MidiFields, f => f != nameof(MidiSlotConfig.OutputPort)));
        }

        /// <summary>The settings of a MIDI slot's card, by the config's
        /// property names.</summary>
        internal static readonly string[] MidiFields =
        {
            nameof(MidiSlotConfig.OutputPort), nameof(MidiSlotConfig.Channel), nameof(MidiSlotConfig.CcCount),
            nameof(MidiSlotConfig.StartCc), nameof(MidiSlotConfig.NoteCount), nameof(MidiSlotConfig.StartNote),
            nameof(MidiSlotConfig.Velocity),
        };

        /// <summary>Puts a MIDI setting back to its default. False for a
        /// name that is no setting of the card.</summary>
        internal static bool ResetMidiField(MidiSlotConfig config, string field)
        {
            switch (field)
            {
                case nameof(MidiSlotConfig.Channel): config.Channel = 1; break;
                case nameof(MidiSlotConfig.CcCount): config.CcCount = 6; break;
                case nameof(MidiSlotConfig.StartCc): config.StartCc = 1; break;
                case nameof(MidiSlotConfig.NoteCount): config.NoteCount = 11; break;
                case nameof(MidiSlotConfig.StartNote): config.StartNote = 60; break;
                case nameof(MidiSlotConfig.Velocity): config.Velocity = 127; break;
                case nameof(MidiSlotConfig.OutputPort): config.ChooseOutputPort(string.Empty); break;
                default: return false;
            }
            return true;
        }

        /// <summary>Puts the named MIDI settings back to their defaults, the
        /// two starts first: a count is held under 128 less its start, so a
        /// count reset against the old start stayed clamped (a start of 125
        /// left 3 controls, not 6). False when none of the names is a
        /// setting of the card.</summary>
        internal static bool ResetMidiFields(MidiSlotConfig config, params string[] fields)
        {
            bool any = false;
            foreach (string field in fields)
                if (field is nameof(MidiSlotConfig.StartCc) or nameof(MidiSlotConfig.StartNote))
                    any |= ResetMidiField(config, field);
            foreach (string field in fields)
                if (field is not (nameof(MidiSlotConfig.StartCc) or nameof(MidiSlotConfig.StartNote)))
                    any |= ResetMidiField(config, field);
            return any;
        }

        /// <summary>Resets the named MIDI settings of the slot. The rows are
        /// built again once when the runs of controls or notes changed, and
        /// the card's boxes are filled from the config.</summary>
        private void ResetMidiFields(params string[] fields)
        {
            if (DataContext is not PadViewModel vm || vm.MidiConfig == null) return;
            var config = vm.MidiConfig;
            var before = (config.CcCount, config.StartCc, config.NoteCount, config.StartNote);
            if (!ResetMidiFields(config, fields)) return;
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
