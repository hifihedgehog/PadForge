using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #395, @heinthanth. The Extended card's four layout count
    /// boxes displayed the preset's HID descriptor while the user's edits went
    /// to the slot's ExtendedConfig, so a slot built with 1 trigger and 24
    /// buttons showed the preset's 2 and 16, and toggling the OEM override
    /// checkbox put the preset's numbers back on screen.
    ///
    /// <para>The rule these pin is general: a control that WRITES to one field
    /// and READS from another lies about the state of the system. The three
    /// identity fields on the same card had it right all along.</para>
    /// </summary>
    public class ExtendedLayoutCountsTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Src(params string[] rel)
            => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(rel)));

        private static string PadPage()
            => Src("PadForge.App", "Views", "PadPage.xaml.cs");

        /// <summary>Extracts one method body by brace matching from its
        /// signature, so a pin reads the method rather than the whole file and
        /// cannot pass on a match that lives somewhere else.</summary>
        private static string Body(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, $"method not found: {signature}");
            int open = src.IndexOf('{', at);
            Assert.True(open > 0);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                    return src.Substring(open, i - open + 1);
            }
            throw new Xunit.Sdk.XunitException($"unbalanced braces after {signature}");
        }

        private static readonly string[] CountBoxes =
        {
            "RawStickCountBox", "ExtendedTriggerCountBox",
            "RawPovCountBox", "RawButtonCountBox",
        };

        // ── The rule: what a control writes is what it reads ─────────────

        /// <summary>The defect in one assertion. Each count box is populated
        /// from the same object its edits are stored on, so the number on the
        /// card is the number the slot is built with.
        ///
        /// <para>Before the fix, the populate path read
        /// <c>profile.ButtonCount</c> and friends while the edit path wrote
        /// <c>vm.ExtendedConfig.ButtonCount</c>, and nothing in the codebase
        /// objected: the binding lens only asks whether a control's change
        /// REACHES the model, never where its displayed value came
        /// from.</para></summary>
        [Fact]
        public void CountBoxes_ReadTheSameFieldTheirEditsWrite()
        {
            string src = PadPage();
            string populate = Body(src, "private void SyncExtendedFields(PadViewModel vm)");

            foreach (var box in CountBoxes)
            {
                var m = Regex.Match(populate, Regex.Escape(box) + @"\.Text\s*=\s*([^;]+);");
                Assert.True(m.Success, $"{box} is never populated in SyncExtendedFields");
                string rhs = m.Groups[1].Value.Trim();
                Assert.True(rhs.StartsWith("vm.ExtendedConfig.", StringComparison.Ordinal),
                    $"{box} displays {rhs}, which is not the field its edits write. "
                    + "A control that writes one field and reads another lies about the state.");
            }
        }

        /// <summary>The write half of the same rule, and the pairing. The
        /// member each box is parsed into must be the member it is displayed
        /// from, so the round trip closes on one storage location.</summary>
        [Fact]
        public void CountBoxes_WriteAndReadNameTheSameMembers()
        {
            string src = PadPage();
            string populate = Body(src, "private void SyncExtendedFields(PadViewModel vm)");
            string apply = Body(src, "private void ApplyExtendedCustomValues()");

            foreach (var box in CountBoxes)
            {
                string read = Regex.Match(
                    populate, Regex.Escape(box) + @"\.Text\s*=\s*vm\.ExtendedConfig\.(\w+)")
                    .Groups[1].Value;
                Assert.False(string.IsNullOrEmpty(read), $"{box} has no read member");

                // The edit path parses the box, then assigns the parsed value
                // to a config member. Both halves must name `read`.
                string parsed = Regex.Match(
                    apply, @"int\.TryParse\(" + Regex.Escape(box) + @"\.Text,\s*out int (\w+)\)")
                    .Groups[1].Value;
                Assert.False(string.IsNullOrEmpty(parsed), $"{box} is never parsed in the edit path");
                Assert.Contains($"vm.ExtendedConfig.{read} = {parsed};", apply);
            }
        }

        // ── One derivation of "what are this profile's counts" ───────────

        /// <summary>The profile-to-layout rule lives in exactly one place.
        /// It had three copies: the authoritative seeder on PadViewModel, and
        /// two cruder ones in the view that guessed the stick and trigger
        /// split from AxisCount and knew nothing about the Nintendo
        /// lettered-count or Valve wire-table corrections. Two copies of a
        /// rule drift, and these two had already drifted.</summary>
        [Fact]
        public void TheProfileToLayoutDerivation_ExistsExactlyOnce()
        {
            string vm = Src("PadForge.App", "ViewModels", "PadViewModel.cs");
            Assert.Contains("internal void SyncExtendedConfigFromProfile()", vm);

            // The crude split lived on this expression. It must exist nowhere.
            foreach (var file in Directory.EnumerateFiles(
                         Path.Combine(RepoRoot(), "PadForge.App"), "*.cs",
                         SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;
                string text = File.ReadAllText(file);
                Assert.DoesNotContain("Math.Min(axes, 4) / 2", text);
            }
        }

        /// <summary>Reset to Defaults snaps the counts back through that one
        /// seeder. Deriving them locally put different numbers in the config
        /// than picking the same preset does, on exactly the profiles whose
        /// corrections matter most.</summary>
        [Fact]
        public void ResetToDefaults_SeedsThroughTheOneDerivation()
        {
            string reset = Body(PadPage(),
                "private void ExtendedResetDefaults_Click(object sender, RoutedEventArgs e)");
            Assert.Contains("vm.SyncExtendedConfigFromProfile();", reset);
            Assert.DoesNotContain("profile.AxisCount", reset);
            Assert.DoesNotContain("vm.ExtendedConfig.ButtonCount = profile.ButtonCount;", reset);
        }

        // ── The guard, and the notification that makes the read work ─────

        /// <summary>Its sibling ExtendedOverride_Changed has carried this
        /// guard all along. Without it, a programmatic populate landing
        /// mid-edit pushes the text it just wrote back into the model as if
        /// the user had typed it, and this handler reads all four boxes
        /// whenever any one of them loses focus.</summary>
        [Fact]
        public void TheEditHandler_CarriesTheGuardItsSiblingHas()
        {
            string src = PadPage();
            Assert.Contains("if (_syncingExtendedConfig) return;",
                Body(src, "private void ApplyExtendedCustomValues()"));
            Assert.Contains("if (_syncingExtendedConfig) return;",
                Body(src, "private void ExtendedOverride_Changed(object sender, RoutedEventArgs e)"));
        }

        /// <summary>Displaying the config means the card has to hear the config
        /// change. Picking a preset seeds all four counts, and without these
        /// the boxes kept showing the previous preset's numbers.</summary>
        [Fact]
        public void TheCard_HearsEveryCountChange()
        {
            string handler = Body(PadPage(),
                "private void OnExtendedConfigBarPropertyChanged(object sender, PropertyChangedEventArgs e)");
            foreach (var member in new[]
                     { "ThumbstickCount", "TriggerCount", "PovCount", "ButtonCount" })
                Assert.Contains($"ExtendedSlotConfig.{member})", handler);
        }

        /// <summary>The anchor for all of the above: the virtual controller is
        /// built from ExtendedConfig, unconditionally. That is why the card
        /// showing anything else was a lie rather than a preference.</summary>
        [Fact]
        public void TheControllerIsBuiltFromTheFieldTheCardNowShows()
        {
            string svc = Src("PadForge.App", "Services", "InputService.cs");
            foreach (var line in new[]
                     {
                         "Buttons = cfg.ButtonCount,",
                         "Povs = cfg.PovCount,",
                         "Sticks = cfg.ThumbstickCount,",
                         "Triggers = cfg.TriggerCount,",
                     })
                Assert.Contains(line, svc);
        }
    }
}
