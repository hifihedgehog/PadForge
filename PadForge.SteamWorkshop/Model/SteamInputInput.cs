using System.Collections.Generic;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Model
{
    /// <summary>
    /// A named physical input within a group (for example <c>button_a</c>, <c>dpad_north</c>,
    /// <c>click</c>, <c>touch_menu_button_0</c>). Carries its activators and the list of
    /// activator types that are explicitly disabled on it.
    /// </summary>
    public sealed class SteamInputInput
    {
        public string Name { get; }

        public IReadOnlyList<SteamInputActivator> Activators { get; }

        public IReadOnlyList<string> DisabledActivatorTypes { get; }

        private SteamInputInput(string name, IReadOnlyList<SteamInputActivator> activators,
            IReadOnlyList<string> disabledActivatorTypes)
        {
            Name = name;
            Activators = activators;
            DisabledActivatorTypes = disabledActivatorTypes;
        }

        public static SteamInputInput FromVdf(string name, VdfNode node)
        {
            var activators = new List<SteamInputActivator>();
            foreach (var kv in node["activators"].Children)
                activators.Add(SteamInputActivator.FromVdf(kv.Key, kv.Value));

            var disabled = new List<string>();
            foreach (var kv in node["disabled_activators"].Children)
                disabled.Add(kv.Key);

            return new SteamInputInput(name, activators, disabled);
        }
    }
}
