using System;
using System.Linq;
using System.Collections.Generic;
using PadForge.Search;
using PadForge.Services.Catalog;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// PadForge's documentation pages, by what they cover (#600). A read
    /// names the page it comes from, and its result carries it as a resource
    /// link, so an assistant can read how a feature works from the source
    /// people read. The pages are the documentation site's (padforge.org,
    /// mkdocs.yml's nav), each at its directory address. The skill carries
    /// each page's text too (<see cref="CopyFolder"/>), and a read links that
    /// copy as well, so an assistant with no web access reads the page from
    /// PadForge. No WPF: the skill's reference list is made from the same
    /// table.
    /// </summary>
    internal static class AssistantDocs
    {
        internal const string Root = "https://padforge.org/docs/";

        /// <summary>Every page an assistant is pointed at, by its path under
        /// the site, with what it covers.</summary>
        internal static readonly IReadOnlyList<(string Path, string Covers)> Pages = new[]
        {
            ("start/first-controller/", "Setting up a first controller"),
            ("features/dashboard/", "Home: the engine, the virtual controllers and the extras that are on"),
            ("features/devices/", "Devices: every device PadForge reads, hiding a device from games, lighting and feedback"),
            ("features/controller-slots/", "Controller slots: making, numbering and removing virtual controllers"),
            ("features/virtual-controllers/", "Virtual controller types and presets"),
            ("features/mappings/", "Button and axis mapping rows"),
            ("features/stick-deadzones/", "Sticks: dead zones, curves and anti-dead zones"),
            ("features/trigger-deadzones/", "Triggers: dead zones and ranges"),
            ("features/force-feedback/", "Force feedback and rumble"),
            ("features/lighting/", "Lighting"),
            ("guides/gyro/", "Motion aiming with a gyro"),
            ("guides/macros/", "Macros"),
            ("guides/shift-layers/", "Shift layers"),
            ("guides/menus/", "Menus"),
            ("guides/profiles/", "Per-game profiles"),
            ("guides/profile-import/", "Importing profiles from other programs"),
            ("guides/web-controller/", "A phone as a controller"),
            ("guides/remote-link/", "Remote Link"),
            ("features/game-telemetry/", "Game telemetry"),
            ("features/game-mods/", "Game mods"),
            ("features/head-tracking/", "Head tracking"),
            ("features/twitch-chat/", "Twitch chat"),
            ("features/screen-overlay/", "The screen overlay"),
            ("features/crosshair/", "The crosshair"),
            ("features/magnifier/", "The magnifier"),
            ("features/touchpad/", "Touchpads"),
            ("features/settings/", "Settings"),
            ("features/driver-management/", "Drivers and HidHide"),
            ("features/automation/", "Automation: the level, consent per program, the question card, Allowed Programs and Activity"),
            ("features/assistants/", "Assistants: adding one, the tools, the guided pointer and assistants on the web"),
            ("features/plugins/", "Plugins: approval, the lifecycle, declared settings, the Test Controller, Replay and the plugin kit"),
            ("features/scripts/", "Scripts in AutoHotkey v2, Python and PowerShell: installs, control names, holds, hotkeys and a script's own controller"),
            ("features/problem-recorder/", "Recording a problem, the review, memory dumps and sending a file privately"),
            ("features/mouse-steering/", "Steering with a mouse, its scroll wheels, a trackball or a spinner"),
            ("features/connection-notes/", "Connection notes, the flyouts, and a device's connection as a value"),
            ("features/controller-database/", "How SDL reads a controller, and copying, pasting, finding and guessing its mapping"),
            ("reference/command-line/", "Every command-line switch, the exit codes and the control pipe"),
            ("troubleshooting/", "When a game does not see a controller, and other problems"),
        };

        private static readonly Dictionary<string, string> ByExtra = new()
        {
            ["WebController"] = "guides/web-controller/",
            ["HeadTracking"] = "features/head-tracking/",
            ["GameTelemetry"] = "features/game-telemetry/",
            ["TwitchChat"] = "features/twitch-chat/",
            ["RemoteLink"] = "guides/remote-link/",
            ["GameMods"] = "features/game-mods/",
            ["ScreenOverlay"] = "features/screen-overlay/",
            ["Crosshair"] = "features/crosshair/",
            ["Magnifier"] = "features/magnifier/",
            ["TouchpadOverlay"] = "features/touchpad/",
            ["Automation"] = "features/automation/",
            ["Plugins"] = "features/plugins/",
        };

        private static readonly string[] BySection =
        {
            "features/mappings/", "features/stick-deadzones/", "features/force-feedback/",
            "guides/gyro/", "features/lighting/", "guides/macros/",
        };

        /// <summary>Whether an address is one of these pages.</summary>
        internal static bool IsPage(string address)
            => address != null && address.StartsWith(Root, StringComparison.Ordinal)
                && Pages.Any(p => address.Length == Root.Length + p.Path.Length && address.EndsWith(p.Path, StringComparison.Ordinal));

        /// <summary>The folder of the skill that carries each page's text
        /// (#600 item 14), made from the documentation site's own Markdown
        /// with every link that leaves the copies pointing at the
        /// site.</summary>
        internal const string CopyFolder = "references/docs/";

        /// <summary>Where the skill carries a page, by its path under the
        /// site: <c>references/docs/features/devices.md</c>.</summary>
        internal static string CopyPathOf(string pagePath) => CopyFolder + pagePath.TrimEnd('/') + ".md";

        /// <summary>The skill's copy of a page, as the URI
        /// <c>resources/read</c> serves it, or null for an address that is
        /// none of these pages or a page the skill does not carry. A read
        /// links it beside the page on the web, for an assistant that does
        /// not read the web.</summary>
        internal static string CopyUri(string address)
        {
            if (!IsPage(address)) return null;
            string path = CopyPathOf(address.Substring(Root.Length));
            return AssistantSkill.Files.Any(f => f.Path == path) ? AssistantSkill.Root + path : null;
        }

        /// <summary>The page that covers a place, as a full address, or
        /// null when none does.</summary>
        internal static string PageFor(SearchEntry entry, LiveEntry live)
        {
            string path = null;
            if (entry != null)
            {
                if (entry.ExtraKey != null && ByExtra.TryGetValue(entry.ExtraKey, out var extra)) path = extra;
                else path = entry.Page switch
                {
                    SearchPage.Pad => entry.Section >= 0 && entry.Section < BySection.Length ? BySection[entry.Section] : "features/mappings/",
                    SearchPage.Devices => "features/devices/",
                    SearchPage.Profiles => "guides/profiles/",
                    SearchPage.Settings => "features/settings/",
                    SearchPage.Home => "features/dashboard/",
                    _ => null,
                };
            }
            else if (live != null)
            {
                path = live.Kind switch
                {
                    LiveKind.Device => "features/devices/",
                    LiveKind.Controller => "features/controller-slots/",
                    LiveKind.Profile => "guides/profiles/",
                    LiveKind.Macro => "guides/macros/",
                    LiveKind.Plugin or LiveKind.PluginDeclaration => "features/plugins/",
                    _ => null,
                };
            }
            return path == null ? null : Root + path;
        }
    }
}
