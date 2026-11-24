using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// Every setting on PadForge's pages, as an assistant reaches it (#597):
    /// the settings catalog. It is built on Search's own index, so a setting
    /// added to a page is found, read, set and shown with no change here. A
    /// test stands a recorder in the app's place. Called on the interface's
    /// thread.
    /// </summary>
    internal interface IAssistantSettings
    {
        /// <summary>The settings whose words match, best first.</summary>
        List<AssistantSetting> Find(string words, int most);

        /// <summary>One setting by its ID. Refuses
        /// <c>unknown-setting</c>.</summary>
        AssistantSetting Get(string id);

        /// <summary>What a change of one setting to <paramref name="value"/>
        /// would need the user's answer for, or null when nothing. Refuses
        /// <c>users-own</c> for a setting only the user may change.</summary>
        AssistantAsk AskFor(string id, JsonElement value);

        /// <summary>Writes one setting through the binding its control on
        /// the page uses, and gives it as it reads after. Refuses
        /// <c>unknown-setting</c>, <c>out-of-range</c> and
        /// <c>bad-argument</c>.</summary>
        AssistantSetting Set(string id, JsonElement value);

        /// <summary>Opens the page on a setting, a device, a controller or a
        /// profile, unfolds More, scrolls to it and rings it. Never raises
        /// the window.</summary>
        AssistantShown Show(AssistantShowTarget target);

        /// <summary>Every setting's ID, for the catalog resource.</summary>
        List<AssistantSetting> All();
    }

    internal enum AssistantSettingKind { Number, Switch, Choice }

    internal sealed class AssistantSetting
    {
        [Description("The setting's ID, which does not change with the interface language. get_setting, set_setting and show take it.")]
        public string Id { get; set; }

        [Description("The setting's name as the interface shows it, in the language in use.")]
        public string Name { get; set; }

        [Description("Where it is: the page, then the controller, section or card, as the interface names them.")]
        public string Where { get; set; }

        [Description("Its value as the page shows it, with its unit.")]
        public string Shown { get; set; }

        [Description("Its value: a number, true or false, or the text of the picked choice. set_setting takes the same kind.")]
        public JsonElement? Value { get; set; }

        [Description("number, switch or choice. None for a setting PadForge has no value of for a program, such as a button, a gallery or a folder's path: show takes the user to it.")]
        public AssistantSettingKind? Kind { get; set; }

        [Description("The least a number takes.")]
        public double? Minimum { get; set; }

        [Description("The most a number takes.")]
        public double? Maximum { get; set; }

        [Description("The unit of a number, as the page names it.")]
        public string Unit { get; set; }

        [Description("The choices of a choice, as the page names them.")]
        public List<string> Choices { get; set; }

        [Description("Whether it sits under More, which the page folds away.")]
        public bool UnderMore { get; set; }

        [Description("Whether the page shows it now. A setting the page hides for the device or controller in use cannot be set.")]
        public bool Available { get; set; } = true;
    }

    internal enum AssistantShowKind { Setting, Device, Controller, Profile }

    internal sealed class AssistantShowTarget
    {
        public AssistantShowKind Kind { get; init; }
        public string Id { get; init; }
    }

    internal sealed class AssistantShown
    {
        [Description("True when the page now shows the place.")]
        public bool Shown { get; set; }

        [Description("Where the place is, as the interface names it.")]
        public string Where { get; set; }

        [Description("Where PadForge's window is: in_front, behind, minimized or in_tray. PadForge never brings its window up for a program, so tell the user where to look.")]
        public AssistantWindowPlace Window { get; set; }

        [Description("What to tell the user, in a sentence.")]
        public string Message { get; set; }
    }
}
