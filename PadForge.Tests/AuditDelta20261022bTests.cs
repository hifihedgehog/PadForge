using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The delta audit of 2ef2be16..ec929b61 (Oct. 22, the Wii Remote
    /// reconnect work of #484).
    ///
    /// <para>A right Joy-Con's NIR camera follows its hint on the fork's
    /// update loop since SDL 82219ef2f2 (51f408fb): UpdateIR reads the hint
    /// on every pass, starts the camera when the hint and the sensors ask for
    /// it, and tears it down when either stops asking. The bounce
    /// InputService ran on every hint flip predates that build. It only
    /// turned the IMU off and on, two subcommands that each wait for the
    /// pad's reply under SDL's joystick lock, and cut the motion data while
    /// they ran.</para>
    /// </summary>
    public class AuditDelta20261022bTests
    {
        /// <summary>A hint flip sets the hint and nothing else. The fork's
        /// update loop starts or stops the camera.</summary>
        [Fact]
        public void TheJoyConHintFlip_LeavesTheCameraToTheDriversUpdateLoop()
        {
            string service = RepoText("PadForge.App/Services/InputService.cs");
            int start = service.IndexOf("if (irWanted != _joyConIrHintOn)", StringComparison.Ordinal);
            int end = service.IndexOf("RefreshRingConArming(nowTick);", Math.Max(start, 0), StringComparison.Ordinal);
            Assert.True(start > 0 && end > start, "the IR hint block moved");
            string block = service[start..end];
            Assert.Contains("SDL3.SDL.SDL_SetHint(SDL3.SDL.SDL_HINT_JOYSTICK_HIDAPI_JOYCON_IR_SENSOR,", block);
            Assert.DoesNotContain("BounceMotionSensors", block);
            Assert.DoesNotContain("Task.Run", block);
        }

        /// <summary>The Wii Remote's camera check is the one caller left, so a
        /// bounce is a Wii camera start and nothing else.</summary>
        [Fact]
        public void OnlyTheWiiCameraCheck_BouncesTheSensors()
        {
            var callers = new[] { "PadForge.App", "PadForge.Engine" }
                .SelectMany(project => Directory.EnumerateFiles(RepoPath(project), "*.cs", SearchOption.AllDirectories))
                .Where(p => !IsBuildOutput(p))
                .Where(p => File.ReadAllText(p).Contains("BounceMotionSensors()", StringComparison.Ordinal))
                .Select(p => Path.GetFileName(p))
                .ToArray();
            Assert.Equal(new[] { "SdlDeviceWrapper.cs" }, callers);
        }

        /// <summary>Both shipped SDL builds carry the update-loop camera
        /// machine the hint flip now relies on. Its log strings are the
        /// fingerprint: an SDL3.dll from before 82219ef2f2 has none of
        /// them.</summary>
        [Theory]
        [InlineData("x64")]
        [InlineData("arm64")]
        public void TheShippedSdlBuild_StartsTheJoyConCameraFromItsUpdateLoop(string arch)
        {
            byte[] dll = File.ReadAllBytes(RepoPath("PadForge.App", "Resources", "SDL3", arch, "SDL3.dll"));
            foreach (string line in new[] { "Joy-Con IR: arming bring-up", "Joy-Con IR: demand dropped", "Joy-Con IR: retrying bring-up" })
                Assert.True(IndexOf(dll, Encoding.ASCII.GetBytes(line)) >= 0, arch + " SDL3.dll lacks \"" + line + "\"");
        }

        private static bool IsBuildOutput(string path)
        {
            string p = path.Replace('\\', '/');
            return p.Contains("/bin/") || p.Contains("/obj/");
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private static string RepoText(string relative)
            => File.ReadAllText(RepoPath(relative.Split('/'))).Replace("\r\n", "\n");

        private static string RepoPath(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }
    }
}
