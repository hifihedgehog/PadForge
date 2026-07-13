using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// Locates the committed Workshop VDF fixtures, which are copied next to the test
    /// assembly by the project's <c>CopyToOutputDirectory</c> item.
    /// </summary>
    internal static class TestFixtures
    {
        public static string Dir => Path.Combine(System.AppContext.BaseDirectory, "Fixtures");

        public static string Path_(long fileId) => Path.Combine(Dir, fileId + ".vdf");

        public static string Read(long fileId) => File.ReadAllText(Path_(fileId));

        public static IEnumerable<string> AllVdfPaths() =>
            Directory.EnumerateFiles(Dir, "*.vdf").OrderBy(p => p);

        // A few fixtures called out by name for targeted model assertions.
        public const long SkyrimDs4 = 793611331;      // "Dualshock 4 Skyrim", controller_ps4, splitconfig
        public const long SkyrimKbm = 789818086;      // "SkyrimSE Perfected: KB/M ..."
        public const long Homm3Deck = 2853328208;     // "HoMM3 HotA Deckified", controller_neptune
        public const long GabeGeneration = 2790927974; // has disabled_activators blocks
    }
}
