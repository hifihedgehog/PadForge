using System;
using System.Collections.Concurrent;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// The names of the built-in plugins' devices (#600) in the language in
    /// use, by the device's identity. PadForge words these names itself, so
    /// they follow a change of the interface language as the merged
    /// keyboards' and the Touchpad Overlay's do: the Devices page and every
    /// reader of <see cref="InputService.LocalizedDeviceName"/> ask here at
    /// the time they show the name, a row that is offline included, and a
    /// live device is renamed when the language changes
    /// (<see cref="BuiltInPlugins.RefreshNames"/>).
    ///
    /// <para>Safe from any thread.</para>
    /// </summary>
    internal static class BuiltInDeviceNames
    {
        private static readonly ConcurrentDictionary<Guid, Func<string>> Names = new();

        /// <summary>Names the device of <paramref name="instanceGuid"/> by
        /// <paramref name="name"/>, which is read each time.</summary>
        internal static void Set(Guid instanceGuid, Func<string> name) => Names[instanceGuid] = name;

        /// <summary>Forgets the name <paramref name="name"/> gave, and only
        /// that one.</summary>
        internal static void Forget(Guid instanceGuid, Func<string> name)
            => ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<Guid, Func<string>>>)Names)
                .Remove(new System.Collections.Generic.KeyValuePair<Guid, Func<string>>(instanceGuid, name));

        /// <summary>The device's name in the language in use, when it is a
        /// built-in plugin's.</summary>
        internal static bool TryName(Guid instanceGuid, out string name)
        {
            name = Names.TryGetValue(instanceGuid, out var of) ? of() : null;
            return !string.IsNullOrEmpty(name);
        }
    }
}
