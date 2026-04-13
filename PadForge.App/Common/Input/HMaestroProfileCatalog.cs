using System;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Read-only catalog of HIDMaestro profiles, partitioned by the v3
    /// category dropdown (Microsoft / Sony / Extended). Owns its own
    /// metadata-only HMContext: it calls LoadDefaultProfiles to enumerate
    /// the 225 embedded profile JSONs but never instantiates HMController
    /// or installs the driver. The engine's separate HMContext in
    /// InputManager.Step5 owns the live device lifecycle.
    ///
    /// Lazily initialized on first access. Safe to call from the UI thread
    /// (read-only after init).
    /// </summary>
    public static class HMaestroProfileCatalog
    {
        private static readonly object _initLock = new object();
        private static bool _initialized;
        private static List<HMProfile> _allProfiles = new();
        private static List<HMProfile> _microsoftProfiles = new();
        private static List<HMProfile> _sonyProfiles = new();
        private static List<HMProfile> _extendedProfiles = new();

        /// <summary>All loaded profiles, ordered by ID slug.</summary>
        public static IReadOnlyList<HMProfile> AllProfiles
        {
            get { EnsureInitialized(); return _allProfiles; }
        }

        /// <summary>Profiles where vendor == "Microsoft" (Xbox 360, Xbox One,
        /// Xbox Series, Elite, Adaptive, etc.).</summary>
        public static IReadOnlyList<HMProfile> MicrosoftProfiles
        {
            get { EnsureInitialized(); return _microsoftProfiles; }
        }

        /// <summary>Profiles where vendor == "Sony" (DualShock 3/4, DualSense,
        /// DualSense Edge, PS Move, PS3 Remote, PS Classic).</summary>
        public static IReadOnlyList<HMProfile> SonyProfiles
        {
            get { EnsureInitialized(); return _sonyProfiles; }
        }

        /// <summary>The full catalog — every profile HIDMaestro ships. Extended
        /// is the DirectInput/vJoy replacement category and exposes all 225
        /// profiles plus (future) the custom profile editor. Microsoft and
        /// Sony are convenience subsets of this same list.</summary>
        public static IReadOnlyList<HMProfile> ExtendedProfiles
        {
            get { EnsureInitialized(); return _extendedProfiles; }
        }

        /// <summary>Direct lookup by stable profile ID slug, or null if not loaded.</summary>
        public static HMProfile GetProfileById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureInitialized();
            return _allProfiles.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                try
                {
                    using var ctx = new HMContext();
                    ctx.LoadDefaultProfiles();

                    _allProfiles = ctx.AllProfiles
                        .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    _microsoftProfiles = _allProfiles
                        .Where(p => string.Equals(p.Vendor, "Microsoft", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    _sonyProfiles = _allProfiles
                        .Where(p => string.Equals(p.Vendor, "Sony", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // Extended lists the entire catalog — it's the
                    // DirectInput/vJoy replacement and the advanced slot where
                    // the user picks from every profile or (future) builds
                    // custom ones. Microsoft/Sony are curated subsets.
                    _extendedProfiles = _allProfiles;
                }
                catch
                {
                    // Catalog unavailable — leave the empty lists in place.
                    // The engine's own HMContext will surface the real error.
                }

                _initialized = true;
            }
        }
    }
}
