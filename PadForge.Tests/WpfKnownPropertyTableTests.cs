namespace PadForge.Tests
{
    public class WpfKnownPropertyTableTests
    {
        /// <summary>The module initializer built WPF's whole table of known
        /// XAML properties, 269 entries in .NET 10's WPF, before the tests
        /// ran. Without it two tests can deadlock inside WPF (see
        /// WpfKnownPropertyTable).</summary>
        [Fact]
        public void TheTableWasBuiltBeforeAnyTestRan()
        {
            Assert.Null(WpfKnownPropertyTable.Failure);
            Assert.True(WpfKnownPropertyTable.Built >= 269, "built " + WpfKnownPropertyTable.Built + " entries");
        }
    }
}
