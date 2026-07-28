using System;
using System.Text;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>
    /// <para>Steam's config format is written in wire tokens: SCREAMING_SNAKE
    /// for verbs and keys (<c>SET_LED</c>, <c>LEFT_CONTROL</c>), lower_snake
    /// for slots and inputs (<c>right_joystick</c>, <c>dpad_east</c>). That
    /// vocabulary is correct in the file and wrong in front of a person, and
    /// the translator emits plenty of strings a person reads: macro names and
    /// shift-layer names written into the imported PROFILE, plus the report
    /// lines the browse dialog renders.</para>
    /// <para>This is the one place that converts. Slot tokens go through
    /// <see cref="PhysicalSlotResolver.SlotDisplayName(string)"/> and key
    /// tokens through <see cref="SteamInputVkTable.KeyDisplayName"/>, both of
    /// which name the thing the way the rest of the app names it. Everything
    /// else falls back to <see cref="SpellToken"/>, which only fixes the
    /// casing and separators and never guesses at meaning.</para>
    /// </summary>
    public static class SteamVocabulary
    {
        /// <summary>A Steam token as words: "LEFT_CONTROL" becomes "Left
        /// Control", "dpad_east" becomes "Dpad East", "KEYPAD_5" becomes
        /// "Keypad 5". The underscore already marks the word boundaries, so
        /// this only restores the casing. A single character (a letter or
        /// digit key) and an already-mixed-case word keep their shape.</summary>
        public static string SpellToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return token;
            var words = token.Trim().Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder(token.Length + 2);
            foreach (var w in words)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w.Length == 1 || w.ToUpperInvariant() != w
                    ? w
                    : char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant());
            }
            return sb.ToString();
        }

        /// <summary><para>Names the control a macro hangs off, for the
        /// parenthetical in the macro's saved name ("Set LED (Left Trigger)").
        /// </para>
        /// <para>Prefers the RESOLVED descriptor over the Steam input token,
        /// and that preference is the whole point rather than a nicety.
        /// Steam's input names are POSITIONAL and mean different hardware on
        /// different slots: <c>dpad_north</c> is the Y button on a
        /// button_diamond, the up direction on a dpad, an up-swipe wedge on a
        /// trackpad, and a forward lean on a stick. Spelling the token would
        /// therefore print "Dpad North" on a macro bound to the Y button.
        /// The descriptor already carries what the resolver decided, so it is
        /// right in every context; the token is only a fallback for a source
        /// that resolved to nothing.</para></summary>
        public static string MemberLabel(string descriptor, string inputToken)
        {
            if (!string.IsNullOrWhiteSpace(descriptor))
            {
                // "Gamepad ButtonY" reads as "ButtonY" here: the macro name
                // already sits inside PadForge, so the qualifier is noise.
                const string prefix = "Gamepad ";
                return descriptor.StartsWith(prefix, StringComparison.Ordinal)
                    ? descriptor.Substring(prefix.Length)
                    : descriptor;
            }
            return SpellToken(inputToken);
        }
    }
}
