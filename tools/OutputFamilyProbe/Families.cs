using System.Runtime.CompilerServices;
using HIDMaestro;
using HIDMaestro.Internal;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Models2D;
using PadForge.ViewModels;

namespace PadForge.Tools.OutputFamilyProbe
{
    /// <summary>The submit lanes of Step 5's Pass 3
    /// (InputManager.Step5's UpdateVirtualDevicesLocked), one per branch of
    /// its if-chain.</summary>
    internal enum Route
    {
        /// <summary>Plain SubmitGamepadState (Step 5's plain vc.SubmitGamepadState call): Xbox.</summary>
        Gamepad,
        /// <summary>A Sony packer's raw frame (Step 5's SonyReportPackers
        /// block), and no state leg (Step 5 skips it when
        /// SonyReportPackers.ForProfile returns a packer).</summary>
        SonyPacked,
        /// <summary>The extended SubmitGamepadState overload (Step 5's hmExtState.SubmitGamepadState call):
        /// a PlayStation profile with no Sony packer.</summary>
        PlayStationState,
        /// <summary>A Valve packer's frame (Step 5's ValveReportPackers.ForProfile branch).</summary>
        ValvePacked,
        /// <summary>SInput's packed report 0x01 (Step 5's SInputReportPacker.IsSInputProfile branch).</summary>
        SInputPacked,
        /// <summary>A layout past 32 buttons or one hat, packed against the
        /// slot's own descriptor (Step 5's ExtendedReportPacker.NeedsRawReport
        /// branch).</summary>
        ExtendedPacked,
        /// <summary>SubmitRawHidState (Step 5's hmExt.SubmitRawHidState call): every other Extended
        /// and Nintendo slot.</summary>
        RawState,
        /// <summary>SubmitKbmState to a virtual keyboard (Step 5's KeyboardMouseVirtualController branch).</summary>
        Keyboard,
        /// <summary>SubmitKbmState to a virtual mouse (Step 5's KeyboardMouseVirtualController branch).</summary>
        Mouse,
        /// <summary>SubmitMidiRawState (Step 5's MidiVirtualController branch).</summary>
        Midi,
        /// <summary>SubmitVrState (Step 5's HMaestroVRController branch).</summary>
        Vr,
    }

    internal enum ReadKind { XInput, Hid, RawKeyboard, RawMouse, None }

    /// <summary>One virtual controller family the probe creates: the slot
    /// type, the profile, the slot configuration Step 5 builds it from, the
    /// lane its frames take, and how Windows' delivery is read.</summary>
    internal sealed class Family
    {
        public string Name;
        public VirtualControllerType Type;
        public string ProfileId;
        public HMProfile Profile;
        public Route Route;
        public ReadKind Read;
        public bool Usbip;
        public CustomControllerLayout Layout;
        public bool Customize;
        public bool BuildsItsOwnReports;
        public string NotReadWhy;

        /// <summary>The extended report the HID read compares against when
        /// the report id matches: the profile's own, or a twin's where the
        /// profile shares a twin's frame.</summary>
        public ExtendedReportSpec Spec;
        public string SpecFrom;

        public string Lane => Route switch
        {
            Route.Gamepad => "SubmitGamepadState (Step 5, plain call)",
            Route.SonyPacked => "SonyReportPackers + SubmitRawReport (Step 5)",
            Route.PlayStationState => "SubmitGamepadState extended overload (Step 5)",
            Route.ValvePacked => "ValveReportPackers + SubmitRawReport (Step 5)",
            Route.SInputPacked => "SInputReportPacker + SubmitRawReport (Step 5)",
            Route.ExtendedPacked => "ExtendedReportPacker + SubmitPackedExtendedReport (Step 5)",
            Route.RawState => "SubmitRawHidState (Step 5)",
            Route.Keyboard or Route.Mouse => "SubmitKbmState (Step 5)",
            Route.Midi => "SubmitMidiRawState (Step 5)",
            _ => "SubmitVrState (Step 5)",
        };

        public string ReadHow => Read switch
        {
            ReadKind.XInput => "XInputGetState on the user index the controller took",
            ReadKind.RawKeyboard => "Raw Input on a message-only window, F24 down and up from the virtual keyboard's handle",
            ReadKind.RawMouse => "Raw Input on a message-only window, one count right and one back from the virtual mouse's handle",
            ReadKind.Hid => HidHow(),
            _ => "not read: " + NotReadWhy,
        };

        private string HidHow()
        {
            if (Route == Route.SInputPacked) return "HID read, SInput report 0x01 table (SINPUT-LIB-HID, SDL_hidapi_sinput.c)";
            if (Type == VirtualControllerType.Extended && Customize) return "HID read, the descriptor PadForge built (ExtendedReportPacker layout)";
            string s = Spec != null ? $"HID read, HIDMaestro extendedReport {Spec.ReportId} of {SpecFrom}" : "HID read";
            if (ProfileId == "switch-pro") s += ", report 0x30 table (switch-pro.json stateBlock)";
            if (ProfileId == Expect.SixaxisProfileId) s += ", the id-less joystick report table (DsHidMini DsHid.c)";
            if (BulkStart.Needs(this)) s = "HID read after the bulk start 03/0D (SDL_hidapi_switch2.c), HIDMaestro extendedReport " + Spec?.ReportId + " of " + SpecFrom + " with stick Y up high (SDL HandleSwitchProState)";
            return s + ", else Windows' HID parser on the device's own preparsed data";
        }
    }

    internal static class Families
    {
        /// <summary>The pad index every family is created on: the last of
        /// PadForge's sixteen, so its HIDMaestro identity key
        /// ("padforge:pad15:...", HMaestroVirtualController.IdentityKeyForPad)
        /// is one no PadForge slot of a group order uses.</summary>
        internal const int Pad = InputManager.MaxPads - 1;

        /// <summary>An unconnected controller, made as the tests make one
        /// (PadForge.Tests Switch2ProCompositeTests.Controller): its
        /// constructor reads the profile and only stores the context, so an
        /// empty context shell serves.</summary>
        internal static HMaestroVirtualController Unconnected(HMProfile profile, VirtualControllerType type)
        {
            var shell = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
            return new HMaestroVirtualController(shell, profile, type);
        }

        /// <summary>The layout counts a slot on this profile gets:
        /// PadViewModel.SeedExtendedConfigFromProfile
        /// into an ExtendedSlotConfig, then the CustomControllerLayout
        /// InputService.SyncExtendedConfigToSlot builds from it
        /// (its CustomControllerLayout initializer).</summary>
        internal static CustomControllerLayout SeededLayout(HMProfile profile, int buttonsOverride = -1)
        {
            var cfg = new ExtendedSlotConfig();
            cfg.TriggerCount = 0;
            cfg.ThumbstickCount = profile.StickCount;
            cfg.TriggerCount = profile.TriggerCount;
            cfg.PovCount = profile.HasHat ? 1 : 0;
            int buttons = profile.ButtonCount;
            if (MacroButtonNames.IsNintendoLetteredProfile(profile.Id))
                buttons = MacroButtonNames.NintendoLetteredCountFor(profile.Id);
            if (MacroButtonNames.IsValveLetteredProfile(profile.Id))
            {
                cfg.TriggerCount = 0;
                cfg.ThumbstickCount = NintendoPreviewMap.StickCount(profile.Id);
                cfg.TriggerCount = NintendoPreviewMap.TriggerCount(profile.Id);
                cfg.PovCount = NintendoPreviewMap.DPadIsHat(profile.Id) ? 1 : 0;
                buttons = NintendoPreviewMap.ButtonCount(profile.Id);
            }
            cfg.ButtonCount = buttonsOverride >= 0 ? buttonsOverride : buttons;
            return new CustomControllerLayout
            {
                Axes = cfg.TotalAxes,
                Buttons = cfg.ButtonCount,
                Povs = cfg.PovCount,
                Sticks = cfg.ThumbstickCount,
                Triggers = cfg.TriggerCount,
            };
        }

        /// <summary>The lane Step 5's Pass 3 sends this slot's frames on,
        /// asked with the predicates it asks, in its order
        /// (UpdateVirtualDevicesLocked). Extended and Nintendo slots ride the
        /// raw surface (InputService.SyncExtendedConfigToSlot sets
        /// SlotRawHidSurface).</summary>
        internal static Route RouteOf(VirtualControllerType type, string profileId, bool buildsItsOwnReports, in CustomControllerLayout layout)
        {
            switch (type)
            {
                case VirtualControllerType.Midi: return Route.Midi;
                case VirtualControllerType.Vr: return Route.Vr;
                case VirtualControllerType.KeyboardMouse: return Route.Keyboard;
                case VirtualControllerType.Extended:
                case VirtualControllerType.Nintendo:
                    if (ValveReportPackers.ForProfile(profileId) != null) return Route.ValvePacked;
                    if (SInputReportPacker.IsSInputProfile(profileId)) return Route.SInputPacked;
                    if (!buildsItsOwnReports && ExtendedReportPacker.NeedsRawReport(layout)) return Route.ExtendedPacked;
                    return Route.RawState;
                case VirtualControllerType.PlayStation:
                    return SonyReportPackers.ForProfile(profileId) != null ? Route.SonyPacked : Route.PlayStationState;
                default:
                    return Route.Gamepad;
            }
        }

        private static Family Make(string name, VirtualControllerType type, HMProfile profile, bool customize = false, int buttonsOverride = -1)
        {
            var f = new Family
            {
                Name = name,
                Type = type,
                ProfileId = profile.Id,
                Profile = profile,
                Usbip = profile.RequiresUsbipBackend,
                Customize = customize,
                BuildsItsOwnReports = profile.BuildsItsOwnReports,
            };
            if (type is VirtualControllerType.Extended or VirtualControllerType.Nintendo)
                f.Layout = SeededLayout(profile, buttonsOverride);
            f.Route = RouteOf(type, profile.Id, f.BuildsItsOwnReports, f.Layout);
            f.Read = f.Route == Route.Gamepad ? ReadKind.XInput : ReadKind.Hid;
            f.Spec = profile.ExtendedReport;
            f.SpecFrom = profile.Id;
            // The 2015 pad's two profiles share one frame
            // (ValveReportPackers.cs:70-73), and only the composite persona
            // carries HIDMaestro's spec of it.
            if (f.Spec == null && string.Equals(profile.Id, "steam-controller", StringComparison.OrdinalIgnoreCase))
            {
                var twin = HMaestroProfileCatalog.GetProfileById("steam-controller-composite");
                f.Spec = twin?.ExtendedReport;
                f.SpecFrom = "steam-controller-composite";
            }
            return f;
        }

        /// <summary>Every family, read from PadForge's own lists: each slot
        /// type's default profile (InputManager.GetDefaultProfileId), every
        /// PlayStation profile the catalog holds (HMaestroProfileCatalog.
        /// PlayStationProfiles, which splits into the Sony packer table and
        /// the extended state lane), every Valve and SInput profile with a
        /// packer (ValveReportPackers, SInputReportPacker), every Nintendo
        /// profile HIDMaestro defines a report for, the Extended Custom
        /// profile on the raw state lane and again past 32 buttons on the
        /// packed lane, and a keyboard and a mouse from the Keyboard + Mouse
        /// pickers. Xbox comes first: it is the positive control. Families on
        /// the USB/IP backend come last, since the first of them deploys
        /// usbip-win2.</summary>
        internal static List<Family> All()
        {
            var list = new List<Family>();
            HMProfile Find(string id) => HMaestroProfileCatalog.GetProfileById(id);

            var xbox = Find(InputManager.GetDefaultProfileId(VirtualControllerType.Xbox));
            if (xbox != null) list.Add(Make("xbox", VirtualControllerType.Xbox, xbox));

            foreach (var p in HMaestroProfileCatalog.PlayStationProfiles)
                list.Add(Make("playstation:" + p.Id, VirtualControllerType.PlayStation, p));

            var nintendoDefault = InputManager.GetDefaultProfileId(VirtualControllerType.Nintendo);
            foreach (var p in HMaestroProfileCatalog.NintendoProfiles)
            {
                if (string.Equals(p.Id, nintendoDefault, StringComparison.OrdinalIgnoreCase) || p.ExtendedReport != null)
                    list.Add(Make("nintendo:" + p.Id, VirtualControllerType.Nintendo, p));
            }

            var custom = Find(InputManager.GetDefaultProfileId(VirtualControllerType.Extended));
            if (custom != null)
            {
                // A new Extended slot on Custom has Customize forced on
                // (PadViewModel.ProfileId's setter).
                list.Add(Make("extended:" + custom.Id, VirtualControllerType.Extended, custom, customize: true));
                list.Add(Make("extended-packed:" + custom.Id, VirtualControllerType.Extended, custom, customize: true, buttonsOverride: 40));
            }

            foreach (var p in HMaestroProfileCatalog.ExtendedProfiles)
            {
                if (ValveReportPackers.ForProfile(p.Id) != null)
                    list.Add(Make("valve:" + p.Id, VirtualControllerType.Extended, p));
                else if (SInputReportPacker.IsSInputProfile(p.Id))
                    list.Add(Make("sinput:" + p.Id, VirtualControllerType.Extended, p));
            }

            var keyboard = PickKeyboard(out _);
            if (keyboard != null)
                list.Add(new Family
                {
                    Name = "keyboard:" + keyboard.Id, Type = VirtualControllerType.KeyboardMouse, ProfileId = keyboard.Id,
                    Profile = keyboard, Route = Route.Keyboard, Read = ReadKind.RawKeyboard, Usbip = keyboard.RequiresUsbipBackend,
                });
            var mouse = PickMouse(out _);
            if (mouse != null)
                list.Add(new Family
                {
                    Name = "mouse:" + mouse.Id, Type = VirtualControllerType.KeyboardMouse, ProfileId = mouse.Id,
                    Profile = mouse, Route = Route.Mouse, Read = ReadKind.RawMouse, Usbip = mouse.RequiresUsbipBackend,
                });

            // Positive control first, the rest in catalog order, USB/IP last.
            var ordered = list.Take(1).Concat(list.Skip(1).Where(f => !f.Usbip)).Concat(list.Skip(1).Where(f => f.Usbip)).ToList();
            return ordered;
        }

        /// <summary>The two slot types no HID or XInput read reaches, listed
        /// so the run says what it leaves out.</summary>
        internal static IEnumerable<Family> NotRead()
        {
            yield return new Family
            {
                Name = "midi", Type = VirtualControllerType.Midi, ProfileId = "-", Route = Route.Midi, Read = ReadKind.None,
                NotReadWhy = "a MIDI slot opens a Windows MIDI Services port (MidiVirtualController), not a HID or XInput device",
            };
            yield return new Family
            {
                Name = "vr", Type = VirtualControllerType.Vr, ProfileId = "-", Route = Route.Vr, Read = ReadKind.None,
                NotReadWhy = "a VR slot feeds HIDMaestro's SteamVR driver through shared memory (HMaestroVRController), which only a running vrserver reads",
            };
        }

        /// <summary>The first keyboard in the Keyboard + Mouse picker whose
        /// report can carry F24: KbmHidKeyboard.Apply skips a key the
        /// profile's caps cannot send (its sendable check).</summary>
        internal static HMProfile PickKeyboard(out string why)
        {
            why = null;
            var usage = KbmHidKeyMap.Resolve(KnownState.VkF24);
            if (!usage.IsKeyboard || usage.Usage != KnownState.F24Usage)
            {
                why = $"VK_F24 resolves to page {usage.Page:X2} usage {usage.Usage:X2}, not 07:73";
                return null;
            }
            foreach (var p in HMaestroProfileCatalog.KeyboardProfiles)
            {
                var caps = KbmDeviceCaps.For(p);
                if (caps != null && caps.CanSendKey(KnownState.F24Usage)) return p;
            }
            why = "no keyboard profile's report carries usage 0x73 (F24)";
            return null;
        }

        /// <summary>The first mouse in the Keyboard + Mouse picker.</summary>
        internal static HMProfile PickMouse(out string why)
        {
            why = null;
            foreach (var p in HMaestroProfileCatalog.MouseProfiles)
            {
                var caps = KbmDeviceCaps.For(p);
                if (caps != null && caps.HasMouse) return p;
            }
            why = "no mouse profile declares a mouse collection";
            return null;
        }
    }
}
