using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every reset icon says "Reset &lt;the setting it resets&gt;".
    ///
    /// <para>A reset icon is a bare glyph. The tooltip is the only thing that
    /// says what it resets, and a row can carry half a dozen of them. "Reset"
    /// on its own, or "Reset to Off", leaves the user counting icons to work
    /// out which is which.</para>
    ///
    /// <para>The convention is set by the hundreds of correct ones on the Pad
    /// page. It broke in two ways. Some buttons were rolled by hand out of a
    /// plain Button with a generic tooltip, instead of using
    /// <c>SettingResetButton</c>, which composes the text from its
    /// SettingLabel and cannot get it wrong. Others used the control but
    /// handed it a DESCRIPTION rather than a name, which produced tooltips
    /// like "Reset How this PC's pairing identity is stored. Choose a
    /// portable mode to carry your pairings on a thumb drive between
    /// machines."</para>
    ///
    /// <para>Text buttons are a different thing and are not covered here. A
    /// button reading "Reset Calibration" already says what it does on its
    /// face, so its tooltip is free to explain consequences.</para>
    /// </summary>
    public class ResetTooltipConventionTests
    {
        /// <summary>Tooltips that name no setting.</summary>
        private static readonly string[] Generic =
            { "Reset", "Reset to Off", "Reset to Default", "Reset to Defaults" };

        private static string ViewsDir()
        {
            var d = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (d != null && !File.Exists(Path.Combine(d.FullName, "SharedVersion.cs")))
                d = d.Parent;
            Assert.True(d != null, "repository root not found");
            return Path.Combine(d.FullName, "PadForge.App", "Views");
        }

        private static Dictionary<string, string> Strings()
        {
            var d = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (d != null && !File.Exists(Path.Combine(d.FullName, "SharedVersion.cs")))
                d = d.Parent;
            string path = Path.Combine(d.FullName, "PadForge.App", "Resources", "Strings",
                                       "Strings.resx");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(File.ReadAllText(path),
                     "<data\\s+name=\"([^\"]+)\"[^>]*>\\s*<value>(.*?)</value>", RegexOptions.Singleline))
                map[m.Groups[1].Value] = Regex.Replace(m.Groups[2].Value, @"\s+", " ").Trim();
            return map;
        }

        /// <summary>An icon-only reset must not carry a tooltip that names no
        /// setting. Icon-only means the button has no Content of its own.</summary>
        [Fact]
        public void NoResetIconCarriesAGenericTooltip()
        {
            var strings = Strings();
            var offenders = new List<string>();

            foreach (string file in Directory.GetFiles(ViewsDir(), "*.xaml"))
            {
                string text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, @"<(?!reset:)[A-Za-z:]*Button\b.*?>",
                                                  RegexOptions.Singleline))
                {
                    string tag = m.Value;
                    if (!Regex.IsMatch(tag, @"Command=""\{Binding [A-Za-z0-9_]*Reset")) continue;
                    // A button with its own visible text says what it does.
                    if (tag.Contains("Content=")) continue;

                    var tip = Regex.Match(tag, @"ToolTip=""\{Binding ([A-Za-z0-9_]+)");
                    if (!tip.Success) continue;          // bound to a view model, checked below
                    if (!strings.TryGetValue(tip.Groups[1].Value, out string value)) continue;

                    if (Generic.Contains(value, StringComparer.OrdinalIgnoreCase)
                        || !value.StartsWith("Reset", StringComparison.OrdinalIgnoreCase))
                    {
                        int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                        offenders.Add($"{Path.GetFileName(file)}:{line} {tip.Groups[1].Value} = \"{value}\"");
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "reset icons whose tooltip names no setting:\n  " + string.Join("\n  ", offenders));
        }


        /// <summary>
        /// Every value row on the Extras page carries its own reset. The
        /// rows were the Dashboard's until its services got a page each
        /// (#574), and the Dashboard holds no number box since.
        ///
        /// <para>The other tests here check what a reset SAYS. This one
        /// checks that the reset EXISTS, which is the half that shipped
        /// broken: the six per-axis head-tracking ranges landed as bare
        /// number boxes next to rows that all had one. The paradigm gives
        /// every tunable a one-click way back to its default, and the owner
        /// has had to say so more than once.</para>
        ///
        /// <para>A row is measured up to the next number box, so a reset
        /// belonging to a later row cannot cover for a missing one.</para>
        /// </summary>
        [Fact]
        public void EveryNumberBoxOnTheExtrasPageHasItsOwnReset()
        {
            Assert.DoesNotContain("<ui:NumberBox", File.ReadAllText(Path.Combine(ViewsDir(), "DashboardPage.xaml")));
            string file = Path.Combine(ViewsDir(), "ExtrasPage.xaml");
            string text = File.ReadAllText(file);
            var starts = Regex.Matches(text, @"<ui:NumberBox\b").Select(m => m.Index).ToList();
            Assert.True(starts.Count > 10, $"expected the Extras page's number boxes, found {starts.Count}");

            var offenders = new List<string>();
            for (int i = 0; i < starts.Count; i++)
            {
                int next = i + 1 < starts.Count ? starts[i + 1] : text.Length;
                int end = Math.Min(next, starts[i] + 1500);
                string row = text[starts[i]..end];
                if (row.Contains("reset:SettingResetButton", StringComparison.Ordinal)) continue;

                var bound = Regex.Match(row, @"Value=""\{Binding ([A-Za-z0-9_]+)");
                int line = text.Take(starts[i]).Count(c => c == '\n') + 1;
                offenders.Add($"ExtrasPage.xaml:{line} {(bound.Success ? bound.Groups[1].Value : "?")}");
            }

            Assert.True(offenders.Count == 0,
                "value rows with no reset button of their own:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>The six per-axis head-tracking ranges each reset
        /// themselves, not the family and not each other.</summary>
        [Fact]
        public void EachPerAxisRangeResetsOnlyItsOwnAxis()
        {
            string xaml = File.ReadAllText(Path.Combine(ViewsDir(), "ExtrasPage.xaml"));
            foreach (string axis in new[] { "Yaw", "Pitch", "Roll", "X", "Y", "Z" })
            {
                Assert.Contains($"ResetHeadTrackingRange{axis}Command", xaml);
                // The label feeds the "Reset <name>" tooltip, so a bare glyph
                // in a stack of six still says which one it is.
                Assert.Contains($"Dashboard_HeadTrackingAxis{axis}, Source=", xaml);
            }
        }


        /// <summary>
        /// The per-axis ranges line up with the two ranges above them.
        ///
        /// <para>They first shipped in a grid of their own, so their label
        /// column was sized by their own longest label and every box and
        /// reset started at a different x than the rows above. One grid
        /// fixed that. On the Head Tracking page (#574) the two ranges show
        /// first and the per-axis ranges sit under More, so they are two
        /// grids again, and what lines them up is one shared size group on
        /// both label columns, inside one scope on the card. Either grid
        /// losing the group, or the card losing the scope, puts the boxes
        /// back at two different x.</para>
        /// </summary>
        [Fact]
        public void ThePerAxisRangesShareTheLabelColumnOfTheRangesAboveThem()
        {
            string xaml = File.ReadAllText(Path.Combine(ViewsDir(), "ExtrasPage.xaml"));
            int page = xaml.IndexOf("x:Name=\"HeadTrackingPage\"", StringComparison.Ordinal);
            int end = xaml.IndexOf("x:Name=\"GameTelemetryPage\"", StringComparison.Ordinal);
            Assert.True(page > 0 && end > page, "the Head Tracking page was not found");
            string card = xaml[page..end];

            int scope = card.IndexOf("<StackPanel Grid.IsSharedSizeScope=\"True\">", StringComparison.Ordinal);
            int first = card.IndexOf("HeadTrackingRotationRange, Mode=TwoWay", StringComparison.Ordinal);
            int more = card.IndexOf("<controls:MoreRow x:Name=\"HeadTrackingMore\"", StringComparison.Ordinal);
            int last = card.IndexOf("HeadTrackingRangeZ, Mode=TwoWay", StringComparison.Ordinal);
            Assert.True(scope > 0 && first > scope && more > first && last > more,
                "the two ranges show first and the per-axis ranges sit under More, all inside the card's size scope");

            // Each range row's grid opens with the shared label column.
            const string Shared = "<ColumnDefinition Width=\"Auto\" SharedSizeGroup=\"HeadTrackingLabel\"/>";
            foreach (int row in new[] { first, last })
            {
                int grid = card.LastIndexOf("<Grid Margin=", row, StringComparison.Ordinal);
                int columns = card.IndexOf("<ColumnDefinition ", grid, StringComparison.Ordinal);
                Assert.True(grid > scope && columns > grid && columns < row, "a range row is not in a grid of its own columns");
                Assert.True(string.CompareOrdinal(card, columns, Shared, 0, Shared.Length) == 0,
                    "a range grid's label column is not in the shared size group, so its boxes cannot line up with the other grid's");
            }

            // The six per-axis rows and the port are still one grid.
            int port = card.IndexOf("{Binding HeadTrackingUdpPort,", StringComparison.Ordinal);
            Assert.True(port > more && port < last, "the UDP port row is not under More");
            Assert.False(card[port..last].Contains("</Grid>", StringComparison.Ordinal),
                "the per-axis ranges are in a different grid from the port above them");
        }

        /// <summary>The same holds on the Game Telemetry page: the game
        /// picker shows first, the ports and Full Speed sit under More, and
        /// one shared size group lines their boxes up.</summary>
        [Fact]
        public void TheGameTelemetryRowsShareOneLabelColumn()
        {
            string xaml = File.ReadAllText(Path.Combine(ViewsDir(), "ExtrasPage.xaml"));
            int page = xaml.IndexOf("x:Name=\"GameTelemetryPage\"", StringComparison.Ordinal);
            int end = xaml.IndexOf("x:Name=\"TwitchChatPage\"", StringComparison.Ordinal);
            Assert.True(page > 0 && end > page, "the Game Telemetry page was not found");
            string card = xaml[page..end];

            int scope = card.IndexOf("<StackPanel Grid.IsSharedSizeScope=\"True\">", StringComparison.Ordinal);
            int game = card.IndexOf("x:Name=\"GameTelemetryGameCombo\"", StringComparison.Ordinal);
            int more = card.IndexOf("<controls:MoreRow x:Name=\"GameTelemetryMore\"", StringComparison.Ordinal);
            int speed = card.IndexOf("{Binding GameTelemetryFullSpeed,", StringComparison.Ordinal);
            Assert.True(scope > 0 && game > scope && more > game && speed > more,
                "the game picker shows first and Full Speed sits under More, all inside the card's size scope");
            const string Shared = "<ColumnDefinition Width=\"Auto\" SharedSizeGroup=\"GameTelemetryLabel\"/>";
            Assert.Equal(2, card.Split(new[] { Shared }, StringSplitOptions.None).Length - 1);
            // Both grids still follow the switch.
            Assert.Equal(2, card.Split(new[] { "<Grid Margin=\"0,0,0,8\" IsEnabled=\"{Binding EnableGameTelemetry}\">" }, StringSplitOptions.None).Length - 1);
        }

        /// <summary>A SettingResetButton's label is the setting's NAME. Feed
        /// it a description and the tooltip becomes a paragraph.</summary>
        [Fact]
        public void NoSettingResetButtonIsLabelledWithADescription()
        {
            var strings = Strings();
            var offenders = new List<string>();

            foreach (string file in Directory.GetFiles(ViewsDir(), "*.xaml"))
            {
                string text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, @"<reset:SettingResetButton\b[^>]*?>",
                                                  RegexOptions.Singleline))
                {
                    var lab = Regex.Match(m.Value, @"SettingLabel=""\{Binding ([A-Za-z0-9_]+)");
                    int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    if (!lab.Success)
                    {
                        if (!m.Value.Contains("SettingLabel="))
                            offenders.Add($"{Path.GetFileName(file)}:{line} has no SettingLabel");
                        continue;
                    }
                    string key = lab.Groups[1].Value;
                    if (key.EndsWith("Desc", StringComparison.Ordinal)
                        || key.EndsWith("Description", StringComparison.Ordinal)
                        || key.EndsWith("_Hint", StringComparison.Ordinal)
                        || key.EndsWith("Tooltip", StringComparison.Ordinal)
                        || key.EndsWith("_Tip", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{line} {key} is a description, not a name");
                        continue;
                    }
                    // A sentence is a description whatever its key is called.
                    if (strings.TryGetValue(key, out string value) && value.EndsWith("."))
                        offenders.Add($"{Path.GetFileName(file)}:{line} {key} = \"{value}\"");
                }
            }

            Assert.True(offenders.Count == 0,
                "reset labels that are descriptions rather than setting names:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>The format string the control composes with stays a
        /// "Reset {0}". Changing it silently rewrites every reset tooltip in
        /// the app.</summary>
        [Fact]
        public void TheResetFormatStillNamesTheSetting()
        {
            var strings = Strings();
            Assert.True(strings.TryGetValue("Pad_ResetSection_Format", out string format));
            Assert.Contains("{0}", format);
            Assert.StartsWith("Reset", format, StringComparison.Ordinal);
        }
    }
}
