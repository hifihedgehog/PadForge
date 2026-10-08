using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Razer Chroma worker's decisions (#494): which category shows
    /// which color, and what the session tells Synapse. The worker's loop
    /// only carries these decisions to the REST server.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class ChromaBackendTests : IDisposable
    {
        private static readonly Guid Mouse = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid Keyboard = new("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid ChromaRow = new("00000000-0000-0000-0000-0000000000c3");

        public ChromaBackendTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static void Link(params DeviceLinks[] rows) => PeripheralOutputs.PublishLinks(new LinkTable(rows));

        private static OutputPath Category(string name) => new(OutputFamily.ChromaCategory, name);

        private static List<KeyValuePair<string, int>> Wanted(Func<int, ISet<Guid>> devicesOnSlot = null,
            long? now = null, Func<int, int> slotNumber = null)
            => ChromaBackend.Wanted(PeripheralOutputs.Links, now ?? Environment.TickCount64,
                devicesOnSlot ?? (_ => new HashSet<Guid>()), slotNumber ?? (slot => slot + 1));

        [Fact]
        public void TheColorIntegerIsBgr_AndTheInitListsTheSessionsCategories()
        {
            Assert.Equal(0x0000FF, ChromaBackend.ToBgr(0xFF0000));
            Assert.Equal(0x00FF00, ChromaBackend.ToBgr(0x00FF00));
            Assert.Equal(0xFF0000, ChromaBackend.ToBgr(0x0000FF));
            Assert.Equal(0x563412, ChromaBackend.ToBgr(0x123456));

            using var doc = JsonDocument.Parse(ChromaBackend.InitBody(new[] { "mouse", "keypad" }));
            var root = doc.RootElement;
            Assert.Equal("PadForge", root.GetProperty("title").GetString());
            Assert.Equal("application", root.GetProperty("category").GetString());
            Assert.Equal(new[] { "mouse", "keypad" },
                root.GetProperty("device_supported").EnumerateArray().Select(e => e.GetString()));
        }

        [Fact]
        public void AnEffectIsAcceptedOnlyByItsResult()
        {
            Assert.True(ChromaBackend.TryReadResult("{\"result\":0}", out int ok));
            Assert.Equal(0, ok);
            Assert.True(ChromaBackend.TryReadResult("{\"result\":1167}", out int absent));
            Assert.Equal(ChromaBackend.ResultDeviceNotConnected, absent);
            Assert.False(ChromaBackend.TryReadResult("[]", out _));
            Assert.False(ChromaBackend.TryReadResult("not json", out _));
            Assert.False(ChromaBackend.TryReadResult("{\"result\":\"0\"}", out _));
        }

        /// <summary>Nothing claimed, nothing wanted, so no session holds
        /// Synapse's lighting. Each claimed category carries its own color,
        /// in the canonical order the init lists them.</summary>
        [Fact]
        public void OnlyClaimedCategoriesAreWanted_EachInItsOwnColor()
        {
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } },
                 new DeviceLinks { Device = Keyboard, Lighting = new[] { Category("keyboard") } });
            Assert.Empty(Wanted());

            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.Equal(new[] { new KeyValuePair<string, int>("mouse", 0x112233) }, Wanted());

            PeripheralOutputs.SetLighting(Keyboard, slot: 1, player: 2, 0x00, 0x00, 0xFF);
            Assert.Equal(new[]
            {
                new KeyValuePair<string, int>("keyboard", 0x0000FF),
                new KeyValuePair<string, int>("mouse", 0x112233),
            }, Wanted());

            PeripheralOutputs.ReleaseLighting(Mouse, 0);
            PeripheralOutputs.ReleaseLighting(Keyboard, 1);
            Assert.Empty(Wanted());
        }

        /// <summary>The Razer Chroma row fills every category no Razer device
        /// claims, and a device's own claim keeps its category whatever the
        /// player numbers.</summary>
        [Fact]
        public void TheChromaRowFillsTheRest_AndADeviceKeepsItsOwnCategory()
        {
            Link(new DeviceLinks
            {
                Device = ChromaRow,
                CatchAll = true,
                Lighting = PeripheralLinker.ChromaCategories.Select(Category).ToArray(),
            }, new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });

            PeripheralOutputs.SetLighting(ChromaRow, slot: 0, player: 1, 0xFF, 0x00, 0x00);
            PeripheralOutputs.SetLighting(Mouse, slot: 3, player: 4, 0x00, 0xFF, 0x00);
            var wanted = Wanted().ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(PeripheralLinker.ChromaCategories.Length, wanted.Count);
            Assert.Equal(0x00FF00, wanted["mouse"]);
            Assert.All(wanted.Where(p => p.Key != "mouse"), p => Assert.Equal(0xFF0000, p.Value));
        }

        /// <summary>Set Chroma Color (#468) paints the categories of the Razer
        /// devices assigned to its controller, whether or not their tabs take
        /// control, over any claim. Two controllers' macros on one category:
        /// the smaller displayed number rules, which here runs against the
        /// slot order. A slot with no Razer device of a category leaves that
        /// category alone. The assertions and the reads share one clock, so a
        /// stalled test thread never ages a macro out.</summary>
        [Fact]
        public void SetChromaColor_PaintsTheRazerDevicesOnItsController()
        {
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } },
                 new DeviceLinks { Device = Keyboard, Lighting = new[] { Category("keyboard") } });
            var onSlot = new Dictionary<int, ISet<Guid>>
            {
                [2] = new HashSet<Guid> { Mouse },
                [5] = new HashSet<Guid> { Mouse, Keyboard },
            };
            ISet<Guid> DevicesOn(int slot) => onSlot.TryGetValue(slot, out var set) ? set : new HashSet<Guid>();
            // Slot 5 shows Virtual Controller 1 and slot 2 shows 2.
            int Shown(int slot) => slot switch { 5 => 1, 2 => 2, _ => slot + 10 };

            // No tab claims either device, and no macro is live.
            Assert.Empty(Wanted(DevicesOn, slotNumber: Shown));

            long now = Environment.TickCount64;
            PeripheralOutputs.AssertChromaMacro(2, 0x0A, 0x0B, 0x0C, now);
            var wanted = Wanted(DevicesOn, now, Shown).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(0x0A0B0C, wanted["mouse"]);
            Assert.False(wanted.ContainsKey("keyboard"));

            // Slot 5 has the larger index and the smaller displayed number.
            PeripheralOutputs.AssertChromaMacro(5, 0x01, 0x02, 0x03, now);
            wanted = Wanted(DevicesOn, now, Shown).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(0x010203, wanted["mouse"]);
            Assert.Equal(0x010203, wanted["keyboard"]);

            // A macro outranks a claim on its category.
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x77, 0x77, 0x77);
            Assert.Equal(0x010203, Wanted(DevicesOn, now, Shown).Single(p => p.Key == "keyboard").Value);

            // Past the window the claim shows again.
            long later = now + PeripheralOutputs.MacroAssertWindowMs + 50;
            Assert.Equal(0x777777, Wanted(DevicesOn, later, Shown).Single(p => p.Key == "keyboard").Value);

            // A macro on a slot without a Razer device paints nothing.
            PeripheralOutputs.ResetForTest();
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            PeripheralOutputs.AssertChromaMacro(7, 0x01, 0x02, 0x03, now);
            Assert.Empty(Wanted(DevicesOn, now, Shown));
        }
    }
}
