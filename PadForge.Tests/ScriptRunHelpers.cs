using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A script run by its interpreter for a test (#600 item 27):
    /// its lines, from its standard output or from a file it writes, and a
    /// process the test ends. <c>PADFORGE_PIPE</c> names the test's pipe, so
    /// a script that opens PadForge's default connection reaches the test's
    /// host and never a PadForge running on this PC.</summary>
    internal sealed class ScriptRun : IDisposable
    {
        private readonly Process _process;
        private readonly BlockingCollection<string> _lines = new();
        private readonly string _file;
        private readonly CancellationTokenSource _stop = new();
        private readonly StringBuilder _seen = new();

        public ScriptRun(string exe, string arguments, string outputFile = null, string pipe = null,
            IReadOnlyDictionary<string, string> environment = null)
        {
            _file = outputFile;
            var start = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = outputFile == null,
                RedirectStandardError = true,
            };
            if (outputFile == null) start.StandardOutputEncoding = Encoding.UTF8;
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            if (pipe != null) start.Environment["PADFORGE_PIPE"] = pipe;
            if (environment != null) foreach (var (k, v) in environment) start.Environment[k] = v;
            _process = Process.Start(start);
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _lines.Add("stderr: " + e.Data); };
            _process.BeginErrorReadLine();
            if (outputFile == null)
            {
                _process.OutputDataReceived += (_, e) => { if (e.Data != null) _lines.Add(e.Data); };
                _process.BeginOutputReadLine();
                return;
            }
            Task.Run(async () =>
            {
                int read = 0;
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        if (File.Exists(_file))
                        {
                            // Shared for writing, so the script's own handle
                            // on the file is never refused while this reads.
                            string text;
                            using (var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                                text = reader.ReadToEnd();
                            int end = text.LastIndexOf('\n');
                            if (end >= read)
                            {
                                foreach (string line in text.Substring(read, end + 1 - read).Split('\n'))
                                    if (line.Length > 0) _lines.Add(line.TrimEnd('\r'));
                                read = end + 1;
                            }
                        }
                    }
                    catch (IOException) { }
                    await Task.Delay(20);
                }
            });
        }

        public bool HasExited => _process.HasExited;

        /// <summary>Every line seen so far.</summary>
        public string Seen => _seen.ToString();

        /// <summary>The next line that starts with
        /// <paramref name="prefix"/>. A line of an error ends the
        /// wait.</summary>
        public string Expect(string prefix, int ms = 30000)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                if (!_lines.TryTake(out string line, 100)) continue;
                _seen.AppendLine(line);
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
                Assert.False(line.StartsWith("error ", StringComparison.Ordinal) || line.StartsWith("stderr: ", StringComparison.Ordinal)
                    && line.Contains("Error", StringComparison.Ordinal), "the script failed: " + line + "\n" + _seen);
            }
            Assert.Fail("no line starting \"" + prefix + "\" in " + ms + " ms. Seen:\n" + _seen);
            return null;
        }

        /// <summary>The lines that wait now, without waiting.</summary>
        public List<string> Drain()
        {
            var list = new List<string>();
            while (_lines.TryTake(out string line))
            {
                _seen.AppendLine(line);
                list.Add(line);
            }
            return list;
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
            catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }

    /// <summary>
    /// The engine's passes for a test, on a thread of their own at a fixed
    /// rate (1,000 a second by default), measured by the clock and not by a
    /// sleep: each pass the steps due land, each device is read from the
    /// state the test gives it, holds and claims go over what mappings read,
    /// and the watch hears it (#600 item 27). A hook runs at the start of
    /// every pass with its number, so a test changes a device between two
    /// passes and counts the passes until a script answers.
    /// </summary>
    internal sealed class PassLoop : IDisposable
    {
        private readonly AutomationPipeRig _rig;
        private readonly UserDevice[] _devices;
        private readonly Thread _thread;
        private volatile bool _stop;
        private readonly long _period;

        /// <summary>The state each device reports, which the hook
        /// changes.</summary>
        public readonly CustomInputState[] Raw;

        /// <summary>Runs on the loop's thread at the start of every pass,
        /// before the devices are read.</summary>
        public Action<long> Hook;

        public long Passes => Interlocked.Read(ref _passes);
        private long _passes;

        public PassLoop(AutomationPipeRig rig, int hz, params UserDevice[] devices)
        {
            _rig = rig;
            _devices = devices;
            _period = Stopwatch.Frequency / hz;
            Raw = new CustomInputState[devices.Length];
            for (int i = 0; i < devices.Length; i++) Raw[i] = (devices[i].ReadInputState ?? new CustomInputState()).Clone();
            _thread = new Thread(Run) { IsBackground = true, Name = "PassLoop", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        private void Run()
        {
            long next = Stopwatch.GetTimestamp();
            while (!_stop)
            {
                while (Stopwatch.GetTimestamp() < next)
                {
                    if (next - Stopwatch.GetTimestamp() > Stopwatch.Frequency / 2000) Thread.Sleep(0);
                    else Thread.SpinWait(20);
                }
                next += _period;
                long pass = Interlocked.Increment(ref _passes);
                Hook?.Invoke(pass);
                ControlHolds.BeginPass();
                for (int i = 0; i < _devices.Length; i++)
                {
                    var ud = _devices[i];
                    var read = Raw[i].Clone();
                    ud.ReadInputState = read;
                    ud.InputState = ControlHolds.Apply(ud.InstanceGuid, read).Clone();
                }
                _rig.Pass(_devices);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(2000);
        }
    }
}
