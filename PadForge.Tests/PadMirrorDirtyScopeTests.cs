using System;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The interface timer's pad mirror writes live display state into each
    /// pad's view model with <c>SuppressSettingsDirty</c> set, so the writes
    /// mark no setting changed. It gives back the value it found, as every
    /// other scope that sets it does (PadViewModel.OnCultureChanged and the
    /// list refreshes): a mirror that ran inside another scope used to clear
    /// it under that scope's feet.
    /// </summary>
    public class PadMirrorDirtyScopeTests
    {
        [Fact]
        public void ThePadMirror_GivesBackTheSuppressValueItFound()
        {
            string source = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs").Replace("\r", "");
            int set = source.IndexOf("padVm.SuppressSettingsDirty = true;", StringComparison.Ordinal);
            Assert.True(set >= 0, "the mirror no longer sets the scope");
            Assert.Equal(set, source.LastIndexOf("padVm.SuppressSettingsDirty = true;", StringComparison.Ordinal));
            Assert.Contains("bool suppress = padVm.SuppressSettingsDirty;\n                padVm.SuppressSettingsDirty = true;", source, StringComparison.Ordinal);
            Assert.Contains("finally { padVm.SuppressSettingsDirty = suppress; }", source, StringComparison.Ordinal);
            Assert.DoesNotContain("padVm.SuppressSettingsDirty = false;", source, StringComparison.Ordinal);
        }
    }
}
