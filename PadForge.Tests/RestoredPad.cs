using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>Builds a pad view model the way a restore does. The type step
    /// stamps the category default and the preset is stamped before it is
    /// assigned, so the ProfileId setter never reads the build as a live
    /// re-target, which translates, fills and merges the shared statics. The
    /// slot's wire stamp is put back afterward, so no build leaks one into a
    /// later test. Callers still belong in the SettingsManagerStatics
    /// collection: the stamp is shared while the build runs.</summary>
    internal static class RestoredPad
    {
        public static PadViewModel Build(int padIndex, VirtualControllerType type, string profile = null)
        {
            string stamp = SettingsManager.GetWireStamp(padIndex);
            try
            {
                var vm = new PadViewModel(padIndex) { OutputType = type };
                if (profile != null)
                {
                    SettingsManager.StampNintendoWire(padIndex, profile);
                    vm.ProfileId = profile;
                }
                return vm;
            }
            finally { SettingsManager.StampNintendoWire(padIndex, stamp); }
        }
    }
}
