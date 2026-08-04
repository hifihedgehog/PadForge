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
        private static List<HMProfile> _nintendoProfiles = new();
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

        /// <summary>Xbox-family controller profiles. Filter is the
        /// intersection of "vendor is Microsoft" AND "name or id contains
        /// Xbox" — keeps the bucket honest to its category label and
        /// drops any Microsoft-vendor profiles that aren't Xbox controllers
        /// (Surface peripherals, generic HID devices, etc.) into Extended.</summary>
        public static IReadOnlyList<HMProfile> XboxProfiles
        {
            get { EnsureInitialized(); return _xboxProfiles; }
        }

        /// <summary>PlayStation-family controller profiles. Filter is the
        /// intersection of "vendor is Sony" AND "name or id contains
        /// DualShock or DualSense" — covers DualShock 3/4 + DualSense /
        /// DualSense Edge only. Non-controller Sony profiles (PS Move,
        /// PS3 Remote, PS Classic, etc.) drop to Extended.</summary>
        public static IReadOnlyList<HMProfile> PlayStationProfiles
        {
            get { EnsureInitialized(); return _playStationProfiles; }
        }

        /// <summary>Nintendo-family controller profiles. The single
        /// switch-pro profile for now; see IsNintendoProfile for the
        /// deliberate scope. Mutually exclusive with the other buckets.</summary>
        public static IReadOnlyList<HMProfile> NintendoProfiles
        {
            get { EnsureInitialized(); return _nintendoProfiles; }
        }

        /// <summary>Profiles that don't match the strict Xbox or PlayStation
        /// filters above — third-party gamepads, flight sticks, wheels,
        /// HOTAS, plus any vendor-Microsoft / vendor-Sony profiles whose
        /// name doesn't carry the canonical product family (Surface, PS
        /// Move, PS Classic, etc.). Mutually exclusive with the other two
        /// buckets so each profile appears in exactly one category.</summary>
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
                _nintendoProfiles = new();
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
                    //
                    // Xbox / PlayStation buckets additionally require the
                    // profile name (or id slug) to carry the canonical
                    // product family — "Xbox" for Microsoft, "DualShock" or
                    // "DualSense" for Sony. Vendor-only matching pulled in
                    // peripherals that share the brand but aren't gamepads
                    // (Surface, PS Move, PS3 Remote, PS Classic), which
                    // confused the user-facing pickers; those drop to
                    // Extended now.
                    _xboxProfiles = _allProfiles
                        .Where(IsXboxProfile)
                        .ToList();

                    _playStationProfiles = _allProfiles
                        .Where(IsPlayStationProfile)
                        .ToList();

                    _nintendoProfiles = _allProfiles
                        .Where(IsNintendoProfile)
                        .ToList();

                    // Extended = everything that's not Xbox or PlayStation,
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
                            !IsXboxProfile(p) &&
                            !IsPlayStationProfile(p) &&
                            !IsNintendoProfile(p)));
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

        private static bool IsPlayStationVendor(string vendor) =>
            !string.IsNullOrEmpty(vendor) &&
            vendor.StartsWith("Sony", StringComparison.OrdinalIgnoreCase);

        private static bool ContainsToken(string s, string token) =>
            !string.IsNullOrEmpty(s) &&
            s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// True when the profile is a Microsoft-vendor Xbox controller —
        /// the canonical "Xbox 360 / One / Series" rumble shape applies.
        /// Used both by the Extended dropdown bucket filter (where this
        /// gate keeps non-Xbox Microsoft peripherals like the SideWinder
        /// out of the Xbox profile list) AND by HMaestroVirtualController's
        /// HID-output rumble dispatch (where the same gate keeps SideWinder
        /// PID FFB output reports from being misread as Xbox Series BT
        /// rumble or Xbox HID legacy rumble).
        ///
        /// Single-source-of-truth definition so the two consumers stay in
        /// sync — adding a new non-Xbox Microsoft profile (a future SideWinder
        /// variant, a Surface peripheral, etc.) only needs the JSON's name
        /// to omit "Xbox" for both consumers to ignore it correctly.
        /// </summary>
        internal static bool IsXboxProfile(HMProfile p) =>
            IsXboxVendor(p.Vendor) &&
            (ContainsToken(p.Name, "Xbox") || ContainsToken(p.Id, "xbox"));

        private static bool IsPlayStationProfile(HMProfile p) =>
            IsPlayStationVendor(p.Vendor) &&
            (ContainsToken(p.Name, "DualShock") || ContainsToken(p.Name, "DualSense")
             || ContainsToken(p.Id, "dualshock") || ContainsToken(p.Id, "dualsense"));

        /// <summary>
        /// True for the profiles the Nintendo category offers. Deliberately
        /// the single switch-pro profile for now (owner call 2026-07-18:
        /// "for now the only type be Switch Pro"). Switch 2 Pro, Joy-Cons,
        /// the NSO retro pads, and the GameCube adapter stay in Extended
        /// until the category is widened. Widening is an id-list edit here,
        /// nothing else.
        /// </summary>
        private static bool IsNintendoProfile(HMProfile p) =>
            string.Equals(p.Id, "switch-pro", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolve a profile id to the 2D + 3D asset folders PadForge should
        /// render for that controller. Profile-id prefixes match HM's catalog
        /// slugs (sony/, microsoft/) so adding a new profile in HM
        /// automatically falls through to the right family without code
        /// changes here. The two folders can differ — Xbox Series uses its
        /// own 2D layout but borrows Xbox One's 3D mesh because HC has no
        /// dedicated Series 3D model. Falls back to the existing DS4 /
        /// XBOX360 assets for unrecognized PlayStation / Xbox profiles so
        /// future HM additions degrade gracefully instead of going blank.
        /// </summary>
        public static (string Name2D, string Name3D) ResolveAssetFolders(
            string profileId, PadForge.Engine.VirtualControllerType slotType)
        {
            profileId ??= string.Empty;

            // PlayStation
            // Edge first: its profiles start with "dualsense" too, and they
            // must always get the Edge mesh, never a plain DualSense.
            if (profileId.StartsWith("dualsense-edge", StringComparison.OrdinalIgnoreCase))
                return ("DualSense", "DualSenseEdge");
            if (profileId.StartsWith("dualsense", StringComparison.OrdinalIgnoreCase))
                return ("DualSense", "DualSense");
            if (profileId.StartsWith("dualshock", StringComparison.OrdinalIgnoreCase))
                return ("DS4", "DS4");

            // Xbox One / Elite / Series / Adaptive — all share the Xbox One
            // 3D mesh (HC ships no Series-specific 3D). 2D layouts diverge:
            // Series profiles get their own white asset set.
            if (profileId.StartsWith("xbox-series-", StringComparison.OrdinalIgnoreCase))
                return ("XBOXSERIES", "XboxSeries");
            if (profileId.StartsWith("xbox-one-", StringComparison.OrdinalIgnoreCase)
                || profileId.StartsWith("xbox-elite-", StringComparison.OrdinalIgnoreCase)
                || profileId.Equals("xbox-adaptive", StringComparison.OrdinalIgnoreCase))
                return ("XBOXONE", "XBOXONE");

            // Xbox 360 family + arcade-stick / dance-pad / wheel siblings.
            if (profileId.StartsWith("xbox-360", StringComparison.OrdinalIgnoreCase))
                return ("XBOX360", "XBOX360");

            // Nintendo Switch Pro family. Both profile generations share
            // the Switch 2 Pro mesh (purchased hado model, split per-part),
            // the same arrangement as Series profiles riding the Xbox One
            // mesh. On an original Switch Pro the S2-only cosmetic parts
            // (C button, GL/GR, four player LEDs) render anyway; they are
            // inert meshes, so nothing maps or flashes wrong.
            if (profileId.StartsWith("switch-pro", StringComparison.OrdinalIgnoreCase)
                || profileId.StartsWith("switch2-pro", StringComparison.OrdinalIgnoreCase))
                return ("SWITCHPRO", "Switch2Pro");

            // Fallback per slot type — preserves existing behavior for
            // Custom / Extended / unrecognized profiles.
            return slotType switch
            {
                PadForge.Engine.VirtualControllerType.PlayStation => ("DS4", "DS4"),
                _ => ("XBOX360", "XBOX360"),
            };
        }

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
