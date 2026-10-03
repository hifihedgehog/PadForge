using System;
using System.Collections.Generic;
using System.IO;
using PadForge.Engine;
using PadForge.Engine.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Consume Mapped Inputs names a key the way the Raw Input reader does
    /// (discussion #486). Numpad Enter reaches Raw Input as VK_RETURN with the
    /// E0 prefix and the low-level hook as VK_RETURN with LLKHF_EXTENDED, and
    /// the reader keeps it at 0x88. The hook looked the raw code up, so a
    /// consumed Numpad Enter typed on and a consumed Enter swallowed Numpad
    /// Enter too.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class KeyboardConsumeIndexTests
    {
        private const int VkReturn = 0x0D;
        private const uint ScanEnter = 0x1C;
        private const uint LlkhfExtended = 0x01;
        private const ushort RiKeyBreak = 0x0001;
        private const ushort RiKeyE0 = 0x0002;

        [Theory]
        [InlineData(0x0D, 0x1C, true, 0x88)]   // Numpad Enter
        [InlineData(0x0D, 0x1C, false, 0x0D)]  // Enter
        [InlineData(0x10, 0x2A, false, 0xA0)]  // Shift reported neutral, left
        [InlineData(0x10, 0x36, false, 0xA1)]  // and right
        [InlineData(0x11, 0x1D, false, 0xA2)]  // Ctrl, left
        [InlineData(0x11, 0x1D, true, 0xA3)]   // and right (E0)
        [InlineData(0x12, 0x38, false, 0xA4)]  // Alt, left
        [InlineData(0x12, 0x38, true, 0xA5)]   // and right (E0)
        [InlineData(0xA1, 0x36, true, 0xA1)]   // the hook's own right Shift
        [InlineData(0x63, 0x51, false, 0x63)]  // Numpad 3
        [InlineData(0x25, 0x4B, true, 0x25)]   // Left arrow
        public void OneTable_NumbersEveryKey(int vk, int scan, bool e0, int expected)
            => Assert.Equal(expected, RawInputListener.KeyIndex(vk, scan, e0));

        private static bool[] Hooked()
        {
            var state = new bool[256];
            InputHookManager.MergeHookedKeyState(state, state.Length);
            return state;
        }

        [Fact]
        public void AConsumedNumpadEnter_IsSwallowed_AndReadAtItsIndex()
        {
            var hooks = new InputHookManager();
            hooks.SetSuppressedKeys(new HashSet<int> { RawInputListener.NumpadEnterKey });
            try
            {
                Assert.True(InputHookManager.ConsumeKey(VkReturn, ScanEnter, LlkhfExtended, isDown: true));
                Assert.True(Hooked()[RawInputListener.NumpadEnterKey]);
                Assert.False(Hooked()[VkReturn]);

                // The main Enter key still types.
                Assert.False(InputHookManager.ConsumeKey(VkReturn, ScanEnter, 0, isDown: true));

                Assert.True(InputHookManager.ConsumeKey(VkReturn, ScanEnter, LlkhfExtended, isDown: false));
                Assert.False(Hooked()[RawInputListener.NumpadEnterKey]);
            }
            finally
            {
                hooks.SetSuppressedKeys(new HashSet<int>());
            }
        }

        [Fact]
        public void AConsumedEnter_LetsNumpadEnterThrough()
        {
            var hooks = new InputHookManager();
            hooks.SetSuppressedKeys(new HashSet<int> { VkReturn });
            try
            {
                Assert.False(InputHookManager.ConsumeKey(VkReturn, ScanEnter, LlkhfExtended, isDown: true));
                Assert.False(Hooked()[VkReturn]);

                Assert.True(InputHookManager.ConsumeKey(VkReturn, ScanEnter, 0, isDown: true));
                Assert.True(Hooked()[VkReturn]);
                Assert.False(Hooked()[RawInputListener.NumpadEnterKey]);
                Assert.True(InputHookManager.ConsumeKey(VkReturn, ScanEnter, 0, isDown: false));
            }
            finally
            {
                hooks.SetSuppressedKeys(new HashSet<int>());
            }
        }

        /// <summary>The same key read both ways lands on the same index: the
        /// reader's record for Numpad Enter and the hook's event for it.
        /// Handle 0 is injected input, which never reaches the iCade
        /// driver.</summary>
        [Fact]
        public void RawInputAndTheHook_NameNumpadEnterAlike()
        {
            var keys = new bool[256];
            RawInputListener.ApplyKeyboardRecord(IntPtr.Zero, (ushort)ScanEnter, RiKeyE0, VkReturn);
            try
            {
                RawInputListener.GetKeyboardState(IntPtr.Zero, keys, keys.Length);
                Assert.True(keys[RawInputListener.NumpadEnterKey]);
                Assert.False(keys[VkReturn]);
            }
            finally
            {
                RawInputListener.ApplyKeyboardRecord(IntPtr.Zero, (ushort)ScanEnter, (ushort)(RiKeyE0 | RiKeyBreak), VkReturn);
            }
            keys = new bool[256];
            RawInputListener.GetKeyboardState(IntPtr.Zero, keys, keys.Length);
            Assert.False(keys[RawInputListener.NumpadEnterKey]);
        }

        private static string RepoText(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, relative)).Replace("\r\n", "\n");
        }

        /// <summary>The hook callback runs only under Windows' hook, so pin
        /// that it decides through the event's scan code and flags.</summary>
        [Fact]
        public void TheHookCallback_DecidesWithTheScanCodeAndFlags()
        {
            string src = RepoText("PadForge.Engine/Common/InputHookManager.cs");
            Assert.Contains("if (ConsumeKey(vk, kb.scanCode, kb.flags, isDown))", src);
            Assert.DoesNotContain("_suppressedVKeys.Contains(vk)", src);
        }
    }
}
