using System;
using System.Linq;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    // Pins the 2026-08-17 audit's voice fixes at their pure seams:
    // the Vosk grammar escaping, the "text" key extraction, the registry's
    // pad-expressibility cap, and the microphone row's named-object display
    // gate. Each test reddens when its fix is reverted.
    [Collection("SettingsManagerStatics")]
    public class VoiceAuditRound20260817Tests
    {
        [Fact]
        public void GrammarJson_EscapesBackslashesAndQuotes()
        {
            string g = PadForge.Services.VoskSession.BuildGrammarJson(
                new[] { "hello", "say \"hi\"", "back\\slash" });
            // Well-formed JSON: parses, and round-trips the raw phrase.
            var doc = System.Text.Json.JsonDocument.Parse(g);
            var items = doc.RootElement.EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(new[] { "hello", "say \"hi\"", "back\\slash", "[unk]" }, items);
        }

        [Fact]
        public void ExtractJsonString_TakesTheKeyNotTheValue()
        {
            // The phrase "text" makes the VALUE spell the key. The old
            // LastIndexOf scan hit the value, found no colon after it, and
            // the phrase could never fire.
            Assert.Equal("text",
                PadForge.Services.VoskSession.ExtractJsonString("{\"text\" : \"text\"}", "text"));
            Assert.Equal("hello",
                PadForge.Services.VoskSession.ExtractJsonString(
                    "{\"result\":[{\"conf\":0.98,\"word\":\"hello\"}],\"text\":\"hello\"}", "text"));
            Assert.Null(PadForge.Services.VoskSession.ExtractJsonString("{\"other\":1}", "text"));
        }

        [Fact]
        public void Registry_CapsAtThePadExpressibleRange()
        {
            // Pads carry phrases at buttons 200..255, so the registry's cap
            // is 55. Phrase #56 must be refused, not registered as a phrase
            // that can silently never fire on a pad.
            var save = VoicePhraseRegistry.Phrases;
            try
            {
                foreach (var ph in save) VoicePhraseRegistry.Remove(ph.Phrase);
                for (int i = 1; i <= 55; i++)
                    Assert.NotNull(VoicePhraseRegistry.Register("phrase number " + i, "P" + i));
                Assert.Equal(55, VoicePhraseRegistry.MaxButtonInUse);
                Assert.Null(VoicePhraseRegistry.Register("one phrase too many", "Overflow"));
            }
            finally
            {
                foreach (var ph in VoicePhraseRegistry.Phrases) VoicePhraseRegistry.Remove(ph.Phrase);
                foreach (var ph in save) VoicePhraseRegistry.Register(ph.Phrase, ph.Name);
            }
        }

        [Theory]
        [InlineData(PadForge.Engine.InputDeviceType.Microphone, false)]  // named phrases must show
        [InlineData(PadForge.Engine.InputDeviceType.Nfc, false)]         // #150 precedent
        [InlineData(PadForge.Engine.InputDeviceType.ConsumerControl, false)] // #168 precedent
        [InlineData(PadForge.Engine.InputDeviceType.Joystick, true)]     // raw numbering is correct here
        public void RawNumberedNaming_ExcludesNamedSurfaceDevices(int capType, bool expectRaw)
        {
            var ud = new PadForge.Engine.Data.UserDevice { CapType = capType };
            Assert.Equal(expectRaw, PadForge.Common.MappingDisplayResolver.UseRawNumberedNaming(ud));
        }
    }
}
