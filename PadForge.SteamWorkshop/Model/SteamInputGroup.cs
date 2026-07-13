using System.Collections.Generic;
using System.Globalization;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Model
{
    /// <summary>
    /// A Steam Input group: a container that binds one physical slot (stick, trackpad,
    /// trigger, button cluster) in a particular <see cref="Mode"/> (<c>trigger</c>,
    /// <c>dpad</c>, <c>four_buttons</c>, <c>joystick_mouse</c>, <c>reference</c>, etc.).
    /// </summary>
    public sealed class SteamInputGroup
    {
        public int Id { get; }

        public string Mode { get; }

        public IReadOnlyDictionary<string, SteamInputInput> Inputs { get; }

        public IReadOnlyDictionary<string, string> Settings { get; }

        /// <summary>
        /// The group this one inlines when <see cref="Mode"/> is <c>reference</c>, taken from
        /// the <c>referenced_mode</c> setting. Null when the group defines its own bindings.
        /// </summary>
        public int? ReferencedGroupId { get; }

        private SteamInputGroup(int id, string mode, IReadOnlyDictionary<string, SteamInputInput> inputs,
            IReadOnlyDictionary<string, string> settings, int? referencedGroupId)
        {
            Id = id;
            Mode = mode;
            Inputs = inputs;
            Settings = settings;
            ReferencedGroupId = referencedGroupId;
        }

        public static SteamInputGroup FromVdf(VdfNode node)
        {
            var id = (int)(node["id"].AsInt ?? -1);
            var mode = node["mode"].AsString;

            var inputs = new Dictionary<string, SteamInputInput>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var kv in node["inputs"].Children)
            {
                if (!inputs.ContainsKey(kv.Key))
                    inputs[kv.Key] = SteamInputInput.FromVdf(kv.Key, kv.Value);
            }

            var settings = VdfModelHelpers.ScalarSettings(node["settings"]);

            int? referenced = null;
            if (settings.TryGetValue("referenced_mode", out var rm) &&
                int.TryParse(rm, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rmv))
            {
                referenced = rmv;
            }

            return new SteamInputGroup(id, mode, inputs, settings, referenced);
        }
    }
}
