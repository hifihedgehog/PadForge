using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using PadForge.Engine;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The iCade host feed (hifihedgehog/SDL#33 Part 16). RawInputListener
    /// holds the keyboard Raw Input class, so every keyboard record goes to
    /// SDL's iCade driver first, and a record the driver claims belongs to the
    /// iCade joystick. The keys are the iCade's own letters: W presses up and
    /// E releases it, A presses left and Q releases it.
    /// </summary>
    public class ICadeHostFeedTests
    {
        private readonly ITestOutputHelper _output;
        public ICadeHostFeedTests(ITestOutputHelper output) => _output = output;

        private const ushort RiKeyBreak = 0x0001;
        private const ushort ScanW = 0x11, ScanE = 0x12, ScanA = 0x1E;
        private const ushort VkW = 0x57, VkE = 0x45, VkA = 0x41;

        private static bool Held(IntPtr handle, int vk)
        {
            var keys = new bool[256];
            RawInputListener.GetKeyboardState(handle, keys, keys.Length);
            return keys[vk];
        }

        [Fact]
        public void TheFirstClaim_ReleasesWhatTheHandleHeld_AndClaimedRecordsStayOut()
        {
            var handle = new IntPtr(0x1CADE001);
            bool claim = false;
            var saved = RawInputListener.ICadeFeed;
            RawInputListener.ICadeFeed = (h, make, flags) => claim && h == handle;
            try
            {
                // W goes down before the driver has identified the keyboard.
                RawInputListener.ApplyKeyboardRecord(handle, ScanW, 0, VkW);
                Assert.True(Held(handle, VkW));

                // The driver decodes it from the next record on. E, the
                // release of up, is the iCade's, and W is let go with it.
                claim = true;
                RawInputListener.ApplyKeyboardRecord(handle, ScanE, 0, VkE);
                Assert.False(Held(handle, VkW));
                Assert.False(Held(handle, VkE));

                RawInputListener.ApplyKeyboardRecord(handle, ScanA, 0, VkA);
                Assert.False(Held(handle, VkA));

                // A keyboard the driver stops decoding types again.
                claim = false;
                RawInputListener.ApplyKeyboardRecord(handle, ScanA, 0, VkA);
                Assert.True(Held(handle, VkA));

                // Decoded again, it lets go again.
                claim = true;
                RawInputListener.ApplyKeyboardRecord(handle, ScanW, 0, VkW);
                Assert.False(Held(handle, VkA));
                Assert.False(Held(handle, VkW));
            }
            finally
            {
                RawInputListener.ICadeFeed = saved;
            }
        }

        [Fact]
        public void InjectedInput_NeverReachesTheDriver()
        {
            int calls = 0;
            var saved = RawInputListener.ICadeFeed;
            RawInputListener.ICadeFeed = (h, make, flags) => { calls++; return true; };
            try
            {
                RawInputListener.ApplyKeyboardRecord(IntPtr.Zero, ScanA, 0, VkA);
                Assert.True(Held(IntPtr.Zero, VkA));
                RawInputListener.ApplyKeyboardRecord(IntPtr.Zero, ScanA, RiKeyBreak, VkA);
                Assert.False(Held(IntPtr.Zero, VkA));
                Assert.Equal(0, calls);
            }
            finally
            {
                RawInputListener.ICadeFeed = saved;
            }
        }

        [Fact]
        public void AKeyboardTheDriverDecodes_GetsNoKeyboardRow()
        {
            var queries = new List<(IntPtr handle, ushort make, ushort flags)>();
            var saved = RawInputListener.ICadeFeed;
            try
            {
                // Every keyboard on this machine, the aggregate row aside.
                RawInputListener.ICadeFeed = (h, make, flags) => false;
                int keyboards = RawInputListener.EnumerateKeyboards().Length - 1;
                _output.WriteLine($"keyboards on this machine: {keyboards}");

                RawInputListener.ICadeFeed = (h, make, flags) =>
                {
                    lock (queries) queries.Add((h, make, flags));
                    return true;
                };
                var rows = RawInputListener.EnumerateKeyboards();
                Assert.Single(rows);
                Assert.Equal(RawInputListener.AggregateKeyboardHandle, rows[0].Handle);
                Assert.Equal(keyboards, queries.Count);

                // The question moves no control: make code 0 with the break flag.
                Assert.All(queries, q =>
                {
                    Assert.Equal(0, q.make);
                    Assert.Equal(RiKeyBreak, q.flags);
                });

                // Mice are never asked.
                int asked = queries.Count;
                RawInputListener.EnumerateMice();
                Assert.Equal(asked, queries.Count);
            }
            finally
            {
                RawInputListener.ICadeFeed = saved;
            }
        }

        [Fact]
        public void TheShippedSdl_ExportsTheFeed_AndClaimsNoUnknownHandle()
        {
            var import = typeof(SDL3.SDL).GetMethod("_SDL_ICadeProcessRawKeyboard",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(import);
            Assert.Null(Record.Exception(() => Marshal.Prelink(import)));

            // No keyboard has this handle, so the driver claims nothing,
            // whether or not SDL is initialized in this process.
            Assert.False(SDL3.SDL.SDL_ICadeProcessRawKeyboard(new IntPtr(0x1CADE0FF), ScanW, 0));
        }
    }
}
