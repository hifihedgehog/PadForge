using HIDMaestro;
using PadForge.Engine;
using PadForge.Models2D;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Turns a Switch 2 Pro slot's raw rows into the state HIDMaestro reads
    /// for that pad, on both of its profiles.
    ///
    /// <para>HIDMaestro reads these two profiles' buttons by NAME, and the
    /// raw surface hands it button N as bit N. The plain profile's report
    /// 0x09 lists its bits by HMButton name (profiles/nintendo/
    /// switch2-pro.json, the button-mask fields at bytes 3, 4 and 5), and
    /// VendorBlobCodec sets a bit while the flag of that name is set.
    /// Switch2ProPacker.BuildBody reads the same names for the composite
    /// persona (HIDMaestro 1.11.0, HIDMaestro#66). Both take ZL and ZR from
    /// the trigger axes and the D-pad from the hat. The first Pro Controller
    /// is the other kind: SwitchProPacker reads bit N as button N, so its
    /// slot's raw mask goes out as it stands.</para>
    ///
    /// <para>Handed that raw mask, a Switch 2 Pro slot pressed the wrong
    /// control from every row. Bit 0 is HMButton.A, so the B row pressed A.
    /// The R row pressed L, ZR pressed R, Plus pressed Minus, the D-pad rows
    /// pressed the stick clicks and Home, and Home, Capture, GR, GL and C
    /// pressed nothing. Running HIDMaestro's two encoders on the slot's own
    /// state showed it: no row of the 21 reached its wire bit.</para>
    ///
    /// <para>Each row is resolved through
    /// <see cref="NintendoPreviewMap.IndexOf"/>, as the Valve packers' slots
    /// are, so the wire order stays written down in one place.</para>
    /// </summary>
    internal sealed class Switch2ProStateMap
    {
        /// <summary>The rows HIDMaestro reads as a named button. The D-pad
        /// and ZL / ZR, the six rows left out, are not buttons to it.</summary>
        private static readonly (string Role, HMButton Button)[] NamedRoles =
        {
            ("ButtonB", HMButton.B), ("ButtonA", HMButton.A),
            ("ButtonY", HMButton.Y), ("ButtonX", HMButton.X),
            ("RightShoulder", HMButton.RightBumper), ("LeftShoulder", HMButton.LeftBumper),
            ("ButtonStart", HMButton.Start), ("ButtonBack", HMButton.Back),
            ("RightThumbButton", HMButton.RightStick), ("LeftThumbButton", HMButton.LeftStick),
            ("ButtonGuide", HMButton.Guide), ("ButtonShare", HMButton.Share),
            ("RightPaddle", HMButton.RightPaddle), ("LeftPaddle", HMButton.LeftPaddle),
            ("ButtonC", HMButton.Misc1),
        };

        // Indexed by raw row. HMButton.None on the six rows above.
        private readonly HMButton[] _named;
        private readonly int _up, _down, _left, _right, _zl, _zr;

        private Switch2ProStateMap(string profileId)
        {
            _named = new HMButton[NintendoPreviewMap.ButtonCount(profileId)];
            foreach (var (role, button) in NamedRoles)
            {
                int row = NintendoPreviewMap.IndexOf(profileId, role);
                if (row >= 0) _named[row] = button;
            }
            _up = NintendoPreviewMap.IndexOf(profileId, "DPadUp");
            _down = NintendoPreviewMap.IndexOf(profileId, "DPadDown");
            _left = NintendoPreviewMap.IndexOf(profileId, "DPadLeft");
            _right = NintendoPreviewMap.IndexOf(profileId, "DPadRight");
            _zl = NintendoPreviewMap.IndexOf(profileId, "LeftTrigger");
            _zr = NintendoPreviewMap.IndexOf(profileId, "RightTrigger");
        }

        /// <summary>The map for a Switch 2 Pro profile, or null for any
        /// other. The family decides, so the plain profile and the persona
        /// share it.</summary>
        internal static Switch2ProStateMap ForProfile(string profileId) =>
            NintendoPreviewMap.FamilyOf(profileId) == NintendoPreviewMap.Family.Switch2Pro
                ? new Switch2ProStateMap(profileId)
                : null;

        /// <summary>One frame's rows as HIDMaestro's named buttons, the
        /// D-pad as XInput direction bits for the hat, and whether ZL and
        /// ZR are held.</summary>
        internal void Read(in RawHidState raw, out HMButton buttons, out ushort dpad, out bool zl, out bool zr)
        {
            uint[] words = raw.Buttons;

            var named = HMButton.None;
            for (int row = 0; row < _named.Length; row++)
                if (_named[row] != HMButton.None && Held(words, row))
                    named |= _named[row];
            buttons = named;

            ushort directions = 0;
            if (Held(words, _up)) directions |= Gamepad.DPAD_UP;
            if (Held(words, _down)) directions |= Gamepad.DPAD_DOWN;
            if (Held(words, _left)) directions |= Gamepad.DPAD_LEFT;
            if (Held(words, _right)) directions |= Gamepad.DPAD_RIGHT;
            dpad = directions;

            zl = Held(words, _zl);
            zr = Held(words, _zr);
        }

        private static bool Held(uint[] words, int row) =>
            row >= 0 && words != null && (row >> 5) < words.Length
            && (words[row >> 5] & (1u << (row & 31))) != 0;
    }
}
