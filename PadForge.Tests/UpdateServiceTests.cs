using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Serialization;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests;

/// <summary>
/// In-app updates (#457). The decisions are pinned against the release
/// shapes GitHub serves today: releases publish PadForge-v{version}-win-x64.zip
/// and -win-arm64.zip, and the rolling dev release latest-v4-dev, titled
/// "PadForge r{count}@{hash}", publishes PadForge.zip and PadForge-arm64.zip.
/// </summary>
public class UpdateServiceTests
{
    private const string Digest = "sha256:be1713ecbf7de65844a9b563c56dbcd9a96cbc12d53c4c101b88d783e3cd525b";

    // The v4.5.2 release as the API returned it, trimmed to the fields read.
    private const string StableJson = """
        {"tag_name":"v4.5.2","name":"PadForge v4.5.2","draft":false,"prerelease":false,
         "html_url":"https://github.com/hifihedgehog/PadForge/releases/tag/v4.5.2",
         "assets":[
          {"name":"PadForge-v4.5.2-win-arm64.zip","size":284476236,
           "digest":"sha256:8f9cf85a3f298eb818a5565e37c0d95c5666d4356c3ea3461442047bc5969a63",
           "browser_download_url":"https://github.com/hifihedgehog/PadForge/releases/download/v4.5.2/PadForge-v4.5.2-win-arm64.zip"},
          {"name":"PadForge-v4.5.2-win-x64.zip","size":304809444,
           "digest":"sha256:be1713ecbf7de65844a9b563c56dbcd9a96cbc12d53c4c101b88d783e3cd525b",
           "browser_download_url":"https://github.com/hifihedgehog/PadForge/releases/download/v4.5.2/PadForge-v4.5.2-win-x64.zip"}]}
        """;

    // latest-v4-dev as the API returned it, trimmed the same way.
    private const string DevJson = """
        {"tag_name":"latest-v4-dev","name":"PadForge r3676@b500441","draft":false,"prerelease":true,
         "html_url":"https://github.com/hifihedgehog/PadForge/releases/tag/latest-v4-dev",
         "assets":[
          {"name":"PadForge-arm64.zip","size":283002879,
           "digest":"sha256:7b7adf4fe9a8dbd895c527406b66d972a4d2e4550da4e667efadfb418b494531",
           "browser_download_url":"https://github.com/hifihedgehog/PadForge/releases/download/latest-v4-dev/PadForge-arm64.zip"},
          {"name":"PadForge.zip","size":303209889,
           "digest":"sha256:72a6b70cd01653600f8e16315d71dd42a86bfd73bdeea89c70e7a9b4221dcbc6",
           "browser_download_url":"https://github.com/hifihedgehog/PadForge/releases/download/latest-v4-dev/PadForge.zip"}]}
        """;

    private static GitHubRelease Parse(string json) => JsonSerializer.Deserialize<GitHubRelease>(json);

    // ── Releases ──

    [Theory]
    [InlineData(false, "PadForge-v4.5.2-win-x64.zip", "be1713ec")]
    [InlineData(true, "PadForge-v4.5.2-win-arm64.zip", "8f9cf85a")]
    public void ANewerReleaseOffersTheZipForThisMachine(bool arm64, string asset, string shaPrefix)
    {
        var outcome = UpdateService.Evaluate(Parse(StableJson), preReleaseChannel: false,
            new Version(4, 5, 1, 0), 3600, "aaaaaaa", arm64, out var offer);
        Assert.Equal(UpdateCheckOutcome.Available, outcome);
        Assert.Equal("4.5.2", offer.DisplayVersion);
        Assert.False(offer.IsPreRelease);
        Assert.Equal(asset, offer.AssetName);
        Assert.StartsWith(shaPrefix, offer.Sha256);
        // The folder name carries the file's hash: a release replaced under
        // its own version is another file.
        Assert.Equal("v4.5.2-" + offer.Sha256[..12], offer.StageKey);
        Assert.Null(offer.Commit);
        Assert.False(offer.SameVersionReplacement);
        Assert.Equal("https://github.com/hifihedgehog/PadForge/releases/tag/v4.5.2", offer.ReleasePageUrl);
    }

    [Theory]
    [InlineData(4, 5, 2)]   // SharedVersion carries a fourth part, the tag does not
    [InlineData(4, 6, 0)]
    [InlineData(5, 0, 0)]
    public void TheSameOrAnOlderReleaseIsUpToDate(int major, int minor, int patch)
    {
        var outcome = UpdateService.Evaluate(Parse(StableJson), false,
            new Version(major, minor, patch, 0), 0, "", false, out var offer);
        Assert.Equal(UpdateCheckOutcome.UpToDate, outcome);
        Assert.Null(offer);
    }

    [Theory]
    [InlineData("v4.6.0-rc1")]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    public void ATagThatIsNotAVersionIsRefused(string tag)
    {
        var release = Parse(StableJson);
        release.TagName = tag;
        Assert.Equal(UpdateCheckOutcome.Failed,
            UpdateService.Evaluate(release, false, new Version(4, 0, 0, 0), 0, "", false, out _));
    }

    [Fact]
    public void ADraftIsNeverOffered()
    {
        var release = Parse(StableJson);
        release.Draft = true;
        Assert.Equal(UpdateCheckOutcome.Failed,
            UpdateService.Evaluate(release, false, new Version(4, 0, 0, 0), 0, "", false, out _));
    }

    // ── Pre-releases ──

    [Theory]
    [InlineData(false, "PadForge.zip")]
    [InlineData(true, "PadForge-arm64.zip")]
    public void ANewerDevBuildOffersTheZipForThisMachine(bool arm64, string asset)
    {
        var outcome = UpdateService.Evaluate(Parse(DevJson), preReleaseChannel: true,
            new Version(4, 5, 2, 0), 3650, "1234567", arm64, out var offer);
        Assert.Equal(UpdateCheckOutcome.Available, outcome);
        Assert.True(offer.IsPreRelease);
        Assert.Equal(3676, offer.BuildNumber);
        Assert.Equal("r3676 (b500441)", offer.DisplayVersion);
        Assert.Equal("r3676-" + offer.Sha256[..12], offer.StageKey);
        Assert.Equal("b500441", offer.Commit);
        Assert.Equal(asset, offer.AssetName);
    }

    [Theory]
    [InlineData(3676, "b500441")]   // this very build
    [InlineData(3677, "2b081e7")]   // a newer local build
    public void TheSameOrANewerBuildIsUpToDate(int build, string commit)
    {
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateService.Evaluate(Parse(DevJson), true,
            new Version(4, 5, 2, 0), build, commit, false, out _));
    }

    [Fact]
    public void TheSameCountOnAnotherCommitIsARebuiltHistoryAndNewer()
    {
        Assert.Equal(UpdateCheckOutcome.Available, UpdateService.Evaluate(Parse(DevJson), true,
            new Version(4, 5, 2, 0), 3676, "deadbee", false, out _));
    }

    [Fact]
    public void ABuildWithoutACountTakesAnyPublishedBuild()
    {
        Assert.Equal(UpdateCheckOutcome.Available, UpdateService.Evaluate(Parse(DevJson), true,
            new Version(4, 5, 2, 0), 0, "", false, out _));
    }

    [Theory]
    [InlineData("PadForge dev build archive")]
    [InlineData("PadForge v4.5.2")]
    [InlineData("PadForge r@b500441")]
    [InlineData("PadForge r0@b500441")]
    public void ADevTitleOutsideTheCiFormatIsRefused(string title)
    {
        var release = Parse(DevJson);
        release.Name = title;
        Assert.Equal(UpdateCheckOutcome.Failed, UpdateService.Evaluate(release, true,
            new Version(4, 5, 2, 0), 1, "0000000", false, out _));
    }

    [Fact]
    public void TheDevFeedFollowsTheRunningMajorVersion()
    {
        Assert.Equal("https://api.github.com/repos/hifihedgehog/PadForge/releases/tags/latest-v4-dev",
            UpdateService.DevEndpoint(new Version(4, 5, 2, 0)));
        Assert.Equal("https://api.github.com/repos/hifihedgehog/PadForge/releases/latest",
            UpdateService.StableEndpoint);
    }

    // ── What is fetched ──

    [Fact]
    public void ADevBuildWithNoArm64ZipHasNothingForAnArm64Pc()
    {
        var release = Parse(DevJson);
        release.Assets.RemoveAll(a => a.Name == "PadForge-arm64.zip");
        Assert.Equal(UpdateCheckOutcome.NoBuildForThisPc, UpdateService.Evaluate(release, true,
            new Version(4, 5, 2, 0), 3600, "aaaaaaa", arm64Machine: true, out _));
        Assert.Equal(UpdateCheckOutcome.Available, UpdateService.Evaluate(release, true,
            new Version(4, 5, 2, 0), 3600, "aaaaaaa", arm64Machine: false, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1:0123456789abcdef0123456789abcdef01234567")]
    [InlineData("sha256:1234")]
    [InlineData("sha256:zz1713ecbf7de65844a9b563c56dbcd9a96cbc12d53c4c101b88d783e3cd525b")]
    public void AnAssetWithoutAUsableDigestIsNeverOffered(string digest)
    {
        var release = Parse(StableJson);
        foreach (var a in release.Assets) a.Digest = digest;
        Assert.Equal(UpdateCheckOutcome.Failed, UpdateService.Evaluate(release, false,
            new Version(4, 0, 0, 0), 0, "", false, out var offer));
        Assert.Null(offer);
    }

    [Fact]
    public void TheDigestIsReadAsLowercaseHex()
    {
        Assert.Equal("be1713ecbf7de65844a9b563c56dbcd9a96cbc12d53c4c101b88d783e3cd525b",
            UpdateService.ParseSha256Digest(Digest.ToUpperInvariant().Replace("SHA256", "sha256")));
    }

    [Theory]
    [InlineData("https://github.com/hifihedgehog/PadForge/releases/download/v4.5.2/PadForge-v4.5.2-win-x64.zip", true)]
    [InlineData("http://github.com/hifihedgehog/PadForge/releases/download/v4.5.2/PadForge-v4.5.2-win-x64.zip", false)]
    [InlineData("https://github.com.evil.example/hifihedgehog/PadForge/releases/download/v4.5.2/x.zip", false)]
    [InlineData("https://github.com/someone/PadForge/releases/download/v4.5.2/PadForge-v4.5.2-win-x64.zip", false)]
    [InlineData("https://github.com/hifihedgehog/PadForge/archive/refs/tags/v4.5.2.zip", false)]
    [InlineData("not a url", false)]
    public void OnlyThisRepositorysReleaseDownloadsAreTrusted(string url, bool trusted)
    {
        Assert.Equal(trusted, UpdateService.IsTrustedDownloadUrl(url));
        var release = Parse(StableJson);
        foreach (var a in release.Assets) a.DownloadUrl = url;
        Assert.Equal(trusted ? UpdateCheckOutcome.Available : UpdateCheckOutcome.Failed,
            UpdateService.Evaluate(release, false, new Version(4, 0, 0, 0), 0, "", false, out _));
    }

    [Fact]
    public void OnlyTheZipsRootExeIsExtracted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "PadForgeUpdateTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string zip = Path.Combine(dir, "a.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Write(archive, "nested/PadForge.exe", "wrong");
                Write(archive, "PadForge.exe", "right");
                Write(archive, "readme.txt", "ignored");
            }
            string exe = Path.Combine(dir, "PadForge.exe");
            string sha = UpdateService.ExtractExe(zip, exe);
            Assert.Equal("right", File.ReadAllText(exe));
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("right"))).ToLowerInvariant(), sha);
            Assert.Equal(2, Directory.GetFiles(dir).Length);

            string empty = Path.Combine(dir, "b.zip");
            using (var archive = ZipFile.Open(empty, ZipArchiveMode.Create))
                Write(archive, "nested/PadForge.exe", "wrong");
            Assert.Throws<InvalidDataException>(() => UpdateService.ExtractExe(empty, Path.Combine(dir, "x.exe")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        static void Write(ZipArchive archive, string name, string text)
        {
            using var stream = archive.CreateEntry(name).Open();
            using var writer = new StreamWriter(stream);
            writer.Write(text);
        }
    }

    // ── Replacing the running exe: this copy's side ──

    /// <summary>A staged exe that changed after it was written never starts.
    /// The check happens before any process is created.</summary>
    [Fact]
    public void AStagedExeThatChangedAfterItWasWrittenIsRefused()
    {
        string path = Path.Combine(Path.GetTempPath(), "PadForgeUpdateTest_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "not the file that was verified");
        try
        {
            Assert.Throws<UpdateVerificationException>(() => UpdateService.StartHelper(
                new StagedUpdate(path, new string('0', 64)), null, Array.Empty<string>(), CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheInstallerGetsTheTargetThePidTheAttemptTheHashAndTheLaunchArguments()
    {
        string sha = new('a', 64);
        var args = UpdateService.BuildInstallerArgs(@"C:\PadForge\PadForge.exe", 4242, "0123456789abcdef0123456789abcdef", sha,
            "b500441", new[] { "--updated", "--profile", "Rocket League" });
        Assert.Equal(new[] { "--apply-update", @"C:\PadForge\PadForge.exe", "4242",
            "--handshake", "0123456789abcdef0123456789abcdef", sha, "b500441", "--profile", "Rocket League" }, args);
        // An offer that named no commit, a newer release, says so with a dash.
        Assert.Equal("-", UpdateService.BuildInstallerArgs(@"C:\PadForge\PadForge.exe", 4242,
            "0123456789abcdef0123456789abcdef", sha, null, Array.Empty<string>())[6]);
        Assert.Equal("-", UpdateService.BuildInstallerArgs(@"C:\PadForge\PadForge.exe", 4242,
            "0123456789abcdef0123456789abcdef", sha, "not a commit", Array.Empty<string>())[6]);
    }

    /// <summary>The helper refuses to be any build but the one the offer
    /// named, and a build with no commit stamped is no named build.</summary>
    [Theory]
    [InlineData("-", "", "", true)]
    [InlineData("b500441", FullAConst, "b500441", true)]
    [InlineData(FullAConst, FullAConst, "b500441", true)]
    [InlineData("B500441", FullAConst, "b500441", true)]
    [InlineData("cc988e7", FullAConst, "b500441", false)]
    [InlineData(FullBConst, FullAConst, "b500441", false)]
    [InlineData("b500441", "", "b500441", true)]
    [InlineData("b500441", "", "", false)]
    [InlineData("b50", FullAConst, "b500441", false)]
    // A short hash cannot prove a full one.
    [InlineData(FullAConst, "", "b500441", false)]
    public void TheHelperIsOnlyTheBuildTheOfferNamed(string expected, string commitSha, string commit, bool ok)
    {
        Assert.Equal(ok, UpdateService.IsExpectedBuild(expected, commitSha, commit));
    }

    [Fact]
    public void AHelperThatIsNotTheBuildTheOfferNamedLeavesWithItsOwnExitCode()
    {
        string sha = new('a', 64);
        int before = Environment.ExitCode;
        try
        {
            var ui = new RecordingUi();
            // No build stamps this commit, so the running test host is not it.
            Assert.True(UpdateService.TryRunApplyMode(new[] { "--apply-update", @"C:\PadForge\PadForge.exe", "4242",
                "--handshake", "0123456789abcdef0123456789abcdef", sha, "0000000000000000000000000000000000000000" }, ui));
            Assert.Equal(UpdateService.WrongBuildExitCode, Environment.ExitCode);
            Assert.Empty(ui.Reports);
        }
        finally
        {
            Environment.ExitCode = before;
        }
    }

    [Fact]
    public void ANormalLaunchIsNotTheInstaller()
    {
        Assert.False(UpdateService.TryRunApplyMode(Array.Empty<string>(), null));
        Assert.False(UpdateService.TryRunApplyMode(new[] { "--profile", "X", "Y" }, null));
        Assert.False(UpdateService.TryRunApplyMode(new[] { "--updated" }, null));
    }

    private sealed class RecordingUi : UpdateService.IApplyUi
    {
        public readonly List<string> Reports = new();
        public void Report(string text) => Reports.Add(text);
        public bool KeepWaiting(string text, Func<bool> exited) => true;
    }

    /// <summary>Once the first argument is the update switch the process is
    /// the helper, and arguments it cannot use end it there. It never goes on
    /// to start PadForge from the staging folder.</summary>
    [Fact]
    public void AnUpdateSwitchWithArgumentsItCannotUseEndsTheProcess()
    {
        string sha = new('a', 64);
        // Too short to name a target: nobody waits for this helper, so it says so.
        var ui = new RecordingUi();
        Assert.True(UpdateService.TryRunApplyMode(new[] { "--apply-update", @"C:\PadForge\PadForge.exe" }, ui));
        Assert.Single(ui.Reports);
        // A handshake it cannot read: the copy that started it waits for it,
        // sees it exit, and reports that itself.
        foreach (var args in new[]
                 {
                     new[] { "--apply-update", @"C:\PadForge\PadForge.exe", "4242", "--handshake" },
                     new[] { "--apply-update", @"C:\PadForge\PadForge.exe", "4242", "--handshake", "not-a-guid", sha, "-" },
                     new[] { "--apply-update", @"C:\PadForge\PadForge.exe", "4242", "--handshake",
                         "0123456789abcdef0123456789abcdef", "not-a-hash", "-" },
                     // The expected commit is missing, or is not one.
                     new[] { "--apply-update", @"C:\PadForge\PadForge.exe", "4242", "--handshake",
                         "0123456789abcdef0123456789abcdef", sha },
                     new[] { "--apply-update", @"C:\PadForge\PadForge.exe", "4242", "--handshake",
                         "0123456789abcdef0123456789abcdef", sha, "--profile" },
                 })
        {
            ui = new RecordingUi();
            Assert.True(UpdateService.TryRunApplyMode(args, ui));
            Assert.Empty(ui.Reports);
        }
        // A copy from before the handshake with a target that is not a full
        // path, or not a file: reported, and nothing is started.
        foreach (string target in new[] { "PadForge.exe", Path.GetTempPath(),
                     Path.Combine(Path.GetTempPath(), "PadForgeNoSuchExe_" + Guid.NewGuid().ToString("N") + ".exe") })
        {
            ui = new RecordingUi();
            Assert.True(UpdateService.TryRunApplyMode(new[] { "--apply-update", target, "0" }, ui));
            Assert.Single(ui.Reports);
        }
    }

    [Fact]
    public void TheHelperIsReadyOnlyWhileItRuns()
    {
        var timeout = TimeSpan.FromSeconds(5);
        Assert.Equal(UpdateService.HelperStart.Ready,
            UpdateService.WaitForHelper(_ => true, () => false, timeout, CancellationToken.None));
        Assert.Equal(UpdateService.HelperStart.Exited,
            UpdateService.WaitForHelper(_ => false, () => true, timeout, CancellationToken.None));
        // Ready and then gone: it gave up, so this copy keeps running.
        bool signaled = false;
        Assert.Equal(UpdateService.HelperStart.Exited,
            UpdateService.WaitForHelper(_ => signaled = true, () => signaled, timeout, CancellationToken.None));
        Assert.Equal(UpdateService.HelperStart.TimedOut,
            UpdateService.WaitForHelper(_ => false, () => false, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Equal(UpdateService.HelperStart.Canceled,
            UpdateService.WaitForHelper(_ => true, () => false, timeout, canceled.Token));
    }

    // ── Replacing the running exe: the helper's side ──

    [Fact]
    public void ASlowCloseIsAskedAboutAndThenWaitedFor()
    {
        int asked = 0, waits = 0;
        var result = UpdateService.WaitForOldCopy(ms => ms > 0 && ++waits >= 3, exited => { asked++; return true; },
            TimeSpan.FromMinutes(2));
        Assert.Equal(UpdateService.WaitResult.Exited, result);
        Assert.Equal(2, asked);
    }

    /// <summary>The question watches the old copy while it is open, so it can
    /// close itself the moment the old copy exits.</summary>
    [Fact]
    public void TheQuestionCanSeeTheOldCopyExit()
    {
        bool closed = false;
        var result = UpdateService.WaitForOldCopy(ms => closed,
            exited =>
            {
                Assert.False(exited());
                closed = true;
                Assert.True(exited());
                return true;
            },
            TimeSpan.FromMinutes(2));
        Assert.Equal(UpdateService.WaitResult.Exited, result);
    }

    /// <summary>Skipping the update means the same thing whether the old copy
    /// closed a moment before the answer or a moment after it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkippingGivesTheSameAnswerWheneverTheOldCopyCloses(bool closedMeanwhile)
    {
        var result = UpdateService.WaitForOldCopy(ms => ms == 0 && closedMeanwhile, exited => false,
            TimeSpan.FromMinutes(2));
        Assert.Equal(UpdateService.WaitResult.Skipped, result);
    }

    /// <summary>A disk that stands in for the real one: a path's content is
    /// its hash, and each step can be made to fail.</summary>
    private sealed class FakeFiles : UpdateService.IFileOps
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Log = new();
        public Func<string, string, bool> FailCopy = (_, _) => false;
        public Func<string, string, string> Corrupt = (_, content) => content;
        public bool Deleted;

        public void Copy(string from, string to)
        {
            Log.Add("copy " + from + " > " + to);
            if (FailCopy(from, to)) throw new IOException("in use");
            Files[to] = Corrupt(to, Files[from]);
        }

        public string Hash(string path) => Files.TryGetValue(path, out var c) ? c : throw new FileNotFoundException(path);
        public void CreateDirectory(string dir) { }
        public void DeleteDirectory(string dir) { Deleted = true; }
    }

    private const string Source = @"C:\temp\new\PadForge.exe";
    private const string Target = @"C:\PadForge\PadForge.exe";
    private const string Recovery = @"C:\temp\recovery-1";
    private static readonly string Backup = Path.Combine(Recovery, "PadForge.exe");

    private static FakeFiles Disk() => new() { Files = { [Source] = "new", [Target] = "old" } };

    [Fact]
    public void TheOldExeIsKeptUntilTheNewOneIsIn()
    {
        var disk = Disk();
        var outcome = UpdateService.Swap(Source, Target, "new", Recovery, disk, 3, TimeSpan.Zero);
        Assert.Equal(UpdateService.SwapResult.Installed, outcome.Result);
        Assert.Equal("new", disk.Files[Target]);
        Assert.Equal("copy " + Target + " > " + Backup, disk.Log[0]);
        // The copy of the old exe outlives the swap: it goes once the new exe
        // has run, which the swap cannot see.
        Assert.False(disk.Deleted);
        Assert.Equal(Recovery, outcome.RecoveryDir);
        Assert.Equal("old", disk.Files[Backup]);
    }

    [Fact]
    public void ABackupThatFailsLeavesTheInstalledExeAlone()
    {
        var disk = Disk();
        disk.FailCopy = (from, to) => to == Backup;
        var outcome = UpdateService.Swap(Source, Target, "new", Recovery, disk, 3, TimeSpan.Zero);
        Assert.Equal(UpdateService.SwapResult.NotChanged, outcome.Result);
        Assert.Equal("old", disk.Files[Target]);
        Assert.DoesNotContain(disk.Log, l => l.EndsWith("> " + Target, StringComparison.Ordinal));
    }

    [Fact]
    public void ALockedTargetIsRetriedUntilTheCopyLands()
    {
        var disk = Disk();
        int tries = 0;
        disk.FailCopy = (from, to) => from == Source && ++tries < 3;
        var outcome = UpdateService.Swap(Source, Target, "new", Recovery, disk, 10, TimeSpan.Zero);
        Assert.Equal(UpdateService.SwapResult.Installed, outcome.Result);
        Assert.Equal(3, tries);
    }

    [Fact]
    public void AReplaceThatNeverLandsPutsTheOldExeBack()
    {
        var disk = Disk();
        // Every copy of the update is written wrong, the way a full disk ends one.
        disk.Corrupt = (to, content) => content == "new" ? "partial" : content;
        var outcome = UpdateService.Swap(Source, Target, "new", Recovery, disk, 3, TimeSpan.Zero);
        Assert.Equal(UpdateService.SwapResult.Restored, outcome.Result);
        Assert.Equal("old", disk.Files[Target]);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public void ARestoreThatFailsKeepsTheRecoveryCopyAndSaysWhere()
    {
        var disk = Disk();
        disk.FailCopy = (from, to) => to == Target;
        var outcome = UpdateService.Swap(Source, Target, "new", Recovery, disk, 3, TimeSpan.Zero);
        Assert.Equal(UpdateService.SwapResult.Broken, outcome.Result);
        Assert.Equal(Recovery, outcome.RecoveryDir);
        Assert.Equal("old", disk.Files[Backup]);
        Assert.False(disk.Deleted);
    }

    // ── The marker the helper adds ──

    [Theory]
    [InlineData(new[] { "--updated" }, true)]
    [InlineData(new[] { "--profile", "--updated" }, false)]
    [InlineData(new[] { "--profile", "--profile", "--updated" }, true)]
    [InlineData(new[] { "--updated", "--profile", "--updated" }, true)]
    [InlineData(new[] { "--updated", "--profile" }, true)]
    [InlineData(new[] { "--default-profile", "--updated" }, true)]
    [InlineData(new string[0], false)]
    public void TheMarkerIsAnUpdatedThatIsNotAProfileName(string[] args, bool marked)
    {
        Assert.Equal(marked, UpdateService.HasUpdatedMarker(args));
    }

    [Fact]
    public void RemovingTheMarkerKeepsAProfileNamedLikeIt()
    {
        Assert.Equal(new[] { "--profile", "--updated" },
            UpdateService.WithoutUpdatedMarkers(new[] { "--updated", "--profile", "--updated" }));
        // A --profile left without a value stays without one.
        Assert.Equal(new[] { "--profile" }, UpdateService.WithoutUpdatedMarkers(new[] { "--updated", "--profile" }));
    }

    // ── Install at next launch ──

    [Theory]
    [InlineData(false, 0, null, "4.6.0", 4, 5, 2, 0, true)]
    [InlineData(false, 0, null, "4.5.2", 4, 5, 2, 0, false)]
    [InlineData(true, 3700, null, null, 4, 5, 2, 3676, true)]
    [InlineData(true, 3676, null, null, 4, 5, 2, 3676, false)]
    [InlineData(true, 3676, "b500441", null, 4, 5, 2, 3676, false)]
    [InlineData(true, 3676, "c0ffee1", null, 4, 5, 2, 3676, true)]
    [InlineData(true, 3700, null, null, 4, 5, 2, 0, true)]
    [InlineData(true, 0, null, null, 4, 5, 2, 0, false)]
    public void APendingUpdateInstallsOnlyWhenItIsNewer(bool pre, int build, string commit, string version,
        int major, int minor, int patch, int currentBuild, bool newer)
    {
        var pending = new PendingUpdate { IsPreRelease = pre, BuildNumber = build, Commit = commit, Version = version };
        Assert.Equal(newer, UpdateService.IsNewerThan(pending, new Version(major, minor, patch, 0), currentBuild, "b500441",
            FullA));
    }

    /// <summary>The check and the launch ask one rule, so a build the check
    /// offered is a build the launch installs.</summary>
    [Theory]
    [InlineData(3700, "abc1234", 3676, "b500441", true)]
    [InlineData(3676, "abc1234", 3676, "b500441", true)]
    [InlineData(3676, "b500441", 3676, "b500441", false)]
    [InlineData(3676, null, 3676, "b500441", false)]
    [InlineData(3600, "abc1234", 3676, "b500441", false)]
    [InlineData(3600, "abc1234", 0, "", true)]
    [InlineData(0, "abc1234", 0, "", false)]
    public void TheCheckAndTheLaunchAgreeOnNewer(int build, string commit, int currentBuild, string currentCommit, bool newer)
    {
        Assert.Equal(newer, UpdateService.IsNewerDevBuild(build, commit, currentBuild, currentCommit));
        var pending = new PendingUpdate { IsPreRelease = true, BuildNumber = build, Commit = commit };
        Assert.Equal(newer, UpdateService.IsNewerThan(pending, new Version(4, 5, 2), currentBuild, currentCommit, FullA));
    }

    private const string FullAConst = "b5004410d4a1c2e3f4a5b6c7d8e9f0a1b2c3d4e5";
    private const string FullBConst = "cc988e74d0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5";
    private const string FullA = FullAConst;
    private const string FullB = FullBConst;

    [Fact]
    public void AReplacedReleaseInstallsOnlyOverTheBuildItWasJudgedAgainst()
    {
        var pending = new PendingUpdate
        {
            Version = "4.5.2", SameVersionReplacement = true, BaseCommit = FullA, Commit = FullB,
        };
        var v = new Version(4, 5, 2);
        Assert.True(UpdateService.IsNewerThan(pending, v, 3676, "b500441", FullA));
        // Another build now, even one whose short hash matches.
        Assert.False(UpdateService.IsNewerThan(pending, v, 3690, "b500441", FullB));
        Assert.False(UpdateService.IsNewerThan(pending, v, 3676, "b500441", "b500441"));
        // The same commit built as another version is not the build it was judged against.
        Assert.False(UpdateService.IsNewerThan(pending, new Version(4, 5, 3), 3676, "b500441", FullA));
        pending.BaseCommit = "b500441";
        Assert.False(UpdateService.IsNewerThan(pending, v, 3676, "b500441", "b500441"));
        pending.BaseCommit = FullA;
        pending.SameVersionReplacement = false;
        Assert.False(UpdateService.IsNewerThan(pending, v, 3676, "b500441", FullA));
    }

    [Fact]
    public void TheLaunchInstallsOnlyThisCopysNewerUpdateWithBothSwitchesOn()
    {
        const string self = @"C:\PadForge\PadForge.exe";
        var v = new Version(4, 5, 2);
        PendingUpdate Ready() => new()
        {
            IsPreRelease = true, BuildNumber = 3700, Commit = "abc1234", TargetPath = @"c:\padforge\PADFORGE.EXE",
            ExePath = @"C:\temp\PadForge.exe", DisplayVersion = "r3700 (abc1234)",
        };
        UpdateService.UpdatePreferences? on = new(true, true, false);
        UpdateService.PendingAction Decide(PendingUpdate p, UpdateService.UpdatePreferences? prefs, int build = 3676) =>
            UpdateService.DecidePending(p, () => prefs, self, v, build, "b500441", FullA, _ => true);

        Assert.Equal(UpdateService.PendingAction.Install, Decide(Ready(), on));
        Assert.Equal(UpdateService.PendingAction.None, Decide(null, on));
        // Installing automatically, or checking, switched off since it was staged.
        Assert.Equal(UpdateService.PendingAction.Discard, Decide(Ready(), new(true, false, false)));
        Assert.Equal(UpdateService.PendingAction.Discard, Decide(Ready(), new(false, true, false)));
        // Staged for the channel the user has since left.
        Assert.Equal(UpdateService.PendingAction.Discard, Decide(Ready(), new(true, true, true)));
        var devChannel = Ready();
        devChannel.RequestedPreReleases = true;
        Assert.Equal(UpdateService.PendingAction.Install, Decide(devChannel, new(true, true, true)));
        Assert.Equal(UpdateService.PendingAction.Discard, Decide(devChannel, on));
        // A settings file that cannot be read leaves the record for next time.
        Assert.Equal(UpdateService.PendingAction.None, Decide(Ready(), null));
        var attempted = Ready();
        attempted.Attempted = true;
        Assert.Equal(UpdateService.PendingAction.DiscardAndReport, Decide(attempted, on));
        // An attempt that worked, whose record outlived it, goes quietly: this
        // build is the staged one now.
        Assert.Equal(UpdateService.PendingAction.Discard,
            UpdateService.DecidePending(attempted, () => on, self, v, 3700, "abc1234", FullA, _ => true));
        var other = Ready();
        other.TargetPath = @"D:\Games\PadForge\PadForge.exe";
        Assert.Equal(UpdateService.PendingAction.Discard, Decide(other, on));
        // This build is ahead of the staged one now, installed by hand.
        Assert.Equal(UpdateService.PendingAction.Discard, Decide(Ready(), on, build: 3800));
        Assert.Equal(UpdateService.PendingAction.Discard,
            UpdateService.DecidePending(Ready(), () => on, self, v, 3676, "b500441", FullA, _ => false));
        // The preferences are read only when the record gets that far.
        bool read = false;
        UpdateService.DecidePending(attempted, () => { read = true; return on; }, self, v, 3676, "b500441", FullA, _ => true);
        Assert.False(read);
    }

    [Theory]
    [InlineData("<AppSettings><CloseToTray>true</CloseToTray></AppSettings>", true, false, false)]
    [InlineData("<AppSettings><InstallUpdatesAutomatically>true</InstallUpdatesAutomatically></AppSettings>", true, true, false)]
    [InlineData("<AppSettings><CheckForUpdatesAutomatically>false</CheckForUpdatesAutomatically><InstallUpdatesAutomatically>true</InstallUpdatesAutomatically></AppSettings>", false, true, false)]
    [InlineData("<AppSettings><IncludePreReleaseUpdates> 1 </IncludePreReleaseUpdates></AppSettings>", true, false, true)]
    public void TheLaunchReadsTheSavedPreferences(string appSettings, bool check, bool install, bool preReleases)
    {
        string path = Path.Combine(Path.GetTempPath(), "PadForgeUpdatePrefs_" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, "<?xml version=\"1.0\"?><PadForgeSettings>" + appSettings + "</PadForgeSettings>");
        try
        {
            Assert.Equal(new UpdateService.UpdatePreferences(check, install, preReleases),
                UpdateService.ReadUpdatePreferences(path));
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Equal(new UpdateService.UpdatePreferences(true, false, false), UpdateService.ReadUpdatePreferences(path));
    }

    [Fact]
    public void AnUnreadableSettingsFileInstallsNothing()
    {
        string path = Path.Combine(Path.GetTempPath(), "PadForgeUpdatePrefs_" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, "<PadForgeSettings><AppSettings><InstallUpdatesAutomatically>maybe");
        try
        {
            Assert.Null(UpdateService.ReadUpdatePreferences(path));
            // Well formed, with a value XmlSerializer would refuse.
            File.WriteAllText(path, "<PadForgeSettings><AppSettings><IncludePreReleaseUpdates>yes</IncludePreReleaseUpdates></AppSettings></PadForgeSettings>");
            Assert.Null(UpdateService.ReadUpdatePreferences(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── One folder per installed copy ──

    [Fact]
    public void EachCopyHasItsOwnFolderWhateverTheCase()
    {
        Assert.Equal(UpdateService.PendingPathFor(@"C:\PadForge\PadForge.exe"),
            UpdateService.PendingPathFor(@"c:\padforge\PADFORGE.EXE"));
        Assert.NotEqual(UpdateService.PendingPathFor(@"C:\PadForge\PadForge.exe"),
            UpdateService.PendingPathFor(@"D:\Games\PadForge\PadForge.exe"));
        Assert.Matches("^[0-9a-f]{16}$", UpdateService.InstallKey(@"C:\PadForge\PadForge.exe"));
    }

    [Fact]
    public void TheCleanupTouchesOnlyWhatItCanAccountFor()
    {
        string root = Path.Combine(Path.GetTempPath(), "PadForgeUpdateClean_" + Guid.NewGuid().ToString("N"));
        string self = Path.Combine(root, "install", "PadForge.exe");
        string mine = Path.Combine(root, UpdateService.InstallKey(self));
        string otherGone = Path.Combine(root, UpdateService.InstallKey(Path.Combine(root, "gone", "PadForge.exe")));
        string otherHere = Path.Combine(root, UpdateService.InstallKey(Path.Combine(root, "install", "Other.exe")));
        string unknown = Path.Combine(root, "0123456789abcdef");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(self));
            File.WriteAllText(self, "exe");
            File.WriteAllText(Path.Combine(root, "install", "Other.exe"), "exe");
            foreach (string d in new[] { Path.Combine(root, "r3679"), Path.Combine(root, "v4.5.2"),
                         Path.Combine(mine, "kept"), Path.Combine(mine, "stale"), Path.Combine(mine, "recovery-1"),
                         otherGone, Path.Combine(otherGone, "recovery-7"), otherHere, unknown })
                Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(root, "pending.json"), "{}");
            File.WriteAllText(Path.Combine(mine, "pending.json"),
                JsonSerializer.Serialize(new PendingUpdate { ExePath = Path.Combine(mine, "kept", "PadForge.exe") }));
            File.WriteAllText(Path.Combine(otherGone, "target.txt"), Path.Combine(root, "gone", "PadForge.exe"));
            File.WriteAllText(Path.Combine(otherHere, "target.txt"), Path.Combine(root, "install", "Other.exe"));

            Assert.True(UpdateService.CleanStaging(root, self));

            Assert.False(Directory.Exists(Path.Combine(root, "r3679")));
            Assert.False(Directory.Exists(Path.Combine(root, "v4.5.2")));
            Assert.False(File.Exists(Path.Combine(root, "pending.json")));
            Assert.True(Directory.Exists(Path.Combine(mine, "kept")));
            Assert.False(Directory.Exists(Path.Combine(mine, "stale")));
            Assert.False(Directory.Exists(Path.Combine(mine, "recovery-1")));
            // Another copy's folder stays even when its exe cannot be found:
            // its drive may be unplugged, and its recovery copy the only one.
            Assert.True(Directory.Exists(otherGone));
            Assert.True(Directory.Exists(Path.Combine(otherGone, "recovery-7")));
            Assert.True(Directory.Exists(otherHere));
            Assert.True(Directory.Exists(unknown));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ARecordThatCannotBeReadNowKeepsItsDownload()
    {
        string root = Path.Combine(Path.GetTempPath(), "PadForgeUpdateLocked_" + Guid.NewGuid().ToString("N"));
        string self = Path.Combine(root, "install", "PadForge.exe");
        string mine = Path.Combine(root, UpdateService.InstallKey(self));
        string record = Path.Combine(mine, "pending.json");
        try
        {
            Directory.CreateDirectory(Path.Combine(mine, "staged"));
            File.WriteAllText(record,
                JsonSerializer.Serialize(new PendingUpdate { ExePath = Path.Combine(mine, "staged", "PadForge.exe") }));
            Assert.Equal(UpdateService.RecordRead.Read, UpdateService.ReadPendingRecord(record, out var read));
            Assert.NotNull(read);
            using (new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Antivirus or a backup holds it: nothing it names goes.
                Assert.Equal(UpdateService.RecordRead.Unreadable, UpdateService.ReadPendingRecord(record, out _));
                Assert.False(UpdateService.CleanStaging(root, self));
                Assert.True(Directory.Exists(Path.Combine(mine, "staged")));
            }
            // Not PadForge's writing: the record goes, and what it named with it.
            File.WriteAllText(record, "{ not json");
            Assert.Equal(UpdateService.RecordRead.Corrupt, UpdateService.ReadPendingRecord(record, out _));
            Assert.True(UpdateService.CleanStaging(root, self));
            Assert.False(File.Exists(record));
            Assert.False(Directory.Exists(Path.Combine(mine, "staged")));
            Assert.Equal(UpdateService.RecordRead.Missing, UpdateService.ReadPendingRecord(record, out _));
            Assert.Equal(UpdateService.RecordRead.Missing,
                UpdateService.ReadPendingRecord(Path.Combine(root, "no such folder", "pending.json"), out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── A release replaced under its own version ──

    [Fact]
    public void AReleaseReplacedUnderItsOwnVersionIsOfferedByItsCommit()
    {
        var release = Parse(StableJson);
        var v = new Version(4, 5, 2, 0);
        Assert.Equal(UpdateCheckOutcome.Available, UpdateService.Evaluate(release, false, v, 3676, "b500441", false,
            FullB.ToUpperInvariant(), out var offer));
        Assert.True(offer.SameVersionReplacement);
        Assert.Equal("4.5.2 (cc988e7)", offer.DisplayVersion);
        Assert.Equal(FullB, offer.Commit);

        // Without a commit found to descend from this build, the same version
        // is this version.
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateService.Evaluate(release, false, v, 3676, "b500441", false,
            null, out _));
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateService.Evaluate(release, false, v, 3676, "b500441", false,
            "cc988e7", out _));
        // A newer version is newer on its own, and no replacement.
        Assert.Equal(UpdateCheckOutcome.Available, UpdateService.Evaluate(release, false, new Version(4, 5, 1), 3676,
            "b500441", false, FullB, out offer));
        Assert.False(offer.SameVersionReplacement);
    }

    /// <summary>Only a build the release's own history contains is behind it.
    /// Counts cannot say that: two builds of one version on two lines of
    /// history have counts that say nothing about each other.</summary>
    [Fact]
    public void ABuildIsBehindAReleaseOnlyWhenTheReleasesHistoryHoldsIt()
    {
        var history = new[] { FullB, "1111111111111111111111111111111111111111", FullA };
        Assert.Equal(UpdateService.Ancestry.Ahead, UpdateService.PlaceInHistory(history, FullA));
        Assert.Equal(UpdateService.Ancestry.Ahead, UpdateService.PlaceInHistory(history, FullA.ToUpperInvariant()));
        Assert.Equal(UpdateService.Ancestry.Same, UpdateService.PlaceInHistory(history, FullB));
        // Ahead of the release, on another line, or further back than a page.
        Assert.Equal(UpdateService.Ancestry.Unknown,
            UpdateService.PlaceInHistory(history, "2222222222222222222222222222222222222222"));
        // Only a full commit identifies a build.
        Assert.Equal(UpdateService.Ancestry.Unknown, UpdateService.PlaceInHistory(history, "b500441"));
        Assert.Equal(UpdateService.Ancestry.Unknown, UpdateService.PlaceInHistory(history, ""));
        Assert.Equal(UpdateService.Ancestry.Unknown, UpdateService.PlaceInHistory(null, FullA));
    }

    private static UpdateService.ApiAnswer Ok(string json) => new(HttpStatusCode.OK, false, json);
    private static string Obj(string type, string sha) => "{\"object\":{\"type\":\"" + type + "\",\"sha\":\"" + sha + "\"}}";
    private static string History(params string[] shas) => "[" + string.Join(",", shas.Select(x => "{\"sha\":\"" + x + "\"}")) + "]";
    private const string TagSha1 = "1111111111111111111111111111111111111111";
    private const string TagSha2 = "2222222222222222222222222222222222222222";

    private static Func<string, CancellationToken, Task<UpdateService.ApiAnswer>> Answers(
        List<string> asked, Func<string, UpdateService.ApiAnswer> answer) =>
        (url, ct) => { asked.Add(url); return Task.FromResult(answer(url)); };

    [Fact]
    public async Task TheLookupFollowsTagsToTheCommitAndPlacesThisBuild()
    {
        var asked = new List<string>();
        // A tag that points at a tag that points at the commit.
        var lookup = await UpdateService.LookUpReplacementAsync("v4.5.2", FullA, Answers(asked, url =>
            url.EndsWith("/git/ref/tags/v4.5.2", StringComparison.Ordinal) ? Ok(Obj("tag", TagSha1))
            : url.EndsWith("/git/tags/" + TagSha1, StringComparison.Ordinal) ? Ok(Obj("tag", TagSha2))
            : url.EndsWith("/git/tags/" + TagSha2, StringComparison.Ordinal) ? Ok(Obj("commit", FullB))
            : url.Contains("/commits?sha=" + FullB, StringComparison.Ordinal) ? Ok(History(FullB, FullA))
            : new UpdateService.ApiAnswer(HttpStatusCode.NotFound, false, "")), CancellationToken.None);
        Assert.Null(lookup.Failure);
        Assert.Equal(FullB, lookup.ReleaseCommit);
        Assert.Equal(UpdateService.Ancestry.Ahead, lookup.Place);
        Assert.Equal(4, asked.Count);

        // This very build: no history is read.
        asked.Clear();
        lookup = await UpdateService.LookUpReplacementAsync("v4.5.2", FullB, Answers(asked, url =>
            Ok(Obj("commit", FullB))), CancellationToken.None);
        Assert.Equal(UpdateService.Ancestry.Same, lookup.Place);
        Assert.DoesNotContain(asked, u => u.Contains("/commits?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALookupThatCannotFinishNeverSaysUpToDate()
    {
        var asked = new List<string>();
        // Rate limited at the history.
        var lookup = await UpdateService.LookUpReplacementAsync("v4.5.2", FullA, Answers(asked, url =>
            url.Contains("/commits?", StringComparison.Ordinal)
                ? new UpdateService.ApiAnswer(HttpStatusCode.Forbidden, true, "")
                : Ok(Obj("commit", FullB))), CancellationToken.None);
        Assert.Equal(UpdateCheckOutcome.RateLimited, lookup.Failure);
        // A missing tag.
        lookup = await UpdateService.LookUpReplacementAsync("v4.5.2", FullA, Answers(asked, url =>
            new UpdateService.ApiAnswer(HttpStatusCode.NotFound, false, "")), CancellationToken.None);
        Assert.Equal(UpdateCheckOutcome.Failed, lookup.Failure);
        Assert.Equal("HTTP 404", lookup.Error);
        // A history that does not start at the release commit.
        lookup = await UpdateService.LookUpReplacementAsync("v4.5.2", FullA, Answers(asked, url =>
            url.Contains("/commits?", StringComparison.Ordinal) ? Ok(History(FullA, FullB)) : Ok(Obj("commit", FullB))),
            CancellationToken.None);
        Assert.Equal(UpdateCheckOutcome.Failed, lookup.Failure);
        // A chain of tags longer than any release uses.
        lookup = await UpdateService.LookUpReplacementAsync("v4.5.2", FullA, Answers(asked, url =>
            Ok(Obj("tag", TagSha1))), CancellationToken.None);
        Assert.Equal(UpdateCheckOutcome.Failed, lookup.Failure);
        // A tag that names something other than a commit.
        lookup = await UpdateService.LookUpReplacementAsync("v4.5.2", FullA, Answers(asked, url =>
            Ok(Obj("tree", FullB))), CancellationToken.None);
        Assert.Equal(UpdateCheckOutcome.Failed, lookup.Failure);
    }

    [Fact]
    public void OneHelperAtATimeHoldsAnInstalledExesLease()
    {
        string exe = Path.Combine(Path.GetTempPath(), "PadForgeLease_" + Guid.NewGuid().ToString("N"), "PadForge.exe");
        Assert.True(UpdateService.TryAcquireUpdateLease(exe, out var first));
        try
        {
            Assert.True(UpdateService.IsUpdateInProgress(exe));
            // A second helper for the same exe, from any session, is refused.
            Assert.False(UpdateService.TryAcquireUpdateLease(exe, out var second));
            Assert.Null(second);
        }
        finally
        {
            first.Dispose();
        }
        Assert.False(UpdateService.IsUpdateInProgress(exe));
        Assert.True(UpdateService.TryAcquireUpdateLease(exe, out var again));
        again.Dispose();
    }

    /// <summary>The check pairs a release's tag with its file only while a
    /// second read finds the same release, the same tag and the same file.</summary>
    [Fact]
    public void AReleaseReadTwiceIsTheSameOnlyWithTheSameTagAndFile()
    {
        var a = Parse(StableJson);
        a.Id = 42;
        Assert.True(UpdateService.SameRelease(a, Clone(a), false));
        Assert.True(UpdateService.SameRelease(a, Clone(a), true));
        var remade = Clone(a);
        remade.Id = 43;
        Assert.False(UpdateService.SameRelease(a, remade, false));
        var retagged = Clone(a);
        retagged.TagName = "v4.5.3";
        Assert.False(UpdateService.SameRelease(a, retagged, false));
        var reuploaded = Clone(a);
        reuploaded.Assets.Single(x => x.Name.EndsWith("-win-x64.zip", StringComparison.Ordinal)).Digest =
            "sha256:" + new string('d', 64);
        Assert.False(UpdateService.SameRelease(a, reuploaded, false));
        // The other machine's file changing does not concern this one.
        Assert.True(UpdateService.SameRelease(a, reuploaded, true));
        Assert.False(UpdateService.SameRelease(a, null, false));
        // No file for this machine in either read is the same release, which
        // the check then reports as no build for this PC.
        var bare = Clone(a);
        bare.Assets.RemoveAll(x => x.Name.EndsWith("-win-arm64.zip", StringComparison.Ordinal));
        Assert.True(UpdateService.SameRelease(bare, Clone(bare), true));
        Assert.False(UpdateService.SameRelease(a, bare, true));
        Assert.Equal(UpdateCheckOutcome.NoBuildForThisPc, UpdateService.Evaluate(bare, false, new Version(4, 5, 2),
            3676, "b500441", true, FullB, out _));

        static GitHubRelease Clone(GitHubRelease r) => JsonSerializer.Deserialize<GitHubRelease>(JsonSerializer.Serialize(r));
    }

    [Fact]
    public void ALeaseOnTheInstalledExeKeepsItsLaunchesAway()
    {
        string exe = Path.Combine(Path.GetTempPath(), "PadForgeLease_" + Guid.NewGuid().ToString("N"), "PadForge.exe");
        Assert.False(UpdateService.IsUpdateInProgress(exe));
        using (new Mutex(false, UpdateService.LeaseName(exe)))
        {
            Assert.True(UpdateService.IsUpdateInProgress(exe));
            Assert.True(UpdateService.IsUpdateInProgress(exe.ToUpperInvariant()));
            Assert.False(UpdateService.IsUpdateInProgress(Path.Combine(Path.GetDirectoryName(exe), "Other.exe")));
        }
        Assert.False(UpdateService.IsUpdateInProgress(exe));
        Assert.StartsWith(@"Global\", UpdateService.LeaseName(exe), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOfferIsTheFileNotTheVersion()
    {
        var a = new UpdateOffer("r3700 (abc1234)", true, 3700, "abc1234", null, "u", "PadForge.zip", "u", 1, new string('a', 64));
        var b = a with { Sha256 = new string('b', 64) };
        Assert.False(a.IsSameFile(b));
        Assert.NotEqual(a.StageKey, b.StageKey);
        Assert.StartsWith("r3700-", a.StageKey, StringComparison.Ordinal);
    }

    // ── GitHub's answers ──

    [Theory]
    [InlineData(429, null, null, null, true)]
    [InlineData(403, "0", null, null, true)]
    [InlineData(403, "12", "60", null, true)]
    [InlineData(403, "12", null, "{\"message\":\"You have exceeded a secondary rate limit.\"}", true)]
    [InlineData(403, "12", null, "{\"message\":\"Resource not accessible\"}", false)]
    [InlineData(404, "0", null, null, false)]
    public void RateLimitsAreToldApartFromOtherRefusals(int status, string remaining, string retryAfter, string body, bool limited)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        if (remaining != null) response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining);
        if (retryAfter != null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        Assert.Equal(limited, UpdateService.IsRateLimited(response, body));
    }

    /// <summary>A channel's answer, labeled in its error slot so a test can
    /// see which answer came back.</summary>
    private static (UpdateCheckOutcome, UpdateOffer, string) Result(UpdateCheckOutcome outcome, string label, int major = 4) =>
        (outcome, outcome == UpdateCheckOutcome.Available
            ? new UpdateOffer(label, false, 0, null, new Version(major, 9, 0), "u", "a", "u", 1, new string('c', 64))
            : null, label);

    [Theory]
    [InlineData((int)UpdateCheckOutcome.Available, (int)UpdateCheckOutcome.Available, 0, true)]
    [InlineData((int)UpdateCheckOutcome.Available, (int)UpdateCheckOutcome.Available, 1, false)]
    [InlineData((int)UpdateCheckOutcome.UpToDate, (int)UpdateCheckOutcome.Available, 0, false)]
    [InlineData((int)UpdateCheckOutcome.NoBuildForThisPc, (int)UpdateCheckOutcome.Available, 0, false)]
    [InlineData((int)UpdateCheckOutcome.Failed, (int)UpdateCheckOutcome.Available, 0, false)]
    [InlineData((int)UpdateCheckOutcome.Failed, (int)UpdateCheckOutcome.UpToDate, 0, true)]
    [InlineData((int)UpdateCheckOutcome.NoBuildForThisPc, (int)UpdateCheckOutcome.UpToDate, 0, true)]
    [InlineData((int)UpdateCheckOutcome.Available, (int)UpdateCheckOutcome.Failed, 0, true)]
    [InlineData((int)UpdateCheckOutcome.Available, (int)UpdateCheckOutcome.RateLimited, 0, true)]
    // Up to date on the feed says nothing of a release the other check could not see.
    [InlineData((int)UpdateCheckOutcome.UpToDate, (int)UpdateCheckOutcome.UpToDate, 0, false)]
    [InlineData((int)UpdateCheckOutcome.UpToDate, (int)UpdateCheckOutcome.Failed, 0, false)]
    [InlineData((int)UpdateCheckOutcome.UpToDate, (int)UpdateCheckOutcome.RateLimited, 0, false)]
    [InlineData((int)UpdateCheckOutcome.UpToDate, (int)UpdateCheckOutcome.NoBuildForThisPc, 0, false)]
    public async Task TheDevChannelAlsoHearsOfNewerReleases(int devResult, int releaseResult, int releaseMajorsAhead, bool devWins)
    {
        var dev = (UpdateCheckOutcome)devResult;
        var release = (UpdateCheckOutcome)releaseResult;
        var result = await UpdateService.CheckAsync(true, CancellationToken.None,
            (pre, _) => Task.FromResult(pre ? Result(dev, "dev") : Result(release, "release", BuildIdentity.Version.Major + releaseMajorsAhead)));
        Assert.Equal(devWins ? "dev" : "release", result.Error);
        Assert.Equal(devWins ? dev : release, result.Outcome);
    }

    [Fact]
    public async Task NothingMoreIsAskedAfterARateLimit()
    {
        int calls = 0;
        var result = await UpdateService.CheckAsync(true, CancellationToken.None,
            (_, _) => { calls++; return Task.FromResult(Result(UpdateCheckOutcome.RateLimited, "dev")); });
        Assert.Equal(UpdateCheckOutcome.RateLimited, result.Outcome);
        Assert.Equal(1, calls);

        calls = 0;
        await UpdateService.CheckAsync(false, CancellationToken.None,
            (_, _) => { calls++; return Task.FromResult(Result(UpdateCheckOutcome.UpToDate, "release")); });
        Assert.Equal(1, calls);
    }

    // ── The download ──

    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    [Fact(Timeout = 20000)]
    public async Task ADownloadThatStopsSendingEndsWithAReason()
    {
        using var target = new MemoryStream();
        await Assert.ThrowsAsync<UpdateDownloadStalledException>(() => UpdateService.CopyAndHashAsync(
            new SilentStream(), target, 100, null, TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task ACanceledDownloadIsACancellationNotATimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var target = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateService.CopyAndHashAsync(
            new SilentStream(), target, 100, null, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task TheCopyHashesWhatItWrote()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("PadForge");
        using var target = new MemoryStream();
        var seen = new List<int>();
        string sha = await UpdateService.CopyAndHashAsync(new MemoryStream(bytes), target, bytes.Length,
            new SyncProgress(seen), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), sha);
        Assert.Equal(bytes, target.ToArray());
        Assert.Equal(100, seen[^1]);
    }

    private sealed class SyncProgress : IProgress<int>
    {
        private readonly List<int> _seen;
        public SyncProgress(List<int> seen) => _seen = seen;
        public void Report(int value) => _seen.Add(value);
    }

    [Fact]
    public void ThisBuildKnowsWhichCommitItIs()
    {
        // StampBuildIdentity runs git, so a checkout always stamps both.
        if (!Directory.Exists(Path.Combine(Root(), ".git"))) return;
        Assert.True(BuildIdentity.BuildNumber > 0);
        // At least seven: git lengthens the short hash when seven would be ambiguous.
        Assert.Matches("^[0-9a-f]{7,40}$", BuildIdentity.Commit);
        Assert.Matches("^[0-9a-f]{40}$", BuildIdentity.CommitSha);
        Assert.StartsWith(BuildIdentity.Commit, BuildIdentity.CommitSha, StringComparison.Ordinal);
        Assert.Equal($"{BuildIdentity.VersionText(BuildIdentity.Version)} (r{BuildIdentity.BuildNumber}@{BuildIdentity.Commit})",
            BuildIdentity.Display);
    }

    // ── Settings ──

    [Fact]
    public void TheThreeSettingsPersistWithTheirDefaults()
    {
        var serializer = new XmlSerializer(typeof(AppSettingsData));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new AppSettingsData
        {
            CheckForUpdatesAutomatically = false,
            InstallUpdatesAutomatically = true,
            IncludePreReleaseUpdates = true,
        });
        using var reader = new StringReader(writer.ToString());
        var restored = (AppSettingsData)serializer.Deserialize(reader);
        Assert.False(restored.CheckForUpdatesAutomatically);
        Assert.True(restored.InstallUpdatesAutomatically);
        Assert.True(restored.IncludePreReleaseUpdates);

        // A settings file from before #457: checking is on, the rest off.
        using var older = new StringReader("<AppSettingsData><CloseToTray>true</CloseToTray></AppSettingsData>");
        var old = (AppSettingsData)serializer.Deserialize(older);
        Assert.True(old.CheckForUpdatesAutomatically);
        Assert.False(old.InstallUpdatesAutomatically);
        Assert.False(old.IncludePreReleaseUpdates);

        var vm = new SettingsViewModel();
        Assert.True(vm.CheckForUpdatesAutomatically);
        Assert.False(vm.InstallUpdatesAutomatically);
        Assert.False(vm.IncludePreReleaseUpdates);
        vm.CheckForUpdatesAutomatically = false;
        vm.InstallUpdatesAutomatically = true;
        vm.IncludePreReleaseUpdates = true;
        foreach (string name in new[] { nameof(SettingsViewModel.CheckForUpdatesAutomatically),
                                        nameof(SettingsViewModel.InstallUpdatesAutomatically),
                                        nameof(SettingsViewModel.IncludePreReleaseUpdates) })
        {
            Assert.True(SettingsViewModel.CanResetSetting(name));
            vm.ResetSettingCommand.Execute(name);
        }
        Assert.True(vm.CheckForUpdatesAutomatically);
        Assert.False(vm.InstallUpdatesAutomatically);
        Assert.False(vm.IncludePreReleaseUpdates);
    }

    [Fact]
    public void TheSettingsReachLoadSaveTheDirtyListAndTheCard()
    {
        string settings = Read("PadForge.App/Services/SettingsService.cs");
        string window = Read("PadForge.App/MainWindow.xaml.cs");
        string view = Read("PadForge.App/Views/SettingsPage.xaml");
        string load = Between(settings, "private void LoadAppSettings(", "private AppSettingsData BuildAppSettings(");
        string save = settings[settings.IndexOf("private AppSettingsData BuildAppSettings(", StringComparison.Ordinal)..];
        string dirty = Between(window, "_viewModel.Settings.PropertyChanged +=", "_viewModel.Dashboard.PropertyChanged +=");
        foreach (string name in new[] { "CheckForUpdatesAutomatically", "InstallUpdatesAutomatically", "IncludePreReleaseUpdates" })
        {
            Assert.Contains($"vm.{name} = appSettings.{name};", load);
            Assert.Contains($"{name} = vm.{name},", save);
            Assert.Contains($"nameof(SettingsViewModel.{name})", dirty);
            Assert.Contains($"IsChecked=\"{{Binding {name}}}\"", view);
        }
        foreach (string command in new[] { "CheckForUpdatesNowCommand", "InstallUpdateCommand", "OpenReleaseNotesCommand" })
            Assert.Contains($"Command=\"{{Binding {command}}}\"", view);
    }

    [Fact]
    public void EveryUpdateStringExistsInEveryLocale()
    {
        var files = Directory.GetFiles(Path.Combine(Root(), "PadForge.App/Resources/Strings"), "Strings*.resx");
        Assert.Equal(10, files.Length);
        string designer = Read("PadForge.App/Resources/Strings/Strings.Designer.cs");
        var keys = XDocument.Load(files.Single(f => Path.GetFileName(f) == "Strings.resx")).Root.Elements("data")
            .Select(d => (string)d.Attribute("name"))
            .Where(k => k.StartsWith("Update_", StringComparison.Ordinal)
                || k is "Settings_Updates" or "Settings_UpdatesDesc" or "Settings_CheckForUpdates" or "Settings_CheckForUpdatesTip"
                    or "Settings_InstallUpdatesAutomatically" or "Settings_InstallUpdatesAutomaticallyTip"
                    or "Settings_IncludePreReleases" or "Settings_IncludePreReleasesTip"
                    or "Settings_CheckNow" or "Settings_InstallAndRestart" or "Settings_ReleaseNotes")
            .ToList();
        Assert.Equal(34, keys.Count);
        var english = XDocument.Load(files.Single(f => Path.GetFileName(f) == "Strings.resx")).Root.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name"), d => (string)d.Element("value"));
        foreach (var path in files)
        {
            var resources = XDocument.Load(path).Root.Elements("data").ToList();
            foreach (string key in keys)
            {
                var resource = Assert.Single(resources, node => (string)node.Attribute("name") == key);
                string value = (string)resource.Element("value");
                Assert.False(string.IsNullOrWhiteSpace(value), path + ": " + key);
                if (key.EndsWith("_Format", StringComparison.Ordinal))
                    Assert.Contains("{0}", value);
                Assert.Equal(Placeholders(english[key]), Placeholders(value));
            }
        }

        static string Placeholders(string text) => string.Join(",",
            System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(v => v));
        foreach (string key in keys)
            Assert.Contains($"public string {key} => Get(\"{key}\");", designer);
    }

    private static string Root([CallerFilePath] string me = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(me), ".."));

    private static string Read(string rel) =>
        File.ReadAllText(Path.Combine(Root(), rel.Replace('/', Path.DirectorySeparatorChar)));

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, start);
        int b = text.IndexOf(end, a, StringComparison.Ordinal);
        Assert.True(b > a, end);
        return text[a..b];
    }
}
