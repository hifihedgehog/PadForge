// A PadForge plugin in C#: a complete small one, from the PadForge Plugin Kit.
//
// To build it, make a console program and put this file in place of its
// Program.cs:
//
//     dotnet new console -n MyPlugin
//     (copy this file over MyPlugin\Program.cs)
//     dotnet build MyPlugin -c Release
//
// To add it, open PadForge, go to Extras, Plugins, press Add a Plugin… and
// pick MyPlugin\bin\Release\<framework>\MyPlugin.exe. PadForge shows the file,
// its signer and its SHA-256, and runs it with your ordinary rights whenever
// the engine runs. A rebuilt file is a new file, which PadForge asks about
// again before it runs it.
//
// What it does: it reads the one-time key and the pipe's name PadForge put in
// its environment, connects, declares a controller with two buttons, a stick
// and rumble, moves the stick around a circle and taps a button once a
// second, writes to its log on the Plugins page, and prints the feedback games
// send. Map the controller on PadForge's Devices page like any other.
//
// The wire is UTF-8 text, one line per message. Axes run 0 to 65535 with a
// stick at rest on 32768.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PadForgePluginKit
{
    public static class Program
    {
        public static int Main()
        {
            string? token = Environment.GetEnvironmentVariable("PADFORGE_PLUGIN_TOKEN");
            string pipeName = Environment.GetEnvironmentVariable("PADFORGE_PIPE") ?? "PadForge.Automation";
            if (string.IsNullOrEmpty(token))
            {
                Console.Error.WriteLine("PadForge did not start this program as a plugin. Add it on the Plugins page.");
                return 2;
            }

            // Overlapped, so a read that waits never holds up a write. The
            // server may identify the plugin and no more: it cannot act as
            // the plugin's user.
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            pipe.Connect(5000);
            // The key goes only to a pipe PadForge made.
            if (!OperatingSystem.IsWindows() || !ServedByPadForge(pipe, pipeName))
            {
                Console.Error.WriteLine("The pipe " + pipeName + " is not PadForge's, so the plugin's key was not sent.");
                return 3;
            }
            var utf8 = new UTF8Encoding(false);
            var reader = new StreamReader(pipe, utf8);
            var writer = new StreamWriter(pipe, utf8) { NewLine = "\n", AutoFlush = true };

            // Answers carry the tag their request had. Everything else is an
            // event: e rumble <id> <low> <high>, e led <id> <rrggbb>, and the
            // like.
            var answers = new BlockingCollection<string>();
            var listen = new Thread(() =>
            {
                try
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith('@')) answers.Add(line);
                        else Console.WriteLine("PadForge says: " + line);
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                answers.CompleteAdding();
            }) { IsBackground = true };
            listen.Start();

            int nextTag = 0;
            string Ask(string line)
            {
                string tag = "@" + (++nextTag).ToString(CultureInfo.InvariantCulture);
                lock (writer) writer.WriteLine(tag + " " + line);
                while (answers.TryTake(out string? answer, 10000))
                {
                    if (!answer.StartsWith(tag + " ", StringComparison.Ordinal)) continue;
                    string rest = answer.Substring(tag.Length + 1);
                    if (!rest.StartsWith("ok", StringComparison.Ordinal))
                        throw new InvalidOperationException("PadForge refused " + line.Split(' ')[0] + ": " + rest);
                    return rest;
                }
                throw new IOException("PadForge did not answer.");
            }
            void Send(string line)
            {
                lock (writer) writer.WriteLine(line);
            }

            try
            {
                Console.WriteLine(Ask("hello name=\"Kit Plugin\" kind=plugin token=" + token + " protocol=1"));
                string declared = Ask("declare key=kitpad name=\"Kit Pad\" "
                    + "{\"buttons\":[\"Fire\",\"Jump\"],\"axes\":[{\"name\":\"Stick X\"},{\"name\":\"Stick Y\"}],\"feedback\":[\"rumble\"]}");
                string id = Word(declared, "id");
                Send("log \"Declared Kit Pad as " + id + ".\"");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    double t = clock.Elapsed.TotalSeconds;
                    int x = (int)Math.Round(32768 + 32767 * Math.Cos(t * Math.PI));
                    int y = (int)Math.Round(32768 + 32767 * Math.Sin(t * Math.PI));
                    int fire = t % 1.0 < 0.2 ? 1 : 0;
                    // One line sets every control as one change, and keeps
                    // the connection's lease.
                    Send("s " + id + " a0=" + x + " a1=" + y + " b0=" + fire);
                    Thread.Sleep(20);
                }
            }
            catch (IOException)
            {
                // PadForge closed the connection: the engine stopped or the
                // plugin was switched off.
                return 0;
            }
            catch (InvalidOperationException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }

        /// <summary>
        /// Whether the pipe's owner is Administrators or SYSTEM, as a pipe
        /// PadForge made is: a program without administrator rights cannot
        /// make one so owned. A pipe of another name than PadForge's own,
        /// which only a test gives, may also be owned by this program's own
        /// user.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        internal static bool ServedByPadForge(NamedPipeClientStream pipe, string pipeName)
        {
            if (pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner) return false;
            if (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)) return true;
            using var me = WindowsIdentity.GetCurrent();
            return pipeName != "PadForge.Automation" && owner == me.User;
        }

        /// <summary>The value of <paramref name="key"/>= in an ok line.</summary>
        private static string Word(string line, string key)
        {
            foreach (string part in line.Split(' '))
                if (part.StartsWith(key + "=", StringComparison.Ordinal)) return part.Substring(key.Length + 1);
            throw new InvalidOperationException("PadForge's answer had no " + key + ": " + line);
        }
    }
}
