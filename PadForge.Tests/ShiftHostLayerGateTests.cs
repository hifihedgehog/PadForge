using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The host-layer condition on shift activators (#370 follow-up, the
    /// native form of the per-layer web #377's macro action first carried):
    /// ShiftActivator.HostLayerMask makes an activator's input count only
    /// while that layer is engaged, decided at the press and latched for
    /// the whole press. Empty is every pre-v9 activator's behavior.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ShiftHostLayerGateTests
    {
        private static ShiftActivator Latch(string mask, int button, string host = "")
            => new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = "Button " + button,
                Mode = "Custom",
                LayerMask = mask,
                LayerName = mask,
                Kind = "Button",
                HostLayerMask = host,
            };

        /// <summary>The requester's graph shape, natively: an ungated Latch
        /// enters Shifter, and a second Latch on another button only works
        /// FROM Shifter. Pressing it in Base does nothing, pressing it in
        /// Shifter jumps to RP, and pressing it again in RP (its host no
        /// longer engaged) does nothing.</summary>
        [Fact]
        public void GatedLatch_FiresOnlyInItsHostLayer()
        {
            InputManager.ClearAllShiftRuntime();
            const int slot = 5;
            var ms = new MappingSet();
            ms.ShiftActivators.Add(Latch("Shifter", 20));
            ms.ShiftActivators.Add(Latch("RP", 21, host: "Shifter"));

            var state = new CustomInputState();

            // Press 21 in Base: the gate is closed, nothing engages.
            state.Buttons[21] = true;
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
            state.Buttons[21] = false;
            InputManager.ResolveActiveLayerMask(slot, ms, state, "");

            // Enter Shifter through the ungated Latch.
            state.Buttons[20] = true;
            Assert.Equal("Shifter", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
            state.Buttons[20] = false;
            Assert.Equal("Shifter", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));

            // Press 21 in Shifter: gate open, jump to RP.
            state.Buttons[21] = true;
            Assert.Equal("RP", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
            state.Buttons[21] = false;
            Assert.Equal("RP", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));

            // Press 21 in RP: its host is no longer engaged, so the Latch
            // does NOT toggle-release. The button belongs to RP's own rows.
            state.Buttons[21] = true;
            Assert.Equal("RP", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));

            InputManager.ClearAllShiftRuntime();
        }

        /// <summary>The press-latch, both ways. A Hold whose host is Base
        /// stays engaged across ticks even though its own engagement left
        /// Base (no oscillation), and a press that began outside the host
        /// stays dead even when the host becomes engaged mid-hold (entering
        /// a layer never conscripts an already-held button).</summary>
        [Fact]
        public void PressLatch_HoldsTheVerdictForTheWholePress()
        {
            InputManager.ClearAllShiftRuntime();
            const int slot = 6;
            var ms = new MappingSet();
            ms.ShiftActivators.Add(Latch("Shifter", 20));
            ms.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = "Button 21",
                Mode = "Hold",
                LayerMask = "RP",
                LayerName = "RP",
                Kind = "Button",
                HostLayerMask = "Base",
            });

            var state = new CustomInputState();

            // Press in Base: engages RP, and STAYS engaged on later ticks
            // although the engaged layer is now RP, not the host.
            state.Buttons[21] = true;
            Assert.Equal("RP", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
            Assert.Equal("RP", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
            state.Buttons[21] = false;
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));

            // Enter Shifter, THEN press and hold 21: its host (Base) is not
            // engaged at the press, so the whole press stays dead...
            state.Buttons[20] = true;
            Assert.Equal("Shifter", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
            state.Buttons[20] = false;
            state.Buttons[21] = true;
            Assert.Equal("Shifter", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));

            // ...even after a Latch release returns the slot to Base while
            // 21 is still physically held.
            state.Buttons[20] = true;   // Latch toggles Shifter off
            InputManager.ResolveActiveLayerMask(slot, ms, state, "");
            state.Buttons[20] = false;
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));

            // A fresh press inside the host works again.
            state.Buttons[21] = false;
            InputManager.ResolveActiveLayerMask(slot, ms, state, "");
            state.Buttons[21] = true;
            Assert.Equal("RP", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));

            InputManager.ClearAllShiftRuntime();
        }

        /// <summary>Cycle rides the gate on BOTH buttons: a Base-hosted
        /// cycle steps once, and once the step leaves Base neither Next nor
        /// Previous moves the cursor again.</summary>
        [Fact]
        public void GatedCycle_GatesNextAndPreviousLegs()
        {
            InputManager.ClearAllShiftRuntime();
            const int slot = 7;
            var ms = new MappingSet();
            ms.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = "Button 20",
                CyclePrevDescriptor = "Button 21",
                Mode = "Cycle",
                LayerMask = "Ring",
                LayerName = "Ring",
                Kind = "Button",
                CycleLayers = "L1|L2",
                HostLayerMask = "Base",
            });
            ms.ShiftActivators.Add(Latch("L1", 25));
            ms.ShiftActivators.Add(Latch("L2", 26));

            var state = new CustomInputState();

            // Next in Base: steps to L1.
            state.Buttons[20] = true;
            Assert.Equal("L1", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
            state.Buttons[20] = false;
            InputManager.ResolveActiveLayerMask(slot, ms, state, "");

            // Next again, now in L1: gate closed, no step.
            state.Buttons[20] = true;
            Assert.Equal("L1", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
            state.Buttons[20] = false;
            InputManager.ResolveActiveLayerMask(slot, ms, state, "");

            // Previous in L1: gate closed on its own leg too.
            state.Buttons[21] = true;
            Assert.Equal("L1", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
            state.Buttons[21] = false;

            InputManager.ClearAllShiftRuntime();
        }

        /// <summary>An empty HostLayerMask is the pre-v9 contract: the
        /// serializer defaults it, an attribute-less XML deserializes to
        /// empty, and a set value round-trips.</summary>
        [Fact]
        public void HostLayerMask_DefaultsEmptyAndRoundTrips()
        {
            Assert.Equal("", new ShiftActivator().HostLayerMask);

            var ser = new XmlSerializer(typeof(ShiftActivator));

            var act = new ShiftActivator { LayerMask = "RP", HostLayerMask = "Shifter" };
            using var w = new StringWriter();
            ser.Serialize(w, act);
            using var r = new StringReader(w.ToString());
            var back = (ShiftActivator)ser.Deserialize(r);
            Assert.Equal("Shifter", back.HostLayerMask);

            // Pre-v9 XML carries no attribute: loads as empty (any layer).
            using var old = new StringReader(
                "<ShiftActivator LayerMask=\"RP\" Descriptor=\"Button 3\" />");
            var legacy = (ShiftActivator)ser.Deserialize(old);
            Assert.Equal("", legacy.HostLayerMask);

            // Clone is memberwise and must carry the condition.
            Assert.Equal("Shifter", act.Clone().HostLayerMask);
        }

        /// <summary>Source contracts on the surfaces a runtime test cannot
        /// reach: the rename sweep retags host conditions before the
        /// CycleLayers early-continue, Configure copies the field in place,
        /// and the dialog authors it (row in the markup, value in the saved
        /// Result).</summary>
        [Fact]
        public void RenameConfigureAndDialogContracts()
        {
            string page = RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            int sweep = page.IndexOf("if (string.Equals(a.HostLayerMask, oldMask, StringComparison.Ordinal))", StringComparison.Ordinal);
            int cont = page.IndexOf("if (string.IsNullOrEmpty(a.CycleLayers)) continue;", StringComparison.Ordinal);
            Assert.True(sweep > 0, "RenameMaskEverywhere lost the HostLayerMask sweep");
            Assert.True(cont > sweep, "the HostLayerMask sweep must run before the CycleLayers early-continue");
            Assert.Contains("existing.HostLayerMask = dlg.Result.HostLayerMask;", page);

            string dlgXaml = RepoText("PadForge.App", "Views", "ShiftActivatorDialog.xaml");
            Assert.Contains("x:Name=\"HostLayerCombo\"", dlgXaml);
            Assert.Contains("Pad_Shift_HostLayer,", dlgXaml);

            string dlgCs = RepoText("PadForge.App", "Views", "ShiftActivatorDialog.xaml.cs");
            Assert.Contains("HostLayerMask = hostLayerMask,", dlgCs);
            Assert.Contains("HostLayerRow.Visibility = isPassive", dlgCs);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
