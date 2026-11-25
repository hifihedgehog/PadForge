using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using PadForge.Common;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The assistant tools and the skill on the command line (#600):
    /// <c>--tools</c>, <c>--call</c>, <c>--skill</c> and <c>--describe</c>.
    /// A call takes the path an assistant's call takes, a script's
    /// <c>call</c> line on the Automation pipe, at the level the tool needs,
    /// so it meets the same gates: Automation, the level, the user's answer
    /// to a first connection, and a change that asks. The exit codes are the
    /// command line's: 0 done, 1 PadForge answered an error, 2 a tool, an
    /// argument or a file PadForge does not know, 4 not running, 5
    /// Automation off or its level too low. Runs before WPF loads and uses
    /// none of it.
    /// </summary>
    internal static class AssistantCommandLine
    {
        /// <summary>The name the command line gives itself in its hello,
        /// which the question card and Activity show.</summary>
        internal const string ProgramName = "PadForge's command line";

        /// <summary>How long a call waits for PadForge's answer: a question
        /// card waits two minutes, and a call that asks waits on it.</summary>
        internal const int CallAnswerMs = 4 * 60 * 1000;

        internal const string BatchNote = "In a batch file, run PadForge.exe through start /wait to read its exit code.";

        internal static int Run(CommandLineSwitches.Request r, ClientRun.Seams seams)
        {
            if (r.Skill != null) return PrintSkill(r.Skill, seams);
            if (r.Describe != null) return Describe(r.Describe, seams);
            if (r.Tools) return Tools(r.Json, seams);
            return Call(r.CallTool, r.CallArguments, r.Json, seams);
        }

        // ── --skill ──

        /// <summary>A file of the skill by a name taken with or without its
        /// folder and its .md, without regard to case.</summary>
        internal static AssistantSkill.SkillFile SkillFile(string name)
        {
            string Key(string s) => s.Replace('\\', '/').Trim('/').ToLowerInvariant()
                .Replace("references/", string.Empty).Replace(".md", string.Empty);
            if (string.IsNullOrWhiteSpace(name)) return AssistantSkill.Files[0];
            string want = Key(name);
            return AssistantSkill.Files.FirstOrDefault(f => Key(f.Path) == want);
        }

        private static int PrintSkill(string name, ClientRun.Seams seams)
        {
            var file = SkillFile(name);
            if (file != null)
            {
                seams.Tell(file.Text.Replace("\r\n", "\n").TrimEnd());
                return ClientRun.Done;
            }
            string files = string.Join(", ", AssistantSkill.Files.Select(f => f.Path));
            string folder = name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0
                ? " To write the skill into a folder, run --install skill <folder>." : string.Empty;
            seams.TellError("The skill has no file named " + name + ". It holds " + files + "." + folder);
            return ClientRun.WrongCommandLine;
        }

        // ── --describe ──

        /// <summary>The page description's two files, as the skill carries
        /// them.</summary>
        internal static readonly string[] DescriptionFiles = { "references/pages.md", "references/pages.json" };

        private static int Describe(string folder, ClientRun.Seams seams)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var written = new List<string>();
                foreach (string path in DescriptionFiles)
                {
                    var file = AssistantSkill.Files.FirstOrDefault(f => f.Path == path)
                        ?? throw new InvalidOperationException("this PadForge carries no " + path);
                    string to = Path.Combine(folder, Path.GetFileName(path));
                    seams.WriteFile(to, file.Bytes);
                    written.Add(to);
                }
                seams.Say("Wrote " + string.Join(" and ", written) + ".");
                return ClientRun.Done;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                seams.SayError("The page description was not written to " + folder + ": " + error.Message);
                return ClientRun.AnsweredAnError;
            }
        }

        // ── --tools ──

        private static string LevelName(AutomationLevel level) => level switch
        {
            AutomationLevel.Read => "Read",
            AutomationLevel.SendInputs => "Send Inputs",
            AutomationLevel.ChangeSettings => "Change Settings",
            _ => "Commands",
        };

        /// <summary>The level in force, read from the running copy's state
        /// line, or the exit code that says why it could not be.</summary>
        private static (AutomationLevel? Level, int Code) LevelInForce(ClientRun.Seams seams)
        {
            if (!seams.CopyRuns())
            {
                seams.SayError(ClientRun.NotRunningText);
                return (null, ClientRun.NotRunning);
            }
            string reply = seams.Exchange("state");
            if (reply == ControlPipeClient.NotPadForge)
            {
                seams.SayError(ClientRun.NotPadForgeText);
                return (null, ClientRun.NotRunning);
            }
            if (reply == null && !seams.PipeStands())
            {
                switch (seams.Settle())
                {
                    case RunningCopy.Settled.Gone:
                        seams.SayError(ClientRun.NotRunningText);
                        return (null, ClientRun.NotRunning);
                    case RunningCopy.Settled.Ready:
                        reply = seams.Exchange("state");
                        break;
                }
            }
            if (reply == null || !reply.StartsWith("ok ", StringComparison.Ordinal))
            {
                seams.SayError(reply == null && !seams.PipeStands() ? ClientRun.AutomationOffText : reply ?? ClientRun.NoAnswerText);
                return (null, reply == null && !seams.PipeStands() ? ClientRun.NotAllowed : ClientRun.AnsweredAnError);
            }
            try
            {
                var state = JsonNode.Parse(reply.Substring(3));
                string word = state?["automation"]?["level"]?.GetValue<string>();
                return (AutomationConsent.TryLevel(word, out var level) ? level : AutomationLevel.Commands, ClientRun.Done);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            {
                return (AutomationLevel.Commands, ClientRun.Done);
            }
        }

        private static int Tools(bool json, ClientRun.Seams seams)
        {
            var (level, code) = LevelInForce(seams);
            if (level == null) return code;
            if (json)
            {
                var list = new JsonArray();
                foreach (var tool in AssistantTools.All)
                {
                    var args = new JsonArray();
                    foreach (string a in AssistantJson.ArgumentNames(tool.Arguments)) args.Add(a);
                    list.Add(new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["title"] = tool.Title,
                        ["level"] = AutomationLevels.Word(tool.Level).Replace('-', '_'),
                        ["arguments"] = args,
                        ["allowed"] = tool.Level <= level.Value,
                        ["description"] = tool.Description,
                    });
                }
                seams.Say(new JsonObject { ["level"] = AutomationLevels.Word(level.Value).Replace('-', '_'), ["tools"] = list }.ToJsonString(Compact));
                return ClientRun.Done;
            }
            var text = new StringBuilder();
            text.Append("PadForge's assistant tools, by the level each needs. The level in force is ").Append(LevelName(level.Value)).Append(".\r\n");
            foreach (var group in AssistantTools.All.GroupBy(t => t.Level))
            {
                text.Append("\r\n").Append(LevelName(group.Key));
                if (group.Key > level.Value) text.Append(" (above the level in force)");
                text.Append("\r\n");
                foreach (var tool in group)
                {
                    var args = AssistantJson.ArgumentNames(tool.Arguments);
                    text.Append("  ").Append(tool.Name);
                    if (args.Count > 0) text.Append(" (").Append(string.Join(", ", args)).Append(')');
                    text.Append("\r\n      ").Append(tool.Title).Append("\r\n");
                }
            }
            text.Append("\r\nAn argument with * is needed. Call a tool with --call <tool> and a JSON object of its arguments,\r\n")
                .Append("as in --call find {\"words\":\"deadzone\"}. --json gives the list and the answers as JSON.\r\n")
                .Append(BatchNote);
            seams.Tell(text.ToString());
            return ClientRun.Done;
        }

        // ── --call ──

        private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };
        private static readonly JsonSerializerOptions Indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

        /// <summary>The level a call needs: its tool's, and Read for a
        /// recording, which record_problem makes past its snapshot.</summary>
        internal static AutomationLevel LevelFor(AssistantToolDefinition tool, JsonObject args)
        {
            if (tool.Name == "record_problem")
            {
                var seconds = args?["seconds"];
                bool snapshot = seconds != null && seconds.GetValueKind() == JsonValueKind.Number && seconds.GetValue<double>() == 0;
                return snapshot ? AutomationLevel.Commands : AutomationLevel.Read;
            }
            return tool.Level;
        }

        private static int Call(string name, string arguments, bool json, ClientRun.Seams seams)
        {
            var tool = AssistantTools.All.FirstOrDefault(t => t.Name == name);
            if (tool == null)
            {
                seams.SayError("PadForge has no tool named " + name + ". --tools lists them.");
                return ClientRun.WrongCommandLine;
            }
            JsonObject args;
            try
            {
                args = JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments) as JsonObject;
            }
            catch (JsonException)
            {
                args = null;
            }
            if (args == null)
            {
                seams.SayError("The arguments of --call are one JSON object, as in {\"words\":\"deadzone\"}. " + AssistantJson.Takes(tool.Name, tool.Arguments));
                return ClientRun.WrongCommandLine;
            }
            if (!seams.CopyRuns())
            {
                seams.SayError(ClientRun.NotRunningText);
                return ClientRun.NotRunning;
            }

            var (client, notPadForge) = AutomationPipeClient.Open(seams.AutomationPipeName);
            if (client == null && !notPadForge)
            {
                // A copy that is still starting serves no pipe yet.
                if (seams.Settle() == RunningCopy.Settled.Gone)
                {
                    seams.SayError(ClientRun.NotRunningText);
                    return ClientRun.NotRunning;
                }
                (client, notPadForge) = AutomationPipeClient.Open(seams.AutomationPipeName);
            }
            if (notPadForge)
            {
                seams.SayError("Another program holds the pipe PadForge.Automation. It is not PadForge, so nothing was sent to it.");
                return ClientRun.NotRunning;
            }
            if (client == null)
            {
                seams.SayError(ClientRun.AutomationOffText);
                return ClientRun.NotAllowed;
            }
            using (client)
            {
                var level = LevelFor(tool, args);
                string hello = client.Ask("hello name=" + AutomationLine.Quote(ProgramName) + " kind=script level=" + AutomationLevels.Word(level), 10000);
                if (hello == null)
                {
                    seams.SayError(ClientRun.NoAnswerText);
                    return ClientRun.AnsweredAnError;
                }
                if (!hello.StartsWith("ok ", StringComparison.Ordinal))
                {
                    seams.SayError(hello.StartsWith("err not-allowed", StringComparison.Ordinal) ? ClientRun.AutomationOffText : hello);
                    return hello.StartsWith("err not-allowed", StringComparison.Ordinal) ? ClientRun.NotAllowed : ClientRun.AnsweredAnError;
                }
                if (hello.Contains(" consent=waiting", StringComparison.Ordinal))
                    seams.SayError("PadForge asks the user on its question card whether its command line may work at " + LevelName(level) + ". This waits for the answer.");
                string answer = client.Ask("@1 call " + tool.Name + " " + args.ToJsonString(Compact), CallAnswerMs, "@1 ");
                try { client.Send("bye"); } catch (IOException) { }
                if (answer == null)
                {
                    seams.SayError(ClientRun.NoAnswerText);
                    return ClientRun.AnsweredAnError;
                }
                answer = answer.Substring(3);
                if (answer.StartsWith("ok", StringComparison.Ordinal))
                {
                    string result = answer.Length > 3 ? answer.Substring(3) : "{}";
                    if (!json)
                    {
                        try { result = JsonNode.Parse(result)?.ToJsonString(Indented) ?? result; }
                        catch (JsonException) { }
                    }
                    seams.Say(result);
                    return ClientRun.Done;
                }
                // err <code> <words>
                string rest = answer.StartsWith("err ", StringComparison.Ordinal) ? answer.Substring(4) : answer;
                int space = rest.IndexOf(' ');
                string code = space < 0 ? rest : rest.Substring(0, space);
                string words = space < 0 ? AutomationLine.WordsFor(code) : rest.Substring(space + 1);
                seams.SayError(json ? new JsonObject { ["error"] = code, ["message"] = words }.ToJsonString(Compact) : words + " (" + code + ")");
                return code switch
                {
                    "not-allowed" => ClientRun.NotAllowed,
                    "unknown-tool" or "bad-argument" => ClientRun.WrongCommandLine,
                    _ => ClientRun.AnsweredAnError,
                };
            }
        }
    }

    /// <summary>
    /// The command line's end of <c>PadForge.Automation</c> (#600): opened
    /// for identification only, with the owner check the assistant's copy
    /// makes (PipeOwner), so nothing goes to a pipe another program made.
    /// One line out, the answer's line back.
    /// </summary>
    internal sealed class AutomationPipeClient : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;

        private AutomationPipeClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        }

        /// <summary>The pipe, connected and checked, or null, and whether a
        /// program other than PadForge holds its name.</summary>
        internal static (AutomationPipeClient Client, bool NotPadForge) Open(string pipeName, int connectMs = 1500)
        {
            var pipe = PipeOwner.Open(pipeName);
            if (pipe == null) return (null, false);
            try
            {
                pipe.Connect(connectMs);
            }
            catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
            {
                pipe.Dispose();
                return (null, false);
            }
            if (!PipeOwner.IsServer(pipe.SafePipeHandle, pipeName))
            {
                pipe.Dispose();
                return (null, true);
            }
            return (new AutomationPipeClient(pipe), false);
        }

        internal void Send(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            _pipe.Write(bytes, 0, bytes.Length);
            _pipe.Flush();
        }

        /// <summary>Sends a line and gives the first answer that starts with
        /// <paramref name="tag"/> (any for none), passing over events, or
        /// null when none comes in time.</summary>
        internal string Ask(string line, int ms, string tag = null)
        {
            Send(line);
            using var timeout = new CancellationTokenSource(ms);
            try
            {
                while (true)
                {
                    string answer = _reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                    if (answer == null) return null;
                    if (answer.StartsWith("e ", StringComparison.Ordinal)) continue;
                    if (tag == null || answer.StartsWith(tag, StringComparison.Ordinal)) return answer;
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _pipe.Dispose();
        }
    }
}
