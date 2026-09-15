// Builds payload/api-db.json from PCGamingWiki, slowly, from one machine.
//
// Why this exists instead of the app asking the wiki: see src/AmdNr.Core/Pcgw.cs. In short, the
// wiki's Cloudflare front refuses a library's worth of lookups from .NET, so the lookups happen
// here, once, and the result ships.
//
// Requests go through Windows' own curl.exe rather than HttpClient. That is not a style choice:
// measured from one address, every .NET request was answered 403 while curl was answered 302/200,
// headers and HTTP version made no difference, so the refusal is on the TLS fingerprint.
//
// Usage:
//   dotnet run --project tools/ApiDbBuilder -- --steam-top 1000
//   dotnet run --project tools/ApiDbBuilder -- --local
//   dotnet run --project tools/ApiDbBuilder -- --appids 12210,1262540
//
// It resumes: games already in the database, and pages already known not to exist, are skipped. It
// saves every 20 games, and stops cleanly after repeated refusals rather than hammering a wiki that
// has asked it to slow down.

using System.Diagnostics;
using System.Text.Json;
using AmdNr.Core;

var repo = FindRepoRoot();
var dbPath = Path.Combine(repo, "payload", "api-db.json");
var missPath = Path.Combine(repo, "payload", "api-db.misses.txt");
var delay = TimeSpan.FromMilliseconds(IntArg("--delay", 2500));

var db = File.Exists(dbPath) ? ApiDatabase.Parse(File.ReadAllText(dbPath)) : new ApiDatabase();
var misses = File.Exists(missPath)
    ? new HashSet<string>(File.ReadAllLines(missPath).Where(l => l.Length > 0))
    : new HashSet<string>();

var queue = new List<(string AppId, string Name)>();
if (HasArg("--local"))
    queue.AddRange(GameScanner.ScanAll().Where(g => g.AppId is not null).Select(g => (g.AppId!, g.Name)));
if (StringArg("--appids") is { } ids)
    queue.AddRange(ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => (id.Trim(), id.Trim())));
if (IntArg("--steam-top", 0) is > 0 and var top)
    queue.AddRange(await SteamSpyTop(top));

queue = queue.DistinctBy(q => q.AppId)
    .Where(q => !db.Games.ContainsKey(ApiDatabase.SteamKey(q.AppId)) && !misses.Contains(q.AppId))
    .ToList();

Console.WriteLine($"{db.Games.Count} already in the database, {misses.Count} known misses, {queue.Count} to look up.");

var done = 0;
var refusals = 0;
foreach (var (appId, name) in queue)
{
    var (status, location, _) = Curl($"https://www.pcgamingwiki.com/api/appid.php?appid={appId}", follow: false);
    if (status is 403 or 429 or 503)
    {
        if (!Backoff(++refusals)) break;
        continue;
    }
    refusals = 0;

    var page = PageFrom(location);
    if (page is null)
    {
        misses.Add(appId);
        Console.WriteLine($"  -  {appId,-8} {name}: no wiki page");
        await Task.Delay(delay);
        continue;
    }

    await Task.Delay(delay);
    var (parseStatus, _, body) = Curl(
        "https://www.pcgamingwiki.com/w/api.php?action=parse&prop=wikitext&format=json&redirects=1&page="
        + Uri.EscapeDataString(page), follow: true);
    if (parseStatus is 403 or 429 or 503)
    {
        if (!Backoff(++refusals)) break;
        continue;
    }

    PcgwApi? api = null;
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("parse", out var parse))
            api = PcgwParser.ParseApiTemplate(page, parse.GetProperty("wikitext").GetProperty("*").GetString() ?? "");
    }
    catch (JsonException)
    {
        // Treated as a miss below.
    }

    if (api is null)
    {
        misses.Add(appId);
        Console.WriteLine($"  -  {appId,-8} {page}: no API section");
    }
    else
    {
        db.Put(ApiDatabase.SteamKey(appId), ApiRecord.From(api));
        Console.WriteLine($"  ok {appId,-8} {api.Page}: {string.Join(", ", api.Supported)}");
    }

    if (++done % 20 == 0) Save();
    await Task.Delay(delay);
}

Save();
Console.WriteLine($"done: {db.Games.Count} games in {dbPath}");
return;

void Save()
{
    db.Generated = DateTime.UtcNow;
    File.WriteAllText(dbPath, db.Serialise());
    File.WriteAllLines(missPath, misses.Order());
}

bool Backoff(int attempt)
{
    if (attempt > 6)
    {
        Console.WriteLine("  !! refused six times in a row; saving and stopping. Run again later -- it resumes.");
        return false;
    }
    var wait = TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, attempt - 1)));
    Console.WriteLine($"  !! refused; waiting {wait.TotalSeconds:0}s");
    Save();
    Thread.Sleep(wait);
    return true;
}

static string? PageFrom(string? location)
{
    if (string.IsNullOrEmpty(location)) return null;
    var marker = location.IndexOf("/wiki/", StringComparison.Ordinal);
    if (marker < 0) return null;
    var title = Uri.UnescapeDataString(location[(marker + "/wiki/".Length)..]);
    return title.Length == 0 || title.StartsWith("Special:", StringComparison.Ordinal) ? null : title;
}

static (int Status, string? Location, string Body) Curl(string url, bool follow)
{
    var start = new ProcessStartInfo("curl.exe")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
    };
    foreach (var arg in new[]
             {
                 "-sS", "--max-time", "30", "-A",
                 "AMD-NR-ReShade-Installer-ApiDbBuilder/1.0 (+https://github.com/zmodelerlover/AMD-NR-ReShade-Installer)",
                 "-w", "\n%{http_code}\n%{redirect_url}", url,
             })
        start.ArgumentList.Add(arg);
    if (follow) start.ArgumentList.Insert(0, "-L");

    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();

    var lines = output.Split('\n');
    if (lines.Length < 3) return (0, null, "");
    var redirect = lines[^1].Trim();
    _ = int.TryParse(lines[^2].Trim(), out var status);
    var body = string.Join('\n', lines[..^2]);
    return (status, redirect.Length > 0 ? redirect : null, body);
}

static async Task<List<(string, string)>> SteamSpyTop(int count)
{
    var result = new List<(string, string)>();
    for (var page = 0; result.Count < count && page < 10; page++)
    {
        var (status, _, body) = Curl($"https://steamspy.com/api.php?request=all&page={page}", follow: true);
        if (status != 200) break;
        using var doc = JsonDocument.Parse(body);
        foreach (var game in doc.RootElement.EnumerateObject())
        {
            result.Add((game.Value.GetProperty("appid").GetInt32().ToString(), game.Value.GetProperty("name").GetString() ?? ""));
            if (result.Count >= count) break;
        }
        // SteamSpy asks for no more than one "all" request a minute.
        if (result.Count < count) await Task.Delay(TimeSpan.FromSeconds(61));
    }
    return result;
}

bool HasArg(string name) => args.Contains(name);
string? StringArg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
int IntArg(string name, int fallback) => int.TryParse(StringArg(name), out var v) ? v : fallback;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AmdNrInstaller.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Run this from inside the repository.");
}
