using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PadForge.Engine
{
    /// <summary>What a walk up from a device node finds
    /// (<see cref="HidMaestroAncestry.Classify"/>).</summary>
    public enum UsbipAncestry
    {
        /// <summary>Neither the usbip-win2 host controller nor a
        /// HIDMaestro device node, on the device's own node or above
        /// it.</summary>
        None,
        /// <summary>Under the usbip-win2 emulated host controller, known
        /// by its usbip2_ude service or by the hardware id HIDMaestro
        /// stamps on it, ROOT\HIDMAESTRO_UDE, with no USB device on the
        /// way named by HIDMaestro's serial. Both name the transport and
        /// not the owner: a persona with a captured serial sits there,
        /// and so does a pad attached over usbip-win2.</summary>
        UnderVhci,
        /// <summary>Under it, below a USB device that serves HIDMaestro's
        /// own serial (<see cref="HidMaestroAncestry.IsPersonaUsbInstanceId"/>):
        /// a persona, whichever session made it and whether or not it is
        /// still attached.</summary>
        Persona,
        /// <summary>On HIDMaestro's own driver: the device's node, or one
        /// above it, carries a HIDMaestro hardware id that is not the
        /// host controller's stamp, as the node HIDMaestro creates for
        /// such a virtual controller does (root\HIDMaestro). No real
        /// device sits under one.</summary>
        HidMaestroNode,
    }

    /// <summary>One device node on the walk from a device up through its
    /// parents: its instance id, its hardware ids and its driver
    /// service.</summary>
    public readonly record struct AncestryHop(string InstanceId, IReadOnlyList<string> HardwareIds, string Service);

    /// <summary>
    /// The rule that tells HIDMaestro's devices from real ones by where
    /// they sit in the device tree, and the walks PadForge's readers make
    /// to drop them. Step 1's guard (InputManager.SelfVirtualSuppressions),
    /// the hiding side (HidHideController) and the identity of an XInput
    /// pad (<see cref="StableXInputInstance"/>) all read it here, so none
    /// can judge a node one way while another judges it the other.
    /// </summary>
    public static class HidMaestroAncestry
    {
        // The usbip-win2 emulated host controller: its driver service, and
        // the hardware id HIDMaestro adds to it
        // (UsbipDriverInstaller.StampOwnerHardwareId).
        private const string UsbipHostService = "usbip2_ude";
        private const string UsbipHostStamp = @"ROOT\HIDMAESTRO_UDE";

        /// <summary>How far <see cref="Classify"/> reads: the device's own
        /// node and the eight above it. The stamped host controller sits
        /// four above a persona's HID interface, and Step 1's walk runs on
        /// the polling thread, so it asks no tree for more than this,
        /// whatever the tree answers.</summary>
        public const int MaxHops = 9;

        /// <summary>What a walk finds, from its hops: the device's own node
        /// first, then each node above it in turn, <see cref="MaxHops"/>
        /// of them at most.
        ///
        /// <para>Two kinds of node carry a HIDMaestro hardware id. The
        /// usbip-win2 emulated host controller carries the stamp
        /// ROOT\HIDMAESTRO_UDE, and its service is usbip2_ude where the
        /// stamp is missing. Every device attached through usbip-win2 sits
        /// under it, so either one names the transport and the answer is
        /// <see cref="UsbipAncestry.UnderVhci"/>, or
        /// <see cref="UsbipAncestry.Persona"/> below a USB device named by
        /// HIDMaestro's serial. The host controller marks nothing by
        /// itself. Any other hardware id that contains HIDMaestro names a
        /// node of HIDMaestro's own driver: the node it creates for a
        /// virtual controller is given root\HIDMaestro (HIDMaestro's
        /// DeviceNodeCreator.CreateDeviceNode, and
        /// DeviceOrchestrator.CreateGamepadCompanion for the Xbox profiles
        /// xinputhid binds to), and the HID interface under it is the one
        /// SDL opens. No real device sits under such a node, so the answer
        /// is <see cref="UsbipAncestry.HidMaestroNode"/> wherever it is
        /// met.</para>
        ///
        /// <para>The device's own node counts, as it does in the SDL fork's
        /// filter (hid_internal_is_hidmaestro_device), which finds
        /// HID\HIDMaestro there for the profiles that are not
        /// Xbox's.</para></summary>
        public static UsbipAncestry Classify(IEnumerable<AncestryHop> hops)
        {
            if (hops == null) return UsbipAncestry.None;
            bool belowPersonaSerial = false;
            int read = 0;
            foreach (var hop in hops)
            {
                if (!belowPersonaSerial) belowPersonaSerial = IsPersonaUsbInstanceId(hop.InstanceId);
                bool host = string.Equals(hop.Service, UsbipHostService, StringComparison.OrdinalIgnoreCase);
                if (hop.HardwareIds != null)
                {
                    foreach (string hardwareId in hop.HardwareIds)
                    {
                        if (hardwareId == null
                            || hardwareId.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        // The host controller's stamp names the transport.
                        // Any other HIDMaestro id names a HIDMaestro node.
                        if (!string.Equals(hardwareId, UsbipHostStamp, StringComparison.OrdinalIgnoreCase))
                            return UsbipAncestry.HidMaestroNode;
                        host = true;
                    }
                }
                if (host) return belowPersonaSerial ? UsbipAncestry.Persona : UsbipAncestry.UnderVhci;
                if (++read == MaxHops) break;
            }
            return UsbipAncestry.None;
        }

        /// <summary>True for the instance id of a USB device that serves
        /// HIDMaestro's own serial, "HM" and twelve hex digits
        /// (DeviceIdentity.SyntheticSerial), which Windows makes the last
        /// part of the id, after the vendor and product ids:
        /// USB\VID_054C&amp;PID_0CE6\HM0123456789AB. Every composite
        /// persona's is one unless its profile carries a captured serial. A
        /// real device's id ends in a serial of its own or in a number
        /// Windows makes from its port.</summary>
        public static bool IsPersonaUsbInstanceId(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)
                || !instanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase))
                return false;
            int serial = instanceId.LastIndexOf('\\') + 1;
            if (instanceId.Length - serial != 14
                || !instanceId.AsSpan(serial, 2).Equals("HM", StringComparison.OrdinalIgnoreCase))
                return false;
            for (int i = serial + 2; i < instanceId.Length; i++)
                if (!Uri.IsHexDigit(instanceId[i])) return false;
            return true;
        }

        /// <summary>True for an instance id that names HIDMaestro:
        /// HIDMAESTRO, HMCOMPANION or HMXINPUT in it, or one of the root
        /// enumerators of HIDMaestro's xinputhid and XUSB nodes,
        /// ROOT\VID_xxxx&amp;PID_yyyy&amp;IG_nn and &amp;XI_nn. A real
        /// device never roots at ROOT\VID_.</summary>
        public static bool NamesHidMaestro(string instanceId)
        {
            if (instanceId == null) return false;
            if (instanceId.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0
                || instanceId.IndexOf("HMCOMPANION", StringComparison.OrdinalIgnoreCase) >= 0
                || instanceId.IndexOf("HMXINPUT", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return instanceId.StartsWith(@"ROOT\VID_", StringComparison.OrdinalIgnoreCase)
                && (instanceId.IndexOf("&IG_", StringComparison.OrdinalIgnoreCase) >= 0
                    || instanceId.IndexOf("&XI_", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>Whether a walk, the device's own node first, ends in one
        /// of HIDMaestro's devices: an instance id on the walk names
        /// HIDMaestro (<see cref="NamesHidMaestro"/>), or
        /// <see cref="Classify"/> finds a node of HIDMaestro's own driver or
        /// a persona. Under the usbip-win2 host controller with neither, the
        /// device is HIDMaestro's only while <paramref name="personaInScope"/>
        /// says a composite persona of its vendor and product id can be
        /// PadForge's own (InputManager.ChecksUsbipAncestry), which the
        /// caller answers for the device it asks about.</summary>
        public static bool IsHidMaestroWalk(IEnumerable<AncestryHop> hops, Func<bool> personaInScope)
        {
            if (hops == null) return false;
            // Both readings take the walk, so it is made once.
            var walk = hops as IReadOnlyList<AncestryHop> ?? new List<AncestryHop>(hops);
            foreach (var hop in walk)
                if (NamesHidMaestro(hop.InstanceId)) return true;
            switch (Classify(walk))
            {
                case UsbipAncestry.HidMaestroNode:
                case UsbipAncestry.Persona:
                    return true;
                case UsbipAncestry.UnderVhci:
                    // The host controller marks nothing by itself, so the
                    // scope is asked.
                    return personaInScope != null && personaInScope();
                default:
                    return false;
            }
        }

        // How many parents each reader walks above the node it starts at.
        // SDL: HM_FILTER_MAX_DEPTH 5, the node and four parents (hid.c:986).
        // OpenXInput: maxDepth 4, the node and three (OpenXinput.cpp:49).
        private const int SdlHidParents = 4;
        private const int OpenXInputParents = 3;

        /// <summary>Whether PadForge's readers drop the node a walk starts
        /// at, so that PadForge reads nothing from it.
        ///
        /// <para>The SDL fork and the OpenXInput fork keep HIDMaestro's
        /// devices away from PadForge by walking up from a device and
        /// dropping it when a hardware id on the way names HIDMaestro. The
        /// stamp on the usbip-win2 host controller is such an id, and it
        /// sits above every device attached there: a persona, a real pad
        /// attached from another machine, another program's virtual pad.
        /// Each reader starts at its own node and walks its own depth, so
        /// what PadForge reads under a stamped host controller is the
        /// reader's to say.</para>
        ///
        /// <para>A HID interface or collection. SDL's HIDAPI enumeration
        /// skips it (hid.c:1344), and so does its DirectInput backend
        /// (SDL_dinputjoystick.c:510), when a hardware id of the HID node
        /// or one of its four parents names HIDMaestro
        /// (hid_internal_is_hidmaestro_device, hid.c:1069-1143). The host
        /// controller is three parents above the HID node of a
        /// single-interface USB device and four above a composite's, so
        /// neither is read.</para>
        ///
        /// <para>An XInput interface, the &amp;IG_ node HIDAPI leaves to
        /// XInput (hid.c:1334) and DirectInput passes over
        /// (SDL_dinputjoystick.c:513). OpenXInput lists XUSB interfaces and
        /// gives no slot to one when a hardware id of its node or one of
        /// that node's three parents names HIDMaestro (OpenXinput.cpp:1103
        /// and 1123, IsHidMaestroInterface). A pad xusb22 serves carries
        /// the interface on the USB device above the HID node, two nodes
        /// below the host controller, and gets no slot. A pad xboxgip
        /// serves carries it on the HID node, four nodes below
        /// (SDL_xinput_paddle_identity.cpp:518-553), where the walk ends at
        /// the root hub: it keeps its slot and PadForge reads it. Windows
        /// says which node carries the interface
        /// (<see cref="CarriesXusbInterface"/>).</para>
        ///
        /// <para>A node that is no HID node, such as the USB interface
        /// above a collection, follows the HID node below it. Every other
        /// node is deeper than these walks or under no stamp, and is
        /// read.</para></summary>
        public static bool ReadersDrop(IReadOnlyList<AncestryHop> hops, Func<string, bool> carriesXusb)
        {
            if (hops == null || hops.Count == 0) return false;

            // How many nodes up the first hardware id that names HIDMaestro
            // sits, 0 for the node itself. Both forks test every hardware
            // id of every node they walk for the name.
            int named = -1;
            for (int i = 0; i < hops.Count && named < 0; i++)
            {
                var hardwareIds = hops[i].HardwareIds;
                if (hardwareIds == null) continue;
                foreach (var hardwareId in hardwareIds)
                {
                    if (hardwareId == null
                        || hardwareId.IndexOf("HIDMaestro", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    named = i;
                    break;
                }
            }
            if (named < 0) return false;

            string node = hops[0].InstanceId ?? string.Empty;
            // Where the HID node is, counted from the node asked about: the
            // node itself, or the one below a node that is none.
            int hidNode = node.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase) ? 0 : -1;

            if (node.IndexOf("&IG_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // OpenXInput starts at the node that carries the XUSB
                // interface: this node, else the one above a HID node, else
                // the HID node below.
                int xusbNode = carriesXusb != null && carriesXusb(node) ? 0
                    : hidNode == 0 ? 1
                    : -1;
                return named <= xusbNode + OpenXInputParents;
            }
            return named <= hidNode + SdlHidParents;
        }

        /// <summary>The device interface class OpenXInput lists, XUSB
        /// (GUID_DEVINTERFACE_XUSB).</summary>
        public static readonly Guid XusbInterfaceGuid = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");

        /// <summary>Whether a devnode carries a present device interface of
        /// the XUSB class. An empty interface list is its one terminating
        /// character. False on any failure.</summary>
        public static bool CarriesXusbInterface(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;
            try
            {
                var guid = XusbInterfaceGuid;
                return CM_Get_Device_Interface_List_SizeW(out uint length, ref guid, instanceId,
                           CM_GET_DEVICE_INTERFACE_LIST_PRESENT) == CR_SUCCESS
                       && length > 1;
            }
            catch
            {
                return false;
            }
        }

        private const int CR_SUCCESS = 0;
        private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List_SizeW(
            out uint pulLen, ref Guid interfaceClassGuid,
            [MarshalAs(UnmanagedType.LPWStr)] string pDeviceID, uint ulFlags);
    }
}
