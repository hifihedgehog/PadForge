using HIDMaestro;
using PadForge.Engine;
using PadForge.Models2D;

namespace PadForge.Tools.OutputFamilyProbe
{
    /// <summary>The one state every family submits, and its neutral twin.
    ///
    /// <para>Lopsided on purpose, so a field read from the wrong offset or a
    /// swapped pair shows: the face button A alone, the left stick full right
    /// and half up, the right stick a quarter left and centered on Y, the left
    /// trigger at full pull and the right one at rest.</para>
    ///
    /// <para>HIDMaestro takes every stick and trigger as a value from 0 to 1,
    /// 0 at the left and the top and 0.5 at the center, and a trigger 0 at
    /// rest (HMaestroVirtualController.SubmitGamepadState and
    /// BuildRawHidState). These are
    /// the values the state below comes to on that scale, and every
    /// comparison decodes the wire back onto it.</para></summary>
    internal static class KnownState
    {
        internal const double LeftX = 1.0, LeftY = 0.25, RightX = 0.375, RightY = 0.5, LeftTrigger = 1.0, RightTrigger = 0.0;

        /// <summary>The tolerance on that 0-to-1 scale: four counts of an
        /// 8-bit field, well inside a 12-bit or 16-bit one.</summary>
        internal const double Tolerance = 4.0 / 255.0;

        /// <summary>The Gamepad a fixed-layout slot (Xbox, PlayStation)
        /// submits: XInput's convention, Y up positive, triggers 0 to 65535
        /// (GamepadTypes.cs Gamepad).</summary>
        internal static Gamepad Pad(bool neutral)
        {
            if (neutral) return default;
            return new Gamepad
            {
                Buttons = PadForge.Engine.Gamepad.A,
                ThumbLX = 32767,
                ThumbLY = 16384,
                ThumbRX = -8192,
                ThumbRY = 0,
                LeftTrigger = 65535,
                RightTrigger = 0,
            };
        }

        /// <summary>The raw surface an Extended or Nintendo slot submits, in
        /// the HID convention the raw surface keeps (Y down positive, a
        /// trigger from short.MinValue at rest to short.MaxValue at full
        /// pull, HMaestroVirtualController.BuildRawHidState), laid out by
        /// ExtendedSlotConfig.ComputeAxisLayout's interleave
        /// (as BuildRawHidState reads it), with the A button at
        /// the index the profile's own wire table gives it
        /// (NintendoPreviewMap.IndexOf) or raw button 0 on a numbered
        /// layout.</summary>
        internal static RawHidState Raw(string profileId, in CustomControllerLayout layout, bool neutral)
        {
            int axes = Math.Max(layout.Axes, layout.Sticks * 2 + layout.Triggers);
            var raw = RawHidState.Create(Math.Max(axes, 1), Math.Max(layout.Buttons, 32), Math.Max(layout.Povs, 1));
            for (int i = 0; i < raw.Axes.Length; i++) raw.Axes[i] = 0;
            for (int i = 0; i < raw.Povs.Length; i++) raw.Povs[i] = -1;

            int sticks = layout.Sticks, triggers = layout.Triggers;
            int interleave = Math.Min(sticks, triggers);
            int StickX(int g) => g < interleave ? g * 3 : g < sticks ? interleave * 3 + (g - interleave) * 2 : -1;
            int TriggerIdx(int g) => g < interleave ? g * 3 + 2
                : g < triggers ? interleave * 3 + Math.Max(0, sticks - interleave) * 2 + (g - interleave) : -1;
            void Set(int i, short v) { if (i >= 0 && i < raw.Axes.Length) raw.Axes[i] = v; }

            for (int t = 0; t < triggers; t++) Set(TriggerIdx(t), short.MinValue);
            if (neutral) return raw;

            if (sticks > 0) { Set(StickX(0), 32767); Set(StickX(0) + 1, -16384); }
            if (sticks > 1) { Set(StickX(1), -8192); Set(StickX(1) + 1, 0); }
            if (triggers > 0) Set(TriggerIdx(0), short.MaxValue);
            raw.SetButton(AButtonIndex(profileId), true);
            return raw;
        }

        /// <summary>The raw button the A role sits on.</summary>
        internal static int AButtonIndex(string profileId)
            => NintendoPreviewMap.IsLettered(profileId) ? NintendoPreviewMap.IndexOf(profileId, "ButtonA") : 0;

        /// <summary>The HIDMaestro buttons a Gamepad with A pressed comes to:
        /// HMButton.A, the bit HMaestroVirtualController.MapButtons sets for
        /// Gamepad.A.</summary>
        internal const HMButton GamepadButtons = HMButton.A;

        /// <summary>The F24 key: VK_F24 0x87, HID usage 0x73
        /// (KbmHidKeyMap.cs:98, F13 to F24 at 0x68 to 0x73), make code 0x76
        /// (KbmHidKeyMap.cs:307).</summary>
        internal const int VkF24 = 0x87;
        internal const byte F24Usage = 0x73;
        internal const ushort F24MakeCode = 0x76;
    }
}
