using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LaunchBuddy;

internal sealed class IntentInterpreter : IDisposable
{
    private readonly HttpClient _client;
    private readonly TimeSpan _inferenceTimeout;
    private string? _model;

    public IntentInterpreter(HttpClient? client = null, TimeSpan? inferenceTimeout = null)
    {
        _client = client ?? new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = Timeout.InfiniteTimeSpan };
        _inferenceTimeout = inferenceTimeout ?? TimeSpan.FromSeconds(40);
    }

    public async Task<(Intent Intent, string ModelStatus)> InterpretAsync(string message, IReadOnlyList<WebsiteRecord> websites, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_inferenceTimeout);
        try
        {
            var model = await GetAvailableModelAsync(deadline.Token);
            if (model is null)
            {
                return (Fallback(message, websites), "Ollama 未連線，已使用有限的本機規則");
            }

            var payload = new
            {
                model,
                stream = false,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = BuildSystemPrompt(websites)
                    },
                    new
                    {
                        role = "user",
                        content = message
                    }
                },
                tools = ToolDefinitions,
                options = new { temperature = 0, num_predict = 180, num_ctx = 4096 }
            };

            using var response = await _client.PostAsJsonAsync("/api/chat", payload, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var intent = ParseToolCall(document.RootElement) ?? ReplyOrFallback(document.RootElement, message, websites);
            return (intent, $"本機模型：{model}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (Fallback(message, websites), "模型回應逾時，已使用本機規則處理");
        }
        catch (Exception exception)
        {
            AppLog.Error("Ollama", exception);
            return (Fallback(message, websites), "Ollama 連線或回應失敗，已使用本機規則處理");
        }
    }

    public async Task<string> GetStatusAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var model = await GetAvailableModelAsync(deadline.Token);
            return model is null ? "Ollama 沒有模型（仍可使用基本指令）" : $"本機模型：{model}";
        }
        catch (Exception)
        {
            return "Ollama 未連線（仍可使用基本指令）";
        }
    }

    private async Task<string?> GetAvailableModelAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_model))
        {
            return _model;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await _client.GetAsync("/api/tags", deadline.Token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        if (!document.RootElement.TryGetProperty("models", out var models) || models.GetArrayLength() == 0)
        {
            return null;
        }

        var names = models.EnumerateArray()
            .Select(item => item.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();

        _model = names.FirstOrDefault(name => name.StartsWith("llama", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault();
        return _model;
    }

    private static Intent? ParseToolCall(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message))
            return null;

        if (message.TryGetProperty("tool_calls", out var calls) && calls.GetArrayLength() > 0)
        {
            return calls[0].TryGetProperty("function", out var function) ? ToIntent(function) : null;
        }

        // Llama models often write the call as text, e.g. {"name": "open_application", "parameters": {"as_administrator": True, ...}}.
        return message.TryGetProperty("content", out var content) ? ParseTextToolCall(content.GetString() ?? string.Empty) : null;
    }

    private static Intent? ParseTextToolCall(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        var json = PythonLiteral.Replace(content[start..(end + 1)], match => match.Value.ToLowerInvariant() == "none" ? "null" : match.Value.ToLowerInvariant());
        try
        {
            using var document = JsonDocument.Parse(json);
            return ToIntent(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly Regex PythonLiteral = new(@"\b(?:True|False|None)\b");

    // No tool call: still act if the basic rules recognise an action, otherwise show the model's own reply.
    private static Intent ReplyOrFallback(JsonElement root, string message, IReadOnlyList<WebsiteRecord> websites)
    {
        var fallback = Fallback(message, websites);
        if (fallback.Action != "chat")
            return fallback;

        return root.TryGetProperty("message", out var reply) &&
            reply.TryGetProperty("content", out var content) &&
            content.GetString() is { Length: > 0 } text
                ? new Intent { Action = "chat", Reply = text.Trim() }
                : fallback;
    }

    private static Intent? ToIntent(JsonElement function)
    {
        if (function.ValueKind != JsonValueKind.Object || !function.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            return null;

        var action = nameElement.GetString() ?? "chat";
        var arguments = function.TryGetProperty("arguments", out var argumentElement) || function.TryGetProperty("parameters", out argumentElement) ? argumentElement : default;
        string ReadString(string property) => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
        bool ReadBool(string property) => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var value) &&
            (value.ValueKind is JsonValueKind.True || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed);

        return action switch
        {
            "save_website" => new Intent { Action = action, Alias = ReadString("alias"), Url = ReadString("url") },
            "open_saved_website" => new Intent { Action = action, Alias = ReadString("alias") },
            "remove_saved_website" => new Intent { Action = action, Alias = ReadString("alias") },
            "list_saved_websites" => new Intent { Action = action },
            "open_application" => new Intent { Action = action, Query = ReadString("query"), AsAdministrator = ReadBool("as_administrator") },
            _ => new Intent { Action = "chat", Reply = "我只可以協助尋找／開啟程式，以及管理已記錄的網站。" }
        };
    }

    private static Intent Fallback(string message, IReadOnlyList<WebsiteRecord> websites)
    {
        var hasOpen = Regex.IsMatch(message, @"開啟|開啓|开启|打開|打开|" + BareOpen + @"|\bopen\b", RegexOptions.IgnoreCase);
        var urlMatch = Regex.Match(message, @"https?://[^\s<>\""'，。]+", RegexOptions.IgnoreCase);

        if (Regex.IsMatch(message, @"記住|儲存|保存|存下|\bsave\b|\bstore\b", RegexOptions.IgnoreCase) && urlMatch.Success)
        {
            var alias = ExtractAlias(message, urlMatch.Index);
            return new Intent { Action = "save_website", Alias = alias, Url = urlMatch.Value };
        }

        if (Regex.IsMatch(message, @"列出|清單|有哪些|\blist\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(message, @"網站|網址|連結|website|link", RegexOptions.IgnoreCase))
        {
            return new Intent { Action = "list_saved_websites" };
        }

        if (Regex.IsMatch(message, @"刪除|移除|\bremove\b|\bdelete\b", RegexOptions.IgnoreCase))
        {
            var matched = websites.OrderByDescending(site => site.Alias.Length)
                .FirstOrDefault(site => message.Contains(site.Alias, StringComparison.OrdinalIgnoreCase));
            return new Intent { Action = "remove_saved_website", Alias = matched?.Alias ?? ExtractTrailingName(message) };
        }

        if (hasOpen)
        {
            // The whole target must be the alias, so "GitHub Desktop" is not taken for a site saved as "GitHub".
            var query = ExtractApplicationQuery(message);
            var matched = websites.FirstOrDefault(site =>
                string.Equals(site.Alias.Trim(), query, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return new Intent { Action = "open_saved_website", Alias = matched.Alias };
            }

            return new Intent
            {
                Action = "open_application",
                Query = query,
                AsAdministrator = Regex.IsMatch(message, @"管理員|administrator|admin", RegexOptions.IgnoreCase)
            };
        }

        return new Intent { Action = "chat", Reply = "我可以幫你開程式、以管理員身分開程式，或永久記住指定網站。" };
    }

    private static string ExtractAlias(string message, int urlIndex)
    {
        var beforeUrl = message[..urlIndex].Trim();
        var match = Regex.Match(beforeUrl, "(?:叫做|叫|名稱是|名稱為|命名為|存成)\\s*[「“\\\"']?\\s*(.+?)\\s*[」”\\\"']?\\s*$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return CleanName(match.Groups[1].Value);
        }

        return string.Empty;
    }

    private static string ExtractTrailingName(string message)
    {
        var match = Regex.Match(message, "(?:刪除|移除|remove|delete)\\s*(?:已記錄的)?(?:網站|網址|連結|website|link)?\\s*[「“\\\"']?\\s*(.+?)\\s*[」”\\\"']?\\s*$", RegexOptions.IgnoreCase);
        return match.Success ? CleanName(match.Groups[1].Value) : string.Empty;
    }

    // A bare "開" counts as "open" anywhere, except in common words such as 開始, 開心 or 開會.
    private const string BareOpen = "開(?![始心會關車放玩門口銷發])";

    private static string ExtractApplicationQuery(string message)
    {
        var query = Regex.Replace(message, @"^.*?(?:開啟|開啓|开启|打開|打开|" + BareOpen + @"|\bopen\b)\s*", string.Empty, RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"\s*\b(?:as|with|in)\s+(?:an?\s+)?(?:administrator|admin)\b(?:\s+(?:mode|privileges?|rights))?", string.Empty, RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"(?:以|用)?\s*(?:管理員|administrator|admin)(?:身分|身份)?\s*(?:模式)?", string.Empty, RegexOptions.IgnoreCase);
        return CleanName(query);
    }

    private static string CleanName(string value) => value.Trim()
        .Trim('「', '」', '“', '”', '"', '\'', '：', ':', '，', ',', '。', '.')
        .Replace("這個網站", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("網站", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim();

    private static string BuildSystemPrompt(IReadOnlyList<WebsiteRecord> websites)
    {
        var aliases = websites.Count == 0
            ? "（沒有）"
            : string.Join("、", websites.Select(website => website.Alias));

        return $"""
            你是 Windows 本機啟動助手，負責理解使用者的意思並呼叫一個工具。使用者可能用繁體中文、簡體中文、粵語或英文，用任何說法表達；請理解意思，不要要求固定格式。程式會先請使用者 Approve，你不可聲稱已完成動作。
            - 想記住、收藏、存起來、加書籤、以後要用某個網址 → save_website
            - 想開、去、上、看已儲存清單中的網站 → open_saved_website
            - 想開啟、啟動、執行、跑、使用電腦上的程式、資料夾或檔案 → open_application；只要使用者表達要管理員或最高權限，as_administrator 就是 true
            - 想刪除、忘記、不再記住某個網站 → remove_saved_website
            - 想知道存了哪些網站 → list_saved_websites
            query 只填目標名稱，程式請用完整正式名稱（例如 vscode → Visual Studio Code、chrome → Google Chrome、記事本 → Notepad），不要自行加上路徑或 .exe；使用者有指明資料夾時要保留，例如「Downloads 裡的 report.pdf」。網址只能使用使用者提供的，不可捏造。
            只是聊天或與以上無關時，不呼叫工具，用使用者的語言回覆一兩句，並提醒你可以幫忙開程式、資料夾、檔案或管理網站。
            已儲存的網站：{aliases}
            """;
    }

    private static readonly object[] ToolDefinitions =
    [
        Tool("save_website", "把使用者提供的 http/https 網址永久記下來，之後可用名稱開啟。", new { alias = new { type = "string", description = "使用者為網站取的名稱；沒有指定時用網站的簡短名稱" }, url = new { type = "string", description = "使用者訊息中的完整 http/https 網址，必須原樣照抄" } }, new[] { "alias", "url" }),
        Tool("open_saved_website", "開啟已儲存網站清單中的一個網站。", new { alias = new { type = "string", description = "已儲存網站清單中的名稱" } }, new[] { "alias" }),
        Tool("remove_saved_website", "從已儲存網站清單刪除一個網站。", new { alias = new { type = "string", description = "已儲存網站清單中的名稱" } }, new[] { "alias" }),
        Tool("list_saved_websites", "列出所有已儲存網站。", new { }, Array.Empty<string>()),
        Tool("open_application", "在這台電腦上尋找並開啟 application、資料夾或檔案。", new { query = new { type = "string", description = "目標名稱，程式用完整正式名稱，例如 Visual Studio Code、Google Chrome、下載、report.pdf；使用者有提供完整路徑才填路徑" }, as_administrator = new { type = "boolean", description = "使用者想要管理員／系統管理員／最高權限／提升權限／admin／elevated 時為 true" } }, new[] { "query", "as_administrator" })
    ];

    private static object Tool(string name, string description, object properties, string[] required) => new
    {
        type = "function",
        function = new
        {
            name,
            description,
            parameters = new { type = "object", properties, required }
        }
    };

    public void Dispose() => _client.Dispose();
}
