using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PadForge.SteamWorkshop;
using PadForge.SteamWorkshop.Api;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Tests;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;
using SteamKit2;

// Mass wild-corpus sweep for the Workshop config translator (#9 follow-up).
// Hits real Steam endpoints, so it lives outside the test suite, beside
// SteamWorkshopSmoke. The browse dialog's exact query shape is reused:
// QueryFiles RankedByVote, page 1, on the 241100 bucket with the app kv-tag
// (SteamWorkshopClient.SearchAsync), banned and legacy items dropped exactly
// like WorkshopConfigListPager.Accept.
//
//   SteamWorkshopSweep harvest   # QueryFiles + download the top configs per game
//   SteamWorkshopSweep sweep     # translate the cache, digest unapproved reason lines
//   SteamWorkshopSweep all       # harvest then sweep (default)
//
// Inputs beside the exe's source dir: games.csv (committed appid list).
// Outputs: cache/*.vdf (gitignored), manifest.json (committed),
// cache/digest.txt (gitignored copy of the sweep digest).
// Resumable: a cached VDF is never re-downloaded. Ctrl+C saves the manifest
// for everything harvested so far and exits cleanly.

internal static class Program
{
    private sealed class AlwaysOnGate : ISteamWorkshopGate
    {
        public bool IsCommunityConfigLookupEnabled => true;
    }

    /// <summary>Top of each game's list, by vote (the dialog pages by 30;
    /// the sweep wants the 10-15 configs a user sees first).</summary>
    private const int PerGame = 15;

    /// <summary>Polite spacing between CDN downloads. The CM client already
    /// spaces QueryFiles calls by 100 ms internally.</summary>
    private static readonly TimeSpan DownloadSpacing = TimeSpan.FromMilliseconds(200);

    /// <summary>Extra spacing between per-game QueryFiles calls.</summary>
    private static readonly TimeSpan GameSpacing = TimeSpan.FromMilliseconds(250);

    private static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        if (mode != "harvest" && mode != "sweep" && mode != "all")
        {
            Console.Error.WriteLine("usage: SteamWorkshopSweep [harvest|sweep|all]");
            return 2;
        }

        var toolDir = FindToolDir();
        var gamesPath = Path.Combine(toolDir, "games.csv");
        var cacheDir = Path.Combine(toolDir, "cache");
        var manifestPath = Path.Combine(toolDir, "manifest.json");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            if (mode is "harvest" or "all")
                await HarvestAsync(gamesPath, cacheDir, manifestPath, cts.Token);
            if (mode is "sweep" or "all")
                SweepCache(cacheDir, manifestPath);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled. Manifest saved for the games harvested so far; re-run to resume.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The tool's source dir (games.csv beside the csproj), found by
    /// walking up from the build output so both "dotnet run" and a direct exe
    /// launch resolve the same committed inputs.</summary>
    private static string FindToolDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "games.csv")))
                return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    private static List<(int AppId, string Name)> LoadGames(string gamesPath)
    {
        var games = new List<(int, string)>();
        var seen = new HashSet<int>();
        foreach (var raw in File.ReadAllLines(gamesPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int comma = line.IndexOf(',');
            if (comma <= 0 || !int.TryParse(line.AsSpan(0, comma), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int appId))
            {
                Console.Error.WriteLine($"games.csv: skipping malformed line '{line}'");
                continue;
            }
            if (seen.Add(appId))
                games.Add((appId, line[(comma + 1)..].Trim()));
        }
        return games;
    }

    private static async Task HarvestAsync(string gamesPath, string cacheDir, string manifestPath, CancellationToken ct)
    {
        var games = LoadGames(gamesPath);
        Directory.CreateDirectory(cacheDir);

        var gate = new AlwaysOnGate();
        await using var client = new SteamWorkshopClient(gate, cache: null);
        var downloader = new SteamUgcDownloader(gate);

        var manifest = new SweepManifest
        {
            HarvestedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
        var seenFileIds = new HashSet<long>();
        int totalDownloaded = 0, totalCached = 0, totalLegacy = 0, totalBanned = 0, totalFailed = 0;

        for (int i = 0; i < games.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (appId, name) = games[i];

            SteamKit2.Internal.CPublishedFile_QueryFiles_Response resp;
            try
            {
                resp = await client.SearchAsync(appId, EPublishedFileQueryType.RankedByVote, 1, PerGame, null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One game's query failure must not end a 165-game run.
                totalFailed++;
                Console.WriteLine($"[{i + 1}/{games.Count}] {appId} {name}: QUERY FAILED ({ex.GetType().Name}: {ex.Message})");
                continue;
            }

            var details = resp?.publishedfiledetails ?? new List<SteamKit2.Internal.PublishedFileDetails>();
            int picked = 0, downloaded = 0, cached = 0, legacy = 0, banned = 0, failed = 0;

            foreach (var d in details)
            {
                ct.ThrowIfCancellationRequested();

                // Mirror WorkshopConfigListPager.Accept: banned drops, legacy
                // (no file_url, pre-2015 cloud-only storage) drops.
                if (d.banned) { banned++; continue; }
                if (string.IsNullOrEmpty(d.file_url)) { legacy++; continue; }

                long fileId = (long)d.publishedfileid;
                if (!seenFileIds.Add(fileId)) continue;

                // Dead published items: Steam serves a 0-byte body for a
                // 0-size file record. Nothing to translate, keep it out of
                // the manifest instead of counting a parser reject later.
                if ((long)d.file_size == 0)
                {
                    failed++;
                    Console.WriteLine($"    {fileId}: SKIPPED (0-byte published file)");
                    continue;
                }

                var path = Path.Combine(cacheDir, fileId + ".vdf");
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    cached++;
                }
                else
                {
                    try
                    {
                        var vdf = await downloader.DownloadVdfAsync(d.file_url, (long)d.file_size, ct);
                        await File.WriteAllTextAsync(path, vdf, ct);
                        downloaded++;
                        await Task.Delay(DownloadSpacing, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Stale file_urls 404 (HttpRequestException), CDN
                        // stalls time out, sizes drift. Per-file failures are
                        // logged and skipped; only Ctrl+C stops the harvest.
                        failed++;
                        Console.WriteLine($"    {fileId}: DOWNLOAD FAILED ({ex.GetType().Name}: {ex.Message})");
                        continue;
                    }
                }

                manifest.Entries.Add(new SweepManifestEntry
                {
                    AppId = appId,
                    AppName = name,
                    FileId = fileId,
                    TitleSha256 = Sha256Hex(Encoding.UTF8.GetBytes(d.title ?? "")),
                    VdfSha256 = Sha256Hex(await File.ReadAllBytesAsync(path, ct)),
                });
                picked++;
            }

            totalDownloaded += downloaded;
            totalCached += cached;
            totalLegacy += legacy;
            totalBanned += banned;
            totalFailed += failed;

            // Save after every game so Ctrl+C loses nothing already harvested.
            manifest.Save(manifestPath);

            Console.WriteLine(
                $"[{i + 1}/{games.Count}] {appId} {name}: total={resp?.total ?? 0} picked={picked} " +
                $"(new={downloaded} cached={cached} legacy={legacy} banned={banned} failed={failed})");

            await Task.Delay(GameSpacing, ct);
        }

        Console.WriteLine();
        Console.WriteLine(
            $"HARVEST DONE: games={games.Count} configs={manifest.Entries.Count} " +
            $"(new={totalDownloaded} cached={totalCached} legacy-skipped={totalLegacy} " +
            $"banned-skipped={totalBanned} failed={totalFailed})");
    }

    private sealed class ReasonBucket
    {
        public int Count;
        public readonly HashSet<long> FileIds = new();
        public readonly List<string> Examples = new();
        public readonly HashSet<string> ExampleKeys = new();
    }

    private static void SweepCache(string cacheDir, string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"No manifest at {manifestPath}; run harvest first.");
            return;
        }

        var manifest = SweepManifest.Load(manifestPath);
        var buckets = new Dictionary<string, ReasonBucket>(StringComparer.Ordinal);
        var exceptions = new List<string>();
        var exceptionFileIds = new HashSet<long>();
        int translated = 0, missing = 0;
        long totalLines = 0, approvedLines = 0;

        foreach (var entry in manifest.Entries)
        {
            var path = Path.Combine(cacheDir, entry.FileId + ".vdf");
            if (!File.Exists(path)) { missing++; continue; }

            TranslatedProfile result;
            try
            {
                var config = SteamInputConfig.FromVdf(VdfParser.Parse(File.ReadAllText(path)));
                result = new ConfigTranslator().Translate(config, new TranslationOptions
                {
                    FileId = entry.FileId,
                });
            }
            catch (Exception ex)
            {
                if (exceptionFileIds.Add(entry.FileId))
                {
                    exceptions.Add($"{ex.GetType().Name} file {entry.FileId} ({entry.AppName}): {Truncate(ex.Message, 200)}");
                }
                continue;
            }

            translated++;
            foreach (var e in result.Report.Entries)
            {
                totalLines++;
                string key = string.IsNullOrEmpty(e.ReasonKey) ? "(empty)" : e.ReasonKey;
                if (ApprovedReasonLockdown.ApprovedKeys.Contains(key))
                {
                    approvedLines++;
                    continue;
                }

                if (!buckets.TryGetValue(key, out var bucket))
                    buckets[key] = bucket = new ReasonBucket();
                bucket.Count++;
                bucket.FileIds.Add(entry.FileId);
                if (bucket.Examples.Count < 3)
                {
                    var exampleKey = e.SourcePath + "|" + e.Binding;
                    if (bucket.ExampleKeys.Add(exampleKey))
                    {
                        bucket.Examples.Add(
                            $"[{e.Status}] {Truncate(e.SourcePath, 90)} :: {Truncate(e.Binding, 60)}" +
                            (e.ReasonArgs.Count > 0 ? $" args({Truncate(string.Join("; ", e.ReasonArgs), 80)})" : "") +
                            $" (file {entry.FileId})");
                    }
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("=== SWEEP DIGEST ===");
        sb.AppendLine(
            $"configs: {manifest.Entries.Count} manifested, {translated} translated, " +
            $"{exceptionFileIds.Count} threw, {missing} missing from cache");
        sb.AppendLine($"report lines: {totalLines} total, {approvedLines} approved, " +
            $"{totalLines - approvedLines} unapproved across {buckets.Count} reason keys");
        sb.AppendLine();

        sb.AppendLine($"--- P0: translator exceptions ({exceptionFileIds.Count}) ---");
        foreach (var line in exceptions)
            sb.AppendLine("  " + line);
        sb.AppendLine();

        sb.AppendLine("--- Unapproved reason lines, ranked ---");
        foreach (var kv in buckets.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var b = kv.Value;
            sb.AppendLine($"{kv.Key}: {b.Count} lines in {b.FileIds.Count} configs");
            foreach (var ex in b.Examples)
                sb.AppendLine("    " + ex);
            sb.AppendLine("    files: " + string.Join(", ", b.FileIds.OrderBy(x => x).Take(12)) +
                (b.FileIds.Count > 12 ? $" (+{b.FileIds.Count - 12} more)" : ""));
        }

        var digest = sb.ToString();
        Console.WriteLine(digest);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "digest.txt"), digest);
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Truncate(string s, int max)
    {
        s ??= "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= max ? s : s[..max] + "...";
    }
}
