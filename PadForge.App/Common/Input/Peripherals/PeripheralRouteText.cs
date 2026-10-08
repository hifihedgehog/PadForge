using System;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The line a peripheral row's tab shows (#494): where its output goes
    /// and whether that path answers, in place of the Dashboard status lines
    /// the global switches carried. Built on the UI thread from the link
    /// table, the backend states and the found HID++ units.
    /// </summary>
    internal static class PeripheralRouteText
    {
        /// <summary>The Force Feedback tab's line for a haptic row, or null
        /// for any other row.</summary>
        internal static string Haptics(UserDevice ud)
        {
            if (ud == null) return null;
            var s = Strings.Instance;
            var links = PeripheralOutputs.Links.For(ud.InstanceGuid);
            if (links == null || links.Haptics.Length == 0)
            {
                // The record says it has haptics, and no path answers now.
                if (!ud.HasPeripheralHaptics) return null;
                // The Sensa row links only where the engine loads and Synapse
                // is installed.
                if (ud.InstanceGuid == PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa))
                    return PlatformSupport.SensaAvailable
                        ? s.Pad_ForceFeedback_RouteSensaWaiting
                        : s.Common_NotAvailableOnArm64;
                // A Logitech device asleep, or not plugged in.
                return ud.VendorId == PeripheralLinker.LogitechVid ? s.Pad_ForceFeedback_RouteHidppAsleep : null;
            }

            var path = links.Haptics[0];
            switch (path.Family)
            {
                case OutputFamily.HidppUnit:
                {
                    var unit = PeripheralOutputs.Hidpp.Units.FirstOrDefault(u => u.Key == path.Key);
                    string name = string.IsNullOrWhiteSpace(unit?.Name) ? ud.ResolvedName : unit.Name;
                    return unit != null && !unit.FeedbackEnabled
                        ? string.Format(s.Pad_ForceFeedback_RouteHidppFeedbackOff, name)
                        : string.Format(s.Pad_ForceFeedback_RouteHidpp, name);
                }
                case OutputFamily.GameSenseTactile:
                {
                    if (PeripheralOutputs.StateOf(OutputFamily.GameSenseTactile) == BackendState.Waiting)
                        return s.Pad_ForceFeedback_RouteGameSenseWaiting;
                    Func<Guid, int> playerOf = PeripheralOutputs.HapticRulingPlayer;
                    if (PeripheralOutputs.TryResolveHapticRuler(path, playerOf, out var ruler) && ruler != ud.InstanceGuid)
                    {
                        int player = playerOf(ruler);
                        if (player > 0 && player < PeripheralOutputs.PeerDrivenPlayer)
                            return string.Format(s.Pad_ForceFeedback_RouteGameSenseShared, player);
                    }
                    return s.Pad_ForceFeedback_RouteGameSense;
                }
                case OutputFamily.Interhaptics:
                    return PeripheralOutputs.StateOf(OutputFamily.Interhaptics) == BackendState.Waiting
                        ? s.Pad_ForceFeedback_RouteSensaWaiting
                        : s.Pad_ForceFeedback_RouteSensa;
                default:
                    return null;
            }
        }
    }
}
