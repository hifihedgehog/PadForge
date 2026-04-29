using System;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Read-only catalog of HIDMaestro profiles, partitioned by the v3
    /// category dropdown (Xbox / PlayStation / Extended). Owns its own
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
        /// <summary>
        /// Reserved profile id for the synthetic "Custom" entry that PadForge
        /// injects at the top of the Extended dropdown. When a slot has this
        /// id selected, the Customize master toggle is forced on and the VC
        /// is built from a generic Xbox 360-like descriptor (2 sticks, 2
        /// triggers, 1 hat, 11 buttons) via HMProfileBuilder, with the user
        /// editing ProductString / VID / PID / stick-trigger-POV-button
        /// counts directly. Distinct from any real HIDMaestro profile id so
        /// it can't collide with a future catalog entry.
        /// </summary>
        public const string CustomProfileId = "padforge-custom";

        private static readonly object _initLock = new object();
        private static bool _initialized;
        private static List<HMProfile> _allProfiles = new();
        private static List<HMProfile> _xboxProfiles = new();
        private static List<HMProfile> _playStationProfiles = new();
        private static List<HMProfile> _extendedProfiles = new();

        /// <summary>
        /// Source of user-imported HIDMaestro profile JSONs to mix into the
        /// Extended category alongside the built-in catalog. Populated by
        /// the settings layer on startup (profiles live in PadForge.xml
        /// under &lt;UserProfiles&gt;) and re-populated after every live
        /// import. EnsureInitialized invokes the provider once per load; if
        /// it's null or returns null, only the built-in catalog + the
        /// synthetic Custom entry appear.
        /// </summary>
        public static System.Func<System.Collections.Generic.IReadOnlyList<string>> UserProfilesProvider { get; set; }

        /// <summary>Raised after the catalog is (re)built so UI bindings
        /// that depend on Extended/All profile lists can refresh.</summary>
        public static event System.EventHandler CatalogReloaded;

        /// <summary>All loaded profiles, ordered by ID slug.</summary>
        public static IReadOnlyList<HMProfile> AllProfiles
        {
            get { EnsureInitialized(); return _allProfiles; }
        }

        /// <summary>Profiles in the Xbox family (HIDMaestro's JSON tags these
        /// with vendor "Microsoft"; PadForge surfaces the category as "Xbox"
        /// in the UI). Covers Xbox 360, Xbox One, Xbox Series, Elite, Adaptive,
        /// etc.</summary>
        public static IReadOnlyList<HMProfile> XboxProfiles
        {
            get { EnsureInitialized(); return _xboxProfiles; }
        }

        /// <summary>Profiles where vendor == "Sony" (HIDMaestro's JSON still
        /// uses the Sony vendor string; PadForge surfaces the category as
        /// "PlayStation" in the UI). Covers DualShock 3/4, DualSense,
        /// DualSense Edge, PS Move, PS3 Remote, PS Classic.</summary>
        public static IReadOnlyList<HMProfile> PlayStationProfiles
        {
            get { EnsureInitialized(); return _playStationProfiles; }
        }

        /// <summary>Profiles that are NEITHER Xbox nor PlayStation family —
        /// third-party gamepads, flight sticks, wheels, HOTAS, etc. Mutually
        /// exclusive with <see cref="XboxProfiles"/> and
        /// <see cref="PlayStationProfiles"/> so each profile appears in exactly
        /// one category bucket.</summary>
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

        /// <summary>
        /// Force the catalog to re-initialize on the next access. Call
        /// after a user imports a new profile so the Extended dropdown
        /// picks it up.
        /// </summary>
        public static void Reload()
        {
            lock (_initLock)
            {
                _initialized = false;
                _allProfiles = new();
                _xboxProfiles = new();
                _playStationProfiles = new();
                _extendedProfiles = new();
            }
            EnsureInitialized();
            CatalogReloaded?.Invoke(null, System.EventArgs.Empty);
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                string userTempDir = null;
                try
                {
                    using var ctx = new HMContext();
                    ctx.LoadDefaultProfiles();

                    // Write any user-imported profile JSONs to a temp
                    // directory and load them through HMContext so they
                    // participate in the same parsing + validation path as
                    // the built-in catalog. HIDMaestro only exposes a
                    // directory-based loader, so we stage the JSONs to
                    // disk just long enough for LoadProfilesFromDirectory
                    // to consume them.
                    var userJsons = UserProfilesProvider?.Invoke();
                    if (userJsons != null && userJsons.Count > 0)
                    {
                        userTempDir = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(),
                            $"padforge-userprofiles-{System.Guid.NewGuid():N}");
                        try
                        {
                            System.IO.Directory.CreateDirectory(userTempDir);
                            for (int i = 0; i < userJsons.Count; i++)
                            {
                                var json = userJsons[i];
                                if (string.IsNullOrWhiteSpace(json)) continue;
                                System.IO.File.WriteAllText(
                                    System.IO.Path.Combine(userTempDir, $"user-{i:D4}.json"),
                                    json);
                            }
                            ctx.LoadProfilesFromDirectory(userTempDir);
                        }
                        catch
                        {
                            // User-profile staging/loading is best-effort —
                            // a single corrupt entry must not break the
                            // catalog. Built-in profiles are already loaded.
                        }
                    }

                    // Filter undeployable profiles at catalog load. HIDMaestro
                    // ships some profile JSONs that lack a HID descriptor —
                    // HMContext.CreateController throws ArgumentException
                    // "Profile 'X' has no HID descriptor and cannot be
                    // deployed." for those. Excluding them at the catalog
                    // level prevents the user from selecting a broken
                    // profile in any dropdown, so creation never attempts a
                    // controller it can't deploy. When HIDMaestro ships a
                    // fixed catalog, these profiles reappear automatically.
                    // Sort by display Name, not Id slug. The dropdown's
                    // DisplayMemberPath is "Name" so the user sees the
                    // product name; slug order produced a visually
                    // unsorted list (e.g. "Logitech F710" after
                    // "HORI Fighting Stick" but before "Thrustmaster T300"
                    // matches slug "logitech-f710" but reads wrong).
                    _allProfiles = ctx.AllProfiles
                        .Where(p => p.IsDeployable)
                        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Vendor match is prefix-based: HM upstream isn't strictly
                    // consistent on the vendor field — older Sony profiles use
                    // "Sony", the dualsense-bt-full profile added in HM 1.2.2
                    // uses "Sony Interactive Entertainment". Same shape for
                    // Microsoft if a future HM rev ships "Microsoft Corporation".
                    // StartsWith keeps both variants in the right family bucket
                    // without us needing to chase upstream string changes.
                    _xboxProfiles = _allProfiles
                        .Where(p => IsXboxVendor(p.Vendor))
                        .ToList();

                    _playStationProfiles = _allProfiles
                        .Where(p => IsSonyVendor(p.Vendor))
                        .ToList();

                    // Extended = everything that's not Microsoft or Sony,
                    // plus the synthetic "Custom" entry at the top so the
                    // user can define a fully custom VC without inheriting
                    // from any catalog profile. Custom sorts first to
                    // make it the discoverable default for new Extended
                    // slots. Also prepended to _allProfiles so that
                    // GetProfileById lookups resolve it for Step 5's
                    // CreateHMaestroController fallback path — HIDMaestro's
                    // own HMContext.GetProfile doesn't know about the
                    // synthetic.
                    var custom = BuildCustomProfile();
                    _allProfiles.Insert(0, custom);
                    var extended = new List<HMProfile> { custom };
                    extended.AddRange(_allProfiles
                        .Where(p =>
                            p.Id != CustomProfileId &&
                            !IsXboxVendor(p.Vendor) &&
                            !IsSonyVendor(p.Vendor)));
                    _extendedProfiles = extended;
                }
                catch
                {
                    // Catalog unavailable — leave the empty lists in place.
                    // The engine's own HMContext will surface the real error.
                }
                finally
                {
                    if (userTempDir != null)
                    {
                        try { System.IO.Directory.Delete(userTempDir, recursive: true); }
                        catch { }
                    }
                }

                _initialized = true;
            }
        }

        private static bool IsXboxVendor(string vendor) =>
            !string.IsNullOrEmpty(vendor) &&
            vendor.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

        private static bool IsSonyVendor(string vendor) =>
            !string.IsNullOrEmpty(vendor) &&
            vendor.StartsWith("Sony", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Build the synthetic "Custom" profile that anchors the Extended
        /// dropdown. Standard Xbox 360-like layout: 2 16-bit sticks, 2
        /// 8-bit triggers, 1 hat switch, 11 buttons. Matches the default
        /// ExtendedConfig values so the dropdown and the override fields
        /// agree on initial selection. Users edit any of these via the
        /// Customize panel.
        ///
        /// VID:PID 0xBEEF:0xF000, PadForge faux-VID convention. 0xBEEF is
        /// our implicit in-program VID (already used by WebControllerDevice
        /// and TouchpadOverlayDevice); the PID namespace under it is
        /// partitioned so the class of device is legible in hex dumps and
        /// joy.cpl:
        ///   0xCA7x: input sources (web, overlay touchpad)
        ///   0xF0xx: Forge synthetic output devices (this profile + any
        ///           future custom-shaped VC variants)
        /// This is squatting, not a registered allocation. No real USB-IF
        /// VID is held by 0xBEEF so collision risk with real hardware is
        /// negligible.
        ///
        /// AddPidFfbBlock auto-injects the Report ID 0x01 prefix and emits
        /// the canonical minimum-viable PID FFB descriptor. FromDescriptorBuilder
        /// derives InputReportSize from the builder's bit count plus the
        /// Report ID byte. Both APIs landed in HM v1.1.41 (issue #16).
        /// </summary>
        private static HMProfile BuildCustomProfile()
        {
            var b = new HidDescriptorBuilder()
                .Joystick()
                .AddStick("Left", 16)
                .AddStick("Right", 16)
                .AddTrigger("Left", 16)
                .AddTrigger("Right", 16)
                .AddHat()
                .AddButtons(11)
                .AddPidFfbBlock();

            return new HMProfileBuilder()
                .Id(CustomProfileId)
                .Name("Custom")
                .Vendor("Custom")
                .Vid(0xBEEF)
                .Pid(0xF000)
                .ProductString("PadForge Game Controller")
                .ManufacturerString("PadForge")
                .Type("gamepad")
                .Connection("usb")
                .FromDescriptorBuilder(b)
                .Build();
        }
    }
}
