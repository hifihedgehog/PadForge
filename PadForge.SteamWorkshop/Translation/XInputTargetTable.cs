using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>
    /// The Xbox side of the atlas: <c>xinput_button</c> parameter spellings
    /// to PadForge mapping targets, each target's automap-identity source
    /// (the abstract descriptor whose device automap already produces the
    /// binding), and the combined-output button bit used for device-free
    /// macro triggers.
    ///
    /// <para>Target names are the PadSetting mapping-field vocabulary Step 3
    /// dispatches on (MappingRow.Target). The identity sources mirror
    /// SettingsManager.CreateDefaultPadSetting's gamepad canon (ButtonA =
    /// Button 0 ... ButtonGuide = Button 10, POV 0 for the D-pad, Axis 0-5
    /// for sticks/triggers) expressed as the abstract Gamepad family, which
    /// canonicalizes to exactly those descriptors
    /// (SourceCoercion.GamepadAliasTable).</para>
    /// </summary>
    public static class XInputTargetTable
    {
        public sealed class XInputTarget
        {
            /// <summary>MappingRow.Target name, e.g. "ButtonA" / "LeftTrigger".</summary>
            public string Target { get; }

            /// <summary>Abstract source whose automap already feeds this
            /// target ("Gamepad ButtonA" for ButtonA, ...).</summary>
            public string IdentitySource { get; }

            /// <summary>Gamepad.Buttons bit for the combined Xbox output, or
            /// 0 for the trigger axes.</summary>
            public ushort XboxButtonBit { get; }

            /// <summary>True for LeftTrigger/RightTrigger (axis targets).</summary>
            public bool IsTriggerAxis { get; }

            /// <summary>True for the LSTICK_/RSTICK_ direction params
            /// (v13): the binding drives one direction of a virtual thumb
            /// stick, so it lowers to a bipolar axis row on
            /// <see cref="Target"/> instead of a button row.</summary>
            public bool IsStickAxis { get; }

            /// <summary>Output polarity for <see cref="IsStickAxis"/>
            /// params: true when the direction is the NEGATIVE end of the
            /// emitted row's SDL-convention axis value (up, left). The row
            /// value convention is SDL "+X right, +Y down", and Step 3's
            /// WriteBipolarAxisTarget negates Y onto the XInput thumb.</summary>
            public bool StickAxisNegative { get; }

            public XInputTarget(string target, string identitySource, ushort bit,
                bool isTriggerAxis = false, bool isStickAxis = false, bool stickAxisNegative = false)
            {
                Target = target;
                IdentitySource = identitySource;
                XboxButtonBit = bit;
                IsTriggerAxis = isTriggerAxis;
                IsStickAxis = isStickAxis;
                StickAxisNegative = stickAxisNegative;
            }
        }

        private static readonly Dictionary<string, XInputTarget> ByParam = Build();

        private static Dictionary<string, XInputTarget> Build()
        {
            var a = new XInputTarget("ButtonA", "Gamepad ButtonA", Gamepad.A);
            var b = new XInputTarget("ButtonB", "Gamepad ButtonB", Gamepad.B);
            var x = new XInputTarget("ButtonX", "Gamepad ButtonX", Gamepad.X);
            var y = new XInputTarget("ButtonY", "Gamepad ButtonY", Gamepad.Y);
            var lb = new XInputTarget("LeftShoulder", "Gamepad LeftShoulder", Gamepad.LEFT_SHOULDER);
            var rb = new XInputTarget("RightShoulder", "Gamepad RightShoulder", Gamepad.RIGHT_SHOULDER);
            var back = new XInputTarget("ButtonBack", "Gamepad ButtonBack", Gamepad.BACK);
            var start = new XInputTarget("ButtonStart", "Gamepad ButtonStart", Gamepad.START);
            var guide = new XInputTarget("ButtonGuide", "Gamepad ButtonGuide", Gamepad.GUIDE);
            var ls = new XInputTarget("LeftThumbButton", "Gamepad LeftStick", Gamepad.LEFT_THUMB);
            var rs = new XInputTarget("RightThumbButton", "Gamepad RightStick", Gamepad.RIGHT_THUMB);
            var du = new XInputTarget("DPadUp", "Gamepad DPadUp", Gamepad.DPAD_UP);
            var dd = new XInputTarget("DPadDown", "Gamepad DPadDown", Gamepad.DPAD_DOWN);
            var dl = new XInputTarget("DPadLeft", "Gamepad DPadLeft", Gamepad.DPAD_LEFT);
            var dr = new XInputTarget("DPadRight", "Gamepad DPadRight", Gamepad.DPAD_RIGHT);
            var lt = new XInputTarget("LeftTrigger", "Gamepad LeftTrigger", 0, isTriggerAxis: true);
            var rt = new XInputTarget("RightTrigger", "Gamepad RightTrigger", 0, isTriggerAxis: true);

            // Virtual-stick direction params (v13). Steam's serializer
            // vocabulary (the steamclient.dll token table: ... START,
            // SELECT, STEAM, DPAD_*, LSTICK_UP/DOWN/LEFT/RIGHT,
            // RSTICK_UP/DOWN/LEFT/RIGHT) binds a button to one direction
            // of the emulated stick. The wild corpus carries them
            // (fixture 3725174032: "xinput_button LSTICK_DOWN" on a
            // D-pad member) and the owner's import report hit LSTICK_UP.
            // Row value convention is SDL "+X right, +Y down" (the same
            // frame the joystick_move rows emit, before
            // WriteBipolarAxisTarget negates Y onto the XInput thumb),
            // so up and left are the negative ends.
            var lsu = new XInputTarget("LeftThumbAxisY", "Gamepad LeftStickY", 0, isStickAxis: true, stickAxisNegative: true);
            var lsd = new XInputTarget("LeftThumbAxisY", "Gamepad LeftStickY", 0, isStickAxis: true);
            var lsl = new XInputTarget("LeftThumbAxisX", "Gamepad LeftStickX", 0, isStickAxis: true, stickAxisNegative: true);
            var lsr = new XInputTarget("LeftThumbAxisX", "Gamepad LeftStickX", 0, isStickAxis: true);
            var rsu = new XInputTarget("RightThumbAxisY", "Gamepad RightStickY", 0, isStickAxis: true, stickAxisNegative: true);
            var rsd = new XInputTarget("RightThumbAxisY", "Gamepad RightStickY", 0, isStickAxis: true);
            var rsl = new XInputTarget("RightThumbAxisX", "Gamepad RightStickX", 0, isStickAxis: true, stickAxisNegative: true);
            var rsr = new XInputTarget("RightThumbAxisX", "Gamepad RightStickX", 0, isStickAxis: true);

            // Steam Input spells these in several cases/aliases across
            // config vintages; the dictionary is case-insensitive so only
            // spelling variants need rows.
            return new Dictionary<string, XInputTarget>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = a,
                ["b"] = b,
                ["x"] = x,
                ["y"] = y,
                ["shoulder_left"] = lb,
                ["shoulder_right"] = rb,
                ["select"] = back,
                ["back"] = back,
                ["start"] = start,
                ["guide"] = guide,
                // The serializer's own spelling for the Guide button
                // (steamclient.dll token table: START, SELECT, STEAM).
                ["steam"] = guide,
                ["joystick_left"] = ls,
                ["joystick_right"] = rs,
                ["trigger_left"] = lt,
                ["trigger_right"] = rt,
                ["dpad_up"] = du,
                ["dpad_down"] = dd,
                ["dpad_left"] = dl,
                ["dpad_right"] = dr,
                ["lstick_up"] = lsu,
                ["lstick_down"] = lsd,
                ["lstick_left"] = lsl,
                ["lstick_right"] = lsr,
                ["rstick_up"] = rsu,
                ["rstick_down"] = rsd,
                ["rstick_left"] = rsl,
                ["rstick_right"] = rsr,
            };
        }

        public static bool TryResolve(string param, out XInputTarget target)
            => ByParam.TryGetValue((param ?? "").Trim(), out target);

        // Reverse index: MappingRow.Target -> the Xbox output bit that target
        // feeds. ByParam is keyed by the STEAM param ("a", "dpad_up"), which
        // answers "what does this binding mean". This answers the opposite
        // question, "does any emitted row actually feed bit X", which is what a
        // macro triggering on a combined-output bit needs in order to know its
        // trigger is live. Several params share one target, hence Distinct.
        private static readonly Dictionary<string, ushort> BitByTarget =
            ByParam.Values
                   .GroupBy(t => t.Target, StringComparer.OrdinalIgnoreCase)
                   .ToDictionary(g => g.Key, g => g.First().XboxButtonBit,
                                 StringComparer.OrdinalIgnoreCase);

        /// <summary>The Xbox output bit a <see cref="MappingRow.Target"/> name
        /// feeds, or 0 when the target is not a combined-output button (the
        /// trigger axes, or any non-Xbox target).</summary>
        public static ushort BitForTarget(string target)
            => BitByTarget.TryGetValue((target ?? "").Trim(), out var bit) ? bit : (ushort)0;
    }
}
