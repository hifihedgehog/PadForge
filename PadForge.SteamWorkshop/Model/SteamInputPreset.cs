using System.Collections.Generic;
using System.Globalization;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Model
{
    /// <summary>
    /// A Steam Input preset (action set). Maps each group id to its source-binding string
    /// (for example <c>"joystick active"</c> or <c>"right_trackpad inactive modeshift"</c>),
    /// which selects the physical slot a group is attached to and whether it is active.
    /// </summary>
    public sealed class SteamInputPreset
    {
        public int Id { get; }

        public string Name { get; }

        public IReadOnlyDictionary<int, string> GroupSourceBindings { get; }

        private SteamInputPreset(int id, string name, IReadOnlyDictionary<int, string> groupSourceBindings)
        {
            Id = id;
            Name = name;
            GroupSourceBindings = groupSourceBindings;
        }

        public static SteamInputPreset FromVdf(VdfNode node)
        {
            var id = (int)(node["id"].AsInt ?? -1);
            var name = node["name"].AsString;

            var bindings = new Dictionary<int, string>();
            foreach (var kv in node["group_source_bindings"].Children)
            {
                if (kv.Value.IsValue &&
                    int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var groupId) &&
                    !bindings.ContainsKey(groupId))
                {
                    bindings[groupId] = kv.Value.AsString;
                }
            }

            return new SteamInputPreset(id, name, bindings);
        }
    }
}
