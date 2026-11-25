using System;
using System.Collections.Generic;
using System.Globalization;

namespace PadForge.Common
{
    /// <summary>
    /// PadForge's command line, read once, the same way by every reader: the
    /// copy that hands switches to the running PadForge, the copy that starts
    /// as the app, the restart with rights and the update's restart.
    ///
    /// <para>One rule: a switch means the same whether it starts PadForge or
    /// reaches a running copy, and the line a running copy takes on
    /// <c>PadForge.Control</c> is the switch without its dashes
    /// (<c>--engine off</c> is <c>engine off</c>). <c>--profile</c> and
    /// <c>--default-profile</c> keep the lines they have had since #366,
    /// <c>activate</c> and <c>deactivate</c>, and the two start options that
    /// place the window, <c>--tray</c> and <c>--minimized</c>, are
    /// <c>hide</c> and <c>minimize</c> to a copy that already runs.</para>
    ///
    /// <para>Pure: no window, no pipe, no file. A switch name is read without
    /// regard to case, and <c>--name=value</c> is <c>--name value</c>.</para>
    /// </summary>
    internal static class CommandLineSwitches
    {
        /// <summary>One switch's work for a running copy.</summary>
        internal readonly struct Step
        {
            public Step(string line, bool canStart, StartPhase phase)
            {
                Line = line;
                CanStart = canStart;
                Phase = phase;
            }

            /// <summary>The line for <c>PadForge.Control</c>.</summary>
            public string Line { get; }

            /// <summary>Whether the switch makes sense as a way to start
            /// PadForge. <c>--exit</c> with nothing running starts nothing.</summary>
            public bool CanStart { get; }

            /// <summary>When a copy that starts with the switch applies it.</summary>
            public StartPhase Phase { get; }
        }

        /// <summary>When a starting copy applies a step.</summary>
        internal enum StartPhase
        {
            /// <summary>Before the engine starts, so a controller turned off
            /// on the command line never appears.</summary>
            BeforeEngine,

            /// <summary>Once the engine is up, or at once when it is not to
            /// start.</summary>
            AfterEngine,

            /// <summary>By the start itself, which reads
            /// <see cref="Request.Engine"/> and <see cref="Request.Window"/>:
            /// whether the engine starts and where the window first is. A
            /// starting copy runs no line for it.</summary>
            AtStart,
        }

        /// <summary>Where a command line puts the window as PadForge
        /// starts.</summary>
        internal enum WindowStart
        {
            /// <summary>The command line says nothing, and the Start
            /// Minimized settings decide.</summary>
            AsSet,
            Shown,
            Minimized,
            Tray,
        }

        /// <summary>What an update's restart does with a switch.</summary>
        internal enum Keep
        {
            /// <summary>How PadForge was started: the restart repeats it.</summary>
            StartOption,

            /// <summary>Asked for once. The restart drops it.</summary>
            Once,

            /// <summary>The caller's own <c>--wait</c>, which no copy that
            /// becomes the app is handed.</summary>
            Wait,
        }

        /// <summary>A command line, parsed.</summary>
        internal sealed class Request
        {
            /// <summary>The steps in the order given.</summary>
            public List<Step> Steps { get; } = new List<Step>();

            /// <summary>What is wrong with the command line, or null. Words for
            /// the person who typed it.</summary>
            public string Error { get; set; }

            public bool Help { get; set; }
            public bool Version { get; set; }
            public bool Mcp { get; set; }
            public bool Wait { get; set; }

            /// <summary><c>--engine on</c> or <c>off</c> when given, for a copy
            /// that starts: it decides whether the engine starts, whatever the
            /// setting says. The last one given counts.</summary>
            public bool? Engine { get; set; }

            /// <summary>The last of <c>--show</c>, <c>--minimized</c> and
            /// <c>--tray</c> given, for a copy that starts.</summary>
            public WindowStart Window { get; set; }

            /// <summary>The command line is the update helper's, which reads
            /// it itself.</summary>
            public bool Internal { get; set; }

            /// <summary><c>--install</c> and what it installs (#600), or
            /// null. A client run of its own, which never starts the
            /// app.</summary>
            public Services.Install.InstallRequest Install { get; set; }

            /// <summary><c>--tools</c> (#600): list the assistant tools with
            /// their levels and arguments.</summary>
            public bool Tools { get; set; }

            /// <summary><c>--call &lt;tool&gt; [json]</c> (#600): the tool's
            /// name, or null.</summary>
            public string CallTool { get; set; }

            /// <summary>The JSON object <c>--call</c> takes, <c>{}</c> when
            /// none was given.</summary>
            public string CallArguments { get; set; }

            /// <summary><c>--skill [name]</c> (#600): the file of the skill to
            /// print, empty for SKILL.md, or null.</summary>
            public string Skill { get; set; }

            /// <summary><c>--describe &lt;folder&gt;</c> (#600): the folder
            /// the page description is written to, or null.</summary>
            public string Describe { get; set; }

            /// <summary><c>--json</c>: machine-readable answers.</summary>
            public bool Json { get; set; }

            /// <summary>Whether the line is an assistant run of its own
            /// (#600): <c>--tools</c>, <c>--call</c>, <c>--skill</c> or
            /// <c>--describe</c>.</summary>
            public bool AssistantRun => Tools || CallTool != null || Skill != null || Describe != null;

            /// <summary><c>--summarize &lt;file&gt;</c> (#600): the recording
            /// whose summary is printed, or null. A client run of its own.</summary>
            public string Summarize { get; set; }

            /// <summary><c>--record save &lt;recording&gt; &lt;file&gt;</c>
            /// (#600): the copy the review's Save… makes as the user, or null.
            /// A client run of its own, never a line for the running copy.</summary>
            public (string Source, string Target)? RecordSave { get; set; }

            /// <summary><c>--plugin-kit &lt;folder&gt;</c> (#600 item 20): the
            /// folder to write the plugin kit into, or null. A client run of
            /// its own, which never starts the app.</summary>
            public string PluginKit { get; set; }

            /// <summary>Whether anything but the update marker was given.</summary>
            public bool Empty => Steps.Count == 0 && !Help && !Version && !Mcp && !Wait && Install == null && !AssistantRun && !Json
                && Summarize == null && RecordSave == null && PluginKit == null;

            /// <summary>Whether a copy that finds none running can start
            /// PadForge with this command line. A switch that only makes sense
            /// against a running copy keeps the whole line from starting one.</summary>
            public bool CanStart
            {
                get
                {
                    foreach (var s in Steps)
                        if (!s.CanStart) return false;
                    return true;
                }
            }

            /// <summary>Each switch read, as the words it took and what an
            /// update's restart does with them.</summary>
            internal List<(int Start, int Count, Keep Keep)> Parts { get; } = new List<(int, int, Keep)>();

            /// <summary>The first word no switch took: where reading stopped
            /// at a mistake, the first word of the update helper's line, or
            /// the number of words when every one was read.</summary>
            internal int ReadTo { get; set; }
        }

        internal const string ProfileSwitch = "--profile";
        internal const string UpdatedSwitch = "--updated";

        /// <summary>Reads a command line. Never throws: what cannot be read
        /// is the request's <see cref="Request.Error"/>.</summary>
        internal static Request Parse(IReadOnlyList<string> args)
        {
            var r = new Request();
            if (args == null || args.Count == 0) return r;

            // The update helper's command line is its own to read, and its
            // first word says so. A give-back task's never reaches this: its
            // run is taken before any other (Program.Main).
            if (string.Equals(args[0], Services.UpdateService.ApplyUpdateSwitch, StringComparison.Ordinal))
            {
                r.Internal = true;
                return r;
            }

            for (int i = 0; i < args.Count; i++)
            {
                int start = i;
                r.ReadTo = start;
                string arg = args[i] ?? string.Empty;
                string name = arg;
                string inline = null;
                int eq = arg.IndexOf('=');
                if (eq > 2 && arg.StartsWith("--", StringComparison.Ordinal))
                {
                    name = arg.Substring(0, eq);
                    inline = arg.Substring(eq + 1);
                }
                name = name.ToLowerInvariant();
                var keep = Keep.Once;

                switch (name)
                {
                    case "--help":
                    case "-h":
                    case "-?":
                    case "/?":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Help = true;
                        break;

                    case "--version":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Version = true;
                        break;

                    case "--mcp":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Mcp = true;
                        break;

                    case "--install":
                    {
                        // The words after it are its own (#600):
                        // what, the program, folder or file, and --remove
                        // or --check.
                        if (inline != null) return Fail(r, "--install takes its words after a space: --install skill claude-code.");
                        if (r.Install != null) return Fail(r, "--install is given once.");
                        string mistake = Services.Install.UserInstall.Parse(args, ref i, out var install);
                        if (mistake != null) return Fail(r, mistake);
                        r.Install = install;
                        break;
                    }

                    case "--tools":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Tools = true;
                        break;

                    case "--json":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Json = true;
                        break;

                    case "--call":
                    {
                        // The tool's name, then an optional JSON object of its
                        // arguments (#600).
                        if (r.CallTool != null) return Fail(r, "--call is given once.");
                        string tool = (inline ?? Next(args, ref i))?.Trim();
                        if (string.IsNullOrEmpty(tool) || LooksLikeSwitch(tool))
                            return Fail(r, "--call needs a tool's name, as in --call status. --tools lists them.");
                        r.CallTool = tool;
                        r.CallArguments = "{}";
                        if (i + 1 < args.Count && (args[i + 1] ?? string.Empty).TrimStart().StartsWith("{", StringComparison.Ordinal))
                            r.CallArguments = args[++i];
                        break;
                    }

                    case "--skill":
                    {
                        // A file of the skill by its name, or SKILL.md (#600).
                        string which = inline;
                        if (which == null && i + 1 < args.Count && !LooksLikeSwitch(args[i + 1])) which = args[++i];
                        r.Skill = (which ?? string.Empty).Trim();
                        break;
                    }

                    case "--describe":
                    {
                        string folder = inline ?? Next(args, ref i);
                        if (string.IsNullOrWhiteSpace(folder) || LooksLikeSwitch(folder))
                            return Fail(r, "--describe needs the folder to write the page description to.");
                        r.Describe = folder.Trim();
                        break;
                    }

                    case "--plugin-kit":
                    {
                        // (#600 item 20) The kit goes where the user says.
                        string folder = inline ?? Next(args, ref i);
                        if (string.IsNullOrWhiteSpace(folder) || (inline == null && LooksLikeSwitch(folder)))
                            return Fail(r, "--plugin-kit needs the folder to write the plugin kit into: --plugin-kit <folder>.");
                        r.PluginKit = folder.Trim();
                        break;
                    }

                    case "--wait":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Wait = true;
                        keep = Keep.Wait;
                        break;

                    case UpdatedSwitch:
                        // The helper's marker on the copy it starts. The
                        // helper puts it there again itself.
                        if (inline != null) return Fail(r, name + " takes no value.");
                        break;

                    case "--minimized":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Window = WindowStart.Minimized;
                        r.Steps.Add(new Step("minimize", true, StartPhase.AtStart));
                        keep = Keep.StartOption;
                        break;

                    case "--tray":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Window = WindowStart.Tray;
                        r.Steps.Add(new Step("hide", true, StartPhase.AtStart));
                        keep = Keep.StartOption;
                        break;

                    case "--show":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Window = WindowStart.Shown;
                        r.Steps.Add(new Step("show", true, StartPhase.AtStart));
                        keep = Keep.StartOption;
                        break;

                    case "--hide":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Steps.Add(new Step("hide", false, StartPhase.AfterEngine));
                        break;

                    case "--exit":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Steps.Add(new Step("exit", false, StartPhase.AfterEngine));
                        break;

                    case "--state":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Steps.Add(new Step("state", false, StartPhase.AfterEngine));
                        break;

                    case "--snapshot":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Steps.Add(new Step("snapshot", false, StartPhase.AfterEngine));
                        break;

                    case "--default-profile":
                        if (inline != null) return Fail(r, name + " takes no value.");
                        r.Steps.Add(new Step("deactivate", true, StartPhase.AfterEngine));
                        keep = Keep.StartOption;
                        break;

                    case ProfileSwitch:
                    {
                        // The next word is the name whatever it says, so a
                        // profile named like a switch stays a profile.
                        string value = OneLine(inline ?? Next(args, ref i));
                        if (value.Length == 0)
                            return Fail(r, "--profile needs a profile's name or ID.");
                        r.Steps.Add(new Step("activate " + value, true, StartPhase.AfterEngine));
                        keep = Keep.StartOption;
                        break;
                    }

                    case "--engine":
                    {
                        if (!TryOnOff(inline ?? Next(args, ref i), out bool on))
                            return Fail(r, "--engine needs on or off.");
                        r.Engine = on;
                        r.Steps.Add(new Step("engine " + OnOff(on), true, StartPhase.AtStart));
                        keep = Keep.StartOption;
                        break;
                    }

                    case "--controllers":
                    {
                        if (!TryOnOff(inline ?? Next(args, ref i), out bool on))
                            return Fail(r, "--controllers needs on or off.");
                        r.Steps.Add(new Step("controllers " + OnOff(on), true, StartPhase.BeforeEngine));
                        keep = Keep.StartOption;
                        break;
                    }

                    case "--slot":
                    {
                        const string needs = "--slot needs a number from 1 to 16, then on or off.";
                        string number = inline ?? Next(args, ref i);
                        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int slot)
                            || slot < 1 || slot > 16)
                            return Fail(r, needs);
                        if (!TryOnOff(Next(args, ref i), out bool on))
                            return Fail(r, needs);
                        r.Steps.Add(new Step("slot " + slot.ToString(CultureInfo.InvariantCulture) + " " + OnOff(on),
                            true, StartPhase.BeforeEngine));
                        keep = Keep.StartOption;
                        break;
                    }

                    case "--summarize":
                    {
                        // A recording's summary, printed (#600). No running
                        // copy is needed.
                        string file = inline ?? Next(args, ref i);
                        if (string.IsNullOrWhiteSpace(file)) return Fail(r, "--summarize needs a recording's file.");
                        if (r.Summarize != null) return Fail(r, "--summarize is given once.");
                        r.Summarize = file.Trim();
                        break;
                    }

                    case "--record":
                    {
                        string what = (inline ?? Next(args, ref i) ?? string.Empty).Trim().ToLowerInvariant();
                        switch (what)
                        {
                            case Recording.RecordingSave.Verb:
                            {
                                // The review's Save… as the user (#600): the
                                // recording, then the file to write.
                                string source = Next(args, ref i), target = Next(args, ref i);
                                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                                    return Fail(r, "--record save needs the recording, then the file to write.");
                                r.RecordSave = (source, target);
                                break;
                            }
                            case "start":
                                r.Steps.Add(new Step("record start", true, StartPhase.AfterEngine));
                                break;
                            case "stop":
                                r.Steps.Add(new Step("record stop", false, StartPhase.AfterEngine));
                                break;
                            case "mark":
                            {
                                // The words after mark, up to the next switch,
                                // are the marker's text.
                                string text = string.Empty;
                                while (i + 1 < args.Count && !LooksLikeSwitch(args[i + 1]))
                                    text = (text + " " + OneLine(args[++i])).Trim();
                                r.Steps.Add(new Step(text.Length == 0 ? "record mark" : "record mark " + text,
                                    false, StartPhase.AfterEngine));
                                break;
                            }
                            default:
                                return Fail(r, "--record needs start, stop or mark.");
                        }
                        break;
                    }

                    default:
                        return Fail(r, LooksLikeSwitch(arg)
                            ? "PadForge does not know the switch " + arg + "."
                            : "PadForge does not know what to do with \"" + arg + "\".");
                }

                r.Parts.Add((start, i - start + 1, keep));
                r.ReadTo = i + 1;
            }
            return r;
        }

        /// <summary>The command line a copy that becomes the app is handed
        /// on: every word but <c>--wait</c>, which is the caller's own. A
        /// profile named <c>--wait</c> stays.</summary>
        internal static List<string> WithoutWait(IReadOnlyList<string> args)
        {
            var kept = new List<string>();
            if (args == null) return kept;
            var request = Parse(args);
            foreach (var part in request.Parts)
            {
                if (part.Keep == Keep.Wait) continue;
                for (int i = part.Start; i < part.Start + part.Count; i++) kept.Add(args[i]);
            }
            // What no switch took is handed on as it is: the update helper's
            // whole line, and the rest of a line that could not be read.
            for (int i = request.ReadTo; i < args.Count; i++)
                kept.Add(args[i]);
            return kept;
        }

        /// <summary>What an update's restart repeats: how PadForge was
        /// started, never what it was told to do once. The restarted copy
        /// would start a second recording (#597).</summary>
        internal static List<string> StartOptionsOnly(IReadOnlyList<string> args)
        {
            var kept = new List<string>();
            if (args == null) return kept;
            foreach (var part in Parse(args).Parts)
            {
                if (part.Keep != Keep.StartOption) continue;
                for (int i = part.Start; i < part.Start + part.Count; i++) kept.Add(args[i]);
            }
            return kept;
        }

        /// <summary>The words <c>--help</c> prints. English and fixed: a
        /// command line is read by scripts as much as by people.</summary>
        internal const string HelpText =
            "PadForge runs as one copy. Started again with switches, it hands them to the\r\n" +
            "running copy and exits with the result.\r\n" +
            "\r\n" +
            "  --profile <name or id>    Switch to a profile\r\n" +
            "  --default-profile         Release it and return to the default profile\r\n" +
            "  --engine on|off           Start or stop the engine\r\n" +
            "  --controllers on|off      Turn every virtual controller on or off\r\n" +
            "  --slot <1-16> on|off      Turn one virtual controller on or off\r\n" +
            "  --show, --hide            Show the window, or send it to the tray\r\n" +
            "  --minimized, --tray       Start minimized, or in the tray\r\n" +
            "  --exit                    Close PadForge\r\n" +
            "  --state                   Print PadForge's state as one line of JSON\r\n" +
            "  --snapshot                Save a diagnostics snapshot\r\n" +
            "  --record start|stop|mark  Record a problem\r\n" +
            "  --summarize <file>        Print a recording's summary\r\n" +
            "  --wait                    Return when PadForge exits, with its exit code\r\n" +
            "  --mcp                     Serve an assistant over standard input and output,\r\n" +
            "                            starting PadForge when a call needs it\r\n" +
            "  --tools                   List the assistant tools with their levels\r\n" +
            "  --call <tool> [json]      Call one, with a JSON object of its arguments\r\n" +
            "  --json                    One line of JSON from --state, --tools and --call\r\n" +
            "  --skill [name]            Print PadForge's skill, or one of its files\r\n" +
            "  --describe <folder>       Write every page's description, Markdown and JSON\r\n" +
            "  --install <what> [where]  Install for the user: skill <program or folder>,\r\n" +
            "                            autohotkey, powershell, python [folder or file]\r\n" +
            "                            or assistant <program>. --remove takes it out,\r\n" +
            "                            --check says current (0), older (6) or none (7)\r\n" +
            "  --plugin-kit <folder>     Write the plugin kit: a guide to every verb and\r\n" +
            "                            setting, and a plugin in C#, Python, PowerShell\r\n" +
            "                            and AutoHotkey\r\n" +
            "  --version, --help         Print and exit\r\n" +
            "\r\n" +
            "Switches run in order. --name=value works as --name value does.\r\n" +
            "Switches that reach a running copy need Automation on in PadForge, under Extras.\r\n" +
            "\r\n" +
            "Exit codes: 0 done, 1 PadForge answered an error, 2 the command line is wrong,\r\n" +
            "4 PadForge is not running, 5 Automation is off or its level is too low.";

        private static Request Fail(Request r, string error)
        {
            r.Error = error;
            return r;
        }

        private static string Next(IReadOnlyList<string> args, ref int i)
            => i + 1 < args.Count ? args[++i] : null;

        private static bool LooksLikeSwitch(string arg)
            => arg != null && (arg.StartsWith("-", StringComparison.Ordinal) || arg == "/?");

        private static bool TryOnOff(string value, out bool on)
        {
            on = false;
            if (value == null) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "on": on = true; return true;
                case "off": return true;
                default: return false;
            }
        }

        private static string OnOff(bool on) => on ? "on" : "off";

        /// <summary>A value that goes onto a line cannot hold a line break.</summary>
        private static string OneLine(string text)
            => (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
