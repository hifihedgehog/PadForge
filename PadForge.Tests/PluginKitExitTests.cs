using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// How the kit's C# plugin ends when PadForge does not answer (#597): a
    /// pipe nobody serves, or a pipe that takes the hello and never answers,
    /// ends it with a line on its error output and code 4, never with an
    /// unhandled exception. A connection PadForge closes ends it with 0 and
    /// nothing on its error output.
    /// </summary>
    public sealed class PluginKitExitTests
    {
        private static string NewPipeName() => "PadForge.Tests.KitExit." + Guid.NewGuid().ToString("N");

        /// <summary>Runs the kit's plugin (the tests' helper with
        /// <c>kit</c>) against <paramref name="pipeName"/>.</summary>
        private static (int Code, string Output, string Error) RunKit(string pipeName)
        {
            Assert.True(File.Exists(PluginLauncherTests.ProbeExe), PluginLauncherTests.ProbeExe);
            var start = new ProcessStartInfo(PluginLauncherTests.ProbeExe, "kit")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["PADFORGE_PLUGIN_TOKEN"] = "kit-exit-test";
            start.Environment["PADFORGE_PIPE"] = pipeName;
            using var process = Process.Start(start);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(40000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("the kit's plugin did not end");
            }
            process.WaitForExit();
            return (process.ExitCode, output.Result, error.Result);
        }

        [Fact]
        public void APipeNobodyServes_EndsThePlugin_WithALineAndCode4()
        {
            var (code, output, error) = RunKit(NewPipeName());
            Assert.True(code == 4, "code " + code + ": " + output + error);
            Assert.Contains("did not answer", error);
            Assert.DoesNotContain("Unhandled exception", error);
        }

        [Fact]
        public async Task APipeThatNeverAnswersTheHello_EndsThePlugin_WithALineAndCode4()
        {
            string name = NewPipeName();
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var served = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                var reader = new StreamReader(server, new UTF8Encoding(false));
                string hello = await reader.ReadLineAsync();
                // Holds the pipe open, past the plugin's wait for an answer.
                await Task.Delay(15000);
                return hello;
            });
            var (code, output, error) = await Task.Run(() => RunKit(name));
            Assert.True(code == 4, "code " + code + ": " + output + error);
            Assert.Contains("did not answer hello", error);
            Assert.StartsWith("@1 hello ", await served);
        }

        [Fact]
        public async Task AConnectionPadForgeCloses_EndsThePlugin_With0AndNoError()
        {
            string name = NewPipeName();
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var served = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                var reader = new StreamReader(server, new UTF8Encoding(false));
                string hello = await reader.ReadLineAsync();
                server.Disconnect();
                return hello;
            });
            var (code, output, error) = await Task.Run(() => RunKit(name));
            Assert.True(code == 0, "code " + code + ": " + output + error);
            Assert.Equal(string.Empty, error);
            Assert.StartsWith("@1 hello ", await served);
        }
    }
}
