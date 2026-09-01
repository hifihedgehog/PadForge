using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Data;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>One entry in the macro action-type picker: the enum
    /// value, its localized label, the localized category header the
    /// grouped dropdown renders, and the optional tooltip.</summary>
    public sealed class MacroTypeChoice
    {
        public MacroActionType Type { get; init; }
        public string Label { get; init; }
        public string Category { get; init; }
        public string Tooltip { get; init; }
    }

    /// <summary>
    /// The macro action-type catalog: every <see cref="MacroActionType"/>
    /// exactly once, grouped into the categories the editor's type picker
    /// renders with headers (the mapping table's cross-device input
    /// picker arrangement, applied to action types). The picker used to
    /// be fifty-six flat entries in enum-history order; the catalog
    /// orders them by what they act on. A census test pins that every
    /// enum member appears exactly once, so a future action type cannot
    /// ship without choosing its shelf.
    /// </summary>
    public static class MacroTypeCatalog
    {
        private static IReadOnlyList<MacroTypeChoice> _choices;
        private static ICollectionView _view;

        /// <summary>The flat catalog in display order (categories in
        /// presentation order, items in their in-category order).</summary>
        public static IReadOnlyList<MacroTypeChoice> Choices
            => _choices ??= Build();

        /// <summary>The grouped view the XAML picker binds to. Grouped on
        /// <see cref="MacroTypeChoice.Category"/>, the AvailableInputsView
        /// arrangement. Created on first XAML access (the UI thread).</summary>
        public static ICollectionView View
        {
            get
            {
                if (_view == null)
                {
                    var v = new ListCollectionView((System.Collections.IList)Choices);
                    v.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MacroTypeChoice.Category)));
                    _view = v;
                }
                return _view;
            }
        }

        private static IReadOnlyList<MacroTypeChoice> Build()
        {
            var S = Strings.Instance;
            var list = new List<MacroTypeChoice>(56);
            void Add(MacroActionType type, string label, string category, string tooltip)
                => list.Add(new MacroTypeChoice { Type = type, Label = label, Category = category, Tooltip = tooltip });

            {
                Add(MacroActionType.ButtonPress, S.Macro_ButtonPress, S.Macro_Cat_VcButtons, null);
                Add(MacroActionType.ButtonRelease, S.Macro_ButtonRelease, S.Macro_Cat_VcButtons, null);
                Add(MacroActionType.ToggleVcButton, S.MacroAction_Type_ToggleVcButton, S.Macro_Cat_VcButtons, S.MacroAction_ToggleVcButton_Tooltip);
                Add(MacroActionType.RepeatVcButtonWhileHeld, S.MacroAction_Type_RepeatVcButtonWhileHeld, S.Macro_Cat_VcButtons, S.MacroAction_RepeatVcButtonWhileHeld_Tooltip);
                Add(MacroActionType.AxisSet, S.Macro_SetAxis, S.Macro_Cat_VcAxes, null);
                Add(MacroActionType.AxisHold, S.MacroAction_Type_AxisHold, S.Macro_Cat_VcAxes, S.MacroAction_AxisHold_Tooltip);
                Add(MacroActionType.AxisAdd, S.MacroAction_Type_AxisAdd, S.Macro_Cat_VcAxes, S.MacroAction_AxisAdd_Tooltip);
                Add(MacroActionType.AxisScale, S.MacroAction_Type_AxisScale, S.Macro_Cat_VcAxes, S.MacroAction_AxisScale_Tooltip);
                Add(MacroActionType.AxisSetLatched, S.MacroAction_Type_AxisSetLatched, S.Macro_Cat_VcAxes, S.MacroAction_AxisSetLatched_Tooltip);
                Add(MacroActionType.AxisLatchRelease, S.MacroAction_Type_AxisLatchRelease, S.Macro_Cat_VcAxes, S.MacroAction_AxisLatchRelease_Tooltip);
                Add(MacroActionType.ToggleVcAxis, S.MacroAction_Type_ToggleVcAxis, S.Macro_Cat_VcAxes, S.MacroAction_ToggleVcAxis_Tooltip);
                Add(MacroActionType.RepeatVcAxisWhileHeld, S.MacroAction_Type_RepeatVcAxisWhileHeld, S.Macro_Cat_VcAxes, S.MacroAction_RepeatVcAxisWhileHeld_Tooltip);
                Add(MacroActionType.ToggleWheel, S.MacroAction_Type_ToggleWheel, S.Macro_Cat_VcAxes, S.MacroAction_ToggleWheel_Tooltip);
                Add(MacroActionType.KeyPress, S.Macro_KeyPress, S.Macro_Cat_Keyboard, null);
                Add(MacroActionType.KeyRelease, S.Macro_KeyRelease, S.Macro_Cat_Keyboard, null);
                Add(MacroActionType.ToggleKey, S.MacroAction_Type_ToggleKey, S.Macro_Cat_Keyboard, S.MacroAction_ToggleKey_Tooltip);
                Add(MacroActionType.RepeatKeyWhileHeld, S.MacroAction_Type_RepeatKeyWhileHeld, S.Macro_Cat_Keyboard, S.MacroAction_RepeatKeyWhileHeld_Tooltip);
                Add(MacroActionType.TextBlock, S.MacroAction_Type_TextBlock, S.Macro_Cat_Keyboard, S.MacroAction_TextBlock_Tooltip);
                Add(MacroActionType.MouseMove, S.Macro_MouseMove, S.Macro_Cat_Mouse, null);
                Add(MacroActionType.MouseButtonPress, S.Macro_MouseButtonPress, S.Macro_Cat_Mouse, null);
                Add(MacroActionType.MouseButtonRelease, S.Macro_MouseButtonRelease, S.Macro_Cat_Mouse, null);
                Add(MacroActionType.ToggleMouseButton, S.MacroAction_Type_ToggleMouseButton, S.Macro_Cat_Mouse, S.MacroAction_ToggleMouseButton_Tooltip);
                Add(MacroActionType.MouseScroll, S.Macro_MouseScroll, S.Macro_Cat_Mouse, null);
                Add(MacroActionType.MouseWheelTap, S.MacroAction_Type_MouseWheelTap, S.Macro_Cat_Mouse, S.MacroAction_MouseWheelTap_Tooltip);
                Add(MacroActionType.MouseNudge, S.MacroAction_Type_MouseNudge, S.Macro_Cat_Mouse, S.MacroAction_MouseNudge_Tooltip);
                Add(MacroActionType.MouseRecenter, S.MacroAction_Type_MouseRecenter, S.Macro_Cat_Mouse, S.MacroAction_MouseRecenter_Tooltip);
                Add(MacroActionType.MouseFixPosition, S.MacroAction_Type_MouseFixPosition, S.Macro_Cat_Mouse, S.MacroAction_MouseFixPosition_Tooltip);
                Add(MacroActionType.MouseLimitRegion, S.MacroAction_Type_MouseLimitRegion, S.Macro_Cat_Mouse, S.MacroAction_MouseLimitRegion_Tooltip);
                Add(MacroActionType.MoveMouseToScreenPosition, S.MacroAction_Type_MoveMouseToScreenPosition, S.Macro_Cat_Mouse, S.MacroAction_MoveMouseToScreenPosition_Tooltip);
                Add(MacroActionType.Delay, S.Macro_Delay, S.Macro_Cat_Flow, null);
                Add(MacroActionType.ComboBreak, S.MacroAction_Type_ComboBreak, S.Macro_Cat_Flow, S.MacroAction_ComboBreak_Tooltip);
                Add(MacroActionType.CycleTapList, S.MacroAction_Type_CycleTapList, S.Macro_Cat_Flow, S.MacroAction_CycleTapList_Tooltip);
                Add(MacroActionType.Rumble, S.MacroAction_Type_Rumble, S.Macro_Cat_Rumble, S.MacroAction_Rumble_Tooltip);
                Add(MacroActionType.RumbleStop, S.MacroAction_Type_RumbleStop, S.Macro_Cat_Rumble, S.MacroAction_RumbleStop_Tooltip);
                Add(MacroActionType.RumbleTrigger, S.MacroAction_Type_RumbleTrigger, S.Macro_Cat_Rumble, S.MacroAction_RumbleTrigger_Tooltip);
                Add(MacroActionType.RumbleTriggerStop, S.MacroAction_Type_RumbleTriggerStop, S.Macro_Cat_Rumble, S.MacroAction_RumbleTriggerStop_Tooltip);
                Add(MacroActionType.LightbarColor, S.MacroAction_Type_LightbarColor, S.Macro_Cat_Leds, S.MacroAction_LightbarColor_Tooltip);
                Add(MacroActionType.LightbarColorClear, S.MacroAction_Type_LightbarColorClear, S.Macro_Cat_Leds, S.MacroAction_LightbarColorClear_Tooltip);
                Add(MacroActionType.LightbarModeSet, S.MacroAction_Type_LightbarModeSet, S.Macro_Cat_Leds, S.MacroAction_LightbarModeSet_Tooltip);
                Add(MacroActionType.LightbarModeCycle, S.MacroAction_Type_LightbarModeCycle, S.Macro_Cat_Leds, S.MacroAction_LightbarModeCycle_Tooltip);
                Add(MacroActionType.GuideLedBrightness, S.MacroAction_Type_GuideLedBrightness, S.Macro_Cat_Leds, S.MacroAction_GuideLedBrightness_Tooltip);
                Add(MacroActionType.PlaySound, S.MacroAction_Type_PlaySound, S.Macro_Cat_Sound, S.MacroAction_PlaySound_Tooltip);
                Add(MacroActionType.SoundStop, S.MacroAction_Type_SoundStop, S.Macro_Cat_Sound, S.MacroAction_SoundStop_Tooltip);
                Add(MacroActionType.SystemVolume, S.Macro_SystemVolume, S.Macro_Cat_Sound, null);
                Add(MacroActionType.AppVolume, S.Macro_AppVolume, S.Macro_Cat_Sound, null);
                Add(MacroActionType.HeadphoneVolumeUp, S.MacroAction_Type_HeadphoneVolumeUp, S.Macro_Cat_Sound, S.MacroAction_HeadphoneVolumeUp_Tooltip);
                Add(MacroActionType.HeadphoneVolumeDown, S.MacroAction_Type_HeadphoneVolumeDown, S.Macro_Cat_Sound, S.MacroAction_HeadphoneVolumeDown_Tooltip);
                Add(MacroActionType.SetGyroEngaged, S.Macro_SetGyroEngaged, S.Macro_Cat_Motion, S.Macro_SetGyroEngaged_Tooltip);
                Add(MacroActionType.GyroRecenter, S.MacroAction_Type_GyroRecenter, S.Macro_Cat_Motion, S.MacroAction_GyroRecenter_Tooltip);
                Add(MacroActionType.PointerModeSet, S.MacroAction_Type_PointerModeSet, S.Macro_Cat_Motion, S.MacroAction_PointerModeSet_Tooltip);
                Add(MacroActionType.PointerModeCycle, S.MacroAction_Type_PointerModeCycle, S.Macro_Cat_Motion, S.MacroAction_PointerModeCycle_Tooltip);
                Add(MacroActionType.SwitchLayer, S.MacroAction_Type_SwitchLayer, S.Macro_Cat_Layers, S.Macro_SwitchLayer_Hint);
                Add(MacroActionType.ToggleTouchpadOverlay, S.Macro_ToggleTouchpadOverlay, S.Macro_Cat_Layers, null);
                Add(MacroActionType.RunProgram, S.MacroAction_Type_RunProgram, S.Macro_Cat_System, S.MacroAction_RunProgram_Tooltip);
                Add(MacroActionType.VoiceListenWhileHeld, S.MacroAction_Type_VoiceListenWhileHeld, S.Macro_Cat_System, S.MacroAction_VoiceListenWhileHeld_Tooltip);
                Add(MacroActionType.DisconnectController, S.MacroAction_Type_DisconnectController, S.Macro_Cat_System, S.MacroAction_DisconnectController_Tooltip);
            }
            return list;
        }
    }
}
