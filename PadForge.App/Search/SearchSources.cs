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
