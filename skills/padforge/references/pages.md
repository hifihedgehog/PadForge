# PadForge's pages

Generated from PadForge's catalog: every setting, button, card, section, extra, dialog and menu item, by page, in English. `PadForge.exe --describe <folder>` writes this file and pages.json. Each place has its ID, which `find`, `read`, `set`, `press` and `show` take. A place on a controller's page takes `@c` and the controller's number after its ID, a card made per item `@i` and its position, and a place of the Devices page `@d` and the device's short ID. `find` gives the full ID of what shows now.

Each line: the words, then the kind (number, switch, choice, setting, action, card, section, part, extra, dialog or menu item) and the reach (free, asks with its reason, or yours-alone).

Live places have IDs of their own: `page/Home` and the other pages, `controller/<number>`, `device/<short ID>`, `profile/<ID>`, `macro/c<number>/<position>`, `plugin/<ID>` with what a plugin declares under `plugin/<ID>/<key>`, and Home's cards under `home/c<number>`. An open dialog's buttons are its ID, `#` and the button's, as `read` gives them.

## A controller's page

### Buttons (Keys & Mouse, Notes & Controls or Hands by the controller's type)

- Mode: choice, free. `pad/Pad_Row_Mode#row`
- Up Input: choice, free. `pad/ResetLabel_UpInput#row`
- Down Input: choice, free. `pad/ResetLabel_DownInput#row`
- Modifier Input: choice, free. `pad/ResetLabel_ModifierInput#row`
- Invert: switch, free. `pad/Pad_Invert#row`
- Half: switch, free. `pad/Pad_Half#row`
- Bidirectional: switch, free. `pad/Pad_Either#row`
- Distance: number, free. `pad/Pad_Mapping_RapidTrigger_Distance#row`
- Rate: number, free. `pad/Pad_Mapping_Rate#row`
- Sticky: switch, free. `pad/Pad_Mapping_Sticky#row`
- Min: number, free. `pad/Pad_Mapping_Min#row`
- Max: number, free. `pad/Pad_Mapping_Max#row`
- Attack: number, free. `pad/Pad_Mapping_Ramp_Attack#row`
- Release: number, free. `pad/Pad_Mapping_Ramp_Release#row`
- Reverse: number, free. `pad/Pad_Mapping_Ramp_Reverse#row`
- Autocenter: switch, free. `pad/Pad_Mapping_Ramp_Autocenter#row`
- Axis-to-Button Deadzone: number, free. `pad/Pad_HeaderDeadZone#row`
- Flip Output: switch, free. `pad/Pad_InvertOutput#row`
- Sensitivity: number, free. `pad/Mapping_GyroSensitivity#row`
- Sensitivity: number, free. `pad/Mapping_GyroSensitivity#row~2`
- Acceleration: number, free. `pad/Pad_Mapping_Accel#row`
- Sensitivity: number, free. `pad/Mapping_MouseCursorSensitivity#row`
- Sensitivity: number, free. `pad/Mapping_MouseSpeedSensitivity#row`
- Back to Center: number, free. `pad/Mapping_MouseBackToCenter#row`
- This axis moves relatively: switch, free. `pad/Mapping_AxisRelative#row`
- Travel to Full Lock: number, free. `pad/Mapping_MouseTravelCounts#row`
- Centering: choice, free. `pad/Mapping_MouseTravelCentering#row`
- Return Time: number, free. `pad/Mapping_MouseTravelReturnTime#row`
- Steer: choice, free. `pad/Mapping_MouseTravelSteer#row`
- Steer Input: choice, free. `pad/Mapping_MouseTravelSteerInput#row`
- Start Where Steering Began: switch, free. `pad/Mapping_MousePositionAnchor#row`
- Smoothing: number, free. `pad/Mapping_MouseTravelSmoothing#row`
- Lock at Speed: number, free. `pad/Mapping_MouseTravelLockAtSpeed#row`
- Sensitivity: number, free. `pad/Mapping_IrPointerSensitivity#row`
- Sensitivity: number, free. `pad/Mapping_IrPointerSensitivity#row~2`
- Combine: choice, free. `pad/Pad_Combine_Label#row`
- Do Not Inherit: switch, free. `pad/Pad_Shift_NoInherit#row`
- Trim Deadzone: number, free. `pad/Pad_Mapping_TrimDeadzone#row`
- Trim Speed: number, free. `pad/Pad_Mapping_TrimRate#row`
- Reset on Release: switch, free. `pad/Pad_Mapping_TrimReset#row`
- Motion Mode: choice, free. `pad/Pad_Mapping_MotionMode#row`
- Top Speed: number, free. `pad/Pad_Mapping_MotionTopSpeed#row`
- Start Speed: number, free. `pad/Pad_Mapping_MotionStartSpeed#row`
- Lean Angle: number, free. `pad/Pad_Mapping_MotionLeanAngle#row`
- Motion Deadzone: number, free. `pad/Pad_Mapping_MotionDeadzone#row`
- Formula: setting, free. `pad/ResetLabel_Formula#row`
- Picture: part, free. `pad.part/Pad_View_Picture`
- List: part, free. `pad.part/Pad_View_List`
- Configure Activator…: menu item, free. `pad.menuitem/ShiftLayerTabStrip/Pad_Shift_Configure`
- Rename Layer…: menu item, free. `pad.menuitem/ShiftLayerTabStrip/Pad_Shift_Rename`
- Copy Layer Rows: menu item, free. `pad.menuitem/ShiftLayerTabStrip/Pad_Shift_Copy`
- Paste Rows into Layer: menu item, free. `pad.menuitem/ShiftLayerTabStrip/Pad_Shift_Paste`
- Clear Layer Rows: menu item, free. `pad.menuitem/ShiftLayerTabStrip/Pad_Shift_Clear`
- Delete Layer: menu item, free. `pad.menuitem/ShiftLayerTabStrip/Pad_Shift_Delete`
- Add Layer: action, free. `pad.action/AddShiftLayerButton/Pad_Shift_AddLayer`
- Copy More: menu item, free. `pad.menuitem/ListMoreButton/Common_Copy#Common_More`
- Paste More: menu item, free. `pad.menuitem/ListMoreButton/Common_Paste#Common_More`
- Copy From… More: menu item, free. `pad.menuitem/ListMoreButton/Pad_CopyFrom#Common_More`
- Clear All More: menu item, free. `pad.menuitem/ListMoreButton/Pad_ClearAll#Common_More`
- Keyboard: choice, free. `pad/KbmDeviceBar/DeviceType_Keyboard`
- Mouse: choice, free. `pad/KbmDeviceBar/DeviceType_Mouse`
- Extended Controller: card, free. `pad.card/ExtendedConfigBar/Pad_ExtendedCard_Title`
- Controller Presets… (Extended Controller): action, free. `pad.action/ExtendedImportBtn/Pad_ExtendedImportFromDevice`
- Clone Device 1:1 (Extended Controller): action, free. `pad.action/ExtendedCloneBtn/Pad_ExtendedCloneDevice`
- Customize (Extended Controller): setting, free. `pad/ExtendedConfigBar/Pad_ExtendedCustomize`
- Product String (Extended Controller): setting, free. `pad/ExtendedCustomRows/Pad_ExtendedProductString`
- OEM Override (Extended Controller): setting, free. `pad/ExtendedCustomRows/Pad_ExtendedOemOverride`
- VID (Extended Controller): setting, free. `pad/ExtendedCustomRows/Pad_ExtendedVid`
- PID (Extended Controller): setting, free. `pad/ExtendedCustomRows/Pad_ExtendedPid`
- Sticks (Extended Controller): setting, free. `pad/ExtendedCustomRows/Pad_ExtendedSticks`
- Triggers (Extended Controller): setting, free. `pad/ExtendedCustomRows/Pad_ExtendedTriggers`
- POVs (Extended Controller): setting, free. `pad/ExtendedCustomRows/Pad_ExtendedPOVs`
- Buttons (Extended Controller): setting, free. `pad/ExtendedCustomRows/Pad_ExtendedButtons`
- Force Feedback (Extended Controller): switch, free. `pad/ExtendedCustomRows/Pad_ExtendedForceFeedback`
- MIDI Output: card, free. `pad.card/MidiConfigBar/Pad_MidiOutput_Title`
- Output Port (MIDI Output): choice, free. `pad/MidiConfigBar/Pad_MidiOutputPort`
- Channel (MIDI Output): setting, free. `pad/MidiConfigBar/Pad_MidiChannel`
- CC Count (MIDI Output): setting, free, under More. `pad/MidiMore/Pad_MidiCCCount`
- Start CC (MIDI Output): setting, free, under More. `pad/MidiMore/Pad_MidiStartCC`
- Note Count (MIDI Output): setting, free, under More. `pad/MidiMore/Pad_MidiNoteCount`
- Start Note (MIDI Output): setting, free, under More. `pad/MidiMore/Pad_MidiStartNote`
- Velocity (MIDI Output): setting, free, under More. `pad/MidiMore/Pad_MidiVelocity`
- SOCD: card, free. `pad.card/SocdFoldCard/Pad_Socd_FoldTitle`
- Mode (SOCD): choice, free. `pad/SocdFoldCard/Pad_Socd_Mode_Label`
- SOCD: card, free. `pad.card/KbmSocdFoldCard/Pad_Socd_FoldTitle`
- Mode (SOCD): choice, free. `pad/KbmSocdFoldCard/Pad_Socd_Mode_Label`
- Keep Controller Awake: card, free. `pad.card/KeepAwakeFoldCard/Pad_KeepAwake_Card_Header`
- Add Motion (Keep Controller Awake): switch, free. `pad/KeepAwakeFoldCard/Pad_KeepAwake_Motion`
- Held Axis (Keep Controller Awake): choice, free. `pad/KeepAwakeFoldCard/Pad_KeepAwake_Axis_Label`
- Deflection (Keep Controller Awake): number, free. `pad/KeepAwakeFoldCard/Pad_KeepAwake_Deflection_Label`

- Preset: choice, free. `pad/HMaestroProfileBar/Pad_Preset`
- Input Surfaces: choice, free. `pad/HMaestroProfileBar/ResetLabel_InputSurfaces`
- Copy More: menu item, free. `pad.menuitem/PadMoreButton/Common_Copy#Common_More`
- Paste More: menu item, free. `pad.menuitem/PadMoreButton/Common_Paste#Common_More`
- Copy From… More: menu item, free. `pad.menuitem/PadMoreButton/Pad_CopyFrom#Common_More`
- Clear All More: menu item, free. `pad.menuitem/PadMoreButton/Pad_ClearAll#Common_More`
- Delete virtual controller More: menu item, free. `pad.menuitem/PadMoreButton/Main_DeleteVC#Common_More`
- Add Device: action, free. `pad.action/AddDeviceButton/Pad_AddDevice`
- Assign: action, free. `pad.action/Pad_AssignOffer_Assign`
- Not Now: action, free. `pad.action/Pad_AssignOffer_Dismiss`

### Sticks & Triggers

- Sticks & Triggers: section, free. `pad.section/Pad_Section_Sticks`
- Deadzone: number, free. `pad/TabSticks/[StickConfigs]/Pad_Sticks_Section_DeadZone#=Title`
- Sensitivity: choice, free. `pad/TabSticks/[StickConfigs]/Macro_Sensitivity#=Title`
- Speed: number, free. `pad/TabSticks/[StickConfigs]/Pad_Stick_Speed#=Title`
- Calibrate Center: action, free. `pad.action/TabSticks/[StickConfigs]/Pad_CalibrateCenter#=Title`
- Center Offset X: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_CenterOffsetX#=Title`
- Center Offset Y: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_CenterOffsetY#=Title`
- Deadzone Shape: choice, free, under More. `pad/TabSticks/[StickConfigs]/Pad_DeadZoneShape#=Title`
- Deadzone X: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_DeadZoneX#=Title`
- Deadzone Y: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_DeadZoneY#=Title`
- Anti-Deadzone X: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_AntiDeadZoneX#=Title`
- Anti-Deadzone Y: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_AntiDeadZoneY#=Title`
- Linear: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_Linear#=Title`
- Momentum: switch, free, under More. `pad/TabSticks/[StickConfigs]/Pad_StickMomentum#=Title`
- Momentum Glide: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_Touchpad_MouseMomentumDecay#=Title`
- Min Range X (Left): number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_MinRangeXLeft#=Title`
- Max Range X (Right): number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_MaxRangeXRight#=Title`
- Min Range Y (Down): number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_MinRangeYDown#=Title`
- Max Range Y (Up): number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_MaxRangeYUp#=Title`
- Steering Mode: choice, free, under More. `pad/TabSticks/[StickConfigs]/Pad_Steering_Mode#=Title`
- Wind Range: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_Steering_WindRange#=Title`
- Wind Power: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_Steering_WindPower#=Title`
- Unwind Rate: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_Steering_WindUnwindRate#=Title`
- Stick Deadzone: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_Steering_StickDeadZone#=Title`
- Angle Inner Deadzone: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_Steering_AngleInnerDz#=Title`
- Angle Outer Deadzone: number, free, under More. `pad/TabSticks/[StickConfigs]/Pad_Steering_AngleOuterDz#=Title`
- Sensitivity X: choice, free, under More. `pad/TabSticks/[StickConfigs]/Pad_SensitivityX#=Title`
- Sensitivity Y: choice, free, under More. `pad/TabSticks/[StickConfigs]/Pad_SensitivityY#=Title`
- Both Sticks: card, free. `pad.card/BothSticksCard/Pad_BothSticks_Header`
- Steering Lock Feedback: card, free. `pad.card/LockFeedbackCard/Pad_Steering_LockFeedback`
- Rumble Pulse on Lock (Steering Lock Feedback): switch, free. `pad/LockRumbleRow/Pad_Steering_Lock_Rumble`
- Trigger Vibration on Lock (Steering Lock Feedback): switch, free. `pad/LockTriggerVibRow/Pad_Steering_Lock_TriggerVib`
- Lightbar Pulse on Lock (Steering Lock Feedback): switch, free. `pad/LockLightbarRow/Pad_Steering_Lock_Lightbar`
- Trigger Resistance Near Lock (Steering Lock Feedback): switch, free. `pad/LockResistanceRow/Pad_Steering_Lock_ATResistance`
- Pulse Length (Steering Lock Feedback): number, free. `pad/LockPulseSection/Pad_Steering_Lock_PulseMs`
- Color (Steering Lock Feedback): choice, free. `pad/LockLightbarSection/Macro_LightbarColorSource_Label`
- Hex (Steering Lock Feedback): setting, free. `pad/LockLightbarSection/Pad_Lighting_Hex`
- Hold Time (Steering Lock Feedback): number, free. `pad/LockLightbarSection/Macro_TriggerHoldMs_Label`
- Decay (Steering Lock Feedback): number, free. `pad/LockLightbarSection/Macro_LightbarDecay_Label`
- Flick Stick: card, free. `pad.card/FlickStickCard/Pad_FlickStick_Header`
- Dots per 360° (Flick Stick): number, free. `pad/FlickStickCard/Settings_FlickCountsPer360`
- Flick Time (Flick Stick): number, free. `pad/FlickStickCard/Settings_FlickTime`
- Flick Threshold (Flick Stick): number, free. `pad/FlickStickCard/Settings_FlickThreshold`
- Snap Angle (Flick Stick): choice, free. `pad/FlickStickCard/Settings_FlickSnapMode`
- Snap Strength (Flick Stick): number, free. `pad/FlickStickCard/Settings_FlickSnapStrength`
- Front Angle Deadzone (Flick Stick): number, free. `pad/FlickStickCard/Settings_FlickForwardDeadzone`
- Sweep Smoothing (Flick Stick): number, free. `pad/FlickStickCard/Settings_FlickSmoothing`
- Rotation Offset (Flick Stick): number, free. `pad/FlickStickCard/Settings_FlickRotationOffset`
- Allow Flick on Engage (Flick Stick): switch, free. `pad/FlickStickCard/Settings_FlickOnEngage`
- Range: setting, free. `pad/TabTriggers/[TriggerConfigs]/Pad_Range#=Title`
- Anti-Deadzone: number, free. `pad/TabTriggers/[TriggerConfigs]/Pad_AntiDeadZone#=Title`
- Sensitivity: choice, free. `pad/TabTriggers/[TriggerConfigs]/Macro_Sensitivity#=Title`
- Mouse Gestures: card, free. `pad.card/TabMouse/Pad_MouseGestures_Header`
- Enable Mouse Gestures (Mouse Gestures): switch, free. `pad/TabMouse/Pad_MouseGestures_Enabled`
- Gesture Buttons (Mouse Gestures): setting, free. `pad/TabMouse/Pad_MouseGestures_Button`
- Custom Activation Input (Mouse Gestures): setting, free. `pad/TabMouse/Pad_MouseGestures_CustomInput`
- Flick Distance (counts) (Mouse Gestures): number, free. `pad/TabMouse/Pad_MouseGestures_FlickThreshold`
- Cooldown (Mouse Gestures): number, free. `pad/TabMouse/Pad_MouseGestures_Cooldown`

### Rumble & Haptics

- Rumble & Haptics: section, free. `pad.section/Pad_Section_Rumble`
- Novint Falcon: card, free. `pad.card/FalconCard/Pad_Falcon_Header`
- Feel (Novint Falcon): choice, free. `pad/FalconCard/Pad_Falcon_Feel`
- Centering Spring (Novint Falcon): number, free. `pad/FalconCard/Pad_Falcon_Spring`
- Force Limit (Novint Falcon): number, free. `pad/FalconCard/Pad_Falcon_ForceCap`
- Kick Button (Novint Falcon): choice, free, under More. `pad/FalconMore/Pad_Falcon_KickButton`
- Kick Strength (Novint Falcon): number, free, under More. `pad/FalconMore/Pad_Falcon_Kick`
- Shake Strength (Novint Falcon): number, free, under More. `pad/FalconMore/Pad_Falcon_Shake`
- Brace Button (Novint Falcon): choice, free, under More. `pad/FalconMore/Pad_Falcon_BraceButton`
- Preload Button (Novint Falcon): choice, free, under More. `pad/FalconMore/Pad_Falcon_PreloadButton`
- Gravity Compensation (Novint Falcon): number, free, under More. `pad/FalconMore/Pad_Falcon_Gravity`
- Force Feedback: card, free. `pad.card/TabForceFeedback/Pad_ForceFeedback_Header`
- Overall Gain (Force Feedback): number, free. `pad/TabForceFeedback/Pad_OverallGain`
- Test Rumble (Force Feedback): action, free. `pad.action/TabForceFeedback/Pad_TestRumble`
- Left Motor (Force Feedback): number, free, under More. `pad/ForceFeedbackMore/Pad_LeftMotorLabel`
- Test Left Motor (Force Feedback): action, free, under More. `pad.action/TestLeftMotorButton/Pad_RumbleAudio_Test#Pad_LeftMotorLabel`
- Right Motor (Force Feedback): number, free, under More. `pad/ForceFeedbackMore/Pad_RightMotorLabel`
- Test Right Motor (Force Feedback): action, free, under More. `pad.action/TestRightMotorButton/Pad_RumbleAudio_Test#Pad_RightMotorLabel`
- Fold Trigger Rumble into Main Motors (Force Feedback): switch, free, under More. `pad/ForceFeedbackMore/Pad_TriggerRumbleFold`
- Swap Left and Right Motors (Force Feedback): switch, free, under More. `pad/FfbSwapMotorsRow/Pad_SwapMotors`
- Enable Steering Angle Rumble (Force Feedback): switch, free, under More. `pad/SteeringAngleRumbleEnabledReset/Pad_SteeringAngleRumble_Enable`
- Virtual Axis (Force Feedback): choice, free, under More. `pad/SteeringAngleRumbleAxisReset/Pad_SteeringAngleRumble_Axis`
- Motor (Force Feedback): choice, free, under More. `pad/SteeringAngleRumbleRows/Pad_SteeringAngleRumble_Motor`
- Strength (Force Feedback): number, free, under More. `pad/SteeringAngleRumbleStrengthReset/Pad_AT_Strength`
- Center Deadzone (Force Feedback): number, free, under More. `pad/SteeringAngleRumbleDeadzoneReset/Pad_SteeringAngleRumble_Deadzone`
- Apply Constant Force (Force Feedback): switch, free, under More. `pad/ConstantForceRows/Pad_ConstantForce_Toggle`
- X (Force Feedback): number, free, under More. `pad/ConstantForceRows/Pad_ConstantForce_X`
- Y (Force Feedback): number, free, under More. `pad/ConstantForceRows/Pad_ConstantForce_Y`
- Audio Rumble: card, free. `pad.card/AudioRumbleFoldCard/Settings_AudioRumble`
- Sensitivity (Audio Rumble): number, free. `pad/AudioRumbleFoldCard/Settings_AudioRumble_Sensitivity`
- Bass Cutoff (Hz) (Audio Rumble): number, free. `pad/AudioRumbleFoldCard/Settings_AudioRumble_Cutoff`
- Left Motor (Audio Rumble): number, free. `pad/AudioRumbleFoldCard/Settings_AudioRumble_LeftMotor`
- Right Motor (Audio Rumble): number, free. `pad/AudioRumbleFoldCard/Settings_AudioRumble_RightMotor`
- Trigger Routing: card, free. `pad.card/TriggerRoutingFoldCard/Pad_TriggerRouting_Header`
- Source Left Trigger (Trigger Routing): choice, free. `pad/TriggerRoutingFoldCard/Pad_TriggerRouting_Source#Pad_TriggerRouting_LeftTrigger`
- Mode Left Trigger (Trigger Routing): choice, free. `pad/TriggerRoutingFoldCard/Pad_TriggerRouting_Mode#Pad_TriggerRouting_LeftTrigger`
- Scale Left Trigger (Trigger Routing): number, free. `pad/TriggerRoutingFoldCard/Pad_TriggerRouting_Scale#Pad_TriggerRouting_LeftTrigger`
- Activator Left Trigger (Trigger Routing): setting, free. `pad/TriggerRoutingFoldCard/Pad_TriggerRouting_Activator#Pad_TriggerRouting_LeftTrigger`
- Activator Mode Left Trigger (Trigger Routing): choice, free. `pad/TriggerRoutingFoldCard/Pad_TriggerRouting_ActivatorMode#Pad_TriggerRouting_LeftTrigger`
- Source Right Trigger (Trigger Routing): choice, free. `pad/TriggerRoutingFoldCard/Pad_TriggerRouting_Source#Pad_TriggerRouting_RightTrigger`
- Mode Right Trigger (Trigger Routing): choice, free. `pad/TriggerRoutingFoldCard/Pad_TriggerRouting_Mode#Pad_TriggerRouting_RightTrigger`
- Scale Right Trigger (Trigger Routing): number, free. `pad/TriggerRoutingFoldCard/Pad_TriggerRouting_Scale#Pad_TriggerRouting_RightTrigger`
- Activator Right Trigger (Trigger Routing): setting, free. `pad/TriggerRoutingFoldCard/Pad_TriggerRouting_Activator#Pad_TriggerRouting_RightTrigger`
- Activator Mode Right Trigger (Trigger Routing): choice, free. `pad/TriggerRoutingFoldCard/Pad_TriggerRouting_ActivatorMode#Pad_TriggerRouting_RightTrigger`
- Impulse Triggers: card, free. `pad.card/TabImpulseTriggers/Pad_ImpulseTriggers_Header`
- Overall Gain (Impulse Triggers): number, free. `pad/TabImpulseTriggers/Pad_OverallGain`
- Left Trigger (Impulse Triggers): number, free, under More. `pad/ImpulseTriggersMore/Pad_ImpulseTriggers_LeftTrigger`
- Test Left Trigger (Impulse Triggers): action, free, under More. `pad.action/ImpulseTriggersMore/Pad_RumbleAudio_Test#Pad_ImpulseTriggers_LeftTrigger`
- Right Trigger (Impulse Triggers): number, free, under More. `pad/ImpulseTriggersMore/Pad_ImpulseTriggers_RightTrigger`
- Test Right Trigger (Impulse Triggers): action, free, under More. `pad.action/ImpulseTriggersMore/Pad_RumbleAudio_Test#Pad_ImpulseTriggers_RightTrigger`
- Swap Left and Right Trigger Motors (Impulse Triggers): switch, free, under More. `pad/ImpulseTriggersMore/Pad_ImpulseTriggers_SwapTriggers`
- Translate Adaptive Trigger Vibrations (Impulse Triggers): switch, free, under More. `pad/AtTranslateRow/Pad_ImpulseTriggers_AtTranslate`
- Apply Constant Trigger Force (Impulse Triggers): switch, free, under More. `pad/ConstantTriggerForceRows/Pad_ConstantTriggerForce_Toggle`
- Left Trigger (Impulse Triggers): number, free, under More. `pad/ConstantTriggerForceRows/Pad_ConstantTriggerForce_Left`
- Right Trigger (Impulse Triggers): number, free, under More. `pad/ConstantTriggerForceRows/Pad_ConstantTriggerForce_Right`
- Audio Bass Trigger Rumble: card, free. `pad.card/AudioTriggerRumbleFoldCard/Pad_AudioTriggerRumble_Header`
- Sensitivity (Audio Bass Trigger Rumble): number, free. `pad/AudioTriggerRumbleFoldCard/Pad_AudioTriggerRumble_Sensitivity`
- Bass Cutoff (Hz) (Audio Bass Trigger Rumble): number, free. `pad/AudioTriggerRumbleFoldCard/Pad_AudioTriggerRumble_Cutoff`
- Left Trigger (Audio Bass Trigger Rumble): number, free. `pad/AudioTriggerRumbleFoldCard/Pad_AudioTriggerRumble_LeftTrigger`
- Right Trigger (Audio Bass Trigger Rumble): number, free. `pad/AudioTriggerRumbleFoldCard/Pad_AudioTriggerRumble_RightTrigger`
- Adaptive Triggers: card, free. `pad.card/TabAdaptiveTriggers/Pad_AdaptiveTriggers_Header`
- Left Trigger (Adaptive Triggers): choice, free. `pad/TabAdaptiveTriggers/Pad_LeftTrigger`
- Range (Adaptive Triggers): number, free. `pad/TabAdaptiveTriggers/Pad_AT_Range`
- Strength (Adaptive Triggers): number, free. `pad/TabAdaptiveTriggers/Pad_AT_Strength`
- Frequency (Hz) (Adaptive Triggers): number, free. `pad/TabAdaptiveTriggers/Pad_AT_Frequency`
- Right Trigger (Adaptive Triggers): choice, free. `pad/TabAdaptiveTriggers/Pad_RightTrigger`
- Range (Adaptive Triggers): number, free. `pad/TabAdaptiveTriggers/Pad_AT_Range~2`
- Strength (Adaptive Triggers): number, free. `pad/TabAdaptiveTriggers/Pad_AT_Strength~2`
- Frequency (Hz) (Adaptive Triggers): number, free. `pad/TabAdaptiveTriggers/Pad_AT_Frequency~2`
- Wheel: card, free. `pad.card/WheelCard/Pad_Wheel`
- Set Rotation Range (Wheel): switch, free. `pad/WheelCard/Pad_Wheel_SetRotationRange`
- Rotation Range (Wheel): number, free. `pad/WheelCard/Pad_Wheel_RotationRange`
- RPM Shift LEDs (Wheel): switch, free. `pad/WheelCard/Pad_Wheel_RpmLeds`
- Centering: card, free. `pad.card/CenteringCard/Pad_Centering`
- Mode (Centering): choice, free. `pad/CenteringCard/Pad_Centering_Mode`
- Auto Centering Strength (Centering): number, free. `pad/CenteringCard/Pad_Wheel_Autocenter`
- Vehicle (Centering): choice, free. `pad/CenteringCard/Pad_Centering_Vehicle`
- Speed (Centering): choice, free. `pad/CenteringCard/Pad_Centering_Speed`
- Steering Weight (Centering): number, free. `pad/CenteringCard/Pad_Centering_Weight`
- Wheel Range (Centering): number, free. `pad/CenteringCard/Pad_Centering_WheelRange`
- On-Center Feel (Centering): number, free, under More. `pad/CenteringMore/Pad_Centering_OnCenterFeel`
- Power Assist (Centering): number, free, under More. `pad/CenteringMore/Pad_Centering_Assist`
- Damping (Centering): number, free, under More. `pad/CenteringMore/Pad_Centering_Damping`
- Friction (Centering): number, free, under More. `pad/CenteringMore/Pad_Centering_Friction`
- Grip (Centering): choice, free, under More. `pad/CenteringMore/Pad_Centering_Grip`
- Fixed Speed (Centering): number, free, under More. `pad/CenteringMore/Pad_Centering_FixedSpeed`
- Wheel Torque (Centering): number, free, under More. `pad/CenteringMore/Pad_Centering_WheelTorque`
- Bass Shakers: card, free. `pad.card/TabBassShakers/Pad_RumbleAudio_Header`
- Route Rumble to an Audio Output (Bass Shakers): switch, free. `pad/TabBassShakers/Pad_RumbleAudio_Enable`
- Output Device (Bass Shakers): choice, free, under More. `pad/BassShakersMore/Pad_RumbleAudio_OutputDevice`
- Channel Mode (Bass Shakers): choice, free, under More. `pad/BassShakersMore/Pad_RumbleAudio_ChannelMode`
- Master Gain (Bass Shakers): number, free, under More. `pad/BassShakersMore/Pad_RumbleAudio_MasterGain`
- Frequency Sweep Check the Shakers (Bass Shakers): action, free, under More. `pad.action/BassShakersMore/Pad_RumbleAudio_Sweep#Pad_RumbleAudio_CheckShakers`
- Stop Check the Shakers (Bass Shakers): action, free, under More. `pad.action/BassShakersMore/Pad_RumbleAudio_StopTest#Pad_RumbleAudio_CheckShakers`

### Motion & Touch

- Motion & Touch: section, free. `pad.section/Pad_Section_Motion`
- Gyro: card, free. `pad.card/GyroCard/Pad_Gyro_Header`
- Motion Passthrough (Gyro): setting, free. `pad/GyroPassthroughCard/Pad_Gyro_Passthrough_Header`
- Calibrate Gyro (Gyro): action, free. `pad.action/GyroCalibrationCard/Settings_GyroCalibrate_Button`
- Horizontal Sensitivity (Gyro): number, free. `pad/GyroSensitivityCard/Settings_GyroSensitivityH`
- Vertical Sensitivity (Gyro): number, free. `pad/GyroSensitivityCard/Settings_GyroSensitivityV`
- Units (Gyro): choice, free, under More. `pad/GyroSensitivityMore/Settings_GyroSensitivityUnits`
- Invert Pitch (Y) (Gyro): switch, free, under More. `pad/GyroSensitivityMore/Settings_GyroInvertPitch`
- Invert Yaw (X) (Gyro): switch, free, under More. `pad/GyroSensitivityMore/Settings_GyroInvertYaw`
- Invert Roll (X) (Gyro): switch, free, under More. `pad/GyroSensitivityMore/Settings_GyroInvertRoll`
- Real-World Calibration (Gyro): number, free, under More. `pad/GyroSensitivityMore/Settings_GyroRealWorldCalibration`
- Grip: card, free. `pad.card/GyroGripCard/Pad_Gyro_Grip_Header`
- Held As (Grip): choice, free. `pad/GyroGripCard/Settings_MotionGrip`
- Engage: card, free. `pad.card/GyroEngageCard/Pad_Gyro_Engage_Header`
- Easy Aim Threshold (Engage): number, free. `pad/GyroEngageCard/Settings_GyroEasyAimThreshold`
- Engage Stick (Engage): choice, free. `pad/GyroEngageCard/Settings_GyroEngageStickSide`
- Engage Direction (Engage): choice, free. `pad/GyroEngageCard/Settings_GyroEngageStickDirection`
- Aim Engage Button (Engage): setting, free. `pad/GyroEngageCard/Settings_GyroAimEngageButton`
- Engage Mode (Engage): choice, free. `pad/GyroEngageCard/Settings_GyroAimEngageMode`
- Gyro On (Engage): choice, free. `pad/GyroEngageCard/Pad_GyroActivation`
- Gyro On Buttons (Engage): setting, free. `pad/GyroEngageCard/Pad_GyroActivation_Buttons`
- Needs (Engage): choice, free. `pad/GyroEngageCard/Pad_GyroActivation_Needs`
- Re-Center When Gyro Turns On (Engage): switch, free. `pad/GyroEngageCard/Pad_GyroRecenterOnEngage`
- Response Shaping: card, free. `pad.card/GyroResponseCard/Pad_Gyro_Response_Header`
- Deadzone (Response Shaping): number, free. `pad/GyroResponseCard/Settings_GyroDeadZone`
- Tightening (Response Shaping): number, free. `pad/GyroResponseCard/Settings_GyroTightening`
- Smoothing Threshold (Response Shaping): number, free. `pad/GyroResponseCard/Settings_GyroSmoothingThreshold`
- Smoothing Window (Response Shaping): number, free. `pad/GyroResponseCard/Settings_GyroSmoothingWindow`
- Acceleration (Response Shaping): number, free. `pad/GyroResponseCard/Settings_GyroAcceleration`
- Output Curve (Response Shaping): choice, free. `pad/GyroResponseCard/Settings_GyroOutputCurve`
- Reference Frame: card, free. `pad.card/GyroSpaceCard/Pad_Gyro_Space_Header`
- Space (Reference Frame): choice, free. `pad/GyroSpaceCard/Settings_GyroSpace`
- Motion Steering: card, free. `pad.card/MotionSteeringFoldCard/Pad_MotionSteer_Header`
- Lean Inner Deadzone (Motion Steering): number, free. `pad/MotionSteeringFoldCard/Pad_Steering_MotionInnerDz`
- Lean Outer Deadzone (Motion Steering): number, free. `pad/MotionSteeringFoldCard/Pad_Steering_MotionOuterDz`
- Controller Orientation (Motion Steering): choice, free. `pad/MotionSteeringFoldCard/Pad_Steering_Orientation`
- Gyro Tilt: card, free. `pad.card/GyroTiltFoldCard/Pad_GyroTilt_Header`
- Full Tilt Range (Gyro Tilt): number, free. `pad/GyroTiltFoldCard/Pad_GyroTilt_Range`
- Tilt Deadzone (Gyro Tilt): number, free. `pad/GyroTiltFoldCard/Pad_GyroTilt_Deadzone`
- Pitch and Roll Simulation: card, free. `pad.card/GyroSimulationCard/Pad_Gyro_Simulation_Header`
- Simulation Smoothing (Pitch and Roll Simulation): number, free. `pad/GyroSimulationCard/Settings_GyroSimulationSmoothing`
- Compass: card, free. `pad.card/CompassYawCard/Pad_Gyro_Compass_Header`
- Calibrate Magnetometer (Compass): action, free. `pad.action/CompassYawCard/Pad_Gyro_MagCalibrate`
- Touchpad: card, free. `pad.card/TouchpadCard/Pad_Touchpad_Header`
- Mouse Sensitivity X (Touchpad): number, free. `pad/TouchpadCard/Pad_Touchpad_MouseSensitivityX`
- Mouse Sensitivity Y (Touchpad): number, free. `pad/TouchpadCard/Pad_Touchpad_MouseSensitivityY`
- Jitter Reduction (Touchpad): switch, free. `pad/TouchpadCard/Pad_Touchpad_MouseJitterReduction`
- Invert Mouse X (Touchpad): switch, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_MouseInvertX`
- Invert Mouse Y (Touchpad): switch, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_MouseInvertY`
- Momentum (Touchpad): switch, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_MouseMomentum`
- Response (Touchpad): choice, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_PointerResponse`
- Speed Threshold (Touchpad): number, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_TrackpadThreshold`
- Pad Width (Touchpad): number, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_TrackpadPadWidth`
- Acceleration (Touchpad): number, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_MouseAcceleration`
- Momentum Glide (Touchpad): number, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_MouseMomentumDecay`
- Fling Boost (Touchpad): number, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_MomentumFlingGain`
- Fling Threshold (Touchpad): number, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_MomentumMinLift`
- Max Fling Speed (Touchpad): number, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_MomentumMaxSpeed`
- Stack Flings (Touchpad): switch, free, under More. `pad/TouchpadMouseMore/Pad_Touchpad_MomentumStacking`
- Stick / D-Pad Output: card, free. `pad.card/TouchpadStickFoldCard/Pad_Touchpad_Stick_Header`
- Max Radius (Stick / D-Pad Output): number, free. `pad/TouchpadStickFoldCard/Pad_Touchpad_StickMaxRadius`
- Inner Deadzone (Stick / D-Pad Output): number, free. `pad/TouchpadStickFoldCard/Pad_Touchpad_StickInnerDeadzone`
- D-Pad Mode (Stick / D-Pad Output): choice, free. `pad/TouchpadStickFoldCard/Pad_Touchpad_StickDPadMode_Label`
- D-Pad Activation D-Pad Mode (Stick / D-Pad Output): number, free. `pad/TouchpadStickFoldCard/Pad_Touchpad_StickDPadActivationThreshold#Pad_Touchpad_StickDPadMode_Label`
- Absolute Pointer: card, free. `pad.card/TouchpadPointerFoldCard/Pad_Touchpad_Pointer_Header`
- Region Width (Absolute Pointer): number, free. `pad/TouchpadPointerFoldCard/Pad_Touchpad_PointerRegionSizeX`
- Region Height (Absolute Pointer): number, free. `pad/TouchpadPointerFoldCard/Pad_Touchpad_PointerRegionSizeY`
- Region Center X (Absolute Pointer): number, free. `pad/TouchpadPointerFoldCard/Pad_Touchpad_PointerRegionCenterX`
- Region Center Y (Absolute Pointer): number, free. `pad/TouchpadPointerFoldCard/Pad_Touchpad_PointerRegionCenterY`
- Synthetic Pressure: card, free. `pad.card/SyntheticPressureFoldCard/Pad_Touchpad_SyntheticPressure_Header`
- Touch Pressure Level (Synthetic Pressure): number, free. `pad/SyntheticPressureFoldCard/Pad_Touchpad_SyntheticTouchLevel`
- Swipe Haptics: card, free. `pad.card/SwipeHapticsFoldCard/Pad_Touchpad_SwipeHaptics_Header`
- Pulse Intensity (Swipe Haptics): number, free. `pad/SwipeHapticsFoldCard/Pad_Touchpad_SwipeHapticsIntensity`
- Gesture Detection: card, free. `pad.card/GestureDetectionFoldCard/Pad_Touchpad_Detection_Header`
- Recognize (Gesture Detection): choice, free. `pad/GestureDetectionFoldCard/Pad_Touchpad_Mode_Label`
- Cooldown (Gesture Detection): number, free. `pad/GestureDetectionFoldCard/Pad_Touchpad_CooldownMs_Label`
- In-Box Gestures: card, free. `pad.card/InBoxGesturesFoldCard/Pad_Touchpad_InBox_Header`
- Touch Spots (Left / Right / Top / Multitouch) (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_TouchSpots`
- 4-Way Swipes (Up / Down / Left / Right) (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_FourWaySwipes`
- 8-Way Swipes (Adds Diagonals) (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_EightWaySwipes`
- Swipe Distance (0..1) (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_SwipeDistanceThreshold`
- Swipe Time Window (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_SwipeTimeWindow`
- Radial Zones (Weapon-Wheel Style) (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_RadialZones`
- Zone Count (In-Box Gestures): choice, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_RadialZoneCount`
- Center Deadzone (0..1) (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_RadialCenterDeadzone`
- Tap / Double Tap / Triple Tap (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_Taps`
- Tap Time Window (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_TapTimeWindow`
- Multi-Tap Gap (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_MultiTapGap`
- Tap Max Travel (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_TapMaxMotion`
- Long Press (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_LongPress`
- Long-Press Duration (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_LongPressTimeWindow`
- Long Press Max Travel (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_LongPressMaxMotion`
- Two-Finger Swipes (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_TwoFingerSwipes`
- Two Finger Angle (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_TwoFingerAngle`
- Pinch / Spread (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_PinchSpread`
- Pinch Threshold (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_PinchThreshold`
- Rotate (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_Rotate`
- Rotate Threshold (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_RotateThreshold`
- Three-Finger Gestures (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_ThreeFinger`
- Four-Finger Gestures (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_FourFinger`
- Five-Finger Tap (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_FiveFinger`
- Shape Gestures (Circle, Square, Triangle, Z, Checkmark) (In-Box Gestures): switch, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_ShapeGestures`
- Shape Match Threshold (In-Box Gestures): number, free. `pad/InBoxGesturesFoldCard/Pad_Touchpad_MatchThreshold`
- Custom Gestures: card, free. `pad.card/CustomGesturesFoldCard/Pad_Touchpad_Custom_Header`
- + Record New Gesture (Custom Gestures): action, free. `pad.action/CustomGesturesFoldCard/Pad_Touchpad_RecordNew`
- IR Pointer: card, free. `pad.card/IrPointerCard/Pad_Pointer_Header`
- Mode (IR Pointer): choice, free. `pad/IrPointerCard/Pad_TriggerMode`
- FPS Speed Mode (IR Pointer): setting, free. `pad/IrPointerCard/Pad_Pointer_FpsSpeed#Pad_TriggerMode`
- Sensor Bar Position (IR Pointer): choice, free, under More. `pad/IrPointerMore/Pad_Pointer_SensorBar`
- Vertical Offset (IR Pointer): setting, free, under More. `pad/IrPointerMore/Pad_Pointer_Compensation`
- Smoothing (IR Pointer): setting, free, under More. `pad/IrPointerMore/Pad_Pointer_Smoothing`

### Lights & Sound

- Lights & Sound: section, free. `pad.section/Pad_Section_Lights`
- Lighting: card, free. `pad.card/LightingCard/Pad_Lighting_Header`
- Mode (Lighting): choice, free. `pad/LightbarModeCard/Pad_Lighting_Mode`
- Period (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_Period`
- Brightness (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_RainbowBrightness`
- Display (Lighting): choice, free. `pad/LightbarModeCard/Pad_Lighting_ScreenDisplay`
- Screen Area (Lighting): choice, free. `pad/LightbarModeCard/Pad_Lighting_ScreenArea`
- Left Edge (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_ScreenLeft`
- Top Edge (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_ScreenTop`
- Width (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_ScreenWidth`
- Height (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_ScreenHeight`
- Color (Lighting): choice, free. `pad/LightbarModeCard/Pad_Lighting_ScreenMethod`
- Saturation (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_ScreenSaturation`
- Brightness (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_RainbowBrightness~2`
- Smoothing (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_ScreenSmoothing`
- Spread the Screen Across This Device’s Lights (Lighting): switch, free. `pad/LightbarModeCard/Pad_Lighting_ScreenSpread`
- Blend Smoothly Between Palette Colors (Lighting): switch, free. `pad/LightbarModeCard/Pad_Lighting_ColorCycleSmooth`
- Sensitivity (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_AudioLightbar_Sensitivity`
- Low-to-Mid Boundary (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_AudioLightbar_LowToMid`
- Mid-to-High Boundary (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_AudioLightbar_MidToHigh`
- Crossfade Width (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_AudioLightbar_CrossFadeWidth`
- Low Color (Lighting): setting, free. `pad/LightbarModeCard/ResetLabel_LowColor`
- Mid Color (Lighting): setting, free. `pad/LightbarModeCard/ResetLabel_MidColor`
- High Color (Lighting): setting, free. `pad/LightbarModeCard/ResetLabel_HighColor`
- Input Reactive (Lighting): choice, free. `pad/LightbarModeCard/Pad_Lighting_InputReactive`
- Pulse Hold (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_InputHold`
- Pulse Decay (Lighting): number, free. `pad/LightbarModeCard/Pad_Lighting_InputDecay`
- Indicator LEDs: card, free. `pad.card/IndicatorLedsCard/Pad_Lighting_IndicatorLeds`
- LED Brightness (Indicator LEDs): choice, free. `pad/IndicatorLedsCard/Pad_Lighting_LedBrightness`
- Player Pattern (Indicator LEDs): choice, free. `pad/IndicatorLedsCard/Pad_Lighting_PlayerPattern`
- Mute LED Mode (Indicator LEDs): choice, free. `pad/IndicatorLedsCard/Pad_Lighting_MuteLedMode`
- Audio Device (Indicator LEDs): choice, free. `pad/IndicatorLedsCard/Pad_Lighting_MicLed_FollowDevice_Picker`
- Lighting Control: card, free. `pad.card/PeripheralLightingCard/Pad_Lighting_ControlCard`
- Lighting Hold (Lighting Control): choice, free. `pad/PeripheralClaimLevelRow/Pad_Lighting_ClaimLevel`
- Guide Button LED: card, free. `pad.card/GuideLedCard/Pad_Lighting_GuideLed`
- Mode (Guide Button LED): choice, free. `pad/GuideLedCard/Pad_Lighting_Mode`
- Brightness (Guide Button LED): number, free. `pad/GuideLedCard/Pad_Lighting_GuideLed_Brightness`
- LED Strips: card, free. `pad.card/LedStripsCard/Pad_Lighting_LedStrips`
- All Keys: action, free. `pad.action/KeyColorsCard/Pad_Lighting_KeyColors_SelectAllKeys`
- Number Row: action, free. `pad.action/KeyColorsCard/Pad_Lighting_KeyColors_SelectNumberRow`
- F Keys: action, free. `pad.action/KeyColorsCard/Pad_Lighting_KeyColors_SelectFunctionKeys`
- Number Pad: action, free. `pad.action/KeyColorsCard/Pad_Lighting_KeyColors_SelectNumberPad`
- Arrow Keys: action, free. `pad.action/KeyColorsCard/Pad_Lighting_KeyColors_SelectArrows`
- WASD: action, free. `pad.action/KeyColorsCard/Pad_Lighting_KeyColors_SelectWasd`
- Edge: action, free. `pad.action/KeyColorsCard/Pad_Lighting_KeyColors_Edge`
- Logo: action, free. `pad.action/KeyColorsCard/Pad_Lighting_KeyColors_Logo`
- All Lights: action, free. `pad.action/KeyColorsCard/Pad_Lighting_KeyColors_SelectAllLights`
- Clear Selection: action, free. `pad.action/KeyColorsCard/Pad_Lighting_KeyColors_ClearSelection`
- Audio: card, free. `pad.card/AudioCard/Pad_Audio_Header`
- Master Volume (Audio): number, free. `pad/AudioCard/Pad_Audio_MasterVolume`
- Mirror System Audio to the Controller Speaker (Audio): switch, free. `pad/AudioCard/Pad_Audio_Mirror`
- Mirror Source (Audio): choice, free, under More. `pad/SoundOutputMore/Pad_Audio_MirrorSource_Label`
- Play Mirrored Audio (Audio): choice, free, under More. `pad/SoundOutputMore/Pad_Audio_Engage_Label`
- Engage Input (Audio): setting, free, under More. `pad/SoundOutputMore/Pad_Audio_EngageInput_Label`
- Release Delay (Audio): number, free, under More. `pad/SoundOutputMore/Pad_Audio_EngageRelease_Label`
- High Tones (Audio): choice, free, under More. `pad/SoundOutputMore/Pad_Audio_ToneFilter_Label`
- Tone Limit (Audio): number, free, under More. `pad/SoundOutputMore/Pad_Audio_ToneLimit_Label`
- Play DualSense Haptics on This Controller (Audio): switch, free, under More. `pad/SoundOutputMore/Pad_Audio_PersonaHaptics_Label`
- Haptics Gain (Audio): number, free, under More. `pad/SoundOutputMore/Pad_Audio_PersonaHapticsGain_Label`
- Actuator Low-Pass Cutoff (Audio): number, free, under More. `pad/SoundOutputMore/Pad_Audio_TritonLowPass_Label`
- Headphone Volume (Audio): number, free, under More. `pad/SoundOutputMore/Pad_Audio_HeadphoneVolume`
- Audio Buffer Length (Audio): number, free, under More. `pad/SoundOutputMore/Pad_Audio_BufferLength`
- Output Path (Audio): choice, free, under More. `pad/SoundOutputMore/Pad_Audio_OutputPath`
- Test (Audio): action, free, under More. `pad.action/SoundOutputMore/Pad_Audio_Test`
- Stop All Sounds (Audio): action, free, under More. `pad.action/SoundOutputMore/Pad_Audio_StopAll`
- Crossfeed: card, free. `pad.card/CrossfeedFoldCard/Pad_Audio_Crossfeed`
- Crossfeed (Crossfeed): choice, free. `pad/CrossfeedFoldCard/Pad_Audio_Crossfeed`
- Cutoff (Crossfeed): number, free. `pad/CrossfeedFoldCard/Pad_Audio_CrossfeedCut`
- Feed (Crossfeed): number, free. `pad/CrossfeedFoldCard/Pad_Audio_CrossfeedFeed`
- Parametric EQ: card, free. `pad.card/ParametricEqFoldCard/Pad_Audio_Eq`
- Preamp (Parametric EQ): number, free. `pad/ParametricEqFoldCard/Pad_Audio_EqPreamp`
- Add Band (Parametric EQ): action, free. `pad.action/ParametricEqFoldCard/Pad_Audio_EqAdd`
- Import from File… (Parametric EQ): action, free. `pad.action/ParametricEqFoldCard/Pad_Audio_EqImportFile`
- Import from Clipboard (Parametric EQ): action, free. `pad.action/ParametricEqFoldCard/Pad_Audio_EqImport`
- Clear (Parametric EQ): action, free. `pad.action/ParametricEqFoldCard/Pad_Audio_EqClear`
- Limiter: card, free. `pad.card/LimiterFoldCard/Pad_Audio_Limiter`
- Ceiling (Limiter): number, free. `pad/LimiterFoldCard/Pad_Audio_LimiterCeiling`
- Sound Macros: card, free. `pad.card/SoundMacrosFoldCard/Pad_Audio_Macros_Header`
- New Sound Macro (Sound Macros): action, free. `pad.action/SoundMacrosFoldCard/Pad_Audio_NewSoundMacro`
- Sound Packages: card, free. `pad.card/SoundPackagesFoldCard/Pad_Audio_Packages_Header`
- Add Package… (Sound Packages): action, free. `pad.action/SoundPackagesFoldCard/Pad_Audio_Packages_Add`
- Create Package… (Sound Packages): action, free. `pad.action/SoundPackagesFoldCard/Pad_Audio_Packages_Create`

### Macros & Menus

- Macros & Menus: section, free. `pad.section/Pad_Section_Macros`
- Copy More: menu item, asks (SendsInput). `pad.menuitem/Common_Copy#Common_More`
- Paste More: menu item, asks (SendsInput). `pad.menuitem/Common_Paste#Common_More`
- Copy From… More: menu item, asks (SendsInput). `pad.menuitem/Pad_CopyFrom#Common_More`
- Macros: part, free. `pad.part/Pad_Macros`
- Menus: part, free. `pad.part/Pad_Menus`
- Duplicate More: menu item, asks (SendsInput). `pad.menuitem/Common_Duplicate#Common_More`
- Copy More: menu item, asks (SendsInput). `pad.menuitem/Common_Copy#Common_More~2`
- Clear Trigger More: menu item, asks (SendsInput). `pad.menuitem/Macro_ClearTrigger#Common_More`
- Remove More: menu item, asks (SendsInput). `pad.menuitem/Common_Remove#Common_More`
- Duplicate More: menu item, asks (SendsInput). `pad.menuitem/Common_Duplicate#Common_More~2`
- Remove Last Cell More: menu item, asks (SendsInput). `pad.menuitem/Menu_RemoveLastCell#Common_More`
- Remove More: menu item, asks (SendsInput). `pad.menuitem/Common_Remove#Common_More~2`

## Settings

- Language: card, free. `settings.card/GeneralGroup/Settings_Language`
- Language (Language): choice, free. `settings/GeneralGroup/Settings_Language`
- Appearance: card, free. `settings.card/GeneralGroup/Settings_Appearance`
- New Theme… (Appearance): action, free. `settings.action/GeneralGroup/Settings_ThemeNew`
- Add Theme… (Appearance): action, free. `settings.action/GeneralGroup/Settings_ThemeAdd`
- Save a Copy… (Appearance): action, free. `settings.action/GeneralGroup/Settings_ThemeSaveCopy`
- Theme (Appearance): setting, free. `settings/GeneralGroup/Settings_Theme`
- Window: card, free. `settings.card/GeneralGroup/Settings_Window`
- Minimize to System Tray (Window): switch, free. `settings/GeneralGroup/Settings_MinimizeToTray`
- Close to System Tray (Window): switch, free. `settings/CloseToTrayReset/Settings_CloseToTray`
- Always Show in System Tray (Window): switch, free. `settings/GeneralGroup/Settings_AlwaysShowTrayIcon`
- Start Minimized (Window): switch, free. `settings/GeneralGroup/Settings_StartMinimized`
- Start at Login (Window): switch, free. `settings/GeneralGroup/Settings_StartAtLogin`
- Move PadForge to Its Own Folder (Window): action, free. `settings.action/GeneralGroup/ExeFolder_Move`
- Updates: card, free. `settings.card/GeneralGroup/Settings_Updates`
- Check Now (Updates): action, free. `settings.action/UpdateStatusItem/Settings_CheckNow`
- Release Notes (Updates): action, free. `settings.action/UpdateStatusItem/Settings_ReleaseNotes`
- Install and Restart (Updates): action, free. `settings.action/UpdateStatusItem/Settings_InstallAndRestart`
- Check for Updates Automatically (Updates): switch, free. `settings/GeneralGroup/Settings_CheckForUpdates`
- Install Updates Automatically (Updates): switch, free. `settings/GeneralGroup/Settings_InstallUpdatesAutomatically`
- Include Pre-Releases (Updates): switch, free. `settings/GeneralGroup/Settings_IncludePreReleases`
- Update Now Controller Database (Updates): action, asks. `settings.action/GeneralGroup/Settings_ControllerDatabaseUpdateNow#Settings_ControllerDatabase`
- Controller Database (Updates): setting, free. `settings/GeneralGroup/Settings_ControllerDatabase`
- Add Rows From a File or the Clipboard… Your Rows (Updates): action, free, under More. `settings.action/ControllerDatabaseMore/ControllerDatabase_AddRows#ControllerDatabase_YourRows`
- Input Engine: card, free. `settings.card/InputGroup/Settings_InputEngine`
- Auto-Start Engine on Launch (Input Engine): switch, free. `settings/InputGroup/Settings_AutoStart`
- Continue Polling When Window Loses Focus (Input Engine): switch, free. `settings/InputGroup/Settings_BackgroundPolling`
- Polling Rate (Unless a Profile Overrides It) (Input Engine): number, free. `settings/InputGroup/Settings_PollingRate`
- Inactivity Timeout (Input Engine): number, free. `settings/InputGroup/Settings_HmInactivityTimeout`
- Assignment Prompts: card, free. `settings.card/InputGroup/Settings_AssignOffer`
- Offer New Devices to the Open Virtual Controller (Assignment Prompts): switch, free. `settings/InputGroup/Settings_AssignOfferNew`
- Offer Any Connecting Device When the Open Virtual Controller Has No Devices (Assignment Prompts): switch, free. `settings/InputGroup/Settings_AssignOfferEmpty`
- Offer New Devices on Home (Assignment Prompts): switch, free. `settings/InputGroup/Settings_AssignOfferHome`
- Battery Alerts: card, free. `settings.card/InputGroup/Settings_BatteryNotify`
- Notify When Battery Runs Low (Battery Alerts): switch, free. `settings/InputGroup/Settings_BatteryNotifyEnable`
- Notify at or Below (Battery Alerts): number, free. `settings/InputGroup/Settings_BatteryNotifyThreshold`
- Test Notification Check the Notice (Battery Alerts): action, free. `settings.action/InputGroup/Settings_BatteryNotifyTest#Settings_BatteryNotifyTestLabel`
- Also Vibrate the Controller (Battery Alerts): switch, free, under More. `settings/BatteryMore/Settings_BatteryNotifyVibrate`
- Repeat the Notice Every (Battery Alerts): number, free, under More. `settings/BatteryMore/Settings_BatteryNotifyRepeat`
- Notify When Fully Charged (Battery Alerts): switch, free, under More. `settings/BatteryMore/Settings_BatteryNotifyFull`
- Hardware Support: card, free. `settings.card/HardwareGroup/Settings_HardwareSupport`
- Flydigi Enhanced Protocol (Hardware Support): switch, free. `settings/HardwareGroup/Settings_FlydigiEnhanced`
- Read Logitech G-Keys (Hardware Support): switch, free. `settings/HardwareGroup/Settings_GKeys`
- Read Analog Keyboards (Hardware Support): switch, free. `settings/HardwareGroup/Settings_AnalogKeyboards`
- Read Bliss-Box Adapters (Hardware Support): switch, free. `settings/HardwareGroup/Settings_BlissBox`
- Enable Handheld PC Buttons (Hardware Support): switch, free. `settings/HardwareGroup/Settings_HandheldEnable`
- Drive Keyboard Screens (Hardware Support): switch, free. `settings/HardwareGroup/Settings_KeyboardScreens`
- Download Strike7API.dll (Hardware Support): action, free. `settings.action/HardwareGroup/KeyboardScreens_DownloadStrike7`
- Choose Strike7API.dll… (Hardware Support): action, free. `settings.action/HardwareGroup/KeyboardScreens_PickStrike7`
- Install Visual C++ 2008 Runtime (Hardware Support): action, free. `settings.action/HardwareGroup/KeyboardScreens_InstallVc2008`
- Drive Novint Falcon (Hardware Support): switch, free. `settings/HardwareGroup/Settings_Falcon`
- Choose dhd64.dll… (Hardware Support): action, free. `settings.action/HardwareGroup/Falcon_PickLibrary`
- Install Falcon Driver (Hardware Support): action, free. `settings.action/HardwareGroup/Falcon_InstallDriver`
- Get Force Dimension’s SDK (Hardware Support): action, free. `settings.action/HardwareGroup/Falcon_OpenDownload`
- Install Location: setting, asks (Drivers). `settings/DriversGroup/Settings_SteamVRInstallDir`
- HidHide Options: card, asks (Drivers). `settings.card/HidHideOptions/Settings_HidHideOptions`
- Hide Devices from Games (HidHide Options): switch, asks (Drivers). `settings/HidHideOptions/Settings_HideDevices`
- Keep Devices Cloaked Between Launches (HidHide Options): switch, asks (Drivers). `settings/HidHideOptions/Settings_KeepCloaksBetweenLaunches`
- Add… (HidHide Options): action, asks (Drivers). `settings.action/WhitelistAdd/Settings_AddDots`
- Community Configs: card, free. `settings.card/DataGroup/Settings_CommunityConfigs`
- Enable Community Configs (Community Configs): switch, free. `settings/DataGroup/Settings_EnableCommunityConfigs`
- Show Legacy Workshop Configs (Community Configs): switch, free. `settings/DataGroup/Settings_ShowLegacyConfigs`
- Clear Cached Configs (Community Configs): action, free. `settings.action/DataGroup/Settings_ClearWorkshopCache`
- Check Imported Profiles for Updates (Community Configs): action, free. `settings.action/DataGroup/Settings_CheckWorkshopUpdates`
- Settings File: card, free. `settings.card/DataGroup/Settings_SettingsFile`
- Reload (Settings File): action, free. `settings.action/DataGroup/Settings_Reload`
- Open Folder (Settings File): action, asks (OpensPlace). `settings.action/DataGroup/Settings_OpenFolder`
- Reset to Defaults (Settings File): action, free. `settings.action/DataGroup/Settings_ResetToDefaults`
- Diagnostics: card, free. `settings.card/DataGroup/Settings_Diagnostics`
- Start Recording (Diagnostics): action, free. `settings.action/RecordProblemButton/Settings_RecordProblem_Start`
- Save a Snapshot… (Diagnostics): action, free. `settings.action/SaveSnapshotButton/Settings_DiagnosticsSnapshot`
- Open Log Folder (Diagnostics): action, asks (OpensPlace). `settings.action/DataGroup/Settings_DiagnosticsOpenFolder`
- Keep a Diagnostics Log (Diagnostics): switch, free. `settings/DataGroup/Settings_DiagnosticsLogging`
- What to Record (Diagnostics): setting, yours-alone, under More. `settings/WhatToRecord/Settings_RecordProblem_WhatToRecord`
- Save the Last 60 Seconds (Diagnostics): action, free, under More. `settings.action/DiagnosticsMore/Settings_RecordProblem_SaveLastMinute`
- Keep the Last 60 Seconds (Diagnostics): switch, free, under More. `settings/DiagnosticsMore/Settings_RecordProblem_KeepLastMinute`
- Write Typed Letters and Numbers as One Key (Diagnostics): switch, free, under More. `settings/DiagnosticsMore/Settings_RecordProblem_HideTypedKeys`

## Extras

### Web Controller

- Web Controller: extra, free. `extras.extra/WebController/WebControllerTile/Dashboard_WebController`
- Enable Web Controller Server: switch, asks (Network). `extras/WebController/WebControllerPage/Dashboard_EnableWeb`
- Connect a Phone: card, asks (Network). `extras.card/WebController/WebControllerConnectCard/Extras_WebController_ConnectTitle`
- Copy Address (Connect a Phone): action, asks (Network). `extras.action/WebController/WebControllerConnectCard/Extras_CopyAddress`
- Phones: card, asks (Network). `extras.card/WebController/WebControllerPhonesCard/Extras_WebController_Phones`
- Web Controller Port: number, asks (Network), under More. `extras/WebController/WebControllerMore/Dashboard_WebControllerPort`
- Also Serve Plain HTTP: switch, asks (Network), under More. `extras/WebController/WebControllerMore/Dashboard_WebPlainEnable`
- Plain HTTP Port: number, asks (Network), under More. `extras/WebController/WebControllerMore/Dashboard_WebPlainPort`
- This PC Only: switch, asks (Network), under More. `extras/WebController/WebControllerMore/Dashboard_WebPlainLocalOnly`
- New Code Access Code: action, yours-alone, under More. `extras.action/WebController/WebControllerMore/Dashboard_WebNewAccessCode#Dashboard_WebAccessCode`
- Copy Address: action, asks (Network), under More. `extras.action/WebController/WebControllerMore/Extras_CopyAddress`

### Head Tracking

- Head Tracking: extra, free. `extras.extra/HeadTracking/HeadTrackingTile/Dashboard_HeadTracking`
- Head Tracking: switch, free. `extras/HeadTracking/HeadTrackingPage/Dashboard_HeadTracking`
- Set Neutral: action, free. `extras.action/HeadTracking/HeadTrackingPage/Dashboard_HeadTrackingRecenter`
- Sources: card, free. `extras.card/HeadTracking/HeadTrackingPage/Extras_HeadTracking_Sources`
- Enable UDP Tracking Input (Sources): switch, free. `extras/HeadTracking/HeadTrackingEnabledReset/Dashboard_HeadTrackingEnable`
- Enable FreeTrack 2.0 Shared Memory Input (Sources): switch, free. `extras/HeadTracking/HeadTrackingFreeTrackReset/Dashboard_HeadTrackingFreeTrack`
- Enable OpenXR Headset Input (Sources): switch, free. `extras/HeadTracking/HeadTrackingOpenXrReset/Dashboard_HeadTrackingOpenXr`
- Range: card, free. `extras.card/HeadTracking/HeadTrackingPage/Extras_HeadTracking_Range`
- Rotation Range (degrees) (Range): number, free. `extras/HeadTracking/HeadTrackingPage/Dashboard_HeadTrackingRotationRange`
- Translation Range (cm) (Range): number, free. `extras/HeadTracking/HeadTrackingPage/Dashboard_HeadTrackingTranslationRange`
- UDP Port: number, free, under More. `extras/HeadTracking/HeadTrackingUdpPortControls/Dashboard_HeadTrackingPort`
- OpenXR Runtime: choice, free, under More. `extras/HeadTracking/HeadTrackingOpenXrRuntimeReset/Dashboard_HeadTrackingOpenXrRuntime`
- Yaw: number, free, under More. `extras/HeadTracking/HeadTrackingMore/Dashboard_HeadTrackingAxisYaw`
- Pitch: number, free, under More. `extras/HeadTracking/HeadTrackingMore/Dashboard_HeadTrackingAxisPitch`
- Roll: number, free, under More. `extras/HeadTracking/HeadTrackingMore/Dashboard_HeadTrackingAxisRoll`
- Lean Left and Right: number, free, under More. `extras/HeadTracking/HeadTrackingMore/Dashboard_HeadTrackingAxisX`
- Rise and Crouch: number, free, under More. `extras/HeadTracking/HeadTrackingMore/Dashboard_HeadTrackingAxisY`
- Lean Forward and Back: number, free, under More. `extras/HeadTracking/HeadTrackingMore/Dashboard_HeadTrackingAxisZ`

### Game Telemetry

- Game Telemetry: extra, free. `extras.extra/GameTelemetry/GameTelemetryTile/Dashboard_GameTelemetry`
- Listen for Game Telemetry: switch, free. `extras/GameTelemetry/GameTelemetryPage/Dashboard_EnableGameTelemetry`
- Game: card, free. `extras.card/GameTelemetry/GameTelemetryPage/Dashboard_GameTelemetryGame`
- Game (Game): choice, free. `extras/GameTelemetry/GameTelemetryPage/Dashboard_GameTelemetryGame`
- Game Telemetry Port: number, free, under More. `extras/GameTelemetry/GameTelemetryMore/Dashboard_GameTelemetryPort`
- Forward to Port: number, free, under More. `extras/GameTelemetry/GameTelemetryMore/Dashboard_GameTelemetryForwardPort`
- Full Speed (km/h): number, free, under More. `extras/GameTelemetry/GameTelemetryMore/Dashboard_GameTelemetryFullSpeed`
- Custom Formats… Games PadForge Does Not Know: action, free, under More. `extras.action/GameTelemetry/GameTelemetryFormatsButton/Dashboard_GameTelemetryFormats#Extras_GameTelemetry_UnknownGames`

### Twitch Chat

- Twitch Chat: extra, free. `extras.extra/TwitchChat/TwitchChatTile/Dashboard_TwitchChat`
- Read Twitch Chat: switch, free. `extras/TwitchChat/TwitchChatPage/Dashboard_EnableTwitchChat`
- Channel: card, free. `extras.card/TwitchChat/TwitchChatChannelCard/Dashboard_TwitchChatChannel`
- Who Approves !claim (Channel): choice, free. `extras/TwitchChat/TwitchChatChannelCard/Dashboard_TwitchChatClaimApproval`
- Manage Chat Commands Words Viewers Can Type (Channel): action, free. `extras.action/TwitchChat/TwitchChatChannelCard/TwitchChat_ManageButton#Extras_TwitchChat_Words`
- While You Are Live: card, free. `extras.card/TwitchChat/TwitchChatLiveCard/Extras_TwitchChat_LiveTitle`
- Pause (While You Are Live): action, free. `extras.action/TwitchChat/TwitchChatLiveCard/TwitchChat_Pause`
- Skip (While You Are Live): action, free. `extras.action/TwitchChat/TwitchChatLiveCard/TwitchChat_Skip`
- Clear Line (While You Are Live): action, free. `extras.action/TwitchChat/TwitchChatLiveCard/TwitchChat_ClearLine`
- End Claim (While You Are Live): action, free. `extras.action/TwitchChat/TwitchChatLiveCard/TwitchChat_EndClaim`
- Picture for Your Broadcast: card, free. `extras.card/TwitchChat/TwitchChatDisplayCard/Extras_TwitchChat_DisplayTitle`
- Copy Address (Picture for Your Broadcast): action, free. `extras.action/TwitchChat/TwitchChatDisplayCard/Extras_CopyAddress`

### Remote Link

- Remote Link: extra, free. `extras.extra/RemoteLink/RemoteLinkTile/Dashboard_RemoteLink`
- Enable Remote Link Server: switch, asks (Network). `extras/RemoteLink/RemoteLinkPage/Dashboard_EnableRemoteLink`
- Paired PCs: card, asks (Network). `extras.card/RemoteLink/RemoteLinkPairedCard/RemoteLink_PairedPeers`
- Revoke All (Paired PCs): action, asks (Network). `extras.action/RemoteLink/RemoteLinkPairedCard/RemoteLink_RevokeAll`
- Pair Another PC: card, asks (Network). `extras.card/RemoteLink/RemoteLinkPairCard/Extras_RemoteLink_PairAnother`
- Auto-Reconnect: switch, asks (Network), under More. `extras/RemoteLink/RemoteLinkMore/RemoteLink_AutoReconnect`
- Remote Link Port: number, asks (Network), under More. `extras/RemoteLink/RemoteLinkMore/Dashboard_RemoteLinkPort`
- Pairing Identity: choice, asks (Network), under More. `extras/RemoteLink/RemoteLinkMore/RemoteLink_IdentityTitle`
- Pair / Connect: action, asks (Network), under More. `extras.action/RemoteLink/RemoteLinkMore/Dashboard_RemoteLinkConnectButton`

### Motion Server

- Motion Server: extra, free. `extras.extra/MotionServer/MotionServerTile/Dashboard_MotionServer`
- Enable DSU Motion Server (CemuHook Motion Provider Protocol): switch, asks (Network). `extras/MotionServer/MotionServerPage/Dashboard_EnableDsu`
- Motion Server Port: number, asks (Network), under More. `extras/MotionServer/MotionServerMore/Dashboard_MotionServerPort`

### Game Mods

- Game Mods: extra, free. `extras.extra/GameMods/GameModsTile/Dashboard_GameMods`
- Accept DSX Game Mods: switch, asks (Network). `extras/GameMods/GameModsPage/Dashboard_EnableGameMods`
- Game Mod Port: number, asks (Network), under More. `extras/GameMods/GameModsMore/Dashboard_GameModPort`
- Run a DSX.exe Stand-In: switch, asks (Network), under More. `extras/GameMods/GameModsMore/Dashboard_GameModStandIn`

### Overlays

- Overlays: extra, free. `extras.extra/Overlays/OverlaysTile/Dashboard_Overlays`
- Overlays: switch, free. `extras/Overlays/OverlaysPage/Dashboard_Overlays`
- What Shows: card, free. `extras.card/Overlays/OverlaysPage/Extras_Overlays_WhatShows`
- Menu Overlay (What Shows): switch, free. `extras/Overlays/OverlaysPage/Dashboard_MenuOverlay`
- Shift Layer Flyout (What Shows): switch, free. `extras/Overlays/OverlaysPage/Dashboard_ShiftLayerFlyout`
- Profile Switch Overlay (What Shows): switch, free. `extras/Overlays/OverlaysPage/Dashboard_ProfileOverlay`
- Controllers On or Off (What Shows): switch, free. `extras/Overlays/OverlaysPage/Dashboard_ControllersNote`
- Device Disconnected Note (What Shows): switch, free. `extras/Overlays/OverlaysPage/Dashboard_DeviceDisconnectedNote`
- Device Connected Note (What Shows): switch, free. `extras/Overlays/OverlaysPage/Dashboard_DeviceConnectedNote`

### Touchpad Overlay

- Touchpad Overlay: extra, free. `extras.extra/TouchpadOverlay/TouchpadOverlayTile/Dashboard_TouchpadOverlay`
- Enable Touchpad Overlay: switch, free. `extras/TouchpadOverlay/TouchpadOverlayPage/Dashboard_EnableTouchpadOverlay`
- Look and Place: card, free. `extras.card/TouchpadOverlay/TouchpadOverlayLookCard/Extras_TouchpadOverlay_LookAndPlace`
- Opacity (Look and Place): number, free. `extras/TouchpadOverlay/TouchpadOverlayLookCard/Dashboard_TouchpadOverlayOpacity`
- Reset Position Place on Screen (Look and Place): action, free. `extras.action/TouchpadOverlay/TouchpadOverlayLookCard/Dashboard_TouchpadOverlayResetPosition#Extras_PlaceOnScreen`

### Screen Overlay

- Screen Overlay: extra, free. `extras.extra/ScreenOverlay/ScreenOverlayTile/Dashboard_ScreenOverlay`
- Add Overlay: action, free. `extras.action/ScreenOverlay/ScreenOverlayPage/Dashboard_ScreenOverlayAdd`
- Enable Screen Overlay: switch, free. `extras/ScreenOverlay/ScreenOverlayPage/[ScreenOverlays]/Dashboard_EnableScreenOverlay#=Title`
- Choose What It Shows…: action, free. `extras.action/ScreenOverlay/ScreenOverlayPage/[ScreenOverlays]/Dashboard_ScreenOverlayChoose#=Title`
- Opacity: number, free. `extras/ScreenOverlay/ScreenOverlayPage/[ScreenOverlays]/Dashboard_TouchpadOverlayOpacity#=Title`
- Reset Position: action, free. `extras.action/ScreenOverlay/ScreenOverlayPage/[ScreenOverlays]/Dashboard_TouchpadOverlayResetPosition#=Title`

### Crosshair

- Crosshair: extra, free. `extras.extra/Crosshair/CrosshairTile/Extras_Crosshair_Title`
- Add Crosshair: action, free. `extras.action/Crosshair/CrosshairPage/Extras_Crosshair_Add`
- Show Crosshair: switch, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Show#=Title`
- Style: choice, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Style#=Title`
- Display: choice, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Pad_Lighting_ScreenDisplay#=Title`
- Offset From Center: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Offset#=Title`
- Arm Length: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Length#=Title`
- Radius: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Radius#=Title`
- Sides: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Sides#=Title`
- Thickness: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Thickness#=Title`
- Gap: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Gap#=Title`
- Center Dot: switch, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_CenterDot#=Title`
- T Shape: switch, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_TShape#=Title`
- Size: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_DotSize#=Title`
- Picture: setting, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Picture#=Title`
- Picture’s Own Size: action, free. `extras.action/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_OwnSize#=Title`
- Size: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_PictureSize#=Title`
- Keep Proportions: switch, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_KeepProportions#=Title`
- Color: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Color#=Title`
- Opacity: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Dashboard_TouchpadOverlayOpacity#=Title`
- Outline: switch, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Outline#=Title`
- Outline Width: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_OutlineWidth#=Title`
- Outline Color: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_OutlineColor#=Title`
- Rotation: number, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_Rotation#=Title`
- Smooth Edges: switch, free. `extras/Crosshair/CrosshairPage/[Crosshairs]/Extras_Crosshair_SmoothEdges#=Title`

### Magnifier

- Magnifier: extra, free. `extras.extra/Magnifier/MagnifierTile/Extras_Magnifier_Title`
- Show Magnifier: switch, free. `extras/Magnifier/MagnifierPage/Extras_Magnifier_Show`
- What It Shows: card, free. `extras.card/Magnifier/MagnifierMainCard/Extras_Magnifier_Card_Main`
- Mode (What It Shows): choice, free. `extras/Magnifier/MagnifierMainCard/Extras_Magnifier_Mode`
- Center On (What It Shows): choice, free. `extras/Magnifier/MagnifierMainCard/Extras_Magnifier_Follow`
- Display (What It Shows): choice, free. `extras/Magnifier/MagnifierMainCard/Pad_Lighting_ScreenDisplay`
- Zoom (What It Shows): number, free. `extras/Magnifier/MagnifierMainCard/Extras_Magnifier_Zoom`
- Lens: card, free. `extras.card/Magnifier/MagnifierLensCard/Extras_Magnifier_Mode_Lens`
- Shape (Lens): choice, free. `extras/Magnifier/MagnifierLensCard/Extras_Magnifier_Shape`
- Size (Lens): number, free. `extras/Magnifier/MagnifierLensCard/Extras_Magnifier_Size`
- Height (Lens): number, free. `extras/Magnifier/MagnifierLensCard/Extras_Magnifier_Height`
- Border (Lens): switch, free. `extras/Magnifier/MagnifierLensCard/Extras_Magnifier_Border`
- Border Width (Lens): number, free. `extras/Magnifier/MagnifierLensCard/Extras_Magnifier_BorderWidth`
- Border Color (Lens): number, free. `extras/Magnifier/MagnifierLensCard/Extras_Magnifier_BorderColor`
- Spotlight: card, free. `extras.card/Magnifier/MagnifierSpotlightCard/Extras_Magnifier_Mode_Spotlight`
- Circle Size (Spotlight): number, free. `extras/Magnifier/MagnifierSpotlightCard/Extras_Magnifier_SpotlightSize`
- Darkness (Spotlight): number, free. `extras/Magnifier/MagnifierSpotlightCard/Extras_Magnifier_SpotlightDim`

### Automation

- Automation: extra, free. `extras.extra/Automation/AutomationTile/Extras_Automation_Title`
- Allow Outside Programs: switch, yours-alone. `extras/Automation/AutomationPage/Extras_Automation_Switch`
- What Programs May Do: card, yours-alone. `extras.card/Automation/AutomationLevelCard/Extras_Automation_Card_Level`
- Level (What Programs May Do): choice, yours-alone. `extras/Automation/AutomationLevelCard/Extras_Automation_Level`
- Waiting for You: card, yours-alone. `extras.card/Automation/AutomationWaitingCard/Extras_Automation_Card_Waiting`
- Assistants: card, yours-alone. `extras.card/Automation/AutomationAssistantsCard/Extras_Automation_Card_Assistants`
- Remove (Assistants): menu item, yours-alone. `extras.menuitem/Automation/AssistantRowsList/Common_Remove`
- Save the Skill as a ZIP… (Assistants): menu item, yours-alone. `extras.menuitem/Automation/AssistantRowsList/Extras_Automation_SaveZip`
- Open Its Settings File (Assistants): menu item, yours-alone. `extras.menuitem/Automation/AssistantRowsList/Extras_Automation_OpenSettings`
- Copy Settings Other Assistants (Assistants): action, yours-alone, under More. `extras.action/Automation/AutomationMore/Extras_Automation_CopySettings#Extras_Automation_OtherAssistants`
- Copy a Message Other Assistants (Assistants): action, yours-alone, under More. `extras.action/Automation/AutomationMore/Extras_Automation_CopyMessage#Extras_Automation_OtherAssistants`
- Scripts: card, yours-alone. `extras.card/Automation/AutomationScriptsCard/Extras_Automation_Card_Scripts`
- Save a Library… Script Library (Scripts): action, yours-alone, under More. `extras.action/Automation/ScriptsMore/Extras_Automation_SaveLibrary#Extras_Automation_Library`
- Remove All Installed Copies (Scripts): action, yours-alone, under More. `extras.action/Automation/ScriptsMore/Extras_Automation_Scripts_RemoveAll#Extras_Automation_Scripts_Installed`
- Allowed Programs: card, yours-alone. `extras.card/Automation/AutomationAllowedCard/Extras_Automation_Card_Allowed`
- Activity: card, yours-alone. `extras.card/Automation/AutomationActivityCard/Extras_Automation_Card_Activity`
- Export… (Activity): action, yours-alone. `extras.action/Automation/AutomationActivityCard/Extras_Automation_Export`

### Plugins

- Plugins: extra, free. `extras.extra/Plugins/PluginsTile/Extras_Plugins_Title`
- Run Plugins: switch, yours-alone. `extras/Plugins/PluginsHead/Extras_Plugins_Switch`
- Test Controller: card, free. `extras.card/Plugins/PluginsTestController/Plugins_TestController`
- Test Controller (Test Controller): switch, yours-alone. `extras/Plugins/PluginsTestControllerSwitch/Plugins_TestController`
- Reset All (Test Controller): action, free. `extras.action/Plugins/PluginsTestController/Pad_ResetAll`
- Replay: card, yours-alone. `extras.card/Plugins/PluginsReplay/Plugins_Replay`
- Open a Recording… (Replay): action, yours-alone. `extras.action/Plugins/PluginsReplay/Plugins_Replay_Open`
- Replay (Replay): switch, yours-alone. `extras/Plugins/PluginsReplay/Plugins_Replay`
- Play Playback (Replay): action, yours-alone. `extras.action/Plugins/PluginsReplay/Plugins_Replay_Play#Plugins_Replay_Playback`
- Repeat (Replay): switch, yours-alone. `extras/Plugins/PluginsReplay/Plugins_Replay_Repeat`
- Add a Plugin…: action, yours-alone. `extras.action/Plugins/PluginsAddCard/Plugins_Add`
- Save the Plugin Kit… Plugin Kit: action, yours-alone, under More. `extras.action/Plugins/PluginsMore/Plugins_Kit_Save#Plugins_Kit`

## Devices

- Pair a Controller: action, free. `devices.action/PairButton/Devices_Pair`
- Not Connected: card, free. `devices.card/NotConnectedFold/Devices_Group_NotConnected`
- Test Rumble: action, free. `devices.action/TestRumbleButton/Pad_TestRumble`
- Clear Mod Effects: action, free. `devices.action/IdentityCard/Devices_ClearModEffects`
- Test Inputs: card, free. `devices.card/TestCard/Devices_RawInputState`
- SDL Mapping: card, free. `devices.card/SdlMappingCard/SdlMapping_Title`
- Copy SDL Mapping (SDL Mapping): action, free. `devices.action/SdlMappingCard/SdlMapping_Copy`
- Remove Mine (SDL Mapping): action, free. `devices.action/SdlMappingCard/SdlMapping_RemoveMine`
- Paste SDL Mapping… (SDL Mapping): action, free. `devices.action/SdlMappingCard/SdlMapping_Paste`
- Find a Mapping… (SDL Mapping): action, free. `devices.action/SdlMappingCard/SdlMapping_Find`
- Guess a Layout (SDL Mapping): action, free. `devices.action/SdlMappingCard/SdlMapping_Guess`
- Map Again (SDL Mapping): action, free, under More. `devices.action/SdlMappingMore/SdlMapping_MapAgain`
- Community Database Page (SDL Mapping): action, asks (OpensPlace), under More. `devices.action/SdlMappingMore/SdlMapping_CommunityLink`
- Settings: card, free. `devices.card/SettingsCard/Devices_SettingsCard`
- Hide from Games (HidHide) (Settings): switch, asks (Drivers). `devices/HidingRows/Devices_HideFromGames`
- Consume Mapped Inputs (Hooks) (Settings): switch, free. `devices/HidingRows/Devices_ConsumeMapped`
- Move PadForge to Its Own Folder (Settings): action, free. `devices.action/HidingRows/ExeFolder_Move`
- Mouse DPI (Settings): setting, free. `devices/MouseDpiRows/Devices_MouseDpi`
- min (Settings): setting, free. `devices/IdleRow/Common_Min`
- Disconnect Bluetooth When Plugged In over USB (Settings): switch, free. `devices/PowerRows/Devices_QuickCharge`
- Read Arrows One by One (Settings): switch, free. `devices/SettingsCard/Devices_NativeArrows`
- Calibrate (Settings): action, free. `devices.action/GunRows/Devices_GunCalibrate`
- Gun Calibration (Settings): setting, free. `devices/GunRows/Devices_GunCalibration`
- Restore the Windows Driver (Settings): action, free. `devices.action/DeviceTools/Devices_DriverRestore`
- Read as iCade Controller (Settings): action, free. `devices.action/DeviceTools/Devices_ReadAsICade`
- Read as Keyboard (Settings): action, free. `devices.action/DeviceTools/Devices_ReadAsKeyboard`
- Read as Tekken Sticks (Settings): action, free. `devices.action/DeviceTools/Devices_UsioTekken`
- Read as Taiko Drums (Settings): action, free. `devices.action/DeviceTools/Devices_UsioTaiko`
- Register / Manage NFC Tags (Settings): action, free. `devices.action/DeviceTools/Nfc_ManageButton`
- Manage Voice Macros (Settings): action, free. `devices.action/DeviceTools/Voice_ManageButton`
- Manage Chat Commands (Settings): action, free. `devices.action/DeviceTools/TwitchChat_ManageButton`
- Learn / Manage Hidden Buttons (Settings): action, free. `devices.action/DeviceTools/Handheld_ManageButton`
- Map Key Lights (Settings): action, free. `devices.action/DeviceTools/MapKeyLights_ManageButton`
- Player Number… (Settings): action, free. `devices.action/DeviceTools/Devices_BlissBoxPlayer`
- Dreamcast Screen… (Settings): action, free. `devices.action/DeviceTools/Devices_DreamcastScreen`
- Back Up Controller Pak… (Settings): action, free. `devices.action/DeviceTools/Devices_PakBackup`
- Restore Controller Pak… (Settings): action, free. `devices.action/DeviceTools/Devices_PakRestore`
- Controller Screen… (Settings): action, free. `devices.action/DeviceTools/Devices_FlydigiScreen`
- Calibrate Reach (Settings): action, free. `devices.action/DeviceTools/Falcon_Calibrate`
- Reset All: action, free. `devices.action/PluginDeviceCard/Pad_ResetAll`
- Details: card, free. `devices.card/DetailsFold/Devices_Dossier`
- Force Raw Joystick Mode (Bypass Gamepad Remapping) (Details): switch, free. `devices/DetailsFold/Devices_ForceRaw`
- Copy Details (Details): action, free. `devices.action/DetailsFold/Devices_CopyDetails`
- Submit Device Mapping (Details): action, free. `devices.action/DetailsFold/Devices_SubmitMapping`
- Remove Device (Details): action, free. `devices.action/DetailsFold/Devices_RemoveConfirmTitle`

## Profiles

- Get Profiles: action, asks (ImportsProfile). `profiles.action/GetProfilesButton/GetProfiles_Title`
- New Profile: action, free. `profiles.action/NewProfileButton/Profiles_New`
- Edit…: menu item, free. `profiles.menuitem/ProfileListBox/Profiles_Edit`
- Save As…: menu item, free. `profiles.menuitem/ProfileListBox/Profiles_SaveAs`
- Save Current Setup Here: menu item, free. `profiles.menuitem/ProfileListBox/Profiles_SaveHere`
- Export…: menu item, free. `profiles.menuitem/ProfileListBox/Profiles_Export`
- Delete: menu item, free. `profiles.menuitem/ProfileListBox/Common_Delete`
- Automatic Switching: card, free. `profiles.card/AutoSwitchCard/Profiles_AutoSwitchTitle`
- Switch Profiles by Game (Automatic Switching): switch, free. `profiles/AutoSwitchCard/Profiles_AutoSwitch`
- Shortcuts: card, free. `profiles.card/ShortcutsCard/Profiles_Shortcuts`
- External Control: card, free. `profiles.card/ExternalControlCard/Profiles_ExternalControlTitle`
- Open Automation (External Control): action, free. `profiles.action/AutomationStateHost/Profiles_OpenAutomation`

## Dialogs

### AddControllerView

- AddControllerView: dialog, free. `dialog.dialog/AddControllerView/==AddControllerView`
- Close: action, free. `dialog.action/AddControllerView/CloseButton/Common_Close`

### AutomationRequestCard

- Waiting for You: dialog, yours-alone. `dialog.dialog/AutomationRequestCard/Extras_Automation_Card_Waiting`
- Don’t Allow: action, yours-alone. `dialog.action/AutomationRequestCard/DenyButton/Automation_Request_Deny`
- Allow: action, yours-alone. `dialog.action/AutomationRequestCard/AllowButton/Automation_Request_Allow`

### BlissBoxPlayerDialog

- Player Number: dialog, free. `dialog.dialog/BlissBoxPlayerDialog/BlissBoxPlayer_Title`
- Cancel: action, free. `dialog.action/BlissBoxPlayerDialog/CancelButton/Common_Cancel`
- Set Player: action, free. `dialog.action/BlissBoxPlayerDialog/ActionButton/BlissBoxPlayer_Action`
- Player: choice, free. `dialog/BlissBoxPlayerDialog/BlissBoxPlayer_Label`

### ChatCommandsDialog

- Chat Commands: dialog, free. `dialog.dialog/ChatCommandsDialog/TwitchChat_DialogTitle`
- Restore Usual Words: action, free. `dialog.action/ChatCommandsDialog/TwitchChat_RestoreButton`
- Close: action, free. `dialog.action/ChatCommandsDialog/Common_Close`
- Add: action, free. `dialog.action/ChatCommandsDialog/AddBtn/TwitchChat_AddButton`

### ConfirmDialog

- ConfirmDialog: dialog, asks. `dialog.dialog/ConfirmDialog/==ConfirmDialog`
- Cancel: action, asks. `dialog.action/ConfirmDialog/CancelButton/Common_Cancel`

### CopyFromDialog

- Copy From: dialog, free. `dialog.dialog/CopyFromDialog/CopyFrom_Title`
- Cancel: action, free. `dialog.action/CopyFromDialog/Common_Cancel`
- Copy: action, free. `dialog.action/CopyFromDialog/CopyButton/Common_Copy`

### CustomTelemetryFormatsDialog

- Custom Formats: dialog, free. `dialog.dialog/CustomTelemetryFormatsDialog/Telemetry_Formats_Title`
- Cancel: action, free. `dialog.action/CustomTelemetryFormatsDialog/Common_Cancel`
- Save: action, free. `dialog.action/CustomTelemetryFormatsDialog/SaveButton/Common_Save`
- Add: action, free. `dialog.action/CustomTelemetryFormatsDialog/AddFormatButton/Common_Add`
- Duplicate: action, free. `dialog.action/CustomTelemetryFormatsDialog/DuplicateFormatButton/Common_Duplicate`
- Delete: action, free. `dialog.action/CustomTelemetryFormatsDialog/DeleteFormatButton/Common_Delete`
- Add Field: action, free. `dialog.action/CustomTelemetryFormatsDialog/AddFieldButton/Telemetry_Formats_AddField`

### DreamcastScreenDialog

- Dreamcast Screen: dialog, free. `dialog.dialog/DreamcastScreenDialog/DreamcastScreen_Title`
- Cancel: action, free. `dialog.action/DreamcastScreenDialog/Common_Cancel`
- Save: action, free. `dialog.action/DreamcastScreenDialog/SaveButton/Common_Save`
- Show: choice, free. `dialog/DreamcastScreenDialog/DreamcastScreen_Mode_Label`
- Choose Picture…: action, free. `dialog.action/DreamcastScreenDialog/ChoosePictureButton/DreamcastScreen_ChoosePicture`
- Chosen Picture: setting, free. `dialog/DreamcastScreenDialog/PictureRow/DreamcastScreen_Mode_Picture`
- Remove Profile Picture: action, free. `dialog.action/DreamcastScreenDialog/RemoveProfilePictureButton/DreamcastScreen_RemoveProfilePicture`

### FlydigiScreenDialog

- Controller Screen: dialog, free. `dialog.dialog/FlydigiScreenDialog/FlydigiScreen_Title`
- Close: action, free. `dialog.action/FlydigiScreenDialog/CloseButton/Common_Close`
- Send: action, free. `dialog.action/FlydigiScreenDialog/SendButton/FlydigiScreen_Send`
- Choose Picture…: action, free. `dialog.action/FlydigiScreenDialog/ChoosePictureButton/DreamcastScreen_ChoosePicture`
- Remove Profile Picture: action, free. `dialog.action/FlydigiScreenDialog/RemoveProfilePictureButton/DreamcastScreen_RemoveProfilePicture`

### GetProfilesDialog

- Get Profiles: dialog, asks (ImportsProfile). `dialog.dialog/GetProfilesDialog/GetProfiles_Title`
- Cancel: action, asks (ImportsProfile). `dialog.action/GetProfilesDialog/CancelButton/Common_Cancel`
- Save and Apply: action, asks (ImportsProfile). `dialog.action/GetProfilesDialog/SaveApplyButton/Workshop_SaveAndApply`
- Save Profile: action, asks (ImportsProfile). `dialog.action/GetProfilesDialog/SaveButton/Workshop_SaveProfile`
- Import…: action, asks (ImportsProfile). `dialog.action/GetProfilesDialog/ImportButton/Profiles_Import`
- Starters: action, asks (ImportsProfile). `dialog.action/GetProfilesDialog/StartersTab/GetProfiles_Starters`
- Community: action, asks (ImportsProfile). `dialog.action/GetProfilesDialog/CommunityTab/GetProfiles_Community`
- Import a File: action, asks (ImportsProfile). `dialog.action/GetProfilesDialog/FileTab/GetProfiles_File`

### ImportConfigDialog

- Import Profile: dialog, asks (ImportsProfile). `dialog.dialog/ImportConfigDialog/Import_DialogTitle`
- Cancel: action, asks (ImportsProfile). `dialog.action/ImportConfigDialog/CancelButton/Common_Cancel`
- Import: action, asks (ImportsProfile). `dialog.action/ImportConfigDialog/ImportButton/Import_Confirm`

### KeepDriverDialog

- KeepDriverDialog: dialog, asks (Drivers). `dialog.dialog/KeepDriverDialog/==KeepDriverDialog`
- Give It Back: action, asks (Drivers). `dialog.action/KeepDriverDialog/GiveBackButton/Devices_DriverGiveBack`
- Keep: action, asks (Drivers). `dialog.action/KeepDriverDialog/KeepButton/Devices_DriverKeep`

### KeyboardScreenDialog

- KeyboardScreenDialog: dialog, free. `dialog.dialog/KeyboardScreenDialog/==KeyboardScreenDialog`
- Cancel: action, free. `dialog.action/KeyboardScreenDialog/Common_Cancel`
- Save: action, free. `dialog.action/KeyboardScreenDialog/SaveButton/Common_Save`
- Show: choice, free. `dialog/KeyboardScreenDialog/DreamcastScreen_Mode_Label`
- Choose Picture…: action, free. `dialog.action/KeyboardScreenDialog/ChoosePictureButton/DreamcastScreen_ChoosePicture`
- Chosen Picture: setting, free. `dialog/KeyboardScreenDialog/PictureRow/DreamcastScreen_Mode_Picture`
- Remove Profile Picture: action, free. `dialog.action/KeyboardScreenDialog/RemoveProfilePictureButton/DreamcastScreen_RemoveProfilePicture`
- Slot: choice, free. `dialog/KeyboardScreenDialog/LightbarSlotRow/Dashboard_Slot`
- Zoom: setting, free. `dialog/KeyboardScreenDialog/ZoomRow/KeyboardScreen_Zoom_Label`
- Pan: setting, free. `dialog/KeyboardScreenDialog/PanRow/KeyboardScreen_Pan_Label`

### LearnHandheldButtonDialog

- Hidden Buttons: dialog, free. `dialog.dialog/LearnHandheldButtonDialog/Handheld_Title`
- Start Over: action, free. `dialog.action/LearnHandheldButtonDialog/StartBtn/Handheld_StartOver`
- Export…: action, free. `dialog.action/LearnHandheldButtonDialog/Handheld_Export`
- Import…: action, free. `dialog.action/LearnHandheldButtonDialog/Handheld_Import`
- Close: action, free. `dialog.action/LearnHandheldButtonDialog/Common_Close`
- Register: action, free. `dialog.action/LearnHandheldButtonDialog/RegisterBtn/Handheld_RegisterButton`

### ManageProfilesDialog

- Controller Presets: dialog, free. `dialog.dialog/ManageProfilesDialog/ManageProfiles_Title`
- Close: action, free. `dialog.action/ManageProfilesDialog/CloseButton/Common_Close`
- Import from File…: action, free. `dialog.action/ManageProfilesDialog/ImportFromFileButton/ManageProfiles_ImportFromFile`
- Import: action, free. `dialog.action/ManageProfilesDialog/ImportButton/ImportFromDevice_Import`

### MapKeyLightsDialog

- Map Key Lights: dialog, free. `dialog.dialog/MapKeyLightsDialog/MapKeyLights_Title`
- Export…: action, free. `dialog.action/MapKeyLightsDialog/MapKeyLights_Export`
- Import…: action, free. `dialog.action/MapKeyLightsDialog/MapKeyLights_Import`
- Forget All: action, free. `dialog.action/MapKeyLightsDialog/MapKeyLights_ForgetAll`
- Close: action, free. `dialog.action/MapKeyLightsDialog/Common_Close`
- Start: action, free. `dialog.action/MapKeyLightsDialog/StartBtn/MapKeyLights_Start`
- Back: action, free. `dialog.action/MapKeyLightsDialog/StepButtons/MapKeyLights_Back`
- Skip: action, free. `dialog.action/MapKeyLightsDialog/StepButtons/MapKeyLights_Skip`
- Forget: action, free. `dialog.action/MapKeyLightsDialog/StepButtons/MapKeyLights_Forget`
- Stop: action, free. `dialog.action/MapKeyLightsDialog/StepButtons/MapKeyLights_Stop`

### PairDeviceDialog

- Pair a Wii Remote: dialog, free. `dialog.dialog/PairDeviceDialog/WiiPair_Title`
- Cancel: action, free. `dialog.action/PairDeviceDialog/DismissButton/Common_Cancel`
- Pair: action, free. `dialog.action/PairDeviceDialog/PairButton/WiiPair_Pair`
- Controller Family: choice, free. `dialog/PairDeviceDialog/WiiPair_Family`
- Temporary Pairing (Hold 1 and 2): setting, free. `dialog/PairDeviceDialog/WiiPair_Temporary`
- Port: choice, free. `dialog/PairDeviceDialog/SerialPickers/SerialPair_Port`
- Controller: choice, free. `dialog/PairDeviceDialog/SerialPickers/SerialPair_Controller`
- Address: setting, free. `dialog/PairDeviceDialog/DjiPickers/DjiPair_Address`

### PickSoundDialog

- Pick a Sound: dialog, free. `dialog.dialog/PickSoundDialog/PickSound_Title`
- Browse Files…: action, free. `dialog.action/PickSoundDialog/BrowseButton/Macro_Sound_BrowseFiles`
- Cancel: action, free. `dialog.action/PickSoundDialog/Common_Cancel`
- Use Sound: action, free. `dialog.action/PickSoundDialog/OkButton/PickSound_Use`

### ProfileDialog

- Save As Profile: dialog, free. `dialog.dialog/ProfileDialog/ProfileDialog_SaveAs`
- Cancel: action, free. `dialog.action/ProfileDialog/Common_Cancel`
- Save: action, free. `dialog.action/ProfileDialog/SaveButton/Common_Save`
- Add a Running Game: action, free. `dialog.action/ProfileDialog/AddRunningGameButton/ProfileDialog_AddRunningGame`
- Browse…: action, free. `dialog.action/ProfileDialog/BrowseButton/Common_Browse`
- Wear the Game’s Look: setting, free. `dialog/ProfileDialog/GameLookPanel/Profile_GameLook`
- Get the Picture: action, free. `dialog.action/ProfileDialog/GameLookGetButton/Profile_GameLook_GetPicture`
- Polling Rate: choice, free, under More. `dialog/ProfileDialog/ProfileMore/ProfileDialog_PollingRate`

### RegisterNfcTagDialog

- NFC Tags: dialog, free. `dialog.dialog/RegisterNfcTagDialog/Nfc_Title`
- Close: action, free. `dialog.action/RegisterNfcTagDialog/Common_Close`
- Register: action, free. `dialog.action/RegisterNfcTagDialog/RegisterBtn/Nfc_RegisterButton`

### RegisterVoicePhraseDialog

- Voice Macros: dialog, free. `dialog.dialog/RegisterVoicePhraseDialog/Voice_Title`
- Close: action, free. `dialog.action/RegisterVoicePhraseDialog/Common_Close`
- Register: action, free. `dialog.action/RegisterVoicePhraseDialog/RegisterBtn/Voice_RegisterButton`
- Enable Voice Macros: setting, free, under More. `dialog/RegisterVoicePhraseDialog/VoiceMore/Voice_EnableLabel`
- Listening: choice, free, under More. `dialog/RegisterVoicePhraseDialog/VoiceMore/Voice_ModeLabel`
- Minimum Confidence: setting, free, under More. `dialog/RegisterVoicePhraseDialog/VoiceMore/Voice_ConfidenceLabel`

### ReleaseTestDialog

- Release Test: dialog, free. `dialog.dialog/ReleaseTestDialog/Pad_Centering_ReleaseTest`
- Run Again: action, free. `dialog.action/ReleaseTestDialog/RunAgainBtn/ReleaseTest_RunAgain`
- Cancel: action, free. `dialog.action/ReleaseTestDialog/Common_Cancel`
- Start: action, free. `dialog.action/ReleaseTestDialog/StartBtn/Common_Start`
- Stop: action, free. `dialog.action/ReleaseTestDialog/StopBtn/Common_Stop`
- Apply: action, free. `dialog.action/ReleaseTestDialog/ApplyBtn/Common_Apply`

### RemoteAssignmentsDialog

- RemoteAssignmentsDialog: dialog, free. `dialog.dialog/RemoteAssignmentsDialog/==RemoteAssignmentsDialog`
- Refresh: action, free. `dialog.action/RemoteAssignmentsDialog/RefreshButton/Common_Refresh`
- Close: action, free. `dialog.action/RemoteAssignmentsDialog/CloseButton/Common_Close`

### RemoteLinkPairDialog

- Pair with Another PC?: dialog, yours-alone. `dialog.dialog/RemoteLinkPairDialog/RemoteLink_PairTitle`
- Reject: action, yours-alone. `dialog.action/RemoteLinkPairDialog/RejectButton/RemoteLink_Reject`
- Pair: action, yours-alone. `dialog.action/RemoteLinkPairDialog/PairButton/RemoteLink_Pair`
- Gamepad Only: setting, yours-alone. `dialog/RemoteLinkPairDialog/RemoteLink_GamepadOnly`
- Allow Remote Assignment Changes: setting, yours-alone. `dialog/RemoteLinkPairDialog/RemoteLink_AllowAssignments`

### RemoteLinkPasswordDialog

- Set a Password: dialog, yours-alone. `dialog.dialog/RemoteLinkPasswordDialog/RemoteLink_PasswordSetTitle`
- Cancel: action, yours-alone. `dialog.action/RemoteLinkPasswordDialog/CancelButton/Common_Cancel`
- OK: action, yours-alone. `dialog.action/RemoteLinkPasswordDialog/OkButton/Common_OK`

### SdlMappingDialog

- Paste an SDL Mapping: dialog, free. `dialog.dialog/SdlMappingDialog/SdlMappingDialog_Title`
- Open a File…: action, free. `dialog.action/SdlMappingDialog/OpenFileButton/SdlMappingDialog_OpenFile`
- Cancel: action, free. `dialog.action/SdlMappingDialog/Common_Cancel`
- Paste: action, free. `dialog.action/SdlMappingDialog/PrimaryButton/SdlMappingDialog_Paste`

### SearchPanel

- SearchPanel: dialog, free. `dialog.dialog/SearchPanel/==SearchPanel`

### ShiftActivatorDialog

- Shift Layer: dialog, free. `dialog.dialog/ShiftActivatorDialog/Pad_Shift_DialogTitle`
- Delete Layer: action, free. `dialog.action/ShiftActivatorDialog/DeleteLayerButton/Pad_Shift_Delete`
- Cancel: action, free. `dialog.action/ShiftActivatorDialog/Common_Cancel`
- Save: action, free. `dialog.action/ShiftActivatorDialog/SaveButton/Common_Save`
- Pick an emoji or an image to show on the flyout when this layer is engaged.: action, free. `dialog.action/ShiftActivatorDialog/IconPickerButton/Pad_Shift_Icon_Tooltip`
- Record: action, free. `dialog.action/ShiftActivatorDialog/InputRecordButton/Common_Record`
- Clear: action, free. `dialog.action/ShiftActivatorDialog/InputClearButton/Common_Clear`
- Record: action, free. `dialog.action/ShiftActivatorDialog/ChordSecondRecordButton/Common_Record`
- Clear: action, free. `dialog.action/ShiftActivatorDialog/ChordSecondClearButton/Common_Clear`
- Axis Threshold: setting, free. `dialog/ShiftActivatorDialog/AxisThresholdRow/Pad_Shift_AxisThreshold`
- Unmapped Controls: choice, free. `dialog/ShiftActivatorDialog/UnmappedRow/Pad_Shift_UnmappedControls`
- Mode: choice, free, under More. `dialog/ShiftActivatorDialog/ModeRow/Pad_Shift_Mode`
- Activator Kind: choice, free, under More. `dialog/ShiftActivatorDialog/KindRow/Pad_Shift_Kind`
- Only While in Layer: choice, free, under More. `dialog/ShiftActivatorDialog/HostLayerRow/Pad_Shift_HostLayer`
- Fire on Release: setting, free, under More. `dialog/ShiftActivatorDialog/MorePanel/Pad_Shift_FireOnRelease`
- Also Fire Activator’s Own Mapping: setting, free, under More. `dialog/ShiftActivatorDialog/MorePanel/Pad_Shift_PostponeMapping`
- Cycle Through Layers: setting, free, under More. `dialog/ShiftActivatorDialog/CycleBlock/Pad_Shift_CycleLayers`
- Wrap Around: setting, free, under More. `dialog/ShiftActivatorDialog/CycleBlock/Pad_Shift_CycleWrap`
- Include Base: setting, free, under More. `dialog/ShiftActivatorDialog/CycleBlock/Pad_Shift_CycleIncludeBase`
- Delay: setting, free, under More. `dialog/ShiftActivatorDialog/DelayRow/Pad_Shift_Delay`
- Auto-Cancel After Inactivity: setting, free, under More. `dialog/ShiftActivatorDialog/AutoCancelRow/Pad_Shift_AutoCancel`
- Flyout: choice, free, under More. `dialog/ShiftActivatorDialog/FlyoutRow/Pad_Shift_Flyout`
- Layer Color: setting, free, under More. `dialog/ShiftActivatorDialog/ClearColorButton/Pad_Shift_Color`

### ThemeBuilderDialog

- New Theme: dialog, free. `dialog.dialog/ThemeBuilderDialog/ThemeBuilder_NewTitle`
- Cancel: action, free. `dialog.action/ThemeBuilderDialog/Common_Cancel`
- Save: action, free. `dialog.action/ThemeBuilderDialog/SaveButton/Common_Save`
- From a Steam Game…: action, free. `dialog.action/ThemeBuilderDialog/FromGameButton/ThemeBuilder_FromGame`
- From a File…: action, free. `dialog.action/ThemeBuilderDialog/FromFileButton/ThemeBuilder_FromFile`
- No Picture: action, free. `dialog.action/ThemeBuilderDialog/NoPictureButton/ThemeBuilder_NoPicture`
- Enable Community Configs: action, free. `dialog.action/ThemeBuilderDialog/LookupOffPanel/Workshop_EnableButton`
- Position: setting, free. `dialog/ThemeBuilderDialog/PositionRow/ThemeBuilder_Position`
- Show the Game’s Logo: setting, free. `dialog/ThemeBuilderDialog/LogoRow/ThemeBuilder_ShowLogo`
- Match the Picture: action, free. `dialog.action/ThemeBuilderDialog/MatchButton/ThemeBuilder_MatchPicture`

### TouchpadGestureRecorderDialog

- Record a Gesture: dialog, free. `dialog.dialog/TouchpadGestureRecorderDialog/Recorder_Title`
- Clear: action, free. `dialog.action/TouchpadGestureRecorderDialog/TryAgainBtn/Common_Clear`
- Cancel: action, free. `dialog.action/TouchpadGestureRecorderDialog/Common_Cancel`
- Save: action, free. `dialog.action/TouchpadGestureRecorderDialog/SaveBtn/Common_Save`
- Samples to Capture: choice, free, under More. `dialog/TouchpadGestureRecorderDialog/GestureMore/Recorder_SampleCount_Label`

### UninstallBthPS3Dialog

- Uninstall BthPS3: dialog, asks (Drivers). `dialog.dialog/UninstallBthPS3Dialog/BthPS3Uninstall_Title`
- Refresh: action, asks (Drivers). `dialog.action/UninstallBthPS3Dialog/RefreshButton/Common_Refresh`
- Cancel: action, asks (Drivers). `dialog.action/UninstallBthPS3Dialog/CancelButton/Common_Cancel`
- Uninstall: action, asks (Drivers). `dialog.action/UninstallBthPS3Dialog/ActionButton/Common_Uninstall`

### UninstallHIDMaestroDialog

- Uninstall HIDMaestro: dialog, asks (Drivers). `dialog.dialog/UninstallHIDMaestroDialog/HIDMaestroUninstall_Title`
- Refresh: action, asks (Drivers). `dialog.action/UninstallHIDMaestroDialog/RefreshButton/Common_Refresh`
- Cancel: action, asks (Drivers). `dialog.action/UninstallHIDMaestroDialog/CancelButton/Common_Cancel`
- Uninstall: action, asks (Drivers). `dialog.action/UninstallHIDMaestroDialog/ActionButton/Common_Uninstall`

## The window

- Full Screen: action, free. `window.action/FullScreenBtn/Main_FullScreen`

### Tray menu

- Show PadForge: menu item, free. `window.menuitem/Tray/Main_ShowPadForge`
- Mark the Recording: menu item, free. `window.menuitem/Tray/Tray_MarkRecording`
- Exit: menu item, free. `window.menuitem/Tray/Tray_Exit`

## Home

- Start: action, free. `home.action/FirstRunStart/Common_Start`
- Input Engine: action, free. `home.action/EngineFlame/Dashboard_InputEngine`
- Use It: action, free. `home.action/HomeOfferUse/Home_Offer_Use`
- Not Now: action, free. `home.action/HomeOfferDismiss/Pad_AssignOffer_Dismiss`
- Move PadForge to Its Own Folder: action, free. `home.action/HomeSharedFolderMove/ExeFolder_Move`
- Open Extras: action, free. `home.action/OpenExtrasButton/Home_OpenExtras`

## About

- Documentation: action, asks (OpensPlace). `about.action/PadForgeCard/About_Documentation`
- GitHub: action, asks (OpensPlace). `about.action/PadForgeCard/About_GitHub`
- Release Notes: action, asks (OpensPlace). `about.action/PadForgeCard/Settings_ReleaseNotes`
- Check for Updates: action, asks (OpensPlace). `about.action/PadForgeCard/About_CheckForUpdates`
- Show All Credits: action, asks (OpensPlace). `about.action/ShowAllCreditsButton/About_ShowAllCredits`
