using System;
using System.ComponentModel;
using PadForge.Common.Input.Telemetry;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>One entry in the Game Telemetry page's game picker (issue
    /// #531): a game family, Auto-Detect, or a custom format by name.</summary>
    public sealed class GameTelemetryOption : INotifyPropertyChanged
    {
        public GameTelemetryOption(GameTelemetryFormat format, string customName)
        {
            Format = format;
            CustomName = format == GameTelemetryFormat.Custom ? customName ?? string.Empty : string.Empty;
        }

        public GameTelemetryFormat Format { get; }
        public string CustomName { get; }

        public string Display => DisplayFor(Format, CustomName);

        public bool Matches(GameTelemetryFormat format, string customName)
            => Format == format
               && (format != GameTelemetryFormat.Custom
                   || string.Equals(CustomName, customName, StringComparison.OrdinalIgnoreCase));

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Called on a language change.</summary>
        internal void RaiseDisplayChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));

        public override string ToString() => Display;

        /// <summary>The picker's words for a choice.</summary>
        internal static string DisplayFor(GameTelemetryFormat format, string customName)
        {
            var s = Strings.Instance;
            return format switch
            {
                GameTelemetryFormat.Auto => s.Telemetry_Game_Auto,
                GameTelemetryFormat.Forza => s.Telemetry_Game_Forza,
                GameTelemetryFormat.F1 => s.Telemetry_Game_F1,
                GameTelemetryFormat.Codemasters => s.Telemetry_Game_Codemasters,
                GameTelemetryFormat.EaSportsWrc => s.Telemetry_Game_EaSportsWrc,
                GameTelemetryFormat.OutGauge => s.Telemetry_Game_OutGauge,
                GameTelemetryFormat.ProjectCars2 => s.Telemetry_Game_ProjectCars2,
                GameTelemetryFormat.AssettoCorsa => s.Telemetry_Game_AssettoCorsa,
                GameTelemetryFormat.TruckSimulator => s.Telemetry_Game_TruckSimulator,
                _ => string.Format(s.Telemetry_Game_Custom_Format, customName),
            };
        }
    }
}
