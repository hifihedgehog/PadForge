using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A stick card's Reset All: every row of the card goes back.</summary>
    public class StickConfigItemResetAllTests
    {
        /// <summary>The Mouse Movement card holds Momentum and Momentum Glide,
        /// and Reset All set every other row back but left both, so the
        /// cursor kept coasting after a fling.</summary>
        [Fact]
        public void ResetAll_PutsMomentumAndGlideBack()
        {
            var item = new StickConfigItem(0, "Mouse Movement") { IsMouseStick = true };
            item.MomentumEnabled = true;
            item.MomentumGlide = 1.0;

            item.ResetAllCommand.Execute(null);

            Assert.False(item.MomentumEnabled);
            Assert.Equal(0.90, item.MomentumGlide);
        }
    }
}
