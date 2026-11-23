using System;
using System.Linq;
using System.Text.Json.Nodes;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Imported profiles under HIDMaestro 2.0.0's strict load (#518). A file
    /// HIDMaestro refuses no longer drops out of the pickers without a word:
    /// Manage Profiles shows HIDMaestro's reason and JSON path beside it, a
    /// profile with no HID descriptor says so, and an import HIDMaestro would
    /// refuse is not stored, so no slot is pointed at a profile that is not
    /// there. Each check loads the profiles in a context of its own, as the
    /// import does, and leaves the app's catalog alone.
    /// </summary>
    public class UserProfileProblemTests
    {
        /// <summary>A catalog profile written out by HIDMaestro's own
        /// extractor, as an import from a device is, under an id of its own.
        /// <paramref name="deployable"/> picks one with a captured
        /// descriptor or one without.</summary>
        internal static string ProfileJson(string id, bool deployable = true, Action<JsonObject> edit = null)
        {
            string json;
            using (var ctx = new HMContext())
            {
                ctx.LoadDefaultProfiles();
                var source = ctx.AllProfiles
                    .Where(p => p.IsDeployable == deployable && string.Equals(p.Type, "gamepad", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Id, StringComparer.Ordinal)
                    .First();
                json = HMDeviceExtractor.ToJson(source);
            }
            var root = JsonNode.Parse(json).AsObject();
            if (id != null) root["id"] = id;
            edit?.Invoke(root);
            return root.ToJsonString();
        }

        [Fact]
        public void AProfileHIDMaestroLoadsHasNoProblem()
        {
            Assert.Null(HMaestroProfileCatalog.CheckUserProfile(ProfileJson("padforge-test-pad-user")));
        }

        /// <summary>A key the schema lacks refuses the file, and the problem
        /// carries HIDMaestro's own reason and the path of the key.</summary>
        [Fact]
        public void ARefusedProfileKeepsHIDMaestrosReasonAndPath()
        {
            var problem = HMaestroProfileCatalog.CheckUserProfile(
                ProfileJson("padforge-test-pad-user", edit: r => r["padforgeBogusKey"] = 1));
            Assert.NotNull(problem);
            Assert.NotNull(problem.Refusal);
            Assert.Equal(HMProfileLoadIssueKind.Refused, problem.Refusal.Kind);
            Assert.Contains("padforgeBogusKey", problem.Refusal.JsonPath, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(problem.Refusal.Reason));
        }

        /// <summary>An id the built-in catalog already has refuses the file
        /// since 2.0.0. The import's "-user" suffix keeps a capture clear of
        /// it, and a file imported as written is checked the same way.</summary>
        [Fact]
        public void ACatalogIdIsRefusedAsARepeat()
        {
            var problem = HMaestroProfileCatalog.CheckUserProfile(ProfileJson(id: null));
            Assert.NotNull(problem?.Refusal);
            Assert.Contains("already loaded", problem.Refusal.Reason, StringComparison.Ordinal);
        }

        /// <summary>A profile that loads with no HID descriptor stays out of
        /// the pickers as one HIDMaestro refused does, with a problem of its
        /// own and no refusal.</summary>
        [Fact]
        public void AProfileWithNoDescriptorIsLeftOut()
        {
            var problem = HMaestroProfileCatalog.CheckUserProfile(ProfileJson("padforge-test-nodescriptor-user", deployable: false));
            Assert.NotNull(problem);
            Assert.Null(problem.Refusal);
        }

        /// <summary>Every problem is keyed by the JSON it belongs to, which
        /// is how Manage Profiles finds a row's line. A profile that loads
        /// has none, and loads.</summary>
        [Fact]
        public void EachProblemIsKeyedByItsJson()
        {
            string good = ProfileJson("padforge-test-good-user");
            string refused = ProfileJson("padforge-test-refused-user", edit: r => r["padforgeBogusKey"] = 1);
            string bare = ProfileJson("padforge-test-bare-user", deployable: false);
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            var problems = HMaestroProfileCatalog.LoadUserProfiles(ctx, new[] { good, refused, "  ", bare });
            Assert.Equal(2, problems.Count);
            Assert.NotNull(problems[refused].Refusal);
            Assert.Null(problems[bare].Refusal);
            Assert.False(problems.ContainsKey(good));
            Assert.NotNull(ctx.GetProfile("padforge-test-good-user"));
        }

        /// <summary>Manage Profiles words a refusal around HIDMaestro's
        /// reason and path, and a missing descriptor in a line of its own.</summary>
        [Fact]
        public void ManageProfilesWordsEachProblem()
        {
            Assert.Null(SettingsService.DescribeUserProfileProblem(null));
            Assert.Equal(Strings.Instance.ManageProfiles_ProblemNoDescriptor,
                SettingsService.DescribeUserProfileProblem(new HMaestroProfileCatalog.UserProfileProblem()));

            var refusal = HMaestroProfileCatalog.CheckUserProfile(
                ProfileJson("padforge-test-pad-user", edit: r => r["padforgeBogusKey"] = 1));
            Assert.Equal(string.Format(Strings.Instance.ManageProfiles_ProblemRefused_Format, refusal.Refusal.Reason, refusal.Refusal.JsonPath),
                SettingsService.DescribeUserProfileProblem(refusal));

            Assert.Equal(Strings.Instance.ImportFromDevice_StatusPersistFailed, ManageProfilesDialog.ImportFailedText(null));
            Assert.Equal(string.Format(Strings.Instance.ManageProfiles_StatusImportRefused_Format, "x"), ManageProfilesDialog.ImportFailedText("x"));
        }
    }

    /// <summary>An import HIDMaestro would refuse is not stored: the
    /// import answers no id and the reason, and the store keeps what it
    /// held.</summary>
    [Collection("SettingsManagerStatics")]
    public class UserProfileImportRefusalTests
    {
        [Fact]
        public void ARefusedImportLeavesTheStoreAsItWas()
        {
            var settings = new SettingsService(new MainViewModel());
            string json = UserProfileProblemTests.ProfileJson("padforge-test-import", edit: r => r["padforgeBogusKey"] = 1);

            Assert.Null(settings.AddUserProfile(json, out string problem));
            Assert.StartsWith(Strings.Instance.ManageProfiles_ProblemRefused_Format.Split('{')[0], problem, StringComparison.Ordinal);
            Assert.Empty(settings.GetUserProfileRows());

            Assert.Null(settings.AddUserProfile("{ not json", out string unparsed));
            Assert.Null(unparsed);
        }
    }

    /// <summary>An imported profile is stored under the device's own name.
    /// The "(User Generated)" mark is the interface's, drawn where the
    /// Preset picker names the profile (HMaestroProfileCatalog.DisplayName),
    /// so it follows the language. The import wrote the English mark into
    /// the stored name, and a store or a file from then is read without
    /// it.</summary>
    [Collection("SettingsManagerStatics")]
    public class UserProfileNameTests
    {
        private const string OldMark = " (User Generated)";

        [Fact]
        public void AnImportStoresTheDevicesOwnName()
        {
            var settings = new SettingsService(new MainViewModel());
            string json = UserProfileProblemTests.ProfileJson("padforge-test-name");
            string name = JsonNode.Parse(json)["name"].GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(name));

            Assert.Equal("padforge-test-name" + HMaestroProfileCatalog.UserProfileIdSuffix, settings.AddUserProfile(json, out string problem));
            Assert.Null(problem);
            Assert.Equal(name, Assert.Single(settings.GetUserProfileRows()).Name);

            // A file exported with the old mark in its name comes back without it.
            string marked = UserProfileProblemTests.ProfileJson("padforge-test-name", edit: r => r["name"] = name + OldMark);
            Assert.NotNull(settings.AddUserProfile(marked, out _));
            Assert.Equal(name, Assert.Single(settings.GetUserProfileRows()).Name);
        }

        [Fact]
        public void AStoredOldMarkIsTakenOffOnLoad()
        {
            string marked = UserProfileProblemTests.ProfileJson("padforge-test-name-user", edit: r => r["name"] = "Pad" + OldMark);
            var root = JsonNode.Parse(SettingsService.WithoutOldUserGeneratedName(marked)).AsObject();
            Assert.Equal("Pad", root["name"].GetValue<string>());
            Assert.Equal("padforge-test-name-user", root["id"].GetValue<string>());
            // A name without the mark, and JSON that does not parse, are left as they are.
            string plain = UserProfileProblemTests.ProfileJson("padforge-test-name-user", edit: r => r["name"] = "Pad");
            Assert.Same(plain, SettingsService.WithoutOldUserGeneratedName(plain));
            Assert.Equal("{ not json", SettingsService.WithoutOldUserGeneratedName("{ not json"));
        }
    }
}
