using System;
using System.IO;
using System.Linq;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The user's profiles are staged in a folder of their own for
    /// HIDMaestro to read, and a folder that cannot be made throws. A
    /// throw there took the built-in catalog down with it, and the import's
    /// check answered it as a profile with no problem, so the import was
    /// stored unchecked.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class UserProfileStagingTests
    {
        private static string FailStaging() =>
            throw new IOException("The protected folder for HIDMaestroProfiles could not be made under C:\\ProgramData\\PadForge\\Work.");

        [Fact]
        public void AStagingFailureKeepsTheBuiltInCatalog()
        {
            string json = UserProfileProblemTests.ProfileJson("padforge-test-staging-user");
            var savedProvider = HMaestroProfileCatalog.UserProfilesProvider;
            HMaestroProfileCatalog.UserProfilesProvider = () => new[] { json };
            HMaestroProfileCatalog.StagingFolderSeam = FailStaging;
            try
            {
                HMaestroProfileCatalog.Reload();
                Assert.NotNull(HMaestroProfileCatalog.GetProfileById(HMaestroProfileCatalog.CustomProfileId));
                Assert.NotNull(HMaestroProfileCatalog.GetProfileById("xbox-360-wired"));
                Assert.True(HMaestroProfileCatalog.ExtendedProfiles.Count > 1);
                // The profile that was never checked says why in Manage Profiles.
                var problem = HMaestroProfileCatalog.GetUserProfileProblem(json);
                Assert.NotNull(problem);
                Assert.Null(problem.Refusal);
                Assert.Contains("HIDMaestroProfiles", problem.CheckFailure, StringComparison.Ordinal);
            }
            finally
            {
                HMaestroProfileCatalog.StagingFolderSeam = null;
                HMaestroProfileCatalog.UserProfilesProvider = savedProvider;
                HMaestroProfileCatalog.Reload();
            }
        }

        [Fact]
        public void AStagingFailureRefusesTheImport()
        {
            string json = UserProfileProblemTests.ProfileJson("padforge-test-staging-user");
            HMaestroProfileCatalog.StagingFolderSeam = FailStaging;
            try
            {
                var problem = HMaestroProfileCatalog.CheckUserProfile(json);
                Assert.NotNull(problem);
                Assert.Null(problem.Refusal);
                Assert.Contains("HIDMaestroProfiles", problem.CheckFailure, StringComparison.Ordinal);
                Assert.Equal(string.Format(Strings.Instance.ManageProfiles_ProblemNotChecked_Format, problem.CheckFailure),
                    SettingsService.DescribeUserProfileProblem(problem));
            }
            finally
            {
                HMaestroProfileCatalog.StagingFolderSeam = null;
            }
        }
    }
}
