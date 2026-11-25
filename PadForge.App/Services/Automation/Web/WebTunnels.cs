using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services.Automation.Web
{
    /// <summary>A tunnel program PadForge knows how to start (#600 item 11).</summary>
    /// <param name="Key">Its word in PadForge.xml and on the wire.</param>
    /// <param name="Name">Its name as the page shows it.</param>
    /// <param name="Exe">Its file name.</param>
    /// <param name="KeepsAddress">Whether its address is the same each time
    /// it starts.</param>
    internal sealed record WebTunnelKind(string Key, string Name, string Exe, bool KeepsAddress)
    {
        /// <summary>Its command line for a loopback port.</summary>
        internal Func<int, string> Arguments { get; init; }

        /// <summary>Reads its public address from one line it wrote, or
        /// null.</summary>
        internal Func<string, string> AddressIn { get; init; }

        /// <summary>The folders it installs to beside the PATH.</summary>
        internal Func<IEnumerable<string>> Folders { get; init; }
    }

    /// <summary>A tunnel program found on this PC.</summary>
    internal sealed record WebTunnelFound(string Key, string Path);

    /// <summary>
    /// The tunnel programs the web helper can start (#600 item 11), the ones
    /// a user already has: cloudflared, devtunnel, ngrok and tailscale.
    /// PadForge downloads none and keeps no key of theirs: each one runs on
    /// its own sign-in. The helper starts the one the user picked, with the
    /// user's rights (it runs without PadForge's), as its own child in the job
    /// the plugin launcher gave it, so the tunnel ends with the helper and
    /// with PadForge, and reads the public address from what the program
    /// writes.
    ///
    /// <para>The command lines and the lines the address is read from:
    /// cloudflared, a quick tunnel, <c>cloudflared tunnel --url
    /// http://127.0.0.1:PORT</c> (Cloudflare's TryCloudflare page), which
    /// prints "Your quick Tunnel has been created! Visit it at ..." and the
    /// address on the next line (cloudflared
    /// cmd/cloudflared/tunnel/quick_tunnel.go quickTunnelStartupLines); its
    /// hostname changes each time ("The hostname changes each time you create
    /// a Quick Tunnel"). ngrok, <c>ngrok http 127.0.0.1:PORT --log stdout
    /// --log-format logfmt</c>, whose "started tunnel" line carries
    /// <c>url=</c>; every account's domain stays ("Every account gets a
    /// stable domain, including free accounts", ngrok's llms.txt). devtunnel,
    /// <c>devtunnel host -p PORT --allow-anonymous</c> (Microsoft's dev tunnels
    /// CLI reference), which prints "Connect via browser:" and the address,
    /// a temporary tunnel "deleted once the connection is closed". Anonymous
    /// access lets the assistant's cloud in, and PadForge's own sign-in
    /// guards the door. tailscale, <c>tailscale funnel PORT</c> in the
    /// foreground, which prints "Available on the internet:" and the
    /// address (tailscale cmd/tailscale/cli/serve_v2.go msgFunnelAvailable,
    /// messageForPort); the name is the machine's in its tailnet and stays
    /// ("Funnel can only use DNS names in your tailnet's domain").</para>
    /// </summary>
    internal static class WebTunnels
    {
        /// <summary>Set by the test assembly: no tunnel program is started
        /// without a test's own <see cref="StartSeam"/>.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Starts a tunnel program in a test.</summary>
        internal static Func<ProcessStartInfo, Process> StartSeam;

        /// <summary>Lists the files in a folder, in a test.</summary>
        internal static Func<string, string, bool> ExistsSeam;

        private static readonly Regex Https = new(@"https://[A-Za-z0-9.\-]+(:\d+)?", RegexOptions.Compiled);

        internal static readonly IReadOnlyList<WebTunnelKind> All = new[]
        {
            new WebTunnelKind("cloudflared", "cloudflared", "cloudflared.exe", KeepsAddress: false)
            {
                Arguments = port => "tunnel --no-autoupdate --url http://127.0.0.1:" + port,
                AddressIn = line => Https.Matches(line).Select(m => m.Value).FirstOrDefault(u => u.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase)),
                Folders = () => new[] { ProgramFiles(true, "cloudflared"), ProgramFiles(false, "cloudflared") },
            },
            new WebTunnelKind("devtunnel", "devtunnel", "devtunnel.exe", KeepsAddress: false)
            {
                Arguments = port => "host -p " + port + " --allow-anonymous",
                AddressIn = line => line.TrimStart().StartsWith("Connect via browser:", StringComparison.OrdinalIgnoreCase)
                    ? Https.Matches(line).Select(m => m.Value).FirstOrDefault(u => !u.Contains("-inspect.", StringComparison.OrdinalIgnoreCase))
                    : null,
                Folders = () => new[] { Local("Microsoft", "WinGet", "Links") },
            },
            new WebTunnelKind("ngrok", "ngrok", "ngrok.exe", KeepsAddress: true)
            {
                Arguments = port => "http 127.0.0.1:" + port + " --log stdout --log-format logfmt",
                AddressIn = line =>
                {
                    var m = Regex.Match(line, @"(?:^|\s)url=(https://[A-Za-z0-9.\-]+(:\d+)?)");
                    return m.Success ? m.Groups[1].Value : null;
                },
                Folders = () => new[] { Local("Microsoft", "WinGet", "Links"), Local("Microsoft", "WindowsApps") },
            },
            new WebTunnelKind("tailscale", "Tailscale Funnel", "tailscale.exe", KeepsAddress: true)
            {
                Arguments = port => "funnel " + port,
                AddressIn = line => Https.Matches(line).Select(m => m.Value).FirstOrDefault(u => u.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) || u.Contains(".ts.net:", StringComparison.OrdinalIgnoreCase)),
                Folders = () => new[] { ProgramFiles(false, "Tailscale") },
            },
        };

        internal static WebTunnelKind Of(string key) => All.FirstOrDefault(k => k.Key == key);

        private static string ProgramFiles(bool x86, string folder)
        {
            string root = Environment.GetFolderPath(x86 ? Environment.SpecialFolder.ProgramFilesX86 : Environment.SpecialFolder.ProgramFiles);
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, folder);
        }

        private static string Local(params string[] parts)
        {
            string root = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            return string.IsNullOrEmpty(root) ? null : Path.Combine(new[] { root }.Concat(parts).ToArray());
        }

        /// <summary>The tunnel programs on this PC: on the PATH of the user
        /// this helper runs as, or in the folders each installs to.</summary>
        internal static IReadOnlyList<WebTunnelFound> Find(string pathVariable = null)
        {
            var found = new List<WebTunnelFound>();
            var folders = (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            foreach (var kind in All)
            {
                foreach (string folder in folders.Concat(kind.Folders()).Where(f => !string.IsNullOrEmpty(f)))
                {
                    string candidate;
                    try { candidate = Path.Combine(folder.Trim('"'), kind.Exe); }
                    catch (ArgumentException) { continue; }
                    if (!Path.IsPathFullyQualified(candidate)) continue;
                    bool exists = ExistsSeam != null ? ExistsSeam(folder, kind.Exe) : File.Exists(candidate);
                    if (!exists) continue;
                    found.Add(new WebTunnelFound(kind.Key, candidate));
                    break;
                }
            }
            return found;
        }

        /// <summary>
        /// Starts a tunnel program for a loopback port and reports each line
        /// it writes and the address once it gives one. Null when it could not
        /// start. Its environment is the helper's, without the helper's
        /// one-time key, so the tunnel cannot speak as the helper.
        /// </summary>
        internal static Process Start(WebTunnelKind kind, string exe, int port, Action<string> address, Action<string> line, Action<int> exited)
        {
            if (OffLimitsForTest && StartSeam == null) return null;
            var info = new ProcessStartInfo(exe, kind.Arguments(port))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            };
            info.Environment.Remove(WebHelper.KeyVariable);
            Process process;
            try
            {
                process = StartSeam != null ? StartSeam(info) : Process.Start(info);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return null;
            }
            if (process == null) return null;
            int said = 0;
            void Read(string text)
            {
                if (text == null) return;
                line?.Invoke(text);
                string found = kind.AddressIn(text);
                if (found != null && Interlocked.Exchange(ref said, 1) == 0) address?.Invoke(found);
            }
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += (_, e) => Read(e.Data);
            process.ErrorDataReceived += (_, e) => Read(e.Data);
            process.Exited += (_, _) =>
            {
                int code = -1;
                try { code = process.ExitCode; } catch (InvalidOperationException) { }
                exited?.Invoke(code);
            };
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (InvalidOperationException) { }
            return process;
        }

        /// <summary>Ends a tunnel program and what it started.</summary>
        internal static void Stop(Process process)
        {
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            try { process.Dispose(); } catch { }
        }
    }
}
