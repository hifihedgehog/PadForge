using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What the request card says a macro would do outside PadForge, the
    /// lines a program's words are cut to, the skill's resources, the
    /// stand-ins a macro's read refuses, and a device's maker read once it
    /// opened.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AssistantScanAndWordsTests
    {
        private static MacroData MacroOf(ActionData action) => new MacroData { Actions = new[] { action } };

        /// <summary>A Cycle Tap List step taps keys and clicks or scrolls the
        /// mouse (ExecuteCycleTapList), and the card says so.</summary>
        [Fact]
        public void ACycleTapListStep_NamesItsKeys_AndItsMouseInput()
        {
            var keys = AssistantScan.Macro(MacroOf(new ActionData { Type = MacroActionType.CycleTapList, CycleStepsCsv = "K:65,K:66+B:4096" }));
            Assert.Equal(new[] { AssistantToolWords.PressesKey("0x41"), AssistantToolWords.PressesKey("0x42") }, keys);
            foreach (string mouse in new[] { "M:0", "W:1", "H:-1" })
                Assert.Equal(new[] { AssistantToolWords.MovesMouse() },
                    AssistantScan.Macro(MacroOf(new ActionData { Type = MacroActionType.CycleTapList, CycleStepsCsv = mouse })));
            // Controller buttons and axes stay inside PadForge.
            Assert.Empty(AssistantScan.Macro(MacroOf(new ActionData { Type = MacroActionType.CycleTapList, CycleStepsCsv = "B:4096,A:2:32767" })));
        }

        /// <summary>A Play Sound step opens the file it names
        /// (SoundMacroService.Play), and the card names the file.</summary>
        [Fact]
        public void APlaySoundStep_NamesItsFile()
        {
            string file = @"\\server\share\clip.wav";
            Assert.Equal(new[] { AssistantToolWords.FileLine(file) },
                AssistantScan.Macro(MacroOf(new ActionData { Type = MacroActionType.PlaySound, SoundFilePath = file })));
            Assert.Empty(AssistantScan.Macro(MacroOf(new ActionData { Type = MacroActionType.PlaySound, SoundFilePath = " " })));
        }

        /// <summary>A program's words are cut at a whole character: an emoji
        /// across the cut goes whole, never half of its pair.</summary>
        [Fact]
        public void ACut_NeverSplitsAPair()
        {
            string letters = new string('a', 59);
            Assert.Equal(letters + "…", AssistantToolWords.Cut(letters + "\U0001F600" + "tail"));
            Assert.Equal(new string('a', 60) + "…", AssistantToolWords.Cut(new string('a', 61)));
        }

        /// <summary>Activity cuts a program's own text the same way.</summary>
        [Fact]
        public void ActivitysCut_NeverSplitsAPair()
        {
            string letters = new string('a', AutomationAsk.LongestArgument - 1);
            string text = new AutomationAsk(AutomationAskKind.ActivateProfile, letters + "\U0001F600" + "tail").Text;
            Assert.Contains(letters + "…", text);
            Assert.DoesNotContain(text, c => char.IsSurrogate(c));
        }

        /// <summary>The skill's one JSON file is announced as JSON, the rest
        /// as Markdown.</summary>
        [Fact]
        public void TheSkillsJsonFile_HasTheJsonType()
        {
            var resources = AssistantSkill.Resources().Select(r => r.ProtocolResource).ToList();
            var pages = Assert.Single(resources, r => r.Name.EndsWith("references/pages.json", StringComparison.Ordinal));
            Assert.Equal("application/json", pages.MimeType);
            Assert.All(resources.Where(r => r.Name.EndsWith(".md", StringComparison.Ordinal)), r => Assert.Equal("text/markdown", r.MimeType));
        }

        /// <summary>A macro's text that only looks like a tag is the user's
        /// own, and only the scrubber's own stand-ins are refused.</summary>
        [Fact]
        public void AMacrosRead_RefusesTheScrubbersStandIns_AndNoOtherTag()
        {
            Assert.False(MainWindow.LeftOutForPrivacy("<enter>"));
            Assert.False(MainWindow.LeftOutForPrivacy("Press <b> to jump"));
            Assert.True(MainWindow.LeftOutForPrivacy("<line left out>"));
            Assert.True(MainWindow.LeftOutForPrivacy("<serial of D1>"));
            Assert.True(MainWindow.LeftOutForPrivacy("<text of 12 characters>"));
            Assert.True(MainWindow.LeftOutForPrivacy(@"<folder>\clip.wav"));
            Assert.True(MainWindow.LeftOutForPrivacy("<user>"));
        }

        /// <summary>A device whose open failed names no maker for now, and
        /// is read again once it opens. A device that is not there is not
        /// opened.</summary>
        [Fact]
        public void AMakerIsKept_OnlyWhenTheDeviceOpened()
        {
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), ProductName = "Maker Pad", IsOnline = true,
                DevicePath = @"\\?\hid#vid_0f0d&pid_00c3#a1test0003#{4d1e55b2-f16f-11cf-88cb-001111000030}" };
            int opens = 0;
            try
            {
                AssistantDeviceMaker.OpenForTest = _ => { opens++; return (false, null); };
                Assert.Null(AssistantDeviceMaker.Of(ud));
                AssistantDeviceMaker.OpenForTest = _ => { opens++; return (true, "HORI CO.,LTD."); };
                Assert.Equal("HORI CO.,LTD.", AssistantDeviceMaker.Of(ud));
                Assert.Equal(2, opens);
                // Kept: no third open.
                Assert.Equal("HORI CO.,LTD.", AssistantDeviceMaker.Of(ud));
                Assert.Equal(2, opens);

                var away = new UserDevice { InstanceGuid = Guid.NewGuid(), ProductName = "Away Pad", IsOnline = false,
                    DevicePath = @"\\?\hid#vid_0f0d&pid_00c4#a1test0004#{4d1e55b2-f16f-11cf-88cb-001111000030}" };
                Assert.Null(AssistantDeviceMaker.Of(away));
                Assert.Equal(2, opens);
                Assert.False(AssistantDeviceMaker.Known.ContainsKey(away.DevicePath));
            }
            finally
            {
                AssistantDeviceMaker.OpenForTest = null;
                AssistantDeviceMaker.Known.TryRemove(ud.DevicePath, out _);
            }
        }
    }
}
