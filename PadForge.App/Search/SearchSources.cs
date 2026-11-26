using System.Collections.Generic;
using System.IO;

namespace PadForge.Search
{
    /// <summary>The five pages' markup, embedded in the app as
    /// PadForge.SearchSources.&lt;Page&gt;.xaml (PadForge.App.csproj), which
    /// Search builds its index from (#574).</summary>
    internal static class SearchSources
    {
        internal static readonly IReadOnlyDictionary<SearchPage, string> FileNames = new Dictionary<SearchPage, string>
        {
            [SearchPage.Pad] = "PadPage.xaml",
            [SearchPage.Settings] = "SettingsPage.xaml",
            [SearchPage.Extras] = "ExtrasPage.xaml",
            [SearchPage.Devices] = "DevicesPage.xaml",
            [SearchPage.Profiles] = "ProfilesPage.xaml",
        };

        internal static string ResourceName(SearchPage page) => "PadForge.SearchSources." + FileNames[page];

        /// <summary>The prefix of the catalog's own markup in the app's
        /// resources (#600): every DialogWindow, the request card, the three
        /// dialogs the window shows inside itself (Add Controller, Search and
        /// Read Your Recording), Home, About and the window itself.</summary>
        internal const string DialogPrefix = "PadForge.CatalogSources.";

        /// <summary>Each source's markup by its name, read from the app's
        /// resources.</summary>
        internal static IReadOnlyDictionary<string, string> LoadCatalog()
        {
            var assembly = typeof(SearchSources).Assembly;
            var texts = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(DialogPrefix, System.StringComparison.Ordinal) || !name.EndsWith(".xaml", System.StringComparison.Ordinal)) continue;
                using var stream = assembly.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream);
                texts[name.Substring(DialogPrefix.Length, name.Length - DialogPrefix.Length - ".xaml".Length)] = reader.ReadToEnd();
            }
            return texts;
        }

        /// <summary>Each page's markup, read from the app's resources.</summary>
        internal static IReadOnlyDictionary<SearchPage, string> Load()
        {
            var assembly = typeof(SearchSources).Assembly;
            var texts = new Dictionary<SearchPage, string>();
            foreach (var page in FileNames.Keys)
            {
                using var stream = assembly.GetManifestResourceStream(ResourceName(page));
                if (stream == null) continue;
                using var reader = new StreamReader(stream);
                texts[page] = reader.ReadToEnd();
            }
            return texts;
        }
    }
}
