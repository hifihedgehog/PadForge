using PadForge.SteamWorkshop;
using SteamKit2;
using PadForge.SteamWorkshop.Api;
using PadForge.SteamWorkshop.Cache;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Vdf;

// Manual smoke harness for the Steam Workshop network paths. These hit real Steam
// endpoints, so this lives outside the test suite (which is network-free).
//
//   SteamWorkshopSmoke store   "the elder scrolls v skyrim"
//   SteamWorkshopSmoke details 793611331
//   SteamWorkshopSmoke download 793611331
//   SteamWorkshopSmoke search  72850                # appid of Skyrim
//   SteamWorkshopSmoke persona 76561198001901205

internal static class Program
{
    private sealed class AlwaysOnGate : ISteamWorkshopGate
    {
        public bool IsCommunityConfigLookupEnabled => true;
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: SteamWorkshopSmoke <store|details|download|search|persona> <arg>");
            return 2;
        }

        var gate = new AlwaysOnGate();
        var command = args[0].ToLowerInvariant();
        var arg = args[1];

        try
        {
            switch (command)
            {
                case "store":
                    await StoreAsync(gate, arg);
                    break;
                case "details":
                    await DetailsAsync(gate, long.Parse(arg));
                    break;
                case "download":
                    await DownloadAsync(gate, long.Parse(arg));
                    break;
                case "search":
                    await SearchAsync(gate, int.Parse(arg),
                        args.Length > 2 ? int.Parse(args[2]) : 9);
                    break;
                case "cmdetails":
                    await CmDetailsAsync(gate, ulong.Parse(arg));
                    break;
                case "persona":
                    await PersonaAsync(gate, ulong.Parse(arg));
                    break;
                default:
                    Console.Error.WriteLine($"unknown command '{command}'");
                    return 2;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task StoreAsync(ISteamWorkshopGate gate, string query)
    {
        var client = new SteamStoreClient(gate);
        var result = await client.SearchAsync(query);
        Console.WriteLine($"total={result.Total}");
        foreach (var item in result.Items ?? new())
            Console.WriteLine($"  {item.Id,-8} {item.Type,-6} {item.Name}");
    }

    private static async Task DetailsAsync(ISteamWorkshopGate gate, long fileId)
    {
        var client = new SteamRemoteStorageClient(gate);
        var d = await client.GetDetailsAsync(fileId);
        if (d == null)
        {
            Console.WriteLine("no details");
            return;
        }
        Console.WriteLine($"title      : {d.Title}");
        Console.WriteLine($"creator    : {d.Creator}");
        Console.WriteLine($"size       : {d.FileSizeBytes}");
        Console.WriteLine($"updated    : {d.TimeUpdated}");
        Console.WriteLine($"file_url   : {d.FileUrl}");
        Console.WriteLine($"tags       : {string.Join(", ", (d.Tags ?? new()).ConvertAll(t => t.Tag))}");
    }

    private static async Task DownloadAsync(ISteamWorkshopGate gate, long fileId)
    {
        var storage = new SteamRemoteStorageClient(gate);
        var details = await storage.GetDetailsAsync(fileId);
        if (details == null || details.IsLegacyNonDownloadable)
        {
            Console.WriteLine("no downloadable file_url");
            return;
        }

        var downloader = new SteamUgcDownloader(gate);
        var vdf = await downloader.DownloadVdfAsync(details.FileUrl, details.FileSizeBytes ?? 0);

        var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
        Console.WriteLine($"title       : {config.Title}");
        Console.WriteLine($"version     : {config.Version}");
        Console.WriteLine($"controller  : {config.ControllerType}");
        Console.WriteLine($"groups      : {config.Groups.Count}");
        Console.WriteLine($"presets     : {config.Presets.Count}");
        Console.WriteLine($"languages   : {config.Localization.Count}");
    }

    private static async Task SearchAsync(ISteamWorkshopGate gate, int appId, int queryType)
    {
        await using var client = new SteamWorkshopClient(gate, cache: null);
        var response = await client.SearchAsync(appId, (EPublishedFileQueryType)queryType, page: 1, perPage: 10);
        Console.WriteLine($"total={response.total} returned={response.publishedfiledetails.Count}");
        foreach (var file in response.publishedfiledetails)
            Console.WriteLine($"  {file.publishedfileid,-12} {file.title}");
    }

    private static async Task CmDetailsAsync(ISteamWorkshopGate gate, ulong fileId)
    {
        await using var client = new SteamWorkshopClient(gate, cache: null);
        var d = await client.GetCmDetailsAsync(fileId);
        Console.WriteLine($"title       : {d.title}");
        Console.WriteLine($"creator_app : {d.creator_appid}");
        Console.WriteLine($"consumer_app: {d.consumer_appid}");
        Console.WriteLine($"visibility  : {d.visibility}");
        Console.WriteLine($"file_type   : {d.file_type}");
        Console.WriteLine($"tags        : {string.Join(", ", d.tags.ConvertAll(t => t.tag))}");
        Console.WriteLine($"kvtags      : {string.Join(", ", d.kvtags.ConvertAll(t => $"{t.key}={t.value}"))}");
    }

    private static async Task PersonaAsync(ISteamWorkshopGate gate, ulong steamId)
    {
        var client = new SteamCommunityClient(gate);
        var persona = await client.GetPersonaAsync(steamId);
        Console.WriteLine($"persona : {persona.PersonaName}");
        Console.WriteLine($"avatar  : {persona.AvatarMediumUrl}");
    }
}
