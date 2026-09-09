using System;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;

namespace PadForge.ViewModels
{
    public partial class SettingsViewModel
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(SelectedThemeIndex) or
            nameof(SelectedLanguage) or
            nameof(AutoStartEngine) or
            nameof(MinimizeToTray) or
            nameof(StartMinimized) or
            nameof(StartAtLogin) or
            nameof(EnablePollingOnFocusLoss) or
            nameof(FlydigiEnhancedProtocol) or
            nameof(PollingRateMs) or
            nameof(HmInactivityDestroyTimeoutSeconds) or
            nameof(AssignOfferNewDevice) or
            nameof(AssignOfferEmptySlot) or
            nameof(HandheldButtonsEnabled) or
            nameof(BatteryNotifyEnabled) or
            nameof(BatteryNotifyThreshold) or
            nameof(BatteryNotifyVibrate) or
            nameof(EnableInputHiding) or
            nameof(KeepHidHideCloaksBetweenLaunches) or
            nameof(SteamVrInstallDir) or
            nameof(EnableCommunityConfigLookup) or
            nameof(ShowLegacyWorkshopConfigs) or
            nameof(DiagnosticsLoggingEnabled) or
            nameof(EnableAutoProfileSwitching) or
            nameof(EnableExternalControl) or
            nameof(IdentityProtectionModeIndex);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(SelectedThemeIndex):
                {
                    SelectedThemeIndex = 0;
                    break;
                }
                case nameof(SelectedLanguage):
                {
                    ResetLanguageToSystemDefault();
                    break;
                }
                case nameof(AutoStartEngine):
                {
                    AutoStartEngine = true;
                    break;
                }
                case nameof(MinimizeToTray):
                {
                    MinimizeToTray = false;
                    break;
                }
                case nameof(StartMinimized):
                {
                    StartMinimized = false;
                    break;
                }
                case nameof(StartAtLogin):
                {
                    StartAtLogin = false;
                    break;
                }
                case nameof(EnablePollingOnFocusLoss):
                {
                    EnablePollingOnFocusLoss = true;
                    break;
                }
                case nameof(FlydigiEnhancedProtocol):
                {
                    FlydigiEnhancedProtocol = true;
                    break;
                }
                case nameof(PollingRateMs):
                {
                    PollingRateMs = 1;
                    break;
                }
                case nameof(HmInactivityDestroyTimeoutSeconds):
                {
                    HmInactivityDestroyTimeoutSeconds = 60;
                    break;
                }
                case nameof(AssignOfferNewDevice):
                {
                    AssignOfferNewDevice = true;
                    break;
                }
                case nameof(AssignOfferEmptySlot):
                {
                    AssignOfferEmptySlot = true;
                    break;
                }
                case nameof(HandheldButtonsEnabled):
                {
                    HandheldButtonsEnabled = false;
                    break;
                }
                case nameof(BatteryNotifyEnabled):
                {
                    BatteryNotifyEnabled = true;
                    break;
                }
                case nameof(BatteryNotifyThreshold):
                {
                    BatteryNotifyThreshold = 15;
                    break;
                }
                case nameof(BatteryNotifyVibrate):
                {
                    BatteryNotifyVibrate = false;
                    break;
                }
                case nameof(EnableInputHiding):
                {
                    EnableInputHiding = true;
                    break;
                }
                case nameof(KeepHidHideCloaksBetweenLaunches):
                {
                    KeepHidHideCloaksBetweenLaunches = false;
                    break;
                }
                case nameof(SteamVrInstallDir):
                {
                    SteamVrInstallDir = PadForge.Common.DriverInstaller.SteamVrInstallDir;
                    break;
                }
                case nameof(EnableCommunityConfigLookup):
                {
                    EnableCommunityConfigLookup = false;
                    break;
                }
                case nameof(ShowLegacyWorkshopConfigs):
                {
                    ShowLegacyWorkshopConfigs = false;
                    break;
                }
                case nameof(DiagnosticsLoggingEnabled):
                {
                    DiagnosticsLoggingEnabled = false;
                    break;
                }
                case nameof(EnableAutoProfileSwitching):
                {
                    EnableAutoProfileSwitching = false;
                    break;
                }
                case nameof(EnableExternalControl):
                {
                    EnableExternalControl = false;
                    break;
                }
                case nameof(IdentityProtectionModeIndex):
                {
                    IdentityProtectionModeIndex = 0;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class DashboardViewModel
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(EnableDsuMotionServer) or
            nameof(EnableWebController) or
            nameof(EnableRemoteLink) or
            nameof(AutoReconnect) or
            nameof(EnableTouchpadOverlay) or
            nameof(EnableChromaLightbar) or
            nameof(EnableLightsyncLightbar) or
            nameof(EnableSensaHaptics) or
            nameof(EnableMenuOverlay) or
            nameof(EnableShiftLayerFlyout) or
            nameof(EnableProfileOverlay);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(EnableDsuMotionServer):
                {
                    EnableDsuMotionServer = false;
                    break;
                }
                case nameof(EnableWebController):
                {
                    EnableWebController = false;
                    break;
                }
                case nameof(EnableRemoteLink):
                {
                    EnableRemoteLink = false;
                    break;
                }
                case nameof(AutoReconnect):
                {
                    AutoReconnect = true;
                    break;
                }
                case nameof(EnableTouchpadOverlay):
                {
                    EnableTouchpadOverlay = false;
                    break;
                }
                case nameof(EnableChromaLightbar):
                {
                    EnableChromaLightbar = false;
                    break;
                }
                case nameof(EnableLightsyncLightbar):
                {
                    EnableLightsyncLightbar = false;
                    break;
                }
                case nameof(EnableSensaHaptics):
                {
                    EnableSensaHaptics = false;
                    break;
                }
                case nameof(EnableMenuOverlay):
                {
                    EnableMenuOverlay = true;
                    break;
                }
                case nameof(EnableShiftLayerFlyout):
                {
                    EnableShiftLayerFlyout = true;
                    break;
                }
                case nameof(EnableProfileOverlay):
                {
                    EnableProfileOverlay = true;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class DeviceRowViewModel
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(ForceRawJoystickMode) or
            nameof(HidHideEnabled) or
            nameof(ConsumeInputEnabled) or
            nameof(IdleDisconnectMinutes) or
            nameof(QuickChargeEnabled);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(ForceRawJoystickMode):
                {
                    ForceRawJoystickMode = false;
                    break;
                }
                case nameof(HidHideEnabled):
                {
                    HidHideEnabled = false;
                    break;
                }
                case nameof(ConsumeInputEnabled):
                {
                    ConsumeInputEnabled = false;
                    break;
                }
                case nameof(IdleDisconnectMinutes):
                {
                    IdleDisconnectMinutes = 0;
                    break;
                }
                case nameof(QuickChargeEnabled):
                {
                    QuickChargeEnabled = false;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class PadViewModel
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(ProfileId) or
            nameof(KbmSurfaces) or
            nameof(SteeringLockLightbarColorSource) or
            nameof(SteeringLockLightbarColor) or
            nameof(TriggerRumbleFold) or
            nameof(SwapMotors) or
            nameof(ConstantForceEnabled) or
            nameof(AudioRumbleEnabled) or
            nameof(ImpulseSwapTriggers) or
            nameof(AtVibrationToImpulse) or
            nameof(ConstantTriggerForceEnabled) or
            nameof(AudioRumbleTriggersEnabled) or
            nameof(MouseGestureButtonLeft) or
            nameof(MouseGestureButtonMiddle) or
            nameof(MouseGestureButtonRight) or
            nameof(MouseGestureButtonX1) or
            nameof(MouseGestureButtonX2) or
            nameof(MouseGestureButtonCustom);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(ProfileId):
                {
                    ProfileId = PadForge.Common.Input.InputManager.GetDefaultProfileId(OutputType);
                    break;
                }
                case nameof(KbmSurfaces):
                {
                    KbmSurfaces = KbmSlotConfig.DefaultSurfaces;
                    break;
                }
                case nameof(SteeringLockLightbarColorSource):
                {
                    SteeringLockLightbarColorSource = MacroLightbarColorSource.Fixed;
                    break;
                }
                case nameof(SteeringLockLightbarColor):
                {
                    SteeringLockLightbarColor = "#FF0000";
                    break;
                }
                case nameof(TriggerRumbleFold):
                {
                    TriggerRumbleFold = false;
                    break;
                }
                case nameof(SwapMotors):
                {
                    SwapMotors = false;
                    break;
                }
                case nameof(ConstantForceEnabled):
                {
                    ConstantForceEnabled = false;
                    break;
                }
                case nameof(AudioRumbleEnabled):
                {
                    AudioRumbleEnabled = false;
                    break;
                }
                case nameof(ImpulseSwapTriggers):
                {
                    ImpulseSwapTriggers = false;
                    break;
                }
                case nameof(AtVibrationToImpulse):
                {
                    AtVibrationToImpulse = false;
                    break;
                }
                case nameof(ConstantTriggerForceEnabled):
                {
                    ConstantTriggerForceEnabled = false;
                    break;
                }
                case nameof(AudioRumbleTriggersEnabled):
                {
                    AudioRumbleTriggersEnabled = false;
                    break;
                }
                case nameof(MouseGestureButtonLeft):
                {
                    MouseGestureButtonLeft = false;
                    break;
                }
                case nameof(MouseGestureButtonMiddle):
                {
                    MouseGestureButtonMiddle = false;
                    break;
                }
                case nameof(MouseGestureButtonRight):
                {
                    MouseGestureButtonRight = false;
                    break;
                }
                case nameof(MouseGestureButtonX1):
                {
                    MouseGestureButtonX1 = true;
                    break;
                }
                case nameof(MouseGestureButtonX2):
                {
                    MouseGestureButtonX2 = false;
                    break;
                }
                case nameof(MouseGestureButtonCustom):
                {
                    MouseGestureButtonCustom = false;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class DeviceSlotConfig
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(LeftStartPosition) or
            nameof(LeftEndPosition) or
            nameof(RightStartPosition) or
            nameof(RightEndPosition) or
            nameof(MicLedFollowDeviceId) or
            nameof(AudioPassthroughEnabled) or
            nameof(AudioEqEnabled) or
            nameof(AudioLimiterEnabled);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(LeftStartPosition):
                {
                    LeftStartPosition = 0;
                    break;
                }
                case nameof(LeftEndPosition):
                {
                    LeftEndPosition = 255;
                    break;
                }
                case nameof(RightStartPosition):
                {
                    RightStartPosition = 0;
                    break;
                }
                case nameof(RightEndPosition):
                {
                    RightEndPosition = 255;
                    break;
                }
                case nameof(MicLedFollowDeviceId):
                {
                    MicLedFollowDeviceId = string.Empty;
                    break;
                }
                case nameof(AudioPassthroughEnabled):
                {
                    AudioPassthroughEnabled = false;
                    break;
                }
                case nameof(AudioEqEnabled):
                {
                    AudioEqEnabled = false;
                    break;
                }
                case nameof(AudioLimiterEnabled):
                {
                    AudioLimiterEnabled = true;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class MacroItem
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(IsEnabled) or
            nameof(TriggerMode) or
            nameof(LayerMask) or
            nameof(TriggerExpression) or
            nameof(TriggerSource) or
            nameof(TriggerAxisThreshold) or
            nameof(TriggerAxisDirectionIndex) or
            nameof(ConsumeTriggerButtons) or
            nameof(RepeatMode) or
            nameof(RepeatCount);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(IsEnabled):
                {
                    IsEnabled = true;
                    break;
                }
                case nameof(TriggerMode):
                {
                    TriggerMode = MacroTriggerMode.OnPress;
                    break;
                }
                case nameof(LayerMask):
                {
                    LayerMask = "";
                    break;
                }
                case nameof(TriggerExpression):
                {
                    TriggerExpression = "";
                    break;
                }
                case nameof(TriggerSource):
                {
                    TriggerSource = MacroTriggerSource.InputDevice;
                    break;
                }
                case nameof(TriggerAxisThreshold):
                {
                    TriggerAxisThreshold = 50;
                    break;
                }
                case nameof(TriggerAxisDirectionIndex):
                {
                    TriggerAxisDirectionIndex = 0;
                    break;
                }
                case nameof(ConsumeTriggerButtons):
                {
                    ConsumeTriggerButtons = true;
                    break;
                }
                case nameof(RepeatMode):
                {
                    RepeatMode = MacroRepeatMode.Once;
                    break;
                }
                case nameof(RepeatCount):
                {
                    RepeatCount = 1;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class MacroExpressionVariable
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(Source) or
            nameof(OutputChannel);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(Source):
                {
                    Source = MacroTriggerSource.InputDevice;
                    break;
                }
                case nameof(OutputChannel):
                {
                    OutputChannel = MacroOutputChannel.None;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class MappingSourceItem
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(Kind) or
            nameof(Invert) or
            nameof(HalfAxis) or
            nameof(Bidirectional) or
            nameof(InvertOutput) or
            nameof(ParamRate) or
            nameof(ParamSticky) or
            nameof(ParamMin) or
            nameof(ParamMax) or
            nameof(ParamAttackTime) or
            nameof(ParamReleaseTime) or
            nameof(ParamReverseMultiplier) or
            nameof(ParamAutocenter) or
            nameof(ParamUpInputChoice) or
            nameof(ParamDownInputChoice) or
            nameof(ParamModifierInputChoice);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(Kind):
                {
                    Kind = "Direct";
                    break;
                }
                case nameof(Invert):
                {
                    Invert = false;
                    break;
                }
                case nameof(HalfAxis):
                {
                    HalfAxis = false;
                    break;
                }
                case nameof(Bidirectional):
                {
                    Bidirectional = false;
                    break;
                }
                case nameof(InvertOutput):
                {
                    InvertOutput = false;
                    break;
                }
                case nameof(ParamRate):
                {
                    ParamRate = 0.5;
                    break;
                }
                case nameof(ParamSticky):
                {
                    ParamSticky = true;
                    break;
                }
                case nameof(ParamMin):
                {
                    ParamMin = 0;
                    break;
                }
                case nameof(ParamMax):
                {
                    ParamMax = 1;
                    break;
                }
                case nameof(ParamAttackTime):
                {
                    ParamAttackTime = 0.30;
                    break;
                }
                case nameof(ParamReleaseTime):
                {
                    ParamReleaseTime = 0.30;
                    break;
                }
                case nameof(ParamReverseMultiplier):
                {
                    ParamReverseMultiplier = 4.0;
                    break;
                }
                case nameof(ParamAutocenter):
                {
                    ParamAutocenter = true;
                    break;
                }
                case nameof(ParamUpInputChoice):
                {
                    ParamUp = "";
                    break;
                }
                case nameof(ParamDownInputChoice):
                {
                    ParamDown = "";
                    break;
                }
                case nameof(ParamModifierInputChoice):
                {
                    ParamModifier = "";
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class MappingItem
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(CombineMode) or
            nameof(CombineExpression) or
            nameof(IsInverted) or
            nameof(IsHalfAxis) or
            nameof(IsBidirectional) or
            nameof(InvertOutput) or
            nameof(NoInherit);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(CombineMode):
                {
                    CombineMode = "";
                    break;
                }
                case nameof(CombineExpression):
                {
                    CombineExpression = "";
                    break;
                }
                case nameof(IsInverted):
                {
                    IsInverted = false;
                    break;
                }
                case nameof(IsHalfAxis):
                {
                    IsHalfAxis = false;
                    break;
                }
                case nameof(IsBidirectional):
                {
                    IsBidirectional = false;
                    break;
                }
                case nameof(InvertOutput):
                {
                    InvertOutput = false;
                    break;
                }
                case nameof(NoInherit):
                {
                    NoInherit = false;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class EqBandVm
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(Enabled) or
            nameof(Type) or
            nameof(FrequencyHz) or
            nameof(GainDb) or
            nameof(Q);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(Enabled):
                {
                    Enabled = true;
                    break;
                }
                case nameof(Type):
                {
                    Type = PadForge.Common.Input.EqBandType.Peaking;
                    break;
                }
                case nameof(FrequencyHz):
                {
                    FrequencyHz = 1000f;
                    break;
                }
                case nameof(GainDb):
                {
                    GainDb = 0;
                    break;
                }
                case nameof(Q):
                {
                    Q = 0.707f;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class MenuEditorItem
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(Enabled) or
            nameof(ShowLabels) or
            nameof(PosXPercent) or
            nameof(PosYPercent) or
            nameof(ScalePercent) or
            nameof(OpacityPercent) or
            nameof(CellCount) or
            nameof(HasCenter);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(Enabled):
                {
                    Enabled = true;
                    break;
                }
                case nameof(ShowLabels):
                {
                    ShowLabels = true;
                    break;
                }
                case nameof(PosXPercent):
                {
                    PosXPercent = 50;
                    break;
                }
                case nameof(PosYPercent):
                {
                    PosYPercent = 50;
                    break;
                }
                case nameof(ScalePercent):
                {
                    ScalePercent = 100;
                    break;
                }
                case nameof(OpacityPercent):
                {
                    OpacityPercent = 90;
                    break;
                }
                case nameof(CellCount):
                {
                    CellCount = 4;
                    break;
                }
                case nameof(HasCenter):
                {
                    HasCenter = false;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class MenuCellItem
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(IconScalePercent) or
            nameof(Label) or
            nameof(BindingKind) or
            nameof(SelectedKeyVk) or
            nameof(SelectedButtonFlag) or
            nameof(SelectedMacroName);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(IconScalePercent):
                {
                    IconScalePercent = 100;
                    break;
                }
                case nameof(Label):
                {
                    Label = "";
                    break;
                }
                case nameof(BindingKind):
                {
                    BindingKind = 0;
                    break;
                }
                case nameof(SelectedKeyVk):
                {
                    SelectedKeyVk = 0;
                    break;
                }
                case nameof(SelectedButtonFlag):
                {
                    SelectedButtonFlag = 0;
                    break;
                }
                case nameof(SelectedMacroName):
                {
                    SelectedMacroName = "";
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class PadViewModel
    {
        public partial class RumbleAudioVoiceItem
        {
            private RelayCommand<string> _resetSettingCommand;
            public RelayCommand<string> ResetSettingCommand =>
                _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

            internal static bool CanResetSetting(string name) => name is
                nameof(Enabled) or
                nameof(FrequencyHz) or
                nameof(GainPercent);

            private void ResetSetting(string name)
            {
                switch (name)
                {
                    case nameof(Enabled):
                    {
                        Enabled = true;
                        break;
                    }
                    case nameof(FrequencyHz):
                    {
                        FrequencyHz = PadForge.Engine.Data.RumbleAudioConfig.DefaultFrequencyHz[_index];
                        break;
                    }
                    case nameof(GainPercent):
                    {
                        GainPercent = 100;
                        break;
                    }
                    default: return;
                }
                // Refresh pending editor text even when the stored value was already the default.
                OnPropertyChanged(name);
            }
        }
    }

    public partial class ProfileShortcutViewModel
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(SwitchMode) or
            nameof(TargetProfileId) or
            nameof(TriggerDeviceGuid);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(SwitchMode):
                {
                    SwitchMode = PadForge.Services.SwitchProfileMode.Next;
                    break;
                }
                case nameof(TargetProfileId):
                {
                    TargetProfileId = "";
                    break;
                }
                case nameof(TriggerDeviceGuid):
                {
                    TriggerDeviceGuid = Guid.Empty;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class RemoteLinkTrustedPeer
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(Name) or
            nameof(AllowRemoteAssignments);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(Name):
                {
                    Name = string.Empty;
                    break;
                }
                case nameof(AllowRemoteAssignments):
                {
                    AllowRemoteAssignments = false;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class MacroItem
    {
        public partial class TriggerInputEntry
        {
            private RelayCommand<string> _resetSettingCommand;
            public RelayCommand<string> ResetSettingCommand =>
                _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

            internal static bool CanResetSetting(string name) => name is
                nameof(Invert) or
                nameof(HalfAxis) or
                nameof(Bidirectional);

            private void ResetSetting(string name)
            {
                switch (name)
                {
                    case nameof(Invert):
                    {
                        Invert = false;
                        break;
                    }
                    case nameof(HalfAxis):
                    {
                        HalfAxis = false;
                        break;
                    }
                    case nameof(Bidirectional):
                    {
                        Bidirectional = false;
                        break;
                    }
                    default: return;
                }
                // Refresh pending editor text even when the stored value was already the default.
                OnPropertyChanged(name);
            }
        }
    }

    public partial class TriggerConfigItem
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(DeadZone) or
            nameof(MaxRange);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(DeadZone):
                {
                    DeadZone = 0;
                    break;
                }
                case nameof(MaxRange):
                {
                    MaxRange = 100;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

    public partial class MacroAction
    {
        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(AxisSource) or
            nameof(AxisTarget) or
            nameof(AxisValue) or
            nameof(AxisValuePercent) or
            nameof(AxisYieldToPhysical) or
            nameof(CursorClampMode) or
            nameof(CursorPinMode) or
            nameof(CursorPinX) or
            nameof(CursorPinY) or
            nameof(CursorRecenterMode) or
            nameof(CycleStepsCsv) or
            nameof(CycleWrap) or
            nameof(DisconnectDeviceGuid) or
            nameof(DisconnectTarget) or
            nameof(DurationMs) or
            nameof(GuideLedPercent) or
            nameof(InvertAxis) or
            nameof(KeyString) or
            nameof(LightbarColorSource) or
            nameof(LightbarFadeMs) or
            nameof(LightbarHoldMode) or
            nameof(LightbarHoldMs) or
            nameof(LightbarTargetMode) or
            nameof(MouseButton) or
            nameof(MouseSensitivity) or
            nameof(MouseX) or
            nameof(MouseY) or
            nameof(NudgeDx) or
            nameof(NudgeDy) or
            nameof(PointerSetMode) or
            nameof(PressureScaledRate) or
            nameof(ProcessName) or
            nameof(ProgramArgs) or
            nameof(ProgramPath) or
            nameof(ProgramWorkingDir) or
            nameof(PulseWhileLatched) or
            nameof(RumbleFadeMs) or
            nameof(RumbleHoldMode) or
            nameof(RumbleHoldMs) or
            nameof(RumbleStrengthLeft) or
            nameof(RumbleStrengthRight) or
            nameof(SetGyroEngagedMode) or
            nameof(ShowVolumeOsd) or
            nameof(SoundFilePath) or
            nameof(SoundLoop) or
            nameof(SoundVolume) or
            nameof(SourceDeviceAxisIndex) or
            nameof(SourceDeviceGuid) or
            nameof(SwitchLayerMask) or
            nameof(TextContent) or
            nameof(TextPerCharDelayMs) or
            nameof(TurboRateCurve) or
            nameof(Type) or
            nameof(VolumeLimit) or
            nameof(WheelHorizontal);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(AxisSource):
                {
                    AxisSource = MacroAxisSource.OutputController;
                    break;
                }
                case nameof(AxisTarget):
                {
                    AxisTarget = MacroAxisTarget.None;
                    break;
                }
                case nameof(AxisValue):
                {
                    AxisValue = 0;
                    break;
                }
                case nameof(AxisValuePercent):
                {
                    AxisValuePercent = 0;
                    break;
                }
                case nameof(AxisYieldToPhysical):
                {
                    AxisYieldToPhysical = false;
                    break;
                }
                case nameof(CursorClampMode):
                {
                    CursorClampMode = CursorClampMode.XAndY;
                    break;
                }
                case nameof(CursorPinMode):
                {
                    CursorPinMode = CursorPinMode.XAndY;
                    break;
                }
                case nameof(CursorPinX):
                {
                    if (PadForge.Services.CursorControlService.TryGetPrimaryCenter(out int x, out int y)) CursorPinX = x;
                    break;
                }
                case nameof(CursorPinY):
                {
                    if (PadForge.Services.CursorControlService.TryGetPrimaryCenter(out int x, out int y)) CursorPinY = y;
                    break;
                }
                case nameof(CursorRecenterMode):
                {
                    CursorRecenterMode = CursorRecenterMode.XAndY;
                    break;
                }
                case nameof(CycleStepsCsv):
                {
                    CycleStepsCsv = "";
                    break;
                }
                case nameof(CycleWrap):
                {
                    CycleWrap = true;
                    break;
                }
                case nameof(DisconnectDeviceGuid):
                {
                    DisconnectDeviceGuid = Guid.Empty;
                    break;
                }
                case nameof(DisconnectTarget):
                {
                    DisconnectTarget = MacroDisconnectTarget.TriggeringDevice;
                    break;
                }
                case nameof(DurationMs):
                {
                    DurationMs = 50;
                    break;
                }
                case nameof(GuideLedPercent):
                {
                    GuideLedPercent = 100;
                    break;
                }
                case nameof(InvertAxis):
                {
                    InvertAxis = false;
                    break;
                }
                case nameof(KeyString):
                {
                    KeyString = "";
                    break;
                }
                case nameof(LightbarColorSource):
                {
                    LightbarColorSource = MacroLightbarColorSource.Fixed;
                    break;
                }
                case nameof(LightbarFadeMs):
                {
                    LightbarFadeMs = 600;
                    break;
                }
                case nameof(LightbarHoldMode):
                {
                    LightbarHoldMode = MacroLightbarHoldMode.Reactive;
                    break;
                }
                case nameof(LightbarHoldMs):
                {
                    LightbarHoldMs = 0;
                    break;
                }
                case nameof(LightbarTargetMode):
                {
                    LightbarTargetMode = LightbarMode.Static;
                    break;
                }
                case nameof(MouseButton):
                {
                    MouseButton = MacroMouseButton.Left;
                    break;
                }
                case nameof(MouseSensitivity):
                {
                    MouseSensitivity = 10f;
                    break;
                }
                case nameof(MouseX):
                {
                    if (PadForge.Services.CursorControlService.TryGetPrimaryCenter(out int x, out int y)) MouseX = x;
                    break;
                }
                case nameof(MouseY):
                {
                    if (PadForge.Services.CursorControlService.TryGetPrimaryCenter(out int x, out int y)) MouseY = y;
                    break;
                }
                case nameof(NudgeDx):
                {
                    NudgeDx = 0;
                    break;
                }
                case nameof(NudgeDy):
                {
                    NudgeDy = 0;
                    break;
                }
                case nameof(PointerSetMode):
                {
                    PointerSetMode = "Mouse";
                    break;
                }
                case nameof(PressureScaledRate):
                {
                    PressureScaledRate = false;
                    break;
                }
                case nameof(ProcessName):
                {
                    ProcessName = "";
                    break;
                }
                case nameof(ProgramArgs):
                {
                    ProgramArgs = "";
                    break;
                }
                case nameof(ProgramPath):
                {
                    ProgramPath = "";
                    break;
                }
                case nameof(ProgramWorkingDir):
                {
                    ProgramWorkingDir = "";
                    break;
                }
                case nameof(PulseWhileLatched):
                {
                    PulseWhileLatched = false;
                    break;
                }
                case nameof(RumbleFadeMs):
                {
                    RumbleFadeMs = 200;
                    break;
                }
                case nameof(RumbleHoldMode):
                {
                    RumbleHoldMode = MacroRumbleHoldMode.Reactive;
                    break;
                }
                case nameof(RumbleHoldMs):
                {
                    RumbleHoldMs = 100;
                    break;
                }
                case nameof(RumbleStrengthLeft):
                {
                    RumbleStrengthLeft = 100;
                    break;
                }
                case nameof(RumbleStrengthRight):
                {
                    RumbleStrengthRight = 100;
                    break;
                }
                case nameof(SetGyroEngagedMode):
                {
                    SetGyroEngagedMode = MacroSetGyroEngagedMode.Toggle;
                    break;
                }
                case nameof(ShowVolumeOsd):
                {
                    ShowVolumeOsd = true;
                    break;
                }
                case nameof(SoundFilePath):
                {
                    SoundFilePath = string.Empty;
                    break;
                }
                case nameof(SoundLoop):
                {
                    SoundLoop = false;
                    break;
                }
                case nameof(SoundVolume):
                {
                    SoundVolume = 100;
                    break;
                }
                case nameof(SourceDeviceAxisIndex):
                {
                    SourceDeviceAxisIndex = -1;
                    break;
                }
                case nameof(SourceDeviceGuid):
                {
                    SourceDeviceGuid = Guid.Empty;
                    break;
                }
                case nameof(SwitchLayerMask):
                {
                    SwitchLayerMask = "Base";
                    break;
                }
                case nameof(TextContent):
                {
                    TextContent = "";
                    break;
                }
                case nameof(TextPerCharDelayMs):
                {
                    TextPerCharDelayMs = 0;
                    break;
                }
                case nameof(TurboRateCurve):
                {
                    TurboRateCurve = "Linear";
                    break;
                }
                case nameof(Type):
                {
                    Type = MacroActionType.ButtonPress;
                    break;
                }
                case nameof(VolumeLimit):
                {
                    VolumeLimit = 100;
                    break;
                }
                case nameof(WheelHorizontal):
                {
                    WheelHorizontal = false;
                    break;
                }
                default: return;
            }
            // Refresh pending editor text even when the stored value was already the default.
            OnPropertyChanged(name);
        }
    }

}
