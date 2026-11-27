using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The test process changes nothing on the PC it runs on through the
    /// update code. Every call in <see cref="UpdateService"/> and
    /// <see cref="UpdateController"/> that reaches past the process and can
    /// change something, or be seen by another program, refuses in the test
    /// host: a process start, a request to GitHub, the update lease for an
    /// installed exe, a helper's handshake, and a file or folder written,
    /// moved or deleted outside the test host's scratch folder. Reads are
    /// not on the list.
    ///
    /// <para>Two sides are pinned, as in MachineOffLimitsTests. Each real
    /// call refuses here, shown by calling it where a call that got through
    /// would do no harm (a program that does not exist, an address that is no
    /// web address, a folder nobody made) and by reading the source where no
    /// such call exists. And nothing in the application sets the flag, so
    /// production runs exactly as before.</para>
    ///
    /// <para>The source is read before a call is made, so a guard that was
    /// removed fails its test on the read, and the call it guarded is never
    /// reached.</para>
    /// </summary>
    public class UpdateServiceOffLimitsTests
    {
        private const string Service = "PadForge.App/Services/UpdateService.cs";
        private const string Controller = "PadForge.App/Services/UpdateController.cs";

        private const string Assigns = @"\s*(=(?!=)|\|=|&=|\^=)";

        /// <summary>A write of the flag through its class's name. Other
        /// areas keep flags of their own under the same field name.</summary>
        private static readonly Regex SetsTheFlagByName = new(@"\bUpdateService\.OffLimitsForTest" + Assigns, RegexOptions.Compiled);

        private static bool InMember(string source, string member, int at)
        {
            int from = source.IndexOf(member, StringComparison.Ordinal);
            return at >= from && at < from + member.Length;
        }

        /// <summary>A member's statements, one line each, comments left
        /// out.</summary>
        private static List<string> Lines(string member) =>
            member[(member.IndexOf('{') + 1)..].Split('\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)).ToList();

        /// <summary>A folder of this test's own in the test host's scratch
        /// folder. It does not exist until the test makes it.</summary>
        private static string OwnFolder() =>
            Path.Combine(UpdateService.TestScratchRoot, "UpdateServiceOffLimits", Guid.NewGuid().ToString("N"));

        /// <summary>A folder nobody made, in the temp folder and outside the
        /// scratch folder, so a write, a move or a delete in it that got
        /// through would find no folder and fail.</summary>
        private static string Nowhere() =>
            Path.Combine(Path.GetTempPath(), "PadForge-off-limits-check-" + Guid.NewGuid().ToString("N"));

        private sealed class Ui : UpdateService.IApplyUi
        {
            public readonly List<string> Reports = new();
            public void Report(string text) => Reports.Add(text);
            public bool KeepWaiting(string text, Func<bool> exited) => true;
        }

        // ── The flag ──

        [Fact]
        public void TheFlag_IsSetInTheTestHost()
        {
            Assert.True(UpdateService.OffLimitsForTest);
        }

        /// <summary>The flag is a plain field that starts false, and no line
        /// of the application or the engine writes it, in its own class or
        /// through its class's name, so production never refuses a call.</summary>
        [Fact]
        public void NothingInTheApplication_SetsTheFlag()
        {
            var writes = new List<string>();
            string service = OffLimitsSource.Read(Service);
            Assert.Single(Regex.Matches(service, @"^\s*internal static bool OffLimitsForTest;$", RegexOptions.Multiline));
            foreach (string file in new[] { Service, Controller })
                foreach (Match m in Regex.Matches(OffLimitsSource.Read(file), @"\bOffLimitsForTest" + Assigns))
                    writes.Add(Path.GetFileName(file) + ": " + m.Value);

            int read = 0;
            foreach (string project in new[] { "PadForge.App", "PadForge.Engine" })
            {
                foreach (string path in OffLimitsSource.Sources(project))
                {
                    read++;
                    foreach (Match m in SetsTheFlagByName.Matches(File.ReadAllText(path)))
                        writes.Add(Path.GetFileName(path) + ": " + m.Value);
                }
            }
            Assert.True(read > 200, "the application's sources were not found");
            Assert.Empty(writes);
        }

        /// <summary>In the tests, only the module initializer writes the
        /// flag, and only to set it, so no test lifts it for a while.</summary>
        [Fact]
        public void OnlyTheModuleInitializer_SetsTheFlag_AndNoTestClearsIt()
        {
            var writes = new List<string>();
            foreach (string path in OffLimitsSource.Sources("PadForge.Tests"))
            {
                string name = Path.GetFileName(path);
                if (name == "UpdateServiceOffLimitsTests.cs") continue;
                foreach (string line in File.ReadAllLines(path))
                    if (SetsTheFlagByName.IsMatch(line)) writes.Add(name + ": " + line.Trim());
            }
            Assert.Equal(
                new[] { "UpdateServiceOffLimits.cs: internal static void Set() => UpdateService.OffLimitsForTest = true;" },
                writes);
        }

        // ── The guards, read ──

        /// <summary>Every guard in the two classes, read from the source.
        /// Each test below reads them all before it makes a call, so with any
        /// one of them gone no call of this class is made.</summary>
        private static void ReadEveryGuard()
        {
            string service = OffLimitsSource.Read(Service);
            string controller = OffLimitsSource.Read(Controller);
            ReadTheProcessGuard(service, controller);
            ReadTheRequestGuard(service, controller);
            ReadTheWriteGuards(service, controller);
            ReadTheLeaseGuard(service, controller);
            ReadTheHandshakeGuard(service, controller);
            Assert.True(UpdateService.OffLimitsForTest);
        }

        /// <summary>Every process the two classes start (the helper,
        /// PadForge again after the swap, the browser on the release notes)
        /// starts through one member, which refuses. The one process they
        /// stop is the helper, which that member started.</summary>
        private static void ReadTheProcessGuard(string service, string controller)
        {
            string start = OffLimitsSource.Member(service, "internal static Process StartProcess(ProcessStartInfo psi)");
            OffLimitsSource.BeginsWith(start, "if (OffLimitsForTest)");
            Assert.True(InMember(service, start, Assert.Single(OffLimitsSource.Calls(service, "Process.Start("))));
            Assert.Empty(OffLimitsSource.Calls(service, "new Process("));
            Assert.Equal(2, OffLimitsSource.Calls(service, "StartProcess(psi)").Count);

            Assert.Empty(OffLimitsSource.Calls(controller, "Process.Start("));
            Assert.Empty(OffLimitsSource.Calls(controller, "new Process("));
            // The release notes open through the desktop shell, with the
            // user's rights (#597), which the test host's shell fence refuses.
            string notes = OffLimitsSource.Member(controller, "private void OpenReleaseNotes()");
            Assert.True(InMember(controller, notes, Assert.Single(OffLimitsSource.Calls(controller, "PadForge.Common.ShellLaunch.TryStart("))));
            Assert.Empty(OffLimitsSource.Calls(controller, "UpdateService.StartProcess("));

            string stop = OffLimitsSource.Member(service, "private static void StopHelper(Process helper)");
            Assert.True(InMember(service, stop, Assert.Single(OffLimitsSource.Calls(service, ".Kill("))));
            Assert.Equal(2, OffLimitsSource.Calls(service, "StopHelper(helper)").Count);
            Assert.Equal(2, OffLimitsSource.Calls(service, "StopHelper(_helper)").Count);
            Assert.Contains("helper = StartProcess(psi) ?? throw new UpdateHelperException(HelperFailure.NoAnswer);", service);
            Assert.Single(OffLimitsSource.Calls(service, "new HelperHandle(helper, ready, commit)"));
            Assert.Empty(OffLimitsSource.Calls(controller, ".Kill("));
        }

        /// <summary>Both requests (a check's question and the download) fail
        /// before they are sent.</summary>
        private static void ReadTheRequestGuard(string service, string controller)
        {
            string guard = OffLimitsSource.Member(service, "private static void ThrowIfRequestOffLimits(string url)");
            OffLimitsSource.BeginsWith(guard, "if (OffLimitsForTest)");
            string api = OffLimitsSource.Member(service, "private static async Task<ApiAnswer> GetApiAsync(string url, CancellationToken ct)");
            OffLimitsSource.BeginsWith(api, "ThrowIfRequestOffLimits(url);");
            Assert.True(InMember(service, api, Assert.Single(OffLimitsSource.Calls(service, ".SendAsync("))));
            int get = Assert.Single(OffLimitsSource.Calls(service, ".GetAsync("));
            Assert.EndsWith("ThrowIfRequestOffLimits(offer.DownloadUrl);\n                using (var response = await LazyDownload.Value",
                service[..get], StringComparison.Ordinal);
            foreach (string other in new[] { "GetStreamAsync(", "GetByteArrayAsync(", "GetStringAsync(", "PostAsync(", "PutAsync(", "PatchAsync(", "DeleteAsync(", ".Send(", "WebClient", "WebRequest" })
                Assert.Empty(OffLimitsSource.Calls(service, other));
            foreach (string any in new[] { "HttpClient", "WebClient", "WebRequest" })
                Assert.Empty(OffLimitsSource.Calls(controller, any));
        }

        /// <summary>Every write, move and delete in the two classes goes
        /// through one of seven members, each of which refuses a path outside
        /// the scratch folder before it touches the disk. The real disk the
        /// swap works on goes through them too.</summary>
        private static void ReadTheWriteGuards(string service, string controller)
        {
            string guard = OffLimitsSource.Member(service, "private static void ThrowIfWriteOffLimits(string path)");
            OffLimitsSource.BeginsWith(guard, "if (OffLimitsForTest && !IsInTestScratch(path))");
            foreach (var (signature, check, call) in new[]
                     {
                         ("internal static void CreateFolder(string path)", "ThrowIfWriteOffLimits(path);", "Directory.CreateDirectory("),
                         ("internal static void DeleteFolder(string path)", "ThrowIfWriteOffLimits(path);", "Directory.Delete("),
                         ("internal static void WriteFile(string path, string text)", "ThrowIfWriteOffLimits(path);", "File.WriteAllText("),
                         ("internal static void MoveFile(string from, string to)", "ThrowIfWriteOffLimits(from);", "File.Move("),
                         ("internal static void CopyFile(string from, string to)", "ThrowIfWriteOffLimits(to);", "File.Copy("),
                         ("internal static void DeleteFile(string path)", "ThrowIfWriteOffLimits(path);", "File.Delete("),
                         ("internal static FileStream CreateFile(string path, int bufferSize = 4096, bool useAsync = false)",
                             "ThrowIfWriteOffLimits(path);", "FileMode.Create"),
                     })
            {
                string member = OffLimitsSource.Member(service, signature);
                OffLimitsSource.BeginsWith(member, check);
                Assert.True(InMember(service, member, Assert.Single(OffLimitsSource.Calls(service, call))), call);
            }
            // A move deletes its source, so both ends are checked before it.
            Assert.Equal(
                new[] { "ThrowIfWriteOffLimits(from);", "ThrowIfWriteOffLimits(to);", "File.Move(from, to, overwrite: true);" },
                Lines(OffLimitsSource.Member(service, "internal static void MoveFile(string from, string to)")));

            foreach (string other in new[]
                     {
                         "File.WriteAllBytes(", "File.WriteAllLines(", "File.AppendAll", "File.Create(", "File.Replace(", "File.OpenWrite(",
                         "File.Open(", "File.SetAttributes(", "Directory.Move(", "FileMode.Append", "FileMode.OpenOrCreate",
                         "FileMode.Truncate", "FileAccess.ReadWrite", "new StreamWriter(", "ExtractToDirectory(", "ExtractToFile(",
                     })
                Assert.Empty(OffLimitsSource.Calls(service, other));
            // Every other stream the class opens is opened to read.
            Assert.Equal(3, OffLimitsSource.Calls(service, "new FileStream(").Count);
            Assert.Equal(2, OffLimitsSource.Calls(service, "FileMode.Open, FileAccess.Read, FileShare.Read)").Count);

            Assert.Contains("public void Copy(string from, string to) => CopyFile(from, to);", service);
            Assert.Contains("public void CreateDirectory(string dir) => CreateFolder(dir);", service);
            Assert.Contains("public void DeleteDirectory(string dir) => TryDeleteDirectory(dir);", service);

            foreach (string write in new[]
                     {
                         "File.Delete(", "File.Copy(", "File.Move(", "File.Write", "File.Append", "File.Create(", "File.Replace(",
                         "Directory.Delete(", "Directory.CreateDirectory(", "Directory.Move(", "new FileStream(", "FileMode.",
                     })
                Assert.Empty(OffLimitsSource.Calls(controller, write));
        }

        /// <summary>The lease is a name every PadForge launched from that
        /// exe looks for, and leaves at once when it finds it. It is taken
        /// only past the guard.</summary>
        private static void ReadTheLeaseGuard(string service, string controller)
        {
            string lease = OffLimitsSource.Member(service, "internal static bool TryAcquireUpdateLease(string targetExe, out Mutex lease)");
            var lines = Lines(lease);
            Assert.Equal("lease = null;", lines[0]);
            Assert.Equal("if (OffLimitsForTest && !IsInTestScratch(targetExe)) return false;", lines[1]);
            Assert.True(InMember(service, lease, Assert.Single(OffLimitsSource.Calls(service, "new Mutex("))));
            Assert.Empty(OffLimitsSource.Calls(controller, "new Mutex("));
        }

        /// <summary>The helper's handshake opens two events the old copy
        /// made and sets one. It opens none past the guard. The only other
        /// event set is the commit, on a handle that exists once a helper
        /// has started.</summary>
        private static void ReadTheHandshakeGuard(string service, string controller)
        {
            string handshake = OffLimitsSource.Member(service, "private static bool AwaitCommit(string attempt, Process oldCopy)");
            OffLimitsSource.BeginsWith(handshake, "if (OffLimitsForTest) return false;");
            var opens = OffLimitsSource.Calls(service, "EventWaitHandle.TryOpenExisting(");
            Assert.Equal(2, opens.Count);
            Assert.All(opens, at => Assert.True(InMember(service, handshake, at)));
            var sets = OffLimitsSource.Calls(service, ".Set()");
            Assert.Equal(2, sets.Count);
            Assert.Single(sets, at => InMember(service, handshake, at));
            Assert.Single(OffLimitsSource.Calls(service, "_commit.Set();"));
            foreach (string other in new[] { "EventWaitHandle", ".Set()" })
                Assert.Empty(OffLimitsSource.Calls(controller, other));
        }

        // ── The guards, run ──

        /// <summary>The program named here does not exist, so a start that
        /// got through would fail as a file not found (2), not as access
        /// denied (5).</summary>
        [Fact]
        public void TheUpdateCode_StartsNoProcess()
        {
            ReadEveryGuard();
            var refused = Assert.Throws<System.ComponentModel.Win32Exception>(() => UpdateService.StartProcess(
                new ProcessStartInfo("PadForge-off-limits-check-no-such-program.exe") { UseShellExecute = false }));
            Assert.Equal(5, refused.NativeErrorCode);
            Assert.StartsWith("The test host starts no process", refused.Message, StringComparison.Ordinal);
        }

        /// <summary>The address here is no web address, so a request that
        /// got through could reach nothing: the client refuses a scheme it
        /// does not know before it opens a connection.</summary>
        [Fact]
        public async Task TheUpdateCode_SendsGitHubNoRequest()
        {
            ReadEveryGuard();
            var api = typeof(UpdateService).GetMethod("GetApiAsync", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(api);
            var refused = await Assert.ThrowsAsync<HttpRequestException>(() => (Task)api.Invoke(null,
                new object[] { "padforge-off-limits-check://nowhere/releases", CancellationToken.None }));
            Assert.StartsWith("The test host sends no request", refused.Message, StringComparison.Ordinal);
        }

        /// <summary>The scratch folder is the test host's own folder in the
        /// temp folder. The staging folder sits in the temp folder too and
        /// holds an installed PadForge's records, so the temp folder as a
        /// whole is not inside it, and neither is the scratch folder
        /// itself.</summary>
        private static void TheScratchFolderIsTheTestHostsOwn()
        {
            Assert.Equal(Path.Combine(Path.GetTempPath(), "PadForge.Tests"), UpdateService.TestScratchRoot);
            Assert.True(UpdateService.IsInTestScratch(Path.Combine(UpdateService.TestScratchRoot, "UpdateService", "x")));
            Assert.True(UpdateService.IsInTestScratch(Path.Combine(UpdateService.TestScratchRoot.ToUpperInvariant(), "x")));
            Assert.False(UpdateService.IsInTestScratch(UpdateService.TestScratchRoot));
            Assert.False(UpdateService.IsInTestScratch(UpdateService.TestScratchRoot + Path.DirectorySeparatorChar));
            Assert.False(UpdateService.IsInTestScratch(UpdateService.TestScratchRoot + "-beside"));
            Assert.False(UpdateService.IsInTestScratch(Path.Combine(UpdateService.TestScratchRoot, "..", "PadForge-off-limits-check")));
            Assert.False(UpdateService.IsInTestScratch(Path.GetTempPath()));
            Assert.False(UpdateService.IsInTestScratch(UpdateService.StagingRoot));
            Assert.False(UpdateService.IsInTestScratch(Path.Combine(UpdateService.StagingRoot, "0123456789abcdef", "pending.json")));
            Assert.False(UpdateService.IsInTestScratch(null));
            Assert.False(UpdateService.IsInTestScratch(" "));
        }

        /// <summary>Each of the seven members refuses a path outside the
        /// scratch folder and works inside it. The folder named for the
        /// refusals does not exist, so a write, a move or a delete that got
        /// through would fail as a folder not found, and a folder that got
        /// made is removed again.</summary>
        [Fact]
        public void TheUpdateCode_WritesNothingOutsideTheScratchFolder()
        {
            ReadEveryGuard();
            TheScratchFolderIsTheTestHostsOwn();

            string own = OwnFolder();
            string nowhere = Nowhere();
            Directory.CreateDirectory(own);
            try
            {
                string kept = Path.Combine(own, "kept.txt");
                File.WriteAllText(kept, "kept");
                string outside = Path.Combine(nowhere, "PadForge.exe");

                void Refused(Action write)
                {
                    var refused = Assert.Throws<UnauthorizedAccessException>(write);
                    Assert.StartsWith("The test host writes nothing outside its scratch folder", refused.Message, StringComparison.Ordinal);
                }
                Refused(() => UpdateService.CreateFolder(nowhere));
                Refused(() => UpdateService.DeleteFolder(nowhere));
                Refused(() => UpdateService.WriteFile(outside, "x"));
                Refused(() => UpdateService.MoveFile(kept, outside));
                Refused(() => UpdateService.MoveFile(outside, Path.Combine(own, "moved.txt")));
                Refused(() => UpdateService.CopyFile(kept, outside));
                Refused(() => UpdateService.DeleteFile(outside));
                Refused(() => UpdateService.CreateFile(outside).Dispose());
                Assert.False(Directory.Exists(nowhere));
                Assert.Equal("kept", File.ReadAllText(kept));
                Assert.False(File.Exists(Path.Combine(own, "moved.txt")));

                // Inside the scratch folder each one does its work.
                string sub = Path.Combine(own, "sub");
                UpdateService.CreateFolder(sub);
                Assert.True(Directory.Exists(sub));
                string first = Path.Combine(sub, "first.txt");
                UpdateService.WriteFile(first, "one");
                Assert.Equal("one", File.ReadAllText(first));
                string second = Path.Combine(sub, "second.txt");
                UpdateService.CopyFile(first, second);
                Assert.Equal("one", File.ReadAllText(second));
                UpdateService.WriteFile(first, "two");
                UpdateService.MoveFile(first, second);
                Assert.False(File.Exists(first));
                Assert.Equal("two", File.ReadAllText(second));
                string made = Path.Combine(sub, "made.bin");
                using (var stream = UpdateService.CreateFile(made)) stream.WriteByte(7);
                Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(made));
                UpdateService.DeleteFile(made);
                Assert.False(File.Exists(made));
                UpdateService.DeleteFolder(sub);
                Assert.False(Directory.Exists(sub));
            }
            finally
            {
                Directory.Delete(own, recursive: true);
                if (Directory.Exists(nowhere)) Directory.Delete(nowhere, recursive: true);
            }
        }

        /// <summary>What the update code does to files, through its own
        /// entry points: an exe taken out of a download, a pending record,
        /// the cleanup, and the disk the swap works on. Outside the scratch
        /// folder each refuses or leaves everything as it was. The folder
        /// the cleanup is handed is this test's own, made and removed here,
        /// with what a first release of the feature left in the staging
        /// folder: the cleanup deletes exactly that when it may.</summary>
        [Fact]
        public void TheUpdateCodesOwnWork_ChangesNothingOutsideTheScratchFolder()
        {
            ReadEveryGuard();
            TheScratchFolderIsTheTestHostsOwn();

            string own = OwnFolder();
            string probe = Nowhere();
            Directory.CreateDirectory(own);
            try
            {
                string zip = Path.Combine(own, "download.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("PadForge.exe").Open()))
                    writer.Write("exe");
                string outside = Path.Combine(probe, "PadForge.exe");
                Assert.Throws<UnauthorizedAccessException>(() => UpdateService.ExtractExe(zip, outside));
                Assert.False(Directory.Exists(probe));
                string inside = Path.Combine(own, "PadForge.exe");
                UpdateService.ExtractExe(zip, inside);
                Assert.Equal("exe", File.ReadAllText(inside));

                // The record of the running exe, which is the test host, lives
                // in the staging folder.
                var offer = new UpdateOffer("r1 (abc1234)", true, 1, "abc1234", null, "u", "PadForge.zip", "u", 1, new string('a', 64));
                Assert.Throws<UnauthorizedAccessException>(() => UpdateService.WritePending(offer, new StagedUpdate(inside, "x"), false));

                Directory.CreateDirectory(Path.Combine(probe, "r1234"));
                File.WriteAllText(Path.Combine(probe, "pending.json"), "{}");
                Assert.False(UpdateService.CleanStaging(probe, Path.Combine(probe, "install", "PadForge.exe")));
                Assert.True(Directory.Exists(Path.Combine(probe, "r1234")));
                Assert.True(File.Exists(Path.Combine(probe, "pending.json")));

                var disk = (UpdateService.IFileOps)typeof(UpdateService).GetNestedType("DiskFiles", BindingFlags.NonPublic)
                    .GetField("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                Assert.Throws<UnauthorizedAccessException>(() => disk.Copy(inside, Path.Combine(probe, "pending.json")));
                Assert.Throws<UnauthorizedAccessException>(() => disk.CreateDirectory(Path.Combine(probe, "recovery-1")));
                disk.DeleteDirectory(Path.Combine(probe, "r1234"));
                Assert.True(Directory.Exists(Path.Combine(probe, "r1234")));
                Assert.Equal("{}", File.ReadAllText(Path.Combine(probe, "pending.json")));
                Assert.False(Directory.Exists(Path.Combine(probe, "recovery-1")));
            }
            finally
            {
                Directory.Delete(own, recursive: true);
                if (Directory.Exists(probe)) Directory.Delete(probe, recursive: true);
            }
        }

        /// <summary>The exe named for the refusal exists nowhere, so a lease
        /// that got taken would be one no PadForge looks for, and it is let
        /// go here either way.</summary>
        [Fact]
        public void TheUpdateLease_IsTakenOnlyForAnExeInTheScratchFolder()
        {
            ReadEveryGuard();
            TheScratchFolderIsTheTestHostsOwn();

            string outside = Path.Combine(Nowhere(), "PadForge.exe");
            bool taken = UpdateService.TryAcquireUpdateLease(outside, out var lease);
            lease?.Dispose();
            Assert.False(taken);
            Assert.Null(lease);
            Assert.False(UpdateService.IsUpdateInProgress(outside));

            string inside = Path.Combine(OwnFolder(), "PadForge.exe");
            Assert.True(UpdateService.TryAcquireUpdateLease(inside, out var own));
            own.Dispose();
        }

        /// <summary>The two events here are this test's own, named for an
        /// attempt nobody else knows. The commit is set beforehand, so a
        /// handshake that got through would set ready and return at once
        /// rather than wait for this process to exit.</summary>
        [Fact]
        public void TheHelpersHandshake_SignalsNoEvent()
        {
            ReadEveryGuard();
            var handshake = typeof(UpdateService).GetMethod("AwaitCommit", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(handshake);
            string attempt = Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateService.ReadyEventName(attempt));
            using var commit = new EventWaitHandle(true, EventResetMode.ManualReset, UpdateService.CommitEventName(attempt));
            using var self = Process.GetCurrentProcess();

            Assert.False((bool)handshake.Invoke(null, new object[] { attempt, self }));
            Assert.False(ready.WaitOne(0));
        }

        /// <summary>
        /// A helper run in the test host with arguments it can use, the case
        /// no stand-in covers: a target that exists, no handshake, no old
        /// copy to wait for. The target is a file of this test's own, so the
        /// lease is taken for it and let go. Its staging folder would be
        /// made in the staging folder an installed PadForge uses, and that
        /// is refused. Nothing is swapped, no record is deleted, PadForge is
        /// not started, and the helper says both.
        /// </summary>
        [Fact]
        public void AHelperThatGetsPastItsArguments_ChangesNothingAndStartsNothing()
        {
            ReadEveryGuard();
            TheScratchFolderIsTheTestHostsOwn();

            string own = OwnFolder();
            string target = Path.Combine(own, "install", "PadForge.exe");
            string staging = UpdateService.InstallDir(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            try
            {
                File.WriteAllText(target, "installed");
                Assert.False(Directory.Exists(staging));
                var ui = new Ui();

                Assert.True(UpdateService.TryRunApplyMode(new[] { UpdateService.ApplyUpdateSwitch, target, "0" }, ui));

                Assert.Equal(2, ui.Reports.Count);
                Assert.Contains("The test host writes nothing outside its scratch folder: " + staging, ui.Reports[0]);
                Assert.Contains("The test host starts no process: " + target, ui.Reports[1]);
                Assert.Equal("installed", File.ReadAllText(target));
                Assert.Equal(new[] { target }, Directory.GetFiles(own, "*", SearchOption.AllDirectories));
                Assert.False(Directory.Exists(staging));
                Assert.False(UpdateService.IsUpdateInProgress(target));
            }
            finally
            {
                Directory.Delete(own, recursive: true);
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }

        // ── The update tests' own paths ──

        /// <summary>The update tests name no installed PadForge and no path
        /// in the temp folder but through the scratch folder. They used to
        /// hand the helper the path PadForge is deployed to on the PC they
        /// run on, where only the helper's own argument checks stood between
        /// a test and a swap of that exe.</summary>
        [Fact]
        public void TheUpdateTests_NameNoInstalledPadForgeAndNoFolderOfTheTempFolder()
        {
            foreach (string file in new[] { "PadForge.Tests/UpdateServiceTests.cs", "PadForge.Tests/UpdateControllerTests.cs" })
            {
                string source = OffLimitsSource.Read(file);
                Assert.DoesNotContain(@":\PadForge", source, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("GetTempPath", source, StringComparison.Ordinal);
                Assert.DoesNotContain("ProcessPath", source, StringComparison.Ordinal);
                Assert.DoesNotContain("BaseDirectory", source, StringComparison.Ordinal);
                Assert.Contains("UpdateService.TestScratchRoot", source);
            }
        }
    }
}
