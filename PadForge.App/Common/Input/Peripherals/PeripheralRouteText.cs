using System;
using System.Linq;
using System.Runtime.InteropServices;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The line a peripheral row's tab shows (#494): where its output goes
    /// and whether that path answers, in place of the Dashboard status lines
    /// the global switches carried. Built on the UI thread from the link
    /// table, the backend states and the found HID++ and direct units. While
    /// the engine is stopped every line is empty, since nothing reaches any
    /// device then (<see cref="PeripheralOutputs.Running"/>), but for the
    /// Razer Sensa row's platform limit, which no start lifts
    /// (<see cref="Haptics(UserDevice, bool)"/>).
    /// </summary>
    internal static class PeripheralRouteText
    {
        /// <summary>The Force Feedback tab's line for a haptic row, or null
        /// for any other row. <paramref name="slotLive"/> says whether the
        /// tab's controller rumbles anything
        /// (<see cref="PeripheralOutputs.SlotLive"/>): one whose controller is
        /// gone sends the row no rumble, so it names no route. A Razer Sensa
        /// row in a build that cannot load its engine reads that platform's
        /// limit whatever the engine or the controller does: the line is no
        /// route, and neither lifts the limit.</summary>
        internal static string Haptics(UserDevice ud, bool slotLive = true)
            => Haptics(ud, slotLive, PlatformSupport.SensaAvailable);

        /// <summary><see cref="Haptics(UserDevice, bool)"/> with the
        /// platform's answer handed in, so a bench with one architecture
        /// reaches the line an ARM64 process shows.</summary>
        internal static string Haptics(UserDevice ud, bool slotLive, bool sensaAvailable)
        {
            if (ud == null) return null;
            var s = Strings.Instance;
            var links = PeripheralOutputs.Links.For(ud.InstanceGuid);
            bool linked = links != null && links.Haptics.Length > 0;
            bool sensa = ud.InstanceGuid == PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa);
            // The Sensa row links only where the engine loads and Synapse is
            // installed. Where the engine cannot load, its record reads the
            // platform's limit ahead of the two checks below: they hide a
            // route, and the limit holds with the engine stopped or the
            // controller gone.
            if (!linked && sensa && !sensaAvailable && ud.HasPeripheralHaptics)
                return s.Common_NotAvailableOnArm64;
            if (!slotLive || !PeripheralOutputs.Running) return null;
            if (!linked)
            {
                // The record says it has haptics, and no path answers now.
                if (!ud.HasPeripheralHaptics) return null;
                // The engine loads here, so the row waits for Synapse.
                if (sensa) return s.Pad_ForceFeedback_RouteSensaWaiting;
                // A Logitech device asleep, or not plugged in. Any other
                // device that rumbles on its own is found by a direct
                // backend or by none, so its row waits for a device only
                // while a backend that rumbles one runs: before its first
                // scan, or while the row's unit is listed asleep.
                return ud.VendorId == PeripheralLinker.LogitechVid
                       || PeripheralOutputs.Direct.Families.Contains(OutputFamily.TouchSenseUnit)
                    ? s.Pad_ForceFeedback_RouteHidppAsleep
                    : null;
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
                case OutputFamily.TouchSenseUnit:
                    return string.Format(s.Pad_ForceFeedback_RouteTouchSense, DirectName(path, ud));
                default:
                    return null;
            }
        }

        /// <summary>The name a line gives a direct unit: the unit's own
        /// where its backend read one, else the row's.</summary>
        private static string DirectName(OutputPath path, UserDevice ud)
        {
            string name = PeripheralOutputs.Direct.Find(path)?.Name;
            return string.IsNullOrWhiteSpace(name) ? ud.ResolvedName : name;
        }

        /// <summary>OpenRGB's server by the name OpenRGB gives it: its tab
        /// "SDK Server" (qt/OpenRGBDialog/OpenRGBDialog.cpp:967) and its Linux
        /// service "OpenRGB SDK Server" (qt/openrgb.service:2). No locale
        /// translates it.</summary>
        internal const string OpenRgbServerName = "OpenRGB SDK Server";

        /// <summary>The vendor software a shared lighting path runs through,
        /// by its product name, which no locale translates.</summary>
        internal static string LightingApp(OutputFamily family) => family switch
        {
            OutputFamily.ChromaCategory => "Razer Synapse",
            OutputFamily.LedSdkType => PeripheralOutputs.Presence.LogitechGamingSoftware
                ? "Logitech Gaming Software" : "Logitech G HUB",
            OutputFamily.GameSenseColor => "SteelSeries GG",
            _ => null,
        };

        /// <summary>The devices a vendor row's line names as examples: what
        /// its software lights that PadForge does not read. SteelSeries GG
        /// binds no mousepad type (gamesense-sdk standard-zones.md:5-17), so
        /// its row names headsets alone.</summary>
        internal static string VendorRowExamples(OutputFamily family)
        {
            var s = Strings.Instance;
            return family switch
            {
                OutputFamily.ChromaCategory => s.Pad_Lighting_VendorRowExamples_Chroma,
                OutputFamily.LedSdkType => s.Pad_Lighting_VendorRowExamples_LedSdk,
                OutputFamily.GameSenseColor => s.Pad_Lighting_VendorRowExamples_GameSense,
                _ => null,
            };
        }

        /// <summary>The Lighting tab's line for a lit row on the virtual
        /// controller at <paramref name="slot"/>, or null for any other row:
        /// where its colors go, whether that path answers, and whose color
        /// shows when another claim rules the path.
        /// <paramref name="lightsHere"/> says whether this controller lights
        /// the device at all: a vendor row, or a device whose Control This
        /// Device's Lighting switch is on, on a controller that lights
        /// anything (<see cref="PeripheralOutputs.SlotLive"/>). A device this
        /// controller leaves alone shows only who else lights it, never a
        /// route its colors would take, and nothing while the vendor's
        /// software is down, when no controller lights it. A vendor row
        /// follows the same rule there. One whose engine cannot be loaded
        /// says so on every controller, since none lights it then.
        /// <paramref name="claimLevel"/> is the lighting hold this
        /// controller asks for (<see cref="PeripheralOutputs.SetLighting"/>).
        /// The line names what is held at the smaller of it and what the
        /// device offers.</summary>
        internal static string Lighting(UserDevice ud, int slot, bool lightsHere = true, int claimLevel = 0)
        {
            if (ud == null || !PeripheralOutputs.Running) return null;
            var s = Strings.Instance;
            var links = PeripheralOutputs.Links.For(ud.InstanceGuid);
            // A program the device's model names runs and lights it, and
            // the device is lit on its own once that program is closed
            // (DeviceLinks.LeftToProgram). The program's name stands where
            // a shared path's app stands. This line says colors go through
            // the program, which holds only for a program with an SDK route
            // of PadForge's own, until the line of its own lands with the
            // next translation pass (#557).
            // OpenRGB runs and its SDK server has not said which devices it
            // holds (#557): the device waits for the server. Until the line
            // of its own lands with the next translation pass, the waiting
            // line names the server as OpenRGB's own window does.
            if (links is { WaitsForOpenRgbServer: true })
                return lightsHere ? string.Format(s.Pad_Lighting_RouteWaiting, OpenRgbServerName) : null;
            if (links?.LeftToProgram is string program)
                return lightsHere ? string.Format(s.Pad_Lighting_RouteThroughOrDirect, program) : null;
            if (links == null || links.Lighting.Length == 0)
            {
                // The record says it has lighting, and no path reaches it
                // now: a device asleep or not plugged in, or one its backend
                // has not scanned for yet.
                if (!ud.HasPeripheralLighting || !lightsHere) return null;
                if (ud.VendorId == PeripheralLinker.LogitechVid) return s.Pad_Lighting_RouteAsleep;
                // Any other device is found by a direct backend or by none,
                // so without one running no device is being waited for. A
                // vendor row stands for software, which is waited for in a
                // line of its own, never for a device to wake.
                return PeripheralOutputs.Direct.Families.Count > 0 && PeripheralOutputRow.KindOf(ud.InstanceGuid) == null
                    ? s.Pad_Lighting_RouteAsleep
                    : null;
            }

            var path = links.Lighting[0];
            string app = LightingApp(path.Family);
            bool waiting = app != null && PeripheralOutputs.StateOf(path.Family) == BackendState.Waiting;
            if (links.CatchAll)
            {
                string examples = VendorRowExamples(path.Family);
                if (app == null || examples == null) return null;
                // An engine that cannot be loaded here lights nothing from any
                // controller, so this comes ahead of the line below, which
                // would name another controller as lighting the row.
                if (path.Family == OutputFamily.LedSdkType && PeripheralOutputs.LedSdkPaintable is { Length: 0 } none)
                    return CannotLight(none);
                // A controller that lights nothing, its own controller gone,
                // names only the controller that lights the row elsewhere,
                // and names none while the vendor's software is down: nothing
                // is lit then, the state a device's own row says nothing in.
                if (!lightsHere)
                    return !waiting && OtherSlotRules(ud.InstanceGuid, slot, links, out int lighting)
                        ? string.Format(s.Pad_Lighting_RouteOtherControllerOnly, lighting)
                        : null;
                // While the vendor's software is down, nothing shows from any
                // controller.
                if (waiting) return string.Format(s.Pad_Lighting_RouteVendorRowWaiting, app, examples);
                // The same row on another controller rules every path it
                // holds, so nothing set here shows.
                if (OtherSlotRules(ud.InstanceGuid, slot, links, out int player))
                    return string.Format(s.Pad_Lighting_RouteOtherController, player);
                return string.Format(s.Pad_Lighting_RouteVendorRow, app, examples);
            }
            // The Logitech LED engine on this PC cannot paint this type.
            if (path.Family == OutputFamily.LedSdkType && PeripheralOutputs.LedSdkPaintable is string[] paintable
                && Array.IndexOf(paintable, path.Key) < 0)
                return lightsHere ? CannotLight(paintable) : null;
            // Windows Dynamic Lighting lights the device, so its backend
            // stands down and nothing shows from any controller: the line
            // names Windows and the switch that hands the device over. A
            // laptop's own keyboard gets a word on what that switch costs.
            if (WindowsLights(links, out bool builtIn))
            {
                if (!lightsHere) return null;
                return builtIn && PeripheralLinker.IsKeyboard(ud.CapType)
                    ? s.Pad_Lighting_RouteWindowsLightingBuiltIn
                    : s.Pad_Lighting_RouteWindowsLighting;
            }
            if (waiting) return lightsHere ? string.Format(s.Pad_Lighting_RouteWaiting, app) : null;

            // Another claim rules the path: name the controller whose color
            // shows. A vendor row is named for itself, the same device on
            // another controller by that controller, and another device on a
            // shared path by the controller it is on.
            if (PeripheralOutputs.TryResolveColor(path, out _, out var ruler) && ruler.Player > 0
                && !(ruler.Device == ud.InstanceGuid && ruler.Slot == slot))
            {
                if (ruler.CatchAll)
                {
                    var kind = PeripheralOutputRow.KindOf(ruler.Device);
                    if (kind != null && app != null)
                        return string.Format(s.Pad_Lighting_RouteSharedVendorRow, app,
                            PeripheralOutputRow.NameFor(kind.Value), ruler.Player);
                }
                else if (ruler.Slot != slot && (ruler.Device == ud.InstanceGuid || app == null))
                    return string.Format(lightsHere ? s.Pad_Lighting_RouteOtherController : s.Pad_Lighting_RouteOtherControllerOnly,
                        ruler.Player);
                // Another row of the same directly lit device on this
                // controller holds the unit.
                else if (app == null && ruler.Device != ud.InstanceGuid)
                    return lightsHere ? s.Pad_Lighting_RouteOtherRow : s.Pad_Lighting_RouteOtherRowOnly;
                else if (ruler.Device != ud.InstanceGuid && app != null)
                    return string.Format(s.Pad_Lighting_RouteShared, app, ruler.Player);
            }

            if (!lightsHere) return null;
            if (!path.Shared)
            {
                // A device OpenRGB holds is lit through its SDK server
                // (#557). Until the line of its own lands with the next
                // translation pass, the line says where the colors go.
                if (path.Family == OutputFamily.OpenRgbDevice)
                    return string.Format(s.Pad_Lighting_RouteThrough, VendorPrograms.OpenRgb.Name);
                // A unit this controller lights on its own, named as its
                // backend read it, with what else is held where the device
                // offers the hold this controller asks for.
                string name;
                if (path.Family == OutputFamily.HidppUnit)
                {
                    var unit = PeripheralOutputs.Hidpp.Units.FirstOrDefault(u => u.Key == path.Key);
                    name = string.IsNullOrWhiteSpace(unit?.Name) ? ud.ResolvedName : unit.Name;
                }
                else
                {
                    name = DirectName(path, ud);
                }
                int level = Math.Min(claimLevel, links.MaxClaimLevel);
                string line = level >= 2 ? s.Pad_Lighting_RouteDirectHostMode
                    : level == 1 ? s.Pad_Lighting_RouteDirectPower
                    : s.Pad_Lighting_RouteDirect;
                // A keyboard whose own controller keeps still pictures only
                // shows changing colors through its LampArray interface,
                // which an option in the laptop's BIOS turns on and off and
                // which Windows may hold (HidLightingModel.LiveThroughLampArray).
                if (PeripheralOutputs.Direct.Find(path) is { LiveThroughLampArray: true })
                {
                    if (links.WindowsLightsLampArray) line = s.Pad_Lighting_RouteDirectBesideWindows;
                    else if (Array.Exists(links.Lighting, p => p.Family == OutputFamily.LampArrayUnit))
                        line = s.Pad_Lighting_RouteDirectChangingNeedsBios;
                }
                return string.Format(line, name);
            }
            if (app == null) return null;
            // The vendor's program lights it now, and it has a path of its
            // own for when the program is not running.
            return string.Format(links.DirectWhenClosed ? s.Pad_Lighting_RouteThroughOrDirect : s.Pad_Lighting_RouteThrough, app);
        }

        /// <summary>Whether Windows Dynamic Lighting lights a LampArray unit
        /// the row is linked to, and whether that unit is built into the PC
        /// (<see cref="PeripheralLinker.LocalMachineContainer"/>).</summary>
        private static bool WindowsLights(DeviceLinks links, out bool builtIn)
        {
            builtIn = false;
            var direct = PeripheralOutputs.Direct;
            foreach (var path in links.Lighting)
            {
                if (path.Family != OutputFamily.LampArrayUnit) continue;
                var unit = direct.Find(path);
                if (unit == null || !unit.WindowsLit) continue;
                builtIn = unit.ContainerId == PeripheralLinker.LocalMachineContainer;
                return true;
            }
            return false;
        }

        /// <summary>The line for an LED SDK path the Logitech engine on this
        /// PC cannot paint. An engine the ARM64 build cannot load at all is
        /// that platform's limit.</summary>
        private static string CannotLight(string[] paintable)
            => paintable.Length == 0 && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? Strings.Instance.Common_NotAvailableOnArm64
                : Strings.Instance.Pad_Lighting_RouteCannotLight;

        /// <summary>Whether a vendor row on this slot rules none of its paths
        /// while the same row on another slot rules one, and that slot's
        /// displayed number.</summary>
        private static bool OtherSlotRules(Guid row, int slot, DeviceLinks links, out int player)
        {
            player = 0;
            foreach (var path in links.Lighting)
            {
                if (!PeripheralOutputs.TryResolveColor(path, out _, out var ruler) || ruler.Device != row) continue;
                if (ruler.Slot == slot) return false;
                if (ruler.Player > 0 && player == 0) player = ruler.Player;
            }
            return player > 0;
        }
    }
}
