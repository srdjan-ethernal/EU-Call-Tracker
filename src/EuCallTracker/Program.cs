using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var workspaceRoot = FindWorkspaceRoot(AppContext.BaseDirectory);
var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var options = CliOptions.Parse(args.Skip(1).ToArray(), workspaceRoot);

switch (command)
{
    case "update":
        await UpdateAsync(options);
        break;
    case "report":
        await WriteReportsAsync(options);
        break;
    case "list":
        await PrintListAsync(options);
        break;
    case "serve":
        await ServeAsync(options, args.Skip(1).ToArray());
        break;
    case "help":
    case "--help":
    case "-h":
        PrintHelp();
        break;
    default:
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 2;
}

return 0;

static async Task UpdateAsync(CliOptions options)
{
    var config = await AppConfig.LoadAsync(options.ConfigPath);
    var store = await CallStore.LoadAsync(options.DataPath);
    var scraper = new CallScraper(config);
    var allItems = new List<CallRecord>();
    var errors = new List<string>();

    foreach (var source in config.Sources.Where(x => x.Enabled))
    {
        try
        {
            Console.WriteLine($"Checking: {source.Name}");
            var items = await scraper.ScrapeAsync(source);
            Console.WriteLine($"  found {items.Count} matching items");
            allItems.AddRange(items);
        }
        catch (Exception ex)
        {
            errors.Add($"{source.Name}: {ex.Message}");
            Console.Error.WriteLine($"  error: {ex.Message}");
        }
    }

    var result = store.Upsert(allItems);
    await store.SaveAsync(options.DataPath);
    Console.WriteLine($"Saved {result.Inserted} new and {result.Updated} updated calls in {options.DataPath}");

    if (errors.Count > 0)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Some sources could not be read:");
        foreach (var error in errors)
        {
            Console.Error.WriteLine($"- {error}");
        }
    }
}

static async Task WriteReportsAsync(CliOptions options)
{
    var store = await CallStore.LoadAsync(options.DataPath);
    var rows = store.Query(options.OpenOnly, options.MinScore).ToList();
    Directory.CreateDirectory(options.ReportPath);

    var htmlPath = Path.Combine(options.ReportPath, "open-calls.html");
    var csvPath = Path.Combine(options.ReportPath, "open-calls.csv");
    var alertPath = Path.Combine(options.ReportPath, "alert-sources.html");
    var usaPath = Path.Combine(options.ReportPath, "usa-readiness.html");

    await File.WriteAllTextAsync(htmlPath, ApplicationReportRenderer.RenderHtml(rows), Encoding.UTF8);
    await File.WriteAllTextAsync(csvPath, ApplicationReportRenderer.RenderCsv(rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    await File.WriteAllTextAsync(alertPath, AlertSourcesPage.RenderHtml(), Encoding.UTF8);
    await File.WriteAllTextAsync(usaPath, UsaReadinessPage.RenderHtml(), Encoding.UTF8);

    Console.WriteLine($"HTML report: {htmlPath}");
    Console.WriteLine($"CSV report:  {csvPath}");
    Console.WriteLine($"Alert page:  {alertPath}");
    Console.WriteLine($"USA page:    {usaPath}");
}

static async Task PrintListAsync(CliOptions options)
{
    var store = await CallStore.LoadAsync(options.DataPath);
    var rows = store.Query(options.OpenOnly, options.MinScore).Take(options.Limit).ToList();

    if (rows.Count == 0)
    {
        Console.WriteLine("No calls found. Run: dotnet run --project .\\src\\EuCallTracker -- update");
        return;
    }

    foreach (var row in rows)
    {
        var deadline = string.IsNullOrWhiteSpace(row.Deadline) ? "no deadline" : row.Deadline;
        Console.WriteLine($"[{row.Score,2}] {deadline} | {row.Source} | {row.Title}");
        Console.WriteLine($"     {row.Url}");
    }
}

static async Task ServeAsync(CliOptions options, string[] rawArgs)
{
    var builder = WebApplication.CreateBuilder(rawArgs);
    builder.WebHost.UseUrls(options.Url);
    var app = builder.Build();

    app.MapGet("/", async () =>
    {
        var store = await CallStore.LoadAsync(options.DataPath);
        var rows = store.Query(openOnly: false, minScore: 0).ToList();
        return Results.Content(ApplicationReportRenderer.RenderHtml(rows, liveMode: true), "text/html; charset=utf-8");
    });

    app.MapGet("/api/calls", async () =>
    {
        var store = await CallStore.LoadAsync(options.DataPath);
        return Results.Json(store.Query(openOnly: false, minScore: 0));
    });

    app.MapGet("/alerts", () =>
    {
        return Results.Content(AlertSourcesPage.RenderHtml(liveMode: true), "text/html; charset=utf-8");
    });

    app.MapGet("/usa", () =>
    {
        return Results.Content(UsaReadinessPage.RenderHtml(liveMode: true), "text/html; charset=utf-8");
    });

    Console.WriteLine($"Serving {options.Url}");
    await app.RunAsync();
}

static void PrintHelp()
{
    Console.WriteLine(
        @"
        EU Call Tracker - Microsoft stack

        Commands:
          update                         Fetch configured sources and save matching calls.
          report [--open-only]           Generate reports/open-calls.html and reports/open-calls.csv.
          list [--open-only]             Print calls in the terminal.
          serve                          Start local ASP.NET Core dashboard.

        Options:
          --config <path>                Default: config\sources.json
          --data <path>                  Default: data\calls.json
          --report-path <path>           Default: reports
          --min-score <number>           Default: 0
          --limit <number>               Default: 30
          --url <url>                    Default: http://localhost:5055
        "
    );
}

static string FindWorkspaceRoot(string start)
{
    var current = new DirectoryInfo(start);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "EuProjects.sln")) || Directory.Exists(Path.Combine(current.FullName, "config")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    return Directory.GetCurrentDirectory();
}

sealed class CliOptions
{
    public string ConfigPath { get; private init; } = "";
    public string DataPath { get; private init; } = "";
    public string ReportPath { get; private init; } = "";
    public string Url { get; private init; } = "http://localhost:5055";
    public bool OpenOnly { get; private init; }
    public int MinScore { get; private init; }
    public int Limit { get; private init; } = 30;

    public static CliOptions Parse(string[] args, string workspaceRoot)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[arg] = args[++i];
            }
            else
            {
                flags.Add(arg);
            }
        }

        return new CliOptions
        {
            ConfigPath = FullPath(values.GetValueOrDefault("--config") ?? Path.Combine(workspaceRoot, "config", "sources.json"), workspaceRoot),
            DataPath = FullPath(values.GetValueOrDefault("--data") ?? Path.Combine(workspaceRoot, "data", "calls.json"), workspaceRoot),
            ReportPath = FullPath(values.GetValueOrDefault("--report-path") ?? Path.Combine(workspaceRoot, "reports"), workspaceRoot),
            Url = values.GetValueOrDefault("--url") ?? "http://localhost:5055",
            OpenOnly = flags.Contains("--open-only"),
            MinScore = int.TryParse(values.GetValueOrDefault("--min-score"), out var minScore) ? minScore : 0,
            Limit = int.TryParse(values.GetValueOrDefault("--limit"), out var limit) ? limit : 30
        };
    }

    private static string FullPath(string path, string workspaceRoot)
    {
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(workspaceRoot, path));
    }
}

sealed class AppConfig
{
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; init; } = 30;

    [JsonPropertyName("request_pause_seconds")]
    public double RequestPauseSeconds { get; init; } = 0.2;

    [JsonPropertyName("detail_fetch_limit")]
    public int DetailFetchLimit { get; init; } = 15;

    [JsonPropertyName("min_link_score")]
    public int MinLinkScore { get; init; } = 2;

    [JsonPropertyName("min_item_score")]
    public int MinItemScore { get; init; } = 2;

    [JsonPropertyName("filters")]
    public Filters Filters { get; init; } = new();

    [JsonPropertyName("sources")]
    public List<SourceConfig> Sources { get; init; } = new();

    public static async Task<AppConfig> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonDefaults.Options) ?? new AppConfig();
    }
}

sealed class Filters
{
    [JsonPropertyName("include_keywords")]
    public List<string> IncludeKeywords { get; init; } = new();

    [JsonPropertyName("sme_keywords")]
    public List<string> SmeKeywords { get; init; } = new();

    [JsonPropertyName("serbia_keywords")]
    public List<string> SerbiaKeywords { get; init; } = new();

    [JsonPropertyName("exclude_keywords")]
    public List<string> ExcludeKeywords { get; init; } = new();
}

sealed class SourceConfig
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("level")]
    public string Level { get; init; } = "unknown";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "web";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("required_link_terms")]
    public List<string> RequiredLinkTerms { get; init; } = new();

    [JsonPropertyName("detail_fetch_limit")]
    public int? DetailFetchLimit { get; init; }

    [JsonPropertyName("min_link_score")]
    public int? MinLinkScore { get; init; }

    [JsonPropertyName("min_item_score")]
    public int? MinItemScore { get; init; }

    [JsonPropertyName("fetch_details")]
    public bool FetchDetails { get; init; } = true;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

sealed class CallRecord
{
    public string Source { get; set; } = "";
    public string SourceLevel { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Deadline { get; set; } = "";
    public string Published { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public int Score { get; set; }
    public string MatchedKeywords { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

sealed class CallStore
{
    public List<CallRecord> Calls { get; init; } = new();

    public static async Task<CallStore> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new CallStore();
        }

        await using var stream = File.OpenRead(path);
        var calls = await JsonSerializer.DeserializeAsync<List<CallRecord>>(stream, JsonDefaults.Options);
        return new CallStore { Calls = calls ?? new List<CallRecord>() };
    }

    public (int Inserted, int Updated) Upsert(IEnumerable<CallRecord> items)
    {
        var inserted = 0;
        var updated = 0;
        var byUrl = Calls.ToDictionary(x => x.Url, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        foreach (var item in items.Where(x => !string.IsNullOrWhiteSpace(x.Url)))
        {
            if (byUrl.TryGetValue(item.Url, out var existing))
            {
                existing.Source = item.Source;
                existing.SourceLevel = item.SourceLevel;
                existing.SourceUrl = item.SourceUrl;
                existing.Title = item.Title;
                existing.Summary = item.Summary;
                existing.Deadline = item.Deadline;
                existing.Published = item.Published;
                existing.Status = item.Status;
                existing.Score = item.Score;
                existing.MatchedKeywords = item.MatchedKeywords;
                existing.LastSeen = now;
                updated++;
            }
            else
            {
                item.FirstSeen = now;
                item.LastSeen = now;
                Calls.Add(item);
                byUrl[item.Url] = item;
                inserted++;
            }
        }

        return (inserted, updated);
    }

    public IEnumerable<CallRecord> Query(bool openOnly, int minScore)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return Calls
            .Where(x => x.Score >= minScore)
            .Where(x => !openOnly || IsOpen(x, today))
            .OrderBy(x => string.IsNullOrWhiteSpace(x.Deadline))
            .ThenBy(x => ParseDate(x.Deadline) ?? DateOnly.MaxValue)
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.LastSeen);
    }

    public async Task SaveAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, Calls.OrderByDescending(x => x.LastSeen), JsonDefaults.Options);
    }

    private static bool IsOpen(CallRecord call, DateOnly today)
    {
        if (call.Status.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var deadline = ParseDate(call.Deadline);
        return deadline is null || deadline >= today;
    }

    private static DateOnly? ParseDate(string value)
    {
        return DateOnly.TryParse(value, out var parsed) ? parsed : null;
    }
}

sealed class CallScraper
{
    private static readonly Regex LinkRegex = new("<a\\s+[^>]*href\\s*=\\s*[\"'](?<href>[^\"']+)[\"'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new("<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex H1Regex = new("<h1[^>]*>(?<title>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly AppConfig _config;
    private readonly HttpClient _httpClient;

    public CallScraper(AppConfig config)
    {
        _config = config;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds))
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EuCallTracker/1.0 (.NET; local SME monitoring)");
    }

    public Task<List<CallRecord>> ScrapeAsync(SourceConfig source)
    {
        return source.Type.Equals("rss", StringComparison.OrdinalIgnoreCase)
            ? ScrapeRssAsync(source)
            : ScrapeWebAsync(source);
    }

    private async Task<List<CallRecord>> ScrapeWebAsync(SourceConfig source)
    {
        var pageHtml = await _httpClient.GetStringAsync(source.Url);
        var baseUri = new Uri(source.Url);
        var candidates = ExtractLinks(pageHtml, baseUri)
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Where(x => LooksLikeCandidate(x, source))
            .ToList();

        var items = new List<CallRecord>();
        var detailLimit = source.DetailFetchLimit ?? _config.DetailFetchLimit;

        for (var i = 0; i < candidates.Count; i++)
        {
            var link = candidates[i];
            var detailHtml = "";
            var title = link.Text;

            if (source.FetchDetails && i < detailLimit)
            {
                try
                {
                    detailHtml = await _httpClient.GetStringAsync(link.Url);
                    title = ExtractTitle(detailHtml) ?? title;
                    await DelayAsync();
                }
                catch
                {
                    detailHtml = "";
                }
            }

            var combined = $"{title} {link.Text} {link.Url} {HtmlToText(detailHtml)}";
            var score = ScoreText(combined);
            if (score.Value < (source.MinItemScore ?? _config.MinItemScore))
            {
                continue;
            }

            var deadline = ExtractDeadline(combined);
            items.Add(new CallRecord
            {
                Source = source.Name,
                SourceLevel = source.Level,
                SourceUrl = source.Url,
                Title = Normalize(title),
                Url = link.Url,
                Summary = Summarize(HtmlToText(detailHtml).Length > 0 ? HtmlToText(detailHtml) : link.Text),
                Deadline = deadline,
                Status = InferStatus(deadline),
                Score = score.Value,
                MatchedKeywords = string.Join(", ", score.Matches)
            });
        }

        return items;
    }

    private async Task<List<CallRecord>> ScrapeRssAsync(SourceConfig source)
    {
        var xml = await _httpClient.GetStringAsync(source.Url);
        var document = XDocument.Parse(xml);
        var nodes = document.Descendants().Where(x =>
            x.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase) ||
            x.Name.LocalName.Equals("entry", StringComparison.OrdinalIgnoreCase));

        var items = new List<CallRecord>();
        foreach (var node in nodes)
        {
            var title = ChildText(node, "title");
            var link = ChildText(node, "link");
            var linkAttribute = node.Elements().FirstOrDefault(x => x.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase))?.Attribute("href")?.Value;
            link = string.IsNullOrWhiteSpace(link) ? linkAttribute ?? "" : link;
            var summary = ChildText(node, "description");
            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = ChildText(node, "summary");
            }

            var published = ChildText(node, "pubDate");
            if (string.IsNullOrWhiteSpace(published))
            {
                published = ChildText(node, "published");
            }

            var combined = $"{title} {summary} {link}";
            var score = ScoreText(combined);
            if (score.Value < (source.MinItemScore ?? _config.MinItemScore))
            {
                continue;
            }

            var deadline = ExtractDeadline(combined);
            items.Add(new CallRecord
            {
                Source = source.Name,
                SourceLevel = source.Level,
                SourceUrl = source.Url,
                Title = Normalize(title),
                Url = new Uri(new Uri(source.Url), link).ToString(),
                Summary = Summarize(HtmlToText(summary)),
                Deadline = deadline,
                Published = published,
                Status = InferStatus(deadline),
                Score = score.Value,
                MatchedKeywords = string.Join(", ", score.Matches)
            });
        }

        return items;
    }

    private bool LooksLikeCandidate(LinkItem link, SourceConfig source)
    {
        var text = $"{link.Text} {link.Url}";
        var score = ScoreText(text);
        if (score.Value >= (source.MinLinkScore ?? _config.MinLinkScore))
        {
            return true;
        }

        var normalized = Key(text);
        return source.RequiredLinkTerms.Select(Key).Any(term => normalized.Contains(term, StringComparison.Ordinal));
    }

    private (int Value, IReadOnlyList<string> Matches) ScoreText(string text)
    {
        var haystack = Key(text);
        var score = 0;
        var matches = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        AddMatches(_config.Filters.IncludeKeywords, 2);
        AddMatches(_config.Filters.SmeKeywords, 4);
        AddMatches(_config.Filters.SerbiaKeywords, 3);
        AddMatches(_config.Filters.ExcludeKeywords, -4, prefix: "-");

        return (score, matches.ToList());

        void AddMatches(IEnumerable<string> keywords, int weight, string prefix = "")
        {
            foreach (var keyword in keywords.Select(Key).Where(x => x.Length > 0))
            {
                if (haystack.Contains(keyword, StringComparison.Ordinal))
                {
                    score += weight;
                    matches.Add(prefix + keyword);
                }
            }
        }
    }

    private async Task DelayAsync()
    {
        if (_config.RequestPauseSeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(_config.RequestPauseSeconds));
        }
    }

    private static List<LinkItem> ExtractLinks(string html, Uri baseUri)
    {
        var links = new List<LinkItem>();
        foreach (Match match in LinkRegex.Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (href.StartsWith("#", StringComparison.Ordinal) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, href, out var absolute))
            {
                continue;
            }

            var text = HtmlToText(match.Groups["text"].Value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                links.Add(new LinkItem(absolute.ToString(), text));
            }
        }

        return links;
    }

    private static string? ExtractTitle(string html)
    {
        var h1 = H1Regex.Match(html);
        if (h1.Success)
        {
            return HtmlToText(h1.Groups["title"].Value);
        }

        var title = TitleRegex.Match(html);
        return title.Success ? HtmlToText(title.Groups["title"].Value) : null;
    }

    private static string ExtractDeadline(string text)
    {
        var patterns = new[]
        {
            @"(?:deadline|closing date|submission deadline|expires|valid until)\s*[:\-]?\s*([0-3]?\d[./-][01]?\d[./-](?:20)?\d{2})",
            @"(?:rok|rok za prijavu|krajnji rok|datum zatvaranja)\s*[:\-]?\s*([0-3]?\d[./-][01]?\d[./-](?:20)?\d{2})",
            @"([0-3]?\d[./-][01]?\d[./-]20\d{2})",
            @"(20\d{2}-[01]\d-[0-3]\d)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return NormalizeDate(match.Groups[1].Value);
            }
        }

        return "";
    }

    private static string NormalizeDate(string value)
    {
        var formats = new[] { "d.M.yyyy", "dd.MM.yyyy", "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "yyyy-MM-dd" };
        return DateOnly.TryParseExact(value.Trim(), formats, null, System.Globalization.DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd")
            : value.Trim();
    }

    private static string InferStatus(string deadline)
    {
        if (string.IsNullOrWhiteSpace(deadline))
        {
            return "unknown";
        }

        return DateOnly.TryParse(deadline, out var date) && date >= DateOnly.FromDateTime(DateTime.Today) ? "open" : "closed";
    }

    private static string ChildText(XElement node, string childName)
    {
        return Normalize(node.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(childName, StringComparison.OrdinalIgnoreCase))?.Value ?? "");
    }

    private static string HtmlToText(string value)
    {
        return Normalize(WebUtility.HtmlDecode(TagRegex.Replace(value, " ")));
    }

    private static string Summarize(string value, int limit = 420)
    {
        var text = Normalize(value);
        if (text.Length <= limit)
        {
            return text;
        }

        var trimmed = text[..Math.Min(limit - 1, text.Length)];
        var lastSpace = trimmed.LastIndexOf(' ');
        return (lastSpace > 80 ? trimmed[..lastSpace] : trimmed) + "...";
    }

    private static string Normalize(string value)
    {
        return Regex.Replace(value ?? "", @"\s+", " ").Trim();
    }

    private static string Key(string value)
    {
        return Normalize(value).ToLowerInvariant();
    }

    private sealed record LinkItem(string Url, string Text);
}

static class ReportRenderer
{
    public static string RenderHtml(IReadOnlyList<CallRecord> rows, bool liveMode = false)
    {
        var generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var cards = rows.Count == 0
            ? "<section class=\"empty\">Nema rezultata u lokalnoj bazi. Pokreni komandu update.</section>"
            : string.Join(Environment.NewLine, rows.Select(RenderCard));

        return $@"<!doctype html>
<html lang=""sr"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>EU pozivi za MSP iz Srbije</title>
  <style>
    :root {{
      --bg: #f5f7f3;
      --text: #1d2728;
      --muted: #65716b;
      --line: #d8ded4;
      --brand: #0b6f6a;
      --header: #103c43;
      --card: #ffffff;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font: 15px/1.5 system-ui, -apple-system, Segoe UI, sans-serif;
    }}
    header {{
      background: var(--header);
      color: white;
      padding: 32px clamp(16px, 5vw, 56px);
    }}
    header h1 {{
      margin: 0 0 8px;
      font-size: clamp(26px, 5vw, 44px);
      letter-spacing: 0;
    }}
    header p {{ margin: 0; color: #d8e8e7; max-width: 820px; }}
    main {{
      width: min(1120px, calc(100% - 32px));
      margin: 24px auto 56px;
      display: grid;
      gap: 14px;
    }}
    .toolbar {{
      display: flex;
      justify-content: space-between;
      gap: 16px;
      align-items: center;
      color: var(--muted);
      flex-wrap: wrap;
    }}
    .call {{
      background: var(--card);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 18px;
    }}
    .meta {{
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      margin-bottom: 8px;
    }}
    .meta span {{
      border: 1px solid var(--line);
      border-radius: 999px;
      padding: 3px 9px;
      color: var(--muted);
      font-size: 13px;
      background: #fbfcf8;
    }}
    h2 {{
      font-size: 20px;
      line-height: 1.25;
      margin: 0 0 8px;
      letter-spacing: 0;
    }}
    a {{ color: var(--brand); text-decoration-thickness: 1px; }}
    p {{ margin: 0 0 14px; color: #384243; }}
    dl {{
      display: grid;
      grid-template-columns: 120px 1fr;
      gap: 6px 12px;
      margin: 0;
      border-top: 1px solid var(--line);
      padding-top: 12px;
    }}
    dt {{ color: var(--muted); }}
    dd {{ margin: 0; }}
    .empty {{
      background: white;
      border: 1px dashed var(--line);
      border-radius: 8px;
      padding: 32px;
      text-align: center;
      color: var(--muted);
    }}
  </style>
</head>
<body>
  <header>
    <h1>EU pozivi za MSP iz Srbije</h1>
    <p>Pregled otvorenih i potencijalno relevantnih poziva iz konfigurisanih EU, nacionalnih, regionalnih i lokalnih izvora.</p>
  </header>
  <main>
    <section class=""toolbar"">
      <strong>{rows.Count} rezultata</strong>
      <span>{(liveMode ? "Lokalni web pregled" : "Izveštaj")} | Generisano: {Html(generated)}</span>
    </section>
    {cards}
  </main>
</body>
</html>";
    }

    public static string RenderCsv(IReadOnlyList<CallRecord> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Title,Source,SourceLevel,Deadline,Status,Score,MatchedKeywords,Url,Summary,FirstSeen,LastSeen");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", new[]
            {
                Csv(row.Title),
                Csv(row.Source),
                Csv(row.SourceLevel),
                Csv(row.Deadline),
                Csv(row.Status),
                row.Score.ToString(),
                Csv(row.MatchedKeywords),
                Csv(row.Url),
                Csv(row.Summary),
                Csv(row.FirstSeen.ToString("u")),
                Csv(row.LastSeen.ToString("u"))
            }));
        }

        return builder.ToString();
    }

    private static string RenderCard(CallRecord row)
    {
        var deadline = string.IsNullOrWhiteSpace(row.Deadline) ? "nije pronađen" : row.Deadline;
        return $@"<article class=""call"">
  <div class=""meta"">
    <span>{Html(row.SourceLevel)}</span>
    <span>{Html(row.Source)}</span>
    <span>score {row.Score}</span>
  </div>
  <h2><a href=""{Html(row.Url)}"" target=""_blank"" rel=""noopener"">{Html(row.Title)}</a></h2>
  <p>{Html(row.Summary)}</p>
  <dl>
    <dt>Rok</dt><dd>{Html(deadline)}</dd>
    <dt>Status</dt><dd>{Html(row.Status)}</dd>
    <dt>Ključne reči</dt><dd>{Html(row.MatchedKeywords)}</dd>
  </dl>
</article>";
    }

    private static string Html(string value)
    {
        return HtmlEncoder.Default.Encode(value ?? "");
    }

    private static string Csv(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    }
}

static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

static class ApplicationReportRenderer
{
    public static string RenderHtml(IReadOnlyList<CallRecord> rows, bool liveMode = false)
    {
        var generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var cards = rows.Count == 0
            ? "<section class=\"empty\">Nema rezultata u lokalnoj bazi. Pokreni komandu update.</section>"
            : string.Join(Environment.NewLine, rows.Select(RenderCard));

        return $@"<!doctype html>
<html lang=""sr"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>EU pozivi za MSP iz Srbije, Malte i Slovenije</title>
  <style>
    :root {{
      --bg: #f5f7f3;
      --text: #1d2728;
      --muted: #65716b;
      --line: #d8ded4;
      --brand: #0b6f6a;
      --brand-dark: #0f4f4c;
      --header: #103c43;
      --card: #ffffff;
      --soft: #fbfcf8;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font: 15px/1.5 system-ui, -apple-system, Segoe UI, sans-serif;
    }}
    header {{
      background: var(--header);
      color: white;
      padding: 32px clamp(16px, 5vw, 56px);
    }}
    header h1 {{
      margin: 0 0 8px;
      font-size: clamp(26px, 5vw, 44px);
      letter-spacing: 0;
    }}
    header p {{ margin: 0; color: #d8e8e7; max-width: 880px; }}
    .header-actions {{
      display: flex;
      gap: 10px;
      flex-wrap: wrap;
      margin-top: 18px;
    }}
    .nav-link {{
      min-height: 38px;
      display: inline-flex;
      align-items: center;
      border: 1px solid rgba(255,255,255,.34);
      border-radius: 6px;
      padding: 8px 12px;
      color: white;
      background: rgba(255,255,255,.08);
      font-weight: 700;
      text-decoration: none;
    }}
    .nav-link:hover {{ background: rgba(255,255,255,.15); }}
    main {{
      width: min(1180px, calc(100% - 32px));
      margin: 24px auto 56px;
      display: grid;
      gap: 14px;
    }}
    .toolbar {{
      display: flex;
      justify-content: space-between;
      gap: 16px;
      align-items: center;
      color: var(--muted);
      flex-wrap: wrap;
    }}
    .call {{
      background: var(--card);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 18px;
    }}
    .meta {{
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      margin-bottom: 8px;
    }}
    .meta span {{
      border: 1px solid var(--line);
      border-radius: 999px;
      padding: 3px 9px;
      color: var(--muted);
      font-size: 13px;
      background: var(--soft);
    }}
    h2 {{
      font-size: 20px;
      line-height: 1.25;
      margin: 0 0 8px;
      letter-spacing: 0;
    }}
    a {{ color: var(--brand); text-decoration-thickness: 1px; }}
    p {{ margin: 0 0 14px; color: #384243; }}
    dl {{
      display: grid;
      grid-template-columns: 120px 1fr;
      gap: 6px 12px;
      margin: 0;
      border-top: 1px solid var(--line);
      padding-top: 12px;
    }}
    dt {{ color: var(--muted); }}
    dd {{ margin: 0; }}
    .apply-panel {{
      margin-top: 14px;
      border-top: 1px solid var(--line);
      padding-top: 14px;
    }}
    .apply-panel summary {{
      width: max-content;
      min-width: 96px;
      min-height: 38px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      border: 1px solid var(--brand);
      border-radius: 6px;
      background: var(--brand);
      color: white;
      cursor: pointer;
      font-weight: 700;
      list-style: none;
      padding: 8px 14px;
    }}
    .apply-panel summary::-webkit-details-marker {{ display: none; }}
    .apply-panel[open] summary {{
      background: var(--brand-dark);
      border-color: var(--brand-dark);
      margin-bottom: 14px;
    }}
    .route-note {{
      margin: 0 0 12px;
      color: #384243;
      font-weight: 600;
    }}
    .apply-grid {{
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 14px;
    }}
    .doc-group {{
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 14px;
      background: var(--soft);
    }}
    .doc-group h3 {{
      margin: 0 0 8px;
      font-size: 15px;
      letter-spacing: 0;
    }}
    .doc-group ul {{
      margin: 0;
      padding-left: 20px;
    }}
    .doc-group li {{ margin: 5px 0; }}
    .actions {{
      display: flex;
      gap: 8px;
      align-items: center;
      flex-wrap: wrap;
      margin: 14px 0 0;
    }}
    .external-apply {{
      min-height: 38px;
      display: inline-flex;
      align-items: center;
      border: 1px solid var(--line);
      border-radius: 6px;
      padding: 8px 12px;
      background: white;
      color: var(--brand);
      font-weight: 700;
    }}
    .empty {{
      background: white;
      border: 1px dashed var(--line);
      border-radius: 8px;
      padding: 32px;
      text-align: center;
      color: var(--muted);
    }}
    @media (max-width: 760px) {{
      .apply-grid {{ grid-template-columns: 1fr; }}
      dl {{ grid-template-columns: 96px 1fr; }}
    }}
  </style>
</head>
<body>
  <header>
    <h1>EU pozivi za MSP iz Srbije, Malte i Slovenije</h1>
    <p>Pregled otvorenih i potencijalno relevantnih poziva, sa Apply checklistom za dokumente koje treba pripremiti.</p>
    <nav class=""header-actions"" aria-label=""Dodatne stranice"">
      <a class=""nav-link"" href=""alert-sources.html"">Alert izvori</a>
      <a class=""nav-link"" href=""usa-readiness.html"">USA plan</a>
      <a class=""nav-link"" href=""open-calls.csv"">CSV za Excel</a>
    </nav>
  </header>
  <main>
    <section class=""toolbar"">
      <strong>{rows.Count} rezultata</strong>
      <span>{(liveMode ? "Lokalni web pregled" : "Izvestaj")} | Generisano: {Html(generated)}</span>
    </section>
    {cards}
  </main>
</body>
</html>";
    }

    public static string RenderCsv(IReadOnlyList<CallRecord> rows)
    {
        return ReportRenderer.RenderCsv(rows);
    }

    private static string RenderCard(CallRecord row)
    {
        var deadline = string.IsNullOrWhiteSpace(row.Deadline) ? "nije pronadjen" : row.Deadline;
        var guide = ApplicationGuide.For(row);
        return $@"<article class=""call"">
  <div class=""meta"">
    <span>{Html(row.SourceLevel)}</span>
    <span>{Html(row.Source)}</span>
    <span>score {row.Score}</span>
  </div>
  <h2><a href=""{Html(row.Url)}"" target=""_blank"" rel=""noopener"">{Html(row.Title)}</a></h2>
  <p>{Html(row.Summary)}</p>
  <dl>
    <dt>Rok</dt><dd>{Html(deadline)}</dd>
    <dt>Status</dt><dd>{Html(row.Status)}</dd>
    <dt>Ruta</dt><dd>{Html(guide.Route)}</dd>
    <dt>Kljucne reci</dt><dd>{Html(row.MatchedKeywords)}</dd>
  </dl>
  <details class=""apply-panel"">
    <summary>Apply</summary>
    <p class=""route-note"">{Html(guide.Note)}</p>
    <div class=""apply-grid"">
      {RenderDocGroup("1. Firma i eligibility", guide.CompanyDocuments)}
      {RenderDocGroup("2. Projekat", guide.ProjectDocuments)}
      {RenderDocGroup("3. Budzet i finansije", guide.FinanceDocuments)}
      {RenderDocGroup("4. Partneri i podnosenje", guide.SubmissionDocuments)}
    </div>
    <div class=""actions"">
      <a class=""external-apply"" href=""{Html(row.Url)}"" target=""_blank"" rel=""noopener"">Otvori zvanicni poziv</a>
    </div>
  </details>
</article>";
    }

    private static string RenderDocGroup(string title, IReadOnlyList<string> items)
    {
        var listItems = string.Join("", items.Select(item => $"<li>{Html(item)}</li>"));
        return $@"<section class=""doc-group"">
  <h3>{Html(title)}</h3>
  <ul>{listItems}</ul>
</section>";
    }

    private static string Html(string value)
    {
        return HtmlEncoder.Default.Encode(value ?? "");
    }
}

sealed record AlertSource(
    string Name,
    string Category,
    string Countries,
    string Frequency,
    string Setup,
    string Notes,
    string Url,
    int Priority);

static class AlertSourcesPage
{
    private static readonly AlertSource[] Sources =
    {
        new(
            "Cascade Funding Hub by Sploro",
            "EU cascade funding / FSTP",
            "Serbia, Malta, Slovenia, EU partners",
            "Weekly plus monthly analysis",
            "Subscribe to alerts and news. Track open calls, July/monthly analysis, events and Matcher.",
            "Best first radar for AID4SME-style calls, smaller equity-free grants and fast SME open calls.",
            "https://cascadefunding.eu/",
            1),
        new(
            "FundingBox / OnePass",
            "Cascade funding, tech access, startup programmes",
            "Serbia, Malta, Slovenia, EU-wide",
            "Newsletter and platform updates",
            "Create account, subscribe to newsletter and save relevant funding opportunities.",
            "Strong for EU project-run calls, digital innovation, communities and partner programmes.",
            "https://fundingbox.com/",
            1),
        new(
            "F6S",
            "Application portal and startup funding hub",
            "Serbia, Malta, Slovenia, global startup routes",
            "Account notifications per followed programme",
            "Create complete company/founder profiles for all entities. Follow F6Sinnovation and relevant calls.",
            "Mandatory or common portal for many EU cascade calls, including AID4SME.",
            "https://www.f6s.com/",
            1),
        new(
            "EU Funding & Tenders Portal",
            "Official EU grants and tenders",
            "Serbia where associated/eligible, Malta, Slovenia",
            "Saved searches and portal notifications",
            "Use EU Login. Save searches for SME, AI, data, cybersecurity, circular, health, energy and EIC.",
            "Primary source for Horizon Europe, EIC, Digital Europe, Single Market Programme and tenders.",
            "https://ec.europa.eu/info/funding-tenders/opportunities/portal/screen/home",
            1),
        new(
            "EIC and EISMEA",
            "EIC, SME support and innovation ecosystems",
            "Malta, Slovenia, Serbia where eligible",
            "News, events and calls",
            "Follow EIC funding pages, EISMEA news and events. Watch Accelerator, Transition, Pathfinder and STEP.",
            "Important for deep tech, scale-up, dual-use, corporate pilots and EIC business acceleration.",
            "https://eismea.ec.europa.eu/index_en",
            1),
        new(
            "Enterprise Europe Network",
            "SME support, partnering and access to finance",
            "Serbia, Malta, Slovenia",
            "Local contact points and news",
            "Contact local EEN offices for all three countries. Ask for EU funding, partner search and access-to-finance alerts.",
            "Useful because human advisors can surface calls that portals miss and help with partners.",
            "https://een.ec.europa.eu/",
            1),
        new(
            "Serbia NGO Dissemination Route",
            "NGO IT support, outreach and training partner",
            "Serbia NGO plus SME/consortium projects",
            "Use per project and donor calls",
            "Position the NGO as dissemination, community outreach, digital skills, IT support or civic-tech partner.",
            "Useful when calls need stakeholder engagement, public-benefit impact, workshops, civil society reach or digital inclusion.",
            "https://euresurscentar.bos.rs/",
            1),
        new(
            "OpenCalls.fund",
            "EU project open calls",
            "EU-wide, depends on each call",
            "Newsletter",
            "Subscribe to newsletter and monitor Active/Upcoming calls.",
            "Good for agrifood, bioeconomy, water, rural and circular-economy calls.",
            "https://opencalls.fund/",
            2),
        new(
            "EIT Opportunities",
            "EIT communities, accelerators and SME programmes",
            "Malta, Slovenia, Serbia where programme allows",
            "Current opportunities page plus newsletters",
            "Track EIT Digital, Food, Health, Manufacturing, RawMaterials and Urban Mobility.",
            "Good source for startup programmes, pilots, grants, venture building and sector accelerators.",
            "https://www.eit.europa.eu/our-activities/opportunities",
            2),
        new(
            "EIT Global Outreach / The DeepTechers",
            "Deep-tech ecosystem and partner route",
            "Europe and Horizon Europe associated countries",
            "Programme pages and application windows",
            "Track DeepTechers, EIT Hub Israel and Global Outreach opportunities; use for partner building, not as a direct SME grant.",
            "Useful for Horizon Europe consortium discovery, EIT partner access, Israeli/Danish ecosystem links and travel-scholarship opportunities.",
            "https://www.go-eit.eu/the-deeptechers/",
            2),
        new(
            "EIT High-Value Startup Calls",
            "EIT startup grants, venture support and pilots",
            "Malta, Slovenia, Serbia where Horizon Europe association/eligibility allows",
            "EIT Opportunities page plus KIC call pages",
            "Track 28DIGITAL Co-Creation, EIT Health Innovation Uptake, Transformative Healthcare Instrument and EIT Urban Mobility startup finance.",
            "These are stronger than generic newsletters because some calls include EUR 250k, EUR 500k, EUR 650k or larger startup financing routes.",
            "https://www.eit.europa.eu/our-activities/opportunities",
            1),
        new(
            "EIT Education, NEB and Skills Calls",
            "EIT training, NEB, RIS and micro-credentials",
            "Serbia NGO, Malta/Slovenia SME, universities and city partners",
            "EIT Opportunities page plus KIC call pages",
            "Track NEB Academy, RIS Education, UMX, EIT Health micro-credentials and Biotech Innovation Path.",
            "Good route when our NGO provides dissemination, IT platform support, training delivery, learner recruitment or impact reporting.",
            "https://www.eit.europa.eu/our-activities/opportunities",
            2),
        new(
            "Israel Innovation Authority",
            "Israel grants, pilots and Horizon support",
            "Israeli partner/entity route",
            "Programmes and calls pages",
            "Monitor bilateral R&D, Horizon participation support, pilots, incubators and startup programmes.",
            "Relevant if we add an Israeli partner or need an Israel-facing deep-tech/commercialisation route.",
            "https://innovationisrael.org.il/en/programs/",
            3),
        new(
            "AWS Activate",
            "Startup cloud credits",
            "Global startup route",
            "Continuous application",
            "Track AWS credits only as non-cash infrastructure support and keep it separate from the Microsoft-stack product architecture.",
            "Useful if a pilot, partner or AI workload can consume AWS credits without changing the core stack.",
            "https://aws.amazon.com/startups/credits/",
            3),
        new(
            "BioInnovation Institute and DTU Skylab",
            "Denmark deep-tech, life science and university innovation",
            "Denmark partner route",
            "Programme pages and calls",
            "Monitor BII Venture Lab, Bio Studio, BII Quantum Lab and DTU Skylab startup support.",
            "Relevant if there is a life science, quantum, robotics, hardware or university-pilot angle with a Danish partner.",
            "https://bii.dk/programs/",
            3),
        new(
            "28Digital Open Innovation Factory",
            "Digital startup matching, EU pilots and cascade funding",
            "Serbia, Malta, Slovenia listed in EOI country selector",
            "Continuous EOI",
            "Submit expression of interest for startup profiles and keep it updated.",
            "Low-effort intake that may route the company to pilots, grants, corporates or investment paths.",
            "https://28digital.eu/open-innovation-factory",
            2),
        new(
            "NGI Open Calls",
            "Next Generation Internet and open-source grants",
            "Usually EU / Horizon Europe eligible countries",
            "Open calls page",
            "Monitor open calls and project-specific eligibility. Use if solution has open-source, privacy or internet infrastructure angle.",
            "Good for smaller grants and technical proposals, but eligibility varies by sub-call.",
            "https://www.ngi.eu/opencalls/",
            2),
        new(
            "EuroAccess",
            "EU funding search and newsletter",
            "Serbia, Malta, Slovenia and many cooperation regions",
            "Newsletter after registration",
            "Register, select SME/private company, countries and themes, then save calls in funding basket.",
            "Very useful for Interreg, cohesion and territorial cooperation searches across countries.",
            "https://www.euro-access.eu/en",
            2),
        new(
            "European Cluster Collaboration Platform",
            "Euroclusters and SME vouchers",
            "Malta, Slovenia, Serbia where call allows",
            "Open calls page",
            "Monitor Euroclusters, vouchers, resilience, green/digital transition and internationalisation calls.",
            "Important because many SME cascade calls are not visible on the main EU portal.",
            "https://clustercollaboration.eu/open-calls",
            2),
        new(
            "Cluster Submission Platform",
            "Eurocluster application platform",
            "Malta, Slovenia, Serbia where call allows",
            "Open calls page",
            "Check weekly together with the Cluster Collaboration Platform.",
            "Often where the actual SME application forms and deadlines live.",
            "https://clustersubmissionplatform.eu/",
            2),
        new(
            "Eureka Open Calls",
            "International SME R&D",
            "Serbia, Malta, Slovenia and global partners",
            "Open calls page",
            "Monitor Eurostars, Globalstars, Network Projects and Innowwide.",
            "One of the strongest routes for mixed Serbia-Malta-Slovenia-international R&D structures.",
            "https://www.eurekanetwork.org/open-calls/",
            2),
        new(
            "LIFE Programme",
            "EU environment and climate grants",
            "Malta, Slovenia, Serbia depending on sub-programme",
            "Annual call cycles",
            "Track environment, circular economy, climate mitigation/adaptation and clean-energy-transition calls.",
            "Relevant if we can quantify environmental impact and line up pilot partners.",
            "https://cinea.ec.europa.eu/programmes/life/life-calls-proposals_en",
            2),
        new(
            "Digital Europe Programme",
            "EU AI, data and cyber deployment",
            "Malta, Slovenia and EU consortia; Serbia depending on call",
            "Funding page and Funding & Tenders",
            "Track AI, data spaces, cybersecurity, cloud/edge, digital skills and EDIH routes.",
            "Good for deployment/pilot consortia rather than basic R&D.",
            "https://digital-strategy.ec.europa.eu/en/funding",
            2),
        new(
            "EUIPO SME Fund",
            "EU IP vouchers",
            "EU SMEs: Malta and Slovenia company",
            "Annual voucher window",
            "Track reimbursement windows for trade marks, designs, patents and IP Scan.",
            "Quick practical win before larger grant applications because IP costs are often needed anyway.",
            "https://www.euipo.europa.eu/en/sme-corner/sme-fund",
            2),
        new(
            "CASSINI",
            "EU space entrepreneurship",
            "EU startups and space-enabled SMEs",
            "Accelerator, hackathons and challenge cycles",
            "Track accelerator batches, hackathons, prizes and investor events.",
            "Strong if the product can use GNSS, Copernicus, satellite data, mobility, logistics or climate-risk use cases.",
            "https://www.cassini.eu/accelerator/",
            2),
        new(
            "EDIH, AI-on-Demand and AI Factories",
            "EU AI and digital infrastructure support",
            "Malta, Slovenia, Serbia through local hubs or access routes",
            "Hub/service pages",
            "Track test-before-invest services, AI compute access, AI experiments and TEF routes.",
            "Not always cash grants, but excellent preparation layer for Digital Europe, EIC and pilot applications.",
            "https://european-digital-innovation-hubs.ec.europa.eu/home",
            2),
        new(
            "Eureka Clusters",
            "Industrial R&D consortia",
            "Serbia, Malta, Slovenia and wider international partners",
            "Cluster call cycles",
            "Track CELTIC-NEXT, ITEA, Xecs and Eurogia for ICT, software, electronics and low-carbon energy.",
            "Good for deeper industrial R&D where Eurostars is too small or too generic.",
            "https://www.celticnext.eu/call-information/",
            2),
        new(
            "EU Resource Centre Serbia and EU TACSO",
            "Serbia / Western Balkans civil society support",
            "Serbia NGO",
            "Calls, trainings and capacity-building updates",
            "Monitor civil society grants, CSO digitalisation, capacity building and EU-funded support.",
            "Good radar for NGO-led dissemination, IT support and civil society technology projects.",
            "https://euresurscentar.bos.rs/",
            2),
        new(
            "CERV and Erasmus+",
            "EU civil society, rights, education and digital skills",
            "Serbia NGO, EU partners",
            "Annual and thematic call cycles",
            "Track digital rights, civic participation, youth, digital skills, education and inclusion calls.",
            "Good for NGO-led or NGO-partnered dissemination/training work packages.",
            "https://commission.europa.eu/funding-tenders/find-funding/eu-funding-programmes/citizens-equality-rights-and-values-programme_en",
            2),
        new(
            "EUSPA and ESA Business Applications",
            "European space-enabled business",
            "Malta, Slovenia, Serbia depending on ESA/EU rules",
            "Funding opportunity pages",
            "Track GNSS, Copernicus, satellite data, earth observation and space-enabled services.",
            "Useful if we can frame the product around geolocation, climate data, mobility, agriculture, logistics or infrastructure monitoring.",
            "https://www.euspa.europa.eu/opportunities/funding",
            3),
        new(
            "HaDEA, CBE JU, Clean Hydrogen, IHI, Chips JU, EuroHPC",
            "EU thematic consortium routes",
            "Mostly Malta/Slovenia and EU-associated consortia",
            "Call pages and Funding & Tenders",
            "Monitor by domain: health, bio-based industry, hydrogen, chips, HPC/AI and digital infrastructure.",
            "Bigger and heavier than cascade calls, but good when we have strong partners and a serious pilot.",
            "https://hadea.ec.europa.eu/calls-proposals_en",
            3),
        new(
            "PRIMA, Water4All, Biodiversa+, DUT, EP PerMed",
            "European partnership transnational calls",
            "Depends on national funding rules",
            "Annual or periodic joint calls",
            "Track water, agriculture, biodiversity, urban transition and personalised medicine calls.",
            "Useful when we can build a research/pilot consortium with strong national funding alignment.",
            "https://prima-med.org/calls-for-proposals/",
            3),
        new(
            "ECCC Cybersecurity Funding",
            "EU cybersecurity calls",
            "EU/Digital Europe consortia; Serbia depending on call",
            "Funding opportunities page",
            "Track cyber, SOC, secure AI, data protection and critical-infrastructure calls.",
            "Important for any cybersecurity or dual-use software angle.",
            "https://cybersecurity-centre.europa.eu/funding-opportunities_en",
            3),
        new(
            "InvestEU, EIF and EIB",
            "EU finance, guarantees and venture debt",
            "Malta, Slovenia, Serbia through intermediaries where available",
            "Finance/investment pages",
            "Track investor visibility, financial intermediaries, venture debt and guarantees.",
            "Not grants, but important once the project needs co-financing or scale-up capital.",
            "https://investeu.europa.eu/investment-opportunities/investeu-portal_en",
            3),
        new(
            "Interreg Italy-Slovenia",
            "Cross-border cooperation",
            "Slovenia company and Italy-Slovenia partners",
            "Newsletter and calls page",
            "Subscribe and monitor Open Calls, small-scale projects and partner search.",
            "Relevant because the Slovenia company can be a direct route into EU territorial cooperation.",
            "https://www.ita-slo.eu/en",
            2),
        new(
            "Interreg Central Europe",
            "Transnational cooperation",
            "Slovenia plus Central Europe partners",
            "Newsletter subscription",
            "Subscribe to newsletter and monitor call timeline.",
            "Best for consortium projects with public/research/cluster partners; SMEs often join as partners.",
            "https://www.interreg-central.eu/",
            2),
        new(
            "Malta Enterprise",
            "Malta national SME and innovation support",
            "Malta company",
            "News, events and support measures",
            "Monitor support measures, EU Affairs, R&D, startup finance, Innovate and Eurostars pages.",
            "Key source for the Malta company route and national co-financing/support schemes.",
            "https://maltaenterprise.com/",
            2),
        new(
            "Innovation Fund Serbia",
            "Serbia national innovation grants",
            "Serbia company",
            "News and programme pages",
            "Monitor Mini Grants, Matching Grants, Smart Start, Katapult, Catalytic and innovation vouchers.",
            "Main Serbian innovation grant radar.",
            "https://www.inovacionifond.rs/",
            2),
        new(
            "SBIR.gov",
            "USA federal SBIR/STTR gateway",
            "US entity route",
            "Solicitations and agency updates",
            "Create search rhythm for NSF, NIH, DOE, NASA, DoD, USDA and NIST topics.",
            "Central map for US small-business R&D grants once the USA entity is active.",
            "https://www.sbir.gov/",
            2),
        new(
            "NSF America's Seed Fund",
            "USA non-dilutive deep-tech seed route",
            "US small business route",
            "Project Pitch cycles and solicitations",
            "Prepare Project Pitch and track topic windows.",
            "Best first US grant route for broad deep-tech startups.",
            "https://seedfund.nsf.gov/",
            2),
        new(
            "NIH SEED",
            "USA health SBIR/STTR",
            "US small business route",
            "Funding opportunity updates",
            "Monitor SBIR/STTR opportunities if product has digital health, medtech, biotech or clinical workflow fit.",
            "Main US radar for health and biomedical small-business funding.",
            "https://seed.nih.gov/small-business-funding/find-funding/sbir-sttr-funding-opportunities",
            2),
        new(
            "Defense Innovation Unit",
            "USA defense commercial solutions",
            "US or allied-vendor route, call-specific",
            "Active Commercial Solutions Openings",
            "Monitor problem statements and keep a one-page capability brief ready.",
            "Interesting because DIU can work with commercial and international vendors when the problem statement allows it.",
            "https://www.diu.mil/solutions/portfolio",
            2),
        new(
            "DOE SBIR/STTR",
            "USA energy and hard-tech R&D",
            "US small business route",
            "Topic releases and deadlines",
            "Track Phase I topics and letter-of-intent deadlines for energy, climate, grid, materials and manufacturing.",
            "Strong route if the product touches energy, climate, industrial AI or advanced manufacturing.",
            "https://science.osti.gov/sbir/Funding-Opportunities",
            3),
        new(
            "NASA SBIR/STTR",
            "USA space, robotics and aerospace R&D",
            "US small business route",
            "Solicitation cycles",
            "Monitor annual solicitations and match technology to NASA subtopics.",
            "Useful for autonomy, sensors, robotics, aerospace, materials, climate observation and space tech.",
            "https://sbir.nasa.gov/",
            3),
        new(
            "ARPA-H",
            "USA frontier health R&D",
            "Opportunity-specific performer route",
            "Funding opportunity announcements",
            "Monitor open opportunities and programme managers if health moonshot angle exists.",
            "High-risk high-reward route for ambitious health technology platforms.",
            "https://arpa-h.gov/",
            3),
        new(
            "DARPA Small Business Programs",
            "USA defense frontier R&D",
            "US small business / defense route",
            "SBIR/STTR and BAA updates",
            "Monitor topics only for truly frontier technical claims with defense relevance.",
            "Harder route, but important for dual-use deep-tech and advanced systems.",
            "https://www.darpa.mil/work-with-us/for-small-businesses",
            3),
        new(
            "Challenge.gov",
            "USA federal prize competitions",
            "Eligibility varies by challenge",
            "Open challenge listings",
            "Set alerts for AI, cyber, health, energy, climate, data and small business challenges.",
            "Useful because some prizes are lighter than grants and may allow broader participation.",
            "https://www.challenge.gov/",
            3),
        new(
            "Development Agency of Serbia",
            "Serbia SME, export and supplier-chain support",
            "Serbia company",
            "Public calls and news",
            "Monitor public calls, SME support, export promotion and supplier-chain programmes.",
            "Good for practical SME support, export, trade fairs and supplier development.",
            "https://www.ras.gov.rs/",
            3),
        new(
            "SPIRIT Slovenia",
            "Slovenia tenders, export and competitiveness",
            "Slovenia company",
            "Tenders and news",
            "Monitor public tenders, entrepreneurship, internationalisation and competitiveness calls.",
            "Important national radar for the Slovenia entity.",
            "https://www.spiritslovenia.si/",
            3),
        new(
            "Slovene Enterprise Fund",
            "Slovenia SME grants, vouchers and finance",
            "Slovenia company",
            "Tenders page",
            "Monitor startup incentives, vouchers, guarantees, loans and SME development finance.",
            "High value for Slovenian SME route when new calls open.",
            "https://www.podjetniskisklad.si/",
            3),
        new(
            "Horizon Europe Missions",
            "EU mission-oriented R&I",
            "Malta, Slovenia, Serbia where Horizon rules allow",
            "Mission pages and Funding & Tenders",
            "Track climate adaptation, cancer, smart cities, soil and ocean/water topics.",
            "Good for consortium projects where software, AI/data or NGO dissemination supports a public-impact pilot.",
            "https://research-and-innovation.ec.europa.eu/funding/funding-opportunities/funding-programmes-and-open-calls/horizon-europe/eu-missions-horizon-europe_en",
            3),
        new(
            "Common European Data Spaces",
            "EU data economy and Digital Europe",
            "Malta, Slovenia and EU consortia; Serbia depending on call",
            "Digital strategy and Funding & Tenders",
            "Track data spaces in health, agriculture, manufacturing, energy, mobility, public administration, skills, tourism and media.",
            "Strong fit when we can provide AI, data integration, interoperability, cybersecurity or sector software.",
            "https://digital-strategy.ec.europa.eu/en/policies/data-spaces",
            3),
        new(
            "ESF+ and Digital Skills",
            "EU skills, inclusion and employment",
            "Mostly Malta/Slovenia national or regional routes; NGO partner route",
            "National/regional calls plus ESF+ updates",
            "Watch digital-skills, training, inclusion, youth, women and workforce upskilling calls.",
            "Good place for the Serbian NGO as training/dissemination partner when a project has public-benefit delivery.",
            "https://european-social-fund-plus.ec.europa.eu/en",
            3),
        new(
            "European DIGITAL SME Alliance",
            "ICT SME ecosystem and partner radar",
            "EU and neighbouring ICT SME ecosystem",
            "News, newsletters and project updates",
            "Track ICT SME policy, AI, cybersecurity, standards, digital skills and partner opportunities.",
            "Not a direct grant source, but useful for consortium discovery and early signals around digital SME support.",
            "https://www.digitalsme.eu/",
            3),
        new(
            "CORDIS / Horizon Magazine",
            "EU R&I news and project intelligence",
            "EU-wide",
            "Newsletter and editorial updates",
            "Subscribe for research and innovation topics close to the product domain.",
            "Not mainly a grant-alert service, but useful for finding trends, funded projects and partner targets.",
            "https://cordis.europa.eu/",
            3)
    };

    public static string RenderHtml(bool liveMode = false)
    {
        var generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var priorityOne = Sources.Count(x => x.Priority == 1);
        var cards = string.Join(Environment.NewLine, Sources
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Name)
            .Select(RenderCard));

        return $@"<!doctype html>
<html lang=""sr"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>Alert izvori za EU SME pozive</title>
  <style>
    :root {{
      --bg: #f5f7f3;
      --text: #1d2728;
      --muted: #65716b;
      --line: #d8ded4;
      --brand: #0b6f6a;
      --header: #103c43;
      --card: #ffffff;
      --soft: #fbfcf8;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font: 15px/1.5 system-ui, -apple-system, Segoe UI, sans-serif;
    }}
    header {{
      background: var(--header);
      color: white;
      padding: 32px clamp(16px, 5vw, 56px);
    }}
    header h1 {{
      margin: 0 0 8px;
      font-size: clamp(26px, 5vw, 44px);
      letter-spacing: 0;
    }}
    header p {{ margin: 0; color: #d8e8e7; max-width: 920px; }}
    .header-actions {{
      display: flex;
      gap: 10px;
      flex-wrap: wrap;
      margin-top: 18px;
    }}
    .nav-link {{
      min-height: 38px;
      display: inline-flex;
      align-items: center;
      border: 1px solid rgba(255,255,255,.34);
      border-radius: 6px;
      padding: 8px 12px;
      color: white;
      background: rgba(255,255,255,.08);
      font-weight: 700;
      text-decoration: none;
    }}
    main {{
      width: min(1180px, calc(100% - 32px));
      margin: 24px auto 56px;
      display: grid;
      gap: 14px;
    }}
    .toolbar {{
      display: flex;
      justify-content: space-between;
      gap: 16px;
      align-items: center;
      color: var(--muted);
      flex-wrap: wrap;
    }}
    .setup {{
      background: var(--card);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 18px;
    }}
    .setup h2, .source h2 {{
      font-size: 20px;
      line-height: 1.25;
      margin: 0 0 8px;
      letter-spacing: 0;
    }}
    .setup ol {{
      margin: 10px 0 0;
      padding-left: 22px;
    }}
    .source {{
      background: var(--card);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 18px;
    }}
    .meta {{
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      margin-bottom: 8px;
    }}
    .meta span {{
      border: 1px solid var(--line);
      border-radius: 999px;
      padding: 3px 9px;
      color: var(--muted);
      font-size: 13px;
      background: var(--soft);
    }}
    p {{ margin: 0 0 14px; color: #384243; }}
    dl {{
      display: grid;
      grid-template-columns: 140px 1fr;
      gap: 6px 12px;
      margin: 0;
      border-top: 1px solid var(--line);
      padding-top: 12px;
    }}
    dt {{ color: var(--muted); }}
    dd {{ margin: 0; }}
    a {{ color: var(--brand); text-decoration-thickness: 1px; }}
    .external {{
      min-height: 38px;
      display: inline-flex;
      align-items: center;
      border: 1px solid var(--line);
      border-radius: 6px;
      padding: 8px 12px;
      margin-top: 14px;
      background: white;
      color: var(--brand);
      font-weight: 700;
    }}
    @media (max-width: 760px) {{
      dl {{ grid-template-columns: 1fr; }}
    }}
  </style>
</head>
<body>
  <header>
    <h1>Alert izvori za EU SME pozive</h1>
    <p>Newsletteri, hubovi i portali koje treba ukljuciti da bismo hvatali EU, kaskadna, cross-border i nacionalna finansiranja za firme u Srbiji, Malti i Sloveniji.</p>
    <nav class=""header-actions"" aria-label=""Navigacija"">
      <a class=""nav-link"" href=""open-calls.html"">Nazad na pozive</a>
      <a class=""nav-link"" href=""usa-readiness.html"">USA plan</a>
    </nav>
  </header>
  <main>
    <section class=""toolbar"">
      <strong>{Sources.Length} alert izvora | {priorityOne} prioritet #1</strong>
      <span>{(liveMode ? "Lokalni web pregled" : "Izvestaj")} | Generisano: {Html(generated)}</span>
    </section>
    <section class=""setup"">
      <h2>Minimalni setup</h2>
      <p>Ovo je prakticni redosled da se ne propusta dobar poziv.</p>
      <ol>
        <li>Jedan Outlook folder: EU Grants Radar, sa pravilima za newslettere i portale.</li>
        <li>EU Login, F6S, FundingBox/OnePass i Cascade Funding nalog odmah.</li>
        <li>Saved searches: AI, data, cybersecurity, digital health, circular economy, energy, SME, startup, Serbia, Malta, Slovenia.</li>
        <li>Jednom nedeljno: otvoriti prioritet #1 izvore i uneti nove pozive u tracker.</li>
        <li>Jednom mesecno: proveriti Interreg, EIT, Malta Enterprise, Serbia Innovation Fund i Slovenian sources.</li>
      </ol>
    </section>
    {cards}
  </main>
</body>
</html>";
    }

    private static string RenderCard(AlertSource source)
    {
        return $@"<article class=""source"">
  <div class=""meta"">
    <span>Prioritet {source.Priority}</span>
    <span>{Html(source.Category)}</span>
    <span>{Html(source.Countries)}</span>
  </div>
  <h2><a href=""{Html(source.Url)}"" target=""_blank"" rel=""noopener"">{Html(source.Name)}</a></h2>
  <p>{Html(source.Notes)}</p>
  <dl>
    <dt>Update ritam</dt><dd>{Html(source.Frequency)}</dd>
    <dt>Sta podesiti</dt><dd>{Html(source.Setup)}</dd>
  </dl>
  <a class=""external"" href=""{Html(source.Url)}"" target=""_blank"" rel=""noopener"">Otvori izvor</a>
</article>";
    }

    private static string Html(string value)
    {
        return HtmlEncoder.Default.Encode(value ?? "");
    }
}

sealed record UsaRoute(
    string Name,
    string Type,
    string Fit,
    string Eligibility,
    string Setup,
    string Url,
    int Priority);

static class UsaReadinessPage
{
    private static readonly UsaRoute[] Routes =
    {
        new(
            "SBIR.gov",
            "Federal SBIR/STTR gateway",
            "Master search for US small-business R&D grants across agencies.",
            "Usually requires a US for-profit small business with majority US ownership/control or qualifying structure.",
            "Create saved searches by agency and topic; track solicitations for NSF, NIH, DOE, NASA, DoD and USDA.",
            "https://www.sbir.gov/",
            1),
        new(
            "NSF America's Seed Fund",
            "Non-dilutive deep-tech seed funding",
            "Best broad US route for early deep-tech commercial R&D: AI, robotics, semiconductors, energy, medical devices, advanced manufacturing.",
            "US small business route. Start with Project Pitch before full proposal.",
            "Prepare Project Pitch, technical innovation, market opportunity, team, company formation and ownership evidence.",
            "https://seedfund.nsf.gov/",
            1),
        new(
            "NIH SEED / SBIR-STTR",
            "Health, medtech, biotech, digital health",
            "Strong route for biomedical, diagnostics, health AI, clinical workflow, therapeutics, devices and research tools.",
            "US small business route with NIH-specific registrations and topic fit.",
            "Prepare Specific Aims, research strategy, commercialization plan, biosketches, facilities and human-subjects/regulatory notes.",
            "https://seed.nih.gov/small-business-funding/find-funding/sbir-sttr-funding-opportunities",
            1),
        new(
            "DOE SBIR/STTR",
            "Energy and hard-tech R&D",
            "Good for energy, climate, grid, materials, sensors, nuclear, manufacturing, AI for science and clean-tech.",
            "US small business route, aligned to DOE topic documents.",
            "Track Phase I topics, letter of intent requirements and DOE registration deadlines.",
            "https://science.osti.gov/sbir/Funding-Opportunities",
            1),
        new(
            "DIU Commercial Solutions Openings",
            "Defense customer / procurement route",
            "Fastest non-grant route for dual-use products with commercial traction: autonomy, cyber, AI, energy, logistics, space, sensors.",
            "DIU says it works with US and international vendors from allied countries; each CSO has its own restrictions.",
            "Create capability one-pagers and map product to active DIU problem statements.",
            "https://www.diu.mil/solutions/portfolio",
            1),
        new(
            "AFWERX SBIR/STTR",
            "Air Force dual-use innovation",
            "Good for commercial products with defense relevance and fast pilot potential.",
            "Generally US small business route; foreign parent/ownership must be checked carefully.",
            "Prepare defense use case, customer discovery, technical volume and transition/customer letters.",
            "https://afwerx.com/",
            2),
        new(
            "NASA SBIR/STTR",
            "Space, robotics, sensors, autonomy, materials",
            "Relevant if tech supports space systems, aerospace, robotics, autonomy, climate observation or advanced materials.",
            "US small business route with NASA topic alignment.",
            "Track NASA solicitations and prepare topic-specific technical proposal and commercialization plan.",
            "https://sbir.nasa.gov/",
            2),
        new(
            "ARPA-H",
            "High-risk health moonshots",
            "Useful for ambitious health technology platforms and breakthrough biomedical systems.",
            "Often broad performer model; eligibility depends on the specific opportunity.",
            "Monitor open funding opportunities and build a technical thesis around ARPA-H program goals.",
            "https://arpa-h.gov/",
            2),
        new(
            "DARPA Small Business Programs",
            "Defense frontier R&D",
            "Good only for very high-risk frontier technology with defense relevance.",
            "Usually US small business or defense-contracting route; foreign ownership/control matters.",
            "Track SBIR/STTR topics and BAAs. Prepare technical white paper and security/export-control checks.",
            "https://www.darpa.mil/work-with-us/for-small-businesses",
            2),
        new(
            "Challenge.gov",
            "US federal prize competitions",
            "Good for occasional open prizes where foreign participation may be allowed by challenge rules.",
            "Eligibility varies by challenge; some are open globally, some require US persons/entities.",
            "Set alerts for AI, cybersecurity, energy, health, climate, small business and data challenges.",
            "https://www.challenge.gov/",
            2),
        new(
            "Grants.gov",
            "US federal grant notice search",
            "Useful for USAID, Department of State, DOE, USDA, NOAA and agency-specific notices.",
            "Eligibility varies heavily. Many notices require US registration, SAM.gov or local implementing partner.",
            "Create saved searches for Serbia, Western Balkans, private sector, innovation, SME, AI, cybersecurity and climate.",
            "https://www.grants.gov/search-grants",
            2)
    };

    public static string RenderHtml(bool liveMode = false)
    {
        var generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var cards = string.Join(Environment.NewLine, Routes
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Name)
            .Select(RenderCard));

        return $@"<!doctype html>
<html lang=""sr"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>USA readiness za grantove i federalne programe</title>
  <style>
    :root {{
      --bg: #f5f7f3;
      --text: #1d2728;
      --muted: #65716b;
      --line: #d8ded4;
      --brand: #0b6f6a;
      --header: #103c43;
      --card: #ffffff;
      --soft: #fbfcf8;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font: 15px/1.5 system-ui, -apple-system, Segoe UI, sans-serif;
    }}
    header {{
      background: var(--header);
      color: white;
      padding: 32px clamp(16px, 5vw, 56px);
    }}
    header h1 {{
      margin: 0 0 8px;
      font-size: clamp(26px, 5vw, 44px);
      letter-spacing: 0;
    }}
    header p {{ margin: 0; color: #d8e8e7; max-width: 920px; }}
    .header-actions {{
      display: flex;
      gap: 10px;
      flex-wrap: wrap;
      margin-top: 18px;
    }}
    .nav-link {{
      min-height: 38px;
      display: inline-flex;
      align-items: center;
      border: 1px solid rgba(255,255,255,.34);
      border-radius: 6px;
      padding: 8px 12px;
      color: white;
      background: rgba(255,255,255,.08);
      font-weight: 700;
      text-decoration: none;
    }}
    main {{
      width: min(1180px, calc(100% - 32px));
      margin: 24px auto 56px;
      display: grid;
      gap: 14px;
    }}
    .toolbar {{
      display: flex;
      justify-content: space-between;
      gap: 16px;
      align-items: center;
      color: var(--muted);
      flex-wrap: wrap;
    }}
    .panel, .route {{
      background: var(--card);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 18px;
    }}
    .panel h2, .route h2 {{
      font-size: 20px;
      line-height: 1.25;
      margin: 0 0 8px;
      letter-spacing: 0;
    }}
    .panel ol, .panel ul {{
      margin: 10px 0 0;
      padding-left: 22px;
    }}
    .grid {{
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 14px;
    }}
    .meta {{
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      margin-bottom: 8px;
    }}
    .meta span {{
      border: 1px solid var(--line);
      border-radius: 999px;
      padding: 3px 9px;
      color: var(--muted);
      font-size: 13px;
      background: var(--soft);
    }}
    p {{ margin: 0 0 14px; color: #384243; }}
    dl {{
      display: grid;
      grid-template-columns: 130px 1fr;
      gap: 6px 12px;
      margin: 0;
      border-top: 1px solid var(--line);
      padding-top: 12px;
    }}
    dt {{ color: var(--muted); }}
    dd {{ margin: 0; }}
    a {{ color: var(--brand); text-decoration-thickness: 1px; }}
    .external {{
      min-height: 38px;
      display: inline-flex;
      align-items: center;
      border: 1px solid var(--line);
      border-radius: 6px;
      padding: 8px 12px;
      margin-top: 14px;
      background: white;
      color: var(--brand);
      font-weight: 700;
    }}
    @media (max-width: 760px) {{
      .grid {{ grid-template-columns: 1fr; }}
      dl {{ grid-template-columns: 1fr; }}
    }}
  </style>
</head>
<body>
  <header>
    <h1>USA readiness za grantove i federalne programe</h1>
    <p>Prakticna mapa za US firmu koju planiramo da otvorimo: koje rute pratiti, sta traze i sta spremiti pre prvog SBIR/STTR ili defense/health/energy pitch-a.</p>
    <nav class=""header-actions"" aria-label=""Navigacija"">
      <a class=""nav-link"" href=""open-calls.html"">Nazad na pozive</a>
      <a class=""nav-link"" href=""alert-sources.html"">Alert izvori</a>
    </nav>
  </header>
  <main>
    <section class=""toolbar"">
      <strong>{Routes.Length} USA ruta</strong>
      <span>{(liveMode ? "Lokalni web pregled" : "Izvestaj")} | Generisano: {Html(generated)}</span>
    </section>
    <section class=""panel"">
      <h2>Prvo srediti</h2>
      <ol>
        <li>Odabrati strukturu: US C-Corp ili LLC, uz proveru SBIR ownership/control pravila.</li>
        <li>Pripremiti SAM.gov, UEI, EIN, banku, accounting i IP assignment chain.</li>
        <li>Mapirati tehnologiju na 3 agencije: NSF za broad deep-tech, NIH za health, DOE/NASA/DoD za sector-specific R&D.</li>
        <li>Napraviti jedan federalni capability deck: problem, novelty, TRL, customer, dual-use, team, budget, milestones.</li>
        <li>Pre svake aplikacije proveriti foreign ownership, export control, data/security i US work location zahteve.</li>
      </ol>
    </section>
    <section class=""grid"">
      <section class=""panel"">
        <h2>SBIR/STTR dokumenti</h2>
        <ul>
          <li>Company registration, EIN, UEI/SAM.gov status and ownership cap table</li>
          <li>Principal investigator eligibility and employment status</li>
          <li>Technical abstract, specific aims and work plan</li>
          <li>Commercialization plan, market validation and letters of support</li>
          <li>Budget, indirect cost assumptions and consultant/subaward quotes</li>
        </ul>
      </section>
      <section class=""panel"">
        <h2>Najbolji redosled</h2>
        <ol>
          <li>NSF Project Pitch za broad deep-tech.</li>
          <li>NIH ako je digital health / medtech / biotech.</li>
          <li>DOE ako je energy, climate, materials or manufacturing.</li>
          <li>DIU/AFWERX ako ima dual-use customer fit.</li>
          <li>Challenge.gov i Grants.gov kao radar, ne kao glavni sales funnel.</li>
        </ol>
      </section>
    </section>
    {cards}
  </main>
</body>
</html>";
    }

    private static string RenderCard(UsaRoute route)
    {
        return $@"<article class=""route"">
  <div class=""meta"">
    <span>Prioritet {route.Priority}</span>
    <span>{Html(route.Type)}</span>
  </div>
  <h2><a href=""{Html(route.Url)}"" target=""_blank"" rel=""noopener"">{Html(route.Name)}</a></h2>
  <p>{Html(route.Fit)}</p>
  <dl>
    <dt>Eligibility</dt><dd>{Html(route.Eligibility)}</dd>
    <dt>Setup</dt><dd>{Html(route.Setup)}</dd>
  </dl>
  <a class=""external"" href=""{Html(route.Url)}"" target=""_blank"" rel=""noopener"">Otvori rutu</a>
</article>";
    }

    private static string Html(string value)
    {
        return HtmlEncoder.Default.Encode(value ?? "");
    }
}

sealed record ApplicationChecklist(
    string Route,
    string Note,
    IReadOnlyList<string> CompanyDocuments,
    IReadOnlyList<string> ProjectDocuments,
    IReadOnlyList<string> FinanceDocuments,
    IReadOnlyList<string> SubmissionDocuments);

static class ApplicationGuide
{
    public static ApplicationChecklist For(CallRecord row)
    {
        var text = $"{row.Title} {row.Source} {row.SourceLevel} {row.Summary} {row.MatchedKeywords}".ToLowerInvariant();
        var route = GetRoute(text);
        var note = GetNote(route, text);

        var company = new List<string>
        {
            "Company registration extract: APR for Serbia, MBR for Malta, or equivalent register",
            "SME declaration: employees, turnover, balance sheet total, linked/partner enterprises",
            "Ownership and UBO structure, with authorised representative details",
            "Tax ID, VAT number if applicable, bank account confirmation",
            "Short company profile and reference projects"
        };

        var project = new List<string>
        {
            "One-page project concept: problem, solution, target users, expected result",
            "Work plan with work packages, milestones, deliverables and timeline",
            "Market and impact section: customers, traction, benefits and KPIs",
            "Team CVs for key people and role allocation",
            "Risk table: technical, commercial, regulatory and delivery risks"
        };

        var finance = new List<string>
        {
            "Detailed budget by partner and cost category",
            "Supplier quotes or cost assumptions for equipment, services and subcontracting",
            "Latest financial statements, management accounts or balance sheet",
            "State-aid, de minimis and double-funding declarations where requested",
            "Cash-flow plan for pre-financing and co-financing"
        };

        var submission = new List<string>
        {
            "Official call text and applicant guide saved locally",
            "Eligibility checklist completed before writing the full proposal",
            "Authorised signatory decision or power of attorney",
            "Submission portal account: EU Login, F6S, national portal or programme portal",
            "Final PDF/application package and proof of submission"
        };

        AddSpecificItems(text, company, project, finance, submission);

        return new ApplicationChecklist(
            route,
            note,
            company.Distinct().ToList(),
            project.Distinct().ToList(),
            finance.Distinct().ToList(),
            submission.Distinct().ToList());
    }

    private static string GetRoute(string text)
    {
        if (text.Contains("hong kong") || text.Contains("china") || text.Contains("hkstp") || text.Contains("innovation and technology fund")) return "Hong Kong / China entity route";
        if (text.Contains("cbe ju") || text.Contains("circular bio-based") || text.Contains("bio-based") || text.Contains("biorefinery") || text.Contains("bioeconomy")) return "EU bioeconomy consortium route";
        if (text.Contains("ligawine") || text.Contains("fs4africa") || text.Contains("ofelia") || text.Contains("agrifood") || text.Contains("viticulture") || text.Contains("winery") || text.Contains("food safety")) return "EU agrifood cascade route";
        if (text.Contains("katalitik") || text.Contains("catalytic co-invest") || text.Contains("co-investing") || text.Contains("co-investiraju") || text.Contains("ai startapi") || (text.Contains("ai startup") && text.Contains("serbia")) || text.Contains("serbia ventures ai") || text.Contains("saige")) return "Serbia AI startup co-investment route";
        if (text.Contains("circular solutions for communities") || text.Contains("cirkularna resenja") || text.Contains("circular economy in local communities") || text.Contains("undp serbia") || (text.Contains("gef") && text.Contains("serbia") && text.Contains("circular"))) return "Serbia circular economy community route";
        if (text.Contains("eu values and policies in serbia") || (text.Contains("eu values") && text.Contains("serbia")) || text.Contains("eu vrednosti") || text.Contains("delegacija eu") || text.Contains("eu delegation to serbia") || text.Contains("local initiatives service contract")) return "Serbia EU communication micro-contract route";
        if (text.Contains("visegrad+") || text.Contains("visegrad grants") || text.Contains("v4 gen") || text.Contains("western balkans fund")) return "Western Balkans regional cooperation route";
        if (text.Contains("serbia ngo") || text.Contains("serbian ngo") || text.Contains("civil society") || text.Contains("civilno drustvo") || text.Contains("civic tech") || text.Contains("tacso") || text.Contains("cerv") || text.Contains("erasmus") || text.Contains("eu resource centre") || text.Contains("euresurs") || text.Contains("techsoup")) return "Serbia NGO / dissemination route";
        if (text.Contains("eit jumpstarter") || text.Contains("28digital") || text.Contains("co-creation accelerator") || text.Contains("spin: rise") || text.Contains("innonext") || text.Contains("innovation uptake call") || text.Contains("transformative healthcare instrument") || text.Contains("financial support to startups")) return "EIT startup / venture route";
        if (text.Contains("ris education") || text.Contains("micro-credentials") || text.Contains("biotech innovation path") || text.Contains("urban mobility explained") || text.Contains("umx open call")) return "EIT education / skills route";
        if (text.Contains("kava") || text.Contains("eit rawmaterials") || text.Contains("rawmaterials")) return "EIT raw materials consortium route";
        if (text.Contains("canada") || text.Contains("canadian") || text.Contains("nrc irap") || text.Contains("canexport")) return "Canada entity or partner route";
        if (text.Contains("usa") || text.Contains("united states") || text.Contains("nsf") || text.Contains("sbir") || text.Contains("sttr") || text.Contains("grants.gov")) return "USA entity or partner route";
        if (text.Contains("uk ") || text.Contains("united kingdom") || text.Contains("innovate uk") || text.Contains("ukri") || text.Contains("aria") || text.Contains("contracts for innovation")) return "UK entity or partner route";
        if (text.Contains("euroclusters") || text.Contains("cluster submission") || text.Contains("voucher")) return "EU SME voucher / cascade route";
        if (text.Contains("euipo") || text.Contains("sme fund") || text.Contains("ip voucher")) return "EU SME IP voucher route";
        if (text.Contains("edih") || text.Contains("digital innovation hubs")) return "EU digital hub support route";
        if (text.Contains("eureka cluster") || text.Contains("celtic-next") || text.Contains("itea") || text.Contains("xecs") || text.Contains("eurogia")) return "Eureka cluster consortium route";
        if (text.Contains("blueinvest") || text.Contains("emfaf") || text.Contains("blue economy") || text.Contains("maritime")) return "EU blue economy route";
        if (text.Contains("data spaces") || text.Contains("data economy") || text.Contains("interoperability")) return "EU data spaces route";
        if (text.Contains("horizon europe missions") || text.Contains("mission-oriented") || text.Contains("climate-neutral and smart cities") || text.Contains("soil health") || text.Contains("ocean/water")) return "EU missions route";
        if (text.Contains("life programme") || text.Contains("innovation fund") || text.Contains("climate") || text.Contains("clean tech")) return "EU climate and environment route";
        if (text.Contains("digital europe") || text.Contains("eurohpc") || text.Contains("chips ju") || text.Contains("ai factories") || text.Contains("ai-on-demand") || text.Contains("cybersecurity competence")) return "EU digital/deep-tech consortium route";
        if (text.Contains("esf plus") || text.Contains("esf+") || text.Contains("european social fund") || text.Contains("digital skills")) return "EU skills and inclusion route";
        if (text.Contains("digital sme") || text.Contains("ict sme")) return "EU digital SME ecosystem route";
        if (text.Contains("deeptechers") || text.Contains("eit global outreach") || text.Contains("israel innovation authority") || text.Contains("innovation centre denmark") || text.Contains("aws activate") || text.Contains("bioinnovation institute") || text.Contains("dtu skylab")) return "DeepTech ecosystem route";
        if (text.Contains("esa business") || text.Contains("euspa") || text.Contains("space-enabled") || text.Contains("space applications") || text.Contains("space data") || text.Contains("copernicus") || text.Contains("galileo") || text.Contains("gnss") || text.Contains("satellite")) return "European space route";
        if (text.Contains("defence") || text.Contains("defense") || text.Contains("dual-use") || text.Contains("nato diana")) return "Defence / dual-use partner route";
        if (text.Contains("eib") || text.Contains("eif") || text.Contains("investeu") || text.Contains("venture debt")) return "EU finance route";
        if (text.Contains("i3") || text.Contains("interregional innovation investments")) return "EU interregional innovation route";
        if (text.Contains("creative europe") || text.Contains("new european bauhaus") || text.Contains("bauhaus") || text.Contains("neb academy") || text.Contains("eit culture") || text.Contains("creative industries") || text.Contains("digital culture")) return "EU creative / NEB route";
        if (text.Contains("cost action") || text.Contains("cost open call")) return "European networking route";
        if (text.Contains("slovenia") || text.Contains("slovene") || text.Contains("slovenian")) return "Slovenia company";
        if (text.Contains("malta")) return "Malta company";
        if (text.Contains("serbia") || text.Contains("srbija") || text.Contains("western balkans")) return "Serbia / Western Balkans";
        if (text.Contains("cascade") || text.Contains("f6s") || text.Contains("ngi") || text.Contains("fstp")) return "EU cascade funding";
        if (text.Contains("eurostars") || text.Contains("eureka") || text.Contains("consortium")) return "International consortium";
        if (text.Contains("monitoring") || text.Contains("partner")) return "Partner or monitoring route";
        return "EU / national grant route";
    }

    private static string GetNote(string route, string text)
    {
        if (route == "Malta company")
        {
            return "Najverovatnije aplicira Malta firma. Proveri da li troskovi, zaposleni i aktivnost moraju biti vezani za Maltu.";
        }

        if (route == "Slovenia company")
        {
            return "Najverovatnije aplicira slovenacka firma. Proveri da li sediste, zaposleni, troskovi i projekat moraju biti vezani za Sloveniju.";
        }

        if (route == "Serbia / Western Balkans")
        {
            return "Najverovatnije aplicira srpska firma ili konzorcijum sa Srbijom. Proveri status Srbije u konkretnom programu.";
        }

        if (route == "Serbia AI startup co-investment route")
        {
            return "Srpska firma aplicira, ali mora da ima AI prototip i investicioni deo spreman. Proveri founder ownership, co-investment odnos i ciklus evaluacije.";
        }

        if (route == "Serbia circular economy community route")
        {
            return "Ovo je Srbija/UNDP/GEF ruta za lokalne cirkularne projekte. Najbolje radi ako imamo javni interes, lokalnog partnera i merljive ekoloske indikatore.";
        }

        if (route == "Serbia EU communication micro-contract route")
        {
            return "Ovo je mikro-ugovor za EU vidljivost, ne klasicni grant. Dobar je za srpski NGO, evente, edukaciju, javnu komunikaciju i dissemination.";
        }

        if (route == "Western Balkans regional cooperation route")
        {
            return "Regionalna ruta obicno trazi partnere iz vise zemalja. Dobra je za NGO, edukaciju, inovacije, mlade, kulturu, policy ili cross-border saradnju.";
        }

        if (route == "EU cascade funding")
        {
            return "Obicno je kraci obrazac, ali rokovi su brzi. Prvo proveri eligibility drzave, SME status i portal za podnosenje.";
        }

        if (route == "International consortium")
        {
            return "Pripremi partnere rano. Ovakvi pozivi cesto traze partnere iz razlicitih zemalja i jasnu podelu budzeta.";
        }

        if (route == "EU SME voucher / cascade route")
        {
            return "Ovo je obicno brza SME/voucher ruta. Prvo proveri zemlje koje smeju direktno da apliciraju i da li je bolja Malta ili Slovenija firma.";
        }

        if (route == "EU agrifood cascade route")
        {
            return "Ovo je FSTP/cascade agrifood ruta. Treba brzo naci use-case partnera: winery, food business, innovation hub, farmer/producer ili regional cluster.";
        }

        if (route == "EU bioeconomy consortium route")
        {
            return "Ovo je teza Horizon/JU bioeconomy ruta. Treba industrijski konzorcijum, feedstock/value-chain logika, LCA/SSbD i jasan exploitation/business plan.";
        }

        if (route == "EU SME IP voucher route")
        {
            return "Direktna i prakticna ruta za IP troskove. Najpre proveri da li aplicira EU firma, pa pripremi trade mark/design/patent plan.";
        }

        if (route == "EU digital hub support route")
        {
            return "Ovo cesto nije cash grant, nego usluge i test-before-invest. Dobro za pripremu vecih AI/cyber/digital aplikacija.";
        }

        if (route == "Eureka cluster consortium route")
        {
            return "Industrijski R&D konzorcijum. Potrebni su partneri, nacionalna pravila finansiranja i jasna komercijalna mapa.";
        }

        if (route == "EU climate and environment route")
        {
            return "Najvaznije je dokazati merljiv klimatski/ekoloski uticaj, pilot lokaciju i co-financing. Cesto je konzorcijum bolji od solo prijave.";
        }

        if (route == "EU digital/deep-tech consortium route")
        {
            return "Ovo je najcesce konzorcijumska ili infrastruktura ruta. Potrebni su partneri, pilot korisnici i jasan tehnicki doprinos.";
        }

        if (route == "EU data spaces route")
        {
            return "Dobra ruta za software, AI, integracije, interoperabilnost i cyber, ali skoro uvek kroz konzorcijum i sektor-specific pilot.";
        }

        if (route == "EU missions route")
        {
            return "Mission ruta trazi jasan societal impact: klima, gradovi, zdravlje, ocean/voda ili soil. NGO moze biti jak za outreach i stakeholder work package.";
        }

        if (route == "EU skills and inclusion route")
        {
            return "Ovo je vise skills/inclusion ruta nego R&D grant. Dobro za NGO, training, digital skills i javni interes, posebno kroz nacionalne programe.";
        }

        if (route == "EU digital SME ecosystem route")
        {
            return "Nije direktan grant, ali je dobar radar za ICT SME partnerstva, standarde, cyber/AI teme i konzorcijume.";
        }

        if (route == "EIT startup / venture route")
        {
            return "EIT ruta je dobra za startup/pilot/venture podrsku, ali uslovi jako variraju. Prvo proveri PIC, zemlju, starost firme, TRL/prototype, co-funding i da li treba EIT partner.";
        }

        if (route == "EIT education / skills route")
        {
            return "Ovo je EIT education/training ruta. Dobra za NGO, univerzitete, edukaciju, micro-credentials i dissemination, ali mora da postoji ozbiljan curriculum i partner delivery model.";
        }

        if (route == "EIT raw materials consortium route")
        {
            return "KAVA/RawMaterials je teza konzorcijumska ruta: validirana tehnologija, partner iz EIT RawMaterials mreze, co-funding i plan finansijske odrzivosti.";
        }

        if (route == "DeepTech ecosystem route")
        {
            return "Ovo je partnerstvo, credits ili ecosystem ruta, ne klasicni SME grant. Vredi ako trazimo EIT/Horizon partnere, Izrael/Danska/BII/DTU vezu ili deep-tech pozicioniranje.";
        }

        if (route == "European space route")
        {
            return "Koristi ako proizvod realno koristi satelitske podatke, GNSS, Copernicus ili space-enabled servis. Eligibility zavisi od ESA/EU pravila.";
        }

        if (route == "Defence / dual-use partner route")
        {
            return "Pre pisanja proveri eligibility, vlasnistvo, security/export-control i da li Malta/Slovenija/USA struktura najbolje odgovara.";
        }

        if (route == "EU finance route")
        {
            return "Ovo nije klasicni grant. Pripremi investment case, finansije i dokaz rasta; koristi kao sloj finansiranja uz grantove.";
        }

        if (route == "EU interregional innovation route")
        {
            return "Ovo je regionalna/value-chain ruta. Treba dokazati pametnu specijalizaciju, regionalne partnere i investicioni efekat.";
        }

        if (route == "EU creative / NEB route")
        {
            return "Koristi samo ako imamo realan creative, culture, design, sustainability ili community angle, najbolje sa NGO/creative partnerima.";
        }

        if (route == "EU blue economy route")
        {
            return "Jaka ruta za Maltu i Mediteran ako postoji maritime, aquaculture, ocean data, ports, logistics ili coastal climate use case.";
        }

        if (route == "European networking route")
        {
            return "Ovo nije razvojni grant, vec networking/training/dissemination ruta. Korisno za partnerstva pre vecih EU proposal-a.";
        }

        if (route == "UK entity or partner route")
        {
            return "Najcesce treba UK registered business, UK delivery ili UK lead. Koristi ovo samo ako imamo UK partnera ili ozbiljan plan za UK entitet.";
        }

        if (route == "USA entity or partner route")
        {
            return "Najcesce treba US entity, SAM.gov/UEI ili US partner. Pre rada na aplikaciji prvo potvrdi eligibility za strane firme.";
        }

        if (route == "Canada entity or partner route")
        {
            return "Najcesce treba Canadian SME ili kanadski konzorcijumski partner. Dobra ruta za partnerstvo ili buducu lokalnu strukturu.";
        }

        if (route == "Hong Kong / China entity route")
        {
            return "Najcesce treba Hong Kong company, accelerator base ili lokalni partner. Koristi samo ako ima jasan China/HK market-entry razlog.";
        }

        if (route == "Serbia NGO / dissemination route")
        {
            return "Ovo je ruta za srpski NGO: dissemination, community outreach, digital skills, civil society IT support ili impact partner u konzorcijumu.";
        }

        if (text.Contains("monitoring"))
        {
            return "Ovo je monitoring ruta, ne gotov poziv. Koristi checklistu za pripremu dok cekas konkretan notice ili cut-off.";
        }

        return "Otvori zvanicni poziv i prvo potvrdi eligibility, rok, intenzitet finansiranja i obavezne anekse.";
    }

    private static void AddSpecificItems(string text, List<string> company, List<string> project, List<string> finance, List<string> submission)
    {
        var isCbeBioeconomy = text.Contains("cbe ju") || text.Contains("circular bio-based") || text.Contains("bio-based") || text.Contains("biorefinery") || text.Contains("bioeconomy");

        if (text.Contains("eic accelerator"))
        {
            project.Add("EIC pitch deck and 3-minute pitch video");
            project.Add("Freedom-to-operate and IP position");
            finance.Add("Investment readiness plan and use of funds");
            submission.Add("EU Funding & Tenders Portal profile, PIC number and EIC AI platform forms");
        }

        if (text.Contains("serbia ngo") || text.Contains("serbian ngo") || text.Contains("civil society") || text.Contains("civilno drustvo") || text.Contains("civic tech") || text.Contains("tacso") || text.Contains("cerv") || text.Contains("erasmus") || text.Contains("eu resource centre") || text.Contains("euresurs") || text.Contains("techsoup"))
        {
            company.Add("Serbian NGO registration extract, statute and authorised representative evidence");
            company.Add("Non-profit status, governing bodies and conflict-of-interest declaration");
            company.Add("References for IT support, training, community outreach or civil society projects");
            project.Add("Dissemination and communication plan: target groups, channels, events and KPIs");
            project.Add("Stakeholder/community engagement plan and participant recruitment approach");
            project.Add("Training/workshop curriculum or digital support methodology");
            project.Add("Impact narrative for civil society, digital inclusion, media literacy or cyber hygiene");
            finance.Add("NGO staff time, trainers, communication, events, travel and participant support budget");
            finance.Add("Evidence of co-financing or in-kind contribution if the donor requires it");
            submission.Add("Partner role letter for the NGO: dissemination, IT support, training or community outreach");
            submission.Add("GDPR/privacy, consent forms and safeguarding policy if working with participants or youth");
        }

        if (text.Contains("katalitik") || text.Contains("catalytic co-invest") || text.Contains("co-investing") || text.Contains("co-investiraju") || text.Contains("ai startapi") || (text.Contains("ai startup") && text.Contains("serbia")) || text.Contains("serbia ventures ai") || text.Contains("saige"))
        {
            company.Add("Serbian APR extract, micro/small enterprise evidence and company age confirmation");
            company.Add("Founder ownership and cap table showing majority ownership by the founding team");
            company.Add("Investor documents: signed investment agreement or near-final term sheet");
            project.Add("AI prototype demo, technical architecture and clear AI use case");
            project.Add("Product-market evidence, customer discovery and investor deck");
            project.Add("Implementation roadmap with milestones for grant plus private investment");
            finance.Add("External investment evidence, minimum qualifying investment check and 1:1 match plan");
            finance.Add("Use-of-funds budget for the non-refundable grant and investor funds");
            submission.Add("Innovation Fund Serbia / eInovacije account and mandatory call forms");
            submission.Add("Cycle timing check because applications are reviewed every few months until funds are spent");
        }

        if (text.Contains("circular solutions for communities") || text.Contains("cirkularna resenja") || text.Contains("circular economy in local communities") || text.Contains("undp serbia") || (text.Contains("gef") && text.Contains("serbia") && text.Contains("circular")))
        {
            company.Add("Eligibility evidence for company, NGO, municipality, public utility, research/education body or consortium");
            project.Add("Circular economy use case: reuse, repair, waste reduction, food waste, public space, circular services or renewables for public good");
            project.Add("Local community benefit, implementation plan and stakeholder support");
            project.Add("Environmental indicators and expected circularity impact");
            finance.Add("Budget up to USD 40,000 with any co-financing or in-kind contribution clearly shown");
            submission.Add("UNDP/GEF public-call forms, annexes and rolling evaluation date check");
        }

        if (text.Contains("eu values and policies in serbia") || (text.Contains("eu values") && text.Contains("serbia")) || text.Contains("eu vrednosti") || text.Contains("delegacija eu") || text.Contains("eu delegation to serbia") || text.Contains("local initiatives service contract"))
        {
            company.Add("Serbian legal registration for NGO, foundation, institution, festival or other eligible entity");
            company.Add("References for events, public outreach, communication, education or cultural initiatives");
            project.Add("Activity concept promoting EU values, policies or Serbia-EU accession topics");
            project.Add("Target groups, reach, media/social plan and event/workshop delivery plan");
            finance.Add("Simple service-contract budget between EUR 1,500 and EUR 7,000");
            submission.Add("English application form or offer, delivery timeline and visibility plan");
            submission.Add("Post-delivery report template and evidence package for payment after completion");
        }

        if (text.Contains("visegrad+") || text.Contains("visegrad grants") || text.Contains("v4 gen") || text.Contains("western balkans fund"))
        {
            company.Add("Partner list with V4 and Western Balkans organisations and signed mandates");
            project.Add("Regional cooperation logic and cross-border added value");
            project.Add("Workplan for education, innovation, entrepreneurship, culture, youth, policy or civil society outcomes");
            finance.Add("Partner budget split, travel/event costs and co-financing if required");
            submission.Add("Regional fund portal account and partner declarations");
        }

        if (text.Contains("eic transition") || text.Contains("eic pathfinder"))
        {
            project.Add("Proof of eligible previous research result or project link");
            project.Add("Technology maturation and validation plan");
            submission.Add("Consortium agreement draft if applying with partners");
        }

        if (text.Contains("f6s"))
        {
            company.Add("Complete F6S company and founder profiles");
            project.Add("Short F6S application answers with clear pilot use case");
            submission.Add("F6S application workspace access for all contributors");
        }

        if (text.Contains("aid4sme"))
        {
            company.Add("Decide lead applicant: Serbia, Malta or Slovenia SME/startup");
            company.Add("Confirm one-application-per-entity rule before using multiple companies");
            project.Add("Select exactly one AID4SME challenge code and align every section to it");
            project.Add("Write the Annex 2 proposal in the mandatory AID4SME template");
            project.Add("Define 14-month plan: Plan, Development, Testing & Validation, Assessment");
            project.Add("Define mandatory deliverables: IP Agreement by M4, Test & Validation Report by M10, Business Plan and Exploitation Roadmap by M14");
            finance.Add("Budget for 70% funding rate and maximum EUR 200,000 contribution");
            finance.Add("No subcontracting: keep external work out of the requested project costs");
            finance.Add("Use depreciation only for equipment costs during the project period");
            submission.Add("Upload final Annex 2 proposal PDF through F6S only");
            submission.Add("Prepare Declaration of Honour, SME Declaration and consortium declaration for contracting if selected");
            submission.Add("Submit before 15 July 2026, 17:00 CEST; do not wait for the final hour");
        }

        if (text.Contains("ngi") || text.Contains("open source") || text.Contains("foss"))
        {
            project.Add("Open-source repository or technical architecture outline");
            project.Add("Licensing plan for code, documentation and results");
            finance.Add("Small-grant budget in the requested EUR 5,000-50,000 style");
        }

        if (text.Contains("malta enterprise"))
        {
            company.Add("Malta Business Registry extract and company memorandum/articles if requested");
            company.Add("Jobsplus and Malta tax compliance evidence if requested");
            finance.Add("Invoices, quotations and evidence that costs are eligible under Malta Enterprise rules");
            submission.Add("Malta Enterprise client account or application form");
        }

        if (text.Contains("slovenia") || text.Contains("slovene") || text.Contains("slovenian") || text.Contains("spirit") || text.Contains("sid bank"))
        {
            company.Add("AJPES / Slovenian Business Register extract for the Slovenian company");
            company.Add("Slovenian tax number and VAT evidence if applicable");
            company.Add("Proof of Slovenian establishment, eligible activity and SME status");
            finance.Add("Latest Slovenian annual accounts or interim financial statements");
            finance.Add("Evidence of own contribution or bank/development-bank finance if co-financing is required");
            submission.Add("Slovenian national portal account or agency-specific submission account");
        }

        if (text.Contains("eurostars") || text.Contains("eureka"))
        {
            company.Add("SME lead confirmation and partner country eligibility checks");
            project.Add("International R&D cooperation plan and partner responsibilities");
            finance.Add("Budget split per country according to national funding rules");
            submission.Add("Eureka/Eurostars online application and national annexes");
        }

        if (text.Contains("euroclusters") || text.Contains("cluster submission") || text.Contains("voucher"))
        {
            company.Add("NACE/sector fit evidence and SME declaration for the applying company");
            company.Add("Cluster membership or ecosystem link if the call gives priority to members");
            project.Add("Short voucher use case: problem, service provider, expected output and SME benefit");
            finance.Add("Supplier/service-provider quote and proof that voucher costs are eligible");
            submission.Add("Cluster Submission Platform or Eurocluster portal account");
        }

        if (!isCbeBioeconomy && (text.Contains("ligawine") || text.Contains("fs4africa") || text.Contains("ofelia") || text.Contains("agrifood") || text.Contains("viticulture") || text.Contains("winery") || text.Contains("food safety")))
        {
            company.Add("Agrifood/winery/food-safety sector fit and SME or innovation-hub eligibility evidence");
            project.Add("Use-case partner description: winery, food business, innovation hub, producer, farmer or regional cluster");
            project.Add("Pilot plan for testing, validation, mentoring, training or deployment in a real food/agri environment");
            project.Add("Sustainability and digital-enablement angle: low inputs, traceability, safety, resource efficiency or decision support");
            finance.Add("FSTP budget template with eligible staff, travel, pilot, equipment/service and subcontracting assumptions");
            submission.Add("OpenCalls.fund account, proposal template, declaration of honour, SME declaration and bank-account form");
        }

        if (isCbeBioeconomy)
        {
            company.Add("Consortium role evidence for industry, SME, research, demonstration site or value-chain partner");
            project.Add("Bio-based value-chain map: feedstock, processing, product, market and end-user validation");
            project.Add("LCA, Safe-and-Sustainable-by-Design, circularity and environmental-impact assumptions");
            project.Add("Scale-up or demonstration plan with TRL, pilot assets, permitting and validation metrics");
            finance.Add("Horizon/CBE budget by partner, action type, co-funding assumptions and in-kind/IKAA annex where relevant");
            submission.Add("CBE JU Part B template, business plan annex for IA-Flagship if relevant, and consortium agreement route");
        }

        if (text.Contains("i3") || text.Contains("interregional innovation investments") || text.Contains("single market programme"))
        {
            company.Add("Regional or cluster partner role confirmation and eligible country/region check");
            project.Add("Interregional value-chain map, smart-specialisation fit and scaling logic");
            project.Add("Letters from clusters, regional agencies, pilot sites or industry adopters");
            finance.Add("Investment package, partner budget and co-financing assumptions by region");
            submission.Add("Consortium governance note and lead-partner mandate");
        }

        if (text.Contains("blueinvest") || text.Contains("emfaf") || text.Contains("blue economy") || text.Contains("maritime") || text.Contains("aquaculture"))
        {
            project.Add("Blue economy use case: maritime, aquaculture, ports, ocean data or coastal climate impact");
            project.Add("Pilot partner evidence from a port, marina, aquaculture operator, coastal authority or maritime cluster");
            finance.Add("Marine/field testing, certification, travel and pilot deployment cost assumptions");
            submission.Add("Malta/Mediterranean partner positioning note if applying through the Malta company");
        }

        if (text.Contains("creative europe") || text.Contains("new european bauhaus") || text.Contains("bauhaus") || text.Contains("neb academy") || text.Contains("eit culture") || text.Contains("creative industries") || text.Contains("digital culture"))
        {
            project.Add("Creative, cultural or New European Bauhaus narrative: sustainability, inclusion and design quality");
            project.Add("Audience/community engagement and dissemination concept with NGO or creative partners");
            finance.Add("Content, design, events, communication and creative partner budget lines");
            submission.Add("Portfolio, demo or visual evidence for the creative/design component");
        }

        if (text.Contains("startup europe") || text.Contains("innovation radar"))
        {
            project.Add("Ecosystem positioning: target investors, accelerators, EU projects and partner leads");
            submission.Add("Public profile, pitch deck and EU project references for visibility platforms");
        }

        if (text.Contains("cost action") || text.Contains("cost open call"))
        {
            project.Add("Networking objective, working groups, training schools, workshops and dissemination logic");
            finance.Add("Travel, meeting and dissemination assumptions; COST usually funds networking not product development");
            submission.Add("COST Action proposal structure and management committee country coverage");
        }

        if (text.Contains("cetpartnership") || text.Contains("m-era.net") || text.Contains("quantera") || text.Contains("chist-era"))
        {
            company.Add("National funding eligibility check for every partner country and funding agency");
            project.Add("Transnational R&I concept with research question, pilot path and partner roles");
            finance.Add("Budget aligned to each national or regional funding agency's rules");
            submission.Add("Pre-proposal/full-proposal timeline and partner commitment letters");
        }

        if (text.Contains("erasmus for young entrepreneurs"))
        {
            project.Add("Entrepreneur exchange objective and learning plan");
            submission.Add("Host/new entrepreneur profile and intermediary organisation route");
        }

        if (text.Contains("euipo") || text.Contains("sme fund") || text.Contains("ip voucher"))
        {
            company.Add("EU SME eligibility proof and company registration for the applying Malta/Slovenia entity");
            project.Add("IP protection plan: trade mark, design, patent or IP Scan scope");
            project.Add("List of target countries/classes for trade mark or design filing");
            finance.Add("Expected official fees and reimbursement category per voucher type");
            submission.Add("EUIPO account and bank/payment details for reimbursement");
        }

        if (text.Contains("edih") || text.Contains("digital innovation hubs") || text.Contains("test before invest"))
        {
            project.Add("Digital maturity need: AI, cybersecurity, automation, HPC, data or digital transformation gap");
            project.Add("Test-before-invest plan with expected hub services and pilot outcome");
            finance.Add("Estimate internal time and any co-financed service costs");
            submission.Add("Contact local EDIH hub for Malta, Slovenia or Serbia and request service eligibility");
        }

        if (text.Contains("eureka cluster") || text.Contains("celtic-next") || text.Contains("itea") || text.Contains("xecs") || text.Contains("eurogia"))
        {
            company.Add("National funding eligibility check for each partner country before consortium lock-in");
            project.Add("Project outline: industrial problem, innovation, partner roles and commercial exploitation");
            project.Add("Consortium map with large-company, SME and research partners");
            finance.Add("National budget split and funding-rate assumptions by partner country");
            submission.Add("Eureka cluster portal account and project outline/full project proposal timeline");
        }

        if (text.Contains("innowwide"))
        {
            project.Add("Target-market feasibility plan with local counterpart and validation activities");
            finance.Add("Market feasibility budget and local partner/service-provider costs");
            submission.Add("Eureka/Innowwide portal account and local counterpart declaration");
        }

        if (text.Contains("life programme") || text.Contains("life calls") || text.Contains("innovation fund") || text.Contains("clean tech") || text.Contains("climate-kic") || text.Contains("climate"))
        {
            project.Add("Quantified environmental or climate impact baseline and target KPIs");
            project.Add("Pilot/demo site description and permitting or regulatory assumptions");
            project.Add("Replication and exploitation plan for EU markets");
            finance.Add("Co-financing plan, capital-expenditure assumptions and eligible-cost split");
            submission.Add("Partner letters for pilot sites, municipalities, utilities or industrial hosts");
        }

        if (text.Contains("digital europe") || text.Contains("eurohpc") || text.Contains("chips ju") || text.Contains("eit digital"))
        {
            project.Add("Technical architecture for AI, data, cybersecurity, cloud/edge or compute components");
            project.Add("Pilot user and deployment plan with measurable digital transformation KPIs");
            finance.Add("Cloud/HPC/compute, equipment and specialist labor cost assumptions");
            submission.Add("Consortium role description and letters from pilot users or infrastructure partners");
        }

        if (text.Contains("data spaces") || text.Contains("data economy") || text.Contains("interoperability"))
        {
            project.Add("Data-space use case with target sector, data holders, users and governance model");
            project.Add("Interoperability, semantics, trust, identity, consent and data-sharing architecture note");
            project.Add("Cybersecurity, GDPR and data-access risk assessment");
            submission.Add("Letters from data providers, sector associations, public bodies or pilot users");
        }

        if (text.Contains("horizon europe missions") || text.Contains("mission-oriented") || text.Contains("climate-neutral and smart cities") || text.Contains("soil health") || text.Contains("ocean/water"))
        {
            project.Add("Mission fit statement: climate adaptation, cancer, smart cities, soil, or ocean/water restoration");
            project.Add("Societal-impact indicators, stakeholder engagement and citizen/community involvement plan");
            project.Add("Pilot geography and public-sector/research/NGO partner roles");
            submission.Add("Consortium partner map with mission-specific expertise and end-user letters");
        }

        if (text.Contains("esf plus") || text.Contains("esf+") || text.Contains("european social fund") || text.Contains("digital skills"))
        {
            project.Add("Training or upskilling curriculum with target groups, learning outcomes and delivery model");
            project.Add("Inclusion/public-benefit narrative for youth, women, workers, SMEs or vulnerable groups");
            finance.Add("Trainer, venue, platform, communication, participant-support and evaluation budget");
            submission.Add("National or regional ESF+ managing-authority route check for Malta or Slovenia");
        }

        if (text.Contains("digital sme") || text.Contains("ict sme"))
        {
            company.Add("ICT SME profile, capability statement and sector references");
            project.Add("Partnering note for AI, cyber, standards, digital skills or EU policy-aligned projects");
            submission.Add("Newsletter, member-network or project-partner outreach record");
        }

        if (text.Contains("eit jumpstarter") || text.Contains("28digital") || text.Contains("co-creation accelerator") || text.Contains("spin: rise") || text.Contains("innonext") || text.Contains("innovation uptake call") || text.Contains("transformative healthcare instrument") || text.Contains("financial support to startups"))
        {
            company.Add("PIC number and EU Funding & Tenders organisation registration if requested");
            company.Add("Proof of startup/SME status, incorporation date, FTE count and ownership/cap table");
            company.Add("EIT Community link, KIC support, alumni status or partner registration if the call requires it");
            project.Add("Pitch deck with problem, solution, TRL/prototype, traction, market and team");
            project.Add("IP/control evidence for the technology and freedom-to-operate assumptions");
            project.Add("Pilot or industry-partner challenge statement with validation plan");
            finance.Add("Funding-round, co-funding and private-investment evidence where required");
            finance.Add("Cost plan separating grant support, equity/investment support and in-kind partner contribution");
            submission.Add("EIT/28DIGITAL/EIT Health/EIT Urban Mobility portal account and call manual saved locally");
            submission.Add("Letters from industry partner, healthcare site, city/mobility partner or EIT KIC contact if relevant");
        }

        if (text.Contains("ris education") || text.Contains("micro-credentials") || text.Contains("biotech innovation path") || text.Contains("urban mobility explained") || text.Contains("umx open call"))
        {
            company.Add("Education/training delivery references for NGO, university, accelerator or SME partner");
            company.Add("EIT partner status or partner-onboarding route if the call requires it");
            project.Add("Curriculum outline with learning outcomes, target learners, modules and assessment approach");
            project.Add("EIT Label, micro-credential, vocational training or lifelong-learning compliance note");
            project.Add("Delivery model: trainers, platform, workshops, cities/industry partners and participant recruitment");
            finance.Add("Education-content, trainers, platform, learner support, events and evaluation budget");
            submission.Add("Letters from universities, cities, industry partners, trainers or NGO/community delivery partners");
            submission.Add("Financial sustainability plan for the course after EIT funding");
        }

        if (text.Contains("kava") || text.Contains("eit rawmaterials") || text.Contains("rawmaterials"))
        {
            company.Add("EIT RawMaterials Core/Associate Partner status or membership commitment if selected");
            project.Add("Validated raw/advanced-materials technology evidence and scaling/test plan");
            project.Add("Knowledge-triangle consortium map: industry, research and education partners");
            finance.Add("Minimum 50% co-funding plan and partner-by-partner budget commitments");
            finance.Add("Financial sustainability plan: royalty, equity, revenue share or other return logic");
            submission.Add("SeedBook platform account and KAVA call manual saved locally");
        }

        if (text.Contains("deeptechers") || text.Contains("eit global outreach") || text.Contains("israel innovation authority") || text.Contains("innovation centre denmark") || text.Contains("aws activate") || text.Contains("bioinnovation institute") || text.Contains("dtu skylab"))
        {
            company.Add("Deep-tech ecosystem profile: accelerator, NGO, SME, lab, venture builder or innovation-support role");
            company.Add("Short bio and decision-making mandate for the person applying or leading partner outreach");
            project.Add("Collaboration objective: Horizon Europe consortium, EIT route, Israeli/Danish partner, BII/DTU pilot or cloud-credit support");
            project.Add("One-page partnership pitch with technology domains, target partners and what we offer");
            project.Add("Follow-up contribution plan: event, blog post, workshop, consortium outreach or dissemination activity");
            finance.Add("Travel, accommodation, event and follow-up budget; separate non-cash credits from grant funding");
            submission.Add("Application answers explaining ecosystem contribution and deep-tech relevance");
            submission.Add("Partner follow-up list for Israel Innovation Authority, ICDK, AWS, BII and DTU Skylab contacts");
        }

        if (text.Contains("ai factories") || text.Contains("ai-on-demand") || text.Contains("tef") || text.Contains("testing and experimentation"))
        {
            project.Add("AI model/use-case description, data readiness and evaluation metrics");
            project.Add("Compute/testing need: model training, benchmarking, validation or sandbox experiment");
            finance.Add("Compute, data preparation, validation and expert-support cost assumptions");
            submission.Add("AI Factory, AI-on-Demand or TEF access request with technical readiness evidence");
        }

        if (text.Contains("cybersecurity competence") || text.Contains("eccc") || text.Contains("cybersecurity"))
        {
            project.Add("Cybersecurity threat model, target users and validation environment");
            project.Add("Secure-by-design, data protection and compliance assumptions");
            finance.Add("Security testing, certification, audit and pilot deployment cost assumptions");
        }

        if (text.Contains("prima") || text.Contains("water4all") || text.Contains("biodiversa") || text.Contains("dut partnership") || text.Contains("ep permed"))
        {
            project.Add("Transnational R&I partner map and national-funding eligibility check");
            project.Add("Research/pilot methodology with measurable impact and stakeholder engagement");
            finance.Add("Budget aligned to each national funding agency's rules");
            submission.Add("Joint transnational call pre-proposal/full proposal timeline");
        }

        if (text.Contains("euspa") || text.Contains("esa business") || text.Contains("space-enabled") || text.Contains("space applications") || text.Contains("space data") || text.Contains("copernicus") || text.Contains("galileo") || text.Contains("gnss") || text.Contains("satellite"))
        {
            project.Add("Space-enabled value proposition: GNSS, earth observation, satellite communications or space data");
            project.Add("Data source and technical feasibility note for satellite/space component");
            finance.Add("Costs for data access, integration, validation and field testing");
            submission.Add("Check ESA delegation / EUSPA / EU eligibility for Malta, Slovenia and Serbia before writing");
        }

        if (text.Contains("defence") || text.Contains("defense") || text.Contains("dual-use") || text.Contains("nato diana") || text.Contains("european defence fund"))
        {
            company.Add("Ownership/control and third-country influence screening");
            project.Add("Dual-use use case, end-user problem and security-sensitive assumptions");
            project.Add("Export-control, ITAR/EAR/EU dual-use and classified-information screening");
            finance.Add("Prototype/test budget and defence customer validation assumptions");
            submission.Add("Security and eligibility review before sharing sensitive technical details");
        }

        if (text.Contains("eib") || text.Contains("eif") || text.Contains("investeu") || text.Contains("venture debt"))
        {
            project.Add("Investment case: market, traction, pipeline, growth plan and EU impact");
            finance.Add("Financial model, runway, debt capacity, cap table and fundraising history");
            finance.Add("Use of funds and co-financing sources");
            submission.Add("Investor data room: deck, financials, contracts, IP, legal and references");
        }

        if (text.Contains("usaid") || text.Contains("development innovation ventures"))
        {
            project.Add("Theory of change and evidence of development impact");
            project.Add("Monitoring, evaluation and learning plan");
            finance.Add("Cost-effectiveness assumptions and scale-up budget");
            submission.Add("SAM.gov/UEI and Grants.gov readiness if required by the notice");
        }

        if (text.Contains("usa") || text.Contains("united states") || text.Contains("sbir") || text.Contains("sttr") || text.Contains("nsf") || text.Contains("nih") || text.Contains("doe") || text.Contains("nasa") || text.Contains("darpa") || text.Contains("afwerx") || text.Contains("diu"))
        {
            company.Add("US entity formation plan: C-Corp or LLC, EIN, bank account and accounting setup");
            company.Add("SAM.gov registration, UEI and Grants.gov workspace where required");
            company.Add("Ownership/control memo for SBIR/STTR eligibility, including foreign ownership review");
            company.Add("Principal Investigator eligibility and employment allocation");
            project.Add("US problem statement and agency fit: NSF, NIH, DOE, NASA, DoD/DIU or ARPA-H");
            project.Add("Technical innovation narrative with novelty, TRL, milestones and measurable Phase I outcomes");
            project.Add("Commercialization plan with US customer discovery and letters of support");
            project.Add("Export-control, data/security and defense-use screening where relevant");
            finance.Add("US budget by labor, consultants, subawards, materials, indirect costs and fee/profit rules");
            finance.Add("Subaward and consultant letters or quotes, especially for university/research partners");
            submission.Add("Agency portal accounts and deadline calendar for registration steps before proposal submission");
        }

        if (text.Contains("challenge.gov") || text.Contains("prize competition"))
        {
            company.Add("Prize eligibility check: foreign entity/person rules, tax forms and payment constraints");
            project.Add("Short demo package: video, screenshots, measurable result and validation evidence");
            submission.Add("Challenge.gov account and challenge-specific submission artifacts");
        }

        if (text.Contains("diu") || text.Contains("commercial solutions opening"))
        {
            project.Add("Dual-use capability one-pager mapped to the DIU problem statement");
            project.Add("Evidence of commercial traction, deployments, pilots or paying customers");
            finance.Add("Prototype pricing, deployment assumptions and commercial procurement terms");
            submission.Add("Check allied/international vendor eligibility and any ITAR/CUI requirements before submission");
        }

        if (text.Contains("rvo") || text.Contains("netherlands") || text.Contains("dutch"))
        {
            company.Add("Dutch partner/entity registration and role confirmation");
            project.Add("Internationalisation, feasibility or demonstration plan for the target market");
            finance.Add("Dutch partner budget and eligible-cost evidence");
        }

        if (text.Contains("switzerland") || text.Contains("swiss") || text.Contains("innosuisse"))
        {
            company.Add("Swiss partner/entity registration and role confirmation");
            project.Add("Swiss partner work package and innovation contribution");
            finance.Add("Swiss national funding assumptions and partner co-financing");
        }

        if (text.Contains("sweden") || text.Contains("vinnova") || text.Contains("sida"))
        {
            company.Add("Swedish partner/entity registration and mandate if route requires it");
            project.Add("Sustainability, inclusion or international R&D impact narrative");
            finance.Add("Swedish partner budget and eligible-cost evidence");
        }

        if (text.Contains("interreg") || text.Contains("cross-border") || text.Contains("ipa"))
        {
            company.Add("Proof of legal status for each partner and geographic eligibility");
            project.Add("Cross-border relevance and joint implementation logic");
            finance.Add("Partner-by-partner co-financing confirmations");
            submission.Add("Lead partner declaration and partnership agreement");
        }
    }
}
