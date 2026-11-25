using System;
using System.IO;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Editing the ACTIVE layer's rows has to put the edit on screen, and
    /// pasting a layer the user copied has to apply even when that layer was
    /// empty.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class LayerClipboardRefreshTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        /// <summary>The setter returns on equality before raising anything, so
        /// assigning the same mask twice cannot drive a reload. That was the
        /// trick the paste and clear handlers used, and it did nothing at all
        /// when the active layer was the one being edited.</summary>
        [Fact]
        public void AssigningTheSameMaskRaisesNothing()
        {
            var vm = new PadViewModel(0);
            vm.ActiveLayerMask = "Base";

            int raised = 0;
            vm.LayerActivated += (_, _) => raised++;

            vm.ActiveLayerMask = "Base";
            vm.ActiveLayerMask = "Base";

            Assert.Equal(0, raised);
        }

        /// <summary>The reload asks for a re-read without pretending the layer
        /// changed, so it works on the active layer, Base included.</summary>
        [Fact]
        public void TheReloadRaisesOnTheActiveLayer()
        {
            var vm = new PadViewModel(0);
            vm.ActiveLayerMask = "Base";

            int raised = 0;
            vm.LayerActivated += (_, _) => raised++;

            vm.ReloadActiveLayerRows();

            Assert.Equal(1, raised);
            // And it did not move the active layer while doing it.
            Assert.Equal("Base", vm.ActiveLayerMask);
        }

        /// <summary>Both handlers use the reload rather than the old two-step
        /// assignment, which silently did nothing on Base: the clear on the
        /// page's controller, and the paste on the controller it lands on,
        /// which a program's press names (#600, PadPage.PasteLayer).</summary>
        [Fact]
        public void BothLayerEditHandlersUseTheReload()
        {
            string cs = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Views", "PadPage.xaml.cs"));

            int reloads = System.Text.RegularExpressions.Regex.Matches(
                cs, @"(_currentPadVm|\bpad)\.ReloadActiveLayerRows\(\);").Count;
            Assert.Equal(2, reloads);
            string paste = cs.Substring(cs.IndexOf("internal static void PasteLayer(", StringComparison.Ordinal));
            Assert.Contains("pad.ReloadActiveLayerRows();", paste.Substring(0, paste.IndexOf("private void ShiftLayer_Clear_Click(", StringComparison.Ordinal)));

            Assert.DoesNotContain("_currentPadVm.ActiveLayerMask = \"Base\";", cs);
            Assert.DoesNotContain("pad.ActiveLayerMask = \"Base\";", cs);
        }

        /// <summary>Nothing copied and an empty layer copied are different
        /// states, and the clipboard already tells them apart: copy always
        /// assigns a fresh list, so null means nothing was copied. The paste
        /// guard folded them together, so copying an empty layer and pasting
        /// it left the destination untouched with nothing to say why.</summary>
        [Fact]
        public void PasteRefusesOnlyWhenNothingWasEverCopied()
        {
            string cs = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Views", "PadPage.xaml.cs"));

            // A person's Paste hands the paste what Copy kept, and the paste
            // refuses only null.
            Assert.Contains("=> PasteLayer(_currentPadVm, TagToLayerMask(sender), _shiftLayerClipboard);", cs);
            int at = cs.IndexOf("internal static void PasteLayer(", StringComparison.Ordinal);
            Assert.True(at > 0);
            string paste = cs.Substring(at, cs.IndexOf("private void ShiftLayer_Clear_Click(", at, StringComparison.Ordinal) - at);
            Assert.Contains("if (rows == null) return;", paste);
            Assert.DoesNotContain("rows.Count == 0) return;", paste);
            Assert.DoesNotContain("_shiftLayerClipboard.Count == 0) return;", cs);
        }
    }
}
