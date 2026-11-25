// A PadForge plugin in C#: a complete small one, from the PadForge Plugin Kit.
//
// To build it, keep this file, PipeOwner.cs and PadForgePlugin.csproj
// together and run build.ps1 beside them, which needs the .NET SDK 10:
//
//     powershell -ExecutionPolicy Bypass -File build.ps1
//
// It writes bin\PadForgePlugin.exe. With dotnet new console instead, put this
// file over Program.cs and PipeOwner.cs beside it, then dotnet build.
//
// To add it, open PadForge, go to Extras, Plugins, press Add a Plugin… and
// pick bin\PadForgePlugin.exe, or drop it on that page. PadForge shows the
// file, its signer and its SHA-256, and runs it with your ordinary rights
// whenever the engine runs. A rebuilt file shows Approve and Restart on its
// card.
//
// What it does: it reads the one-time key and the pipe's name PadForge put in
// its environment, connects, declares a controller with two buttons, a stick
// and rumble, and four settings PadForge draws on its card: Speed, Tap Fire,
// Center and Turns. It moves the stick around a circle at the Speed set, taps
// Fire once a second while Tap Fire is on, counts its turns in Turns, writes
// to its log on the Plugins page, and prints the feedback games send. Map the
// controller on PadForge's Devices page like any other. README.md lists every
// verb, event and kind of setting.
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
        // What the user set on the plugin's card, written by the thread that
        // reads PadForge's lines and read by the loop that moves the stick.
        private static double _speed = 1;
        private static volatile bool _tapFire = true;
        private static long _centeredAt = -1;

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
            // The key goes only to a pipe PadForge made: PipeOwner.cs holds
            // the rule, the same file PadForge builds.
            if (!OperatingSystem.IsWindows() || !PadForge.Services.Automation.PipeOwner.IsServer(pipe.SafePipeHandle, pipeName))
            {
                Console.Error.WriteLine("Another program holds the pipe " + pipeName + ". It is not PadForge, so nothing was sent to it.");
                return 3;
            }
            var utf8 = new UTF8Encoding(false);
            var reader = new StreamReader(pipe, utf8);
            var writer = new StreamWriter(pipe, utf8) { NewLine = "\n", AutoFlush = true };
            var clock = System.Diagnostics.Stopwatch.StartNew();

            void Send(string line)
            {
                lock (writer) writer.WriteLine(line);
            }

            // Answers carry the tag their request had. Everything else is an
            // event: e setting, e press, e rumble <id> <low> <high> and the
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
                        else if (line.StartsWith("e setting ", StringComparison.Ordinal)) Setting(line, Send);
                        else if (line.StartsWith("e press ", StringComparison.Ordinal) && Word(line, "key") == "center")
                            Interlocked.Exchange(ref _centeredAt, clock.ElapsedMilliseconds);
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
                Send(tag + " " + line);
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

            try
            {
                Console.WriteLine(Ask("hello name=\"Kit Plugin\" kind=plugin token=" + token + " protocol=1"));
                string declared = Ask("declare key=kitpad name=\"Kit Pad\" "
                    + "{\"buttons\":[\"Fire\",\"Jump\"],\"axes\":[{\"name\":\"Stick X\"},{\"name\":\"Stick Y\"}],\"feedback\":[\"rumble\"]}");
                string id = Word(declared, "id");
                Send("log \"Declared Kit Pad as " + id + ".\"");

                // The settings PadForge draws on the plugin's card. Each answer
                // carries the value the user set in an earlier run.
                Setting("e setting key=speed value=" + Word(Ask("setting key=speed kind=number name=Speed min=0.5 max=4 step=0.5 unit=x default=1 "
                    + "help=\"How fast the stick goes round.\""), "value"), Send);
                Setting("e setting key=fire value=" + Word(Ask("setting key=fire kind=switch name=\"Tap Fire\" default=true "
                    + "help=\"Taps Fire once a second.\""), "value"), Send);
                Ask("setting key=center kind=button name=Center help=\"Takes the stick back to the top.\"");
                Ask("setting key=turns kind=status name=Turns");

                double angle = 0, last = 0;
                int turns = 0, shown = -1;
                while (true)
                {
                    double t = clock.Elapsed.TotalSeconds;
                    angle += (t - last) * Math.PI * Volatile.Read(ref _speed);
                    last = t;
                    if (Interlocked.Exchange(ref _centeredAt, -1) >= 0) angle = 0;
                    turns = (int)(angle / (2 * Math.PI));
                    int x = (int)Math.Round(32768 + 32767 * Math.Sin(angle));
                    int y = (int)Math.Round(32768 - 32767 * Math.Cos(angle));
                    int fire = _tapFire && t % 1.0 < 0.2 ? 1 : 0;
                    // One line sets every control as one change, and keeps
                    // the connection's lease.
                    Send("s " + id + " a0=" + x + " a1=" + y + " b0=" + fire);
                    if (turns != shown)
                    {
                        shown = turns;
                        Send("setting key=turns value=" + turns.ToString(CultureInfo.InvariantCulture));
                    }
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

        /// <summary>A setting the user changed, or the value in force that
        /// a declaration's answer gave.</summary>
        private static void Setting(string line, Action<string> send)
        {
            string key = Word(line, "key"), value = Word(line, "value");
            if (key == "speed" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed))
                Volatile.Write(ref _speed, speed);
            else if (key == "fire") _tapFire = value == "true";
            else return;
            send("log \"" + key + " is " + value + ".\"");
        }

        /// <summary>The value of <paramref name="key"/>= in a line.</summary>
        private static string Word(string line, string key)
        {
            foreach (string part in line.Split(' '))
                if (part.StartsWith(key + "=", StringComparison.Ordinal)) return part.Substring(key.Length + 1);
            throw new InvalidOperationException("PadForge's line had no " + key + ": " + line);
        }
    }
}
