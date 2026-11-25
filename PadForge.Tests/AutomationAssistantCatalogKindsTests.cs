using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PadForge.Search;
using PadForge.Services.Automation;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The settings catalog's round trip of each kind on each page (#597).
    /// The Settings page's one choice is the interface language, so its
    /// round trip changes the language for a moment: the class runs alone,
    /// in the collection the tests that switch the language share.
    /// </summary>
    [Collection("CultureSwitching")]
    public class AutomationAssistantCatalogKindsTests
    {
        private readonly ITestOutputHelper _output;

        public AutomationAssistantCatalogKindsTests(ITestOutputHelper output) => _output = output;

        /// <summary>The kinds each page offers, read from the index: the
        /// value kind of each setting entry of the page. Automation's and
        /// Plugins' are the user's alone and are left out.</summary>
        private static Dictionary<SearchPage, HashSet<SearchValueKind>> KindsOffered(SearchService service)
        {
            var offered = Enum.GetValues(typeof(SearchPage)).Cast<SearchPage>().ToDictionary(p => p, _ => new HashSet<SearchValueKind>());
            foreach (var entry in service.Index.Where(e => e.Kind == SearchKind.Setting && e.Value != null))
            {
                string id = service.IdOf(entry);
                if (id.Contains("/Automation/") || id.Contains("/Plugins/")) continue;
                offered[entry.Page].Add(entry.Value.Kind);
            }
            return offered;
        }

        /// <summary>Every kind each page offers, by the index, reads and
        /// writes through its control and back on that page. The kinds a
        /// page does not offer are pinned as the index shows them: the
        /// Devices and Profiles pages each hold one setting, a switch. A
        /// setting the index reads no value of (a row of buttons, a
        /// gallery, a folder's path) has no kind.</summary>
        [Fact]
        public void EveryKindEachPageOffers_ReadsAndWritesThroughItsControl_AndBack()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var offered = KindsOffered(p.Service);
                var expected = new Dictionary<SearchPage, SearchValueKind[]>
                {
                    [SearchPage.Pad] = new[] { SearchValueKind.Number, SearchValueKind.Switch, SearchValueKind.Choice },
                    [SearchPage.Settings] = new[] { SearchValueKind.Number, SearchValueKind.Switch, SearchValueKind.Choice },
                    [SearchPage.Extras] = new[] { SearchValueKind.Number, SearchValueKind.Switch, SearchValueKind.Choice },
                    [SearchPage.Devices] = new[] { SearchValueKind.Switch },
                    [SearchPage.Profiles] = new[] { SearchValueKind.Switch },
                };
                foreach (var (page, kinds) in expected)
                    Assert.True(offered[page].SetEquals(kinds), page + " offers " + string.Join(", ", offered[page].OrderBy(k => k)));

                var all = p.Catalog.All();
                var done = new List<string>();
                foreach (var (page, kinds) in offered)
                {
                    foreach (var kind in kinds.OrderBy(k => k))
                    {
                        string want = kind.ToString().ToLowerInvariant();
                        var ofKind = all.Where(s => s.Id.StartsWith(SearchIds.PageWord(page) + "/", StringComparison.Ordinal)
                                                    && s.Takes == want
                                                    && !s.Id.Contains("/Automation/") && !s.Id.Contains("/Plugins/")).ToList();
                        var candidates = ofKind.Where(s => s.Value != null).ToList();
                        Assert.True(candidates.Count > 0, "the " + page + " page offers " + ofKind.Count + " " + kind + " settings and the fixture gives none a value");
                        var setting = candidates.FirstOrDefault(s => RoundTrips(p.Catalog, s));
                        Assert.True(setting != null, "no " + kind + " on the " + page + " page round-tripped of " + candidates.Count);
                        done.Add(page + " " + kind + ": " + setting.Id);
                    }
                }
                foreach (string line in done) _output.WriteLine(line);
                Assert.Equal(expected.Values.Sum(k => k.Length), done.Count);

                // A setting whose row takes nothing a program can write (a
                // row of buttons, a gallery, a folder's path) takes nothing,
                // and it is refused for writing.
                var none = all.Where(s => s.Takes == null).ToList();
                Assert.NotEmpty(none);
                foreach (var s in none) _output.WriteLine("no kind: " + s.Id);
                Assert.Contains(none, s => s.Id.EndsWith("/Settings_SteamVRInstallDir", StringComparison.Ordinal));
                Assert.All(none, s => Assert.Null(s.Value));
                var refusal = Assert.Throws<AssistantRefusal>(() => p.Catalog.Set(none[0].Id, JsonSerializer.SerializeToElement(1)));
                Assert.Equal("not-available", refusal.Code);
            });
        }

        private bool RoundTrips(AssistantCatalog catalog, AssistantPlace setting)
        {
            JsonElement before = setting.Value.Value;
            JsonElement next;
            switch (setting.Takes)
            {
                case "number":
                {
                    double v = before.GetDouble();
                    double min = setting.Minimum ?? v - 1, max = setting.Maximum ?? v + 1;
                    double to = v + 1 <= max ? v + 1 : v - 1;
                    if (to < min) return false;
                    next = JsonSerializer.SerializeToElement(to);
                    break;
                }
                case "switch":
                    next = JsonSerializer.SerializeToElement(!before.GetBoolean());
                    break;
                default:
                    var other = setting.Choices?.FirstOrDefault(c => c != before.GetString());
                    if (other == null) return false;
                    next = JsonSerializer.SerializeToElement(other);
                    break;
            }
            try
            {
                var after = catalog.Set(setting.Id, next);
                bool changed = setting.Takes switch
                {
                    "number" => after.Value != null && Math.Abs(after.Value.Value.GetDouble() - next.GetDouble()) < 0.0001,
                    "switch" => after.Value != null && after.Value.Value.GetBoolean() == next.GetBoolean(),
                    _ => after.Value != null && after.Value.Value.GetString() == next.GetString(),
                };
                catalog.Set(setting.Id, before);
                var back = catalog.Read(setting.Id).Place;
                return changed && back.Value != null && back.Value.Value.GetRawText() == before.GetRawText();
            }
            catch (AssistantRefusal refusal)
            {
                _output.WriteLine(setting.Id + " refused: " + refusal.Code + " " + refusal.Words);
                return false;
            }
        }
    }
}
