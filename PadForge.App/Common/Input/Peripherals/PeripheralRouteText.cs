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
                        return PeripheralOutputs.RefusalOn(path) != null
                            ? s.Pad_ForceFeedback_RouteGameSenseRefused
                            : s.Pad_ForceFeedback_RouteGameSenseWaiting;
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

        /// <summary>The vendor software a shared lighting path runs through,
        /// by its product name, which no locale translates.</summary>
        internal static string LightingApp(OutputFamily family) => family switch
        {
            OutputFamily.ChromaCategory => "Razer Synapse",
            OutputFamily.LedSdkType => PeripheralOutputs.Presence.LogitechGamingSoftware
                ? "Logitech Gaming Software" : "Logitech G HUB",
            OutputFamily.GameSenseColor => "SteelSeries GG",
            OutputFamily.AuraSdkType => VendorPrograms.ArmouryCrate.Name,
            OutputFamily.MysticLightType => VendorPrograms.MsiMysticLight.Name,
            OutputFamily.IcueDevice => VendorPrograms.CorsairIcue.Name,
            _ => null,
        };

        /// <summary>The devices a vendor row's line names as examples: what
        /// its software lights that PadForge does not read. SteelSeries GG
        /// binds no mousepad type (gamesense-sdk standard-zones.md:5-17), so
        /// its row names headsets alone. The ASUS Aura row names what the
        /// Aura SDK lights beside keyboards and mice, its mainboard,
        /// graphics card and DRAM types (RGB.NET 3c5af72
        /// RGB.NET.Devices.Asus/AsusDeviceProvider.cs:80-84), and the MSI
        /// Mystic Light row what Mystic Light lights there, MSI_MB and
        /// MSI_VGA (RGB.NET.Devices.Msi/MsiDeviceProvider.cs:92-101). The
        /// Corsair iCUE row names the two device types of IcueRoute.Types
        /// with no input row (iCUESDK.h:45-62).</summary>
        internal static string VendorRowExamples(OutputFamily family)
        {
            var s = Strings.Instance;
            return family switch
            {
                OutputFamily.ChromaCategory => s.Pad_Lighting_VendorRowExamples_Chroma,
                OutputFamily.LedSdkType => s.Pad_Lighting_VendorRowExamples_LedSdk,
                OutputFamily.GameSenseColor => s.Pad_Lighting_VendorRowExamples_GameSense,
                OutputFamily.AuraSdkType => s.Pad_Lighting_VendorRowExamples_Aura,
                OutputFamily.MysticLightType => s.Pad_Lighting_VendorRowExamples_MysticLight,
                OutputFamily.IcueDevice => s.Pad_Lighting_VendorRowExamples_Icue,
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
            // OpenRGB runs and its SDK server has not said which devices it
            // holds (#557): the device is left to OpenRGB until the server
            // answers, and the line names the port it was looked for on and
            // says how to turn the server on. It holds for a server that is
            // off and for one listening where PadForge could not find it. The
            // OpenRGB lines name the server by its tab in OpenRGB's window,
            // "SDK Server" (qt/OpenRGBDialog/OpenRGBDialog.cpp:967).
            if (links is { WaitsForOpenRgbServer: true })
                return lightsHere
                    ? string.Format(s.Pad_Lighting_RouteOpenRgbServerNoAnswer,
                        PeripheralOutputs.OpenRgbPortTried is > 0 and var tried ? tried : OpenRgbProtocol.DefaultPort)
                    : null;
            // A program the device's model names runs and lights it, and
            // PadForge has no path through it: the device is lit on its own
            // once that program is closed (DeviceLinks.LeftToProgram). A
            // device OpenRGB holds with no Direct mode is left to OpenRGB
            // (PeripheralLinker.LinkOpenRgb), which names it there.
            if (links?.LeftToProgram is string program)
            {
                if (!lightsHere) return null;
                return program == VendorPrograms.OpenRgb.Name
                    ? s.Pad_Lighting_RouteLeftToOpenRgb
                    : string.Format(s.Pad_Lighting_RouteLeftToProgram, program);
            }
            if (links == null || links.Lighting.Length == 0)
            {
                // The record says it has lighting, and no path reaches it
                // now: a device asleep or not plugged in, or one its backend
                // has not scanned for yet.
                if (!ud.HasPeripheralLighting || !lightsHere) return null;
                // Its handle opened and it took no session, or Windows or
                // another program holds it: it is here, and a line about waking
                // would send the user to wake it.
                if (RefusedUnit(ud) is { } refused)
                    return refused.WindowsHolds ? s.Pad_Lighting_RouteWindowsHolds
                        : refused.HeldByProgram ? s.Pad_Lighting_RouteHeldByProgram
                        : s.Pad_Lighting_RouteRefused;
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
                // The software runs and answered no, so the line names that
                // and not a wait for it to start.
                if (PeripheralOutputs.RefusalOn(path) is { } rowRefusal) return RefusedLine(rowRefusal, app);
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
            // The vendor's program runs and answered no on this path.
            if (app != null && PeripheralOutputs.RefusalOn(path) is { } refusal)
                return lightsHere ? RefusedLine(refusal, app) : null;
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
                // (#557). Where OpenRGB took a path of the device's own, the
                // device is lit on that path once OpenRGB closes, and the
                // line says so. A device only OpenRGB reaches gets no such
                // promise.
                if (path.Family == OutputFamily.OpenRgbDevice)
                    return LitOnItsOwnWithoutOpenRgb(links)
                        ? s.Pad_Lighting_RouteThroughOpenRgb
                        : s.Pad_Lighting_RouteThroughOpenRgbOnly;
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
                // A controller that keeps still pictures only shows changing
                // colors through a LampArray interface of the same device, on any
                // brand. What turns that interface on is the device's own: nothing
                // PadForge reads tells, so the line gives the remedy and states no
                // setting as fact. It holds with the interface linked or not, since
                // a unit asleep for a moment says nothing about the firmware.
                var direct = PeripheralOutputs.Direct.Find(path);
                if (direct is { StoresPictures: true } && links.WindowsLightsLampArray)
                    line = s.Pad_Lighting_RouteDirectBesideWindows;
                else if (direct is { StoresPictures: true } && links.HasLampArray)
                    line = s.Pad_Lighting_RouteDirectChangingThroughLampArray;
                // A device that stores each picture is sent no color that
                // keeps changing (HidLightingModel.HoldMs), and one whose
                // pictures also write over the effects stored on it says those
                // are lost (HidLightingModel.ReplacesStoredEffects).
                else if (direct is { StoresPictures: true })
                    line = direct.ReplacesStoredEffects ? s.Pad_Lighting_RouteDirectStoredEffects : s.Pad_Lighting_RouteDirectStored;
                // One sent no changing color, with no reference that it stores
                // what it is sent (DirectUnit.SteadyColorsOnly), says that alone.
                else if (direct is { SteadyColorsOnly: true })
                    line = s.Pad_Lighting_RouteDirectSteadyOnly;
                return string.Format(line, name);
            }
            if (app == null) return null;
            // The vendor's program lights it now, and it has a path of its
            // own for when the program is not running.
            return string.Format(links.DirectWhenClosed ? s.Pad_Lighting_RouteThroughOrDirect : s.Pad_Lighting_RouteThrough, app);
        }

        /// <summary>A unit of this row that opened and took no session
        /// (<see cref="DirectUnit.Refused"/>), or whose collection Windows or
        /// another program holds, or null. The row of a unit, or a device row
        /// by the linker's own rule, so the row of an identical device in
        /// another container says nothing of this one.</summary>
        private static DirectUnit RefusedUnit(UserDevice ud)
        {
            var row = new LinkRow(ud.InstanceGuid, ud.CapType, ud.VendorId, ud.ProdId,
                PeripheralOutputs.Links.ContainerOf(ud.InstanceGuid), null);
            foreach (var unit in PeripheralOutputs.Direct.Units)
            {
                if (!unit.Refused && !unit.WindowsHolds && !unit.HeldByProgram) continue;
                if (PeripheralUnitRow.IdentityFor(unit.Family, unit.Key) == ud.InstanceGuid
                    || PeripheralLinker.MatchesDevice(unit, row))
                    return unit;
            }
            return null;
        }

        /// <summary>Whether a path OpenRGB took from the row
        /// (<see cref="DeviceLinks.TakenByOpenRgb"/>) lights the device once
        /// OpenRGB closes: any of them but a LampArray unit Windows Dynamic
        /// Lighting lights, whose backend leaves it unpainted.</summary>
        private static bool LitOnItsOwnWithoutOpenRgb(DeviceLinks links)
        {
            var direct = PeripheralOutputs.Direct;
            foreach (var path in links.TakenByOpenRgb)
                if (path.Family != OutputFamily.LampArrayUnit || direct.Find(path) is not { WindowsLit: true })
                    return true;
            return false;
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

        /// <summary>The line for a path whose vendor program runs and answered
        /// no (<see cref="PeripheralOutputs.RefusalOn"/>).</summary>
        private static string RefusedLine(ProgramRefusal refusal, string app)
            => refusal.SettingOff
                ? Strings.Instance.Pad_Lighting_RouteIcueSdkOff
                : string.Format(Strings.Instance.Pad_Lighting_RouteProgramRefused, app);

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
