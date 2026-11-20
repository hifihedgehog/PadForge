using System;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The two macros of a Workshop hold pair and of a cursor region pair
    /// are named from a format string in the interface's language. The name
    /// took the language's own base name and an English "(hold)",
    /// "(release)" or "(engage)" after it.
    /// </summary>
    public class WorkshopMacroNameTests
    {
        [Fact]
        public void APairsNames_HaveNoEnglishSuffixInTheCode()
        {
            string source = ExtrasPageTests.RepoText("PadForge.App", "Services", "WorkshopProfileMaterializer.cs");
            Assert.DoesNotContain("\"(hold)\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("\"(release)\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("\"(engage)\"", source, StringComparison.Ordinal);
        }
    }
}
