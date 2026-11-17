using System.Reflection;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The engine's long-running loops are compiled optimized at their first
    /// call.
    ///
    /// <para>A method that never leaves its loop starts as quick code, and
    /// the runtime later swaps in optimized code mid-loop by compiling it on
    /// the thread that runs the loop. For the poll loop that compile landed
    /// about 15 to 20 seconds after launch and held the thread 9 to 14 ms
    /// between two passes, a stall Home showed as a dip in the polling rate
    /// (measured 2026-11-16 from the poll thread's compile events beside each
    /// late pass).</para>
    /// </summary>
    public class PollLoopCompileTests
    {
        [Theory]
        [InlineData("PollingLoop")]
        [InlineData("MouseInjectorLoop")]
        public void TheLoopCompilesOptimizedAtItsFirstCall(string name)
        {
            var method = typeof(InputManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            Assert.True((method.MethodImplementationFlags & MethodImplAttributes.AggressiveOptimization) != 0,
                $"{name} is compiled as quick code first, so its optimized code is compiled mid-loop on its own thread");
        }
    }
}
