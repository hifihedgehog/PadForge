using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The reason each macro step's fields ask with (#600), by what the
    /// step does when its macro fires (InputManager.Step4b.EvaluateMacros.cs):
    /// a step that writes keys, mouse buttons, the cursor, the wheel or the
    /// volume keys to Windows, or changes what the controller's own mapped
    /// input sends (a layer, a profile, the gyro, the pointer mode), sends
    /// input. A step that lights, rumbles, draws on a screen, plays a sound,
    /// sets one program's volume, toggles PadForge's own overlays, marks or
    /// saves a recording or disconnects a device sends none, and its card
    /// gives no reason of that kind. A part shared by several steps keeps
    /// the strictest of theirs. No program reaches a step's fields today
    /// (the catalog leaves the Macros section's editor out), and the reason
    /// holds should one ever do so.
    /// </summary>
    public class MacroStepReachTests
    {
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        private const string None = "None";
        private const string Sends = "SendsInput";
        private const string Runs = "RunsProgram";

        /// <summary>Each step template's class on its root, by its key
        /// after "StepFields". Null for a template with nothing to set.</summary>
        private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
        {
            ["DisconnectController"] = None,
            ["RunProgram"] = Runs,
            ["TextBlock"] = Sends,
            ["SetGyroEngaged"] = Sends,
            ["GyroRecenter"] = null,
            ["ComboBreak"] = null,
            ["CenterMouseSteering"] = null,
            ["MouseRecenter"] = Sends,
            ["MouseFixPosition"] = Sends,
            ["MouseLimitRegion"] = Sends,
            ["MoveMouseToScreenPosition"] = Sends,
            ["MouseNudge"] = Sends,
            ["CycleTapList"] = Sends,
            ["PulseCapable"] = Sends,
            ["RepeatInterval"] = Sends,
            ["RepeatInterval2"] = Sends,
            ["PressureTurboRows"] = Sends,
            ["AnyVcButton"] = Sends,
            ["AnyKey"] = Sends,
            ["Duration"] = Sends,
            ["SetChromaColor"] = None,
            ["SetKeyColors"] = None,
            ["ClearKeyColors"] = None,
            ["ShowDreamcastScreen"] = None,
            ["KeyboardScreenKind"] = None,
            ["ProfileShortcut"] = Sends,
            ["ProblemRecording"] = None,
            ["ScreenOverlayTarget"] = None,
            ["CrosshairTarget"] = None,
            ["ShowKeyboardScreen"] = None,
            ["KeyboardScreenMode"] = None,
            ["Stopwatch"] = None,
            ["PanKeyboardScreen"] = None,
            ["Lightbar"] = None,
            ["LightbarColorClear"] = null,
            ["LightbarModeSet"] = None,
            ["LightbarModeCycle"] = None,
            ["PointerModeCycle"] = Sends,
            ["PointerModeSet"] = Sends,
            ["SwitchLayer"] = Sends,
            ["GuideLedBrightness"] = None,
            ["AnyRumbleSet"] = None,
            ["AnyRumbleStop"] = None,
            ["TriggerEffect"] = None,
            ["TriggerEffectStop"] = None,
            ["PlaySound"] = None,
            ["SoundStop"] = null,
            ["AnyAxisValue"] = Sends,
            ["AxisLatchRelease"] = Sends,
            ["AxisYieldCapable"] = Sends,
            ["AnyWheelTap"] = Sends,
            ["SystemVolume"] = Sends,
            ["AppVolume"] = None,
            ["MouseMove"] = Sends,
            ["AnyMouseButton"] = Sends,
            [""] = Sends,
        };

        private static List<XElement> Templates()
        {
            var doc = XDocument.Parse(File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "Views", "PadPage.xaml")));
            return doc.Descendants().Where(e => e.Name.LocalName == "DataTemplate"
                && ((string)e.Attribute(Xaml + "Key"))?.StartsWith("StepFields", StringComparison.Ordinal) == true).ToList();
        }

        /// <summary>The root's class and reason: Asks with its Why, or None
        /// when it carries no Why.</summary>
        private static string ReasonOf(XElement template)
        {
            var root = template.Elements().First();
            if (ReachMarkup.Attr(root, "Reach.Class") is not "Asks") return null;
            return ReachMarkup.Attr(root, "Reach.Why") ?? None;
        }

        [Fact]
        public void EveryStepTemplate_AsksWithTheReasonOfWhatItsStepDoes()
        {
            var templates = Templates();
            var keys = templates.Select(t => ((string)t.Attribute(Xaml + "Key")).Substring("StepFields".Length)).ToList();
            Assert.Equal(Expected.Keys.OrderBy(k => k, StringComparer.Ordinal), keys.Distinct().OrderBy(k => k, StringComparer.Ordinal));
            var wrong = new List<string>();
            foreach (var template in templates)
            {
                string key = ((string)template.Attribute(Xaml + "Key")).Substring("StepFields".Length);
                string found = ReasonOf(template);
                if (found != Expected[key]) wrong.Add("StepFields" + key + " asks with " + (found ?? "no class") + ", not " + (Expected[key] ?? "no class"));
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        /// <summary>Disconnect Controller drops a device's connection and
        /// sends nothing to Windows: its device picker's card asks with the
        /// plain line.</summary>
        [Fact]
        public void TheDisconnectControllerStepsPicker_AsksWithNoInputReason()
        {
            var template = Templates().Single(t => (string)t.Attribute(Xaml + "Key") == "StepFieldsDisconnectController");
            var picker = template.Descendants().First(e => e.Name.LocalName == "ComboBox");
            ReachReason why = ReachReason.None;
            ReachClass cls = ReachClass.Unset;
            for (var at = picker; at != null && at != template; at = at.Parent)
                if (ReachMarkup.Attr(at, "Reach.Class") is { } c)
                {
                    cls = ReachMarkup.Parse(c);
                    why = Enum.TryParse(ReachMarkup.Attr(at, "Reach.Why"), out ReachReason w) ? w : ReachReason.None;
                    break;
                }
            Assert.Equal(ReachClass.Asks, cls);
            Assert.NotEqual(Strings.Instance.Automation_Ask_SendsInput, AssistantToolWords.Reason(why));
            Assert.Equal(Strings.Instance.Automation_Ask_Always, AssistantToolWords.Reason(why));
        }
    }
}
