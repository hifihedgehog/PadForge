using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What a device's row offers on the Devices page: Force Raw Joystick
    /// Mode for a row read raw, the hiding switches only where HidHide or
    /// Consume Input can act (never on a row PadForge or an extra feeds, a
    /// vendor's row or a merged row's HidHide), and Submit Mapping only for
    /// hardware a mapping describes.
    /// </summary>
    public class DeviceRowGateTests
    {
        /// <summary>Force Raw Joystick Mode is offered on rows typed Gamepad.
        /// A row of another type whose saved flag is on is read raw all the
        /// same (a hand-edited file, or a record whose type changed after
        /// the flag was set), so it shows the checkbox, the one control on
        /// the page that turns the flag off. With the flag off such a row
        /// offers none.</summary>
        [Theory]
        [InlineData("Wheel")]
        [InlineData("Joystick")]
        [InlineData("FlightStick")]
        [InlineData("Supplemental")]
        public void ARowReadRawShowsTheCheckboxThatTurnsItOff_WhateverItsType(string typeKey)
        {
            var vm = new DeviceRowViewModel { DeviceTypeKey = typeKey, DevicePath = @"\?\hid#vid_046d&pid_c262" };
            Assert.False(vm.ShowInputModeSection, "control: a " + typeKey + " row with the flag off offers no checkbox");

            var raised = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.ForceRawJoystickMode = true;
            Assert.True(vm.ShowInputModeSection, "a " + typeKey + " row read raw had no checkbox to turn it off");
            Assert.Contains(nameof(DeviceRowViewModel.ShowInputModeSection), raised);

            raised.Clear();
            vm.ForceRawJoystickMode = false;
            Assert.False(vm.ShowInputModeSection);
            Assert.Contains(nameof(DeviceRowViewModel.ShowInputModeSection), raised);
        }

        /// <summary>The flag acts on a row HidHide cannot cloak as well: a web
        /// pad or a Remote Link copy read raw answers no Gamepad name
        /// (InputManager.SpeaksGamepadLayout). Such a row shows the checkbox
        /// while its flag is on, and no Input Hiding.</summary>
        [Theory]
        [InlineData("web://controller/1")]
        [InlineData("peer://pc/1")]
        public void ASyntheticRowReadRawShowsTheCheckboxToo(string devicePath)
        {
            var vm = new DeviceRowViewModel { DeviceTypeKey = "Gamepad", DevicePath = devicePath };
            Assert.False(vm.ShowInputModeSection);

            vm.ForceRawJoystickMode = true;
            Assert.True(vm.ShowInputModeSection);
            Assert.False(vm.ShowInputHidingSection);
        }

        /// <summary>
        /// Every synthetic row must answer IsInternalVirtual, because HidHide
        /// has no HID instance to cloak for any of them and the Input Hiding
        /// section would otherwise render toggles that can never do anything.
        ///
        /// <para>The OpenXR hand rows and the Logitech G-keys row were added in
        /// 4.5.0 and missed the list, which is the sibling-set gap this pins.</para>
        /// </summary>
        [Theory]
        [InlineData("web://controller/1")]
        [InlineData("overlay://touchpad/0")]
        [InlineData("midi://in/0")]
        [InlineData("peer://pc/1")]
        [InlineData("nfc://reader/0")]
        [InlineData("mic://capture/0")]
        [InlineData("handheld://buttons")]
        [InlineData("sensor://motion")]
        [InlineData("headtrack://opentrack")]
        [InlineData("openxr://hand/left")]
        [InlineData("openxr://hand/right")]
        [InlineData("logigkeys://local")]
        [InlineData("analogkb://x")]
        [InlineData("razerchroma://local")]
        [InlineData("logilightsync://local")]
        [InlineData("steelseriesgg://local")]
        [InlineData("razersensa://local")]
        [InlineData("lightunit://0123456789abcdef0123456789abcdef")]
        [InlineData("keyboardscreen://hid:046d:c222:{00000000-0000-0000-0000-000000000000}")]
        [InlineData("keyboardscreen://arx")]
        [InlineData("falcon://11204")]
        public void EverySyntheticPath_IsInternalVirtual(string devicePath)
        {
            var vm = new DeviceRowViewModel { DevicePath = devicePath };
            Assert.True(vm.IsInternalVirtual, devicePath + " must not offer HidHide toggles");
        }

        /// <summary>
        /// Every scheme the source assigns to a row's DevicePath answers
        /// IsInternalVirtual, read from the app and the engine, so a new or
        /// renamed scheme cannot slip past the hand-written list above.
        ///
        /// <para>When each keyboard screen got its own row (#519), the rows
        /// moved to keyboardscreen:// and the list kept the old
        /// keyboardscreenkeys://, so every screen row offered HidHide toggles
        /// that could never do anything.</para>
        /// </summary>
        [Fact]
        public void EverySchemeTheSourceAssigns_IsInternalVirtual()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SharedVersion.cs"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var schemes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string project in new[] { "PadForge.App", "PadForge.Engine" })
                foreach (string file in Directory.EnumerateFiles(Path.Combine(dir.FullName, project), "*.cs", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(dir.FullName, file);
                    if (rel.Split(Path.DirectorySeparatorChar).Any(s => s is "obj" or "bin")) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(file), @"DevicePath\s*=\s*\$?""([a-z0-9]+://)"))
                        schemes.Add(m.Groups[1].Value);
                }
            // A merged row answers IsAggregate instead, pinned below.
            schemes.Remove("aggregate://");
            Assert.Contains("keyboardscreen://", schemes);
            Assert.Contains("falcon://", schemes);
            foreach (string scheme in schemes)
                Assert.True(new DeviceRowViewModel { DevicePath = scheme + "x" }.IsInternalVirtual,
                    scheme + " rows would offer HidHide toggles that can never do anything");
        }

        /// <summary>The vendor rows (#494) stand for a vendor's software, not
        /// a HID device, so none offers Input Hiding, whatever kind is added
        /// later: each kind's own row is built and asked.</summary>
        [Fact]
        public void EveryVendorRow_OffersNoInputHiding()
        {
            foreach (PadForge.Common.Input.Peripherals.PeripheralRowKind kind
                in Enum.GetValues(typeof(PadForge.Common.Input.Peripherals.PeripheralRowKind)))
            {
                using var row = new PadForge.Common.Input.Peripherals.PeripheralOutputRow(kind);
                var vm = new DeviceRowViewModel
                {
                    DeviceTypeKey = PadForge.Common.Input.Peripherals.PeripheralOutputRow.IsLightingRow(kind)
                        ? "PeripheralLighting" : "PeripheralHaptics",
                    DevicePath = row.DevicePath,
                };
                Assert.False(vm.ShowHidHideToggle, kind + " offers HidHide");
                Assert.False(vm.ShowInputHidingSection, kind + " shows the Input Hiding section");
            }
        }

        /// <summary>A real HID path is not synthetic. Without this the theory
        /// above would pass against a property hard-coded to true.</summary>
        [Fact]
        public void ARealHidPath_IsNotInternalVirtual()
        {
            var vm = new DeviceRowViewModel { DevicePath = @"\?\hid#vid_054c&pid_042f" };
            Assert.False(vm.IsInternalVirtual);
        }

        /// <summary>
        /// A merged row stands for every device of its kind and has no HID
        /// instance of its own, so HidHide has nothing to cloak for it. The
        /// hiding pass skips such a row on both of its lookups (its path names
        /// no instance and its vendor and product ids are zero), and the box
        /// was offered there anyway: it took the tick, saved it and hid
        /// nothing.
        ///
        /// <para>The four merged rows do not end up the same. Consume Input
        /// is a separate route that does work on a merged keyboard or mouse,
        /// so those two keep the section with that one box in it. Touchpads
        /// and consumer controls have neither box, so the heading goes as
        /// well, or it would sit over an empty body.</para>
        /// </summary>
        [Theory]
        [InlineData("aggregate://keyboards", "Keyboard", true)]
        [InlineData("aggregate://mice", "Mouse", true)]
        [InlineData("aggregate://touchpads", "Touchpad", false)]
        [InlineData("aggregate://consumercontrols", "ConsumerControl", false)]
        public void AMergedRow_NeverOffersHidHide_AndKeepsConsumeWhereItWorks(
            string devicePath, string typeKey, bool consumeApplies)
        {
            var vm = new DeviceRowViewModel { DeviceTypeKey = typeKey, DevicePath = devicePath };

            Assert.True(vm.IsAggregate);
            Assert.False(vm.ShowHidHideToggle, devicePath + " has no HID instance for HidHide to cloak");
            Assert.Equal(consumeApplies, vm.ShowConsumeToggle);
            Assert.Equal(consumeApplies, vm.ShowInputHidingSection);
        }

        /// <summary>The physical devices the merged rows stand for keep the
        /// box. Taking it off a real keyboard to fix the merged row would
        /// trade one defect for a worse one.</summary>
        [Theory]
        [InlineData("Keyboard")]
        [InlineData("Mouse")]
        [InlineData("Gamepad")]
        [InlineData("ConsumerControl")]
        public void APhysicalRow_StillOffersHidHide(string typeKey)
        {
            var vm = new DeviceRowViewModel { DeviceTypeKey = typeKey, DevicePath = @"\?\hid#vid_046d&pid_c33f" };

            Assert.False(vm.IsAggregate);
            Assert.True(vm.ShowHidHideToggle);
            Assert.True(vm.ShowInputHidingSection);
        }

        /// <summary>A row is built before its type and path are known, and
        /// they arrive in either order. The heading now follows the two
        /// boxes, and one of those follows the type, so setting the type has
        /// to raise the heading's change too or a binding made against the
        /// empty row keeps the stale answer.</summary>
        [Fact]
        public void SettingTheTypeAfterThePath_RaisesTheHidingSectionsChange()
        {
            var vm = new DeviceRowViewModel { DevicePath = "aggregate://keyboards" };
            Assert.False(vm.ShowInputHidingSection);

            var raised = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.DeviceTypeKey = "Keyboard";

            Assert.True(vm.ShowInputHidingSection);
            Assert.Contains(nameof(DeviceRowViewModel.ShowConsumeToggle), raised);
            Assert.Contains(nameof(DeviceRowViewModel.ShowInputHidingSection), raised);
        }

        /// <summary>
        /// Submitting a mapping opens a pre-filled GitHub issue about a piece of
        /// hardware, so a row backed by a runtime or an SDK has nothing to
        /// submit. VrController and LogitechGKeys missed this list in 4.5.0, and
        /// the list's AnalogKeyboard entry had no case here.
        /// </summary>
        [Theory]
        [InlineData("Gamepad")]
        [InlineData("Mouse")]
        [InlineData("Keyboard")]
        [InlineData("Touchpad")]
        [InlineData("Tablet")]
        [InlineData("Midi")]
        [InlineData("Nfc")]
        [InlineData("HeadsetMotion")]
        [InlineData("Microphone")]
        [InlineData("HandheldButtons")]
        [InlineData("ConsumerControl")]
        [InlineData("SystemMotion")]
        [InlineData("HeadTracker")]
        [InlineData("VrController")]
        [InlineData("LogitechGKeys")]
        [InlineData("AnalogKeyboard")]
        [InlineData("WebMenus")]
        [InlineData("PeripheralLighting")]
        [InlineData("PeripheralHaptics")]
        public void RowsWithNoHardwareToDescribe_DoNotOfferSubmitMapping(string typeKey)
        {
            var vm = new DeviceRowViewModel { DeviceTypeKey = typeKey };
            Assert.False(vm.ShowSubmitMapping, typeKey + " must not offer Submit Device Mapping");
        }

        /// <summary>A joystick still offers it, which is the whole point of the
        /// button. Without this the theory above proves nothing.</summary>
        [Fact]
        public void AJoystick_StillOffersSubmitMapping()
        {
            var vm = new DeviceRowViewModel { DeviceTypeKey = "Joystick" };
            Assert.True(vm.ShowSubmitMapping);
        }

    }
}
