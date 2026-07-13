using System.Collections.Generic;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Model
{
    /// <summary>
    /// A Steam Input activator (<c>Full_Press</c>, <c>release</c>, <c>Soft_Press</c>,
    /// <c>Long_Press</c>, <c>Double_Press</c>, etc.) attached to an input. Holds the
    /// bindings that fire for that press style plus per-activator settings.
    /// </summary>
    public sealed class SteamInputActivator
    {
        public string Type { get; }

        public IReadOnlyList<SteamInputBinding> Bindings { get; }

        public IReadOnlyDictionary<string, string> Settings { get; }

        private SteamInputActivator(string type, IReadOnlyList<SteamInputBinding> bindings,
            IReadOnlyDictionary<string, string> settings)
        {
            Type = type;
            Bindings = bindings;
            Settings = settings;
        }

        public static SteamInputActivator FromVdf(string type, VdfNode node)
        {
            var bindings = new List<SteamInputBinding>();
            foreach (var b in node["bindings"].Multi("binding"))
            {
                var raw = b.AsString;
                if (raw != null)
                    bindings.Add(SteamInputBinding.Parse(raw));
            }

            return new SteamInputActivator(type, bindings, VdfModelHelpers.ScalarSettings(node["settings"]));
        }
    }
}
