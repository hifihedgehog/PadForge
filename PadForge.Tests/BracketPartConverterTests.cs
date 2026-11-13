using System.Globalization;
using PadForge.Converters;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The converter that splits a string of the string table around its
    /// first pair of square brackets (#574), so a note on an Extras page draws
    /// its bracketed words as a link.
    /// </summary>
    public class BracketPartConverterTests
    {
        private static string Part(object value, object parameter) =>
            (string)new BracketPartConverter().Convert(value, typeof(string), parameter, CultureInfo.InvariantCulture);

        [Fact]
        public void APairSplitsTheTextInThree()
        {
            Assert.Equal("a ", Part("a [b] c", "0"));
            Assert.Equal("b", Part("a [b] c", "1"));
            Assert.Equal(" c", Part("a [b] c", "2"));
        }

        [Fact]
        public void NoClosedPair_IsAllTheFirstPart()
        {
            Assert.Equal("no link here", Part("no link here", "0"));
            Assert.Equal(string.Empty, Part("no link here", "1"));
            Assert.Equal(string.Empty, Part("no link here", "2"));
            Assert.Equal("open [only", Part("open [only", "0"));
            Assert.Equal(string.Empty, Part("open [only", "1"));
        }

        [Fact]
        public void NothingGivesNothing_AndAParameterThatIsNoNumberReadsAsTheFirstPart()
        {
            Assert.Equal(string.Empty, Part(null, "0"));
            Assert.Equal(string.Empty, Part(null, "1"));
            Assert.Equal("a ", Part("a [b] c", "first"));
            Assert.Equal("a ", Part("a [b] c", null));
        }
    }
}
