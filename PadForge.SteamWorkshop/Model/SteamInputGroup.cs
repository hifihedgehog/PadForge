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

        /// <summary>The author's display name for the group ("name" in the
        /// VDF; radial / touch menus often carry one, e.g. "Systems" in
        /// corpus 3451446931). Empty when absent.</summary>
        public string Name { get; }

        public IReadOnlyDictionary<string, SteamInputInput> Inputs { get; }

        public IReadOnlyDictionary<string, string> Settings { get; }

        /// <summary>The group's <c>gameactions</c> pairs: action-set name to
        /// the in-game (Steam Input API) action the group drives while that
        /// set is active, e.g. <c>TacticalControls / TacticalCamera</c> in
        /// Valve's XCOM 2 config. Analog sibling of <c>game_action</c>
        /// bindings: the linkage has no game-side hook outside Steam, so
        /// the translator counts it into the same per-preset skip.
        /// Empty blocks (common in official configs) contribute nothing.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> GameActions { get; }

        /// <summary>
        /// The group this one inlines when <see cref="Mode"/> is <c>reference</c>, taken from
        /// the <c>referenced_mode</c> setting. Null when the group defines its own bindings.
        /// </summary>
        public int? ReferencedGroupId { get; }

        private SteamInputGroup(int id, string mode, string name,
            IReadOnlyDictionary<string, SteamInputInput> inputs,
            IReadOnlyDictionary<string, string> settings,
            IReadOnlyList<KeyValuePair<string, string>> gameActions, int? referencedGroupId)
        {
            Id = id;
            Mode = mode;
            Name = name;
            Inputs = inputs;
            Settings = settings;
            GameActions = gameActions;
            ReferencedGroupId = referencedGroupId;
        }

        public static SteamInputGroup FromVdf(VdfNode node)
        {
            var id = (int)(node["id"].AsInt ?? -1);
            var mode = node["mode"].AsString;
            var name = node["name"].AsString ?? "";

            var inputs = new Dictionary<string, SteamInputInput>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var kv in node["inputs"].Children)
            {
                if (!inputs.ContainsKey(kv.Key))
                    inputs[kv.Key] = SteamInputInput.FromVdf(kv.Key, kv.Value);
            }

            var settings = VdfModelHelpers.ScalarSettings(node["settings"]);

            List<KeyValuePair<string, string>> gameActions = null;
            foreach (var kv in node["gameactions"].Children)
            {
                if (!kv.Value.IsValue) continue;
                (gameActions ??= new List<KeyValuePair<string, string>>())
                    .Add(new KeyValuePair<string, string>(kv.Key, kv.Value.AsString ?? ""));
            }

            int? referenced = null;
            if (settings.TryGetValue("referenced_mode", out var rm) &&
                int.TryParse(rm, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rmv))
            {
                referenced = rmv;
            }

            return new SteamInputGroup(id, mode, name, inputs, settings,
                (IReadOnlyList<KeyValuePair<string, string>>)gameActions
                    ?? System.Array.Empty<KeyValuePair<string, string>>(),
                referenced);
        }
    }
}
