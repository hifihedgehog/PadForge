using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ── Composite-persona self-readback guard (HIDMaestro v1.4.0, HM#39) ──
        //
        // Composite USB personas enumerate through the REAL USB stack
        // (vhci → UDE → usbccgp → hidusb). The persona's vendor and product
        // ids are the real controller's, deliberately: Windows has to see a
        // real DualSense for the UAC audio class driver to bind. So its HID
        // interface path carries no HIDMAESTRO fragment, and the serial SDL
        // reports for it is no "HM-CTL-" one.
        //
        // Two things in the device tree do tell a persona from a real pad.
        //
        // HIDMaestro v1.4.3 (HM#42) stamps the ONE node it owns, the
        // emulated host controller, with an additive hardware id:
        //
        //   ROOT\USB\0000  ids = [ROOT\USBIP_WIN2\UDE, ROOT\HIDMAESTRO_UDE]
        //
        // Upstream's id stays at index 0 so driver matching is unchanged.
        // Measured on hardware, the token sits at depth 4 from a persona's
        // HID interface, which is why the walk below is generous.
        //
        // And a persona serves a USB serial (HM#60): HIDMaestro's own, "HM"
        // and twelve hex digits, unless its profile carries a captured one,
        // as the Steam Deck's does. Windows names the USB device by it,
        // USB\VID_054C&PID_0CE6\HM0123456789AB, two nodes above the HID
        // interface of a composite (HidMaestroAncestry.IsPersonaUsbInstanceId).
        //
        // Prefer the token: it names HIDMaestro rather than the transport.
        // The usbip2_ude service check stays as a fallback because the stamp
        // is best-effort upstream (a machine that refuses the write still
        // creates controllers) and because a pre-1.4.3 driver may still be
        // resident. That fallback is the user-mode mirror of the driver's
        // own is_abobe_vhci() check (usbip-win2 drivers/ude_filter/device.cpp),
        // and it matches every device attached through usbip-win2, a real
        // pad forwarded from another machine or another program's virtual
        // pad included.
        //
        // The Switch 2 Pro persona (HIDMaestro 1.11.0, HM#66) is the same
        // shape under Nintendo's identifiers, and the Valve composites under
        // Valve's.
        //
        // Where the stamp is in place, the forks drop a persona before Step 1
        // sees it. The SDL fork drops every HID interface that has the token
        // on its own node or one of the four above it
        // (hid_internal_is_hidmaestro_device at HM_FILTER_MAX_DEPTH 5, called
        // by hid_enumerate and by the RawInput and DirectInput backends).
        // It tests the host controller and not the device, so on a stamped
        // host it drops, with a persona's, the HID interface of a real pad
        // forwarded over usbip-win2 and of another program's virtual pad
        // there. The OpenXInput fork makes the same test on a walk of the
        // same length. It gives no slot to an XUSB interface whose own
        // node, or one of the four nodes above it, has the token
        // (IsHidMaestroInterface in OpenXInput's src/OpenXinput.cpp, lines
        // 18 to 109, maxDepth 5 at line 75, asked of every XUSB interface
        // it lists at line 1149). A pad xusb22 serves carries its XUSB
        // interface on its USB device, two nodes below the host
        // controller. A pad xboxgip serves, an Xbox One or Series pad,
        // carries it on its HID node, four nodes below. Neither gets a
        // slot under a stamped host, a persona and a forwarded real pad
        // alike. The walk read four nodes until fork 082eefc, which left
        // a pad xboxgip serves its slot: HIDMaestro 2.3.0 presented its
        // Xbox One and Series controllers that way, and PadForge listed
        // its own virtual controller as XInput#0 (hifihedgehog/SDL#51). A
        // forwarded pad of any other kind reaches Step 1 through none of
        // these backends, and PadForge reads it only by one of the ways
        // below.
        //
        // SDL has other ways in, and no filter stands on them. The fork's
        // GameInput backend (src/joystick/gdk/SDL_gameinputjoystick.cpp) is
        // built into the Windows DLL and asks the filter nothing. PadForge
        // sets neither of its hints. So it lists no gamepad
        // (SDL_HINT_JOYSTICK_GAMEINPUT, off by default in the fork), and
        // where the GameInput runtime it needs is installed it lists the
        // Xbox One instruments of its raw-report table
        // (SDL_HINT_JOYSTICK_GAMEINPUT_RAW, on by default: the Rock Band
        // guitars and drum kits, the Guitar Hero Live guitar and the Mad
        // Catz legacy adapter of SDL_gameinput_rawtype.h). An instrument
        // GameInput reports reaches Step 1 whatever host controller it sits
        // under, and PadForge reads it. Nor does a filter stand on what
        // hidapi reads through libusb (SDL_hidapi_vendorusb.c: the vendor
        // interfaces of its table, once WinUSB is bound to one, and an Xbox
        // interface the system's own driver does not hold) or on the fork's
        // serial, Bluetooth, network and iCade drivers.
        //
        // A persona does not come in that way. No HIDMaestro profile carries
        // an instrument's ids or an id of that table, and the Xbox
        // interface of a persona is one the system's own driver holds. The
        // Switch 2 driver, which opens its controller's bulk interface
        // through libusb itself, asks whether a candidate's serial is a
        // persona's (SDL_HidmaestroOwnsUsbSerial). The serial, Bluetooth and
        // network drivers read no USB HID interface, and the iCade driver
        // reads the keyboards whose ids the user names for it, under an
        // interface path this guard walks like any other.
        //
        // So this guard decides under a host controller with no stamp,
        // where neither fork has anything to find. SDL lists every HID
        // interface there, and the walk starts from its path. OpenXInput
        // gives every XUSB interface there a slot, and SDL's XInput backend
        // lists the pad as XInput#N, a path that names no node. A persona
        // can be such a pad since HIDMaestro 2.3.0, whose Xbox One and
        // Series controllers on its USB/IP driver are served by xboxgip.
        // The walk for one starts from the XUSB interface OpenXInput says
        // serves the number (AncestryPathOf, XInputSlotIdentity), and a
        // persona's serial above it settles it as for any other device. A
        // number OpenXInput has no interface for is let through, and one it
        // is busy with waits for the next sweep. A device
        // GameInput lists is let through as well: the path SDL gives for
        // one is GameInput's device id, which the walk finds no node for,
        // and its ids are no persona's. An XInput pad's identity is the
        // node StableXInputInstance finds for it, by the rule this guard
        // reads.
        //
        // The persona serial needs no scope and no clock: under the emulated
        // host controller, below a USB device named by it, a device is a
        // persona and is passed over whenever Step 1 sees it. That matters
        // once a persona is detached. Nothing waits for it to leave the
        // tree: usbip-win2 returns from the plug-out as soon as UDE has
        // taken it (plugout_and_delete, drivers/ude/device.cpp) and waits
        // 30 s after one before its own reattach (start_attach_attempts,
        // drivers/ude/persistent.cpp), HIDMaestro detaches and moves on
        // (UsbipBackendHandle.Dispose), and Windows removes the device stack
        // in its own time. A persona still listed after the scope below had
        // lapsed was opened as a controller of the user's.
        //
        // Scope of the ancestry test alone, for a device under the emulated
        // host controller with no persona serial: it holds for the vendor
        // and product id of a composite persona this process has live, is
        // creating, or retired within PersonaRetireGraceMs
        // (ChecksUsbipAncestry). HIDMaestro's USB/IP device emulator runs
        // inside PadForge's process, and a session's persona lives while its
        // controller is connected. One can outlive the session that made
        // it: a session that ended without its exit sweep leaves the device
        // enumerated until the next launch's sweep (App.OrphanSweepTask,
        // HIDMaestro's RemoveAllVirtualControllers, which unplugs an ended
        // session's imports in OwnerSweep.Reclaim through
        // UsbipBackend.DetachDeadImport and DetachImportsOfDeadServers). So
        // while that sweep runs, the test
        // also holds for the ids of every composite profile the catalog
        // offers. Outside those two windows such a device is taken for a
        // real or third-party one and not passed over. The residual is a
        // forwarded pad of the exact model of a persona live at the same
        // moment, disposed within the grace, or offered while the launch
        // sweep runs, which is taken for PadForge's own and passed over with
        // a log line. It is passed over only while that holds: Step 1 opens
        // it again once no persona of its ids is in scope and it is still
        // in the device tree (SelfVirtualSuppressions.ReleaseLapsed), where
        // it once stayed out for as long as it was attached. The other
        // residual is a persona with a captured serial, the Steam Deck's,
        // which nothing in the tree tells from a real Deck forwarded over
        // USB/IP: the grace alone covers its way out.
        //
        // The walk meets a second kind of node with a HIDMaestro hardware
        // id, and that one is no host controller. A virtual controller on
        // HIDMaestro's own driver is no persona: its HID interface hangs off
        // a node HIDMaestro creates and gives the hardware id
        // root\HIDMaestro (DeviceNodeCreator.CreateDeviceNode in HIDMaestro's
        // source). No real device sits under such a node, so it is a marker
        // like the HM-CTL- serial: the device is passed over whatever its
        // ids and whatever the scope, and is never released. The walk tells
        // the two kinds by the whole id, the host controller's stamp against
        // every other id that contains HIDMaestro, and it reads the device's
        // own node as well as the nodes above it (HidMaestroAncestry.Classify
        // in the engine, the rule the hiding side and StableXInputInstance
        // read too). It once took every such id for the host controller's,
        // which made that virtual controller a pad over USB/IP: let through
        // unless a composite persona of its ids was in scope, and opened
        // again once that persona's grace had run.

        // The composite personas this process makes, by vendor and product
        // id. HMaestroVirtualController records one before it asks HIDMaestro
        // for the persona and retires it once the persona is disposed, so
        // Step 1 knows the persona from its first enumeration to its last.
        private static readonly object s_personaGate = new();
        private static readonly List<LivePersona> s_personas = new();

        private sealed class LivePersona
        {
            public object Owner;
            public ushort VendorId;
            public ushort ProductId;
            /// <summary>TickCount64 ms when the persona was disposed, 0 while
            /// it lives.</summary>
            public long RetiredAtMs;
        }

        /// <summary>How long a disposed persona still counts as PadForge's.
        /// Step 1 enumerates every 2 s (every 5 s with no slot active), and
        /// the device leaves the PnP tree after the controller is disposed,
        /// so a persona that lived less than one enumeration is still known
        /// when Step 1 first sees it. It is no bound on how long Windows
        /// takes to remove the device: one that outlives it is known by the
        /// serial on its USB device (<see cref="HidMaestroAncestry.IsPersonaUsbInstanceId"/>),
        /// where it serves HIDMaestro's.</summary>
        internal const long PersonaRetireGraceMs = 10_000;

        /// <summary>Records the composite persona <paramref name="owner"/>
        /// is about to make. Called before HIDMaestro creates the device, so
        /// the persona is known from its first enumeration.</summary>
        internal static void NotePersonaLive(object owner, ushort vendorId, ushort productId)
        {
            if (owner == null) return;
            lock (s_personaGate)
            {
                PrunePersonas(Environment.TickCount64);
                foreach (var p in s_personas)
                {
                    if (!ReferenceEquals(p.Owner, owner)) continue;
                    p.VendorId = vendorId;
                    p.ProductId = productId;
                    p.RetiredAtMs = 0;
                    return;
                }
                s_personas.Add(new LivePersona { Owner = owner, VendorId = vendorId, ProductId = productId });
            }
        }

        /// <summary>Marks <paramref name="owner"/>'s persona disposed. It
        /// still counts for <see cref="PersonaRetireGraceMs"/>.</summary>
        internal static void NotePersonaRetired(object owner)
            => NotePersonaRetired(owner, Environment.TickCount64);

        /// <summary><see cref="NotePersonaRetired(object)"/> at a given time,
        /// for the tests.</summary>
        internal static void NotePersonaRetired(object owner, long nowMs)
        {
            if (owner == null) return;
            lock (s_personaGate)
            {
                foreach (var p in s_personas)
                    if (ReferenceEquals(p.Owner, owner) && p.RetiredAtMs == 0)
                        p.RetiredAtMs = Math.Max(1, nowMs);
                PrunePersonas(nowMs);
            }
        }

        /// <summary>True when its place under the emulated host controller
        /// alone marks a device with these ids as PadForge's own: a composite
        /// persona of this vendor and product id is live, being created, or
        /// was disposed less than <see cref="PersonaRetireGraceMs"/> ago, or a
        /// composite profile has them while the launch sweep runs
        /// (<see cref="LaunchSweepPending"/>). Any other device there is
        /// passed over only on a persona's own serial, so the guard lets a
        /// pad attached through usbip-win2 through unless it could be one of
        /// PadForge's own. Such a pad's HID interface reaches the guard only
        /// under a host controller HIDMaestro has not stamped, since the SDL
        /// fork drops it under a stamped one. What SDL lists there by a way
        /// the fork's filter is not on, the header names: an instrument
        /// through GameInput, which the walk finds no node for, and a
        /// keyboard the user named for the iCade driver, which this test
        /// then weighs like any other device. Under a host controller with
        /// no stamp it weighs a pad SDL's XInput backend lists as well,
        /// walked from the interface behind its number
        /// (<see cref="AncestryPathOf"/>). The hiding side asks this for a node under the
        /// host controller (HidHideController.PersonaInScope), and the
        /// engine for the node of an XInput pad's identity
        /// (StableXInputInstance.PersonaInScope).</summary>
        internal static bool ChecksUsbipAncestry(ushort vendorId, ushort productId)
            => ChecksUsbipAncestry(vendorId, productId, Environment.TickCount64);

        /// <summary><see cref="ChecksUsbipAncestry(ushort, ushort)"/> at a
        /// given time.</summary>
        internal static bool ChecksUsbipAncestry(ushort vendorId, ushort productId, long nowMs)
        {
            lock (s_personaGate)
            {
                PrunePersonas(nowMs);
                foreach (var p in s_personas)
                    if (p.VendorId == vendorId && p.ProductId == productId) return true;
            }
            return LaunchSweepPending() && IsCompositeProfileIds(vendorId, productId);
        }

        /// <summary>True while the launch sweep that detaches the personas a
        /// previous session left is still running (App.OrphanSweepTask). A
        /// seam for the tests.</summary>
        internal static Func<bool> LaunchSweepPending = () => App.OrphanSweepTask is { IsCompleted: false };

        /// <summary>True when a composite profile the catalog offers
        /// (HMProfile.RequiresUsbipBackend) carries these ids, a persona a
        /// session of PadForge could have made.</summary>
        private static bool IsCompositeProfileIds(ushort vendorId, ushort productId)
        {
            foreach (var p in HMaestroProfileCatalog.AllProfiles)
                if (p.RequiresUsbipBackend && p.VendorId == vendorId && p.ProductId == productId) return true;
            return false;
        }

        /// <summary>Drops the personas whose grace has run out. Called under
        /// the gate.</summary>
        private static void PrunePersonas(long nowMs)
            => s_personas.RemoveAll(p => p.RetiredAtMs != 0 && nowMs - p.RetiredAtMs >= PersonaRetireGraceMs);

        /// <summary>The SDL instance ids Step 1 rejected as PadForge's own
        /// virtual controllers, kept so each enumeration skips them instead
        /// of opening and probing them again. SDL instance ids are unique per
        /// connection, so a real device never inherits one. An id rejected on
        /// a marker (the HM-CTL- serial, a HIDMAESTRO path, a node of
        /// HIDMaestro's own driver on the way up, a persona's own serial on
        /// the USB device above it) stays for the life of the process. An id
        /// rejected on its place under the emulated host controller alone
        /// stays while a persona of its ids is in scope
        /// (<see cref="ChecksUsbipAncestry(ushort, ushort, long)"/>) and is
        /// opened again after, if it is still in the device tree: a real pad
        /// or another program's, attached over USB/IP while a persona of its
        /// model was live or the launch sweep ran, or a persona with a
        /// captured serial whose device outlived its grace.</summary>
        internal sealed class SelfVirtualSuppressions
        {
            private readonly HashSet<uint> _ids = new();
            private readonly Dictionary<uint, (ushort VendorId, ushort ProductId, string DevicePath)> _byAncestry = new();

            public bool Contains(uint instanceId) => _ids.Contains(instanceId);

            /// <summary>Step 1's test of a device it has just opened. True
            /// when the device is PadForge's own virtual controller: the id
            /// is recorded, and the caller closes the device. A marker
            /// settles it, whatever the ids and whatever the scope: the
            /// HM-CTL- serial, a HIDMAESTRO path, or a node of HIDMaestro's
            /// own driver in the device tree, the device's own or one above
            /// it (<see cref="UsbipAncestry.HidMaestroNode"/>), which is
            /// what tells a controller on that driver once SDL has replaced
            /// its serial. Composite personas (HM v1.4.0) ride the real USB
            /// stack and carry none of the three. One under the emulated
            /// host controller, below a USB device named by HIDMaestro's own
            /// serial, is settled the same way
            /// (<see cref="UsbipAncestry.Persona"/>). A device under that
            /// controller with no such serial is passed over for the ids of
            /// a persona this process makes
            /// (<see cref="ChecksUsbipAncestry(ushort, ushort)"/>), until
            /// that persona is gone, and is let through otherwise, as a real
            /// pad forwarded over USB/IP. The walk starts at the device's
            /// HID interface, or for a joystick SDL names only "XInput#N"
            /// at the XUSB interface that serves the number
            /// (<see cref="AncestryPathOf"/>).</summary>
            public bool PassOver(uint instanceId, string serialNumber, string devicePath,
                ushort vendorId, ushort productId)
            {
                bool markedSelf =
                    (serialNumber != null
                     && serialNumber.StartsWith("HM-CTL-", StringComparison.Ordinal))
                    || (devicePath != null
                        && devicePath.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0);
                // The path the walk starts from, and the one kept to ask
                // later whether the device is still in the tree: an
                // XInput#N joystick's is the interface behind its number.
                bool busy = false;
                string walkFrom = markedSelf ? devicePath : AncestryPathOf(devicePath, out busy);
                // OpenXInput was in another thread's device I/O. The
                // joystick is closed and not recorded, so the next sweep
                // opens it and asks again: let through unasked, it could be
                // a persona.
                if (busy)
                {
                    Engine.SdlDiagLog.WriteLine(
                        $"DEV identity busy SDL#{instanceId} {devicePath}: asked again at the next sweep");
                    return true;
                }
                var ancestry = markedSelf ? UsbipAncestry.None : UsbipAncestryOf(walkFrom);
                if (ancestry is UsbipAncestry.Persona or UsbipAncestry.HidMaestroNode) markedSelf = true;
                bool ancestrySelf = !markedSelf
                    && ancestry == UsbipAncestry.UnderVhci
                    && ChecksUsbipAncestry(vendorId, productId);
                if (!markedSelf && !ancestrySelf) return false;
                Engine.SdlDiagLog.WriteLine(
                    $"DEV self-virtual suppressed SDL#{instanceId} {vendorId:X4}:{productId:X4} serial={serialNumber}");
                Add(instanceId, vendorId, productId, byAncestryOnly: ancestrySelf, walkFrom);
                return true;
            }

            /// <summary>Records a rejected id. One rejected on its place
            /// under the emulated host controller alone
            /// (<paramref name="byAncestryOnly"/>) keeps the ids that test
            /// held for and the path of its device, which
            /// <see cref="ReleaseLapsed"/> asks about.</summary>
            public void Add(uint instanceId, ushort vendorId, ushort productId, bool byAncestryOnly,
                string devicePath = null)
            {
                _ids.Add(instanceId);
                if (byAncestryOnly) _byAncestry[instanceId] = (vendorId, productId, devicePath);
            }

            /// <summary>Forgets the ancestry-only ids SDL no longer lists
            /// (<paramref name="present"/>) and the ones no persona's scope
            /// covers any more. Returns the latter, the devices to open
            /// again, or null when there is none. One whose device has left
            /// the device tree is neither: SDL lists a removed device a
            /// moment longer, and a persona opened then became a controller
            /// of the user's that was never there. It stays passed over
            /// until SDL drops it.</summary>
            public List<uint> ReleaseLapsed(HashSet<uint> present, long nowMs)
            {
                if (_byAncestry.Count == 0) return null;
                List<uint> forget = null, probeAgain = null;
                foreach (var entry in _byAncestry)
                {
                    bool attached = present.Contains(entry.Key);
                    if (attached)
                    {
                        if (ChecksUsbipAncestry(entry.Value.VendorId, entry.Value.ProductId, nowMs)) continue;
                        if (entry.Value.DevicePath != null && !InDeviceTree(entry.Value.DevicePath)) continue;
                    }
                    (forget ??= new List<uint>()).Add(entry.Key);
                    if (attached) (probeAgain ??= new List<uint>()).Add(entry.Key);
                }
                if (forget == null) return null;
                foreach (uint id in forget)
                {
                    _byAncestry.Remove(id);
                    _ids.Remove(id);
                }
                return probeAgain;
            }
        }

        /// <summary>The path the ancestry walk starts from for a device SDL
        /// lists under <paramref name="devicePath"/>. A joystick of SDL's
        /// XInput backend has no path of its own, only "XInput#N", so its
        /// walk starts at the XUSB interface OpenXInput says serves that
        /// number (<see cref="XInputSlotIdentity"/>). Null when OpenXInput
        /// names none: the number is empty or the DLL is not PadForge's own.
        /// The walk then finds no node, and the joystick is let through.
        /// <paramref name="busy"/> is set when OpenXInput was in another
        /// thread's device I/O. The number is asked once and never waited
        /// for on the poll thread, and PassOver leaves that joystick for the
        /// next sweep. Every other path is its own.</summary>
        internal static string AncestryPathOf(string devicePath, out bool busy)
        {
            busy = false;
            if (!XInputSlotIdentity.TryParseSlot(devicePath, out int slot)) return devicePath;
            var served = XInputSlotIdentity.Get(slot);
            busy = served.Kind == XInputSlotIdentity.Kind.Busy;
            return served.Kind == XInputSlotIdentity.Kind.Found ? served.InterfacePath : null;
        }

        /// <summary>The ancestry walk the guard runs
        /// (<see cref="WalkUsbipAncestry"/>). A seam for the tests, which
        /// have no persona's device node to walk.</summary>
        internal static Func<string, UsbipAncestry> UsbipAncestryOf = WalkUsbipAncestry;

        /// <summary>True while the device behind a HID device path is in the
        /// device tree (<see cref="IsInDeviceTree"/>). A seam for the
        /// tests.</summary>
        internal static Func<string, bool> InDeviceTree = IsInDeviceTree;

        /// <summary>The live walk behind <see cref="UsbipAncestryOf"/>: the
        /// hops read off the device tree, classified by the rule the hiding
        /// side and the identity of an XInput pad read too
        /// (<see cref="HidMaestroAncestry.Classify"/>). A device that is not
        /// in the tree, and any read that fails, answers
        /// <see cref="UsbipAncestry.None"/>, so a real device is never
        /// passed over by a plumbing error.</summary>
        private static UsbipAncestry WalkUsbipAncestry(string hidDevicePath)
            => TryLocateDevNode(hidDevicePath, out uint devInst)
                ? HidMaestroAncestry.Classify(ReadAncestryHops(devInst))
                : UsbipAncestry.None;

        /// <summary>The walk up from the node behind a device path, as
        /// <see cref="ReadAncestryHops"/> reads it, or null when the device
        /// tree does not hold the node. The Devices page names a device's
        /// link from it (Common.DeviceTransport.LinkOf). A device that is
        /// not connected is found as the node Windows keeps for it, whose
        /// place in the tree stays recorded (CM_LOCATE_DEVNODE_PHANTOM, as
        /// HeadsetTrackerRepair finds a headset), so it shows the link it
        /// last had.</summary>
        internal static IEnumerable<AncestryHop> AncestryHopsOf(string devicePath)
            => TryLocateDevNode(devicePath, out uint devInst)
                || TryLocateDevNode(devicePath, out devInst, CM_LOCATE_DEVNODE_PHANTOM)
                ? ReadAncestryHops(devInst) : null;

        /// <summary>The device's own node, then its parents in turn, up to
        /// <see cref="HidMaestroAncestry.MaxHops"/> nodes in all, each read
        /// as it is asked for, so the walk stops at the node that settles
        /// it.</summary>
        private static IEnumerable<AncestryHop> ReadAncestryHops(uint devInst)
        {
            // Bounded parent walk: HID child → usbccgp function → USB
            // device → hub → host controller is 4-5 hops on real trees.
            for (int read = 1; ; read++)
            {
                yield return new AncestryHop(
                    GetDevNodeInstanceId(devInst), GetDevNodeHardwareIds(devInst), GetDevNodeService(devInst));
                if (read == HidMaestroAncestry.MaxHops || CM_Get_Parent(out uint parent, devInst, 0) != CR_SUCCESS)
                    yield break;
                devInst = parent;
            }
        }

        /// <summary>The device node behind a HID interface path, when it is
        /// in the device tree, or with <see cref="CM_LOCATE_DEVNODE_PHANTOM"/>
        /// when Windows keeps a node for it that has left the tree.</summary>
        private static bool TryLocateDevNode(string hidDevicePath, out uint devInst, uint flags = 0)
        {
            devInst = 0;
            if (string.IsNullOrEmpty(hidDevicePath)) return false;

            // Interface path → instance ID: "\\?\hid#vid_054c&pid_0ce6#7&x&y&z#{guid}"
            // becomes "HID\VID_054C&PID_0CE6\7&x&y&z".
            string p = hidDevicePath;
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);
            int guidSep = p.IndexOf("#{", StringComparison.Ordinal);
            if (guidSep > 0) p = p.Substring(0, guidSep);
            string instanceId = p.Replace('#', '\\');

            // Flags 0 is CM_LOCATE_DEVNODE_NORMAL: a node that has left the
            // tree is not found.
            return CM_Locate_DevNodeW(out devInst, instanceId, flags) == CR_SUCCESS;
        }

        /// <summary>True while the device behind
        /// <paramref name="hidDevicePath"/> is in the device tree. The guard
        /// asks it of a device the walk found there before, so a miss means
        /// the device has gone.</summary>
        private static bool IsInDeviceTree(string hidDevicePath)
            => TryLocateDevNode(hidDevicePath, out _);

        /// <summary>The instance id of a device node, or null. An id is at
        /// most 200 characters (MAX_DEVICE_ID_LEN).</summary>
        private static string GetDevNodeInstanceId(uint devInst)
        {
            var id = new System.Text.StringBuilder(512);
            return CM_Get_Device_IDW(devInst, id, id.Capacity, 0) == CR_SUCCESS ? id.ToString() : null;
        }

        /// <summary>A devnode's hardware ids, none when they do not read.
        /// REG_MULTI_SZ, so the buffer is a run of NUL-terminated strings,
        /// and each is handed over whole: two kinds of node carry a
        /// HIDMaestro id, and the one that is the emulated host controller's
        /// stamp (HM#42, v1.4.3) is told from the rest by the full string
        /// (<see cref="HidMaestroAncestry.Classify"/>). The SDL fork's filter takes a
        /// substring of any of them, since it drops both kinds.</summary>
        private static string[] GetDevNodeHardwareIds(uint devInst)
        {
            uint size = 0;
            CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_HardwareIds, out _, null, ref size, 0);
            if (size == 0) return Array.Empty<string>();
            byte[] buf = new byte[size];
            int rc = CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_HardwareIds, out uint type, buf, ref size, 0);
            if (rc != CR_SUCCESS) return Array.Empty<string>();
            if (type != DEVPROP_TYPE_STRING && type != DEVPROP_TYPE_STRING_LIST) return Array.Empty<string>();
            return System.Text.Encoding.Unicode.GetString(buf, 0, (int)size)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        private static string GetDevNodeService(uint devInst)
        {
            uint size = 0;
            CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_Service, out _, null, ref size, 0);
            if (size == 0) return null;
            byte[] buf = new byte[size];
            int rc = CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_Service, out uint type, buf, ref size, 0);
            if (rc != CR_SUCCESS || type != DEVPROP_TYPE_STRING) return null;
            string s = System.Text.Encoding.Unicode.GetString(buf, 0, (int)size);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s.Substring(0, nul) : s;
        }

        private const int CR_SUCCESS = 0;

        /// <summary>Finds a node Windows keeps for a device that has left the
        /// tree (cfgmgr32.h).</summary>
        private const uint CM_LOCATE_DEVNODE_PHANTOM = 0x1;
        private const uint DEVPROP_TYPE_STRING = 0x12;
        private const uint DEVPROP_TYPE_STRING_LIST = 0x2012;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        private static readonly DEVPROPKEY DEVPKEY_Device_Service =
            new DEVPROPKEY { fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), pid = 6 };

        // DEVPKEY_Device_HardwareIds, same fmtid, pid 3. NOT the same
        // numbering as setupapi's SPDRP_HARDWAREID (1). HM#42 records
        // shipping a devnode-corrupting bug from exactly that mismatch.
        private static readonly DEVPROPKEY DEVPKEY_Device_HardwareIds =
            new DEVPROPKEY { fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), pid = 3 };

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint dnDevInst, System.Text.StringBuilder buffer, int bufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(
            uint dnDevInst, in DEVPROPKEY propertyKey,
            out uint propertyType, byte[] propertyBuffer, ref uint propertyBufferSize, uint ulFlags);
    }
}
