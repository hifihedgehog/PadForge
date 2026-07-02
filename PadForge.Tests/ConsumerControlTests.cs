using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Engine-level coverage for the #168 Consumer Control support: the
    /// canonical usage table's integrity (indices ARE the persisted button
    /// numbers, so the table must never reorder), the wrapper's device
    /// objects, and the identity gate.
    /// </summary>
    public class ConsumerControlTests
    {
        [Fact]
        public void FixedTable_HasNoDuplicateUsagesOrNames()
        {
            var usages = new HashSet<ushort>();
            var names = new HashSet<string>();
            foreach (var e in ConsumerUsageTable.Fixed)
            {
                Assert.True(usages.Add(e.Usage), $"duplicate usage 0x{e.Usage:X}");
                Assert.True(names.Add(e.Name), $"duplicate name {e.Name}");
            }
        }

        [Fact]
        public void FixedTable_PinsThePersistedButtonIndices()
        {
            // These indices persist in saved "Button N" mappings. If this test
            // breaks, a table edit reordered or removed rows, which silently
            // retargets every saved consumer mapping. Append-only.
            Assert.Equal(36, ConsumerUsageTable.Fixed.Length);
            Assert.Equal(0x41, ConsumerUsageTable.Fixed[2].Usage);   // OK (the #168 button)
            Assert.Equal(0xCD, ConsumerUsageTable.Fixed[17].Usage);  // Play/Pause
            Assert.Equal(0xCF, ConsumerUsageTable.Fixed[18].Usage);  // Voice Command (#168)
            Assert.Equal(0xE9, ConsumerUsageTable.Fixed[20].Usage);  // Volume Up
            Assert.Equal(0xEA, ConsumerUsageTable.Fixed[21].Usage);  // Volume Down
            Assert.Equal(0x22A, ConsumerUsageTable.Fixed[35].Usage); // Browser Bookmarks
        }

        [Theory]
        [InlineData((ushort)0x41, 2)]
        [InlineData((ushort)0xCF, 18)]
        [InlineData((ushort)0xE9, 20)]
        [InlineData((ushort)0x9999, -1)] // untabled usage: dynamic-slot territory
        public void IndexOf_ResolvesFixedUsages(ushort usage, int expected)
        {
            Assert.Equal(expected, ConsumerUsageTable.IndexOf(usage));
        }

        [Fact]
        public void TotalSlots_CoversFixedPlusDynamicSlack()
        {
            Assert.Equal(ConsumerUsageTable.Fixed.Length + ConsumerUsageTable.DynamicSlack,
                ConsumerUsageTable.TotalSlots);
            Assert.Equal("Consumer 0x0199", ConsumerUsageTable.DynamicName(0x199));
        }

        [Fact]
        public void Wrapper_DeviceObjects_NameTheFixedButtons()
        {
            var wrapper = new ConsumerControlWrapper();
            wrapper.Open(new RawInputListener.DeviceInfo
            {
                Handle = RawInputListener.AggregateConsumerHandle,
                Name = "All Consumer Controls (Merged)",
                DevicePath = "aggregate://consumercontrols",
            });

            var objects = wrapper.GetDeviceObjects();
            Assert.Equal(ConsumerUsageTable.TotalSlots, objects.Length);
            Assert.Equal("OK", objects[2].Name);
            Assert.Equal("Voice Command", objects[18].Name);
            Assert.Equal("Play/Pause", objects[17].Name);
            Assert.Equal(InputDeviceType.ConsumerControl, wrapper.GetInputDeviceType());
            Assert.Equal(0, wrapper.NumAxes);
            Assert.Equal(ConsumerUsageTable.TotalSlots, wrapper.NumButtons);
        }

        [Fact]
        public void Wrapper_AggregateIdentity_IsDeterministic()
        {
            var a = new ConsumerControlWrapper();
            var b = new ConsumerControlWrapper();
            var info = new RawInputListener.DeviceInfo
            {
                Handle = RawInputListener.AggregateConsumerHandle,
                Name = "All Consumer Controls (Merged)",
                DevicePath = "aggregate://consumercontrols",
            };
            a.Open(info);
            b.Open(info);
            // Same path -> same InstanceGuid, so the UserDevice (and its saved
            // mappings) survives restarts, like the keyboard aggregate.
            Assert.Equal(a.InstanceGuid, b.InstanceGuid);
            Assert.NotEqual(System.Guid.Empty, a.InstanceGuid);
        }

        [Fact]
        public void UserDevice_IsConsumerControl_GatesOnCapType()
        {
            var ud = new UserDevice();
            ud.LoadCapabilities(0, ConsumerUsageTable.TotalSlots, 0, InputDeviceType.ConsumerControl);
            Assert.True(ud.IsConsumerControl);
            Assert.False(ud.IsKeyboard);

            var kb = new UserDevice();
            kb.LoadCapabilities(0, 256, 0, InputDeviceType.Keyboard);
            Assert.False(kb.IsConsumerControl);
        }

        [Fact]
        public void AggregateSentinel_DoesNotCollideWithKeyboardOrMouse()
        {
            Assert.NotEqual(RawInputListener.AggregateConsumerHandle, RawInputListener.AggregateKeyboardHandle);
            Assert.NotEqual(RawInputListener.AggregateConsumerHandle, RawInputListener.AggregateMouseHandle);
        }
    }
}
