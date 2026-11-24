using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Controls;
using System.Xml.Linq;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every action, dialog button, settable and data-bound element of the
    /// app's markup with the class and reason it ends up with (#600), pinned
    /// in ReachSnapshot.json beside this file, as ResetControlBindings.json
    /// pins the reset bindings. A control added anywhere, inside a Free page
    /// included, is a row the file does not hold, so its class is chosen on
    /// purpose: the lane that adds it adds its row.
    ///
    /// <para>To write the file again after a deliberate change, run this
    /// class with the variable PADFORGE_WRITE_REACH_SNAPSHOT=1 set, read the
    /// file's diff, and commit it with the change:
    /// <c>$env:PADFORGE_WRITE_REACH_SNAPSHOT=1; dotnet test PadForge.Tests -c Release --filter FullyQualifiedName~ReachSnapshotTests</c>.</para>
    /// </summary>
    public class ReachSnapshotTests
    {
        internal const string WriteVariable = "PADFORGE_WRITE_REACH_SNAPSHOT";

        private static string SnapshotPath => Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.Tests", "ReachSnapshot.json");

        private static readonly Regex StringKey = new(@"Binding\s+(?:Path=)?(\w+),\s*Source=\{x:Static\s+\w+:Strings\.Instance\}");
        private static readonly Regex PlainPath = new(@"^\{Binding\s+(?:Path=)?([\w.\[\]]+)");

        /// <summary>A name for an element that stays while lines move: its
        /// AutomationId, its x:Name, the string its words bind, or its first
        /// data binding's path.</summary>
        private static string Identity(XElement e)
        {
            string id = ReachMarkup.Attr(e, "AutomationProperties.AutomationId");
            if (!string.IsNullOrEmpty(id)) return "id:" + id;
            string name = (string)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"));
            if (!string.IsNullOrEmpty(name)) return "name:" + name;
            foreach (string attribute in new[] { "Content", "Header", "Text", "SettingLabel", "ToolTip", "AutomationProperties.Name" })
                if (ReachMarkup.Attr(e, attribute) is { } v && StringKey.Match(v) is { Success: true } m) return attribute.ToLowerInvariant() + ":" + m.Groups[1].Value;
            foreach (var a in e.Attributes())
                if (PlainPath.Match(a.Value) is { Success: true } p && !a.Value.Contains("Strings.Instance", StringComparison.Ordinal))
                    return a.Name.LocalName + ":" + p.Groups[1].Value;
            return "-";
        }

        /// <summary>The rows of one file: file, element, identity (with its
        /// count when it repeats), class and reason.</summary>
        internal static List<string> Rows(string relative, string text, Func<string, bool> hosted)
        {
            var rows = new List<string>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            bool isHosted = hosted(text);
            foreach (var (element, cls, _, _) in ReachMarkup.Read(text))
            {
                string key = relative + " <" + element.Name.LocalName + "> " + Identity(element);
                seen.TryGetValue(key, out int n);
                seen[key] = n + 1;
                string why = cls == ReachClass.Asks ? ReachWhy(element, text) : "-";
                string shown = cls == ReachClass.Unset && isHosted ? "host" : cls.ToString();
                rows.Add(key + (n > 0 ? " #" + (n + 1) : string.Empty) + " = " + shown + (why != "-" ? "/" + why : string.Empty));
            }
            return rows;
        }

        /// <summary>The reason of the strictest Asks on the way up.</summary>
        private static string ReachWhy(XElement e, string text)
        {
            for (var at = e; at != null; at = at.Parent)
                if (ReachMarkup.Attr(at, "Reach.Class") == "Asks") return ReachMarkup.Attr(at, "Reach.Why") ?? "None";
            return "None";
        }

        internal static List<string> AllRows()
        {
            var files = ReachMarkup.Files().ToList();
            var texts = files.ToDictionary(f => f, File.ReadAllText);
            var rows = new List<string>();
            foreach (string file in files)
            {
                var root = XDocument.Parse(texts[file]).Root;
                if (root.Name.LocalName is "ResourceDictionary" or "Application") continue;
                string name = Path.GetFileNameWithoutExtension(file);
                string relative = Path.GetRelativePath(ReachMarkup.AppRoot, file).Replace('\\', '/');
                rows.AddRange(Rows(relative, texts[file], t => ReachMarkup.Attr(XDocument.Parse(t).Root, "Reach.Class") == null
                    && texts.Any(o => o.Key != file && o.Value.Contains(":" + name, StringComparison.Ordinal))));
            }
            return rows;
        }

        [Fact]
        public void EveryElementsClass_IsWhatTheSnapshotHolds()
        {
            var rows = AllRows();
            if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
            {
                File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                return;
            }
            Assert.True(File.Exists(SnapshotPath), "ReachSnapshot.json is missing: write it with " + WriteVariable + "=1.");
            var pinned = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(SnapshotPath));
            var added = rows.Except(pinned).ToList();
            var gone = pinned.Except(rows).ToList();
            Assert.True(added.Count == 0 && gone.Count == 0,
                "The markup's reach classes differ from ReachSnapshot.json. New or changed:\n" + string.Join("\n", added.Take(40))
                + "\nGone or changed:\n" + string.Join("\n", gone.Take(40))
                + "\nGive a new control its class on purpose, then write the file again with " + WriteVariable + "=1.");
            Assert.DoesNotContain(rows, r => r.EndsWith(" = Unset", StringComparison.Ordinal));
        }

        /// <summary>A button added inside a Free page with no class of its
        /// own is a row the snapshot does not hold.</summary>
        [Fact]
        public void AButtonAddedToAFreePage_IsCaught()
        {
            string relative = "Views/ProfilesPage.xaml";
            string text = File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "Views", "ProfilesPage.xaml"));
            var pinned = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(SnapshotPath));
            int at = text.IndexOf("<StackPanel x:Name=\"HeadButtons\"", StringComparison.Ordinal);
            int end = text.IndexOf('>', at) + 1;
            string planted = text.Substring(0, end) + "<Button Content=\"{Binding Planted_Button, Source={x:Static strings:Strings.Instance}}\"/>" + text.Substring(end);
            var added = Rows(relative, planted, _ => false).Except(pinned).ToList();
            Assert.Equal(new[] { "Views/ProfilesPage.xaml <Button> content:Planted_Button = Free" }, added);
        }

        private static void Sta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(20000), "the run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A template that sets Free on its root, drawn inside a
        /// place that is the user's own, stays the user's own.</summary>
        [Fact]
        public void AFreeTemplateRoot_UnderAStricterPlace_TakesThePlacesClass()
            => Sta(() =>
            {
                var place = new StackPanel();
                Reach.SetClass(place, ReachClass.UsersOwn);
                var templateRoot = new Border();
                Reach.SetClass(templateRoot, ReachClass.Free);
                var button = new Button();
                templateRoot.Child = button;
                place.Children.Add(templateRoot);
                Assert.Equal(ReachClass.UsersOwn, Reach.Of(button).Class);
                var asks = new StackPanel();
                Reach.SetClass(asks, ReachClass.Asks);
                Reach.SetWhy(asks, ReachReason.SendsInput);
                var inner = new Border();
                Reach.SetClass(inner, ReachClass.Free);
                var other = new Button();
                inner.Child = other;
                asks.Children.Add(inner);
                Assert.Equal((ReachClass.Asks, ReachReason.SendsInput), Reach.Of(other));
            });
    }
}
