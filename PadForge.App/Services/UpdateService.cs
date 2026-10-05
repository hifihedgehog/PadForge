using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>
    /// What this exe is. The release version comes from SharedVersion.cs.
    /// A build made from a git checkout also carries its commit
    /// (StampBuildIdentity in PadForge.App.csproj): the commit count and the
    /// short hash, the two numbers CI names a dev build by ("PadForge
    /// r3676@b500441"), and the full hash. Every build between two releases
    /// reports the same version, so the count is the only way to tell an
    /// older dev build from a newer one (#457), and the full hash is the only
    /// thing that names one build exactly.
    /// </summary>
    internal static class BuildIdentity
    {
        public static Version Version { get; } =
            typeof(BuildIdentity).Assembly.GetName().Version ?? new Version(0, 0, 0);

        /// <summary>The commit count, or 0 for a tree built without git.</summary>
        public static int BuildNumber { get; } = ParseNumber(Read("PadForgeBuildNumber"));

        /// <summary>The seven-character commit hash, or empty. For display,
        /// and for the dev feed, whose titles carry the same seven.</summary>
        public static string Commit { get; } = Read("PadForgeCommit");

        /// <summary>The full 40-character commit hash, or empty.</summary>
        public static string CommitSha { get; } = Read("PadForgeCommitSha");

        /// <summary>"4.5.2 (r3676@b500441)", or "4.5.2" without git.</summary>
        public static string Display =>
            BuildNumber > 0 && Commit.Length > 0
                ? $"{VersionText(Version)} (r{BuildNumber}@{Commit})"
                : VersionText(Version);

        /// <summary>Three parts, the way releases are tagged.</summary>
        internal static string VersionText(Version v) =>
            $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

        internal static int ParseNumber(string text) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

        private static string Read(string key) =>
            typeof(BuildIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? string.Empty;
    }

    internal enum UpdateCheckOutcome
    {
        UpToDate,
        Available,
        /// <summary>The newer release carries no zip for this machine's
        /// processor, which happens when a dev build's ARM64 job fails.</summary>
        NoBuildForThisPc,
        RateLimited,
        Failed,
    }

    /// <summary>A newer build, and exactly which file to fetch for it.</summary>
    internal sealed record UpdateOffer(
        string DisplayVersion,
        bool IsPreRelease,
        int BuildNumber,
        string Commit,
        Version Version,
        string ReleasePageUrl,
        string AssetName,
        string DownloadUrl,
        long Size,
        string Sha256,
        bool SameVersionReplacement = false)
    {
        /// <summary>The staging folder's name, and only a name. What makes
        /// two offers the same file is the full <see cref="Sha256"/>
        /// (<see cref="IsSameFile"/>): a dev build rebuilt at the same count,
        /// or a release replaced under its own version, is another file.</summary>
        public string StageKey => (IsPreRelease
                ? "r" + BuildNumber.ToString(CultureInfo.InvariantCulture)
                : "v" + (Version != null ? BuildIdentity.VersionText(Version) : DisplayVersion))
            + "-" + (Sha256 is { Length: > 12 } ? Sha256[..12] : Sha256);

        public bool IsSameFile(UpdateOffer other) =>
            other != null && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);
    }

    /// <summary>A downloaded, verified exe, and the SHA-256 of the bytes
    /// that were written, so the file can be checked again right before it
    /// runs.</summary>
    internal sealed record StagedUpdate(string ExePath, string ExeSha256);

    /// <summary>The download's hash did not match the one GitHub published
    /// for it, or a staged exe changed after it was written.</summary>
    internal sealed class UpdateVerificationException : Exception
    {
        public UpdateVerificationException() : base("The download did not match its published SHA-256.") { }
    }

    /// <summary>The download stopped sending, or GitHub never answered. The
    /// card words it when it shows it, in the language in use then.</summary>
    internal sealed class UpdateDownloadStalledException : Exception
    {
        public UpdateDownloadStalledException() : base("The download stopped responding.") { }
    }

    internal enum HelperFailure { Exited, NoAnswer, NotStopped, WrongBuild }

    /// <summary>The helper never got as far as a commit, or would not stop.
    /// The card words it when it shows it, in the language in use then.</summary>
    internal sealed class UpdateHelperException : Exception
    {
        public HelperFailure Failure { get; }
        public string ExitCode { get; }

        public UpdateHelperException(HelperFailure failure, string exitCode = null)
            : base("The update helper failed: " + failure)
        {
            Failure = failure;
            ExitCode = exitCode;
        }

        /// <summary>The reason, in the language in use when it is read.</summary>
        public string Reason => Failure switch
        {
            HelperFailure.Exited => string.Format(Strings.Instance.Update_HelperExited_Format, ExitCode ?? "?"),
            HelperFailure.NotStopped => Strings.Instance.Update_HelperNotStopped,
            HelperFailure.WrongBuild => Strings.Instance.Update_HelperWrongBuild,
            _ => Strings.Instance.Update_HelperNoAnswer,
        };
    }

    internal sealed class GitHubRelease
    {
        /// <summary>A new number for a release deleted and made again.</summary>
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("tag_name")] public string TagName { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool PreRelease { get; set; }
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
    }

    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        /// <summary>"sha256:&lt;hex&gt;", which GitHub computes on upload.</summary>
        [JsonPropertyName("digest")] public string Digest { get; set; }
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; }
    }

    /// <summary>A git ref, or an annotated tag, as the API returns it: the
    /// object it points at.</summary>
    internal sealed class GitHubRef
    {
        [JsonPropertyName("object")] public GitHubObject Object { get; set; }
    }

    internal sealed class GitHubObject
    {
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("sha")] public string Sha { get; set; }
    }

    /// <summary>One entry of a commit list.</summary>
    internal sealed class GitHubCommit
    {
        [JsonPropertyName("sha")] public string Sha { get; set; }
    }

    /// <summary>An update downloaded in the background by Install Updates
    /// Automatically, waiting for the next launch.</summary>
    internal sealed class PendingUpdate
    {
        public string DisplayVersion { get; set; }
        public bool IsPreRelease { get; set; }
        public int BuildNumber { get; set; }
        /// <summary>A dev build's commit, so the launch decides "newer" the
        /// way the check did. For a replaced release, its full commit.</summary>
        public string Commit { get; set; }
        public string Version { get; set; }
        /// <summary>Whether Include Pre-Releases was on when this was staged.
        /// A release can be staged with it on, so this is kept apart from
        /// <see cref="IsPreRelease"/>.</summary>
        public bool RequestedPreReleases { get; set; }
        /// <summary>A release replaced under its own version, and the full
        /// commit this build had when that was judged. The launch installs it
        /// only while this build still has that version and that commit.</summary>
        public bool SameVersionReplacement { get; set; }
        public string BaseCommit { get; set; }
        /// <summary>The published zip's full SHA-256: which file this is.</summary>
        public string AssetSha256 { get; set; }
        /// <summary>The installed exe this update replaces.</summary>
        public string TargetPath { get; set; }
        public string ExePath { get; set; }
        public string ExeSha256 { get; set; }
        /// <summary>Set before the helper is started. A launch that finds it
        /// set, with this build still older than the record, knows the last
        /// attempt never finished and does not try again, so a broken
        /// download cannot restart PadForge in a loop.</summary>
        public bool Attempted { get; set; }
    }

    /// <summary>
    /// In-app updates (#457): checks GitHub, downloads and verifies the build
    /// for this machine, and replaces the running exe.
    ///
    /// <para><b>Channels.</b> Releases come from <c>releases/latest</c>,
    /// which GitHub never answers with a pre-release. Pre-releases come from
    /// the rolling dev release CI rewrites on every push to the dev branch,
    /// tagged <c>latest-v{major}-dev</c> and titled
    /// <c>PadForge r{count}@{hash}</c> (.github/workflows/build.yml). That
    /// feed is one per major version, so the dev channel reads
    /// <c>releases/latest</c> as well, and a release of a higher major
    /// version wins over the feed.</para>
    ///
    /// <para><b>Verification.</b> GitHub publishes a SHA-256 for every
    /// release asset over the same HTTPS API the check reads. The download
    /// is hashed as it streams and refused on a mismatch, and an asset with
    /// no digest, or a URL outside this repository's release downloads, is
    /// never offered. Only the zip's root PadForge.exe is extracted, and it
    /// is hashed again right before it runs, so a file damaged after it was
    /// written never starts.</para>
    ///
    /// <para><b>Replacing a running exe.</b> Windows will not overwrite an
    /// exe while it runs. OpenTabletDriver moves the running files aside,
    /// which only works on the same volume and would leave a file beside
    /// PadForge.exe, where nothing but PadForge.xml and crash.log belongs.
    /// DS4Windows hands the swap to a helper that waits for the app to close,
    /// and OpenTabletDriver's elevated path runs its own binary with an update
    /// verb. PadForge does both: the downloaded exe runs from the temp folder
    /// as the helper. It reports ready, this copy commits on its UI thread and
    /// exits, and the helper waits for that exit, keeps a copy of the old exe,
    /// copies itself over it, checks the result and starts it again. A helper
    /// that never reports ready is stopped before anything is committed.
    /// While a helper works, a lease named for the installed exe keeps an
    /// ordinary launch of that exe, and a second helper for it, out of the
    /// way.</para>
    /// </summary>
    internal static class UpdateService
    {
        internal const string Repo = "hifihedgehog/PadForge";
        internal const string ApplyUpdateSwitch = "--apply-update";
        /// <summary>After the PID, marks the exchange that has the ready and
        /// commit handshake. A copy from before it puts the relaunch arguments
        /// there instead.</summary>
        internal const string HandshakeSwitch = "--handshake";
        /// <summary>Put at the front of the relaunch so the new copy can say
        /// it was updated.</summary>
        internal const string UpdatedSwitch = "--updated";
        internal const string ExeName = "PadForge.exe";

        /// <summary>How long a download may go without a byte. A connection
        /// that stops without a reset would otherwise wait forever, and Check
        /// Now and Install and Restart would stay disabled with it.</summary>
        internal static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(60);

        /// <summary>How long the helper has to report ready.</summary>
        internal static readonly TimeSpan HelperReadyTimeout = TimeSpan.FromSeconds(60);

        /// <summary>How long the helper waits between questions while the old
        /// copy is still closing.</summary>
        internal static readonly TimeSpan OldCopyNoticeInterval = TimeSpan.FromMinutes(2);

        /// <summary>How far back a release's history is read to find this
        /// build: one page of the commit list.</summary>
        internal const int HistoryDepth = 100;

        internal static string StableEndpoint => $"https://api.github.com/repos/{Repo}/releases/latest";

        internal static string DevEndpoint(Version current) =>
            $"https://api.github.com/repos/{Repo}/releases/tags/latest-v{current.Major}-dev";

        internal static string TagRefEndpoint(string tag) =>
            $"https://api.github.com/repos/{Repo}/git/ref/tags/{Uri.EscapeDataString(tag)}";

        internal static string TagObjectEndpoint(string sha) =>
            $"https://api.github.com/repos/{Repo}/git/tags/{sha}";

        internal static string HistoryEndpoint(string sha) =>
            $"https://api.github.com/repos/{Repo}/commits?sha={sha}&per_page={HistoryDepth.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>Beside the other download folders PadForge uses
        /// (PadForge_HidHide, PadForge_SteamCmd), never beside the exe.</summary>
        internal static string StagingRoot => Path.Combine(Path.GetTempPath(), "PadForge_Update");

        /// <summary>
        /// One folder per installed copy, named after the exe it replaces. It
        /// holds that copy's pending record, its downloads and the helper's
        /// recovery copies, so two portable copies never install, delete or
        /// reuse each other's files. target.txt names the owner for anyone
        /// who looks.
        /// </summary>
        internal static string InstallDir(string targetExe) => Path.Combine(StagingRoot, InstallKey(targetExe));

        internal static string InstallKey(string targetExe) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizePath(targetExe))), 0, 8)
                .ToLowerInvariant();

        /// <summary>Full and upper-cased, so one file has one name.</summary>
        internal static string NormalizePath(string path) => Path.GetFullPath(path).ToUpperInvariant();

        internal static bool SamePath(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.Ordinal);

        internal static string PendingPathFor(string targetExe) => Path.Combine(InstallDir(targetExe), "pending.json");

        /// <summary>The single record the first release of this feature kept
        /// for every copy, which said nothing of whose it was.</summary>
        private static string LegacyPendingPath => Path.Combine(StagingRoot, "pending.json");

        /// <summary>That release's stage folders sat at the top level:
        /// "r3679", "v4.5.2".</summary>
        private static readonly Regex LegacyStageName = new(@"^[rv]\d", RegexOptions.CultureInvariant);

        private static string ThisExe => Environment.ProcessPath
            ?? throw new InvalidOperationException("The running exe's path is unknown.");

        private static readonly Regex DevTitle = new(@"^PadForge r(\d+)@([0-9a-f]{7,40})$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Lazy<HttpClient> LazyApi = new(() => CreateClient(TimeSpan.FromSeconds(15)));
        // HttpClient's timeout ends once the headers arrive when the body is
        // streamed (HttpClient.FinishSend disposes its timer), so this bounds
        // only the wait for GitHub to answer. CopyAndHashAsync gives every
        // read of the body its own deadline.
        private static readonly Lazy<HttpClient> LazyDownload = new(() => CreateClient(TimeSpan.FromSeconds(30)));

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            // GitHub refuses API calls without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("PadForge", BuildIdentity.VersionText(BuildIdentity.Version)));
            return http;
        }

        // ─────────────────────────────────────────────
        //  Check
        // ─────────────────────────────────────────────

        internal static Task<(UpdateCheckOutcome Outcome, UpdateOffer Offer, string Error)> CheckAsync(
            bool includePreReleases, CancellationToken ct) =>
            CheckAsync(includePreReleases, ct,
                // A replaced release is looked for on the release channel only.
                // On the dev channel the feed's counts already show a newer build.
                (pre, token) => CheckChannelAsync(pre, sameVersionLookup: !includePreReleases, token));

        /// <summary>
        /// The dev channel also reads <c>releases/latest</c>, since the feed
        /// is one per major version and a copy on the old major's feed would
        /// never hear of the next major. A newer release wins when there is
        /// no dev offer, or when its major version is higher than this
        /// build's. "Up to date" is said only when both checks found nothing
        /// newer: a release check that failed, was rate limited or found no
        /// build for this PC says so instead. Nothing more is asked after a
        /// rate-limited answer.
        /// </summary>
        internal static async Task<(UpdateCheckOutcome Outcome, UpdateOffer Offer, string Error)> CheckAsync(
            bool includePreReleases, CancellationToken ct,
            Func<bool, CancellationToken, Task<(UpdateCheckOutcome Outcome, UpdateOffer Offer, string Error)>> checkChannel)
        {
            var result = await checkChannel(includePreReleases, ct).ConfigureAwait(false);
            if (!includePreReleases || result.Outcome == UpdateCheckOutcome.RateLimited)
                return result;
            var release = await checkChannel(false, ct).ConfigureAwait(false);
            if (release.Outcome == UpdateCheckOutcome.Available)
                return result.Outcome != UpdateCheckOutcome.Available
                    || (release.Offer?.Version?.Major ?? 0) > BuildIdentity.Version.Major
                    ? release
                    : result;
            return result.Outcome == UpdateCheckOutcome.UpToDate ? release : result;
        }

        private static async Task<(UpdateCheckOutcome Outcome, UpdateOffer Offer, string Error)> CheckChannelAsync(
            bool preReleaseChannel, bool sameVersionLookup, CancellationToken ct)
        {
            try
            {
                var answer = await GetApiAsync(
                    preReleaseChannel ? DevEndpoint(BuildIdentity.Version) : StableEndpoint, ct).ConfigureAwait(false);
                if (answer.RateLimited)
                    return (UpdateCheckOutcome.RateLimited, null, null);
                if (!answer.Ok)
                    return (UpdateCheckOutcome.Failed, null, answer.StatusText);
                bool arm64 = PadForge.Engine.PlatformSupport.IsArm64Machine;
                var release = JsonSerializer.Deserialize<GitHubRelease>(answer.Body);
                var outcome = Evaluate(release, preReleaseChannel, BuildIdentity.Version,
                    BuildIdentity.BuildNumber, BuildIdentity.Commit, arm64, out var offer);
                if (outcome != UpdateCheckOutcome.UpToDate || preReleaseChannel || !sameVersionLookup
                    || !IsFullSha(BuildIdentity.CommitSha) || !IsThisVersion(release))
                    return (outcome, offer, outcome == UpdateCheckOutcome.Failed ? "unexpected release data" : null);

                // The release has this build's version. The owner re-ships a
                // pulled release that way, so look for this build in the
                // history of the commit the release's tag names now.
                var lookup = await LookUpReplacementAsync(release.TagName, BuildIdentity.CommitSha, ct)
                    .ConfigureAwait(false);
                if (lookup.Failure != null)
                    return (lookup.Failure.Value, null, lookup.Error);
                if (lookup.Place != Ancestry.Ahead)
                    return (UpdateCheckOutcome.UpToDate, null, null);
                // The release, its tag and its file have to be one observation.
                // A release replaced while these requests ran is read again at
                // the next check rather than paired with a commit it never had.
                var again = await GetApiAsync(StableEndpoint, ct).ConfigureAwait(false);
                if (again.RateLimited)
                    return (UpdateCheckOutcome.RateLimited, null, null);
                if (!again.Ok)
                    return (UpdateCheckOutcome.Failed, null, again.StatusText);
                if (!SameRelease(release, JsonSerializer.Deserialize<GitHubRelease>(again.Body), arm64))
                    return (UpdateCheckOutcome.Failed, null, "the release changed during the check");
                outcome = Evaluate(release, false, BuildIdentity.Version, BuildIdentity.BuildNumber,
                    BuildIdentity.Commit, arm64, lookup.ReleaseCommit, out offer);
                return (outcome, offer, outcome == UpdateCheckOutcome.Failed ? "unexpected release data" : null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (UpdateCheckOutcome.Failed, null, ex.Message);
            }
        }

        private static bool IsThisVersion(GitHubRelease release)
        {
            var v = ParseTag(release?.TagName);
            return v != null && CompareVersions(v, BuildIdentity.Version) == 0;
        }

        /// <summary>The same release, under the same tag, with the same file
        /// for this machine, or with none in both reads, which
        /// <see cref="Evaluate"/> then reports as no build for this PC.</summary>
        internal static bool SameRelease(GitHubRelease a, GitHubRelease b, bool arm64Machine)
        {
            if (a == null || b == null || a.Id != b.Id
                || !string.Equals(a.TagName, b.TagName, StringComparison.Ordinal))
                return false;
            var fa = SelectAsset(a.Assets, arm64Machine);
            var fb = SelectAsset(b.Assets, arm64Machine);
            return string.Equals(fa?.Name, fb?.Name, StringComparison.Ordinal)
                && string.Equals(fa?.Digest, fb?.Digest, StringComparison.OrdinalIgnoreCase)
                && string.Equals(fa?.DownloadUrl, fb?.DownloadUrl, StringComparison.Ordinal);
        }

        internal sealed record ApiAnswer(HttpStatusCode Status, bool RateLimited, string Body)
        {
            public bool Ok => (int)Status is >= 200 and <= 299;
            public string StatusText => "HTTP " + ((int)Status).ToString(CultureInfo.InvariantCulture);
        }

        private static async Task<ApiAnswer> GetApiAsync(string url, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await LazyApi.Value.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new ApiAnswer(response.StatusCode, IsRateLimited(response, body), body);
        }

        /// <summary>
        /// GitHub answers a spent quota with 403 or 429 (docs.github.com,
        /// "Rate limits for the REST API"). The primary limit, 60
        /// unauthenticated requests an hour per address, sets
        /// x-ratelimit-remaining to 0. A secondary limit may carry Retry-After,
        /// or only a message that names it. Any other 403 is not a rate limit.
        /// </summary>
        internal static bool IsRateLimited(HttpResponseMessage response, string body)
        {
            if (response.StatusCode == (HttpStatusCode)429) return true;
            if (response.StatusCode != HttpStatusCode.Forbidden) return false;
            if (response.Headers.RetryAfter != null) return true;
            if (response.Headers.TryGetValues("x-ratelimit-remaining", out var values) && values.FirstOrDefault() == "0")
                return true;
            return body != null && body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase);
        }

        internal enum Ancestry { Ahead, Same, Unknown }

        /// <summary>What the lookup found, or why it could not finish.</summary>
        internal sealed record ReplacementLookup(UpdateCheckOutcome? Failure, string Error, string ReleaseCommit, Ancestry Place);

        /// <summary>
        /// Resolves a release tag to its commit, following an annotated tag,
        /// and, unless that commit is this build, reads its recent history.
        /// Only public data leaves the machine: the tag and the commit came
        /// from GitHub, and this build's own commit is looked for here, never
        /// sent. A step that fails or is rate limited ends the lookup with
        /// that answer, never with "up to date".
        /// </summary>
        private static Task<ReplacementLookup> LookUpReplacementAsync(string tag, string currentCommitSha,
            CancellationToken ct) =>
            LookUpReplacementAsync(tag, currentCommitSha, GetApiAsync, ct);

        /// <summary>How many tag objects the lookup follows before it gives
        /// up. Git lets an annotated tag point at another tag.</summary>
        private const int MaxTagDepth = 4;

        internal static async Task<ReplacementLookup> LookUpReplacementAsync(string tag, string currentCommitSha,
            Func<string, CancellationToken, Task<ApiAnswer>> get, CancellationToken ct)
        {
            static ReplacementLookup Stop(ApiAnswer a) => a.RateLimited
                ? new(UpdateCheckOutcome.RateLimited, null, null, Ancestry.Unknown)
                : new(UpdateCheckOutcome.Failed, a.StatusText, null, Ancestry.Unknown);
            static ReplacementLookup Bad(string what) => new(UpdateCheckOutcome.Failed, what, null, Ancestry.Unknown);

            if (string.IsNullOrEmpty(tag)) return Bad("unexpected release data");
            var tagRef = await get(TagRefEndpoint(tag), ct).ConfigureAwait(false);
            if (!tagRef.Ok || tagRef.RateLimited) return Stop(tagRef);
            var obj = JsonSerializer.Deserialize<GitHubRef>(tagRef.Body)?.Object;
            // An annotated tag points at a tag object, which points at the
            // commit, or at another tag.
            for (int depth = 0; obj?.Type == "tag" && IsFullSha(obj.Sha); depth++)
            {
                if (depth == MaxTagDepth) return Bad("unexpected tag data");
                var tagObject = await get(TagObjectEndpoint(obj.Sha), ct).ConfigureAwait(false);
                if (!tagObject.Ok || tagObject.RateLimited) return Stop(tagObject);
                obj = JsonSerializer.Deserialize<GitHubRef>(tagObject.Body)?.Object;
            }
            if (obj?.Type != "commit" || !IsFullSha(obj.Sha)) return Bad("unexpected tag data");
            string releaseCommit = obj.Sha.ToLowerInvariant();
            // A release built from this very commit needs no history.
            if (string.Equals(releaseCommit, currentCommitSha, StringComparison.OrdinalIgnoreCase))
                return new(null, null, releaseCommit, Ancestry.Same);
            var history = await get(HistoryEndpoint(releaseCommit), ct).ConfigureAwait(false);
            if (!history.Ok || history.RateLimited) return Stop(history);
            var commits = JsonSerializer.Deserialize<List<GitHubCommit>>(history.Body);
            if (commits == null || commits.Count == 0 || commits.Any(c => !IsFullSha(c?.Sha))
                || !string.Equals(commits[0].Sha, releaseCommit, StringComparison.OrdinalIgnoreCase))
                return Bad("unexpected history data");
            return new(null, null, releaseCommit, PlaceInHistory(commits.Select(c => c.Sha).ToList(), currentCommitSha));
        }

        internal static bool IsFullSha(string sha) => sha is { Length: 40 } && sha.All(Uri.IsHexDigit);

        /// <summary>
        /// Where this build sits in a release commit's history, newest first,
        /// as GitHub lists it from that commit. First: the release is this
        /// build. Further down: the release descends from this build, so it is
        /// newer. Not in the list: this build is ahead of the release, on
        /// another line of history, or more than a page behind it, and none of
        /// those is a replacement to offer.
        /// </summary>
        internal static Ancestry PlaceInHistory(IReadOnlyList<string> historyNewestFirst, string currentCommitSha)
        {
            if (!IsFullSha(currentCommitSha) || historyNewestFirst == null) return Ancestry.Unknown;
            for (int i = 0; i < historyNewestFirst.Count; i++)
                if (string.Equals(historyNewestFirst[i], currentCommitSha, StringComparison.OrdinalIgnoreCase))
                    return i == 0 ? Ancestry.Same : Ancestry.Ahead;
            return Ancestry.Unknown;
        }

        internal static UpdateCheckOutcome Evaluate(GitHubRelease release, bool preReleaseChannel,
            Version currentVersion, int currentBuild, string currentCommit, bool arm64Machine,
            out UpdateOffer offer) =>
            Evaluate(release, preReleaseChannel, currentVersion, currentBuild, currentCommit, arm64Machine,
                replacementCommit: null, out offer);

        /// <summary>
        /// Decides whether <paramref name="release"/> is newer than the
        /// running build and, when it is, which asset to fetch. No I/O, so
        /// every branch is testable. <paramref name="replacementCommit"/> is
        /// set only when the release has this build's version and its commit
        /// was found to descend from this build's (<see cref="PlaceInHistory"/>):
        /// the owner re-ships a pulled release under its own version.
        /// </summary>
        internal static UpdateCheckOutcome Evaluate(GitHubRelease release, bool preReleaseChannel,
            Version currentVersion, int currentBuild, string currentCommit, bool arm64Machine,
            string replacementCommit, out UpdateOffer offer)
        {
            offer = null;
            if (release == null || release.Draft) return UpdateCheckOutcome.Failed;

            string display;
            int build = 0;
            string commit = null;
            Version version = null;
            bool newer;
            bool replacement = false;
            if (preReleaseChannel)
            {
                var m = DevTitle.Match(release.Name ?? string.Empty);
                if (!m.Success) return UpdateCheckOutcome.Failed;
                build = BuildIdentity.ParseNumber(m.Groups[1].Value);
                if (build <= 0) return UpdateCheckOutcome.Failed;
                commit = m.Groups[2].Value.ToLowerInvariant();
                display = $"r{build} ({ShortCommit(commit)})";
                newer = IsNewerDevBuild(build, commit, currentBuild, currentCommit);
            }
            else
            {
                version = ParseTag(release.TagName);
                if (version == null) return UpdateCheckOutcome.Failed;
                display = BuildIdentity.VersionText(version);
                int order = CompareVersions(version, currentVersion);
                newer = order > 0;
                if (order == 0 && IsFullSha(replacementCommit))
                {
                    newer = replacement = true;
                    commit = replacementCommit.ToLowerInvariant();
                    display = $"{display} ({ShortCommit(commit)})";
                }
            }
            if (!newer) return UpdateCheckOutcome.UpToDate;

            var asset = SelectAsset(release.Assets, arm64Machine);
            if (asset == null) return UpdateCheckOutcome.NoBuildForThisPc;
            string sha = ParseSha256Digest(asset.Digest);
            if (sha == null || !IsTrustedDownloadUrl(asset.DownloadUrl)) return UpdateCheckOutcome.Failed;

            offer = new UpdateOffer(display, preReleaseChannel, build, commit, version,
                release.HtmlUrl, asset.Name, asset.DownloadUrl, asset.Size, sha, replacement);
            return UpdateCheckOutcome.Available;
        }

        /// <summary>
        /// A build with no count (made without git) cannot be placed, so any
        /// published one counts as newer. The same count on another commit is
        /// a rebuilt history, and newer too. The check and the install at the
        /// next launch both ask this, so they never disagree. A pending record
        /// written without a commit, before this rule, gets the strict answer.
        /// </summary>
        internal static bool IsNewerDevBuild(int build, string commit, int currentBuild, string currentCommit) =>
            build > 0
            && (currentBuild <= 0
                || build > currentBuild
                || (build == currentBuild && !string.IsNullOrEmpty(commit) && !SameCommit(commit, currentCommit)));

        private static string ShortCommit(string commit) =>
            commit is { Length: > 7 } ? commit[..7] : commit;

        private static bool SameCommit(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));

        /// <summary>"v4.5.2" or "4.5.2". A suffix such as "-rc1" is refused
        /// rather than guessed at.</summary>
        internal static Version ParseTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string t = tag.Trim();
            if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t.Substring(1);
            return Version.TryParse(t, out var v) && v.Build >= 0 ? v : null;
        }

        /// <summary>Major, minor and patch. The fourth part is always 0 in
        /// SharedVersion.cs and never appears in a tag.</summary>
        internal static int CompareVersions(Version a, Version b)
        {
            int c = a.Major.CompareTo(b.Major);
            if (c != 0) return c;
            c = a.Minor.CompareTo(b.Minor);
            if (c != 0) return c;
            return Math.Max(a.Build, 0).CompareTo(Math.Max(b.Build, 0));
        }

        /// <summary>
        /// The two naming schemes in use: releases publish
        /// PadForge-v{version}-win-x64.zip and -win-arm64.zip, and the dev
        /// feed publishes PadForge.zip and PadForge-arm64.zip. The MACHINE
        /// picks the asset, as it does for the MIDI Services installer, so an
        /// x64 copy running under emulation on ARM64 moves to the native
        /// build.
        /// </summary>
        internal static GitHubAsset SelectAsset(IEnumerable<GitHubAsset> assets, bool arm64Machine)
        {
            foreach (var a in assets ?? Enumerable.Empty<GitHubAsset>())
            {
                if (a?.Name == null) continue;
                bool match = arm64Machine
                    ? a.Name.EndsWith("-win-arm64.zip", StringComparison.OrdinalIgnoreCase)
                        || a.Name.Equals("PadForge-arm64.zip", StringComparison.OrdinalIgnoreCase)
                    : a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)
                        || a.Name.Equals("PadForge.zip", StringComparison.OrdinalIgnoreCase);
                if (match) return a;
            }
            return null;
        }

        /// <summary>Lowercase hex from "sha256:&lt;64 hex&gt;", else null.</summary>
        internal static string ParseSha256Digest(string digest)
        {
            const string prefix = "sha256:";
            if (digest == null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return ParseSha256Hex(digest.Substring(prefix.Length).Trim());
        }

        private static string ParseSha256Hex(string hex)
        {
            if (hex == null || hex.Length != 64) return null;
            foreach (char c in hex)
                if (!Uri.IsHexDigit(c)) return null;
            return hex.ToLowerInvariant();
        }

        /// <summary>Only this repository's release downloads, over HTTPS.
        /// GitHub redirects them to its asset host, which HttpClient follows.</summary>
        internal static bool IsTrustedDownloadUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith($"/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);

        // ─────────────────────────────────────────────
        //  Download
        // ─────────────────────────────────────────────

        /// <summary>
        /// Downloads the offer into its folder in this copy's staging folder,
        /// checks the SHA-256 as the bytes arrive, and extracts PadForge.exe.
        /// Progress is reported in whole percent.
        /// </summary>
        internal static async Task<StagedUpdate> DownloadAsync(UpdateOffer offer, IProgress<int> progress, CancellationToken ct)
        {
            if (offer == null) throw new ArgumentNullException(nameof(offer));
            // The cleanup deletes every staging folder it does not recognize,
            // and a download started while it still runs would be one of them.
            try { await _cleanup.ConfigureAwait(false); } catch { }
            string dir = Path.Combine(EnsureInstallDir(ThisExe), offer.StageKey);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "download.zip");
            try
            {
                using (var response = await LazyDownload.Value.GetAsync(offer.DownloadUrl,
                           HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? offer.Size;
                    await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var target = new FileStream(zip, FileMode.Create, FileAccess.Write,
                        FileShare.None, 81920, useAsync: true);
                    string got = await CopyAndHashAsync(source, target, total, progress, DownloadStallTimeout, ct)
                        .ConfigureAwait(false);
                    if (got != offer.Sha256)
                        throw new UpdateVerificationException();
                }

                string exe = Path.Combine(dir, ExeName);
                string exeSha = ExtractExe(zip, exe);
                File.Delete(zip);
                return new StagedUpdate(exe, exeSha);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient's own timeout, while it waited for GitHub's answer.
                TryDeleteDirectory(dir);
                throw new UpdateDownloadStalledException();
            }
            catch
            {
                TryDeleteDirectory(dir);
                throw;
            }
        }

        /// <summary>
        /// Copies <paramref name="source"/> to <paramref name="target"/>,
        /// hashing as it goes, and returns the lowercase SHA-256. Every read
        /// has its own deadline and its own token source, disposed with it, so
        /// the deadline never runs while the bytes are written or hashed.
        /// </summary>
        internal static async Task<string> CopyAndHashAsync(Stream source, Stream target, long total,
            IProgress<int> progress, TimeSpan stallTimeout, CancellationToken ct)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long done = 0;
            int lastPercent = -1;
            while (true)
            {
                int read;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    stall.CancelAfter(stallTimeout);
                    try
                    {
                        read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new UpdateDownloadStalledException();
                    }
                }
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (total > 0)
                {
                    int percent = (int)Math.Min(100, done * 100 / total);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(percent);
                    }
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        /// <summary>The zip's root PadForge.exe and nothing else. Returns the
        /// SHA-256 of the bytes written, hashed as they are written, so the
        /// hash describes what this process put there.</summary>
        internal static string ExtractExe(string zipPath, string exePath)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals(ExeName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                throw new InvalidDataException("The download has no " + ExeName + ".");
            using var source = entry.Open();
            using var target = new FileStream(exePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                target.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        // ─────────────────────────────────────────────
        //  Install at next launch
        // ─────────────────────────────────────────────

        internal static void WritePending(UpdateOffer offer, StagedUpdate staged, bool requestedPreReleases)
        {
            string self = ThisExe;
            EnsureInstallDir(self);
            WritePendingFile(PendingPathFor(self), new PendingUpdate
            {
                DisplayVersion = offer.DisplayVersion,
                IsPreRelease = offer.IsPreRelease,
                BuildNumber = offer.BuildNumber,
                Commit = offer.Commit,
                Version = offer.Version != null ? BuildIdentity.VersionText(offer.Version) : null,
                RequestedPreReleases = requestedPreReleases,
                SameVersionReplacement = offer.SameVersionReplacement,
                BaseCommit = offer.SameVersionReplacement ? BuildIdentity.CommitSha : null,
                AssetSha256 = offer.Sha256,
                TargetPath = Path.GetFullPath(self),
                ExePath = staged.ExePath,
                ExeSha256 = staged.ExeSha256,
            });
        }

        /// <summary>Written beside the record and moved over it, so a crash
        /// never leaves half a record.</summary>
        private static void WritePendingFile(string path, PendingUpdate pending)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(pending));
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>This copy's pending update, or null.</summary>
        internal static PendingUpdate ReadPending() => ReadPendingFile(PendingPathFor(ThisExe));

        internal static PendingUpdate ReadPendingFile(string path) =>
            ReadPendingRecord(path, out var pending) == RecordRead.Read ? pending : null;

        internal enum RecordRead { Missing, Read, Unreadable, Corrupt }

        /// <summary>A record that cannot be read now, because antivirus or a
        /// backup holds it, is not a missing one: its download must stay. One
        /// that reads but does not parse was not written by PadForge, which
        /// writes a whole record or none (<see cref="WritePendingFile"/>).</summary>
        internal static RecordRead ReadPendingRecord(string path, out PendingUpdate pending)
        {
            pending = null;
            string text;
            try
            {
                // File.Exists answers false when it may not look, which is not
                // the same as absent, so the read itself decides.
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return RecordRead.Missing;
            }
            catch
            {
                return RecordRead.Unreadable;
            }
            try
            {
                pending = JsonSerializer.Deserialize<PendingUpdate>(text);
                return pending != null ? RecordRead.Read : RecordRead.Corrupt;
            }
            catch
            {
                return RecordRead.Corrupt;
            }
        }

        /// <summary>Deletes this copy's pending record, and throws when it
        /// cannot, because a record left behind installs at the next launch.</summary>
        internal static void DeletePending()
        {
            string path = PendingPathFor(ThisExe);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>
        /// Whether a staged update is newer than the running build. A release
        /// replaced under its own version was judged against the build this
        /// copy was then, so it is still newer only while this copy has that
        /// version and is still that exact commit.
        /// </summary>
        internal static bool IsNewerThan(PendingUpdate pending, Version currentVersion, int currentBuild,
            string currentCommit, string currentCommitSha)
        {
            if (pending == null) return false;
            if (pending.IsPreRelease)
                return IsNewerDevBuild(pending.BuildNumber, pending.Commit, currentBuild, currentCommit);
            var v = ParseTag(pending.Version);
            if (v == null) return false;
            int order = CompareVersions(v, currentVersion);
            if (order > 0) return true;
            return order == 0 && pending.SameVersionReplacement
                && IsFullSha(pending.BaseCommit)
                && string.Equals(pending.BaseCommit, currentCommitSha, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The update preferences as saved in the settings file.</summary>
        internal readonly record struct UpdatePreferences(bool Check, bool Install, bool PreReleases);

        /// <summary>
        /// The update preferences as saved in the settings file, read before
        /// anything else loads. A missing file or element takes
        /// AppSettingsData's default: checking on, installing off, releases
        /// only. A value XmlSerializer would refuse makes the file unreadable,
        /// which returns null.
        /// </summary>
        internal static UpdatePreferences? ReadUpdatePreferences(string settingsPath)
        {
            bool check = true, install = false, pre = false;
            try
            {
                if (string.IsNullOrEmpty(settingsPath) || !File.Exists(settingsPath))
                    return new UpdatePreferences(check, install, pre);
                var app = XDocument.Load(settingsPath).Root?.Element("AppSettings");
                if (app != null)
                {
                    check = Bool(app, "CheckForUpdatesAutomatically", check);
                    install = Bool(app, "InstallUpdatesAutomatically", install);
                    pre = Bool(app, "IncludePreReleaseUpdates", pre);
                }
                return new UpdatePreferences(check, install, pre);
            }
            catch
            {
                return null;
            }

            static bool Bool(XElement app, string name, bool fallback)
            {
                var e = app.Element(name);
                return e == null ? fallback : XmlConvert.ToBoolean(e.Value.Trim());
            }
        }

        /// <summary>A pending install that started and never finished, set
        /// by <see cref="TryStartPendingInstall"/> for the window to report.</summary>
        internal static string LastPendingFailure { get; set; }

        /// <summary>Why it did not finish, when the launch saw why. A helper
        /// that would not stop is reported as such.</summary>
        internal static Exception LastPendingError { get; set; }

        internal enum PendingAction { None, Install, Discard, DiscardAndReport }

        /// <summary>
        /// What the launch does with this copy's pending record. A record for
        /// a build this one already is, or has passed, goes quietly, even one
        /// marked attempted: that attempt worked, and only deleting the record
        /// failed. An attempt that did not work is reported and dropped. The
        /// saved preferences come next, read only when needed: unreadable
        /// leaves the record for the next launch, and either switch off, or
        /// another channel than the one it was staged for, drops it. A record
        /// for another exe, or whose exe is gone, is dropped too.
        /// </summary>
        internal static PendingAction DecidePending(PendingUpdate pending, Func<UpdatePreferences?> preferences,
            string self, Version currentVersion, int currentBuild, string currentCommit, string currentCommitSha,
            Func<string, bool> fileExists)
        {
            if (pending == null) return PendingAction.None;
            if (!IsNewerThan(pending, currentVersion, currentBuild, currentCommit, currentCommitSha))
                return PendingAction.Discard;
            if (pending.Attempted) return PendingAction.DiscardAndReport;
            var prefs = preferences();
            if (prefs == null) return PendingAction.None;
            if (!prefs.Value.Check || !prefs.Value.Install || prefs.Value.PreReleases != pending.RequestedPreReleases)
                return PendingAction.Discard;
            if (!SamePath(pending.TargetPath, self) || string.IsNullOrEmpty(pending.ExePath) || !fileExists(pending.ExePath))
                return PendingAction.Discard;
            return PendingAction.Install;
        }

        /// <summary>
        /// Called at launch, after the single-instance check and before
        /// anything else starts. Returns true when the helper has committed
        /// to the swap and this process has to exit at once.
        /// </summary>
        internal static bool TryStartPendingInstall(string[] args)
        {
            // The first release of this feature kept one record for every copy
            // and said nothing of whose it was, so no copy can claim it.
            TryDeleteFile(LegacyPendingPath);
            string path;
            PendingUpdate pending;
            PendingAction action;
            try
            {
                string self = ThisExe;
                path = PendingPathFor(self);
                pending = ReadPendingFile(path);
                // The preferences this copy saved decide, whatever deleting the
                // record when they changed managed to do. A file changed since
                // it was written is refused by StartHelper, which checks it
                // again as it runs.
                action = DecidePending(pending, () => ReadUpdatePreferences(SettingsService.FindSettingsFile()),
                    self, BuildIdentity.Version, BuildIdentity.BuildNumber, BuildIdentity.Commit,
                    BuildIdentity.CommitSha, File.Exists);
            }
            catch
            {
                // A record PadForge did not write, with a path that is not a
                // path. PadForge starts without it, whatever it says.
                try { TryDeleteFile(PendingPathFor(ThisExe)); } catch { }
                return false;
            }
            if (action == PendingAction.DiscardAndReport) LastPendingFailure = pending.DisplayVersion;
            if (action is PendingAction.Discard or PendingAction.DiscardAndReport) TryDeleteFile(path);
            if (action != PendingAction.Install) return false;
            try
            {
                pending.Attempted = true;
                WritePendingFile(path, pending);
                using var helper = StartHelper(new StagedUpdate(pending.ExePath, pending.ExeSha256), pending.Commit,
                    args, CancellationToken.None);
                helper.Commit();
                return true;
            }
            catch (Exception ex)
            {
                LastPendingFailure = pending.DisplayVersion;
                LastPendingError = ex;
                TryDeleteFile(path);
                return false;
            }
        }

        // ─────────────────────────────────────────────
        //  Install: this copy's side
        // ─────────────────────────────────────────────

        internal enum HelperStart { Ready, Exited, TimedOut, Canceled }

        internal static string ReadyEventName(string attempt) => @"Local\PadForge_Update_" + attempt + "_Ready";

        internal static string CommitEventName(string attempt) => @"Local\PadForge_Update_" + attempt + "_Commit";

        /// <summary>A helper that reported ready and waits for this copy's
        /// word. <see cref="Commit"/> hands it the swap, and this copy has to
        /// exit after it, since the helper waits for that. <see cref="Abort"/>,
        /// or disposing it uncommitted, stops the helper.</summary>
        internal interface IHelperHandle : IDisposable
        {
            void Commit();

            /// <summary>Stops the helper. Throws <see cref="UpdateHelperException"/>
            /// when it cannot be confirmed stopped.</summary>
            void Abort();
        }

        private sealed class HelperHandle : IHelperHandle
        {
            private readonly Process _helper;
            private readonly EventWaitHandle _ready;
            private readonly EventWaitHandle _commit;
            private bool _committed;
            private bool _stopTried;
            private bool _disposed;

            internal HelperHandle(Process helper, EventWaitHandle ready, EventWaitHandle commit)
            {
                _helper = helper;
                _ready = ready;
                _commit = commit;
            }

            public void Commit()
            {
                _commit.Set();
                _committed = true;
            }

            public void Abort()
            {
                _stopTried = true;
                StopHelper(_helper);
            }

            /// <summary>Stops a helper that was neither committed nor asked
            /// to stop, and throws <see cref="UpdateHelperException"/> when it
            /// cannot be confirmed stopped, so the caller can say so. The
            /// handles close either way.</summary>
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    // A committed helper runs on after this copy exits, and one
                    // already asked to stop is not waited for twice.
                    if (!_committed && !_stopTried) StopHelper(_helper);
                }
                finally
                {
                    _helper.Dispose();
                    _ready.Dispose();
                    _commit.Dispose();
                }
            }
        }

        /// <summary>
        /// Starts the downloaded exe as the helper that replaces this one, and
        /// returns once it reports ready, uncommitted. <paramref name="relaunchArgs"/>
        /// are the arguments this process was started with, so a copy a
        /// launcher started with --profile comes back with it.
        ///
        /// <para>Before the helper starts, PadForge hashes the file through a
        /// handle that denies writes and deletion and compares it with the
        /// hash recorded when it was written, and the handle stays open while
        /// it launches.</para>
        ///
        /// <para>The helper gets <see cref="HelperReadyTimeout"/> to report
        /// ready through a pair of events named for this attempt. If it does
        /// not, or <paramref name="ct"/> is canceled first, the helper is
        /// stopped and this throws, so the caller keeps running and says why.
        /// Any other failure once it has started stops it too. A helper from
        /// before the handshake never reports ready, so it is stopped the same
        /// way, as long as this copy lives to stop it.</para>
        ///
        /// <para><paramref name="expectedCommit"/> is the commit the offer
        /// named, when it named one: the helper checks it against its own
        /// build and refuses to be anything else. A release tag moved before
        /// its file was replaced, or a dev title published before its zip,
        /// is caught there.</para>
        /// </summary>
        internal static IHelperHandle StartHelper(StagedUpdate staged, string expectedCommit,
            IEnumerable<string> relaunchArgs, CancellationToken ct)
        {
            string target = ThisExe;
            string attempt = Guid.NewGuid().ToString("N");
            var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName(attempt), out bool readyIsNew);
            var commit = new EventWaitHandle(false, EventResetMode.ManualReset, CommitEventName(attempt), out bool commitIsNew);
            Process helper = null;
            bool handedOver = false;
            bool stopped = false;
            try
            {
                if (!readyIsNew || !commitIsNew)
                    throw new UpdateHelperException(HelperFailure.NoAnswer);
                using (var pin = new FileStream(staged.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string sha = Convert.ToHexString(SHA256.HashData(pin)).ToLowerInvariant();
                    if (!string.Equals(sha, staged.ExeSha256, StringComparison.Ordinal))
                        throw new UpdateVerificationException();
                    ct.ThrowIfCancellationRequested();
                    var psi = new ProcessStartInfo(staged.ExePath)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(staged.ExePath),
                    };
                    foreach (string a in BuildInstallerArgs(target, Environment.ProcessId, attempt, sha, expectedCommit,
                                 relaunchArgs))
                        psi.ArgumentList.Add(a);
                    helper = Process.Start(psi) ?? throw new UpdateHelperException(HelperFailure.NoAnswer);
                }
                var result = WaitForHelper(ms => ready.WaitOne(ms), () => helper.HasExited, HelperReadyTimeout, ct);
                if (result == HelperStart.Ready)
                {
                    handedOver = true;
                    return new HelperHandle(helper, ready, commit);
                }
                // Not ready, gone, or no longer wanted: nothing may install
                // behind this copy, which keeps running.
                stopped = true;
                StopHelper(helper);
                ct.ThrowIfCancellationRequested();
                if (result != HelperStart.Exited)
                    throw new UpdateHelperException(HelperFailure.NoAnswer);
                string code = SafeExitCode(helper);
                throw code == WrongBuildExitCode.ToString(CultureInfo.InvariantCulture)
                    ? new UpdateHelperException(HelperFailure.WrongBuild)
                    : new UpdateHelperException(HelperFailure.Exited, code);
            }
            catch when (helper != null && !stopped)
            {
                // Anything else that went wrong once the helper started. It is
                // stopped before this copy moves on, and a helper that will
                // not stop is what gets reported.
                stopped = true;
                StopHelper(helper);
                throw;
            }
            finally
            {
                if (!handedOver)
                {
                    helper?.Dispose();
                    ready.Dispose();
                    commit.Dispose();
                }
            }
        }

        /// <summary>Kills the helper and waits for it, ten seconds at most.
        /// Throws when it cannot be confirmed stopped, since a helper still
        /// running from before the handshake installs once this copy exits.</summary>
        private static void StopHelper(Process helper)
        {
            try
            {
                if (helper.HasExited) return;
                helper.Kill();
                if (helper.WaitForExit(10000)) return;
            }
            catch (InvalidOperationException)
            {
                return; // it exited on its own
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            throw new UpdateHelperException(HelperFailure.NotStopped);
        }

        private static string SafeExitCode(Process process)
        {
            try { return process.ExitCode.ToString(CultureInfo.InvariantCulture); }
            catch { return "?"; }
        }

        /// <summary>
        /// Waits for the helper to report ready. Its exit comes first in each
        /// round: a helper that reported ready and then exited has given up,
        /// so it is not ready.
        /// </summary>
        internal static HelperStart WaitForHelper(Func<int, bool> waitReady, Func<bool> exited, TimeSpan timeout,
            CancellationToken ct)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (true)
            {
                if (ct.IsCancellationRequested) return HelperStart.Canceled;
                if (exited()) return HelperStart.Exited;
                if (waitReady(100))
                {
                    if (exited()) return HelperStart.Exited;
                    return ct.IsCancellationRequested ? HelperStart.Canceled : HelperStart.Ready;
                }
                if (Environment.TickCount64 >= deadline) return HelperStart.TimedOut;
            }
        }

        /// <summary><c>--apply-update &lt;target&gt; &lt;pid&gt; --handshake
        /// &lt;attempt&gt; &lt;SHA-256&gt; &lt;expected commit, or -&gt; [relaunch
        /// args]</c>. A marker left by the last update is not carried again.</summary>
        internal static List<string> BuildInstallerArgs(string target, int pid, string attempt, string payloadSha256,
            string expectedCommit, IEnumerable<string> relaunchArgs)
        {
            var args = new List<string>
            {
                ApplyUpdateSwitch, target, pid.ToString(CultureInfo.InvariantCulture),
                HandshakeSwitch, attempt, payloadSha256, IsCommitName(expectedCommit) ? expectedCommit : NoCommit,
            };
            args.AddRange(WithoutUpdatedMarkers(relaunchArgs));
            return args;
        }

        /// <summary>Stands for "the offer named no commit".</summary>
        private const string NoCommit = "-";

        /// <summary>The helper's exit code when it is not the build the offer
        /// named.</summary>
        internal const int WrongBuildExitCode = 3;

        /// <summary>A short or full commit hash, the way a dev title or a
        /// release lookup gives one.</summary>
        private static bool IsCommitName(string text) =>
            text is { Length: >= 7 and <= 40 } && text.All(Uri.IsHexDigit);

        /// <summary>Whether this build is the one <paramref name="expected"/>
        /// names. A build with no commit stamped is no named build.</summary>
        internal static bool IsExpectedBuild(string expected, string commitSha, string commit)
        {
            if (expected == NoCommit) return true;
            string mine = commitSha is { Length: > 0 } ? commitSha : commit;
            // This build's name has to be at least as long as the expected one
            // and start with it: a short hash cannot prove a full one.
            return IsCommitName(expected) && mine != null && mine.Length >= expected.Length
                && mine.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether the arguments carry the marker the helper adds when it
        /// starts the updated copy. They are read the way
        /// <see cref="App.ParseProfileCommand"/> reads them: --profile takes
        /// the next argument as its value when there is one, and every
        /// --updated that is not such a value is the marker. A profile named
        /// "--updated" stays a profile. The helper puts the marker first, so a
        /// --profile left without a value at the end stays without one.
        /// </summary>
        internal static bool HasUpdatedMarker(IReadOnlyList<string> args) => MarkerPositions(args).Count > 0;

        internal static List<string> WithoutUpdatedMarkers(IEnumerable<string> args)
        {
            var list = (args ?? Enumerable.Empty<string>()).ToList();
            var at = MarkerPositions(list);
            for (int i = at.Count - 1; i >= 0; i--) list.RemoveAt(at[i]);
            return list;
        }

        private static List<int> MarkerPositions(IReadOnlyList<string> args)
        {
            var found = new List<int>();
            if (args == null) return found;
            for (int i = 0; i < args.Count; i++)
            {
                if (string.Equals(args[i], "--profile", StringComparison.OrdinalIgnoreCase))
                {
                    i++; // its value, whatever it says
                    continue;
                }
                if (string.Equals(args[i], UpdatedSwitch, StringComparison.OrdinalIgnoreCase))
                    found.Add(i);
            }
            return found;
        }

        // ─────────────────────────────────────────────
        //  The update lease
        // ─────────────────────────────────────────────

        /// <summary>A named mutex for the installed exe, held by a helper from
        /// before it reports ready until it starts PadForge again. Global, so
        /// a copy launched in another session sees it too. The name lives
        /// only while the helper holds it, and a helper that crashes lets it
        /// go.</summary>
        internal static string LeaseName(string targetExe) => @"Global\PadForge_UpdateLease_" + InstallKey(targetExe);

        /// <summary>Takes the lease when no other helper holds it. Creating
        /// the name is the whole test, and Windows makes it atomic.</summary>
        internal static bool TryAcquireUpdateLease(string targetExe, out Mutex lease)
        {
            lease = null;
            Mutex mutex;
            bool createdNew;
            try
            {
                mutex = new Mutex(false, LeaseName(targetExe), out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                return false; // it exists, and belongs to someone else
            }
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }
            lease = mutex;
            return true;
        }

        /// <summary>Whether a helper is replacing <paramref name="exe"/> now.
        /// An ordinary launch then leaves at once: the helper starts PadForge
        /// itself when it is done, and a copy running meanwhile would hold the
        /// very file being replaced.</summary>
        internal static bool IsUpdateInProgress(string exe)
        {
            try
            {
                if (string.IsNullOrEmpty(exe) || !Mutex.TryOpenExisting(LeaseName(exe), out var lease)) return false;
                lease.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true; // it exists, and belongs to someone else
            }
            catch
            {
                return false;
            }
        }

        private static void ReleaseLease(ref Mutex lease)
        {
            lease?.Dispose();
            lease = null;
        }

        // ─────────────────────────────────────────────
        //  Install: the helper's side
        // ─────────────────────────────────────────────

        /// <summary>What the helper shows. It runs before any window exists.</summary>
        internal interface IApplyUi
        {
            /// <summary>A failure, with OK.</summary>
            void Report(string text);

            /// <summary>Asks whether to keep waiting for the old copy. True
            /// for OK, and when the question closed itself because
            /// <paramref name="exited"/> turned true. False to skip the
            /// update.</summary>
            bool KeepWaiting(string text, Func<bool> exited);
        }

        internal enum WaitResult { Exited, Skipped }

        /// <summary>
        /// Waits for the old copy to exit. A slow shutdown is not the end of
        /// the wait: every <paramref name="interval"/> the user is asked, one
        /// question at a time, and the question closes itself once the old
        /// copy exits. Skipping the update gives the same answer whenever the
        /// old copy happens to exit.
        /// </summary>
        internal static WaitResult WaitForOldCopy(Func<int, bool> waitExit, Func<Func<bool>, bool> keepWaiting,
            TimeSpan interval)
        {
            while (!waitExit((int)interval.TotalMilliseconds))
            {
                if (!keepWaiting(() => waitExit(0)))
                    return WaitResult.Skipped;
            }
            return WaitResult.Exited;
        }

        /// <summary>Where the helper is, which decides what a failure owes.</summary>
        private enum HelperPhase
        {
            /// <summary>No commit yet. Nothing installs and nothing restarts:
            /// the old copy runs on, or was closed on purpose.</summary>
            NotAuthorized,
            /// <summary>Committed, or started by a copy from before the
            /// handshake, and waiting for the old copy to exit. It is closing,
            /// so PadForge is owed a restart whatever happens next.</summary>
            WaitingForExit,
            /// <summary>The old copy has exited and nothing has touched the
            /// installed exe.</summary>
            TargetUntouched,
            /// <summary>The installed exe may be half written.</summary>
            Swapping,
            /// <summary>The swap is over, and its outcome has been acted on.</summary>
            Finished,
        }

        private static string InstallFailedText(string reason) =>
            string.Format(Strings.Instance.Update_InstallFailed_Format, reason);

        private static void Tell(IApplyUi ui, string text)
        {
            try { ui?.Report(text); } catch { }
        }

        /// <summary>
        /// Runs in the downloaded exe when an old copy started it. Returns
        /// false for any other launch, which then starts PadForge as usual.
        /// Once the first argument is the update switch, this process is the
        /// helper whatever else the arguments say, and it never goes on to
        /// start PadForge from the staging folder.
        /// </summary>
        internal static bool TryRunApplyMode(string[] args, IApplyUi ui)
        {
            if (args == null || args.Length == 0
                || !string.Equals(args[0], ApplyUpdateSwitch, StringComparison.Ordinal))
                return false;
            bool handshake = args.Length > 3 && string.Equals(args[3], HandshakeSwitch, StringComparison.Ordinal);
            if (handshake)
            {
                // The old copy waits for this helper and reports its failure.
                if (args.Length < 7 || !Guid.TryParseExact(args[4], "N", out _) || ParseSha256Hex(args[5]) == null
                    || !(args[6] == NoCommit || IsCommitName(args[6])))
                    return true;
                if (!IsExpectedBuild(args[6], BuildIdentity.CommitSha, BuildIdentity.Commit))
                {
                    // Not the build the offer named: the old copy reads this
                    // exit code and says so.
                    Environment.ExitCode = WrongBuildExitCode;
                    return true;
                }
            }
            else if (args.Length < 3)
            {
                Tell(ui, InstallFailedText("The update was started without a target."));
                return true;
            }

            string target = args[1];
            int pid = BuildIdentity.ParseNumber(args[2]);
            string attempt = handshake ? args[4] : Guid.NewGuid().ToString("N");
            var relaunch = WithoutUpdatedMarkers(args.Skip(handshake ? 7 : 3));
            var phase = HelperPhase.NotAuthorized;
            Process parent = null;
            // Whether the old copy is known to be gone: OpenOldCopy found no
            // such process, which is not the same as failing to open it.
            bool parentGone = false;
            Mutex lease = null;
            string recoveryDir = null;
            try
            {
                if (string.IsNullOrEmpty(target) || !Path.IsPathFullyQualified(target) || !File.Exists(target))
                    throw new ArgumentException("The update was started without a target.");
                string self = ThisExe;
                if (SamePath(self, target))
                    throw new ArgumentException("The update cannot replace itself.");
                // A copy from before the handshake cannot send a commit, and
                // exits without waiting for an answer: starting the helper was
                // its commit.
                if (!handshake) phase = HelperPhase.WaitingForExit;
                parent = OpenOldCopy(pid, Path.GetFileNameWithoutExtension(target));
                parentGone = parent == null;
                string payloadSha = handshake ? args[5].ToLowerInvariant() : HashFile(self);

                // One transaction per installed exe, across sessions: a second
                // helper for the same exe finds the lease taken and stops here,
                // before it reports ready, so its old copy runs on.
                if (!TryAcquireUpdateLease(target, out lease))
                {
                    if (!handshake) Tell(ui, InstallFailedText("Another update of this copy of PadForge is running."));
                    return true;
                }
                if (handshake)
                {
                    if (parent == null || !AwaitCommit(attempt, parent)) return true;
                    phase = HelperPhase.WaitingForExit;
                }
                if (parent != null)
                {
                    string question = string.Format(Strings.Instance.Update_WaitingForClose_Format,
                        Strings.Instance.Common_OK, Strings.Instance.Common_Cancel);
                    var wait = WaitForOldCopy(ms => parent.WaitForExit(ms),
                        exited => ui?.KeepWaiting(question, exited) ?? true, OldCopyNoticeInterval);
                    if (wait == WaitResult.Skipped)
                    {
                        // The old copy is closing and cannot be stopped, so
                        // PadForge starts again, as it was, once it has.
                        parent.WaitForExit();
                        phase = HelperPhase.Finished;
                        DropPendingRecords(target);
                        ReleaseLease(ref lease);
                        Relaunch(target, relaunch, ui, backupDir: null);
                        return true;
                    }
                }
                phase = HelperPhase.TargetUntouched;
                recoveryDir = Path.Combine(EnsureInstallDir(target), "recovery-" + attempt);
                phase = HelperPhase.Swapping;
                var outcome = Swap(self, target, payloadSha, recoveryDir, DiskFiles.Instance,
                    attempts: 60, retryDelay: TimeSpan.FromMilliseconds(500));
                phase = HelperPhase.Finished;
                // This attempt is over, and what became of it is said here, so
                // no launch reports it again.
                DropPendingRecords(target);
                ReleaseLease(ref lease);
                switch (outcome.Result)
                {
                    case SwapResult.Installed:
                        relaunch.Insert(0, UpdatedSwitch);
                        // The old exe's copy stays until the new exe has run
                        // long enough to clean up (UpdateController).
                        Relaunch(target, relaunch, ui, backupDir: outcome.RecoveryDir);
                        break;
                    case SwapResult.NotChanged:
                    case SwapResult.Restored:
                        Tell(ui, InstallFailedText(outcome.Error));
                        Relaunch(target, relaunch, ui, backupDir: null);
                        break;
                    default:
                        Tell(ui, string.Format(Strings.Instance.Update_RestoreFailed_Format, outcome.RecoveryDir));
                        break;
                }
                return true;
            }
            catch (Exception ex)
            {
                switch (phase)
                {
                    case HelperPhase.NotAuthorized when !handshake:
                        // No old copy waits to report it. The target is not one
                        // to act on, so no record is touched either.
                        Tell(ui, InstallFailedText(ex.Message));
                        break;
                    case HelperPhase.WaitingForExit:
                    case HelperPhase.TargetUntouched:
                        Tell(ui, InstallFailedText(ex.Message));
                        DropPendingRecords(target);
                        // Nothing touched the installed exe. It starts again
                        // only once the old copy is known to have exited: one
                        // started sooner meets the old copy's single-instance
                        // lock and leaves, and then no PadForge runs at all.
                        if (OldCopyHasExited(parent, parentGone))
                        {
                            ReleaseLease(ref lease);
                            Relaunch(target, relaunch, ui, backupDir: null);
                        }
                        break;
                    case HelperPhase.Swapping when recoveryDir != null && File.Exists(Path.Combine(recoveryDir, ExeName)):
                        Tell(ui, string.Format(Strings.Instance.Update_RestoreFailed_Format, recoveryDir));
                        DropPendingRecords(target);
                        break;
                    case HelperPhase.Swapping:
                        Tell(ui, InstallFailedText(ex.Message));
                        DropPendingRecords(target);
                        break;
                }
                return true;
            }
            finally
            {
                ReleaseLease(ref lease);
                parent?.Dispose();
            }
        }

        /// <summary>The helper said what became of this attempt, so no launch
        /// reports it again. Never throws, whatever the target says.</summary>
        private static void DropPendingRecords(string target)
        {
            try { TryDeleteFile(PendingPathFor(target)); } catch { }
            TryDeleteFile(LegacyPendingPath);
        }

        /// <summary>Waits for the old copy when it is open. False when whether
        /// it exited cannot be known: it could not be opened, or the wait
        /// failed.</summary>
        private static bool OldCopyHasExited(Process parent, bool parentGone)
        {
            if (parent == null) return parentGone;
            try
            {
                parent.WaitForExit();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The old copy, held by handle so its PID cannot be reused under this
        /// helper. With the handshake it is open for certain, since it waits
        /// for this helper's answer. Null when it has already exited, or a
        /// program of another name has its PID. Throws when it cannot be
        /// opened, which leaves its state unknown, so nothing is replaced.
        /// </summary>
        private static Process OpenOldCopy(int pid, string expectedName)
        {
            if (pid <= 0) return null;
            Process p;
            try { p = Process.GetProcessById(pid); }
            catch (ArgumentException) { return null; }
            try
            {
                _ = p.Handle;
                if (!string.Equals(p.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    p.Dispose();
                    return null;
                }
                return p;
            }
            catch
            {
                p.Dispose();
                throw;
            }
        }

        /// <summary>Tells the old copy this helper is ready, then waits for
        /// its commit for as long as it runs: the old copy commits, stops this
        /// helper, or exits. Both events are open before ready is set, so a
        /// commit set at once is not lost. The old copy exiting without a
        /// commit means install nothing.</summary>
        private static bool AwaitCommit(string attempt, Process oldCopy)
        {
            if (!EventWaitHandle.TryOpenExisting(ReadyEventName(attempt), out var ready)) return false;
            using (ready)
            {
                if (!EventWaitHandle.TryOpenExisting(CommitEventName(attempt), out var commit)) return false;
                using (commit)
                {
                    ready.Set();
                    while (true)
                    {
                        if (commit.WaitOne(250)) return true;
                        // Committed and gone at once is still a commit.
                        if (oldCopy.HasExited) return commit.WaitOne(0);
                    }
                }
            }
        }

        /// <summary>Starts PadForge, and says so when it cannot, naming the
        /// folder that holds the previous version's copy when there is one.
        /// A process that started is all this shows, not a finished startup.</summary>
        private static void Relaunch(string exe, List<string> args, IApplyUi ui, string backupDir)
        {
            string error = Launch(exe, args);
            if (error == null) return;
            Tell(ui, backupDir != null
                ? string.Format(Strings.Instance.Update_RestartFailedBackup_Format, error, backupDir)
                : string.Format(Strings.Instance.Update_RestartFailed_Format, error));
        }

        private static string Launch(string exe, IEnumerable<string> args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                };
                foreach (string a in args) psi.ArgumentList.Add(a);
                using var process = Process.Start(psi);
                return process == null ? "Windows did not start a process." : null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        internal enum SwapResult { Installed, NotChanged, Restored, Broken }

        internal sealed record SwapOutcome(SwapResult Result, string Error, string RecoveryDir);

        /// <summary>The file work the swap does, so tests can stand in for
        /// the disk.</summary>
        internal interface IFileOps
        {
            void Copy(string from, string to);
            string Hash(string path);
            void CreateDirectory(string dir);
            void DeleteDirectory(string dir);
        }

        private sealed class DiskFiles : IFileOps
        {
            public static readonly DiskFiles Instance = new();
            public void Copy(string from, string to) => File.Copy(from, to, overwrite: true);
            public string Hash(string path) => HashFile(path);
            public void CreateDirectory(string dir) => Directory.CreateDirectory(dir);
            public void DeleteDirectory(string dir) => TryDeleteDirectory(dir);
        }

        /// <summary>SHA-256 through a handle that denies writes and deletion
        /// while it reads, and is closed when it returns.</summary>
        internal static string HashFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        /// <summary>
        /// The swap, after the old copy has exited. The copy over the installed
        /// exe truncates it before it writes, so one that dies halfway (a full
        /// disk, an I/O error) would leave no working PadForge. So the old exe
        /// is copied into this attempt's recovery folder first and checked
        /// against its hash, never beside PadForge.exe. Then the new one is
        /// copied over it and checked against <paramref name="sourceSha"/>.
        /// When that fails, the old one goes back and is checked again. Every
        /// step retries while antivirus or the loader still holds a file, and
        /// no retry writes over the only good backup. Installed keeps the
        /// backup, which is dropped once the new exe has run. Broken means the
        /// old exe could not be put back, and its copy stays in
        /// <paramref name="recoveryDir"/>.
        /// </summary>
        internal static SwapOutcome Swap(string source, string target, string sourceSha, string recoveryDir,
            IFileOps files, int attempts, TimeSpan retryDelay)
        {
            string backup = Path.Combine(recoveryDir, ExeName);
            string targetSha = null;
            var failed = Retry(() =>
            {
                files.CreateDirectory(recoveryDir);
                targetSha ??= files.Hash(target);
                files.Copy(target, backup);
                if (files.Hash(backup) != targetSha)
                    throw new IOException("The copy of the installed PadForge did not match it.");
            }, attempts, retryDelay);
            if (failed != null)
            {
                // Nothing touched the installed exe.
                files.DeleteDirectory(recoveryDir);
                return new SwapOutcome(SwapResult.NotChanged, failed.Message, null);
            }

            failed = Retry(() =>
            {
                files.Copy(source, target);
                if (files.Hash(target) != sourceSha)
                    throw new IOException("The installed copy did not match the update.");
            }, attempts, retryDelay);
            if (failed == null)
                return new SwapOutcome(SwapResult.Installed, null, recoveryDir);

            var restoreFailed = Retry(() =>
            {
                files.Copy(backup, target);
                if (files.Hash(target) != targetSha)
                    throw new IOException("The previous PadForge did not go back whole.");
            }, attempts, retryDelay);
            if (restoreFailed == null)
            {
                files.DeleteDirectory(recoveryDir);
                return new SwapOutcome(SwapResult.Restored, failed.Message, null);
            }
            return new SwapOutcome(SwapResult.Broken, failed.Message, recoveryDir);
        }

        /// <summary>Runs <paramref name="step"/> until it succeeds, retrying on
        /// the errors a locked or busy file gives. Returns the last error, or
        /// null.</summary>
        private static Exception Retry(Action step, int attempts, TimeSpan retryDelay)
        {
            Exception last = null;
            for (int i = 0; i < Math.Max(1, attempts); i++)
            {
                try
                {
                    step();
                    return null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = ex;
                    if (i + 1 < attempts) Thread.Sleep(retryDelay);
                }
            }
            return last;
        }

        // ─────────────────────────────────────────────
        //  Cleanup
        // ─────────────────────────────────────────────

        /// <summary>
        /// Deletes this copy's leftover downloads and recovery copies,
        /// keeping a pending one. UpdateController runs it once PadForge has
        /// run from this exe for a while with nothing downloading, which shows
        /// the exe works and the previous version's copy is no longer needed.
        /// A helper that is still exiting can hold a folder, so a locked one
        /// is tried again a few times. It never runs inside the staging folder
        /// itself, nor while a helper holds the update lease.
        /// </summary>
        private static Task _cleanup = Task.CompletedTask;

        internal static Task CleanupStagingAsync()
        {
            return _cleanup = Task.Run(async () =>
            {
                try
                {
                    string self = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(self)
                        || NormalizePath(self).StartsWith(NormalizePath(StagingRoot), StringComparison.Ordinal))
                        return;
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        if (IsUpdateInProgress(self) || CleanStaging(StagingRoot, self)) return;
                        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Best effort: what stays is tried again at the next launch.
                }
            });
        }

        /// <summary>
        /// One pass over <paramref name="root"/>. This copy's folder keeps
        /// what its pending record names and loses everything else. Other
        /// copies' folders are never touched: an exe that cannot be found may
        /// sit on a drive that is not plugged in, and its recovery copy may be
        /// the only good one. What the first release of this feature left at
        /// the top level goes. Returns false when something could not be
        /// deleted.
        /// </summary>
        internal static bool CleanStaging(string root, string self)
        {
            if (!Directory.Exists(root)) return true;
            bool clean = TryDeleteFile(Path.Combine(root, "pending.json"));
            string mine = InstallKey(self);
            foreach (string dir in SafeDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (LegacyStageName.IsMatch(name))
                    clean &= TryDeleteDirectory(dir);
                else if (name == mine)
                    clean &= CleanInstallDir(dir);
            }
            return clean;
        }

        private static bool CleanInstallDir(string dir)
        {
            bool clean = true;
            string record = Path.Combine(dir, "pending.json");
            switch (ReadPendingRecord(record, out var pending))
            {
                case RecordRead.Unreadable:
                    // Whatever it names stays until it can be read.
                    return false;
                case RecordRead.Corrupt:
                    clean &= TryDeleteFile(record);
                    break;
            }
            string keep = pending?.ExePath;
            string keepDir = null;
            try { if (!string.IsNullOrEmpty(keep)) keepDir = Path.GetDirectoryName(Path.GetFullPath(keep)); }
            catch { keepDir = null; }
            foreach (string sub in SafeDirectories(dir))
            {
                if (keepDir != null && SamePath(sub, keepDir)) continue;
                clean &= TryDeleteDirectory(sub);
            }
            TryDeleteFile(Path.Combine(dir, "pending.json.tmp"));
            return clean;
        }

        /// <summary>This copy's folder, created with the note that names its
        /// owner.</summary>
        private static string EnsureInstallDir(string targetExe)
        {
            string dir = InstallDir(targetExe);
            Directory.CreateDirectory(dir);
            string owner = Path.Combine(dir, "target.txt");
            if (!File.Exists(owner)) File.WriteAllText(owner, Path.GetFullPath(targetExe));
            return dir;
        }

        /// <summary>Deletes a staged update's folder, best effort.</summary>
        internal static void DiscardStaged(StagedUpdate staged)
        {
            string dir = staged?.ExePath != null ? Path.GetDirectoryName(staged.ExePath) : null;
            if (dir != null && NormalizePath(dir).StartsWith(NormalizePath(StagingRoot), StringComparison.Ordinal))
                TryDeleteDirectory(dir);
        }

        private static IEnumerable<string> SafeDirectories(string root)
        {
            try { return Directory.GetDirectories(root); }
            catch { return Array.Empty<string>(); }
        }

        private static bool TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
