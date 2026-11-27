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
    /// the embedded profile JSONs (344 in HIDMaestro 2.4.0) but never instantiates HMController
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

        /// <summary>The id suffix every profile the user imports carries
        /// (SettingsService.AddUserProfile), which keeps a capture's id clear
        /// of the built-in catalog's.</summary>
        public const string UserProfileIdSuffix = "-user";

        /// <summary>The name PadForge shows for a profile: the catalog's own,
        /// a product name read the same in every language, except the
        /// synthetic Custom entry, whose name is a word of PadForge's and is
        /// shown in the interface's language, and a profile the user
        /// imported, whose own name is shown with the interface language's
        /// "(User Generated)" mark. Each profile keeps its own Name, which
        /// HIDMaestro reads for a device name and mapping string, so the
        /// virtual device stays the same in every language.</summary>
        /// <remarks>Read on the 30 Hz dashboard lane (PadViewModel's
        /// PresetDisplayName), so an imported profile's name is made once
        /// for each language and kept with the profile.</remarks>
        public static string DisplayName(HIDMaestro.HMProfile profile)
        {
            if (profile == null) return string.Empty;
            if (string.Equals(profile.Id, CustomProfileId, StringComparison.OrdinalIgnoreCase))
                return PadForge.Resources.Strings.Strings.Instance.Pad_Preset_Custom;
            string name = profile.Name ?? string.Empty;
            if (profile.Id == null || !profile.Id.EndsWith(UserProfileIdSuffix, StringComparison.OrdinalIgnoreCase))
                return name;
            string format = PadForge.Resources.Strings.Strings.Instance.Pad_Preset_UserGenerated_Format;
            if (s_userNames.TryGetValue(profile, out var made) && string.Equals(made.Format, format, StringComparison.Ordinal))
                return made.Name;
            made = new UserProfileName(format, string.Format(format, name));
            s_userNames.AddOrUpdate(profile, made);
            return made.Name;
        }

        /// <summary>An imported profile's shown name and the format it was
        /// made with.</summary>
        private sealed record UserProfileName(string Format, string Name);

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HIDMaestro.HMProfile, UserProfileName> s_userNames = new();

        private static readonly object _initLock = new object();

        /// <summary>One load of the catalog: every list and map the accessors
        /// hand out, built whole before it is published and never changed
        /// after. A reload publishes a new one in a single assignment, so a
        /// reader on another thread, a lifecycle task's
        /// CreateHMaestroController among them, reads with no lock and sees
        /// the old catalog or the new one, never one half emptied or half
        /// built.</summary>
        private sealed class Loaded
        {
            public List<HMProfile> All = new();
            public List<HMProfile> Xbox = new();
            public List<HMProfile> PlayStation = new();
            public List<HMProfile> Nintendo = new();
            public List<HMProfile> Extended = new();
            public List<HMProfile> Keyboard = new();
            public List<HMProfile> Mouse = new();
            public Dictionary<string, UserProfileProblem> UserProfileProblems = new(StringComparer.Ordinal);

            /// <summary>Every loaded profile by its ID slug, the first in
            /// <see cref="All"/> order where two share one. The UI timer
            /// looks up each slot's profile 30 times a second, and a scan of
            /// the list with a lambda made a closure and a delegate each
            /// time, the most numerous garbage in the program at rest
            /// (measured 2026-11-16).</summary>
            public readonly Dictionary<string, HMProfile> ById = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The published catalog, null until the first load.</summary>
        private static volatile Loaded s_loaded;

        /// <summary>Why one user profile is missing from the pickers.</summary>
        public sealed class UserProfileProblem
        {
            /// <summary>HIDMaestro's refusal of the profile, with the JSON
            /// path and the reason. Null when HIDMaestro loaded the profile
            /// and it has no HID descriptor, which keeps it out of the
            /// pickers all the same.</summary>
            public HMProfileLoadIssue Refusal { get; init; }

            /// <summary>Why the profile could not be staged and loaded for
            /// the check, as the failure says it, or null when it was
            /// checked. A profile that was never checked is kept out of
            /// the pickers and refused by the import.</summary>
            public string CheckFailure { get; init; }
        }

        /// <summary>
        /// Source of user-imported HIDMaestro profile JSONs to mix into the
        /// Extended category alongside the built-in catalog. Populated by
        /// the settings layer on startup (profiles live in PadForge.xml
        /// under &lt;UserProfiles&gt;) and re-populated after every live
        /// import. Each load of the catalog invokes the provider once; if
        /// it's null or returns null, only the built-in catalog + the
        /// synthetic Custom entry appear.
        /// </summary>
        public static System.Func<System.Collections.Generic.IReadOnlyList<string>> UserProfilesProvider { get; set; }

        private static readonly WeakHandlers<System.EventHandler> s_catalogReloaded = new();

        /// <summary>Raised after the catalog is (re)built so UI bindings
        /// that depend on Extended/All profile lists can refresh. A
        /// subscriber is held weakly (WeakHandlers): every pad view model
        /// subscribes, and a strong hook kept each one alive for the rest of
        /// the process.</summary>
        public static event System.EventHandler CatalogReloaded
        {
            add => s_catalogReloaded.Add(value);
            remove => s_catalogReloaded.Remove(value);
        }

        /// <summary>All loaded profiles, ordered by ID slug.</summary>
        public static IReadOnlyList<HMProfile> AllProfiles
        {
            get => Current.All;
        }

        /// <summary>Xbox-family controller profiles. Filter is the
        /// intersection of "vendor is Microsoft" AND "name or id contains
        /// Xbox" — keeps the bucket honest to its category label and
        /// drops any Microsoft-vendor profiles that aren't Xbox controllers
        /// (Surface peripherals, generic HID devices, etc.) into Extended.</summary>
        public static IReadOnlyList<HMProfile> XboxProfiles
        {
            get => Current.Xbox;
        }

        /// <summary>PlayStation-family controller profiles. Filter is the
        /// intersection of "vendor is Sony" AND "name or id contains
        /// DualShock or DualSense" — covers DualShock 3/4 + DualSense /
        /// DualSense Edge only. Non-controller Sony profiles (PS Move,
        /// PS3 Remote, PS Classic, etc.) drop to Extended.</summary>
        public static IReadOnlyList<HMProfile> PlayStationProfiles
        {
            get => Current.PlayStation;
        }

        /// <summary>Nintendo-family controller profiles: switch-pro,
        /// switch-pro-usb, switch2-pro-controller and
        /// switch2-pro-controller-composite (see IsNintendoProfile). Mutually
        /// exclusive with the other buckets.</summary>
        public static IReadOnlyList<HMProfile> NintendoProfiles
        {
            get => Current.Nintendo;
        }

        /// <summary>The synthetic Custom profile first, then the profiles
        /// that match none of the strict Xbox, PlayStation and Nintendo
        /// filters above and are no keyboard or mouse: third-party gamepads,
        /// flight sticks, wheels, HOTAS, plus any vendor-Microsoft /
        /// vendor-Sony profiles whose name doesn't carry the canonical
        /// product family (Surface, PS Move, PS Classic, etc.). Mutually
        /// exclusive with the other buckets so each profile appears in
        /// exactly one category.</summary>
        public static IReadOnlyList<HMProfile> ExtendedProfiles
        {
            get => Current.Extended;
        }

        /// <summary>Keyboard profiles (HIDMaestro 1.12.0, HIDMaestro#67): the
        /// devices a Keyboard + Mouse slot can send its keys through instead of
        /// SendInput. Profile type "keyboard". Never in any gamepad bucket: a
        /// keyboard takes SubmitKeyboard, not gamepad state.</summary>
        public static IReadOnlyList<HMProfile> KeyboardProfiles
        {
            get => Current.Keyboard;
        }

        /// <summary>Mouse profiles, profile type "mouse", the twin of
        /// <see cref="KeyboardProfiles"/>.</summary>
        public static IReadOnlyList<HMProfile> MouseProfiles
        {
            get => Current.Mouse;
        }

        /// <summary>True for a keyboard or mouse profile, by the profile's own
        /// type (ControllerProfile.IsKeyboardOrMouse). HIDMaestro delivers
        /// every report of more than these (ControllerProfile.DeliversEveryReport):
        /// the touchscreen, touchpad and pen types, and an "other" profile
        /// whose descriptor has a mouse, keyboard, touch or pen collection.
        /// Those stay in Extended, and their frames go out in order on a
        /// submit lane (HMaestroVirtualController.FinishConnect).</summary>
        internal static bool IsKeyboardOrMouseProfile(HMProfile p) =>
            p != null
            && (string.Equals(p.Type, "keyboard", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Type, "mouse", StringComparison.OrdinalIgnoreCase));

        /// <summary>Direct lookup by stable profile ID slug, or null if not loaded.</summary>
        public static HMProfile GetProfileById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var catalog = Current;
            LookupSeam?.Invoke();
            return catalog.ById.TryGetValue(id, out var profile) ? profile : null;
        }

        /// <summary>Test seam run by <see cref="GetProfileById"/> between
        /// taking the catalog and reading it, null in production. A test
        /// reloads the catalog there.</summary>
        internal static System.Action LookupSeam;

        /// <summary>Why the user profile with this JSON, as
        /// <see cref="UserProfilesProvider"/> handed it in, is not in the
        /// pickers, or null when it is.</summary>
        public static UserProfileProblem GetUserProfileProblem(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return Current.UserProfileProblems.TryGetValue(json, out var problem) ? problem : null;
        }

        /// <summary>Why HIDMaestro would keep this profile JSON out of the
        /// pickers, checked in a context of its own beside the built-in
        /// catalog, or null when it loads with a descriptor. The import
        /// asks before it stores a profile, so a slot is never pointed at
        /// one that is not there. Null too when HIDMaestro cannot be
        /// loaded at all, which the catalog and the engine report on their
        /// own.</summary>
        public static UserProfileProblem CheckUserProfile(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var ctx = HMaestroContexts.ForProfiles();
                ctx.LoadDefaultProfiles();
                return LoadUserProfiles(ctx, new[] { json }).TryGetValue(json, out var problem) ? problem : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Test seam for the folder the user's profiles are staged
        /// under, null in production, where it is the protected folder's
        /// Work\HIDMaestroProfiles. A test throws from it as that folder
        /// throws when it cannot be made. The setting thread's alone, so the
        /// checks of other tests running beside it stage as they
        /// would.</summary>
        [System.ThreadStatic]
        internal static System.Func<string> StagingFolderSeam;

        /// <summary>
        /// Loads the user's profile JSONs into <paramref name="ctx"/>, which
        /// already holds the built-in catalog, and returns why each one that
        /// will not appear in the pickers is missing, keyed by its JSON.
        ///
        /// <para>HIDMaestro only loads profiles from a directory, so the
        /// JSONs are staged to a temporary one just long enough for
        /// LoadProfilesFromDirectory to read them. Since 2.0.0 that load is
        /// strict and throws for no single file: a file that does not parse,
        /// a key the schema lacks, a value a check refuses or an id already
        /// loaded refuses that file alone, and HMContext.ProfileLoadIssues
        /// lists it with its JSON path and the reason. A profile that loads
        /// without a HID descriptor is left out of the pickers too
        /// (IsDeployable), so it gets a problem of its own. A model problem
        /// leaves the profile in the pickers, running on the model its
        /// identity gives it, and only goes to the log.</para>
        /// </summary>
        internal static Dictionary<string, UserProfileProblem> LoadUserProfiles(
            HMContext ctx, IReadOnlyList<string> userJsons)
        {
            var problems = new Dictionary<string, UserProfileProblem>(StringComparer.Ordinal);
            if (ctx == null || userJsons == null || userJsons.Count == 0) return problems;

            string dir = null;
            try
            {
                // A folder of its own in the user's temp folder: HIDMaestro
                // reads every profile in it.
                var stagingSeam = StagingFolderSeam;
                dir = System.IO.Path.Combine(
                    stagingSeam != null ? stagingSeam()
                        : System.IO.Path.GetTempPath(),
                    $"padforge-userprofiles-{Guid.NewGuid():N}");
                System.IO.Directory.CreateDirectory(dir);
                // The load names each file under the full path of the
                // directory it was given (ProfileDatabase.Load), so the
                // staged names are built the same way to match its issues.
                string root = System.IO.Path.GetFullPath(dir);
                var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < userJsons.Count; i++)
                {
                    string json = userJsons[i];
                    if (string.IsNullOrWhiteSpace(json)) continue;
                    string file = System.IO.Path.Combine(root, $"user-{i:D4}.json");
                    System.IO.File.WriteAllText(file, json);
                    staged[file] = json;
                }
                if (staged.Count == 0) return problems;
                ctx.LoadProfilesFromDirectory(root);

                foreach (var issue in ctx.ProfileLoadIssues)
                {
                    if (!staged.TryGetValue(issue.File, out var json)) continue;
                    if (issue.Kind == HMProfileLoadIssueKind.ModelProblem)
                    {
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PROFILES: user profile {issue.ProfileId} loaded with a model problem at {issue.JsonPath}: {issue.Reason}");
                        continue;
                    }
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PROFILES: user profile {issue.ProfileId ?? "(no id)"} refused at {issue.JsonPath}: {issue.Reason}");
                    if (!problems.ContainsKey(json))
                        problems[json] = new UserProfileProblem { Refusal = issue };
                }

                foreach (string json in staged.Values)
                {
                    if (problems.ContainsKey(json)) continue;
                    string id = null;
                    try { id = System.Text.Json.Nodes.JsonNode.Parse(json)?["id"]?.GetValue<string>(); }
                    catch { }
                    var loaded = string.IsNullOrEmpty(id) ? null : ctx.GetProfile(id);
                    if (loaded == null || loaded.IsDeployable) continue;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PROFILES: user profile {id} has no HID descriptor and is not offered");
                    problems[json] = new UserProfileProblem();
                }
            }
            catch (Exception ex)
            {
                // A staging or load failure must not break the built-in
                // profiles, which are already loaded. A profile it left
                // unchecked has a problem of its own, so the import refuses
                // it and Manage Profiles says why it is not in the pickers.
                PadForge.Engine.SdlDiagLog.WriteLine($"PROFILES: user profiles not checked: {ex.GetType().Name}: {ex.Message}");
                foreach (string json in userJsons)
                    if (!string.IsNullOrWhiteSpace(json) && !problems.ContainsKey(json))
                        problems[json] = new UserProfileProblem { CheckFailure = ex.Message };
            }
            finally
            {
                if (dir != null)
                {
                    try { System.IO.Directory.Delete(dir, recursive: true); }
                    catch { }
                }
            }
            return problems;
        }

        /// <summary>
        /// Builds the catalog again and publishes it. Call after a user
        /// imports a new profile so the Extended dropdown picks it up.
        /// </summary>
        public static void Reload()
        {
            // The catalog in place stays in place while the new one builds.
            lock (_initLock) s_loaded = Load();
            foreach (var reloaded in s_catalogReloaded.Live()) reloaded(null, System.EventArgs.Empty);
        }

        /// <summary>A catalog <see cref="Build"/> made and
        /// <see cref="Publish"/> has not published yet, with the user
        /// profiles it was built from.</summary>
        public sealed class Built
        {
            internal object Catalog;
            internal IReadOnlyList<string> UserJsons;
        }

        /// <summary>Builds the catalog with <paramref name="userJsons"/> as
        /// the user's profiles, and publishes nothing. It loads every
        /// built-in profile in a context of its own, so Manage Profiles runs
        /// it off the UI thread.</summary>
        public static Built Build(IReadOnlyList<string> userJsons)
        {
            var jsons = userJsons?.ToList() ?? new List<string>();
            return new Built { Catalog = Load(jsons), UserJsons = jsons };
        }

        /// <summary>Publishes a catalog <see cref="Build"/> made, as
        /// <see cref="Reload"/> publishes its own, while the user's profiles
        /// are still the ones it was built from. When they changed since, the
        /// catalog is built again here.</summary>
        public static void Publish(Built built)
        {
            var now = UserProfilesProvider?.Invoke() ?? (IReadOnlyList<string>)System.Array.Empty<string>();
            if (built?.Catalog is not Loaded catalog || !now.SequenceEqual(built.UserJsons, StringComparer.Ordinal))
            {
                Reload();
                return;
            }
            lock (_initLock) s_loaded = catalog;
            foreach (var reloaded in s_catalogReloaded.Live()) reloaded(null, System.EventArgs.Empty);
        }

        /// <summary>The published catalog, loaded on first use.</summary>
        private static Loaded Current
        {
            get
            {
                var loaded = s_loaded;
                if (loaded != null) return loaded;
                lock (_initLock)
                {
                    loaded = s_loaded;
                    if (loaded == null)
                    {
                        loaded = Load();
                        s_loaded = loaded;
                    }
                    return loaded;
                }
            }
        }

        /// <summary>Builds the catalog with the provider's user profiles.
        /// Caller holds _initLock.</summary>
        private static Loaded Load() => Load(UserProfilesProvider?.Invoke());

        /// <summary>Builds the catalog with <paramref name="userJsons"/> as the
        /// user's profiles. It publishes nothing and shares nothing, so
        /// <see cref="Build"/> runs it without the lock.</summary>
        private static Loaded Load(IReadOnlyList<string> userJsons)
        {
            var c = new Loaded();
            try
            {
                using var ctx = HMaestroContexts.ForProfiles();
                ctx.LoadDefaultProfiles();
                // A refused catalog file would drop a built-in profile
                // without a word, so whatever the catalog's own load
                // lists goes to the log. HIDMaestro's release checks keep
                // this empty.
                foreach (var issue in ctx.ProfileLoadIssues)
                    PadForge.Engine.SdlDiagLog.WriteLine($"PROFILES: catalog {issue.Kind} {issue}");

                // The user's imported profiles load through the same
                // context, so they get the same parse and checks as the
                // built-in catalog, and each one that does not make the
                // pickers keeps its reason for Manage Profiles.
                c.UserProfileProblems = LoadUserProfiles(ctx, userJsons);

                // Filter undeployable profiles at catalog load. HIDMaestro
                // ships some profile JSONs that lack a HID descriptor, and
                // HMContext.CreateController throws HMProfileException
                // "Profile 'X' at $.descriptor: the profile has no HID
                // descriptor and cannot be deployed." for those.
                // Excluding them at the catalog
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
                c.All = ctx.AllProfiles
                    .Where(p => p.IsDeployable)
                    .Where(p => !IsWithheldProfile(p.Id))
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Vendor match is prefix-based: HM upstream isn't strictly
                // consistent on the vendor field: older Sony profiles use
                // "Sony", the dualsense-bt-full profile added in HM 1.2.2
                // uses "Sony Interactive Entertainment". Same shape for
                // Microsoft if a future HM rev ships "Microsoft Corporation".
                // StartsWith keeps both variants in the right family bucket
                // without us needing to chase upstream string changes.
                //
                // Xbox / PlayStation buckets additionally require the
                // profile name (or id slug) to carry the canonical
                // product family: "Xbox" for Microsoft, "DualShock" or
                // "DualSense" for Sony. Vendor-only matching pulled in
                // peripherals that share the brand but aren't gamepads
                // (Surface, PS Move, PS3 Remote, PS Classic), which
                // confused the user-facing pickers; those drop to
                // Extended now.
                c.Xbox = c.All
                    .Where(IsXboxProfile)
                    .ToList();

                c.PlayStation = c.All
                    .Where(IsPlayStationProfile)
                    .ToList();

                c.Nintendo = c.All
                    .Where(IsNintendoProfile)
                    .ToList();

                // Extended = everything that's not Xbox, PlayStation,
                // Nintendo, a keyboard or a mouse, plus the synthetic "Custom" entry at the top so the
                // user can define a fully custom VC without inheriting
                // from any catalog profile. Custom sorts first to
                // make it the discoverable default for new Extended
                // slots. Also prepended to the full list so that
                // GetProfileById lookups resolve it for Step 5's
                // CreateHMaestroController fallback path, since
                // HIDMaestro's own HMContext.GetProfile does not know
                // the synthetic profile.
                var custom = BuildCustomProfile();
                c.All.Insert(0, custom);
                var extended = new List<HMProfile> { custom };
                extended.AddRange(c.All
                    .Where(p =>
                        p.Id != CustomProfileId &&
                        !IsXboxProfile(p) &&
                        !IsPlayStationProfile(p) &&
                        !IsNintendoProfile(p) &&
                        !IsKeyboardOrMouseProfile(p)));
                c.Extended = extended;

                // Keyboards and mice (HIDMaestro 1.12.0) belong to the
                // Keyboard + Mouse slot's device pickers and to no gamepad
                // bucket. In Extended they would have taken gamepad
                // state, which their reports cannot carry.
                c.Keyboard = c.All
                    .Where(p => string.Equals(p.Type, "keyboard", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                c.Mouse = c.All
                    .Where(p => string.Equals(p.Type, "mouse", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch
            {
                // Catalog unavailable. Leave the empty lists in place.
                // The engine's own HMContext will surface the real error.
            }

            foreach (var profile in c.All)
                if (profile?.Id != null)
                    c.ById.TryAdd(profile.Id, profile);
            return c;
        }

        /// <summary>
        /// Profiles HIDMaestro ships that PadForge deliberately does not
        /// offer yet. Same mechanism as the IsDeployable filter above and
        /// for the same reason: a profile the user cannot be given a good
        /// result from should not appear in any dropdown.
        ///
        /// <para>The three Valve composite personas (#338, milestone
        /// v4.4.0) present as real Valve hardware to Steam, which is the
        /// point of them, but PadForge has no controller art for any of
        /// them. There is no 3D mesh for the Steam Deck, the 2015 Steam
        /// Controller or the 2026 Steam Controller, and no 2D set for the
        /// 2026 model at all. Selecting one today gives a working device
        /// under a preview of something else entirely. They come back by
        /// deleting an id from this set once the artwork ships.</para>
        ///
        /// <para>The plain "steam-controller" and "steam-deck" profiles
        /// are NOT withheld. They predate this work, they have been
        /// selectable for releases, and removing them would take away
        /// something users already have.</para>
        ///
        /// <para>No released build ever offered these. 4.3.0 shipped
        /// HIDMaestro.Core 1.6.2.0, which does not contain them at all.
        /// They reached v4-dev with the 1.7.0.0 bump (first carried by
        /// dbad1717) and were selectable there, which is what this closes.</para>
        ///
        /// <para>This hides a profile from the pickers. It does NOT break a
        /// slot that already has one saved, because Step 5 asks
        /// HMContext.GetProfile before it asks this catalog, and HIDMaestro's
        /// own context is unfiltered. Only a development build could have
        /// saved one, but the asymmetry costs nothing and keeps the gate
        /// honest about what it is. Do not "fix" it by gating Step 5.</para>
        /// </summary>
        private static readonly HashSet<string> WithheldProfileIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // The three Valve entries that lived here were released once
                // their meshes and overlays landed.
                //
                // The Wii U GameCube adapter on USB/IP (057E:0337). HIDMaestro
                // 2.0.0 gave it a descriptor, so it became deployable for the
                // first time, and offering it as it stands would give a device
                // nothing reads: Dolphin and Slippi open it only after
                // HMController.BindWinUsb, and Windows' HID driver fails it
                // with Code 10, as on a real adapter. It also reports four
                // ports where a slot drives one, and VendorUsbDriverInstaller
                // binds WinUSB to a real adapter of the same ids for input.
                // No released build offered it.
                "gamecube-adapter",
            };

        internal static bool IsWithheldProfile(string profileId) =>
            !string.IsNullOrEmpty(profileId) && WithheldProfileIds.Contains(profileId);

        /// <summary>True when the FIRST input report a descriptor declares
        /// belongs to a mouse or a keyboard collection.
        ///
        /// <para>Such a profile cannot be driven. HIDMaestro frames every
        /// input we submit into the descriptor's first input report and
        /// prepends that report's id: SubmitRawReport takes data bytes only
        /// (its own XML says so) and the driver re-prepends
        /// ctx-&gt;FirstInputReportId, which
        /// DeviceOrchestrator.ComputeInputReportByteLength reads as the
        /// first Report ID in the descriptor. Point that at a mouse and
        /// every frame we send becomes cursor motion.</para>
        ///
        /// <para>Valve's 2026 Steam Controller is exactly this shape. It
        /// carries its lizard-mode mouse (report 0x40) and keyboard (0x41)
        /// on the same interface as its controller state (0x42), and the
        /// mouse comes first, so a slot on that profile drove the pointer
        /// with its own rolling sequence number: byte 1 of our frame landed
        /// on the mouse's relative X, 250 times a second.</para>
        ///
        /// <para>FIXED UPSTREAM in HIDMaestro v1.7.1 (HM#58). A profile
        /// that declares an input report id and is always armed now emits
        /// verbatim, so the 2026 pad's frames land on 0x42 where they were
        /// built to. HIDMaestro infers that from length; PadForge says it
        /// outright by calling SubmitRawExtendedReport, so the pairing
        /// cannot flip silently on a size change.</para>
        ///
        /// <para>This stays a TRIPWIRE, never a gate. It reads the
        /// descriptor rather than an id list, and
        /// PointingReportProfileGuardTests uses it to require that any
        /// pointing-led profile carrying a packer is on that verbatim
        /// path.</para></summary>
        internal static bool LeadsWithAPointingReport(byte[] descriptor)
        {
            if (descriptor == null || descriptor.Length == 0) return false;

            uint page = 0, pendingUsage = 0, appPage = 0, appUsage = 0;
            bool haveUsage = false, sawReportId = false;
            int depth = 0, currentReportId = 0, firstInputReportId = -1;
            uint firstInputPage = 0, firstInputUsage = 0;

            for (int i = 0; i < descriptor.Length;)
            {
                byte prefix = descriptor[i];
                int size = prefix & 0x03;
                if (size == 3) size = 4;
                int type = (prefix >> 2) & 0x03;
                int tag = (prefix >> 4) & 0x0F;

                uint val = 0;
                for (int j = 0; j < size && i + 1 + j < descriptor.Length; j++)
                    val |= (uint)descriptor[i + 1 + j] << (8 * j);

                if (type == 1)                       // Global
                {
                    if (tag == 0) page = val;        // Usage Page
                    if (tag == 8) { currentReportId = (int)val; sawReportId = true; }
                }
                else if (type == 2)                  // Local
                {
                    if (tag == 0) { pendingUsage = val; haveUsage = true; }
                }
                else if (type == 0)                  // Main
                {
                    if (tag == 10)                   // Collection
                    {
                        if (depth == 0 && haveUsage) { appUsage = pendingUsage; appPage = page; }
                        depth++;
                    }
                    else if (tag == 12)              // End Collection
                    {
                        depth--;
                    }
                    else if (tag == 8 && firstInputReportId < 0)   // Input
                    {
                        firstInputReportId = currentReportId;
                        firstInputPage = appPage;
                        firstInputUsage = appUsage;
                    }
                    haveUsage = false;
                }

                i += 1 + size;
            }

            // A descriptor with no report ids has one input report, and the
            // driver prepends nothing, so there is nothing to collide with.
            if (!sawReportId || firstInputReportId < 0) return false;

            // Generic Desktop / Mouse and Generic Desktop / Keyboard.
            return firstInputPage == 0x01 && (firstInputUsage == 0x02 || firstInputUsage == 0x06);
        }

        /// <summary>True when the descriptor declares an input the host
        /// reads as a movement: a Main Input item that is data, not
        /// constant, with the Relative flag (bit 2 of the item's data, HID
        /// 1.11 section 6.2.2.5), as a mouse's X, Y and wheel are.
        /// HIDMaestro packs a state's axes into such a field as they are
        /// (HidReportBuilder.BuildReportInto, whose InputField keeps no
        /// Relative flag), so each report the device sends is one movement.
        /// A long item (0xFE) is stepped over.</summary>
        internal static bool DeclaresRelativeInput(byte[] descriptor)
        {
            if (descriptor == null) return false;
            for (int i = 0; i < descriptor.Length;)
            {
                byte prefix = descriptor[i];
                if (prefix == 0xFE)
                {
                    int longSize = i + 1 < descriptor.Length ? descriptor[i + 1] : 0;
                    i += 3 + longSize;
                    continue;
                }
                int size = prefix & 0x03;
                if (size == 3) size = 4;
                int type = (prefix >> 2) & 0x03;
                int tag = (prefix >> 4) & 0x0F;
                uint val = 0;
                for (int j = 0; j < size && i + 1 + j < descriptor.Length; j++)
                    val |= (uint)descriptor[i + 1 + j] << (8 * j);
                if (type == 0 && tag == 8 && (val & 0x01) == 0 && (val & 0x04) != 0) return true;
                i += 1 + size;
            }
            return false;
        }

        internal static bool DeclaresRelativeInput(HMProfile p)
        {
            byte[] desc;
            try { desc = p?.GetDescriptorBytes(); }
            catch { return false; }
            return DeclaresRelativeInput(desc);
        }

        internal static bool LeadsWithAPointingReport(HMProfile p)
        {
            byte[] desc;
            try { desc = p?.GetDescriptorBytes(); }
            catch { return false; }
            return LeadsWithAPointingReport(desc);
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
        /// True for the profiles the Nintendo category offers: the original
        /// Switch Pro Controller on its Bluetooth descriptor and, from
        /// HIDMaestro 2.0.0, on its USB one (switch-pro-usb, the USB
        /// handshake and 64-byte reports SDL's USB path expects), and the
        /// Switch 2 Pro Controller in its two forms. switch2-pro-controller is
        /// the HID gamepad every reader sees. switch2-pro-controller-composite
        /// (HIDMaestro 1.11.0, HIDMaestro#66) is the pad's two-interface USB
        /// form, which Steam and SDL built with libusb start and read motion
        /// from. No other reader gets input from it, as with the real pad on
        /// Windows, so it is offered beside the plain one and replaces
        /// nothing. Joy-Cons and the NSO retro pads stay in Extended until the
        /// category is widened further, and the GameCube adapter persona is
        /// withheld (WithheldProfileIds). Widening is an id-list edit here,
        /// beside NintendoPreviewMap.FamilyOf and
        /// MacroItem.IsNintendoLetteredProfile.
        /// </summary>
        private static bool IsNintendoProfile(HMProfile p) =>
            string.Equals(p.Id, "switch-pro", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Id, "switch-pro-usb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Id, "switch2-pro-controller", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Id, "switch2-pro-controller-composite", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for the three presets in the motion-carrying families whose
        /// input report has no motion field: the DualShock 3 (its HM profile
        /// declares no extended report, and PadForge has no packer for it),
        /// the plain Switch 2 Pro (its report 0x09 carries no IMU data, the
        /// controller-slots page says the same, and its composite persona's
        /// report 0x05 carries both sensors), and the plain Steam Deck
        /// (its standard 10-byte report has no motion field, its HM profile
        /// declares no extended report, and PadForge packs only the
        /// composite). Their Motion rows still feed the motion server.
        /// </summary>
        internal static bool ReportCarriesNoMotion(string profileId) =>
            string.Equals(profileId, "dualshock-3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profileId, "switch2-pro-controller", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profileId, "steam-deck", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for the preset whose report carries motion but no pitch or
        /// roll rate: the DualShock 3 (SIXAXIS): Full, whose extended report
        /// carries the accelerometer and the yaw rate only, as the pad does.
        /// A Speed turn on its Motion Pitch or Roll row reaches the game only
        /// as tilt, which the slot's motion composes into the accelerometer
        /// only while no real accelerometer is mapped.
        /// </summary>
        internal static bool ReportCarriesNoPitchRollRate(string profileId) =>
            string.Equals(profileId, "dualshock-3-full", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for the preset whose report carries button pressure: the
        /// DualShock 3 (SIXAXIS): Full, HIDMaestro 1.10.0's form of the pad
        /// that Sony's sixaxis driver and DsHidMini's SXS mode present, and
        /// the one PCSX2 and RPCS3 read pressure from (discussion #476). Its
        /// slots grow the ten pressure rows. An id, not a descriptor read,
        /// because the SDK keeps the profile's sixaxis flag internal.
        /// </summary>
        internal static bool ReportCarriesPressure(string profileId) =>
            string.Equals(profileId, "dualshock-3-full", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for the two DualShock 3 presets, dualshock-3 and
        /// dualshock-3-full. The pad has Select and Start where the DualShock
        /// 4 and the DualSense have Share and Options, and no touchpad: both
        /// HM profiles describe none (profiles/sony/dualshock-3*.json carry
        /// no touch field, where every DualShock 4 and DualSense profile
        /// does).
        /// </summary>
        internal static bool IsDualShock3(string profileId) =>
            profileId != null && profileId.StartsWith("dualshock-3", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when a PlayStation slot's preset has a touchpad to send:
        /// every one but the DualShock 3's. A slot with no preset yet runs
        /// the category default, dualsense-composite, which has one.
        /// </summary>
        internal static bool ReportCarriesTouchpad(string profileId) => !IsDualShock3(profileId);

        /// <summary>
        /// Resolve a profile id to the 2D + 3D asset folders PadForge should
        /// render for that controller. Profile-id prefixes match HM's catalog
        /// slugs (sony/, microsoft/) so adding a new profile in HM
        /// automatically falls through to the right family without code
        /// changes here. The two folders can differ: Xbox One, Elite and
        /// Adaptive profiles keep the Xbox One 2D layout but borrow the Xbox
        /// Series 3D mesh, and an original Switch Pro keeps its own 2D set
        /// but borrows the Switch 2 Pro mesh. Falls back to the existing DS4 /
        /// XBOX360 assets for unrecognized PlayStation / Xbox profiles so
        /// future HM additions degrade gracefully instead of going blank.
        /// </summary>
        public static (string Name2D, string Name3D) ResolveAssetFolders(
            string profileId, PadForge.Engine.VirtualControllerType slotType)
            => ResolveAssetFolders(profileId, slotType, out _);

        /// <summary>
        /// As <see cref="ResolveAssetFolders(string, PadForge.Engine.VirtualControllerType)"/>,
        /// but also reports whether the profile matched art of its OWN or
        /// merely landed on the graceful fallback.
        ///
        /// <para>The distinction matters because the fallback is a real
        /// controller. An unrecognized profile resolves to an Xbox 360 body
        /// and draws perfectly well, so a caller cannot tell "this is the
        /// device" from "this is the stand-in" by looking at the result.
        /// The Pad page needs to: it draws a body when we have the right
        /// one and a generic schematic when we do not, and a stand-in Xbox
        /// pad under a flight stick is worse than the schematic.</para>
        /// </summary>
        public static (string Name2D, string Name3D) ResolveAssetFolders(
            string profileId, PadForge.Engine.VirtualControllerType slotType,
            out bool dedicated)
        {
            profileId ??= string.Empty;
            dedicated = true;

            // PlayStation
            // Edge first: its profiles start with "dualsense" too, and they
            // must always get the Edge mesh, never a plain DualSense.
            // The Edge has its OWN 2D set: the same pack sprites over a base
            // widened for the four floating tiles that carry its back buttons
            // and Fn pair. A plain DualSense must not render four controls it
            // has no wire for, the same rule that split SWITCH2PRO off from
            // SWITCHPRO.
            if (profileId.StartsWith("dualsense-edge", StringComparison.OrdinalIgnoreCase))
                return ("DUALSENSEEDGE", "DualSenseEdge");
            if (profileId.StartsWith("dualsense", StringComparison.OrdinalIgnoreCase))
                return ("DualSense", "DualSense");
            // The DualShock 3 before the generic DualShock line, which
            // would otherwise give it the DualShock 4's body.
            if (profileId.StartsWith("dualshock-3", StringComparison.OrdinalIgnoreCase))
                return ("DS3", "DS3");
            if (profileId.StartsWith("dualshock", StringComparison.OrdinalIgnoreCase))
                return ("DS4", "DS4");

            // Xbox One / Elite / Series / Adaptive all render the Series
            // 3D mesh now. It is the better model (per-part split, the
            // hado atlases and colorways) and the Xbox One controller is
            // close enough in shape that the old lower-detail mesh has
            // nothing left to offer. Only Series profiles expose Share,
            // which the model gates on. 2D layouts still diverge: these
            // profiles keep the Xbox One asset set, Series its own white
            // one, which is why the two halves of this tuple differ.
            if (profileId.StartsWith("xbox-series-", StringComparison.OrdinalIgnoreCase))
                return ("XBOXSERIES", "XboxSeries");
            if (profileId.StartsWith("xbox-one-", StringComparison.OrdinalIgnoreCase)
                || profileId.StartsWith("xbox-elite-", StringComparison.OrdinalIgnoreCase)
                || profileId.Equals("xbox-adaptive", StringComparison.OrdinalIgnoreCase))
                return ("XBOXONE", "XboxSeries");

            // Xbox 360 family + arcade-stick / dance-pad / wheel siblings.
            if (profileId.StartsWith("xbox-360", StringComparison.OrdinalIgnoreCase))
                return ("XBOX360", "XBOX360");

            // Nintendo Switch Pro family. Both profile generations share
            // the Switch 2 Pro mesh (purchased hado model, split per-part),
            // the same arrangement as Xbox One profiles riding the Series
            // mesh. On an original Switch Pro the S2-only cosmetic parts
            // (C button, GL/GR, four player LEDs) render anyway; they are
            // inert meshes, so nothing maps or flashes wrong.
            // The 3D mesh is shared (the Switch 2 Pro model serves both, the
            // same arrangement as Xbox One profiles riding the Series mesh),
            // but the 2D sets are NOT. Switch 2 Pro art carries a C button and
            // the GL / GR grip tiles; drawing those on an original Pro
            // Controller would show three controls it does not have.
            if (profileId.StartsWith("switch2-pro", StringComparison.OrdinalIgnoreCase))
                return ("SWITCH2PRO", "Switch2Pro");
            if (profileId.StartsWith("switch-pro", StringComparison.OrdinalIgnoreCase))
                return ("SWITCHPRO", "Switch2Pro");

            // Valve. The two controller generations get separate 2D and 3D
            // sets rather than sharing one, for the reason SWITCH2PRO was
            // split off from SWITCHPRO: the 2026 pad has two sticks, two
            // square trackpads, a real D-pad and four grip buttons, so its
            // art drawn for a 2015 pad would show controls that do not
            // exist. The composite profiles share with their plain
            // siblings, the dualsense-composite arrangement.
            if (profileId.StartsWith("steam-controller-2", StringComparison.OrdinalIgnoreCase))
                return ("STEAMCONTROLLER2", "SteamController2");
            if (profileId.StartsWith("steam-controller", StringComparison.OrdinalIgnoreCase))
                return ("STEAMCONTROLLER", "SteamController");
            if (profileId.StartsWith("steam-deck", StringComparison.OrdinalIgnoreCase))
                return ("STEAMDECK", "SteamDeck");

            // Fallback per slot type — preserves existing behavior for
            // Custom / Extended / unrecognized profiles.
            dedicated = false;
            return slotType switch
            {
                PadForge.Engine.VirtualControllerType.PlayStation => ("DS4", "DS4"),
                _ => ("XBOX360", "XBOX360"),
            };
        }

        /// <summary>True when this profile has controller art of its own,
        /// rather than falling back to another device's body.</summary>
        public static bool HasDedicatedArt(
            string profileId, PadForge.Engine.VirtualControllerType slotType)
        {
            ResolveAssetFolders(profileId, slotType, out bool dedicated);
            return dedicated;
        }

        /// <summary>
        /// Build the synthetic "Custom" profile that anchors the Extended
        /// dropdown. Standard Xbox 360-like layout: 2 16-bit sticks, 2
        /// 16-bit triggers, 1 hat switch, 11 buttons. Matches the default
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
