using System;

namespace PadForge.SteamWorkshop
{
    /// <summary>
    /// The single opt-in signal the Steam Workshop clients consult before doing any network
    /// work. PadForge.App implements this over its "Community Configs" settings toggle and
    /// injects it, so this assembly never references the App. When the signal is false, every
    /// client constructor throws, giving a hard privacy gate independent of the UI.
    /// </summary>
    public interface ISteamWorkshopGate
    {
        /// <summary>True when the user has opted in to community config lookup.</summary>
        bool IsCommunityConfigLookupEnabled { get; }
    }

    /// <summary>
    /// Adapts a <see cref="Func{Boolean}"/> to <see cref="ISteamWorkshopGate"/> so callers can
    /// wire the opt-in with a lambda over their own settings, for example
    /// <c>new DelegateSteamWorkshopGate(() =&gt; settings.EnableCommunityConfigLookup)</c>.
    /// </summary>
    public sealed class DelegateSteamWorkshopGate : ISteamWorkshopGate
    {
        private readonly Func<bool> _isEnabled;

        public DelegateSteamWorkshopGate(Func<bool> isEnabled)
        {
            _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        }

        public bool IsCommunityConfigLookupEnabled => _isEnabled();
    }

    internal static class SteamWorkshopGuard
    {
        public const string DisabledMessage =
            "Community config lookup is disabled in PadForge settings. Enable it before using the Steam Workshop clients.";

        /// <summary>Throws if the gate is null or the opt-in is off. Called from every client constructor.</summary>
        public static void EnsureEnabled(ISteamWorkshopGate gate)
        {
            if (gate == null) throw new ArgumentNullException(nameof(gate));
            if (!gate.IsCommunityConfigLookupEnabled)
                throw new InvalidOperationException(DisabledMessage);
        }
    }
}
