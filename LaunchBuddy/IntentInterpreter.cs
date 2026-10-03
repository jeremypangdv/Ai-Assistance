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
                options = new { temperature = 0, num_predict = 180, num_ctx = 2048 }
            };

            using var response = await _client.PostAsJsonAsync("/api/chat", payload, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var intent = ParseToolCall(document.RootElement) ?? Fallback(message, websites);
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
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("tool_calls", out var calls) ||
            calls.GetArrayLength() == 0)
        {
            return null;
        }

        var call = calls[0];
        if (!call.TryGetProperty("function", out var function) ||
            !function.TryGetProperty("name", out var nameElement))
        {
            return null;
        }

        var action = nameElement.GetString() ?? "chat";
        var arguments = function.TryGetProperty("arguments", out var argumentElement) ? argumentElement : default;
        string ReadString(string property) => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
        bool ReadBool(string property) => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True;

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
        var hasOpen = Regex.IsMatch(message, @"開啟|開啓|开启|打開|打开|^\s*開|\bopen\b", RegexOptions.IgnoreCase);
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
            var matched = websites.OrderByDescending(site => site.Alias.Length)
                .FirstOrDefault(site => message.Contains(site.Alias, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return new Intent { Action = "open_saved_website", Alias = matched.Alias };
            }

            var query = ExtractApplicationQuery(message);
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

    private static string ExtractApplicationQuery(string message)
    {
        var query = Regex.Replace(message, @"^.*?(?:開啟|開啓|开启|打開|打开|開|open)\s*", string.Empty, RegexOptions.IgnoreCase);
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
            ? "（尚未儲存任何網站）"
            : string.Join("、", websites.Select(website => $"{website.Alias} = {website.Url}"));

        return $"""
            你是 Windows 本機啟動助手。只可選擇一項已提供的工具；不可執行工具，也不可聲稱已完成。\
            使用者所有開啟、儲存與刪除動作都會先由程式要求 Approve。\
            只有使用者明確要求「記住／儲存／保存」網址時才可使用 save_website。\
            只有使用者明確要求開啟，才用 open_saved_website 或 open_application。open_application 可處理 application、資料夾、檔案或完整 Windows 路徑。\
            不可憑空捏造網址或應用程式路徑。\
            目前已儲存的網站：{aliases}
            """;
    }

    private static readonly object[] ToolDefinitions =
    [
        Tool("save_website", "永久儲存使用者明確要求記住的 http/https 網站。", new { alias = new { type = "string", description = "網站名称" }, url = new { type = "string", description = "使用者提供的完整 http/https 網址" } }, new[] { "alias", "url" }),
        Tool("open_saved_website", "開啟已儲存網站，需使用完全相符的已知名稱。", new { alias = new { type = "string", description = "已儲存網站名稱" } }, new[] { "alias" }),
        Tool("remove_saved_website", "刪除使用者明確要求移除的已儲存網站。", new { alias = new { type = "string", description = "已儲存網站名稱" } }, new[] { "alias" }),
        Tool("list_saved_websites", "列出已儲存網站。", new { }, Array.Empty<string>()),
        Tool("open_application", "尋找並開啟使用者指定的 Windows application、資料夾、檔案或完整本機路徑。", new { query = new { type = "string", description = "程式、檔案、資料夾名稱或完整路徑" }, as_administrator = new { type = "boolean", description = "只有使用者明確要求管理員身分時才為 true" } }, new[] { "query", "as_administrator" })
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
