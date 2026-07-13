using System;
using System.Collections.Generic;
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

            public XInputTarget(string target, string identitySource, ushort bit, bool isTriggerAxis = false)
            {
                Target = target;
                IdentitySource = identitySource;
                XboxButtonBit = bit;
                IsTriggerAxis = isTriggerAxis;
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
                ["joystick_left"] = ls,
                ["joystick_right"] = rs,
                ["trigger_left"] = lt,
                ["trigger_right"] = rt,
                ["dpad_up"] = du,
                ["dpad_down"] = dd,
                ["dpad_left"] = dl,
                ["dpad_right"] = dr,
            };
        }

        public static bool TryResolve(string param, out XInputTarget target)
            => ByParam.TryGetValue((param ?? "").Trim(), out target);
    }
}
