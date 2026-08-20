using System.Text.RegularExpressions;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The pnputil oemNN.inf pattern shipped for two months carrying literal
    /// 0x08 BACKSPACE bytes where its word boundaries belong (a scripted
    /// edit wrote the escapes through a non-raw string), so it matched
    /// nothing and legacy-driver detection was silently dead. These pin the
    /// pattern against real pnputil value lines and against that exact
    /// corruption: a backspace-carrying pattern fails the match tests.
    /// </summary>
    public class OemInfPatternTests
    {
        [Theory]
        [InlineData("Published Name:     oem42.inf")]
        [InlineData("Publizierter Name:  oem7.inf")]
        [InlineData("oem123.inf")]
        public void MatchesTheOemInfValue_OnAnyLocaleLine(string line)
        {
            var m = Regex.Match(line, DriverInstaller.OemInfPattern, RegexOptions.IgnoreCase);
            Assert.True(m.Success);
            Assert.StartsWith("oem", m.Value);
            Assert.EndsWith(".inf", m.Value);
        }

        [Theory]
        [InlineData("foem42.inf")]
        [InlineData("oem42.infx")]
        [InlineData("oem.inf")]
        public void RejectsNonValues(string line)
            => Assert.False(Regex.Match(line, DriverInstaller.OemInfPattern, RegexOptions.IgnoreCase).Success);

        /// <summary>The disease itself: the pattern must not contain control
        /// characters. This is what the shipped version would have failed.</summary>
        [Fact]
        public void Pattern_CarriesNoControlCharacters()
        {
            foreach (char c in DriverInstaller.OemInfPattern)
                Assert.False(char.IsControl(c), $"control char 0x{(int)c:X2} in pattern");
        }
    }
}
