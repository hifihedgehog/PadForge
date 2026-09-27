using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PadForge.Services;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// Runs Inf2Cat, from the toolchain the signing step extracts, on one INF
    /// with the /os value <see cref="Ds3DriverInstaller.CatalogOs"/> gives each
    /// architecture. Inf2Cat checks the INF and writes the catalog. Nothing is
    /// signed and nothing is installed.
    /// </summary>
    internal static class Inf2Cat
    {
        public static void AssertCatalogs(string infName, string infText, string catalogName, ITestOutputHelper output)
        {
            string tools = HIDMaestro.Internal.DriverBuilder.EnsureExtracted();
            string inf2cat = Path.Combine(tools, "Inf2Cat.exe");
            Assert.True(File.Exists(inf2cat), inf2cat);

            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests", "Inf2Cat", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, infName), infText, Encoding.ASCII);
                foreach (var machine in new[] { Architecture.X64, Architecture.Arm64 })
                {
                    string os = Ds3DriverInstaller.CatalogOs(machine);
                    foreach (string cat in Directory.GetFiles(dir, "*.cat")) File.Delete(cat);
                    var psi = new System.Diagnostics.ProcessStartInfo(inf2cat, $"/driver:\"{dir}\" /os:{os}")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    var text = new StringBuilder();
                    p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                    p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    Assert.True(p.WaitForExit(120000), "Inf2Cat timed out");
                    p.WaitForExit();
                    string log;
                    lock (text) log = text.ToString();
                    output.WriteLine($"{machine} /os:{os} exit {p.ExitCode}\n{log}");
                    Assert.True(p.ExitCode == 0, $"{os}: Inf2Cat exited {p.ExitCode}: {log}");
                    Assert.True(File.Exists(Path.Combine(dir, catalogName)), $"{os}: no {catalogName}");
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
