using System;
using System.Collections.Generic;

namespace PadForge.SteamWorkshop.Model
{
    /// <summary>
    /// A single Steam Input binding, parsed from a <c>binding</c> value string such as
    /// <c>"key_press F5, Quicksave, ghost_030_inv_0100.png #000000 #ad0000"</c> or
    /// <c>"xinput_button A"</c>. The wire format is
    /// <c>&lt;type&gt; &lt;param...&gt;, &lt;action label&gt;, &lt;icon&gt; &lt;colors&gt;</c>
    /// with comma-separated fields. The corpus carries the icon both with
    /// the colors inside its own field ("icon.png #232323 #E4E4E4") and
    /// with the colors in a fourth field ("icon.png, #232323 #E4E4E4").
    /// </summary>
    public sealed class SteamInputBinding
    {
        /// <summary>The binding kind, e.g. <c>key_press</c>, <c>xinput_button</c>, <c>controller_action</c>.</summary>
        public string Type { get; }

        /// <summary>The parameter(s) following the type token in the first field, e.g. <c>F5</c> or <c>CHANGE_PRESET 2 1 1</c>.</summary>
        public string Param { get; }

        /// <summary>The human-readable action label (second comma field), or null.</summary>
        public string ActionName { get; }

        /// <summary>The cell icon reference: the first whitespace token of
        /// the fields past the label that ends in ".png" (e.g.
        /// <c>ghost_030_inv_0100.png</c>), or null when the binding
        /// carries none. Colors that share the icon field are not part of
        /// the reference and are dropped here (they stay in <see cref="Raw"/>).</summary>
        public string Icon { get; }

        /// <summary>The full, unmodified binding string. Preserves icon/color fields not otherwise modeled.</summary>
        public string Raw { get; }

        /// <summary>
        /// Explicit <c>key=value</c> flags found anywhere in the binding fields. Empty for
        /// the standard positional binding syntax; reserved for schemas that carry named flags.
        /// </summary>
        public IReadOnlyDictionary<string, string> Flags { get; }

        private SteamInputBinding(string type, string param, string actionName, string icon,
            string raw, IReadOnlyDictionary<string, string> flags)
        {
            Type = type;
            Param = param;
            ActionName = actionName;
            Icon = icon;
            Raw = raw;
            Flags = flags;
        }

        /// <summary>Parses a raw <c>binding</c> value string into its fields.</summary>
        public static SteamInputBinding Parse(string raw)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));

            var fields = raw.Split(',');
            var head = fields[0].Trim();

            string type;
            string param;
            var firstSpace = head.IndexOf(' ');
            if (firstSpace < 0)
            {
                type = head;
                param = string.Empty;
            }
            else
            {
                type = head.Substring(0, firstSpace);
                param = head.Substring(firstSpace + 1).Trim();
            }

            var actionName = fields.Length > 1 ? fields[1].Trim() : null;

            // Icon: first ".png"-suffixed whitespace token past the label
            // field. Handles both corpus shapes (colors inside the icon
            // field and colors in a field of their own).
            string icon = null;
            for (int f = 2; f < fields.Length && icon == null; f++)
            {
                foreach (var token in fields[f].Split(' ', '\t'))
                {
                    if (token.Length > 4
                        && token.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        icon = token;
                        break;
                    }
                }
            }

            Dictionary<string, string> flags = null;
            foreach (var field in fields)
            {
                var eq = field.IndexOf('=');
                if (eq > 0)
                {
                    var k = field.Substring(0, eq).Trim();
                    var v = field.Substring(eq + 1).Trim();
                    if (k.Length > 0)
                        (flags ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[k] = v;
                }
            }

            return new SteamInputBinding(
                type,
                param,
                actionName,
                icon,
                raw,
                (IReadOnlyDictionary<string, string>)flags ?? EmptyFlags);
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyFlags =
            new Dictionary<string, string>(0);
    }
}
