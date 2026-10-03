using System;
using System.Collections.Generic;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The Motion Pitch, Yaw and Roll rows' settings (#475), and the notes the
    /// Motion rows show when they hold a source they cannot read.
    /// </summary>
    public partial class MappingItem
    {
        /// <summary>True for the Motion Pitch, Yaw and Roll rows.</summary>
        public bool IsMotionAxisRow => MappingSetMigrator.IsMotionAxisTarget(TargetSettingName);

        /// <summary>Pitch and roll can lean. Yaw has no Angle response,
        /// because gravity carries no heading.</summary>
        public bool CanUseMotionAngle
            => TargetSettingName is MappingSetMigrator.MotionPitchTarget or MappingSetMigrator.MotionRollTarget;

        private string _motionResponse = "";
        /// <summary>"" for Speed, "Angle" for Angle. See
        /// <see cref="MappingRow.MotionResponse"/>.</summary>
        public string MotionResponse
        {
            get => _motionResponse;
            set
            {
                string v = string.Equals(value, MappingRow.MotionResponseAngle, StringComparison.Ordinal)
                    ? MappingRow.MotionResponseAngle : "";
                if (SetProperty(ref _motionResponse, v))
                {
                    OnPropertyChanged(nameof(IsMotionAngle));
                    OnPropertyChanged(nameof(ShowMotionSpeedSettings));
                    OnPropertyChanged(nameof(ShowMotionAngleSettings));
                    RaiseMotionRowNote();
                }
            }
        }

        /// <summary>True when the row leans instead of turning.</summary>
        public bool IsMotionAngle
            => CanUseMotionAngle && string.Equals(_motionResponse, MappingRow.MotionResponseAngle, StringComparison.Ordinal);

        public bool ShowMotionSpeedSettings => IsMotionAxisRow && !IsMotionAngle;
        public bool ShowMotionAngleSettings => IsMotionAxisRow && IsMotionAngle;

        private int _motionSpeed = MappingRow.DefaultMotionSpeed;
        /// <summary>Degrees per second at full deflection.</summary>
        public int MotionSpeed
        {
            get => _motionSpeed;
            set => SetProperty(ref _motionSpeed, Math.Clamp(value, 1, MappingRow.MaxMotionSpeed));
        }

        private int _motionMinSpeed;
        /// <summary>Degrees per second just past the deadzone.</summary>
        public int MotionMinSpeed
        {
            get => _motionMinSpeed;
            set => SetProperty(ref _motionMinSpeed, Math.Clamp(value, 0, MappingRow.MaxMotionSpeed));
        }

        private int _motionAngle = MappingRow.DefaultMotionAngle;
        /// <summary>Degrees of lean at full deflection.</summary>
        public int MotionAngle
        {
            get => _motionAngle;
            set => SetProperty(ref _motionAngle, Math.Clamp(value, 1, MappingRow.MaxMotionAngle));
        }

        private int _motionDeadzone = MappingRow.DefaultMotionDeadzone;
        /// <summary>Percent of full deflection read as rest.</summary>
        public int MotionDeadzone
        {
            get => _motionDeadzone;
            set => SetProperty(ref _motionDeadzone, Math.Clamp(value, 0, MappingRow.MaxMotionDeadzone));
        }

        private static CombineModeOption[] _motionResponseOptionsCache;
        private static int _motionResponseOptionsCulture;

        /// <summary>The Response dropdown's entries, culture-cached like the
        /// combine modes.</summary>
        public IReadOnlyList<CombineModeOption> MotionResponseOptions
        {
            get
            {
                int culture = System.Globalization.CultureInfo.CurrentUICulture.LCID;
                var cached = _motionResponseOptionsCache;
                if (cached != null && _motionResponseOptionsCulture == culture) return cached;
                var s = Strings.Instance;
                cached = new[]
                {
                    new CombineModeOption { Value = "", Name = s.Pad_Mapping_MotionSpeed_Name, Description = s.Pad_Mapping_MotionSpeed_Description },
                    new CombineModeOption { Value = MappingRow.MotionResponseAngle, Name = s.Pad_Mapping_MotionAngle_Name, Description = s.Pad_Mapping_MotionAngle_Description },
                };
                _motionResponseOptionsCache = cached;
                _motionResponseOptionsCulture = culture;
                return cached;
            }
        }

        /// <summary>Set by the grid builder on the Motion rows of a preset
        /// whose report carries no motion (DualShock 3, Switch 2 Pro, plain
        /// Steam Deck).</summary>
        public bool PresetCarriesNoMotion { get; init; }

        /// <summary>Set by the grid builder on the Motion rows of a preset
        /// whose report carries no pitch or roll rate (DualShock 3
        /// (SIXAXIS): Full). The note shows on Motion Pitch and Roll in
        /// Speed mode.</summary>
        public bool PresetCarriesNoPitchRollRate { get; init; }

        /// <summary>The note under a Motion row, or a row holding a Motion
        /// source, when something on it does not reach the game. Null when
        /// nothing needs saying.</summary>
        public string MotionRowNote
        {
            get
            {
                var s = Strings.Instance;
                bool passthrough = MappingSetMigrator.IsMotionTarget(TargetSettingName);
                if (passthrough)
                {
                    // The engine reads a Motion row source only when its
                    // descriptor carries the "Motion " prefix.
                    if (AnyReadSource(d => !SourceCoercion.IsMotionDescriptor(d)))
                        return s.Pad_Mapping_MotionRowUnreadNote;
                }
                else if (AnyReadSource(MappingSetMigrator.IsMotionVectorDescriptor))
                {
                    return s.Pad_Mapping_MotionSourceElsewhereNote;
                }
                if (PresetCarriesNoMotion && (passthrough || IsMotionAxisRow))
                    return s.Pad_Mapping_MotionPresetNote;
                if (PresetCarriesNoPitchRollRate && CanUseMotionAngle && !IsMotionAngle)
                    return s.Pad_Mapping_MotionPitchRollRateNote;
                return null;
            }
        }

        public bool ShowMotionRowNote => MotionRowNote != null;

        /// <summary>True when the primary or an extra source, other than an
        /// InvertOnHold modifier, carries a descriptor that matches.</summary>
        private bool AnyReadSource(Func<string, bool> match)
        {
            string primary = StripLegacyPrefix(_sourceDescriptor);
            if (!string.IsNullOrEmpty(primary) && match(primary)) return true;
            foreach (var msi in ExtraSources)
            {
                if (msi == null) continue;
                if (string.Equals(msi.Kind, "InvertOnHold", StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(msi.Descriptor) && match(msi.Descriptor)) return true;
            }
            return false;
        }

        private void RaiseMotionRowNote()
        {
            OnPropertyChanged(nameof(MotionRowNote));
            OnPropertyChanged(nameof(ShowMotionRowNote));
        }
    }
}
