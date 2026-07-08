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

    await File.WriteAllTextAsync(htmlPath, ApplicationReportRenderer.RenderHtml(rows), Encoding.UTF8);
    await File.WriteAllTextAsync(csvPath, ApplicationReportRenderer.RenderCsv(rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    Console.WriteLine($"HTML report: {htmlPath}");
    Console.WriteLine($"CSV report:  {csvPath}");
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
  <title>EU pozivi za MSP iz Srbije i Malte</title>
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
    <h1>EU pozivi za MSP iz Srbije i Malte</h1>
    <p>Pregled otvorenih i potencijalno relevantnih poziva, sa Apply checklistom za dokumente koje treba pripremiti.</p>
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

        if (route == "EU cascade funding")
        {
            return "Obicno je kraci obrazac, ali rokovi su brzi. Prvo proveri eligibility drzave, SME status i portal za podnosenje.";
        }

        if (route == "International consortium")
        {
            return "Pripremi partnere rano. Ovakvi pozivi cesto traze partnere iz razlicitih zemalja i jasnu podelu budzeta.";
        }

        if (text.Contains("monitoring"))
        {
            return "Ovo je monitoring ruta, ne gotov poziv. Koristi checklistu za pripremu dok cekas konkretan notice ili cut-off.";
        }

        return "Otvori zvanicni poziv i prvo potvrdi eligibility, rok, intenzitet finansiranja i obavezne anekse.";
    }

    private static void AddSpecificItems(string text, List<string> company, List<string> project, List<string> finance, List<string> submission)
    {
        if (text.Contains("eic accelerator"))
        {
            project.Add("EIC pitch deck and 3-minute pitch video");
            project.Add("Freedom-to-operate and IP position");
            finance.Add("Investment readiness plan and use of funds");
            submission.Add("EU Funding & Tenders Portal profile, PIC number and EIC AI platform forms");
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

        if (text.Contains("usaid") || text.Contains("development innovation ventures"))
        {
            project.Add("Theory of change and evidence of development impact");
            project.Add("Monitoring, evaluation and learning plan");
            finance.Add("Cost-effectiveness assumptions and scale-up budget");
            submission.Add("SAM.gov/UEI and Grants.gov readiness if required by the notice");
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
