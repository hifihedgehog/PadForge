using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The ATK ACPI device, scripted: every call is logged with its
    /// method and arguments, and answers <see cref="Answer"/>.</summary>
    internal sealed class FakeAtkAcpi : IAtkAcpiChannel
    {
        public readonly List<(uint Method, byte[] Arguments)> Calls = new();
        public int? Answer = 1;
        public bool Disposed;

        public int? Call(uint method, byte[] arguments)
        {
            Calls.Add((method, (byte[])arguments.Clone()));
            return Answer;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>The ASUS TUF laptop family against OpenRGB's
    /// AsusTUFLaptopController (#557).</summary>
    public class AsusTufLaptopLightingTests
    {
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

        /// <summary>DEVS on 0x00100056 with setMode's two numbers for the
        /// Static mode of a color: high = 0xB3 | 0x00 &lt;&lt; 8 | red &lt;&lt; 16 |
        /// green &lt;&lt; 24, low = blue | 0xEB &lt;&lt; 8
        /// (AsusTUFLaptopController_Windows.cpp:294-297), three little-endian
        /// ints (lines 190-199), and the method DEVS, 0x53564544.</summary>
        [Fact]
        public void AColor_IsOpenRgbsStaticModeCall()
        {
            Assert.Equal("56 00 10 00 B3 00 11 22 33 EB 00 00", Hex(AsusTufLaptopLighting.ColorArguments(0x112233)));
            Assert.Equal("56 00 10 00 B3 00 FF 00 80 EB 00 00", Hex(AsusTufLaptopLighting.ColorArguments(0xFF0080)));
            Assert.Equal(0x53564544u, AsusTufLaptopLighting.Devs);
            // G-Helper's six setting bytes are the same, from the magic on
            // (AsusACPI.cs:941-948), save that it stores the setting with
            // 0xB4 where OpenRGB's Direct use does not.
        }

        [Fact]
        public void TheLook_IsOpenRgbsDetector_AndKeepsItsAnswer()
        {
            int reads = 0;
            var asked = new List<string>();
            var family = new AsusTufLaptopLighting(() => { reads++; return "ASUS TUF Gaming F15 FX507ZM_FX507ZM"; },
                id => { asked.Add(id); return true; }, () => new FakeAtkAcpi());
            var device = Assert.Single(family.Look());
            Assert.Equal(@"\\.\ATKACPI", device.Key);
            Assert.Same(family.Model, device.Model);
            Assert.Equal(PeripheralLinker.LocalMachineContainer, device.Container);
            Assert.True(device.BuiltInKeyboard);
            Assert.Equal(new[] { @"ACPI\PNP0C14\ATK" }, asked);
            // Asked once.
            Assert.Same(family.Look(), family.Look());
            Assert.Equal(1, reads);
        }

        [Theory]
        [InlineData("ASUS ROG Strix G15", true)]
        [InlineData("ASUS TUF Gaming F15", false)]
        public void ALaptopThatIsNoTufGaming_OrHasNoAtkInterface_HasNoUnit(string product, bool atk)
        {
            var family = new AsusTufLaptopLighting(() => product, _ => atk, () => new FakeAtkAcpi());
            Assert.Empty(family.Look());
        }

        [Fact]
        public void AProductNameThatCouldNotBeRead_IsAskedAgain()
        {
            string product = null;
            var family = new AsusTufLaptopLighting(() => product, _ => true, () => new FakeAtkAcpi());
            Assert.Null(family.Look());
            product = "TUF Gaming FX505DU";
            Assert.Single(family.Look());
        }

        [Fact]
        public void TheModel_IsOneZone_LitWhileArmouryCrateIsClosed()
        {
            var model = new AsusTufLaptopLighting(() => null, _ => false, () => null).Model;
            var light = Assert.Single(model.Map.Lights);
            Assert.Equal(LightKind.Zone, light.Kind);
            Assert.Equal(new[] { VendorPrograms.ArmouryCrate }, model.Programs);
            Assert.Equal(new[] { "ArmouryCrate.Service", "LightingService" }, VendorPrograms.ArmouryCrate.Processes);
            Assert.NotNull(model.Open);
            Assert.NotNull(model.BeginOwn);
            Assert.Null(model.WriteOnce);
            Assert.Null(model.Begin);
            Assert.Null(model.Owed);
        }

        [Fact]
        public void ASession_SendsTheColor_ChecksTheAnswer_AndHandsBackNothing()
        {
            var atk = new FakeAtkAcpi();
            var family = new AsusTufLaptopLighting(() => null, _ => false, () => atk);
            var model = family.Model;
            var channel = model.Open(@"\\.\ATKACPI");
            Assert.Same(atk, channel);
            var session = model.BeginOwn(channel, _ => { });
            Assert.NotNull(session);
            // Opening asks nothing.
            Assert.Empty(atk.Calls);

            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, model.Map.Lights, new[] { 0x00FF00 }));
            var call = Assert.Single(atk.Calls);
            Assert.Equal(AsusTufLaptopLighting.Devs, call.Method);
            Assert.Equal("56 00 10 00 B3 00 00 FF 00 EB 00 00", Hex(call.Arguments));

            // DEVS answers 1 when it took the color
            // (AsusTUFLaptopController_Windows.cpp:205-208).
            atk.Answer = 0;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(null, _ => { }, model.Map.Lights, new[] { 0x00FF00 }));
            atk.Answer = null;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(null, _ => { }, model.Map.Lights, new[] { 0x00FF00 }));

            int calls = atk.Calls.Count;
            Assert.True(session.HandBack(null, _ => { }));
            Assert.Equal(calls, atk.Calls.Count);
            Assert.Empty(session.CrashFrames);
            // A channel of another kind begins nothing.
            Assert.Null(model.BeginOwn(new FakeSerialLine(), _ => { }));
        }
    }
}
