using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// Everything on PadForge's pages, dialogs and menus as an assistant
    /// reaches it (#600): the catalog. It stands on Search's index, the
    /// dialogs' and the window's markup, the tray menu and the live objects
    /// (LiveCatalog), so a setting, button or dialog added later is found,
    /// read, set, pressed and shown with no change here. Each entry is
    /// reached by its reach class. A test stands a recorder in the app's
    /// place. Called on the interface's thread.
    /// </summary>
    internal interface IAssistantCatalog
    {
        /// <summary>The entries whose words match, best first, of one kind
        /// when <paramref name="kind"/> names one, and how many matched
        /// past <paramref name="most"/>.</summary>
        AssistantFound Find(string words, string kind, int most);

        /// <summary>One entry by its ID, with everything under it when it
        /// holds other entries: a page, card, section, dialog, device,
        /// controller or plugin. Changes nothing on screen. Refuses
        /// <c>unknown-setting</c>.</summary>
        AssistantRead Read(string id);

        /// <summary>What setting <paramref name="id"/> to
        /// <paramref name="value"/> needs the user's answer for, or null.
        /// Refuses <c>users-own</c>.</summary>
        AssistantAsk AskFor(string id, JsonElement value);

        /// <summary>Writes one entry through the control a person uses, and
        /// gives it as it reads after. Refuses <c>unknown-setting</c>,
        /// <c>not-available</c> (hidden or turned off), <c>out-of-range</c>
        /// and <c>bad-argument</c>.</summary>
        AssistantPlace Set(string id, JsonElement value);

        /// <summary>Checks a press of an action, a menu item or a dialog's
        /// button and gives the question it needs and the press itself,
        /// which the caller posts to the interface's thread so a dialog it
        /// opens never holds the call.</summary>
        AssistantPressPlan PlanPress(string id);

        /// <summary>What the user sees now.</summary>
        AssistantWhere Where();

        /// <summary>The dialogs open now, PadForge's own and the Windows
        /// dialogs it cannot read.</summary>
        AssistantDialogs Dialogs();

        /// <summary>Points at places in the assistant's words, or takes the
        /// callout away when no step is given. Refuses as
        /// <see cref="CheckShow"/> does, before anything plays.</summary>
        AssistantShown Show(AssistantShowRequest request);

        /// <summary>Checks every step of a show before anything plays or
        /// asks: refuses <c>unknown-setting</c>, <c>users-own</c> for a
        /// place that is the user's alone, and <c>not-available</c> for one
        /// the page does not show now for the controller or device it names.
        /// Gives the last step's place.</summary>
        AssistantPlace CheckShow(AssistantShowRequest request);

        /// <summary>A program called again: its callout's step that waits
        /// for its next call goes on. True when one did.</summary>
        bool NextCall(int owner);

        /// <summary>Every setting that shows now, for the settings
        /// resource.</summary>
        List<AssistantPlace> All();
    }

    /// <summary>One entry of the catalog as a program reads it.</summary>
    internal sealed class AssistantPlace
    {
        [Description("The entry's ID, which does not change with the interface language. read, set, press and show take it.")]
        public string Id { get; set; }

        [Description("What it is: setting, action, card, section, part, extra, page, dialog, menu_item, device, controller, profile, macro, plugin or plugin_setting.")]
        public string Kind { get; set; }

        [Description("Its words as the interface shows them, in the language in use, or the screen reader's name where it shows none.")]
        public string Name { get; set; }

        [Description("Where it is: the page, then the controller, section or card, as the interface names them.")]
        public string Where { get; set; }

        [Description("Its value as the page shows it, with its unit.")]
        public string ValueText { get; set; }

        [Description("Its value: a number, true or false, a choice's text or text. set takes the same.")]
        public JsonElement? Value { get; set; }

        [Description("What set takes for it: number, switch, choice, text, list or section. None for something only pressed or read.")]
        public string Takes { get; set; }

        [Description("The least a number takes.")]
        public double? Minimum { get; set; }

        [Description("The most a number takes.")]
        public double? Maximum { get; set; }

        [Description("The unit of a number, as the page names it.")]
        public string Unit { get; set; }

        [Description("The choices of a choice or the rows of a list, as the page names them.")]
        public List<string> Choices { get; set; }

        [Description("Whether its control takes a change or a press now. A control the page has turned off is refused.")]
        public bool Enabled { get; set; } = true;

        [Description("Whether the page shows it now. One the page hides for the device or controller in use is named but cannot be set.")]
        public bool Showing { get; set; } = true;

        [Description("Whether it sits under More, which the page folds away.")]
        public bool UnderMore { get; set; }

        [Description("What a program may do with it: free (set or press it at Change Settings), asks (the user is asked first) or yours-alone (only the user, and its value is not read).")]
        public string Reach { get; set; }

        [Description("What it does, in the words the user hears on hover and from a screen reader.")]
        public string Help { get; set; }

        [Description("The dialog a press opens, by its ID, when one does.")]
        public string Opens { get; set; }

        [Description("Who made it, for a device: its maker as the device says it.")]
        public OutsideText Maker { get; set; }
    }

    /// <summary>What find gives.</summary>
    internal sealed class AssistantFound
    {
        public List<AssistantPlace> Places { get; set; } = new();

        [Description("How many more matched than were given. Ask with narrower words or a kind, or raise most.")]
        public int LeftOut { get; set; }
    }

    /// <summary>A list a place holds, row by row, as it shows.</summary>
    internal sealed class AssistantListRead
    {
        [Description("The list's name, when it has one.")]
        public string Name { get; set; }

        [Description("Each row's words, in order.")]
        public List<string> Rows { get; set; } = new();

        [Description("The row picked, by its position from 0, when the list picks one.")]
        public int? Selected { get; set; }
    }

    /// <summary>What read gives.</summary>
    internal sealed class AssistantRead
    {
        public AssistantPlace Place { get; set; }

        [Description("The entries under it, with their values: a page's cards, a card's settings and buttons, a dialog's settings.")]
        public List<AssistantPlace> Children { get; set; } = new();

        [Description("Every line of text it shows now, in order: labels, status lines and notes. Empty while it is not drawn.")]
        public List<string> Lines { get; set; } = new();

        [Description("The lists it shows, with their rows.")]
        public List<AssistantListRead> Lists { get; set; } = new();

        [Description("A dialog's buttons as they show now, each with its class. press takes their IDs.")]
        public List<AssistantPlace> Buttons { get; set; } = new();

        [Description("Set when a Windows dialog PadForge cannot read waits for the person, such as a file chooser or a message box.")]
        public string WaitingForPerson { get; set; }

        [Description("PadForge's documentation page for it, when it has one.")]
        public string Docs { get; set; }
    }

    /// <summary>A dialog that is open.</summary>
    internal sealed class AssistantOpenDialog
    {
        [Description("The dialog's ID: read takes it for its text and buttons.")]
        public string Id { get; set; }

        public string Title { get; set; }

        [Description("Whether it holds PadForge's window until it is answered.")]
        public bool Modal { get; set; }

        [Description("What a program may do with its buttons: free, asks or yours-alone.")]
        public string Reach { get; set; }
    }

    /// <summary>The dialogs open now.</summary>
    internal sealed class AssistantDialogs
    {
        public List<AssistantOpenDialog> Open { get; set; } = new();

        [Description("Set when a Windows dialog PadForge cannot read waits for the person, by its kind and title.")]
        public string WaitingForPerson { get; set; }
    }

    /// <summary>What the user sees.</summary>
    internal sealed class AssistantWhere
    {
        [Description("Where PadForge's window is: in_front, behind, minimized or in_tray.")]
        public AssistantWindowPlace Window { get; set; }

        [Description("The page shown, by its ID, such as page/Devices or controller/1.")]
        public string Page { get; set; }

        [Description("The page's name as the interface shows it.")]
        public string PageName { get; set; }

        [Description("The section, extra or group open on the page, by its name.")]
        public string Section { get; set; }

        public List<AssistantOpenDialog> Dialogs { get; set; } = new();

        [Description("Set when a Windows dialog PadForge cannot read waits for the person.")]
        public string WaitingForPerson { get; set; }

        [Description("The virtual controller whose page shows, by its number.")]
        public int? Controller { get; set; }

        [Description("The device picked on the Devices page, by its short ID.")]
        public string Device { get; set; }

        [Description("The profile in force, by its ID.")]
        public string Profile { get; set; }

        [Description("The profile picked on the Profiles page, by its ID.")]
        public string ProfileSelected { get; set; }

        [Description("What your callout says now, when one shows: its step and how it goes on.")]
        public string Callout { get; set; }
    }

    /// <summary>A press, checked: the question it needs first, if any, and
    /// the press itself.</summary>
    internal sealed class AssistantPressPlan
    {
        public AssistantPlace Place { get; init; }
        public AssistantAsk Ask { get; init; }

        /// <summary>The press, posted to the interface's thread.</summary>
        public Action Press { get; init; }

        /// <summary>Run on the interface's thread once the press may go,
        /// before it is posted: refuses it when what the card listed is no
        /// longer what the press would do. Null when nothing can
        /// change.</summary>
        public Action Check { get; init; }
    }

    /// <summary>How a step of a show goes on to the next.</summary>
    internal enum AssistantAdvance
    {
        /// <summary>When the user clicks the ringed place.</summary>
        Click,

        /// <summary>After the step's seconds.</summary>
        Seconds,

        /// <summary>At the program's next call.</summary>
        Next,
    }

    /// <summary>One step of a show.</summary>
    internal sealed class AssistantShowStep
    {
        public string Place { get; init; }
        public string Words { get; init; }
        public AssistantAdvance Advance { get; init; }
        public int Seconds { get; init; }
    }

    /// <summary>A show: its steps, or none to take the callout away.</summary>
    internal sealed class AssistantShowRequest
    {
        /// <summary>The program's name, which heads the callout.</summary>
        public string Program { get; init; }

        public IReadOnlyList<AssistantShowStep> Steps { get; init; } = Array.Empty<AssistantShowStep>();

        /// <summary>Ring each click on the way to the last step's place.</summary>
        public bool Walk { get; init; }

        /// <summary>The calling program's key, whose next call moves a
        /// step that waits for it.</summary>
        public int Owner { get; init; }
    }

    internal sealed class AssistantShown
    {
        [Description("True when PadForge shows the callout now.")]
        public bool Shown { get; set; }

        [Description("Where the first place is, as the interface names it.")]
        public string Where { get; set; }

        [Description("Where PadForge's window is: in_front, behind, minimized or in_tray. PadForge never brings its window up for a program.")]
        public AssistantWindowPlace Window { get; set; }

        [Description("How many steps the show has, a walk's clicks included.")]
        public int Steps { get; set; }

        [Description("True when PadForge's question card asks the user first, since the window is not in front. The steps play once the user picks Show.")]
        public bool Asked { get; set; }

        [Description("What to tell the user, in a sentence.")]
        public string Message { get; set; }
    }
}
