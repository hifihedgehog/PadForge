using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Slot Copy / Paste carries macros.
    ///
    /// <para>They were left out on purpose when the Macros tab sat visibly
    /// apart and "Copy" on the Mappings tab read as "not macros" on its own.
    /// That stopped being self-explanatory once the tooltip said all settings
    /// and every OTHER whole-slot transfer (profile apply, the Macros tab's
    /// Copy From) carried them: the one exception the interface did not name
    /// became the confusing part.</para>
    ///
    /// <para>The wire shape is the Macros tab's own clipboard envelope
    /// (#112) and the re-scoping is the single-macro paste's, so the two
    /// gestures cannot disagree on how a macro crosses a slot.</para></summary>
    [Collection("SettingsManagerStatics")]
    public class SlotMacrosClipboardTests
    {
        private static MacroItem Macro(string name, ushort trigger = 0x1000)
            => new MacroItem { Name = name, TriggerButtons = trigger, IsEnabled = true };

        private static string Envelope(PadViewModel src)
        {
            var data = new List<MacroData>(src.Macros.Count);
            foreach (var m in src.Macros) data.Add(SettingsService.BuildMacroDataForMacro(m, src.PadIndex));
            return SettingsService.SerializeMacrosToClipboard(data.ToArray());
        }

        private static IDisposable CleanSets()
        {
            var saved = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
            for (int i = 0; i < SettingsManager.SlotMappingSets.Length; i++)
                SettingsManager.SlotMappingSets[i] = null;
            return new Restore(saved);
        }

        private sealed class Restore : IDisposable
        {
            private readonly MappingSet[] _saved;
            public Restore(MappingSet[] saved) { _saved = saved; }
            public void Dispose()
            {
                for (int i = 0; i < _saved.Length; i++)
                    SettingsManager.SlotMappingSets[i] = _saved[i];
            }
        }

        // ── replace semantics ───────────────────────────────────────────────

        /// <summary>The destination's macros are REPLACED, not appended to.
        /// Every other surface this paste touches replaces, and a paste that
        /// replaced everything except macros would double the list on every
        /// re-paste. The positive control is that the destination really had
        /// macros of its own beforehand, so "they are gone" is a real
        /// observation and not a vacuously empty list.</summary>
        [Fact]
        public void Paste_ReplacesTheDestinationsMacros()
        {
            using var _sets = CleanSets();
            var src = new PadViewModel(0);
            src.Macros.Add(Macro("Source A"));
            src.Macros.Add(Macro("Source B"));

            var dst = new PadViewModel(1);
            dst.Macros.Add(Macro("Dest old 1"));
            dst.Macros.Add(Macro("Dest old 2"));
            dst.Macros.Add(Macro("Dest old 3"));
            Assert.Equal(3, dst.Macros.Count);   // positive control

            MainWindow.ApplySlotMacrosFromClipboard(dst, Envelope(src));

            Assert.Equal(2, dst.Macros.Count);
            Assert.Equal("Source A", dst.Macros[0].Name);
            Assert.Equal("Source B", dst.Macros[1].Name);
            Assert.DoesNotContain(dst.Macros, m => m.Name.StartsWith("Dest old", StringComparison.Ordinal));
        }

        /// <summary>Every pasted macro belongs to the destination slot. A
        /// macro that kept the source's PadIndex would evaluate against the
        /// wrong slot's state.</summary>
        [Fact]
        public void Paste_RestampsPadIndex()
        {
            using var _sets = CleanSets();
            var src = new PadViewModel(0);
            src.Macros.Add(Macro("M"));
            var dst = new PadViewModel(3);

            MainWindow.ApplySlotMacrosFromClipboard(dst, Envelope(src));

            Assert.All(dst.Macros, m => Assert.Equal(3, m.PadIndex));
        }

        /// <summary>Copying a clean slot over a busy one must produce a clean
        /// slot. Copy writes the field unconditionally so that this holds;
        /// writing it only when non-empty would quietly become append
        /// semantics for exactly this case.</summary>
        [Fact]
        public void Paste_FromASlotWithNoMacros_ClearsTheDestination()
        {
            using var _sets = CleanSets();
            var src = new PadViewModel(0);           // no macros
            var dst = new PadViewModel(1);
            dst.Macros.Add(Macro("Dest old"));
            Assert.Single(dst.Macros);               // positive control

            string env = Envelope(src);
            Assert.False(string.IsNullOrEmpty(env)); // the field IS written for an empty slot
            MainWindow.ApplySlotMacrosFromClipboard(dst, env);

            Assert.Empty(dst.Macros);
            Assert.Null(dst.SelectedMacro);
        }

        /// <summary>An ABSENT field is an older clipboard that never carried
        /// macros, and wiping the destination's on that evidence would be
        /// destructive for no reason. Distinct from the present-but-empty case
        /// above, and the two must not be collapsed.</summary>
        [Fact]
        public void Paste_WithNoMacroField_LeavesTheDestinationAlone()
        {
            using var _sets = CleanSets();
            var dst = new PadViewModel(1);
            dst.Macros.Add(Macro("Dest old"));

            MainWindow.ApplySlotMacrosFromClipboard(dst, null);
            Assert.Single(dst.Macros);

            MainWindow.ApplySlotMacrosFromClipboard(dst, string.Empty);
            Assert.Single(dst.Macros);
        }

        /// <summary>A payload that is not a macro envelope (someone pasted a
        /// different PadForge clipboard, or garbage) is inert rather than a
        /// clear, for the same reason.</summary>
        [Fact]
        public void Paste_UnparseablePayload_LeavesTheDestinationAlone()
        {
            using var _sets = CleanSets();
            var dst = new PadViewModel(1);
            dst.Macros.Add(Macro("Dest old"));

            MainWindow.ApplySlotMacrosFromClipboard(dst, "{ not an envelope");
            Assert.Single(dst.Macros);

            MainWindow.ApplySlotMacrosFromClipboard(dst, "{\"Type\":\"something-else\",\"Macros\":[]}");
            Assert.Single(dst.Macros);
        }

        // ── the re-scoping the single-macro paste already does ──────────────

        /// <summary>A layer scope belongs to the SOURCE slot's layer set
        /// (audit 2026-07-25 C8). Carrying it across to a slot that does not
        /// declare the layer would leave the macro gated on a foreign slot's
        /// layer through the split-config fallback, with no way for the
        /// picker to show or clear it. The slot paste applies the same rule
        /// as the single-macro paste and Copy From.</summary>
        [Fact]
        public void Paste_DropsALayerMaskTheDestinationDoesNotDeclare()
        {
            using var _sets = CleanSets();
            // No slot declares any shift layer, so "Layer 2" is foreign
            // everywhere and must be dropped.
            var src = new PadViewModel(0);
            var m = Macro("Layered");
            m.LayerMask = "Layer 2";
            src.Macros.Add(m);
            var dst = new PadViewModel(1);

            MainWindow.ApplySlotMacrosFromClipboard(dst, Envelope(src));

            Assert.Single(dst.Macros);
            Assert.Equal(string.Empty, dst.Macros[0].LayerMask);
        }

        /// <summary>Positive control for the rule above: a mask the
        /// destination DOES declare survives, so the previous test is about
        /// the rule and not about masks never crossing.</summary>
        [Fact]
        public void Paste_KeepsALayerMaskTheDestinationDeclares()
        {
            using var _sets = CleanSets();
            SettingsManager.SlotMappingSets[1] = new MappingSet
            {
                ShiftActivators = new List<ShiftActivator>
                {
                    new ShiftActivator { LayerMask = "Layer 2", Descriptor = "Button 9" },
                },
            };
            var src = new PadViewModel(0);
            var m = Macro("Layered");
            m.LayerMask = "Layer 2";
            src.Macros.Add(m);
            var dst = new PadViewModel(1);

            MainWindow.ApplySlotMacrosFromClipboard(dst, Envelope(src));

            Assert.Single(dst.Macros);
            Assert.Equal("Layer 2", dst.Macros[0].LayerMask);
        }

        /// <summary>Base is always declared, so a Base-scoped macro never loses
        /// its scope on a slot with no layers at all.</summary>
        [Fact]
        public void Paste_KeepsABaseLayerMask()
        {
            using var _sets = CleanSets();
            var src = new PadViewModel(0);
            var m = Macro("Base");
            m.LayerMask = "Base";
            src.Macros.Add(m);
            var dst = new PadViewModel(1);

            MainWindow.ApplySlotMacrosFromClipboard(dst, Envelope(src));
            Assert.Equal("Base", dst.Macros[0].LayerMask);
        }

        // ── the payload crosses the clipboard envelope ──────────────────────

        /// <summary>The field has to survive PadSetting's JSON envelope, which
        /// carries it as an opaque string under its own key. A build and apply
        /// that agree in process while the envelope drops the field would be
        /// exactly the shape of the defect the Keep Awake fix just closed.</summary>
        [Fact]
        public void SlotMacros_SurviveThePadSettingJsonEnvelope()
        {
            using var _sets = CleanSets();
            var src = new PadViewModel(0);
            src.Macros.Add(Macro("Round trip"));
            var ps = new PadSetting { SlotMacrosJson = Envelope(src) };

            string wire = ps.ToJson(VirtualControllerType.Xbox, false);
            var back = PadSetting.FromJson(wire, out _, out _);

            Assert.NotNull(back);
            Assert.Equal(ps.SlotMacrosJson, back.SlotMacrosJson);

            var dst = new PadViewModel(1);
            MainWindow.ApplySlotMacrosFromClipboard(dst, back.SlotMacrosJson);
            Assert.Single(dst.Macros);
            Assert.Equal("Round trip", dst.Macros[0].Name);
        }
    }
}
