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
                            !IsPlayStationProfile(p)));
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

        /// <summary>
        /// Resolve a profile's PadForge "row count" — how many paired
        /// stick rows and unipolar trigger rows the Extended UI should
        /// expose for it. Prefers the v1.3.9 <see cref="HMProfile.Layout"/>
        /// block when authored (so a wheel reports its wheel + pedals,
        /// a HOTAS reports its stick + throttle module, etc.), and falls
        /// back to the simple-view <see cref="HMProfile.StickCount"/> /
        /// <see cref="HMProfile.TriggerCount"/> classifier-derived values
        /// when no layout is authored.
        ///
        /// <para>The fallback path matters: HM's classifier uses the
        /// Chromium-standard-gamepad heuristic (no Rx/Ry + Z+Rz both
        /// present means 4-axis DInput right-stick layout). That fires
        /// on wheels too — Logitech G25 has X/Y/Z/Rz where Y is the
        /// clutch pedal and Z/Rz are accelerator/brake, but the
        /// classifier paints Z+Rz as a second stick because no Rx/Ry
        /// exists. The Layout block is the authoritative source for
        /// these cases; the classifier is best-effort for profiles
        /// without one.</para>
        ///
        /// <para>Mapping per layout kind, primitives-only (PadForge keeps
        /// stick / trigger / POV / button widgets, no specialised wheel
        /// gauge or HOTAS panel — that's parked for a later release):</para>
        /// <list type="bullet">
        /// <item><see cref="HMGamepadLayout"/>: layout's Sticks.Count + Triggers.Count.</item>
        /// <item><see cref="HMWheelLayout"/>: 1 paired stick (wheel X carried in stick X; Y free for the user to bind to whatever, including a clutch pedal) + Pedals.Count triggers.</item>
        /// <item><see cref="HMFlightStickLayout"/> / <see cref="HMJoystickLayout"/>: 1 stick + 1 trigger per Throttle / per separate-Rudder.</item>
        /// <item><see cref="HMHotasLayout"/>: 1 stick + 1 throttle + ThrottleSecondary.Count + 1 rudder-module trigger.</item>
        /// <item><see cref="HMPedalsLayout"/>: 0 sticks + Pedals.Count triggers.</item>
        /// <item><see cref="HMHandbrakeLayout"/> / <see cref="HMSingleAxisAccessoryLayout"/>: 0 sticks + 1 trigger.</item>
        /// <item><see cref="HMShifterLayout"/> / <see cref="HMArcadeStickLayout"/> / <see cref="HMDancePadLayout"/> / <see cref="HMRemoteLayout"/>: 0 sticks + 0 triggers (everything is buttons).</item>
        /// <item><see cref="HMGuitarLayout"/>: 0 sticks + (1 if WhammyAxis else 0) triggers.</item>
        /// <item><see cref="HMMotionWandLayout"/>: 0 sticks + (1 if TriggerAxis else 0) triggers.</item>
        /// <item><see cref="HMUnspecifiedLayout"/> or null: fall back to <c>profile.StickCount</c> + <c>profile.TriggerCount</c>.</item>
        /// </list>
        ///
        /// <para>Returned counts are clamped to <see cref="ExtendedSlotConfig.MaxAxes"/>
        /// across the (sticks*2 + triggers) total so PadForge's 8-axis UI
        /// budget isn't exceeded by an unusually rich HOTAS profile. Triggers
        /// give way to sticks during the clamp because losing a trigger
        /// loses one mappable input while losing a stick loses two.</para>
        /// </summary>
        public static (int sticks, int triggers) GetLayoutCounts(HMProfile profile)
        {
            if (profile == null) return (0, 0);

            int sticks, triggers;
            switch (profile.Layout)
            {
                case HMGamepadLayout gp:
                    sticks = gp.Sticks?.Count ?? 0;
                    triggers = gp.Triggers?.Count ?? 0;
                    break;
                case HMWheelLayout w:
                    sticks = 1;
                    triggers = w.Pedals?.Count ?? 0;
                    break;
                case HMJoystickLayout j:
                    sticks = 1;
                    triggers = (j.Throttle != null ? 1 : 0)
                             + (j.Rudder?.Kind == HMRudderKind.Pedals ? 1 : 0);
                    break;
                case HMFlightStickLayout fs:
                    sticks = 1;
                    triggers = (fs.Throttle != null ? 1 : 0)
                             + (fs.Rudder?.Kind == HMRudderKind.Pedals ? 1 : 0);
                    break;
                case HMHotasLayout h:
                    sticks = 1;
                    triggers = (h.ThrottlePrimary != null ? 1 : 0)
                             + (h.ThrottleSecondary?.Count ?? 0)
                             + (h.RudderModule != null ? 1 : 0);
                    break;
                case HMPedalsLayout p:
                    sticks = 0;
                    triggers = p.Pedals?.Count ?? 0;
                    break;
                case HMHandbrakeLayout:
                case HMSingleAxisAccessoryLayout:
                    sticks = 0;
                    triggers = 1;
                    break;
                case HMShifterLayout:
                case HMArcadeStickLayout:
                case HMDancePadLayout:
                case HMRemoteLayout:
                    sticks = 0;
                    triggers = 0;
                    break;
                case HMGuitarLayout g:
                    sticks = 0;
                    triggers = g.WhammyAxis.HasValue ? 1 : 0;
                    break;
                case HMMotionWandLayout m:
                    sticks = 0;
                    triggers = m.TriggerAxis.HasValue ? 1 : 0;
                    break;
                case HMControllerAdapterLayout:
                case HMUnspecifiedLayout:
                case null:
                default:
                    // No structured layout authored, or the kind is one
                    // we haven't enumerated. Fall back to the classifier's
                    // simple view; better than guessing zero.
                    sticks = profile.StickCount;
                    triggers = profile.TriggerCount;
                    break;
            }

            // Clamp to PadForge's 8-axis Extended budget. Drop triggers
            // first when the (sticks*2 + triggers) total overflows; sticks
            // are a paired (X, Y) input and dropping one loses two
            // mappable axes vs. a trigger's one.
            const int MaxAxes = 8;
            while (sticks * 2 + triggers > MaxAxes && triggers > 0) triggers--;
            while (sticks * 2 + triggers > MaxAxes && sticks   > 0) sticks--;
            return (sticks, triggers);
        }

        /// <summary>
        /// Resolve the per-row HID axis usages PadForge's Extended pipeline
        /// should drive on the wire. Companion to <see cref="GetLayoutCounts"/>:
        /// <c>GetLayoutCounts</c> produces the row count the UI exposes;
        /// <c>GetLayoutAxisMap</c> produces the <see cref="HMAxis"/> each row
        /// writes through. Both prefer the v1.3.9
        /// <see cref="HMProfile.Layout"/> block when authored and fall back
        /// to the classifier-derived simple view otherwise.
        ///
        /// <para>The layout-vs-classifier distinction matters for non-
        /// gamepad shapes. A wheel like the Logitech G25 has X/Y/Z/Rz
        /// where Y is the clutch pedal and Z/Rz are accelerator/brake;
        /// the classifier paints Z+Rz as a second stick (4-axis DInput
        /// heuristic), so <c>HMProfile.Triggers</c> reads as empty. Routing
        /// PadForge's Trigger 1 / 2 / 3 rows through that empty list silently
        /// drops every trigger on the way to the wire — which is exactly
        /// what surfaced after the row-count fix landed first. The
        /// authored Layout block knows the wheel uses X for steering and
        /// Z/Rz/Y for the three pedals; this helper hands those usages
        /// back per-row in PadForge's row order.</para>
        ///
        /// <para>Returns:</para>
        /// <list type="bullet">
        /// <item><c>stickAxes[i]</c> = (XAxis, YAxis) tuple for stick row <c>i</c>.
        /// <c>YAxis</c> may be <see cref="HMAxis.None"/> when the layout's
        /// stick is single-axis (a wheel's wheel axis, a single-axis
        /// accessory, etc.) — the caller skips writing the Y position
        /// to the wire when so flagged.</item>
        /// <item><c>triggerAxes[i]</c> = the <see cref="HMAxis"/> for trigger row <c>i</c>.</item>
        /// </list>
        ///
        /// <para>The returned arrays' lengths match the
        /// <c>(sticks, triggers)</c> tuple from <see cref="GetLayoutCounts"/>
        /// for the same profile (after the 8-axis clamp). Callers in
        /// <c>SubmitExtendedRawState</c> iterate by row and write
        /// <c>state.Axes[stickAxes[i].x] = ...</c> directly, bypassing
        /// <see cref="HMProfile.Sticks"/> / <see cref="HMProfile.Triggers"/>
        /// entirely so the classifier's misclassification never bites the
        /// wire layer.</para>
        /// </summary>
        public static ((HMAxis x, HMAxis y)[] stickAxes, HMAxis[] triggerAxes) GetLayoutAxisMap(HMProfile profile)
        {
            if (profile == null) return (System.Array.Empty<(HMAxis, HMAxis)>(), System.Array.Empty<HMAxis>());

            var (sticks, triggers) = GetLayoutCounts(profile);
            var stickAxes = new (HMAxis, HMAxis)[sticks];
            var triggerAxes = new HMAxis[triggers];

            switch (profile.Layout)
            {
                case HMGamepadLayout gp:
                    for (int s = 0; s < sticks; s++)
                    {
                        var st = (s < (gp.Sticks?.Count ?? 0)) ? gp.Sticks[s] : null;
                        stickAxes[s] = st != null ? (st.XAxis, st.YAxis) : (HMAxis.None, HMAxis.None);
                    }
                    for (int t = 0; t < triggers; t++)
                    {
                        var tr = (t < (gp.Triggers?.Count ?? 0)) ? gp.Triggers[t] : null;
                        triggerAxes[t] = tr?.Axis ?? HMAxis.None;
                    }
                    break;

                case HMWheelLayout w:
                    // 1 stick row carrying the steering axis on X; Y has no
                    // wheel-side counterpart (the layout's Y, when present,
                    // is a pedal rather than a paired stick axis). The
                    // submit path skips writing Y when YAxis is None, so
                    // the user's binding for the row's Y position is
                    // silently dropped — fine, since the profile didn't
                    // declare anything Y-shaped on the steering side.
                    if (sticks > 0) stickAxes[0] = (w.Wheel?.Axis ?? HMAxis.None, HMAxis.None);
                    for (int t = 0; t < triggers; t++)
                        triggerAxes[t] = (t < (w.Pedals?.Count ?? 0)) ? w.Pedals[t].Axis : HMAxis.None;
                    break;

                case HMJoystickLayout j:
                    if (sticks > 0) stickAxes[0] = (j.Stick?.XAxis ?? HMAxis.None, j.Stick?.YAxis ?? HMAxis.None);
                    {
                        int t = 0;
                        if (t < triggers && j.Throttle != null) triggerAxes[t++] = j.Throttle.Axis;
                        if (t < triggers && j.Rudder?.Kind == HMRudderKind.Pedals) triggerAxes[t++] = j.Rudder.Axis;
                        for (; t < triggers; t++) triggerAxes[t] = HMAxis.None;
                    }
                    break;

                case HMFlightStickLayout fs:
                    if (sticks > 0) stickAxes[0] = (fs.Stick?.XAxis ?? HMAxis.None, fs.Stick?.YAxis ?? HMAxis.None);
                    {
                        int t = 0;
                        if (t < triggers && fs.Throttle != null) triggerAxes[t++] = fs.Throttle.Axis;
                        if (t < triggers && fs.Rudder?.Kind == HMRudderKind.Pedals) triggerAxes[t++] = fs.Rudder.Axis;
                        for (; t < triggers; t++) triggerAxes[t] = HMAxis.None;
                    }
                    break;

                case HMHotasLayout h:
                    if (sticks > 0) stickAxes[0] = (h.Stick?.XAxis ?? HMAxis.None, h.Stick?.YAxis ?? HMAxis.None);
                    {
                        int t = 0;
                        if (t < triggers && h.ThrottlePrimary != null) triggerAxes[t++] = h.ThrottlePrimary.Axis;
                        if (h.ThrottleSecondary != null)
                            for (int k = 0; k < h.ThrottleSecondary.Count && t < triggers; k++)
                                triggerAxes[t++] = h.ThrottleSecondary[k].Axis;
                        if (t < triggers && h.RudderModule != null) triggerAxes[t++] = h.RudderModule.Axis;
                        for (; t < triggers; t++) triggerAxes[t] = HMAxis.None;
                    }
                    break;

                case HMPedalsLayout p:
                    for (int t = 0; t < triggers; t++)
                        triggerAxes[t] = (t < (p.Pedals?.Count ?? 0)) ? p.Pedals[t].Axis : HMAxis.None;
                    break;

                case HMHandbrakeLayout hb:
                    if (triggers > 0) triggerAxes[0] = hb.Axis;
                    break;

                case HMSingleAxisAccessoryLayout sa:
                    if (triggers > 0) triggerAxes[0] = sa.Axis;
                    break;

                case HMGuitarLayout g:
                    if (triggers > 0 && g.WhammyAxis.HasValue) triggerAxes[0] = g.WhammyAxis.Value;
                    break;

                case HMMotionWandLayout m:
                    if (triggers > 0 && m.TriggerAxis.HasValue) triggerAxes[0] = m.TriggerAxis.Value;
                    break;

                case HMShifterLayout:
                case HMArcadeStickLayout:
                case HMDancePadLayout:
                case HMRemoteLayout:
                case HMControllerAdapterLayout:
                    // No analog axes; arrays stay empty per GetLayoutCounts.
                    break;

                case HMUnspecifiedLayout:
                case null:
                default:
                    // Fall back to the classifier's simple view. Same as
                    // the GetLayoutCounts fallback path so per-row HMAxis
                    // assignment matches the row count the UI exposes.
                    var simpleSticks = profile.Sticks;
                    var simpleTriggers = profile.Triggers;
                    for (int s = 0; s < sticks; s++)
                    {
                        var st = (s < simpleSticks.Count) ? simpleSticks[s] : null;
                        stickAxes[s] = st != null ? (st.XAxis, st.YAxis) : (HMAxis.None, HMAxis.None);
                    }
                    for (int t = 0; t < triggers; t++)
                        triggerAxes[t] = (t < simpleTriggers.Count) ? simpleTriggers[t].Axis : HMAxis.None;
                    break;
            }

            return (stickAxes, triggerAxes);
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

        private static bool IsXboxProfile(HMProfile p) =>
            IsXboxVendor(p.Vendor) &&
            (ContainsToken(p.Name, "Xbox") || ContainsToken(p.Id, "xbox"));

        private static bool IsPlayStationProfile(HMProfile p) =>
            IsPlayStationVendor(p.Vendor) &&
            (ContainsToken(p.Name, "DualShock") || ContainsToken(p.Name, "DualSense")
             || ContainsToken(p.Id, "dualshock") || ContainsToken(p.Id, "dualsense"));

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
